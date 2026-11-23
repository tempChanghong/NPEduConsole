using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class TermStatusWindow : Window
{
    private sealed record Operation(TermStatusPreview Preview, bool Force, Guid RequestId);
    private static readonly ConditionalWeakTable<ConsoleClient, Dictionary<string, Operation>> Operations = new();
    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly CancellationTokenSource _lifetime = new();
    private ManagedAcademicTerm? _target, _knownActive;
    private string? _action;
    private Guid? _receiptId;
    private TermStatusPreview? _preview;
    private TermStatusResult? _result;
    private bool _busy, _closed, _blocked, _mayRetry, _writing;

    public TermStatusWindow() => InitializeComponent();

    internal TermStatusWindow(ConsoleClient client, School school, ManagedAcademicTerm? active,
        ManagedAcademicTerm? target, string? action, Guid? receipt) : this()
    {
        _client = client;
        _school = school;
        _knownActive = active;
        _target = target;
        _action = action;
        _receiptId = receipt;
        Opened += async (_, _) =>
        {
            BatchWorkspaceWindow.FitSchoolWindow(this);
            AdaptLayout();
            await RunAsync(LoadAsync);
        };
        SizeChanged += (_, _) => AdaptLayout();
        StatusScreens.SelectionChanged += (_, _) => RenderControls();
        Closing += (_, args) => args.Cancel = _writing && !_closed;
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
        Render();
    }

    internal static bool HasPending(ConsoleClient client, string schoolId) =>
        Operations.TryGetValue(client, out var pending) && pending.ContainsKey(schoolId);
    private Dictionary<string, Operation> PendingOperations => Operations.GetOrCreateValue(_client);
    private Operation? Pending => PendingOperations.GetValueOrDefault(_school.Id);
    internal Action<ConsoleApiException>? AuthorizationLost { get; init; }
    internal Func<Window, string, string, Task<TermPreparationView.ScreenDestination?>>? OpenPlacement { get; init; }
    internal bool ObservedWrite { get; private set; }
    internal string? ResultTermId => _result?.Receipt.Term.Id;
    internal TermPreparationView.ScreenDestination? Destination { get; private set; }

    internal void CloseForScopeChange()
    {
        _closed = true;
        Close();
    }

    private async Task LoadAsync()
    {
        if (Pending is { } pending)
        {
            _target = pending.Preview.Term;
            _action = pending.Preview.Action;
            StatusMoveScreens.IsChecked = pending.Preview.RebindScreens;
            SetPreview(pending.Preview);
            await LookupAsync(pending.RequestId, pending);
            return;
        }
        if (_receiptId is { } id) await LookupAsync(id);
        else await ReadPreviewAsync();
    }

    private void AdaptLayout()
    {
        bool narrow = ClientSize.Width < 900;
        StatusComparison.ColumnDefinitions = new(narrow ? "*" : "*,*");
        StatusComparison.RowDefinitions = new(narrow ? "Auto,Auto" : "Auto");
        Grid.SetRow(StatusAfterCard, narrow ? 1 : 0);
        Grid.SetColumn(StatusAfterCard, narrow ? 0 : 1);
    }

    private async Task ReadPreviewAsync()
    {
        if (_target is not { } term || _action is not { } action) return;
        _preview = null;
        _result = null;
        StatusForce.IsChecked = false;
        Notice("正在读取本次学期操作的影响…");
        Render();
        var result = await _client.PreviewTermStatusAsync(_school.Id, term.Id, action,
            action == "ACTIVATE" && StatusMoveScreens.IsChecked == true, _lifetime.Token);
        if (_closed) return;
        SetPreview(result);
        Notice(result.ChangesStatus ? null : "该学期已经处于所选状态。请返回目录。", !result.ChangesStatus);
    }

    private void SetPreview(TermStatusPreview preview)
    {
        _preview = preview;
        _target = preview.Term;
        _knownActive = preview.ActiveTerm;
        StatusForce.IsChecked = false;
        Render();
    }

    private async Task SubmitAsync(Operation operation)
    {
        PendingOperations[_school.Id] = operation;
        _writing = true;
        _mayRetry = false;
        Notice("正在提交并核对服务器结果…");
        RenderControls();
        TermStatusCommit commit;
        try { commit = await _client.SaveTermStatusAsync(operation.Preview, operation.Force, operation.RequestId, _lifetime.Token); }
        catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or >= 500 || error.Code == "TERM_STATUS_REQUEST_CONFLICT")
        {
            if (!_closed) await LookupAsync(operation.RequestId, operation);
            return;
        }
        catch (ConsoleApiException)
        {
            PendingOperations.Remove(_school.Id);
            _preview = null;
            Render();
            throw;
        }
        if (_closed) return;
        PendingOperations.Remove(_school.Id);
        if (commit.Receipt is { } receipt && commit.Screens is { } screens)
        {
            ShowReceipt(new(receipt, commit.ActiveTerm, screens, commit.IsCurrentRound));
            return;
        }
        SetPreview(commit.Preview!);
        Notice("本次没有写入。" + string.Join(" ", commit.Changes!), true);
        StatusScroll.Offset = default;
        Focus(StatusRefresh);
    }

    private async Task LookupAsync(Guid requestId, Operation? pending = null)
    {
        _mayRetry = false;
        var value = await _client.GetTermStatusReceiptAsync(_school.Id, requestId, _lifetime.Token,
            pending?.Preview.Term.Id, pending?.Preview.Action, pending?.Preview.RebindScreens);
        if (_closed) return;
        if (value is not null)
        {
            PendingOperations.Remove(_school.Id);
            ShowReceipt(value);
            return;
        }
        _mayRetry = pending is not null;
        Notice(pending is null ? "暂未找到该学期操作回执。" :
            "暂未找到原编号回执，尚未确认结果。可继续核对，或按原编号重试并重新核验该预览。", true);
    }

    private void ShowReceipt(TermStatusResult result)
    {
        _result = result;
        _receiptId = result.Receipt.RequestId;
        _target = result.Receipt.Term;
        _action = result.Receipt.Operation;
        _knownActive = result.ActiveTerm;
        _preview = null;
        ObservedWrite = true;
        Notice(result.Receipt.Operation == "ACTIVATE" && !result.IsCurrentRound ?
            "正在查看历史启用回执。当前归属与上报另列。" : "已核对服务器回执。逐台结果使用最新读取的设备状态。");
        StatusScroll.Offset = default;
        Render();
        Focus(StatusBack);
    }

    private async void MoveScreensChanged(object? sender, RoutedEventArgs args) => await RunAsync(ReadPreviewAsync);
    private void ForceChanged(object? sender, RoutedEventArgs args) => RenderControls();
    private void BackClicked(object? sender, RoutedEventArgs args) => Close();

    private async void RefreshClicked(object? sender, RoutedEventArgs args) => await RunAsync(() => Pending is { } pending ?
        LookupAsync(pending.RequestId, pending) : _receiptId is { } id ? LookupAsync(id) : ReadPreviewAsync());

    private async void EndLookupClicked(object? sender, RoutedEventArgs args)
    {
        if (!_mayRetry || Pending is not { } pending) return;
        await RunAsync(async () =>
        {
            await LookupAsync(pending.RequestId, pending);
            if (_closed || Pending is null || !_mayRetry) return;
            PendingOperations.Remove(_school.Id);
            _mayRetry = false;
            await ReadPreviewAsync();
            if (!_closed) Notice("原编号暂未找到回执。已重新读取当前学期和影响，请按新预览办理。", true);
        });
    }

    private async void CommitClicked(object? sender, RoutedEventArgs args)
    {
        if (!StatusCommit.IsEnabled || _blocked) return;
        if (Pending is { } pending) { await RunAsync(() => SubmitAsync(pending)); return; }
        if (_result is { } result) { await OpenScreenAsync(result); return; }
        if (_preview is not {CanApply: true} preview) return;
        bool force = StatusForce.IsChecked == true && preview.CanForce;
        if (preview.BlockingDiagnostics.Length > 0 && !force) return;
        await RunAsync(() => SubmitAsync(new(preview, force, Guid.NewGuid())));
    }

    private TermStatusScreen? SelectedScreen => _result?.Screens.FirstOrDefault(row => row.BindingId == StatusScreens.SelectedBindingId);

    private async Task OpenScreenAsync(TermStatusResult result)
    {
        if (SelectedScreen is not { } row || !result.IsCurrentRound || result.ActiveTerm?.Id != result.Receipt.Term.Id) return;
        if (row.PlacementNeeded && OpenPlacement is not null && !row.IntentionallyRetained &&
            (row.Placement is null || row.Placement.Status != "SUPERSEDED"))
        {
            var destination = await OpenPlacement(this, result.Receipt.Term.Id, row.BindingId);
            if (_closed) return;
            if (destination is null) { await RunAsync(() => LookupAsync(result.Receipt.RequestId)); return; }
            Destination = destination;
            Close();
        }
        else if ((row.TargetClass?.Id ?? (row.FromClass?.TermId == result.ActiveTerm.Id ? row.FromClass.Id : null)) is { } classId)
        {
            Destination = new(classId, row.BindingId, result.ActiveTerm.Id, row.Status is "AWAITING_PAIRING" or "REPAIR_REQUIRED");
            Close();
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_closed || _blocked || _busy) return;
        _busy = true;
        RenderControls();
        try { await action(); }
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error)
        {
            if (_closed) return;
            if (error.StatusCode is 401 or 403)
            {
                _blocked = true;
                _target = _knownActive = null;
                _preview = null;
                _result = null;
                PendingOperations.Remove(_school.Id);
                Render();
                AuthorizationLost?.Invoke(error);
            }
            if (!_closed) Notice(error.Message, true);
        }
        finally { _busy = _writing = false; if (!_closed) RenderControls(); }
    }

    private static string OperationName(string? action, ManagedAcademicTerm? term) => action switch
    {
        "ACTIVATE" when term?.Status == "ARCHIVED" => "重新启用",
        "ACTIVATE" => "启用",
        "ARCHIVE" => "单独归档",
        "DRAFT" => "转回草稿",
        _ => "学期操作"
    };

    private void Render()
    {
        var receipt = _result?.Receipt;
        var operation = OperationName(_action, receipt?.PreviousTerm ?? _target);
        StatusTitle.Text = _target is { } term ? $"{operation} · {term.Name}（{term.Period}）" : operation;
        ToolTip.SetTip(StatusTitle, StatusTitle.Text);
        StatusScope.Text = $"{_school.Name} · 全校实际启用：{_knownActive?.Name ?? (_blocked ? "待核对" : "暂无")}";
        StatusReceiptNumber.Text = receipt is null ? null : $"操作回执 {receipt.RequestId:D}";
        StatusReceiptNumber.IsVisible = receipt is not null;
        StatusOptions.IsVisible = _action == "ACTIVATE" && receipt is null && !_blocked;
        StatusMoveImpact.Text = StatusMoveScreens.IsChecked == true ? "按行政班代码匹配。无匹配的屏仍留原班。" :
            "保留每台大屏原班。原启用学期归档后，所属大屏旧原生配对仍会失效。";
        StatusBeforeLabel.Text = receipt is null ? "当前" : "操作前";
        StatusAfterLabel.Text = receipt is null ? "提交后" : "服务器确认的结果";
        var activeBefore = receipt?.PreviousActiveTerm ?? _preview?.ActiveTerm;
        StatusBefore.Text = (_preview is null && receipt is null) ? "等待读取学期及大屏依据。" :
            $"所选学期：{(receipt?.PreviousTerm ?? _preview?.Term)?.StatusText}\n{StudentAccess(activeBefore)}";
        var after = receipt is null ? _preview?.NextActiveTerm : receipt.ActiveTermAfterWrite;
        string? change = receipt?.Term.StatusText ?? (_preview?.Action switch { "ACTIVATE" => "启用中", "ARCHIVE" => "已归档", "DRAFT" => "草稿", _ => null });
        StatusAfter.Text = change is null ? "等待读取。" : $"所选学期：{change}\n{StudentAccess(after)}\n" +
            (after is null ? "当前无启用学期时，整校迁出暂不可办。" : "有启用学期，整校迁出仍须核对所有者与其他导出条件。");
        var issues = _preview?.BlockingDiagnostics ?? [];
        var reminders = _preview?.Reminders ?? [];
        StatusIssues.IsVisible = issues.Length > 0 || _preview?.EvidenceComplete == false;
        StatusIssueHeading.Text = _preview?.EvidenceComplete == false ? "大屏影响证据不足，无法办理" :
            _preview?.CanForce == true ? "启用例外与实际影响" : "须先处理的启用阻断";
        StatusIssueItems.ItemsSource = issues.Select(issue => issue.Consequence is { } consequence ? $"{issue.Message}\n{consequence}" : issue.Message).ToArray();
        StatusReminders.IsVisible = reminders.Length > 0;
        StatusReminderItems.ItemsSource = reminders.Select(row => row.Message).ToArray();
        StatusForce.IsVisible = _preview?.CanForce == true && receipt is null;
        var rows = _result?.Screens ?? _preview?.Screens ?? [];
        StatusScreensCard.IsVisible = _preview is not null || _result is not null;
        StatusScreensTitle.Text = receipt is null ? "逐台大屏影响" : "逐台大屏 · 回执与最新状态";
        StatusScreenSummary.Text = $"{rows.Length} 台 · {(receipt is null ? "拟改绑" : "已改绑")} {rows.Count(row => row.Rebound)} 台";
        StatusScreens.SetRows(rows.Select(row => new TermStatusScreenLine(row.BindingId, row.Name,
            row.FromClass is { } from ? $"{from.TermName} · {from.Name}（{from.Code}）" : "原班待核对",
            row.TargetClass is { } target && row.Rebound ? $"{target.Name}（{target.Code}）" : row.FromClass is null ? "待核对" : "保留原班",
            DescribeImpact(row, _result?.IsCurrentRound))).ToArray());
        RenderControls();
    }

    private static string StudentAccess(ManagedAcademicTerm? term) => term is null ? "学生入口：暂无启用学期" :
        $"学生入口：{term.Name}（{term.Period}）";

    private static string DescribeImpact(TermStatusScreen row, bool? isCurrentRound)
    {
        string atWrite = row.NativeImpact switch
        {
            "VERIFY" => "原生授权影响待核对。",
            "REPAIR_REQUIRED" => "须到教室重新配对，旧原生凭据已不适用。",
            "AWAITING_PAIRING" => "改绑后到教室核对接入。",
            _ when row.ArchiveInvalidatesNative => "归档或转草稿会使该屏原生旧凭据失效。",
            _ when row.PlacementNeeded => "无同码班可迁，待安排。",
            _ => "原绑定保持。"
        };
        string? now = (isCurrentRound == false && row.Live is { } observed ? observed.State : row.Status) switch
        {
            "REPORTED" => $"首次有效上报已确认 · {(row.Live?.LastReportedAt ?? row.LastReportedAt)?.ToLocalTime():yyyy-MM-dd HH:mm}",
            "TERM_INACTIVE" => "原班学期目前未启用。",
            "REPAIR_REQUIRED" => "当前仍需现场重配。",
            "AWAITING_PAIRING" => "当前待现场配对。",
            "AWAITING_REPORT" => "当前待有效首报。",
            "RETAINED_ORIGINAL" => "当前保留在原班。",
            "NOT_APPLICABLE" => "暂无原生接入。",
            null => null,
            _ => "当前设备状态待核对。"
        };
        if (now is null) return atWrite;
        string arranged = row.Placement is {Source: "CONSOLE"} placement ? $"\n本轮后续安排：{placement.TargetClass.Name}" : "";
        string liveClass = row.Live is { } current ? $"{current.TermName} · {current.ClassName} · " : "";
        return $"操作时：{atWrite}{arranged}\n现在：{liveClass}{now}";
    }

    private void RenderControls()
    {
        if (_client is null || StatusCommit is null) return;
        var pending = Pending;
        bool editable = !_blocked && !_busy;
        StatusBack.IsEnabled = !_writing;
        StatusRefresh.IsEnabled = editable;
        StatusRefresh.Content = pending is not null || _receiptId is not null ? "核对回执" : "刷新预览";
        StatusEndLookup.IsVisible = pending is not null && _mayRetry;
        StatusEndLookup.IsEnabled = editable;
        StatusMoveScreens.IsEnabled = StatusForce.IsEnabled = editable && pending is null;
        var screen = SelectedScreen;
        var active = _result?.ActiveTerm;
        bool canNavigate = screen is not null && _result?.IsCurrentRound == true && _result.Receipt.Term.Id == active?.Id && active is not null &&
            (screen.Rebound || screen.FromClass?.TermId == active.Id || screen.PlacementNeeded && OpenPlacement is not null);
        bool planned = _preview is {CanApply: true};
        StatusCommit.IsVisible = pending is not null ? _mayRetry : planned || canNavigate;
        StatusCommit.IsEnabled = editable && (pending is not null ? _mayRetry : canNavigate ||
            planned && (_preview!.BlockingDiagnostics.Length == 0 || StatusForce.IsChecked == true));
        StatusCommit.Content = pending is not null ? "按原编号重试" : canNavigate ? "办理所选大屏" :
            _preview?.CanForce == true ? "确认强制启用" : $"确认{OperationName(_action, _target)}";
    }

    private void Notice(string? message, bool error = false)
    {
        StatusMessage.Text = message;
        StatusMessage.IsVisible = !string.IsNullOrEmpty(message);
        StatusMessage.Classes.Set("error", error);
    }

    private void Focus(Control control) => Dispatcher.UIThread.Post(() => { if (!_closed && control.IsEffectivelyVisible) control.Focus(); },
        DispatcherPriority.Loaded);
}
