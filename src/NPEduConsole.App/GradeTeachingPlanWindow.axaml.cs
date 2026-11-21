using System.Runtime.CompilerServices;
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

public sealed partial class GradeTeachingPlanWindow : Window
{
    private sealed record Option(string Id, string Name) { public override string ToString() => Name; }
    private sealed record Submission(GradePlanDraft Draft, GradePlanPreview Preview, Guid RequestId);
    private static readonly ConditionalWeakTable<ConsoleClient, Dictionary<string, Submission>> PendingByClient = new();
    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, GradePlanRuleEdit> _rules = [];
    private readonly Dictionary<string, GradePlanGroupEdit> _groups = [];
    private readonly List<GradePlanTeacherEdit> _assignments = [];
    private GradePlanCatalog? _catalog;
    private GradePlanPreview? _preview, _previousPreview;
    private GradePlanReceipt? _receipt;
    private string? _gradeId, _subjectId, _openingGroup, _selectedGroupKey;
    private bool _busy, _closed, _allowClose, _filling, _scopeLost, _replay, _confirmedPreview, _compact;
    private readonly HashSet<string> _changedKeys = [];
    internal Action<ConsoleApiException>? SessionExpired { get; init; }
    internal bool WorkspaceChanged { get; private set; }

    private Dictionary<string, Submission> PendingOperations => PendingByClient.GetOrCreateValue(_client);
    private Submission? Pending => PendingOperations.GetValueOrDefault(_school.Id);
    private bool HasDraft => _rules.Count + _groups.Count + _assignments.Count > 0;
    private string? RuleSubjectId => (PlanRuleSubject.SelectedItem as GradePlanLabel)?.Id;
    private IEnumerable<GradeTeachingPlanRowView> SelectedClasses => PlanRows.SelectedItems?.OfType<GradeTeachingPlanRowView>() ?? [];
    private static string RowKey(string id, string subject) => $"{id}:{subject}";

    public GradeTeachingPlanWindow() => InitializeComponent();

    internal GradeTeachingPlanWindow(ConsoleClient client, School school, string? gradeId, string? subjectId, string? groupId) : this()
    {
        _client = client;
        _school = school;
        _openingGroup = groupId;
        _gradeId = gradeId;
        _subjectId = subjectId;
        PlanScope.Text = school.Name;
        PlanColumns.ColumnDefinitions = new("3*,2*");
        PlanRuleMode.ItemsSource = new Option[] {new("ADMIN_CLASS", "随行政班"), new("COURSE_GROUP", "走班"), new("UNCONFIGURED", "移除该科规则")};
        PlanCompulsory.ItemsSource = new Option[] {new("KEEP", "保留必修设置"), new("REQUIRED", "设为必修"), new("OPTIONAL", "设为选修")};
        PlanTeacherRole.ItemsSource = new Option[] {new("PRIMARY", "主讲"), new("CO_TEACHER", "协同")};
        PlanTeacherRole.SelectedIndex = PlanRuleMode.SelectedIndex = PlanCompulsory.SelectedIndex = 0;
        Opened += async (_, _) =>
        {
            BatchWorkspaceWindow.FitSchoolWindow(this);
            Arrange(force: true);
            await RunAsync(async () =>
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
            });
        };
        SizeChanged += (_, _) => Dispatcher.UIThread.Post(() => { if (!_closed) Arrange(); }, DispatcherPriority.Loaded);
        Closing += (_, args) =>
        {
            if (_allowClose) return;
            args.Cancel = _busy || HasDraft && Pending is null && !_scopeLost;
            if (args.Cancel && !_busy) ExplainDraft();
        };
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _lifetime.Dispose(); };
        Render();
    }

    internal void CloseForScopeChange() { _allowClose = true; Close(); }

    private void Arrange(bool force = false)
    {
        bool compact = ClientSize.Width < 900;
        if (!force && compact == _compact) return;
        _compact = compact;
        Classes.Set("compact-plan", compact);
        // Keep the column count and definitions stable through resizing. Avalonia
        // can still be using the old cell indexes when a SizeChanged event fires.
        PlanColumns.ColumnSpacing = compact ? 0 : 10;
        PlanColumns.ColumnDefinitions[0].Width = new GridLength(compact ? 1 : 3, GridUnitType.Star);
        PlanColumns.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        PlanWideChanges.IsVisible = !compact;
        PlanNarrowChanges.IsVisible = compact;
        if (compact)
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
    }

    private async Task LoadAsync()
    {
        var options = await _client.GetGradePlanAsync(_school.Id, null, null, null, _lifetime.Token);
        if (_closed) return;
        _filling = true;
        PlanGrade.ItemsSource = options.Grades;
        PlanSubject.ItemsSource = options.Subjects;
        PlanGrade.SelectedItem = options.Grades.FirstOrDefault(row => row.Id == _gradeId) ?? options.Grades.FirstOrDefault();
        PlanSubject.SelectedItem = options.Subjects.FirstOrDefault(row => row.Id == _subjectId) ?? options.Subjects.FirstOrDefault();
        _filling = false;
        _gradeId = (PlanGrade.SelectedItem as GradePlanLabel)?.Id;
        _subjectId = (PlanSubject.SelectedItem as GradePlanLabel)?.Id;
        if (options.TermId is { } term && _gradeId is { } grade && _subjectId is { } subject)
            options = await _client.GetGradePlanAsync(_school.Id, term, grade, subject, _lifetime.Token);
        if (_closed) return;
        _catalog = options;
        _scopeLost = Pending?.Draft.TermId is { } pendingTerm && pendingTerm != options.TermId;
        PlanScope.Text = options.TermName is { } termName ? $"{_school.Name} · {termName}" : $"{_school.Name} · 暂无启用学期";
        BindEditors();
        if (options.TermId is null) Notice("学校暂无启用学期，请先到学期管理核对。");
        Render();
    }

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
        await RunAsync(LoadAsync);
    }

    private GradePlanDraft? Draft() => _catalog?.TermId is { } term && _gradeId is { } grade && _subjectId is { } subject
        ? new(term, grade, subject, _rules.Values.ToArray(), _groups.Values.ToArray(), _assignments.ToArray()) : null;

    private async void Preview(object? sender, RoutedEventArgs args)
    {
        if (Draft() is not { HasEdits: true } draft) return;
        await RunAsync(async () =>
        {
            _confirmedPreview = false;
            _changedKeys.Clear();
            _previousPreview = null;
            var observed = await _client.PreviewGradePlanAsync(_school.Id, draft, _lifetime.Token);
            if (_closed) return;
            _preview = observed;
            _confirmedPreview = observed.CanApply;
            Notice(observed.CanApply ? "已预览。请核对逐班对照、学生可选项和访问变化，再保存整份方案。" :
                "本方案有未解决的冲突。请定位对应班级或教学班。", !observed.CanApply);
            BindRows();
            if (!observed.CanApply && observed.Issues.FirstOrDefault() is { } issue) FocusKey(issue.Key);
        });
    }

    private async void Save(object? sender, RoutedEventArgs args)
    {
        if (Pending is { } pending)
        {
            if (_replay) await RunAsync(() => SubmitAsync(pending));
            return;
        }
        if (!_confirmedPreview || _preview is not { CanApply: true } preview || Draft() is not { HasEdits: true } draft) return;
        var operation = new Submission(draft, preview, Guid.NewGuid());
        PendingOperations[_school.Id] = operation;
        await RunAsync(() => SubmitAsync(operation));
    }

    private async Task SubmitAsync(Submission operation)
    {
        _replay = false;
        Render();
        GradePlanResult response;
        try { response = await _client.SaveGradePlanAsync(_school.Id, operation.Draft, operation.Preview, operation.RequestId, _lifetime.Token); }
        catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or >= 500)
        {
            if (!_closed) await VerifyAsync(operation);
            return;
        }
        catch (ConsoleApiException error)
        {
            if (!_closed)
            {
                PendingOperations.Remove(_school.Id);
                _confirmedPreview = false;
                if (error.Code is "GRADE_PLAN_TERM_CHANGED" or "GRADE_PLAN_SCOPE_CHANGED") _scopeLost = true;
            }
            throw;
        }
        if (_closed) return;
        if (response.Receipt is { } receipt) { await SavedAsync(receipt); return; }
        PendingOperations.Remove(_school.Id);
        _previousPreview = response.State == "STALE" ? operation.Preview : null;
        _preview = response.Preview;
        _confirmedPreview = false;
        _changedKeys.Clear();
        foreach (string key in response.ChangedKeys ?? []) _changedKeys.Add(key);
        BindRows();
        if (_changedKeys.FirstOrDefault(key => key != "scope") is { } changed) FocusKey(changed);
        else if (_preview?.Issues.FirstOrDefault() is { } issue) FocusKey(issue.Key);
        Notice(response.State == "STALE" ? "服务器安排已变化，本次整份零写入。保留草稿，请核对变化后重新预览。" :
            "仍有阻断，本次整份零写入。请先处理对应行。", true);
    }

    private async Task VerifyAsync(Submission operation)
    {
        _replay = false;
        Render();
        var receipt = await _client.GetGradePlanReceiptAsync(_school.Id, operation.Draft, operation.RequestId, _lifetime.Token);
        if (_closed) return;
        if (receipt is not null) { await SavedAsync(receipt); return; }
        _replay = true;
        Notice("服务器暂未找到回执。可继续只读核对，或用原编号重试已确认的同一份方案。", true);
        Focus(PlanReload);
    }

    private async Task SavedAsync(GradePlanReceipt receipt)
    {
        PendingOperations.Remove(_school.Id);
        _receipt = receipt;
        WorkspaceChanged = true;
        ClearDraft();
        Notice($"整份方案已保存。新建走班 {receipt.CreatedGroups.Length} 个。可继续查看当前安排，或返回学校总览处理缺任课事项。");
        await LoadAsync();
        Focus(PlanBack);
    }

    private async void Refresh(object? sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        if (Pending is { } pending) { await VerifyAsync(pending); return; }
        if (HasDraft)
        {
            var draft = Draft();
            _confirmedPreview = false;
            var current = await _client.GetGradePlanAsync(_school.Id, null, null, null, _lifetime.Token);
            if (_closed) return;
            if (draft?.TermId != current.TermId) { _scopeLost = true; Notice("当前学期已切换。草稿已保留，可清空后重新选择。", true); return; }
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
        _rules.Clear();
        _groups.Clear();
        _assignments.Clear();
        _preview = _previousPreview = null;
        _changedKeys.Clear();
        _confirmedPreview = false;
    }

    private void DraftChanged()
    {
        _preview = _previousPreview = null;
        _changedKeys.Clear();
        _confirmedPreview = false;
        Notice(null);
        BindRuleSubjects();
        BindRows();
        BindTeacherTargets();
        Render();
    }

    private void Back(object? sender, RoutedEventArgs args)
    {
        if (HasDraft && Pending is null && !_scopeLost)
        {
            ExplainDraft();
            return;
        }
        Close();
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
                PendingOperations.Remove(_school.Id);
            }
            if (error.Code is "GRADE_PLAN_TERM_CHANGED" or "GRADE_PLAN_SCOPE_CHANGED") _scopeLost = true;
            string prefix = Pending is not null ? "调整回执待核对。 " : _receipt is not null ? "已保存本次方案。 " : "";
            Notice(prefix + error.Message, true);
            if (error.StatusCode == 401) SessionExpired?.Invoke(error);
        }
        finally { _busy = false; if (!_closed) Render(); }
    }

    private void Render()
    {
        if (_client is null) return;
        bool waiting = Pending is not null;
        bool editable = !_busy && !waiting && !_scopeLost && _catalog is { TermId: not null };
        PlanTabs.IsEnabled = !_busy && !waiting && _catalog is { TermId: not null };
        PlanGrade.IsEnabled = PlanSubject.IsEnabled = editable || !_busy && !waiting && _catalog is null;
        PlanStageRules.IsEnabled = editable && SelectedClasses.Any();
        PlanStageGroup.IsEnabled = editable && PlanGroupOperation.SelectedItem is Option;
        PlanStageTeacher.IsEnabled = editable && PlanTeacher.SelectedItem is not null;
        PlanStageRemoval.IsEnabled = editable && PlanTeachingExisting.ItemCount > 0;
        PlanReplaceGroup.IsEnabled = editable && Existing is not null;
        PlanMerge.IsEnabled = editable && Existing?.IsActive == true;
        PlanPreview.IsEnabled = editable && HasDraft;
        PlanSave.IsEnabled = !_busy && (waiting ? _replay : editable && _confirmedPreview && _preview?.CanApply == true);
        PlanReset.IsEnabled = !_busy && !waiting && (HasDraft || _scopeLost);
        PlanReload.IsEnabled = PlanBack.IsEnabled = !_busy;
        PlanReload.Content = waiting ? "核对回执" : "刷新";
        PlanSave.Content = waiting ? "按原编号重试" : "保存整份方案";
        PlanBack.Content = waiting ? "关闭，稍后核对" : "返回";
        BindChanges();
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
