using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class TeacherAllocationView : UserControl
{
    private sealed record PendingRequest(string SchoolId, string ScopeLabel, TeacherAllocationDraft Draft,
        TeacherAllocationPreview Preview, Guid Id);
    internal enum Stage { Edit, Review, ByTeacher, BySpace, Receipt }
    private sealed record TermOption(OrganizationTerm? Term)
    {
        public override string ToString() => Term?.ToString() ?? "无指定学期 · 只建账号";
    }
    private sealed record GradeOption(OrganizationGradeOption? Grade)
    {
        public override string ToString() => Grade?.ToString() ?? "查看整个学期";
    }
    private static readonly ConditionalWeakTable<ConsoleClient, Dictionary<Guid, PendingRequest>> Requests = new();
    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly bool _archivedCorrection, _fixedScope;
    private readonly string? _initialTermId, _initialGradeId, _initialWorkspaceId;
    private readonly CancellationTokenSource _lifetime = new();
    private TeacherAllocationCatalog? _catalog;
    private TeacherAllocationDocumentTarget? _fileTarget, _rejectedTarget;
    private string? _unsupportedFile;
    private StaffConfigurationDocument? _staffConfigurationFile;
    private readonly Dictionary<string, InitialPinDelivery<int>> _deliveries = new(StringComparer.Ordinal);
    private TeacherAllocationPreview? _preview;
    private AllocationDifference[] _reviewRows = [];
    private bool _busy, _writing, _binding, _closed, _allowClose, _discard, _dirty, _reviewed, _rescope, _retryMissingReceipt;
    private string Mode => _catalog?.Mode ?? "LOCAL_PIN";
    private bool EmailMode => Mode == "OAUTH_EMAIL";
    private bool PinMode => !_archivedCorrection && Mode == "LOCAL_PIN";
    private bool CanEdit => !_busy && Pending is null && _catalog is not null && !_rescope;
    private OrganizationTerm? Term => _catalog?.Terms.FirstOrDefault(row => row.Id == _catalog.TermId);
    private Dictionary<Guid, PendingRequest> SessionRequests => Requests.GetOrCreateValue(_client);
    private PendingRequest? Pending { get; set; }
    private bool HasUnresolvedTarget => _catalog is not null && Requests.TryGetValue(_client, out var requests) &&
        requests.Values.Any(request => MatchesTarget(request, _catalog.TermId, _catalog.GradeId, _archivedCorrection));
    private Stage _stage;
    internal Stage DisplayedStage => _stage;
    internal bool ReturningWithDraft => _dirty && !_allowClose && Pending is null;
    internal Guid? RecoveryId { get; init; }
    internal Func<Guid, string?, string?, bool, Task>? OpenPendingRequest { get; set; }
    internal Func<Task>? OpenTeaching { get; set; }
    internal Func<StaffConfigurationDocument, string, Task<bool>>? OpenStaffConfiguration { get; set; }
    internal Action? CloseRequested { get; set; }
    private Window HostWindow => (Window)TopLevel.GetTopLevel(this)!;

    internal static void EndSession(ConsoleClient client) => Requests.Remove(client);

    internal static void ForgetPending(ConsoleClient client, string schoolId)
    {
        if (!Requests.TryGetValue(client, out var requests)) return;
        foreach (var request in requests.Values.Where(row => row.SchoolId == schoolId).ToArray()) requests.Remove(request.Id);
    }

    private bool MatchesTarget(PendingRequest request, string? term, string? grade, bool archived) =>
        request.SchoolId == _school.Id && request.Draft.TermId == term && request.Draft.GradeId == grade &&
        request.Draft.ArchivedCorrection == archived;

    private void ClearPending(PendingRequest request)
    {
        SessionRequests.Remove(request.Id);
        if (Pending?.Id == request.Id) Pending = null;
    }

    internal Action<ConsoleApiException>? SessionExpired { get; set; }
    internal Func<Window, Task<bool>>? OpenSchoolProfile { get; set; }
    internal Uri? LegacyWebLink { get; set; }
    internal string? AllocatedAccountId { get; private set; }
    internal string? InitialInvitationId { get; set; }
    internal bool AccessChanged { get; private set; }
    internal (string TermId, string WorkspaceId, string? SubjectId, string AccountId, bool IsGroup)? TeachingTarget { get; private set; }

    public TeacherAllocationView() => InitializeComponent();

    internal TeacherAllocationView(ConsoleClient client, School school, string? termId, string? gradeId,
        bool archivedCorrection = false, string? initialWorkspaceId = null, bool fixedScope = false) : this()
    {
        _client = client;
        _school = school;
        _archivedCorrection = archivedCorrection;
        _fixedScope = fixedScope || archivedCorrection || initialWorkspaceId is not null;
        _initialTermId = termId;
        _initialGradeId = gradeId;
        _initialWorkspaceId = initialWorkspaceId;
        if (_archivedCorrection)
        {
            AllocationHeading.Text = "归档学期 · 教师访问";
            AllocationJson.IsVisible = AllocationPaste.IsVisible = AllocationAddTeacher.IsVisible = false;
            AllocationPreviewButton.Content = "预览访问变化";
            AllocationApprove.Content = "我已核对该学期的直接访问变化";
        }
        AllocationScope.Text = school.Name;
        AllocationFilter.ItemsSource = new[] { "全部", "新增", "修改或移除", "阻断与待复核", "一致" };
        AllocationFilter.SelectedIndex = 0;
        DetachedFromVisualTree += (_, _) => ClearPinDeliveries();
        AllocationPinBatch.CloseOrOpenReview += Render;
        AllocationPinBatch.ReadWebsiteRequested += ReadBatchWebsite;
        AllocationPinBatch.RefreshRequested += RefreshBatchDirectory;
        AllocationPinBatch.SaveRequested += SaveBatchPins;
        SizeChanged += (_, _) => ArrangeAllocationLayout();
        AddHandler(KeyDownEvent, AllocationKeyDown, RoutingStrategies.Tunnel);
        Render();
    }

    internal Task StartAsync() => RunAsync(async () =>
    {
        var pending = RecoveryId is { } id
            ? SessionRequests.GetValueOrDefault(id) : null;
        if (RecoveryId is not null && (pending is null || !MatchesTarget(pending, _initialTermId, _initialGradeId, _archivedCorrection)))
        {
            _rescope = true;
            Status("这笔分配已结束核对。请返回原页。");
            return;
        }

        if (pending is not null) Pending = pending;
        try { await LoadAsync(_initialTermId, _initialGradeId, pending?.Draft.TermId is null && pending is not null); }
        catch (ConsoleApiException error) when (pending is not null && !_closed &&
            (IsScopeError(error) || error.StatusCode is 0 or 404 or 408 or >= 500))
        {
            _rescope = true;
        }
        if (_closed) return;
        if (pending is not null)
        {
            SetTable(pending.Draft.Teachers, dirty: true);
            await VerifyAsync(pending);
            return;
        }
        if (_archivedCorrection)
            Status("仅为已有本校教师维护这个历史年级的直接访问。任课、职责和其他访问分别核对。");
        if (InitialInvitationId is { } invitation) FocusInvitation(invitation);
        else FocusLater(AllocationTerm.IsEnabled ? AllocationTerm : AllocationTeachers);
    });

    internal bool CanClose()
    {
        if (_writing) return false;
        if (!ReturningWithDraft) return true;
        _discard = true;
        Status("还有未保存的教师表。可继续编辑，或放弃草稿后返回。");
        Render();
        FocusLater(AllocationKeepEditing);
        return false;
    }

    internal void End()
    {
        if (_closed) return;
        _closed = true;
        _staffConfigurationFile = null;
        ClearPinDeliveries();
        _archiveInvitationConfirmation?.Close();
        _pasteDialog?.Close();
        _retryPinDialog?.Close();
        _lifetime.Cancel();
        _lifetime.Dispose();
        foreach (var row in _teachers) row.Pin = "";
        foreach (var box in _allocationFields.Values.OfType<TextBox>().Where(box => box.PasswordChar != '\0')) box.Clear();
        _teachers.Clear();
        _allocationFields.Clear();
        _catalog = null;
        _reviewRows = [];
        _preview = null;
        _lastReceipt = null;
        Pending = null;
        AllocationTerm.ItemsSource = AllocationGrade.ItemsSource = AllocationExisting.ItemsSource = null;
        AllocationTeachers.ItemsSource = AllocationDifferences.ItemsSource = AllocationReceiptRows.ItemsSource = null;
        AllocationDirectoryTeachers.ItemsSource = AllocationDirectorySpaces.ItemsSource = null;
        AllocationTeacherDetails.Children.Clear();
        AllocationSpaceDetails.Children.Clear();
        AllocationAccessRows.Children.Clear();
        AllocationPendingActions.Children.Clear();
    }

    private async Task LoadAsync(string? termId, string? gradeId, bool accountsOnly = false)
    {
        var data = await _client.GetTeacherAllocationAsync(_school.Id, termId, gradeId, accountsOnly, _lifetime.Token, _archivedCorrection);
        if (_closed) return;
        if (_catalog is not null && data.Mode != _catalog.Mode)
            foreach (var teacher in _teachers) teacher.Pin = "";
        _catalog = data;
        AllocationLegacyWeb.IsVisible = false;
        _binding = true;
        try
        {
            AllocationTerm.ItemsSource = new[] {new TermOption(null)}.Concat(data.Terms.Select(row => new TermOption(row))).ToArray();
            AllocationTerm.SelectedItem = ((TermOption[])AllocationTerm.ItemsSource).First(row => row.Term?.Id == data.TermId);
            AllocationGrade.ItemsSource = new[] {new GradeOption(null)}.Concat(data.Grades.Select(row => new GradeOption(row))).ToArray();
            AllocationGrade.SelectedItem = ((GradeOption[])AllocationGrade.ItemsSource).First(row => row.Grade?.Id == data.GradeId);
            AllocationIdentityHeader.Text = _archivedCorrection ? "账号标识" : EmailMode ? "邮箱" : "短账号";
            AllocationPinHeader.IsVisible = PinMode;
            AllocationTeacherHeader.ColumnDefinitions[2].Width = PinMode ? new(1, Avalonia.Controls.GridUnitType.Star) : new(0);
            string scope = $"{_school.Name} / {Term?.ToString() ?? "学校级账号"} / " +
                (data.Grades.FirstOrDefault(row => row.Id == data.GradeId)?.Name ?? (Term is null ? "无班级分配" : "整个学期"));
            AllocationScope.Text = _archivedCorrection ?
                $"{scope} · 全校启用学期：{data.Terms.FirstOrDefault(row => row.Status == "ACTIVE")?.Name ?? "无"}" :
                $"{scope} · {ModeName()}";
            RefreshExisting();
            RebuildEditors();
            RebuildDirectories();
        }
        finally { _binding = false; }
    }

    private string ModeName() => Mode switch { "LOCAL_PIN" => "个人 PIN", "SHARED_PASSWORD" => "学校通用口令", _ => "OAuth 邮箱" };

    private async void TermChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_closed || _fixedScope || _binding || _busy || Pending is not null || AllocationTerm.SelectedItem is not TermOption target ||
            !_rescope && target.Term?.Id == _catalog?.TermId) return;
        await ChangeScopeAsync(target.Term?.Id, null, target.Term is null);
    }

    private async void GradeChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_closed || _fixedScope || _binding || _busy || Pending is not null || AllocationGrade.SelectedItem is not GradeOption target ||
            Term is not { } term || target.Grade?.Id == _catalog?.GradeId) return;
        await ChangeScopeAsync(term.Id, target.Grade?.Id, false);
    }

    private async Task ChangeScopeAsync(string? termId, string? gradeId, bool accountsOnly)
    {
        if (_dirty && !_rescope)
        {
            RestoreScopeSelection();
            Status("请先保存或放弃当前教师表，再切换办理范围。");
            return;
        }
        ClearPinDeliveries();
        ClearReview();
        await RunAsync(async () =>
        {
            await LoadAsync(termId, gradeId, accountsOnly);
            if (_closed) return;
            _rescope = false;
            if (_dirty) Status("已保留教师草稿。请按新目标核对每条班级访问。");
        });
        RestoreScopeSelection();
    }

    private void RestoreScopeSelection()
    {
        if (_closed || _catalog is null || _rescope) return;
        _binding = true;
        AllocationTerm.SelectedItem = ((TermOption[])AllocationTerm.ItemsSource!).First(row => row.Term?.Id == _catalog.TermId);
        AllocationGrade.SelectedItem = ((GradeOption[])AllocationGrade.ItemsSource!).First(row => row.Grade?.Id == _catalog.GradeId);
        _binding = false;
    }

    private async void RefreshClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy || Pending is not null) return;
        if (_rescope || _catalog is null)
        {
            await ChangeScopeAsync(_initialTermId, _initialGradeId, false);
            return;
        }
        ClearReview();
        await RunAsync(async () =>
        {
            await LoadAsync(_catalog?.TermId, _catalog?.GradeId, _catalog is { TermId: null });
            if (!_closed) Status("已从服务器重新读取当前账号、访问、职责和邀请。");
        });
    }

    private async void OpenSchoolProfileClicked(object? sender, RoutedEventArgs e)
    {
        if (OpenSchoolProfile is not { } open || Pending is not null) return;
        await RunAsync(async () =>
        {
            if (!await open(HostWindow) || _closed) return;
            ClearReview();
            await LoadAsync(_catalog?.TermId, _catalog?.GradeId, _catalog is {TermId: null});
            Status("学校登录方式已重新核对，原教师表保留。请重新预览草稿。");
        });
        if (!_closed) AllocationSchoolProfile.Focus();
    }

    internal void LoadAllocationText(string text, bool legacy)
    {
        _staffConfigurationFile = null;
        TeacherAllocationDocument? file;
        try { file = legacy ? TeacherAllocationDocumentReader.ReadLegacy(text, Mode) : null; }
        catch (StaffConfigurationFileException error)
        {
            _unsupportedFile = error.Message;
            _rejectedTarget = null;
            OfferStaffConfiguration(text);
            ClearReview();
            Status("未导入此文件，原教师表已保留。");
            Render();
            return;
        }
        _unsupportedFile = null;
        if (file is not null && TargetDifferences(file.Target).Length > 0)
        {
            _rejectedTarget = file.Target;
            ClearReview();
            Status("文件目标有差异，原来的教师表已保留。");
            Render();
            return;
        }
        var rows = file?.Teachers ?? TeacherAllocationDocumentReader.ReadColumns(text, Mode);
        _fileTarget = file?.Target;
        _rejectedTarget = null;
        SetTable(rows, dirty: true);
        SetStage(Stage.Edit);
        Status("已载入教师表。确认前会按当前所选学期校验每个教学空间。");
    }

    private string[] TargetDifferences(TeacherAllocationDocumentTarget target)
    {
        var differences = new List<string>();
        if (target.SchoolId is { } schoolId && schoolId != _school.Id)
            differences.Add($"学校　所选 {_school.Name}／文件声明编号 {schoolId}");
        if (target.TermId is { } termId && termId != _catalog?.TermId)
        {
            var fileTerm = target.SchoolId is null || target.SchoolId == _school.Id ?
                _catalog?.Terms.FirstOrDefault(row => row.Id == termId)?.Name : null;
            differences.Add($"学期　所选 {Term?.Name ?? "未选择"}／文件声明 {fileTerm ?? $"编号 {termId}"}");
        }
        if (target.GradeId is { } gradeId && gradeId != _catalog?.GradeId)
        {
            var grades = _catalog?.Grades ?? [];
            var grade = grades.FirstOrDefault(row => row.Id == _catalog?.GradeId)?.Name;
            var fileGrade = (target.SchoolId is null || target.SchoolId == _school.Id) &&
                (target.TermId is null || target.TermId == _catalog?.TermId) ? grades.FirstOrDefault(row => row.Id == gradeId)?.Name : null;
            differences.Add($"年级　所选 {grade ?? "未指定"}／文件声明 {fileGrade ?? $"编号 {gradeId}"}");
        }
        return differences.ToArray();
    }

    private void KeepAllocationTableClicked(object? sender, RoutedEventArgs args)
    {
        _unsupportedFile = null;
        _staffConfigurationFile = null;
        _rejectedTarget = null;
        Status("已保留打开文件前的教师表，请按当前目标重新预览。");
        Render();
    }

    private async void OpenJsonClicked(object? sender, RoutedEventArgs args)
    {
        if (!CanEdit || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        await RunAsync(async () =>
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions {Title = "打开旧教师分配 JSON",
                AllowMultiple = false, FileTypeFilter = [new("教师分配 JSON") {Patterns = ["*.json"]}]});
            if (files.Count != 1 || _closed) return;
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            var buffer = new char[OrganizationImportDocumentReader.MaximumCharacters + 1];
            try
            {
                var count = await reader.ReadBlockAsync(buffer, _lifetime.Token);
                if (!_closed) LoadAllocationText(new(buffer, 0, count), legacy: true);
            }
            finally { Array.Clear(buffer); }
        });
    }

    private void ClearReview()
    {
        _reviewed = false;
        _preview = null;
        AllocationApprove.IsChecked = false;
        if (_reviewRows.Length != 0)
        {
            _reviewRows = [];
            ShowFilteredReview();
        }
    }

    private void Edited()
    {
        if (_binding || _closed) return;
        if (_preview is not null) Status("教师表已修改，请重新预览。");
        _dirty = true;
        _discard = false;
        AllocationClose.Content = "返回";
        ClearReview();
        Render();
    }

    private TeacherAllocationDraft Draft() => new(Mode, Term?.Id, Term?.Status, _catalog?.GradeId,
        _teachers.Select(row => _archivedCorrection ? new AllocationTeacher(null, null, row.AccountId, row.Name, null, false, [.. row.Access]) :
            row.Snapshot(Mode)).ToArray(), _archivedCorrection);

    private async void PreviewClicked(object? sender, RoutedEventArgs args)
    {
        if (!CanEdit || HasDocumentConflict || _teachers.Count == 0) return;
        await RunAsync(async () =>
        {
            var review = await _client.PreviewTeacherAllocationAsync(_school.Id, Draft(), _lifetime.Token);
            if (_closed) return;
            _reviewed = review.CanApply && review.Changes.Any(row => row.Action != "KEEP");
            ShowReview(review, []);
            Status(!review.CanApply ? "本批存在阻断项。可用定位按钮或 Enter 返回相应单元格。" :
                _reviewed ? (_archivedCorrection ? "请核对所选教师和直接访问变化，再确认保存。" : "请核对账号和每项访问变化，再确认保存。") :
                "与服务器现状一致，无需重复保存。");
        });
    }

    private void ShowReview(TeacherAllocationPreview review, string[] changed, TeacherAllocationPreview? previous = null)
    {
        _preview = review;
        _locatedIssueKey = null;
        var prior = previous?.Changes.ToDictionary(row => row.Key);
        _reviewRows = review.Changes.Select(row =>
        {
            if (!changed.Contains(row.Key)) return row;
            var old = prior?.GetValueOrDefault(row.Key);
            var context = old is not null && old.Current != row.Current
                ? $"预览时 {old.Current ?? "无记录"}，现为 {row.Current ?? "无记录"}。"
                : "此项的服务器记录已变化。";
            return row with {NeedsReview = true, Message = context + row.Message};
        }).ToArray();
        if (previous is not null)
        {
            var oldRows = changed.Where(key => !_reviewRows.Any(row => row.Key.StartsWith(key + ":", StringComparison.Ordinal) || row.Key == key))
                .Select(key => previous.Changes.FirstOrDefault(row => row.Key == key)).OfType<AllocationDifference>()
                .Select(row => row with {NeedsReview = true, After = null, Message = "此项原预览依据已变化，请重新核对。"});
            _reviewRows = [.. _reviewRows, .. oldRows];
        }
        _reviewRows = _reviewRows.Select(WithReviewIdentity).ToArray();
        AllocationApprove.IsChecked = false;
        AllocationFilter.SelectedIndex = 0;
        SetStage(Stage.Review);
        ShowFilteredReview();
        if (AllocationDifferences.Items.OfType<AllocationDifference>().FirstOrDefault(row => row.NeedsReview || row.Action == "BLOCK") is { } issue)
            AllocationDifferences.SelectedItem = issue;
        FocusSelected(AllocationDifferences);
    }

    private void ShowFilteredReview()
    {
        var filter = AllocationFilter.SelectedIndex;
        var previous = (AllocationDifferences.SelectedItem as AllocationDifference)?.Key;
        var rows = _reviewRows.Where(row => filter switch
        {
            1 => row.Action == "ADD", 2 => row.Action is "CHANGE" or "REMOVE", 3 => row.Action == "BLOCK" || row.NeedsReview,
            4 => row.Action == "KEEP", _ => !_archivedCorrection || row.Action != "KEEP" || row.WorkspaceId is not null
        }).ToArray();
        AllocationDifferences.ItemsSource = rows;
        AllocationDifferences.SelectedItem = rows.FirstOrDefault(row => row.Key == previous) ?? rows.FirstOrDefault();
        AllocationLocate.IsEnabled = rows.Length > 0;
        var stale = _reviewRows.Count(row => row.NeedsReview);
        AllocationCounts.Text = $"新增 {_reviewRows.Count(row => row.Action == "ADD")} · 变更 {_reviewRows.Count(row => row.Action is "CHANGE" or "REMOVE")} · 阻断 {_reviewRows.Count(row => row.Action == "BLOCK")}" +
            (stale == 0 ? "" : $" · 待复核 {stale}");
    }

    private async void SaveClicked(object? sender, RoutedEventArgs args)
    {
        if (!_busy && _retryMissingReceipt && Pending is { } pending)
        {
            await RetryOriginalAsync(pending);
            return;
        }
        if (_stage != Stage.Review || !CanEdit || HasUnresolvedTarget || HasDocumentConflict || !_reviewed || AllocationApprove.IsChecked != true || _preview is not {CanApply: true} review) return;
        var draft = Draft();
        var request = new PendingRequest(_school.Id, AllocationScope.Text ?? _school.Name, draft.WithoutPins(), review, Guid.NewGuid());
        SessionRequests.Add(request.Id, request);
        Pending = request;
        await SubmitAsync(request, draft);
    }

    private Task SubmitAsync(PendingRequest request, TeacherAllocationDraft draft) => RunAsync(async () =>
    {
        _retryMissingReceipt = false;
        var review = request.Preview;
        TeacherAllocationResult result;
        try { result = await _client.CommitTeacherAllocationAsync(_school.Id, draft, review, request.Id, _lifetime.Token); }
        catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or >= 500)
        {
            if (!_closed)
            {
                ClearPins();
                await VerifyAsync(request);
            }
            return;
        }
        catch (ConsoleApiException)
        {
            ClearPending(request);
            _reviewed = false;
            throw;
        }
        if (_closed) return;
        ClearPending(request);
        if (result.Receipt is { } receipt)
        {
            ClearPinDeliveries();
            if (draft.Mode == "LOCAL_PIN" && !draft.ArchivedCorrection && result.AppliedPins is { } applied)
                for (var i = 0; i < receipt.Teachers.Length; i++)
                {
                    var row = receipt.Teachers[i];
                    if (row is {Created: true, AccountId: { } id, Username: { } username} && draft.Teachers[i].Pin is { } pin &&
                        applied.FirstOrDefault(item => item.AccountId == id) is {Version: >= 0} proof)
                        _deliveries[id] = new(id, username, pin, proof.Version);
                }
            BindPinBatch(receipt);
            await ShowReceiptAsync(receipt, recovered: false);
        }
        else if (result.Preview is { } observed)
        {
            _reviewed = false;
            var changed = result.ChangedKeys ?? [];
            var marked = changed.SelectMany(key => key.EndsWith(":email", StringComparison.Ordinal) || key.EndsWith(":same-name", StringComparison.Ordinal)
                ? new[] {key, key[..key.LastIndexOf(':')]} : new[] {key}).Distinct().ToArray();
            ShowReview(observed, marked, review);
            Status(result.State == "STALE" ? "服务器已有变化，本次零写入。草稿已保留，请定位复核后重新预览。" :
                "分配存在阻断项，本次零写入。请修正表格。");
        }
    }, writing: true);

    private async void VerifyClicked(object? sender, RoutedEventArgs args)
    {
        if (Pending is { } request) await RunAsync(() => VerifyAsync(request));
    }

    private async Task VerifyAsync(PendingRequest request)
    {
        _retryMissingReceipt = false;
        Status("正在按原请求只读核对。");
        var receipt = await _client.GetTeacherAllocationReceiptAsync(_school.Id, request.Draft, request.Id, _lifetime.Token);
        if (_closed) return;
        if (receipt is null)
        {
            _retryMissingReceipt = !_rescope;
            var retry = RequiredRetryPins(request).Length == 0 ? "或按原编号重试。" : "或重新输入新账号的初始 PIN 后按原编号重试。";
            Status(_retryMissingReceipt ? "暂未找到本次回执。可继续只读查询，" + retry :
                "暂未找到本次回执，结果仍待核对。请稍后按原请求查询。");
        }
        else
        {
            ClearPending(request);
            await ShowReceiptAsync(receipt, recovered: true);
        }
    }

    private async Task ShowReceiptAsync(TeacherAllocationReceipt receipt, bool recovered)
    {
        if (recovered) ClearPinDeliveries();
        AccessChanged = true;
        _dirty = false;
        ClearPins();
        ClearReview();
        _lastReceipt = receipt;
        AllocatedAccountId = receipt.Teachers.Length == 1 ? receipt.Teachers[0].AccountId : null;
        ShowReceiptRows(receipt);
        _writing = false;
        SetStage(Stage.Receipt);
        await ReadReceiptDirectoryAsync(receipt, recovered);
    }

    private async Task ReadReceiptDirectoryAsync(TeacherAllocationReceipt receipt, bool recovered = false)
    {
        try { await LoadAsync(receipt.TermId, receipt.GradeId, receipt.TermId is null); }
        catch (ConsoleApiException error) when (!_closed && error.StatusCode is not (401 or 403))
        {
            if (IsScopeError(error) || error.StatusCode is 404 or 409 or 426) ClearPinDeliveries();
            else AllocationPinBatch.SetDirectoryPending(true);
            Status("服务器已确认成功；当前目录暂未刷新。");
            return;
        }
        if (_closed) return;
        if (_deliveries.Count != 0 && (_catalog is not { Mode: "LOCAL_PIN" } data || data.TermId != receipt.TermId ||
            receipt.TermId is not null && !data.Terms.Any(row => row.Id == receipt.TermId && row.Status == receipt.TermStatus)))
        {
            ClearPinDeliveries();
            ShowReceiptRows(receipt);
            Status("服务器已确认成功。原学期或登录方式已变化，本次 PIN 已清除。");
            return;
        }
        AllocationPinBatch.SetDirectoryPending(false);
        ShowReceiptRows(receipt);
        FocusLater(AllocationReceiptRows);
        var accounts = receipt.Teachers.Count(row => row.Created);
        var access = receipt.Teachers.Sum(row => row.Access.Length);
        var confirmation = recovered ? "已从服务器找回原回执" : "服务器已确认";
        Status(_archivedCorrection ? $"{confirmation} · 历史直接访问办理完成，已核对 {access} 条访问。" :
            $"{confirmation} · 新建 {accounts} 个学校账号 · 已核对 {access} 条访问。" +
                (receipt.TermStatus == "DRAFT" ? "分配属于草稿学期。" : ""));
    }

    private static bool IsScopeError(ConsoleApiException error) =>
        error.Code is "TEACHER_ALLOCATION_TERM_CHANGED" or "TEACHER_ALLOCATION_GRADE_CHANGED" or "TEACHER_ALLOCATION_MODE_CHANGED" or
            "ARCHIVED_INVITATION_TERM_CHANGED";

    private async Task RunAsync(Func<Task> action, bool writing = false)
    {
        if (_busy || _closed) return;
        _busy = true;
        _writing = writing;
        Render();
        try { await action(); }
        catch (Exception error) when (_closed && error is OperationCanceledException or ConsoleApiException or IOException) { }
        catch (ConsoleApiException error) when (!_closed && IsScopeError(error))
        {
            ClearReview();
            _rescope = true;
            try { if (!_fixedScope) await LoadAsync(null, null); }
            catch (ConsoleApiException loadError) { SessionExpired?.Invoke(loadError); }
            if (!_closed)
            {
                _binding = true;
                if (!_fixedScope) AllocationTerm.SelectedIndex = -1;
                _binding = false;
                Status(error.Message + (_fixedScope ? " 草稿已保留，请返回原入口核对目标。" :
                    " 草稿已保留，请重新选择目标再预览。"));
            }
        }
        catch (ConsoleApiException error) when (!_closed)
        {
            if (error.StatusCode is 401 or 403) ClearPinDeliveries();
            if (error.StatusCode == 403)
            {
                ForgetPending(_client, _school.Id);
                Pending = null;
                _rescope = true;
                _catalog = null;
                _lastReceipt = null;
                ClearReview();
                SetTable([], dirty: false);
                _dirty = false;
                AllocationReceiptRows.ItemsSource = null;
                RefreshExisting();
                RebuildDirectories();
            }
            SessionExpired?.Invoke(error);
            if (!_archivedCorrection && _catalog is null && Pending is null && error.StatusCode is (404 or 426) && LegacyWebLink is not null)
            {
                AllocationLegacyWeb.IsVisible = true;
                Status("当前 KV 版本未提供这项桌面邮箱分配，可从兼容网页继续。");
                return;
            }
            if (!_closed) Status(Pending is null ? error.Message : "服务器回执暂不可读。请重试只读核对。");
        }
        catch (ArgumentException error) when (!_closed) { Status(error.Message); }
        catch (IOException) when (!_closed) { Status("无法读取所选文件，请核对访问权限。"); }
        finally
        {
            _busy = false;
            _writing = false;
            if (!_closed) Render();
        }
    }

    private void Render()
    {
        if (AllocationStatus is null || _client is null || _closed) return;
        var edit = CanEdit;
        var pending = Pending is not null;
        bool editing = _stage == Stage.Edit;
        bool reviewing = _stage == Stage.Review;
        bool directory = _stage is Stage.ByTeacher or Stage.BySpace;
        bool showDirectories = !pending && !_discard && !_busy && !reviewing && _catalog is not null;
        AllocationStageLabel.Text = _stage switch
        {
            Stage.Review => "核对差异", Stage.Receipt => "分配结果",
            Stage.ByTeacher => "按教师查", Stage.BySpace => "按班查", _ => "录入教师表"
        };
        AllocationEditLayout.IsVisible = editing && !pending;
        AllocationReviewLayout.IsVisible = reviewing && !pending;
        AllocationTeacherDirectoryLayout.IsVisible = _stage == Stage.ByTeacher && !pending;
        AllocationSpaceDirectoryLayout.IsVisible = _stage == Stage.BySpace && !pending;
        AllocationReceiptLayout.IsVisible = _stage == Stage.Receipt && !pending;
        Grid.SetRowSpan(AllocationPinBatch, AllocationPinBatch.Reviewing ? 2 : 1);
        AllocationReceiptContent.IsVisible = AllocationMainActions.IsVisible = !AllocationPinBatch.Reviewing;
        AllocationStatus.IsVisible = !AllocationPinBatch.Reviewing && !string.IsNullOrWhiteSpace(AllocationStatus.Text);
        AllocationPinBatch.Render(_busy);
        AllocationTargetFields.IsVisible = editing && !pending;
        AllocationTerm.IsEnabled = !_fixedScope && !_busy && !pending && _catalog is not null;
        AllocationGrade.IsEnabled = AllocationTerm.IsEnabled && Term is not null && !_rescope;
        AllocationRefresh.IsVisible = !pending && !_discard && (directory || _rescope || _catalog is null);
        AllocationRefresh.IsEnabled = !_busy;
        AllocationJson.IsEnabled = AllocationPaste.IsEnabled = AllocationTeacherTable.IsEnabled = AllocationAccessEditor.IsEnabled = edit && !_discard;
        var differences = (_rejectedTarget ?? _fileTarget) is { } target ? TargetDifferences(target) : [];
        var conflict = HasDocumentConflict;
        AllocationTargetNotice.IsVisible = editing && conflict && !pending;
        AllocationTargetDetails.Text = _unsupportedFile ?? (differences.Length > 0 ? string.Join("\n", differences) + "\n请换文件，或调整所选目标后重新打开。" :
            "目标已匹配。请重新打开刚才的文件，或保留原草稿。");
        AllocationKeepTable.IsVisible = _rejectedTarget is not null || _unsupportedFile is not null;
        AllocationKeepTable.IsEnabled = edit;
        AllocationOpenStaffConfiguration.IsVisible = _staffConfigurationFile is not null && OpenStaffConfiguration is not null;
        AllocationOpenStaffConfiguration.IsEnabled = edit && CanOpenStaffConfiguration(_staffConfigurationFile);
        AllocationPreviewButton.IsVisible = !pending && !_discard && !AllocationLegacyWeb.IsVisible && (editing || reviewing && !_reviewed);
        AllocationPreviewButton.Content = editing ? "预览分配" : "重新预览";
        AllocationPreviewButton.IsEnabled = edit && !conflict && _teachers.Count > 0;
        AllocationApprove.IsVisible = _reviewed;
        AllocationApprove.IsEnabled = edit && !conflict && _reviewed;
        AllocationSave.IsVisible = !_discard && (pending ? _retryMissingReceipt : reviewing && _reviewed);
        AllocationSave.IsEnabled = !_busy && (pending ? _retryMissingReceipt :
            edit && !HasUnresolvedTarget && !conflict && _reviewed && AllocationApprove.IsChecked == true);
        AllocationSave.Content = pending ? "按原编号重试" : _archivedCorrection
            ? $"确认访问变更 {_reviewRows.Count(row => row.Action is "ADD" or "CHANGE" or "REMOVE")} 项" : "确认保存";
        AllocationVerify.IsVisible = pending;
        AllocationVerify.IsEnabled = !_busy;
        AllocationClose.IsVisible = !_discard;
        AllocationClose.IsEnabled = !_writing;
        AllocationKeepEditing.IsVisible = AllocationDiscard.IsVisible = _discard;
        AllocationEdit.IsVisible = !_discard && !pending && !editing && _catalog is not null;
        AllocationBackToReview.IsVisible = !_discard && !pending && editing && _preview is not null;
        AllocationBackToReceipt.IsVisible = showDirectories && directory && _lastReceipt is not null;
        AllocationByTeacher.IsVisible = showDirectories && _stage != Stage.ByTeacher;
        AllocationBySpace.IsVisible = showDirectories && _stage != Stage.BySpace;
        AllocationSchoolProfile.IsVisible = !_discard && !pending && editing && OpenSchoolProfile is not null;
        AllocationFooter.Text = $"{_teachers.Count} 位教师 · {_teachers.Sum(row => row.Access.Count)} 项分配";
        RenderReceiptActions();
        UpdatePendingActions();
    }

    private bool HasDocumentConflict => _unsupportedFile is not null || _rejectedTarget is not null || _fileTarget is { } target && TargetDifferences(target).Length > 0;

    private void Status(string text)
    {
        AllocationStatus.Text = text;
        AllocationStatus.IsVisible = !string.IsNullOrWhiteSpace(text);
        ToolTip.SetTip(AllocationStatus, text);
    }
    private void FilterChanged(object? sender, SelectionChangedEventArgs args) { if (_client is not null) ShowFilteredReview(); }
    private void ApprovalChanged(object? sender, RoutedEventArgs args) { if (_client is not null) Render(); }
    private void CloseClicked(object? sender, RoutedEventArgs args) => CloseRequested?.Invoke();
    private void KeepEditingClicked(object? sender, RoutedEventArgs args)
    {
        _discard = false;
        Status("");
        Render();
        FocusLater(AllocationClose);
    }
    private void DiscardClicked(object? sender, RoutedEventArgs args)
    {
        _allowClose = true;
        CloseRequested?.Invoke();
    }

    private void OpenLegacyWebClicked(object? sender, RoutedEventArgs args)
    {
        if (LegacyWebLink is { } link && !SchoolAdminBrowser.Open(link)) Status("浏览器未能打开，请从旧网页的「教师分配」继续。");
    }
}
