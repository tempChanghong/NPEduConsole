using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed record ClassRuleEditorLine(string Id, string Label, string Detail);

public sealed partial class ClassSubjectRulesView : UserControl
{
    private sealed record Option(string Id, string Label) { public override string ToString() => Label; }
    private sealed record Submission(ClassRuleSnapshot Baseline, ClassRuleChoice[] Choices, bool EditArchive,
        ClassRulePreview Preview, string[] Sources, bool Clear, Guid Id);

    private const string NotOffered = "NOT_OFFERED";
    private const string Following = "ADMIN_CLASS";
    private const string Walking = "COURSE_GROUP";
    private static readonly Option[] Modes = [new(Following, "随行政班"), new(Walking, "走班教学"), new(NotOffered, "本班不开设")];
    private static readonly Option[] Requirements = [new("REQUIRED", "必修"), new("OPTIONAL", "选修")];
    private static readonly ConditionalWeakTable<ConsoleClient, Dictionary<(string, string, string), Submission>> Submissions = new();

    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly string _termId = null!, _classId = null!;
    private readonly string? _initialSubjectId;
    private readonly bool _currentEntry;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, ClassRuleChoice> _draft = [];
    private readonly Dictionary<string, bool> _lastRequirement = [];
    private readonly Dictionary<string, string> _overlaps = [];
    private readonly Dictionary<string, string> _differences = [];
    private ClassRuleSnapshot? _baseline;
    private ClassRulePreview? _preview;
    private ClassRuleSourceReview? _sourceReview;
    private GradeTeachingPlanWindow? _resolver;
    private Window? _presetWindow;
    private bool _working, _checkingReceipt, _closed, _filling, _archiveEditable, _scopeLost, _recoveryRead;
    private bool _closeRequested, _discardDraft, _reviewListHint;

    internal Action<ConsoleApiException>? AccessChanged { get; set; }
    internal Action? CloseRequested { get; set; }
    internal Func<string, string, string, string, string, Task<bool>>? OpenConflict { get; init; }
    internal bool WorkspaceChanged { get; private set; }
    internal bool ReturningWithDraft => Pending is null && ChangeCount > 0 && !_scopeLost;
    private bool MayLeave => !_working || _checkingReceipt;
    private Window OwnerWindow => (Window)TopLevel.GetTopLevel(this)!;
    private Dictionary<(string, string, string), Submission> PendingOperations => Submissions.GetOrCreateValue(_client);
    private (string, string, string) PendingKey => (_school.Id, _termId, _classId);
    private Submission? Pending => PendingOperations.GetValueOrDefault(PendingKey);
    private bool CanEdit => !_working && !_scopeLost && Pending is null && _baseline is { Classroom.IsActive: true } snapshot &&
        (!_currentEntry || snapshot.Term.Status == "ACTIVE") && (snapshot.Term.Status != "ARCHIVED" || _archiveEditable);
    private int ChangeCount => _baseline?.Rows.Count(row => _draft.GetValueOrDefault(row.SubjectId) != row.Choice) ?? 0;
    private string? SelectedId => (RulesRows.SelectedItem as ClassRuleEditorLine)?.Id;

    public ClassSubjectRulesView()
    {
        InitializeComponent();
        RulesMode.ItemsSource = Modes;
        RulesRequirement.ItemsSource = Requirements;
    }

    internal ClassSubjectRulesView(ConsoleClient client, School school, string termId, string classId, bool currentEntry = false,
        string? initialSubjectId = null) : this()
    {
        _client = client;
        _school = school;
        _termId = termId;
        _classId = classId;
        _currentEntry = currentEntry;
        _initialSubjectId = initialSubjectId;
        RulesScope.Text = school.Name;
        SizeChanged += (_, _) => Arrange();
        KeyDown += RulesKeyDown;
        Render();
    }

    internal async Task StartAsync()
    {
        Arrange();
        await RunAsync(Pending is { } operation ? () => VerifyAsync(operation) : ReloadAsync);
        if (!_closed && RulesFilter.IsEffectivelyEnabled) RulesFilter.Focus();
    }

    internal void End()
    {
        if (_closed) return;
        _closed = true;
        _resolver?.CloseForScopeChange();
        _presetWindow?.Close();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    internal bool CanClose()
    {
        if (!MayLeave) return false;
        if (!ReturningWithDraft || _discardDraft) return true;
        _closeRequested = true;
        Render();
        Notice("本次调整尚未保存。继续编辑，或点击「放弃草稿并返回」。");
        RulesClose.Focus();
        return false;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_working || _closed) return;
        ResetExitPrompt();
        _working = true;
        Render();
        try { await action(); }
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error)
        {
            if (_closed) return;
            if (error.StatusCode is 401 or 403)
            {
                _scopeLost = true;
                _baseline = null;
                DropReview();
                _draft.Clear();
                PendingOperations.Remove(PendingKey);
                RefreshRows();
                AccessChanged?.Invoke(error);
            }
            Notice(error.Message, true);
        }
        finally
        {
            _working = false;
            if (!_closed) Render();
        }
    }

    private Task<ClassRuleSnapshot> ReadAsync() => _client.GetClassRulesAsync(_school.Id, _termId, _classId, _lifetime.Token);

    private async Task ReloadAsync()
    {
        var current = await ReadAsync();
        if (_closed) return;
        Merge(current);
        if (_currentEntry && current.Term.Status != "ACTIVE")
            Notice("原班所属学期已不再启用。可从学期管理核对或更正原学期。");
        else if (_overlaps.Count == 0 && _differences.Count == 0 && current.Classroom.IsActive)
            Notice(current.Rows.Length == 0 ? "本校还没有学科。请先到「学科目录」新建学科。" : "");
        else if (!current.Classroom.IsActive) Notice("行政班已停用，当前只可查看。");
        else Notice("已读取学校当前安排。请在对应科目核对关联或草稿中的变化。", true);
    }

    private void Bind(ClassRuleSnapshot current)
    {
        DropReview();
        _overlaps.Clear();
        _differences.Clear();
        _draft.Clear();
        _lastRequirement.Clear();
        foreach (var row in current.Rows) _draft[row.SubjectId] = row.Choice;
        SetBaseline(current);
    }

    private void SetBaseline(ClassRuleSnapshot current)
    {
        var initialSubject = _baseline is null ? _initialSubjectId : null;
        if (_baseline?.Term.Status != current.Term.Status) _archiveEditable = false;
        _baseline = current;
        RulesScope.Text = $"{_school.Name}  /  {current.Term.Name} · {current.Term.StatusLabel}  /  {current.Classroom.Name}（{current.Classroom.Code}）";
        RefreshRows(initialSubject);
    }

    private void Merge(ClassRuleSnapshot current)
    {
        if (_baseline is not { } previous) { Bind(current); return; }
        DropReview();
        var before = previous.Rows.ToDictionary(row => row.SubjectId);
        var missing = _draft.Keys.Except(current.Rows.Select(row => row.SubjectId)).ToArray();
        _differences.Clear();
        foreach (var key in missing)
        {
            _draft.Remove(key);
            _lastRequirement.Remove(key);
            _overlaps.Remove(key);
        }
        foreach (var row in current.Rows)
        {
            var id = row.SubjectId;
            if (!before.TryGetValue(id, out var original)) { _draft[id] = row.Choice; continue; }
            var local = _draft.GetValueOrDefault(id) ?? original.Choice;
            if (local == original.Choice && !_overlaps.ContainsKey(id)) _draft[id] = row.Choice;
            else if (local == row.Choice) _overlaps.Remove(id);
            else if (original.Choice != row.Choice)
                _overlaps[id] = $"学校由「{Describe(original.Choice)}」改为「{Describe(row.Choice)}」。我的草稿为「{Describe(local)}」。";
            if (DescribeRelationChanges(original, row) is { Length: > 0 } changes) _differences[id] = changes;
        }
        SetBaseline(current);
        if ((_overlaps.Keys.FirstOrDefault() ?? _differences.Keys.FirstOrDefault()) is { } changed)
        {
            SelectEditorSubject(changed);
        }
    }

    private void RefreshRows(string? prefer = null)
    {
        var selected = prefer ?? SelectedId;
        var filter = RulesFilter.Text?.Trim() ?? "";
        var rows = (_baseline?.Rows ?? []).Where(row => row.SubjectId == _pinnedEditorSubject || row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            row.Code.Contains(filter, StringComparison.OrdinalIgnoreCase)).Select(row =>
        {
            var choice = _draft.GetValueOrDefault(row.SubjectId) ?? row.Choice;
            var detail = Describe(choice);
            if (_overlaps.ContainsKey(row.SubjectId)) detail += " · 学校已改，待核对";
            else if (_differences.ContainsKey(row.SubjectId)) detail += " · 关联已变化";
            else if (choice != row.Choice) detail += " · 已调整";
            return new ClassRuleEditorLine(row.SubjectId, $"{row.Name} · {row.Code}", detail);
        }).ToArray();
        _filling = true;
        RulesRows.ItemsSource = rows;
        RulesRows.SelectedItem = rows.FirstOrDefault(row => row.Id == selected) ?? rows.FirstOrDefault();
        _filling = false;
        RenderSelected();
        ApplyPaneLayout();
    }

    private static string Describe(ClassRuleChoice? row) => row is null ? "目录中已无此科" :
        row.Mode == NotOffered ? "本班不开设" : $"{Modes.First(mode => mode.Id == row.Mode).Label} · {(row.Compulsory ? "必修" : "选修")}";

    private static string DescribeRelations(ClassRuleRow row)
    {
        var groups = row.Groups.Select(DescribeGroup);
        var teachers = row.Assignments.Where(assignment => assignment.IsActive).Select(DescribeTeacher);
        return $"走班：{JoinOrNone(groups)}\n有效任课：{JoinOrNone(teachers)}";
    }

    private static string DescribeGroup(ClassRuleGroup group) => group.Name + $"（{group.Code}）" + (!group.IsActive ? " · 已停用" :
        !group.IsStudentSelectable ? " · 不可自选" : "") + (!group.ScopeValid ? " · 来源归属异常" : "") +
        " · 来源 " + string.Join("、", group.Sources.Select(source => $"{source.Name}（{source.Code}）"));

    private static string DescribeTeacher(ClassRuleAssignment assignment) =>
        assignment.TeacherName + (assignment.Position == "PRIMARY" ? "（主讲）" : "（协同）");

    private static string DescribeRelationChanges(ClassRuleRow before, ClassRuleRow current)
    {
        var past = RelationLabels(before);
        var latest = RelationLabels(current);
        var removed = past.Where(item => latest.GetValueOrDefault(item.Key) != item.Value).Select(item => item.Value).ToArray();
        var added = latest.Where(item => past.GetValueOrDefault(item.Key) != item.Value).Select(item => item.Value).ToArray();
        if (added.Length == 0 && removed.Length == 0) return "";
        return $"已变化的关联\n原先：{JoinOrNone(removed)}\n现在：{JoinOrNone(added)}";

        static Dictionary<string, string> RelationLabels(ClassRuleRow row) => row.Groups
            .Select(group => new KeyValuePair<string, string>("group:" + group.Id, "走班 " + DescribeGroup(group)))
            .Concat(row.Assignments.Where(assignment => assignment.IsActive)
                .Select(assignment => new KeyValuePair<string, string>("teaching:" + assignment.Id, "任课 " + DescribeTeacher(assignment))))
            .ToDictionary();
    }

    private static string JoinOrNone(IEnumerable<string> labels) => string.Join("；", labels) is { Length: > 0 } text ? text : "无";

    private void RenderSelected()
    {
        var row = _baseline?.Rows.FirstOrDefault(item => item.SubjectId == SelectedId);
        RulesSelected.IsVisible = row is not null;
        if (row is null) { RulesSelectedName.Text = "选择一门科目"; return; }
        var choice = _draft.GetValueOrDefault(row.SubjectId) ?? row.Choice;
        RulesSelectedName.Text = $"{row.Name}（{row.Code}）";
        RulesBefore.Text = $"学校当前　{Describe(row.Choice)}";
        RulesRelations.Text = _differences.GetValueOrDefault(row.SubjectId) ?? DescribeRelations(row);
        RulesResolveDraft.IsVisible = _overlaps.TryGetValue(row.SubjectId, out var overlap);
        RulesOverlap.Text = overlap;
        RulesKeepChoice.IsEnabled = CanEdit;
        _filling = true;
        RulesMode.SelectedItem = Modes.First(mode => mode.Id == choice.Mode);
        RulesRequirement.SelectedItem = choice.Mode == NotOffered ? null : Requirements[choice.Compulsory ? 0 : 1];
        _filling = false;
        RulesMode.IsEnabled = CanEdit;
        RulesRequirement.IsVisible = choice.Mode != NotOffered;
        RulesRequirement.IsEnabled = CanEdit;
        RulesResolveDraft.IsEnabled = CanEdit;
    }

    private void Render()
    {
        var pending = Pending;
        var reviewing = _preview is not null;
        RulesEditor.IsVisible = _baseline is not null && !reviewing;
        RulesEditor.IsEnabled = pending is null;
        RulesReview.IsVisible = reviewing;
        RulesReload.IsVisible = !reviewing && pending is null && !_scopeLost;
        RulesReload.IsEnabled = !_working;
        RulesBack.IsVisible = reviewing && pending is null;
        RulesBack.IsEnabled = !_working;
        RulesPreview.IsVisible = !reviewing && pending is null && _baseline is not null;
        RulesPreview.Content = $"预览 {ChangeCount} 科调整";
        RulesPreview.IsEnabled = CanEdit && ChangeCount > 0 && _overlaps.Count == 0;
        RulesPreset.IsEnabled = CanEdit && _baseline!.Rows.Length > 0;
        RulesEditArchive.IsVisible = !_currentEntry && _baseline is { Term.Status: "ARCHIVED", Classroom.IsActive: true } && !_archiveEditable;
        RulesEditArchive.IsEnabled = !_working && pending is null;
        RulesSave.IsVisible = reviewing && pending is null;
        RulesSave.IsEnabled = CanConfirmReview;
        RulesClose.IsEnabled = MayLeave;
        RulesClose.Content = _closeRequested ? "放弃草稿并返回" : _checkingReceipt ? "返回，稍后核对" : "返回";
        RulesRecheck.IsVisible = pending is not null && !_scopeLost;
        RulesRetry.IsVisible = RulesRecheck.IsVisible && _recoveryRead;
        RulesRecheck.IsEnabled = RulesRetry.IsEnabled = !_working;
        RulesOpenConflict.IsEnabled = !_working && !_scopeLost && pending is null;
        RulesConfirmBox.IsEnabled = pending is null && !_working;
        RenderSelected();
        RenderReviewProgress();
        ApplyPaneLayout();
    }

    private void Notice(string message, bool error = false, bool reviewListHint = false)
    {
        _reviewListHint = reviewListHint;
        RulesNotice.Text = message;
        UpdateNoticeVisibility();
        RulesNotice[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(error ? "ErrorBrush" : "MutedBrush");
    }

    private void UpdateNoticeVisibility() =>
        RulesNoticeScroll.IsVisible = !string.IsNullOrEmpty(RulesNotice.Text) &&
            !(_reviewListHint && _compact && _reviewDetail && _preview is not null);

    private void FilterChanged(object? sender, TextChangedEventArgs args)
    {
        if (_client is null) return;
        _pinnedEditorSubject = null;
        RefreshRows();
    }

    private void RowChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_filling) return;
        RenderSelected();
        ApplyPaneLayout();
    }
    private void ModeChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_filling || !CanEdit || SelectedId is not { } id || RulesMode.SelectedItem is not Option mode) return;
        ChangeMode(id, mode.Id);
        RefreshRows(id);
        Render();
    }

    private void ChangeMode(string id, string mode)
    {
        var current = _draft[id];
        if (current.Mode == mode) return;
        ResetExitPrompt();
        if (current.Mode != NotOffered) _lastRequirement[id] = current.Compulsory;
        var required = _lastRequirement.GetValueOrDefault(id, _baseline!.Rows.First(row => row.SubjectId == id).Category == "CORE");
        _draft[id] = new(id, mode, mode != NotOffered && required);
    }

    private void RequirementChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_filling || !CanEdit || SelectedId is not { } id || RulesRequirement.SelectedItem is not Option requirement) return;
        ResetExitPrompt();
        _draft[id] = _draft[id] with { Compulsory = requirement.Id == "REQUIRED" };
        RefreshRows(id);
        Render();
    }

    private void KeepChoiceClicked(object? sender, RoutedEventArgs args) => ResolveOverlap(false);
    private void UseServerClicked(object? sender, RoutedEventArgs args) => ResolveOverlap(true);
    private void ResolveOverlap(bool useServer)
    {
        if (!CanEdit || SelectedId is not { } id || !_overlaps.Remove(id)) return;
        ResetExitPrompt();
        if (useServer) _draft[id] = _baseline!.Rows.First(row => row.SubjectId == id).Choice;
        RefreshRows(id);
        Render();
        RulesMode.Focus();
    }

    private void EditArchiveClicked(object? sender, RoutedEventArgs args)
    {
        if (_currentEntry || _baseline?.Term.Status != "ARCHIVED" || _working) return;
        _archiveEditable = true;
        Render();
        if (_compact && _editorDetail) FocusEditor(reveal: true);
        else RulesFilter.Focus();
    }

    private async void ReloadClicked(object? sender, RoutedEventArgs args) => await RunAsync(ReloadAsync);
    private async void PreviewClicked(object? sender, RoutedEventArgs args) => await RunAsync(PreviewAsync);
    private void CloseClicked(object? sender, RoutedEventArgs args)
    {
        _discardDraft = _closeRequested;
        try { CloseRequested?.Invoke(); }
        finally { _discardDraft = false; }
    }

    private void ResetExitPrompt()
    {
        if (!_closeRequested) return;
        _closeRequested = false;
        Notice("");
    }
    private void ConfirmationChanged(object? sender, RoutedEventArgs args) { if (_client is not null && !_filling) Render(); }

    private void BackClicked(object? sender, RoutedEventArgs args)
    {
        ResetExitPrompt();
        if (SelectedReview?.SubjectId is { } key) SelectEditorSubject(key);
        DropReview();
        Render();
        FocusEditor();
    }
}
