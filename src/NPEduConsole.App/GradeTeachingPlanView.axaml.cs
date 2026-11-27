using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed record GradeTeachingPlanRowView(string Key, string ClassId, string Title, string Current, string After, string Action, bool Conflict);
public sealed record GradeTeachingPlanChangeView(string Key, string Text, string? Action = null, string? Kind = null)
{
    public bool HasAction => Action is not null;
}

public sealed partial class GradeTeachingPlanView : UserControl
{
    private sealed record Option(string Id, string Name) { public override string ToString() => Name; }
    private sealed record Submission(GradePlanDraft Draft, GradePlanPreview Preview, Guid RequestId, string? ScopeVersion);
    private sealed record TeachingIssue(string Code, string? Grade, string? Subject, string? Room, string? Group, string? Account);
    private static readonly ConditionalWeakTable<ConsoleClient, Dictionary<(string School, string? Term), Submission>> PendingByClient = new();
    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly string? _specifiedTermId;
    private readonly string? _requiredCurrentTermId, _entryGroupId;
    private readonly bool _inWorkspace;
    private readonly bool _fromClassRules;
    private string? _initialClassId;
    private readonly bool _requireInitialTarget;
    private readonly TeachingIssue? _teachingIssue;
    private Guid? _ownRequestId;
    private string? _continueTeachingKey;
    private bool _archiveEditable;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, GradePlanRuleEdit> _rules = [];
    private readonly Dictionary<string, GradePlanGroupEdit> _groups = [];
    private readonly List<GradePlanTeacherEdit> _assignments = [];
    private GradePlanCatalog? _catalog;
    private GradePlanPreview? _preview, _previousPreview;
    private GradePlanReceipt? _receipt;
    private string? _gradeId, _subjectId, _openingGroup, _selectedGroupKey;
    private bool _busy, _closed, _filling, _scopeLost, _replay, _confirmedPreview, _compact, _checkingReceipt;
    private bool _workspaceReview, _workspaceDraftList;
    private Control? _reviewContent;
    private readonly HashSet<string> _changedKeys = [];
    internal Action<ConsoleApiException>? SessionExpired { get; set; }
    internal Action? CloseRequested { get; set; }
    internal Window OwnerWindow => (Window)TopLevel.GetTopLevel(this)!;
    internal bool WorkspaceChanged { get; private set; }

    private Dictionary<(string School, string? Term), Submission> PendingOperations => PendingByClient.GetOrCreateValue(_client);
    private (string School, string? Term) PendingKey => (_school.Id, _specifiedTermId);
    private Submission? SavedPending => PendingOperations.GetValueOrDefault(PendingKey);
    private Submission? Pending => SavedPending is { } operation &&
        (_teachingIssue is null && !_fromClassRules || operation.RequestId == _ownRequestId) &&
        (_requiredCurrentTermId is null || operation.Draft.TermId == _requiredCurrentTermId) &&
        (_entryGroupId is null || operation.RequestId == _ownRequestId ||
            operation.Draft.GradeId == _gradeId && operation.Draft.SubjectId == _subjectId &&
            operation.Draft.Groups.Any(group => group.GroupId == _entryGroupId)) ? operation : null;
    private bool OtherPlanPending => SavedPending is not null && Pending is null;
    private bool HasDraft => _rules.Count + _groups.Count + _assignments.Count > 0;
    private string? RuleSubjectId => (PlanRuleSubject.SelectedItem as GradePlanLabel)?.Id;
    private IEnumerable<GradeTeachingPlanRowView> SelectedClasses => PlanRows.SelectedItems?.OfType<GradeTeachingPlanRowView>() ?? [];
    private static string RowKey(string id, string subject) => $"{id}:{subject}";

    public GradeTeachingPlanView() => InitializeComponent();

    internal GradeTeachingPlanView(ConsoleClient client, School school, string? gradeId, string? subjectId, string? groupId,
        string? specifiedTermId = null, string? classId = null, string? teacherId = null, string? ruleAction = null,
        bool requireInitialTarget = false, string? teachingIssue = null, bool inWorkspace = false,
        string? requiredCurrentTermId = null) : this()
    {
        _client = client;
        _school = school;
        _specifiedTermId = specifiedTermId;
        _requiredCurrentTermId = requiredCurrentTermId;
        _entryGroupId = requiredCurrentTermId is null ? null : groupId;
        _inWorkspace = inWorkspace;
        _fromClassRules = inWorkspace && ruleAction is not null;
        _initialClassId = classId;
        _requireInitialTarget = requireInitialTarget;
        _openingGroup = groupId;
        _gradeId = gradeId;
        _subjectId = subjectId;
        if (teachingIssue is not null)
        {
            _teachingIssue = new(teachingIssue, gradeId, subjectId, classId, groupId, teacherId);
            PlanTeachingHint.Text = "核对原教学空间的任课。加入要移除的关系后，预览并确认。";
            PlanTeachingAccessHint.Text = "移除任课后，原独立成员权限仍有效。";
            PlanAddTeaching.IsVisible = PlanOpenMixedTeaching.IsVisible = false;
        }
        else if (teacherId is not null) _linkedTeacher = new(gradeId, subjectId, classId, groupId, teacherId);
        _openedFromStaffReview = teacherId is not null && teachingIssue is null;
        if (ruleAction is not null && classId is not null && subjectId is not null)
            _linkedClassRule = new(classId, subjectId, ruleAction);
        PlanScope.Text = school.Name;
        PlanHeading.Text = specifiedTermId is null ? "年级教学安排" : "教学安排与师资";
        PlanColumns.ColumnDefinitions = new(inWorkspace ? "3.6*,2*" : "3*,2*");
        PlanInlineChanges.IsVisible = !inWorkspace;
        PlanRuleMode.ItemsSource = new Option[] {new("ADMIN_CLASS", "随行政班"), new("COURSE_GROUP", "走班"),
            new("UNCONFIGURED", specifiedTermId is null ? "移除该科规则" : "本班不开设")};
        PlanCompulsory.ItemsSource = new Option[] {new("KEEP", "保留必修设置"), new("REQUIRED", "设为必修"), new("OPTIONAL", "设为选修")};
        PlanTeacherRole.ItemsSource = new Option[] {new("PRIMARY", "主讲"), new("CO_TEACHER", "协同")};
        PlanTeacherRole.SelectedIndex = PlanRuleMode.SelectedIndex = PlanCompulsory.SelectedIndex = 0;
        SizeChanged += (_, _) => Dispatcher.UIThread.Post(() => { if (!_closed) Arrange(); }, DispatcherPriority.Loaded);
        Render();
    }

    internal Task StartAsync()
    {
        _reviewContent = PlanReviewTab.Content as Control;
        Arrange(force: true);
        return RunAsync(async () =>
        {
            if (Pending is { } pending)
            {
                _gradeId = pending.Draft.GradeId;
                _subjectId = pending.Draft.SubjectId;
                foreach (var row in pending.Draft.Rules) _rules[RowKey(row.ClassId, row.SubjectId)] = row;
                foreach (var row in pending.Draft.Groups) _groups[row.Key] = row;
                _assignments.AddRange(pending.Draft.Assignments);
                _preview = pending.Preview;
                await VerifyAsync(pending);
            }
            if (!_closed) await LoadAsync();
            if (!_closed && _inWorkspace && _compact && Pending is not null) ShowScopedReview(null);
            if (!_closed && OtherPlanPending && (_entryGroupId is not null || _fromClassRules))
                Notice("年级教学安排还有另一份待核对的方案。可返回常规入口查原回执。", true);
        });
    }

    private bool MayLeave => !_busy || _inWorkspace && _checkingReceipt;
    internal bool ReturningWithDraft => HasDraft && Pending is null && (_inWorkspace || !_scopeLost);

    internal bool CanClose()
    {
        if (!MayLeave) return false;
        if (!ReturningWithDraft) return true;
        ExplainDraft();
        return false;
    }

    internal void End()
    {
        if (_closed) return;
        _closed = true;
        _staffEditor?.CloseForScopeChange();
        _staffReview?.CloseForScopeChange();
        _classRulesEditor?.CloseForScopeChange();
        CancelStaffRead();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private void Arrange(bool force = false)
    {
        bool compact = Bounds.Width < (_inWorkspace ? 850 : 900);
        if (!force && compact == _compact) return;
        _compact = compact;
        Classes.Set("compact-plan", compact);
        // Keep the column count and definitions stable through resizing. Avalonia
        // can still be using the old cell indexes when a SizeChanged event fires.
        PlanColumns.ColumnSpacing = compact ? 0 : 10;
        PlanColumns.ColumnDefinitions[0].Width = new GridLength(compact ? 1 : _inWorkspace ? 3.6 : 3, GridUnitType.Star);
        PlanColumns.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        PlanWideChanges.IsVisible = !compact;
        PlanNarrowChanges.IsVisible = compact;
        if (_inWorkspace)
        {
            if (compact && !_workspaceReview && _preview is not null &&
                (_confirmedPreview || Pending is not null || PlanTabs.SelectedItem == PlanReviewTab)) ShowScopedReview(null);
            else ArrangeWorkspaceReview();
        }
        else if (compact)
        {
            PlanWideChanges.Child = null;
            PlanChangeCard.MinHeight = 180;
            PlanChangeRows.MaxHeight = 220;
            PlanNarrowChanges.Child = PlanChangeCard;
        }
        else
        {
            PlanNarrowChanges.Child = null;
            PlanChangeCard.MinHeight = 0;
            PlanChangeRows.MaxHeight = double.PositiveInfinity;
            PlanWideChanges.Child = PlanChangeCard;
        }
        UpdateReviewLayout();
        UpdateReviewActions();
        Render();
    }

    private async Task LoadAsync()
    {
        var options = await ReadCatalogAsync(null, null, null);
        if (_closed) return;
        if (_requiredCurrentTermId is { } required && options.TermId != required)
            throw new ConsoleApiException(409, "启用学期已变化。请返回原走班重新核对。", "GRADE_PLAN_TERM_CHANGED");
        if (_requireInitialTarget && (_gradeId is { } gradeTarget && !options.Grades.Any(row => row.Id == gradeTarget) ||
            _subjectId is { } subjectTarget && !options.Subjects.Any(row => row.Id == subjectTarget)))
            throw new ConsoleApiException(409, "待办对应的年级或科目已变化。请返回学期诊断重新核对。");
        _filling = true;
        PlanGrade.ItemsSource = options.Grades;
        PlanSubject.ItemsSource = options.Subjects;
        PlanGrade.SelectedItem = options.Grades.FirstOrDefault(row => row.Id == _gradeId) ?? options.Grades.FirstOrDefault();
        PlanSubject.SelectedItem = options.Subjects.FirstOrDefault(row => row.Id == _subjectId) ?? options.Subjects.FirstOrDefault();
        _filling = false;
        _gradeId = (PlanGrade.SelectedItem as GradePlanLabel)?.Id;
        _subjectId = (PlanSubject.SelectedItem as GradePlanLabel)?.Id;
        if (options.TermId is { } term && _gradeId is { } grade && (_subjectId is not null || _specifiedTermId is not null))
            options = await ReadCatalogAsync(term, grade, _subjectId);
        if (_closed) return;
        if (_requireInitialTarget && (_initialClassId is { } classTarget && !options.Classes.Any(row => row.Id == classTarget) ||
            _openingGroup is { } groupTarget && !options.Groups.Any(row => row.Id == groupTarget)))
            throw new ConsoleApiException(409, "待办对应的班级或教学班已变化。请返回学期诊断重新核对。");
        if (_catalog?.ScopeVersion != options.ScopeVersion) _archiveEditable = false;
        _catalog = options;
        _scopeLost = Pending?.Draft.TermId is { } pendingTerm && pendingTerm != options.TermId;
        PlanScope.Text = options.Term is { } selected
            ? $"{_school.Name} / {selected.Name} · {selected.StatusLabel}\n全校启用学期　{options.ActiveTerm?.Name ?? "暂无"}"
            : options.TermName is { } termName ? $"{_school.Name} · {termName}" : $"{_school.Name} · 暂无启用学期";
        BindEditors();
        BindTermObjects();
        OpenLinkedTeacher();
        OpenLinkedClassRule();
        if (!HasDraft && _receipt is null) OpenTeachingIssue();
        if (options.TermId is null) Notice("学校暂无启用学期，请先到学期管理核对。");
        Render();
    }

    private Task<GradePlanCatalog> ReadCatalogAsync(string? termId, string? gradeId, string? subjectId) =>
        _client.GetGradePlanAsync(_school.Id, _specifiedTermId ?? termId, gradeId, subjectId, _lifetime.Token, _specifiedTermId is not null);

    private async void ScopeChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_filling || _busy || _client is null) return;
        if (HasDraft || Pending is not null)
        {
            _filling = true;
            PlanGrade.SelectedItem = _catalog?.Grades.FirstOrDefault(row => row.Id == _gradeId);
            PlanSubject.SelectedItem = _catalog?.Subjects.FirstOrDefault(row => row.Id == _subjectId);
            _filling = false;
            Notice("当前方案仍在编辑。请先保存或清空，再切换年级或学科。");
            return;
        }
        _gradeId = (PlanGrade.SelectedItem as GradePlanLabel)?.Id;
        _subjectId = (PlanSubject.SelectedItem as GradePlanLabel)?.Id;
        _filling = true;
        PlanRuleSubject.SelectedItem = null;
        _filling = false;
        _receipt = null;
        _continueTeachingKey = null;
        await RunAsync(LoadAsync);
    }

    private GradePlanDraft? Draft()
    {
        if (_catalog?.TermId is not { } term || _gradeId is not { } grade || _subjectId is not { } subject) return null;
        var issue = _teachingIssue is { Subject: { } expectedSubject } target && (target.Group ?? target.Room) is { } space
            ? new GradePlanExpectedIssue(target.Code, space, expectedSubject, target.Account) : null;
        return new(term, grade, subject, _rules.Values.ToArray(), _groups.Values.ToArray(), _assignments.ToArray(), issue);
    }

    private async void Preview(object? sender, RoutedEventArgs args)
    {
        if (Draft() is not { HasEdits: true } draft) return;
        await RunAsync(async () =>
        {
            _confirmedPreview = false;
            _changedKeys.Clear();
            _previousPreview = null;
            var observed = await _client.PreviewGradePlanAsync(_school.Id, draft, _lifetime.Token, _catalog?.ScopeVersion);
            if (_closed) return;
            _preview = observed;
            _confirmedPreview = observed.CanApply;
            Notice(observed.CanApply ? "已预览。请核对逐班对照、学生可选项和访问变化，再保存整份方案。" :
                "本方案有未解决的冲突。请定位对应班级或教学班。", !observed.CanApply);
            BindRows();
            if (_specifiedTermId is not null || _inWorkspace && _compact) ShowScopedReview(observed.Issues.FirstOrDefault()?.Key);
            else if (!observed.CanApply && observed.Issues.FirstOrDefault() is { } issue) FocusKey(issue.Key);
            if (_inWorkspace && _compact && observed.CanApply) Focus(PlanSave);
        });
    }

    private async void Save(object? sender, RoutedEventArgs args)
    {
        if (Pending is { } pending)
        {
            if (_replay) await RunAsync(() => SubmitAsync(pending));
            return;
        }
        if (OtherPlanPending || !_confirmedPreview || _preview is not { CanApply: true } preview || Draft() is not { HasEdits: true } draft) return;
        var operation = new Submission(draft, preview, Guid.NewGuid(), _catalog?.ScopeVersion);
        _ownRequestId = operation.RequestId;
        PendingOperations[PendingKey] = operation;
        await RunAsync(() => SubmitAsync(operation));
    }

    private async Task SubmitAsync(Submission operation)
    {
        _replay = false;
        Render();
        GradePlanResult response;
        try { response = await _client.SaveGradePlanAsync(_school.Id, operation.Draft, operation.Preview, operation.RequestId, _lifetime.Token,
            operation.ScopeVersion); }
        catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or >= 500)
        {
            if (!_closed) await VerifyAsync(operation);
            return;
        }
        catch (ConsoleApiException error)
        {
            if (!_closed)
            {
                PendingOperations.Remove(PendingKey);
                _confirmedPreview = false;
                if (error.Code is "GRADE_PLAN_TERM_CHANGED" or "GRADE_PLAN_SCOPE_CHANGED" or "TERM_STRUCTURE_SCOPE_CHANGED") _scopeLost = true;
            }
            throw;
        }
        if (_closed) return;
        if (response.Receipt is { } receipt) { await SavedAsync(receipt, operation.Preview); return; }
        PendingOperations.Remove(PendingKey);
        _previousPreview = response.State == "STALE" ? operation.Preview : null;
        _preview = response.Preview;
        _confirmedPreview = false;
        _changedKeys.Clear();
        foreach (string changedKey in response.ChangedKeys ?? []) _changedKeys.Add(changedKey);
        BindRows();
        string? focusKey = _specifiedTermId is null ? null :
            _changedKeys.FirstOrDefault(key => _groups.ContainsKey(key) || _rules.ContainsKey(key));
        focusKey ??= _changedKeys.FirstOrDefault(key => key != "scope") ?? _preview?.Issues.FirstOrDefault()?.Key;
        if (_specifiedTermId is not null || _inWorkspace && _compact) ShowScopedReview(focusKey);
        else if (focusKey is not null) FocusKey(focusKey);
        Notice(response.State == "STALE" ? "服务器安排已变化，本次整份零写入。保留草稿，请核对变化后重新预览。" :
            "仍有阻断，本次整份零写入。请先处理对应行。", true);
    }

    private async Task VerifyAsync(Submission operation)
    {
        _replay = false;
        _checkingReceipt = true;
        Render();
        Notice("正在按原编号只读核对回执…");
        GradePlanReceipt? receipt;
        try
        {
            receipt = await _client.GetGradePlanReceiptAsync(_school.Id, operation.Draft, operation.RequestId, _lifetime.Token,
                _specifiedTermId is not null);
        }
        finally
        {
            _checkingReceipt = false;
            if (!_closed) Render();
        }
        if (_closed) return;
        if (receipt is not null) { await SavedAsync(receipt, operation.Preview); return; }
        _replay = true;
        Notice("服务器暂未找到回执。可继续只读核对，或用原编号重试已确认的同一份方案。", true);
        Focus(PlanReload);
    }

    private async Task SavedAsync(GradePlanReceipt receipt, GradePlanPreview submitted)
    {
        PendingOperations.Remove(PendingKey);
        _receipt = receipt;
        WorkspaceChanged = true;
        ClearDraft();
        Render();
        Notice($"整份方案已保存。新建走班 {receipt.CreatedGroups.Length} 个。");
        await LoadAsync();
        if (_closed) return;
        bool nonCurrent = _catalog?.Term is { Status: not "ACTIVE" };
        _continueTeachingKey = nonCurrent ? submitted.Notices.FirstOrDefault(row => row.Code == "GRADE_PLAN_TEACHER_PENDING")?.Key : null;
        Notice($"整份方案已保存。新建走班 {receipt.CreatedGroups.Length} 个。" + (nonCurrent
            ? "可在本学期的任课页继续分配教师。" : "可继续查看当前安排，或返回学校总览处理缺任课事项。"));
        Focus(PlanBack);
    }

    private async void Refresh(object? sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        if (Pending is { } pending) { await VerifyAsync(pending); return; }
        if (HasDraft)
        {
            var draft = Draft();
            _confirmedPreview = false;
            var current = await ReadCatalogAsync(null, null, null);
            if (_closed) return;
            if (draft?.TermId != current.TermId || _catalog?.ScopeVersion != current.ScopeVersion)
            {
                _scopeLost = true;
                Notice("学期范围已变化。草稿已保留，可返回学期管理重新核对。", true);
                return;
            }
        }
        await LoadAsync();
    });

    private async void Reset(object? sender, RoutedEventArgs args)
    {
        if (Pending is not null || _busy) return;
        ClearDraft();
        _scopeLost = false;
        Notice(null);
        await RunAsync(LoadAsync);
    }

    private void ClearDraft()
    {
        LeaveWorkspaceReview();
        _rules.Clear();
        _groups.Clear();
        _assignments.Clear();
        _preview = _previousPreview = null;
        _changedKeys.Clear();
        _confirmedPreview = false;
        _continueTeachingKey = null;
        PlanScopedReview.SetRows([]);
    }

    private void DraftChanged()
    {
        _preview = _previousPreview = null;
        _changedKeys.Clear();
        _confirmedPreview = false;
        if (!HasDraft) LeaveWorkspaceReview();
        PlanScopedReview.SetRows([]);
        Notice(null);
        BindRuleSubjects();
        BindRows();
        BindTeacherTargets();
        Render();
    }

    private void Back(object? sender, RoutedEventArgs args)
    {
        if (CanClose()) CloseRequested?.Invoke();
    }

    private void ExplainDraft()
    {
        Notice("仍有未保存的调整。可继续编辑，或清空草稿后返回。", true);
        Focus(PlanReset);
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _closed) return;
        _busy = true;
        Render();
        try { await action(); }
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error)
        {
            if (_closed) return;
            if (error.StatusCode is 401 or 403)
            {
                _catalog = null;
                ClearDraft();
                _receipt = null;
                PlanScope.Text = "学校权限待核对";
                PlanGrade.ItemsSource = PlanSubject.ItemsSource = PlanRows.ItemsSource = PlanExistingGroup.ItemsSource =
                    PlanTeacher.ItemsSource = PlanSources.ItemsSource = null;
                PendingOperations.Remove(PendingKey);
            }
            if (error.Code is "GRADE_PLAN_TERM_CHANGED" or "GRADE_PLAN_SCOPE_CHANGED" or "TERM_STRUCTURE_SCOPE_CHANGED") _scopeLost = true;
            string prefix = Pending is not null ? "调整回执待核对。 " : _receipt is not null ? "已保存本次方案。 " : "";
            Notice(prefix + error.Message, true);
            if (error.StatusCode == 401 || _inWorkspace && error.StatusCode == 403) SessionExpired?.Invoke(error);
        }
        finally { _busy = false; if (!_closed) Render(); }
    }

    private void Render()
    {
        if (_client is null) return;
        bool waiting = Pending is not null;
        bool browse = !_busy && !waiting && !_scopeLost && !OtherPlanPending && _catalog is { TermId: not null };
        bool archived = _catalog?.Term?.Status == "ARCHIVED";
        bool editable = browse && (!archived || _archiveEditable);
        bool general = editable && _teachingIssue is null;
        PlanTabs.IsEnabled = !_busy && !waiting && _catalog is { TermId: not null };
        PlanGrade.IsEnabled = PlanSubject.IsEnabled = _teachingIssue is null && (browse || !_busy && !waiting && _catalog is null);
        PlanEditArchive.IsVisible = archived && !_archiveEditable;
        PlanEditArchive.IsEnabled = browse;
        PlanClassSummaryTab.IsVisible = PlanStaffTab.IsVisible = _specifiedTermId is not null;
        PlanReviewTab.IsVisible = _specifiedTermId is not null && (!_inWorkspace || !_compact);
        RenderScopedObjects(general);
        PlanStageRules.IsEnabled = general && SelectedClasses.Any();
        PlanStageGroup.IsEnabled = general && PlanGroupOperation.SelectedItem is Option;
        PlanTeachingTarget.IsEnabled = _teachingIssue is null;
        PlanTeachingExisting.IsEnabled = !_scopeLost;
        PlanStageTeacher.IsEnabled = general && PlanTeacher.SelectedItem is not null;
        PlanOpenMixedTeaching.IsEnabled = general && _subjectId is not null;
        PlanStageMixedTeaching.IsEnabled = PlanOpenMixedTeaching.IsEnabled && PlanTeacher.SelectedItem is not null &&
            PlanMixedTeachingTargets.SelectedItems?.Count > 0;
        PlanStageRemoval.IsEnabled = editable && PlanTeachingExisting.SelectedItem is GradePlanAssignment;
        PlanReplaceGroup.IsEnabled = general && Existing is not null;
        PlanMerge.IsEnabled = general && Existing?.IsActive == true;
        PlanPreview.IsEnabled = editable && HasDraft;
        if (_specifiedTermId is not null) PlanPreview.Content = $"预览 {_rules.Count + _groups.Count + _assignments.Count} 项调整";
        PlanSave.IsEnabled = !_busy && (waiting ? _replay : editable && _confirmedPreview && _preview?.CanApply == true);
        PlanReset.IsEnabled = !_busy && !waiting && (HasDraft || _scopeLost && _teachingIssue is null);
        PlanReload.IsEnabled = !_busy;
        PlanBack.IsEnabled = MayLeave;
        PlanReload.Content = waiting ? "核对回执" : "刷新";
        PlanSave.Content = waiting ? "按原编号重试" : "保存整份方案";
        PlanBack.Content = waiting ? "返回，稍后核对" : "返回";
        BindChanges();
        RenderWorkspaceReview();
    }

    private void Notice(string? message, bool isError = false)
    {
        PlanNotice.Text = message;
        PlanNotice.IsVisible = !string.IsNullOrEmpty(message);
        PlanNotice.Classes.Set("error", isError);
    }

    private void Focus(Control target) => Dispatcher.UIThread.Post(() =>
    {
        if (!_closed && target.IsEffectivelyVisible && target.IsEffectivelyEnabled)
        {
            target.BringIntoView();
            target.Focus(NavigationMethod.Tab);
        }
    }, DispatcherPriority.Loaded);
}
