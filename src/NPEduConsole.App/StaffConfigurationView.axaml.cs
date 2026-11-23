using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.Core;

namespace NPEduConsole.App;

internal sealed record StaffPlanRow(int Index, string Title, string Subtitle, StaffConfigurationTeacher? Teacher,
    StaffConfigurationIssue[] Issues, StaffConfigurationIssue[] Warnings, StaffConfigurationSavedTeacher? Saved = null);

public sealed partial class StaffConfigurationView : UserControl
{
    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly string? _entryTerm;
    private readonly StaffConfigurationRequests _requests = null!;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, InitialPinDelivery<int>> _pins = new(StringComparer.Ordinal);
    private StaffConfigurationScope? _scope;
    private StaffConfigurationDocument? _document;
    private StaffConfigurationPreview? _preview;
    private StaffConfigurationReceipt? _receipt;
    private StaffConfigurationPending? _pending;
    private PendingChoice[] _available = [];
    private StaffPlanRow[] _rows = [];
    private string? _actor;
    private bool _busy, _writing, _closed, _detail, _binding, _missingReceipt, _cancelPrompt, _cancelUncertain;

    private sealed record PendingChoice(StaffConfigurationPending Request, string Title)
    {
        public override string ToString() => Title;
    }

    public StaffConfigurationView() => InitializeComponent();

    internal StaffConfigurationView(ConsoleClient client, School school, string? entryTerm, StaffConfigurationRequests requests,
        StaffConfigurationDocument? document) : this()
    {
        _client = client;
        _school = school;
        _entryTerm = entryTerm;
        _requests = requests;
        _document = document;
        SizeChanged += (_, _) => Arrange();
        AddHandler(KeyDownEvent, PageKeyDown, RoutingStrategies.Tunnel);
        Render();
    }

    internal Action? ReturnRequested { get; set; }
    internal Action<ConsoleApiException>? AccessLost { get; set; }
    internal Func<StaffConfigurationIssue, StaffConfigurationTeacher?, string, CancellationToken, Task>? ResolveIssue { get; set; }
    internal Func<CancellationToken, Task<string?>>? SelectDocumentText { private get; set; }
    internal Func<FilePickerSaveOptions, Task<IStorageFile?>>? SelectPinFile { private get; set; }
    internal bool Applied { get; private set; }
    private bool Compact => Bounds.Width is > 0 and < 820;
    private StaffPlanRow? Selected => StaffPlanRows.SelectedItem as StaffPlanRow;
    private bool HasRows => _preview is not null || _receipt is not null;
    private StaffConfigurationTerm? ActiveTerm => _scope?.Term is { } term && (_entryTerm is null || _entryTerm == term.Id) ? term : null;
    private bool ScopeWritable => ActiveTerm is not null && _actor is not null && _scope?.Mode is "LOCAL_PIN" or "SHARED_PASSWORD";
    private bool CanPick => !_busy && !_cancelPrompt && ScopeWritable && _receipt is null && (_pending is null || _missingReceipt && _pending.TermId == ActiveTerm?.Id);
    private bool CanPreview => CanPick && _document is not null && DeclarationMatches && _preview is null;
    private bool CanSave => !_busy && !_cancelPrompt && !_cancelUncertain && ScopeWritable && _document is not null && _preview is { CanApply: true } preview &&
        StaffPlanApprove.IsChecked == true && (_pending is null ? !_available.Any(item => item.Request.TermId == preview.TermId)
            : _missingReceipt && _pending.Proof == preview.Version);

    private bool DeclarationMatches => _document is { } document && ActiveTerm is { } term &&
        string.Equals(document.SchoolCode, _scope!.SchoolCode, StringComparison.OrdinalIgnoreCase) &&
        document.AcademicYear == term.AcademicYear && document.Semester == term.Semester;

    internal async Task StartAsync()
    {
        await RunAsync(async ct =>
        {
            await ReadScopeAsync(ct);
            if (_closed) return;
            if (_document is null && _available.FirstOrDefault(item => item.Request.TermId == ActiveTerm?.Id) is { } choice)
            {
                _pending = choice.Request;
                await ReadReceiptAsync(ct);
            }
        });
        FocusLater(StaffPlanOpen.IsEffectivelyVisible && StaffPlanOpen.IsEnabled ? StaffPlanOpen : StaffPlanClose);
    }

    internal bool CanClose() => !_writing;

    internal void End()
    {
        if (_closed) return;
        _closed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _document = null;
        _pins.Clear();
        _preview = null;
        _receipt = null;
        _scope = null;
        _pending = null;
        _available = [];
        _rows = [];
        StaffPlanRows.ItemsSource = null;
        StaffPlanPendingChoices.ItemsSource = null;
        StaffPlanScope.Text = StaffPlanFile.Text = StaffPlanStatus.Text = StaffPlanDetailTitle.Text = null;
        StaffPlanSearch.Clear();
        StaffPlanDetail.Children.Clear();
    }

    private async Task ReadScopeAsync(CancellationToken ct)
    {
        _actor ??= await _client.GetStaffConfigurationActorAsync(ct);
        var scope = await _client.GetStaffConfigurationAsync(_school.Id, ct);
        if (_closed || ct.IsCancellationRequested) return;
        _scope = scope;
        RefreshPending();
    }

    private void RefreshPending()
    {
        if (_actor is null) return;
        _available = _requests.ForSchool(_client.ServerUri, _actor, _school.Id).Select(item =>
            new PendingChoice(item, $"{(item.TermId == _scope?.Term?.Id ? "当前学期" : "原学期")} · {item.Id.ToString("D")[..8]}"))
            .OrderByDescending(item => item.Request.TermId == ActiveTerm?.Id).ThenBy(item => item.Title).ToArray();
        var previous = (StaffPlanPendingChoices.SelectedItem as PendingChoice)?.Request.Id;
        StaffPlanPendingChoices.ItemsSource = _available;
        StaffPlanPendingChoices.SelectedItem = _available.FirstOrDefault(item => item.Request.Id == previous) ?? _available.FirstOrDefault();
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_busy || _closed) return;
        _busy = true;
        var ct = _lifetime.Token;
        Render();
        try { await action(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || _closed) { }
        catch (ConsoleApiException error) when (!_closed)
        {
            if (error.StatusCode is 401 or 403)
            {
                End();
                AccessLost?.Invoke(error);
            }
            else Status(error.Message);
        }
        catch (ConsoleApiException) when (_closed) { }
        catch (ArgumentException error) when (!_closed) { Status(error.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (!_closed) Status($"无法读取所选文件或保存核对编号，请检查本机文件访问后重试（{error.HResult:X8}）。");
        }
        finally
        {
            _busy = false;
            if (!_closed) Render();
        }
    }

    private void Status(string? text)
    {
        StaffPlanStatus.Text = text;
        StaffPlanStatus.IsVisible = !string.IsNullOrEmpty(text);
    }

    private async Task<string?> OpenFileAsync(CancellationToken ct)
    {
        if (SelectDocumentText is { } select) return await select(ct);
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "选择完整师资 JSON", AllowMultiple = false,
            FileTypeFilter = [new("完整师资 JSON") { Patterns = ["*.json"] }] });
        ct.ThrowIfCancellationRequested();
        if (files.Count != 1) return null;
        using var file = files[0];
        await using var stream = await file.OpenReadAsync();
        using var reader = new StreamReader(stream);
        var buffer = new char[OrganizationImportDocumentReader.MaximumCharacters + 1];
        try { return new string(buffer, 0, await reader.ReadBlockAsync(buffer, ct)); }
        finally { Array.Clear(buffer); }
    }

    private async void OpenClicked(object? sender, RoutedEventArgs args)
    {
        if (!CanPick) return;
        await RunAsync(async ct =>
        {
            var text = await OpenFileAsync(ct);
            if (_closed || text is null) return;
            var document = StaffConfigurationDocument.Read(text);
            _document = document;
            ClearPreview();
            Status(DeclarationMatches ? "已读取完整师资文件。预览只核对差异，不会保存。" :
                "文件声明与办理学校或学期不一致，请选择匹配的文件。");
        });
    }

    private async void PreviewClicked(object? sender, RoutedEventArgs args)
    {
        if (!CanPreview) return;
        await RunAsync(ct => ReadPreviewAsync(ct, Selected?.Index));
    }

    private async Task ReadPreviewAsync(CancellationToken ct, int? selectedIndex)
    {
        if (_document is not { } document || ActiveTerm is not { } term) return;
        try
        {
            var preview = await _client.PreviewStaffConfigurationAsync(_school.Id, term.Id, document, ct);
            if (_closed) return;
            _preview = preview;
            StaffPlanApprove.IsChecked = false;
            LoadPreviewRows(preview, selectedIndex);
            Status(_pending is { } pending && pending.Proof != preview.Version
                ? "重新读取的方案与原确认不同，不能按原编号提交。原编号仍可只读核对。"
                : preview.CanApply ? null : "还有需要处理的问题，请从问题所在教师或文件行查看详情。");
        }
        catch (ConsoleApiException error) when (error.Code is "STAFF_CONFIGURATION_TERM_CHANGED" or "STAFF_CONFIGURATION_STALE")
        {
            await ReadScopeAsync(ct);
            if (!_closed) Status("当前启用学期或学校设置已变化。请核对页头后再预览，原文件保留。");
        }
    }

    private async void SaveClicked(object? sender, RoutedEventArgs args)
    {
        if (!CanSave || _document is not { } document || _preview is not { } preview || _actor is not { } actor) return;
        await RunAsync(async ct =>
        {
            var request = _pending ?? new(Guid.NewGuid(), _client.ServerUri.AbsoluteUri, actor, _school.Id, preview.TermId, preview.Version);
            if (_pending is null)
            {
                RefreshPending();
                if (_available.Any(item => item.Request.TermId == preview.TermId))
                {
                    Status("另一个窗口已有同学期待核对的申请，请先核对原编号。");
                    return;
                }
                _requests.Remember(request);
                _pending = request;
            }
            _missingReceipt = false;
            _writing = true;
            Render();
            try
            {
                var saved = await _client.CommitStaffConfigurationAsync(_school.Id, document, preview, request.Id, ct);
                if (!_closed) Accept(saved.Receipt, saved.AppliedPins);
            }
            catch (ConsoleApiException error) when (error.Code is "STAFF_CONFIGURATION_STALE" or "STAFF_CONFIGURATION_TERM_CHANGED")
            {
                if (_closed) return;
                ClearRequest(request);
                ClearPreview();
                AllowReturnDuringRead();
                await ReadScopeAsync(ct);
                if (!_closed) Status("预览后办理依据已变化，本次没有保存。原文件已保留，请重新预览。");
            }
            catch (ConsoleApiException error) when (error.Code == "STAFF_CONFIGURATION_CANCELLED")
            {
                if (!_closed) Cancelled(request);
            }
            catch (ConsoleApiException error) when (error.StatusCode is not (401 or 403))
            {
                if (_closed) return;
                Status("未收到明确的保存结果，先按原编号只读核对。");
                AllowReturnDuringRead();
                await ReadReceiptAsync(ct);
            }
            finally { _writing = false; }
        });
    }

    private void AllowReturnDuringRead()
    {
        _writing = false;
        if (!_closed) Render();
    }

    private async Task ReadReceiptAsync(CancellationToken ct)
    {
        if (_pending is not { } request) return;
        StaffConfigurationReceipt? receipt;
        try { receipt = await _client.GetStaffConfigurationReceiptAsync(request.SchoolId, request.TermId, request.Id, ct); }
        catch (ConsoleApiException error) when (error.Code == "STAFF_CONFIGURATION_CANCELLED")
        {
            if (!_closed) Cancelled(request);
            return;
        }
        if (_closed) return;
        if (receipt is not null) { Accept(receipt, null); return; }
        if (!_missingReceipt && !_cancelUncertain)
        {
            _document = null;
            ClearPreview();
        }
        _missingReceipt = true;
        if (_cancelUncertain)
        {
            Status("仍未查到原申请结果。可继续核对，或再次明确选择结束原申请。");
            return;
        }
        Status(request.TermId == ActiveTerm?.Id ? "未找到本人的回执。可重新选择原文件并核对，证明一致后按原编号重试。" :
            "未找到本人的回执。原学期已不能重新提交，可继续只读核对或结束原申请。");
    }

    private void ClearRequest(StaffConfigurationPending request)
    {
        _pending = null;
        _missingReceipt = _cancelPrompt = _cancelUncertain = false;
        try { _requests.Clear(request); RefreshPending(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Status("已取得服务端结果，本机尚未清除核对编号。重开后仍可只读查询。"); }
    }

    private void Accept(StaffConfigurationReceipt receipt, StaffConfigurationAppliedPin[]? pins)
    {
        Applied = true;
        _document = null;
        _preview = null;
        _receipt = receipt;
        _detail = false;
        _pins.Clear();
        foreach (var pin in pins ?? []) _pins.Add(pin.AccountId, new(pin.AccountId, pin.Username, pin.Pin, pin.Version));
        Status(pins is { Length: > 0 } ? "已保存。选择新建教师可交付本次初始 PIN。" : "已核对本人申请及服务端回执。");
        if (_pending is { } request) ClearRequest(request);
        SetRows(receipt.Teachers.Select((teacher, index) => new StaffPlanRow(index, teacher.Name,
            $"{teacher.Username} · {(teacher.Created ? "新建账号" : "沿用账号")}", null, [], [], teacher)).ToArray());
    }

    private void ClearPreview()
    {
        _preview = null;
        _detail = false;
        StaffPlanApprove.IsChecked = false;
        SetRows([]);
    }

    private async void CheckClicked(object? sender, RoutedEventArgs args) => await RunAsync(ReadReceiptAsync);

    private void AskCancelRequestClicked(object? sender, RoutedEventArgs args)
    {
        if (_pending is null || !_missingReceipt || _busy) return;
        _cancelPrompt = true;
        Status("结束后，这个编号将不能再导入。如果原申请已经保存，将显示原回执。当前文件会保留以便重新预览。");
        Render();
        FocusLater(StaffPlanKeepRequest);
    }

    private void KeepRequestClicked(object? sender, RoutedEventArgs args)
    {
        _cancelPrompt = false;
        Status("原申请保留，可继续按这个编号核对。");
        Render();
        FocusLater(StaffPlanCheck);
    }

    private async void CancelRequestClicked(object? sender, RoutedEventArgs args)
    {
        if (!_cancelPrompt || !_missingReceipt || _pending is not { } request) return;
        _cancelPrompt = false;
        await RunAsync(async ct =>
        {
            _cancelUncertain = true;
            _writing = true;
            Render();
            try
            {
                var result = await _client.CancelStaffConfigurationAsync(_school.Id, request.TermId, request.Id, request.Proof, ct);
                if (_closed) return;
                if (result is not null) Accept(result, null);
                else Cancelled(request);
            }
            catch (ConsoleApiException error) when (error.StatusCode is not (401 or 403))
            {
                AllowReturnDuringRead();
                if (!_closed) await ReadReceiptAsync(ct);
            }
            finally { _writing = false; }
        });
    }

    private void Cancelled(StaffConfigurationPending request)
    {
        ClearRequest(request);
        ClearPreview();
        Status("原申请已结束。请重新预览保留的文件，然后确认新申请。");
    }

    private async void PendingClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy || _pending is not null || StaffPlanPendingChoices.SelectedItem is not PendingChoice choice) return;
        await RunAsync(async ct =>
        {
            _pending = choice.Request;
            await ReadReceiptAsync(ct);
        });
    }

    private void NewClicked(object? sender, RoutedEventArgs args)
    {
        _receipt = null;
        _pins.Clear();
        Status(null);
        ClearPreview();
        Render();
        FocusLater(StaffPlanOpen);
    }

    private void CloseClicked(object? sender, RoutedEventArgs args) => ReturnRequested?.Invoke();
    private void ApprovalChanged(object? sender, RoutedEventArgs args)
    {
        if (_client is not null && !_closed) StaffPlanSave.IsEnabled = CanSave;
    }
}
