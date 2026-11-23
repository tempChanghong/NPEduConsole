using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class TeacherAllocationWindow : Window
{
    private sealed record PendingRequest(TeacherAllocationDraft Draft, TeacherAllocationPreview Preview, Guid Id);
    private sealed record TermOption(OrganizationTerm? Term)
    {
        public override string ToString() => Term?.ToString() ?? "无指定学期 · 只建账号";
    }
    private sealed record GradeOption(OrganizationGradeOption? Grade)
    {
        public override string ToString() => Grade?.ToString() ?? "查看整个学期";
    }
    private static readonly ConditionalWeakTable<ConsoleClient, Dictionary<string, PendingRequest>> Requests = new();
    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly bool _archivedCorrection;
    private readonly string? _initialTermId, _initialGradeId, _initialWorkspaceId;
    private readonly CancellationTokenSource _lifetime = new();
    private TeacherAllocationCatalog? _catalog;
    private TeacherAllocationDocumentTarget? _fileTarget, _rejectedTarget;
    private TeacherAllocationPreview? _preview;
    private AllocationDifference[] _reviewRows = [];
    private bool _busy, _writing, _binding, _closed, _allowClose, _discard, _dirty, _reviewed, _rescope, _retryMissingReceipt;
    private string Mode => _catalog?.Mode ?? "LOCAL_PIN";
    private bool EmailMode => Mode == "OAUTH_EMAIL";
    private bool PinMode => !_archivedCorrection && Mode == "LOCAL_PIN";
    private bool CanEdit => !_busy && Pending is null && _catalog is not null && !_rescope;
    private OrganizationTerm? Term => _catalog?.Terms.FirstOrDefault(row => row.Id == _catalog.TermId);
    private string PendingKey => _archivedCorrection ? $"{_school.Id}:{_initialTermId}:{_initialGradeId}" : _school.Id;
    private Dictionary<string, PendingRequest> SessionRequests => Requests.GetOrCreateValue(_client);
    internal static bool HasOtherPendingTarget(ConsoleClient client, string schoolId, string termId, string gradeId) =>
        Requests.TryGetValue(client, out var requests) && requests.TryGetValue(schoolId, out var pending) &&
        (pending.Draft.TermId != termId || pending.Draft.GradeId != gradeId);

    private PendingRequest? Pending => SessionRequests.GetValueOrDefault(PendingKey);
    internal Action<ConsoleApiException>? SessionExpired { get; init; }
    internal Func<Window, Task<bool>>? OpenSchoolProfile { get; init; }
    internal Uri? LegacyWebLink { get; init; }
    internal string? AllocatedAccountId => _lastReceipt?.Teachers.Length == 1 ? _lastReceipt.Teachers[0].AccountId : null;
    internal string? InitialInvitationId { get; init; }
    internal bool AccessChanged { get; private set; }
    internal (string TermId, string WorkspaceId, string? SubjectId, string AccountId, bool IsGroup)? TeachingTarget { get; private set; }

    public TeacherAllocationWindow() => InitializeComponent();

    internal TeacherAllocationWindow(ConsoleClient client, School school, string? termId, string? gradeId,
        bool archivedCorrection = false, string? initialWorkspaceId = null) : this()
    {
        _client = client;
        _school = school;
        _archivedCorrection = archivedCorrection;
        _initialTermId = termId;
        _initialGradeId = gradeId;
        _initialWorkspaceId = initialWorkspaceId;
        if (_archivedCorrection)
        {
            Title = AllocationHeading.Text = "归档学期 · 教师访问";
            AllocationJson.IsVisible = AllocationPaste.IsVisible = AllocationAddTeacher.IsVisible = false;
            AllocationPreviewButton.Content = "预览访问变化";
            AllocationApprove.Content = "我已核对该学期的直接访问变化";
        }
        AllocationScope.Text = school.Name;
        AllocationFilter.ItemsSource = new[] { "全部", "新增", "修改或移除", "阻断与待复核", "一致" };
        AllocationFilter.SelectedIndex = 0;
        SizeChanged += (_, _) => ArrangeAllocationLayout();
        Opened += async (_, _) =>
        {
            AllocationSchoolProfile.IsVisible = OpenSchoolProfile is not null;
            BatchWorkspaceWindow.FitSchoolWindow(this);
            ArrangeAllocationLayout();
            await RunAsync(async () =>
            {
                if (Pending is not { } request)
                {
                    await LoadAsync(termId, gradeId);
                    if (!_closed && _archivedCorrection)
                        Status("仅为已有本校教师维护这个历史年级的直接访问。任课、职责和其他访问分别核对。");
                    if (!_closed && InitialInvitationId is { } invitation) FocusInvitation(invitation);
                    return;
                }
                try { await LoadAsync(request.Draft.TermId, request.Draft.GradeId, request.Draft.TermId is null); }
                catch (ConsoleApiException error) when (IsScopeError(error))
                {
                    if (_archivedCorrection) _rescope = true;
                    else await LoadAsync(null, null);
                }
                if (_closed) return;
                SetTable(request.Draft.Teachers, dirty: false);
                await VerifyAsync(request);
            });
        };
        Closing += (_, args) =>
        {
            args.Cancel = !_allowClose && (_writing || _dirty && !_discard && Pending is null);
            if (args.Cancel && !_writing)
            {
                _discard = true;
                AllocationClose.Content = "放弃草稿并返回";
                Status("还有未保存的教师表。再次确认返回可放弃，继续编辑可保留。");
            }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _archiveInvitationConfirmation?.Close();
            _lifetime.Cancel();
            _lifetime.Dispose();
            foreach (var row in _teachers) row.Pin = "";
        };
        Render();
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
        if (_archivedCorrection || _initialWorkspaceId is not null || _binding || _busy || Pending is not null || AllocationTerm.SelectedItem is not TermOption target ||
            !_rescope && target.Term?.Id == _catalog?.TermId) return;
        await ChangeScopeAsync(target.Term?.Id, null, target.Term is null);
    }

    private async void GradeChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_archivedCorrection || _binding || _busy || Pending is not null || AllocationGrade.SelectedItem is not GradeOption target ||
            Term is not { } term || target.Grade?.Id == _catalog?.GradeId) return;
        await ChangeScopeAsync(term.Id, target.Grade?.Id, false);
    }

    private async Task ChangeScopeAsync(string? termId, string? gradeId, bool accountsOnly)
    {
        ClearReview();
        await RunAsync(async () =>
        {
            await LoadAsync(termId, gradeId, accountsOnly);
            if (_closed) return;
            _rescope = false;
            if (_dirty) Status("已保留教师草稿。请按新目标核对每条班级访问。");
        });
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
            if (!await open(this) || _closed) return;
            ClearReview();
            await LoadAsync(_catalog?.TermId, _catalog?.GradeId, _catalog is {TermId: null});
            Status("学校登录方式已重新核对，原教师表保留。请重新预览草稿。");
        });
        if (!_closed) AllocationSchoolProfile.Focus();
    }

    internal void LoadAllocationText(string text, bool legacy)
    {
        var file = legacy ? TeacherAllocationDocumentReader.ReadLegacy(text, Mode) : null;
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
        AllocationTabs.SelectedIndex = 0;
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
        _rejectedTarget = null;
        Status("已保留打开文件前的教师表，请按当前目标重新预览。");
        Render();
    }

    private async void OpenJsonClicked(object? sender, RoutedEventArgs args)
    {
        if (!CanEdit) return;
        await RunAsync(async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {Title = "打开旧教师分配 JSON",
                AllowMultiple = false, FileTypeFilter = [new("教师分配 JSON") {Patterns = ["*.json"]}]});
            if (files.Count != 1 || _closed) return;
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            var buffer = new char[OrganizationImportDocumentReader.MaximumCharacters + 1];
            var count = await reader.ReadBlockAsync(buffer, _lifetime.Token);
            if (!_closed) LoadAllocationText(new(buffer, 0, count), legacy: true);
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
        if (_binding) return;
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
        AllocationApprove.IsChecked = false;
        AllocationFilter.SelectedIndex = _reviewRows.Any(row => row.Action == "BLOCK" || row.NeedsReview) ? 3 : 0;
        AllocationTabs.SelectedIndex = 1;
        ShowFilteredReview();
        Dispatcher.UIThread.Post(() =>
        {
            if (!_closed && AllocationTabs.SelectedIndex == 1 && AllocationDifferences.SelectedItem is { } row &&
                AllocationDifferences.ContainerFromItem(row) is Control cell) cell.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private void ShowFilteredReview()
    {
        var filter = AllocationFilter.SelectedIndex;
        var rows = _reviewRows.Where(row => filter switch
        {
            1 => row.Action == "ADD", 2 => row.Action is "CHANGE" or "REMOVE", 3 => row.Action == "BLOCK" || row.NeedsReview,
            4 => row.Action == "KEEP", _ => !_archivedCorrection || row.Action != "KEEP" || row.WorkspaceId is not null
        }).ToArray();
        AllocationDifferences.ItemsSource = rows;
        AllocationDifferences.SelectedIndex = rows.Length == 0 ? -1 : 0;
        AllocationLocate.IsEnabled = rows.Length > 0;
        var stale = _reviewRows.Count(row => row.NeedsReview);
        AllocationCounts.Text = $"新增 {_reviewRows.Count(row => row.Action == "ADD")} · 变更 {_reviewRows.Count(row => row.Action is "CHANGE" or "REMOVE")} · 阻断 {_reviewRows.Count(row => row.Action == "BLOCK")}" +
            (stale == 0 ? "" : $" · 待复核 {stale}");
    }

    private async void SaveClicked(object? sender, RoutedEventArgs args)
    {
        if (!_busy && _retryMissingReceipt && Pending is { } pending)
        {
            await SubmitAsync(pending, pending.Draft);
            return;
        }
        if (!CanEdit || HasDocumentConflict || !_reviewed || AllocationApprove.IsChecked != true || _preview is not {CanApply: true} review) return;
        var draft = Draft();
        var request = new PendingRequest(draft.WithoutPins(), review, Guid.NewGuid());
        SessionRequests[PendingKey] = request;
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
            SessionRequests.Remove(PendingKey);
            _reviewed = false;
            throw;
        }
        if (_closed) return;
        SessionRequests.Remove(PendingKey);
        if (result.Receipt is { } receipt) await ShowReceiptAsync(receipt, recovered: false);
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
            _retryMissingReceipt = _archivedCorrection && !_rescope;
            Status(_retryMissingReceipt ? "暂未找到本次回执。可继续只读查询，或按原编号重试。" :
                "暂未找到本次回执，结果仍待核对。请稍后按原请求查询。");
        }
        else
        {
            SessionRequests.Remove(PendingKey);
            await ShowReceiptAsync(receipt, recovered: true);
        }
    }

    private async Task ShowReceiptAsync(TeacherAllocationReceipt receipt, bool recovered)
    {
        AccessChanged = true;
        _dirty = false;
        ClearPins();
        ClearReview();
        _lastReceipt = receipt;
        try { await LoadAsync(receipt.TermId, receipt.GradeId, receipt.TermId is null); }
        catch (ConsoleApiException error)
        {
            SessionExpired?.Invoke(error);
            if (!_closed) Status("服务器已确认成功；当前目录暂未刷新。请重读实际账号和访问。");
            return;
        }
        if (_closed) return;
        AllocationTabs.SelectedIndex = 2;
        if (receipt.Teachers.FirstOrDefault() is { } first)
            AllocationDirectoryTeachers.SelectedItem = AllocationDirectoryTeachers.Items.OfType<DirectoryPerson>().FirstOrDefault(row =>
                first.AccountId is not null ? row.Id == first.AccountId : first.Email == row.Email);
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
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error) when (!_closed && IsScopeError(error))
        {
            ClearReview();
            _rescope = true;
            try { if (!_archivedCorrection) await LoadAsync(null, null); }
            catch (ConsoleApiException loadError) { SessionExpired?.Invoke(loadError); }
            if (!_closed)
            {
                _binding = true;
                if (!_archivedCorrection) AllocationTerm.SelectedIndex = -1;
                _binding = false;
                Status(error.Message + (_archivedCorrection ? " 草稿已保留，请返回学期目录核对。" :
                    " 草稿已保留，请重新选择目标再预览。"));
            }
        }
        catch (ConsoleApiException error) when (!_closed)
        {
            if (_archivedCorrection && error.StatusCode == 403)
            {
                _rescope = true;
                _catalog = null;
                _lastReceipt = null;
                ClearReview();
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
        if (AllocationStatus is null) return;
        var edit = CanEdit;
        var pending = Pending is not null;
        AllocationTerm.IsEnabled = !_archivedCorrection && _initialWorkspaceId is null && !_busy && !pending && _catalog is not null;
        AllocationGrade.IsEnabled = AllocationTerm.IsEnabled && Term is not null && !_rescope;
        AllocationRefresh.IsEnabled = !_busy && !pending;
        AllocationJson.IsEnabled = AllocationPaste.IsEnabled = AllocationTeacherTable.IsEnabled = AllocationAccessEditor.IsEnabled = edit;
        AllocationPreviewButton.IsVisible = !pending && !AllocationLegacyWeb.IsVisible;
        var differences = (_rejectedTarget ?? _fileTarget) is { } target ? TargetDifferences(target) : [];
        var conflict = HasDocumentConflict;
        AllocationTargetNotice.IsVisible = conflict;
        AllocationTargetDetails.Text = differences.Length > 0 ? string.Join("\n", differences) + "\n请换文件，或调整所选目标后重新打开。" :
            "目标已匹配。请重新打开刚才的文件，或保留原草稿。";
        AllocationKeepTable.IsVisible = _rejectedTarget is not null;
        AllocationKeepTable.IsEnabled = edit;
        AllocationPreviewButton.IsEnabled = edit && !conflict && _teachers.Count > 0;
        AllocationApprove.IsEnabled = edit && !conflict && _reviewed;
        AllocationSave.IsEnabled = !_busy && (pending ? _retryMissingReceipt : edit && !conflict && _reviewed && AllocationApprove.IsChecked == true);
        if (_archivedCorrection)
        {
            AllocationSave.IsVisible = !pending || _retryMissingReceipt;
            AllocationSave.Content = pending ? "按原编号重试" : $"确认访问变更 {_reviewRows.Count(row => row.Action is "ADD" or "CHANGE" or "REMOVE")} 项";
        }
        AllocationVerify.IsVisible = pending;
        AllocationVerify.IsEnabled = !_busy;
        AllocationClose.IsEnabled = !_writing;
        AllocationFooter.Text = $"{_teachers.Count} 位教师 · {_teachers.Sum(row => row.Access.Count)} 项分配";
    }

    private bool HasDocumentConflict => _rejectedTarget is not null || _fileTarget is { } target && TargetDifferences(target).Length > 0;

    private void Status(string text) { AllocationStatus.Text = text; ToolTip.SetTip(AllocationStatus, text); }
    private void TabChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_client is null || !ReferenceEquals(args.Source, AllocationTabs)) return;
        if (AllocationTabs.SelectedIndex == 0) RebuildAccessEditor();
    }
    private void FilterChanged(object? sender, SelectionChangedEventArgs args) { if (_client is not null) ShowFilteredReview(); }
    private void ApprovalChanged(object? sender, RoutedEventArgs args) { if (_client is not null) Render(); }
    private void CloseClicked(object? sender, RoutedEventArgs args) => Close();
    private void OpenLegacyWebClicked(object? sender, RoutedEventArgs args)
    {
        if (LegacyWebLink is { } link && !SchoolAdminBrowser.Open(link)) Status("浏览器未能打开，请从旧网页的「教师分配」继续。");
    }
    internal void CloseForScopeChange() { _allowClose = true; Close(); }
}
