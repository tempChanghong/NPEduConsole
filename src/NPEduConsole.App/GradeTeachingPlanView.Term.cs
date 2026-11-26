using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed record TermTeachingSubjectLine(string Id, string Name, string Detail);

public sealed partial class GradeTeachingPlanView
{
    private CancellationTokenSource? _staffRead;
    private StaffResponsibilitiesWindow? _staffEditor;
    private StaffReviewWindow? _staffReview;
    private ClassSubjectRulesWindow? _classRulesEditor;
    private sealed record LinkedClassRule(string ClassId, string SubjectId, string Action);
    private LinkedClassRule? _linkedClassRule;
    private sealed record LinkedTeacher(string? GradeId, string? SubjectId, string? ClassId, string? GroupId, string AccountId);
    private LinkedTeacher? _linkedTeacher;
    private bool _openedFromStaffReview;
    private StaffSnapshot? _gradeStaff, _classStaff;
    private string? _staffReadError;

    private string? StaffClassId => (PlanStaffClass.SelectedItem as Option)?.Id;
    private Option? SummaryClass => PlanSummaryClass.SelectedItem as Option;

    private void BindTermObjects()
    {
        if (_specifiedTermId is null || _catalog is not { } catalog) return;
        var classes = catalog.Classes.Select(row => new Option(row.Id, $"{row.Name}（{row.Code}）")).ToArray();
        string? summary = SummaryClass?.Id ?? _initialClassId;
        string? staff = StaffClassId ?? _initialClassId;
        _filling = true;
        PlanSummaryClass.ItemsSource = PlanStaffClass.ItemsSource = classes;
        PlanSummaryClass.SelectedItem = classes.FirstOrDefault(row => row.Id == summary) ?? classes.FirstOrDefault();
        PlanStaffClass.SelectedItem = classes.FirstOrDefault(row => row.Id == staff) ?? classes.FirstOrDefault();
        _filling = false;
        if (_initialClassId is { } classId)
        {
            _initialClassId = null;
            if (_subjectId is { } subject && catalog.Classes.Any(row => row.Id == classId))
                FocusKey(RowKey(classId, subject));
        }
        ShowClassSubjects();
        if (PlanTabs.SelectedItem == PlanStaffTab) _ = ReadRolesAsync();
    }

    private void SummaryClassChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!_filling && _client is not null) ShowClassSubjects();
    }

    private void ClassSubjectChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!_filling && _client is not null) Render();
    }

    private void ShowClassSubjects()
    {
        if (_catalog is not { } catalog) return;
        var room = catalog.Classes.FirstOrDefault(row => row.Id == SummaryClass?.Id);
        string? selected = (PlanClassSubjects.SelectedItem as TermTeachingSubjectLine)?.Id;
        var rows = room is null ? [] : catalog.Subjects.Select(subject =>
        {
            var rule = room.Rules.FirstOrDefault(row => row.SubjectId == subject.Id);
            var teachers = room.Assignments.Where(row => row.SubjectId == subject.Id).Select(row => row.ToString());
            var groups = catalog.Groups.Where(row => row.IsActive && row.SubjectId == subject.Id && row.SourceClassIds.Contains(room.Id));
            string groupText = string.Join("、", groups.Select(row => $"{row.Name}（{row.Code}）" +
                (row.Assignments.Length > 0 ? " · " + string.Join("、", row.Assignments.Select(teacher => teacher.ToString())) : "")));
            string teacherText = string.Join("、", teachers);
            string mode = FormatMode(rule?.Mode ?? "UNCONFIGURED", rule?.Compulsory == true);
            return new TermTeachingSubjectLine(subject.Id, $"{subject.Name}（{subject.Code}）",
                mode + (teacherText.Length == 0 ? "" : $"\n随班任课  {teacherText}") + (groupText.Length == 0 ? "" : $"\n走班  {groupText}"));
        }).ToArray();
        PlanClassSubjects.ItemsSource = rows;
        PlanClassSubjects.SelectedItem = rows.FirstOrDefault(row => row.Id == selected) ?? rows.FirstOrDefault();
    }

    private async void OpenSubjectFromClassClicked(object? sender, RoutedEventArgs args)
    {
        if (SummaryClass is not { } room || PlanClassSubjects.SelectedItem is not TermTeachingSubjectLine line || HasDraft || Pending is not null) return;
        _subjectId = line.Id;
        _initialClassId = room.Id;
        await RunAsync(LoadAsync);
    }

    private void TermPlanTabChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_closed || args.Source != PlanTabs) return;
        if (_inWorkspace && PlanTabs.SelectedItem is { } selected && selected != PlanReviewTab) _workspaceEditTab = selected;
        if (_specifiedTermId is null) return;
        UpdateReviewLayout();
        if (PlanTabs.SelectedItem == PlanStaffTab) _ = ReadRolesAsync();
    }

    private void UpdateReviewLayout()
    {
        if (_specifiedTermId is null) return;
        bool sideBySide = !_compact && PlanTabs.SelectedItem != PlanReviewTab;
        PlanWideChanges.IsVisible = sideBySide;
        PlanColumns.ColumnDefinitions[1].Width = sideBySide ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        PlanColumns.ColumnSpacing = sideBySide ? 10 : 0;
    }

    private void StaffClassChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!_filling && !_closed && _specifiedTermId is not null) _ = ReadRolesAsync();
    }

    private void CancelStaffRead()
    {
        var request = _staffRead;
        _staffRead = null;
        request?.Cancel();
    }

    private async Task ReadRolesAsync()
    {
        if (_catalog is not { Term: { } term } catalog || _gradeId is not { } gradeId || _closed) return;
        CancelStaffRead();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _staffRead = request;
        _gradeStaff = _classStaff = null;
        _staffReadError = null;
        PlanGradeStaffHeading.Text = $"{catalog.Grades.FirstOrDefault(row => row.Id == gradeId)?.Name} · 年级组长与副组长";
        PlanGradeStaffText.Text = PlanClassStaffText.Text = "正在读取岗位…";
        PlanStaffPolicy.Text = null;
        Render();
        try
        {
            var grade = _client.GetStaffResponsibilitiesAsync(_school.Id, term.Id, "grades", gradeId, request.Token, specifiedTerm: true);
            var room = StaffClassId is { } classId
                ? _client.GetStaffResponsibilitiesAsync(_school.Id, term.Id, "classes", classId, request.Token, specifiedTerm: true)
                : null;
            if (room is not null) await Task.WhenAll(grade, room);
            var gradeRoles = await grade;
            var classRoles = room is null ? null : await room;
            if (_closed || _staffRead != request) return;
            if (gradeRoles.ScopeVersion != catalog.ScopeVersion || classRoles is not null && classRoles.ScopeVersion != catalog.ScopeVersion)
                throw new ConsoleApiException(409, "所选学期已有变化，请刷新学期后核对岗位。", "TERM_STRUCTURE_SCOPE_CHANGED");
            _gradeStaff = gradeRoles;
            _classStaff = classRoles;
            PlanGradeStaffText.Text = DescribeRoles(gradeRoles);
            PlanClassStaffText.Text = classRoles is null ? "请选择一个启用行政班。" : DescribeRoles(classRoles);
            if (gradeRoles.Policies is { } policy)
                PlanStaffPolicy.Text = $"全校联动要求（只读）　班主任需任课 {Flag(policy.HomeroomMustTeach)} · " +
                    $"组长需任课 {Flag(policy.GradeLeaderMustTeach)} · 组长须兼任班主任 {Flag(policy.GradeLeaderMustBeHomeroom)}";
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (ConsoleApiException error)
        {
            if (_closed || _staffRead != request) return;
            _staffReadError = error.Message;
            PlanGradeStaffText.Text = PlanClassStaffText.Text = error.Message;
            if (error.StatusCode == 401) SessionExpired?.Invoke(error);
        }
        finally
        {
            if (_staffRead == request) _staffRead = null;
            if (!_closed) Render();
        }
    }

    private static string DescribeRoles(StaffSnapshot snapshot) => snapshot.Assignments.Count == 0 ? "尚未设置" :
        string.Join("\n", snapshot.Assignments.Select(row =>
            $"{(snapshot.IsClass ? row.Position == snapshot.PrimaryRole ? "班主任" : "协同班主任" :
                row.Position == snapshot.PrimaryRole ? "年级组长" : "副组长")}　{row.Teacher}"));

    private static string Flag(bool value) => value ? "是" : "否";

    private async void OpenGradeStaffClicked(object? sender, RoutedEventArgs args) => await OpenStaffAsync(_gradeStaff, PlanOpenGradeStaff);
    private async void OpenClassStaffClicked(object? sender, RoutedEventArgs args) => await OpenStaffAsync(_classStaff, PlanOpenClassStaff);

    private async void OpenPlanStaffReviewClicked(object? sender, RoutedEventArgs args)
    {
        if (_staffReview is not null || _catalog?.Term is not { } term || HasDraft || Pending is not null) return;
        var window = new StaffReviewWindow(_client, _school, term.Id, grade: _gradeId, classroom: StaffClassId)
        { SessionExpired = SessionExpired };
        _staffReview = window;
        try { await window.ShowDialog(OwnerWindow); }
        finally { _staffReview = null; }
        if (_closed) return;
        WorkspaceChanged |= window.WorkspaceChanged;
        if (window.WorkspaceChanged) await RunAsync(LoadAsync);
        Focus(PlanOpenStaffReview);
    }

    private void OpenLinkedTeacher()
    {
        if (_linkedTeacher is not { } target || _catalog is not { } catalog || HasDraft || Pending is not null) return;
        _linkedTeacher = null;
        var teacher = catalog.Teachers.FirstOrDefault(row => row.Id == target.AccountId);
        var room = target.ClassId is { } classId ? catalog.Classes.FirstOrDefault(row => row.Id == classId) : null;
        var group = target.GroupId is { } groupId ? catalog.Groups.FirstOrDefault(row => row.Id == groupId) : null;
        if (teacher is null || _gradeId != target.GradeId || _subjectId != target.SubjectId ||
            (room is null || !room.Rules.Any(row => row.SubjectId == target.SubjectId && row.Mode == "ADMIN_CLASS")) &&
            (group is not { IsActive: true } || group.SubjectId != target.SubjectId))
        {
            Notice("原教师、教学空间或科目已变化。请返回师资核对并重新选择。", true);
            return;
        }
        if (room is not null && target.SubjectId is { } subject) FocusKey(RowKey(room.Id, subject));
        PlanTeachingTarget.SelectedItem = PlanTeachingTarget.Items.OfType<Option>().FirstOrDefault(row => row.Id == (group?.Id ?? ""));
        PlanTeacher.SelectedItem = teacher;
        PlanTabs.SelectedIndex = 2;
        Notice($"正在为 {teacher} 核对任课。请选择主讲或协同，加入方案后预览保存。");
        Focus(catalog.Term?.Status == "ARCHIVED" ? PlanEditArchive : PlanTeacherRole);
    }

    private async Task OpenStaffAsync(StaffSnapshot? snapshot, Button source)
    {
        if (_staffEditor is not null || !source.IsEnabled || snapshot is null || _catalog is not { Term: { } term } catalog) return;
        var editor = new StaffResponsibilitiesWindow(_client, _school, term.Id, snapshot.Kind, snapshot.TargetId,
            null, term, catalog.ActiveTerm, catalog.ScopeVersion) { SessionExpired = SessionExpired };
        _staffEditor = editor;
        Render();
        try { await editor.ShowDialog(OwnerWindow); }
        finally { _staffEditor = null; }
        if (_closed) return;
        WorkspaceChanged |= editor.RolesChanged;
        await ReadRolesAsync();
        Focus(source);
    }

    private void EditArchiveClicked(object? sender, RoutedEventArgs args)
    {
        if (_catalog?.Term?.Status != "ARCHIVED") return;
        _archiveEditable = true;
        Notice("正在更正归档学期的安排。每次保存前会核对所选学期、对象与已有关系。");
        Render();
        Focus(PlanTabs);
    }

    private void RenderScopedObjects(bool editable)
    {
        if (_specifiedTermId is null) return;
        PlanOpenSubjectFromClass.IsEnabled = !_busy && !_scopeLost && !HasDraft && Pending is null &&
            SummaryClass is not null && PlanClassSubjects.SelectedItem is TermTeachingSubjectLine;
        PlanEditClassRules.IsEnabled = !_busy && !_scopeLost && !HasDraft && Pending is null &&
            _classRulesEditor is null && SummaryClass is not null && _catalog?.Term is not null;
        PlanOpenGradeStaff.IsEnabled = editable && _staffEditor is null && _staffReadError is null && _gradeStaff is not null;
        PlanOpenClassStaff.IsEnabled = editable && _staffEditor is null && _staffReadError is null && _classStaff is not null;
        PlanOpenStaffReview.IsVisible = !_openedFromStaffReview;
        PlanOpenStaffReview.IsEnabled = !_busy && !HasDraft && Pending is null && _catalog?.Term is not null;
        PlanContinueTeaching.IsVisible = _continueTeachingKey is not null && !HasDraft;
        PlanContinueTeaching.IsEnabled = editable;
        PlanRuleMode.IsEnabled = PlanCompulsory.IsEnabled = editable;
        PlanTeacher.IsEnabled = PlanTeacherRole.IsEnabled = editable;
    }

    private async void EditClassRulesClicked(object? sender, RoutedEventArgs args)
    {
        if (!PlanEditClassRules.IsEnabled || SummaryClass is not { } room || _catalog?.Term is not { } term) return;
        var window = new ClassSubjectRulesWindow(_client, _school, term.Id, room.Id) { AccessChanged = SessionExpired };
        _classRulesEditor = window;
        Render();
        try { await window.ShowDialog(OwnerWindow); }
        finally { _classRulesEditor = null; }
        if (_closed) return;
        WorkspaceChanged |= window.WorkspaceChanged;
        await RunAsync(LoadAsync);
        Focus(PlanEditClassRules);
    }

    private void OpenLinkedClassRule()
    {
        if (_linkedClassRule is not { } target || _catalog is not { } catalog || HasDraft || Pending is not null) return;
        _linkedClassRule = null;
        if (_subjectId != target.SubjectId || catalog.Classes.All(room => room.Id != target.ClassId))
        {
            Notice("原行政班或科目已变化，请返回整班规则重新核对。", true);
            return;
        }
        FocusKey(RowKey(target.ClassId, target.SubjectId));
        if (target.Action == "TEACHING")
        {
            PlanTabs.SelectedIndex = 2;
            PlanTeachingTarget.SelectedIndex = 0;
            Focus(catalog.Term?.Status == "ARCHIVED" ? PlanEditArchive : PlanTeachingTarget);
        }
        else
        {
            PlanTabs.SelectedIndex = 1;
            var source = PlanSources.Items.OfType<Option>().FirstOrDefault(row => row.Id == target.ClassId);
            if (source is not null && PlanSources.SelectedItems is { } selected && !selected.Contains(source)) selected.Add(source);
            PlanSetSourceRules.IsChecked = true;
            Focus(catalog.Term?.Status == "ARCHIVED" ? PlanEditArchive : PlanGroupOperation);
        }
    }

    private void ContinueTeachingClicked(object? sender, RoutedEventArgs args)
    {
        if (!PlanContinueTeaching.IsEnabled || _continueTeachingKey is not { } key) return;
        FocusKey(key);
        PlanTabs.SelectedIndex = 2;
        PlanTeachingTarget.SelectedIndex = 0;
        Focus(PlanTeacher);
    }

    private void ShowScopedReview(string? focusKey)
    {
        if (_preview is not { } preview) return;
        var oldRooms = _previousPreview?.Rows.ToDictionary(row => row.Key) ?? [];
        var oldGroups = _previousPreview?.GroupChanges.ToDictionary(row => row.Key) ?? [];
        var lines = new List<TermTeachingReviewLine>();
        bool onlyTeaching = _rules.Count == 0 && _groups.Count == 0 && preview.TeacherChanges.Length > 0;
        foreach (var row in preview.Rows)
        {
            if (onlyTeaching && !preview.Issues.Any(issue => issue.Key == row.Key) && !preview.Notices.Any(issue => issue.Key == row.Key)) continue;
            string before = State(row.Current);
            if (_changedKeys.Contains(row.Key) && oldRooms.TryGetValue(row.Key, out var old))
                before = $"上次核对\n{State(old.Current)}\n\n服务器当前\n{before}";
            lines.Add(new(row.Key, $"{row.ClassName} · {row.SubjectName}", before,
                State(row.After), Issue(row.Key)));
        }
        foreach (var group in preview.GroupChanges)
        {
            string before = GroupState(group.Current);
            if (_changedKeys.Contains(group.Key) && oldGroups.TryGetValue(group.Key, out var old))
                before = $"上次核对\n{GroupState(old.Current)}\n\n服务器当前\n{before}";
            var stagedTeaching = preview.Rows.SelectMany(row => row.After.Groups).FirstOrDefault(item => item.Id == group.Key)?.Assignments;
            var after = stagedTeaching is null ? group.After : group.After with { Assignments = stagedTeaching };
            lines.Add(new(group.Key, $"走班 · {group.After.Name}（{group.After.Code}）", before, GroupState(after), Issue(group.Key)));
        }
        foreach (var target in preview.TeacherChanges.GroupBy(change => change.Key))
        {
            string before = DescribeTeachers(target, false), after = DescribeTeachers(target, true);
            if (_changedKeys.Contains(target.Key) && _previousPreview is { } previous)
            {
                string original = DescribeTeachers(previous.TeacherChanges.Where(change => change.Key == target.Key), false);
                if (original.Length > 0) before = $"上次核对\n{original}\n\n服务器当前\n{before}";
            }
            int existing = lines.FindIndex(line => line.Key == target.Key);
            if (existing >= 0)
            {
                var line = lines[existing];
                lines[existing] = line with {Before = line.Before + "\n\n" + before, After = line.After + "\n\n" + after};
            }
            else
            {
                string? subject = _catalog?.Subjects.FirstOrDefault(row => row.Id == target.First().SubjectId)?.Name;
                string type = target.Key == target.First().WorkspaceKey ? $"走班 · {TargetName(target.Key)} · {subject}" :
                    $"随班 · {TargetName(target.Key)}";
                lines.Add(new(target.Key, type, before, after, Issue(target.Key)));
            }
        }
        foreach (string key in _changedKeys.Concat(preview.Issues.Select(issue => issue.Key)).Distinct().Except(lines.Select(row => row.Key)))
            lines.Add(new(key, TargetName(key), "原预览", "请重新核对", Issue(key)));
        bool compactWorkspace = _inWorkspace && _compact;
        if (compactWorkspace)
            lines = lines.OrderByDescending(line => line.Key == focusKey).ThenByDescending(line => line.HasIssue).ToList();
        PlanScopedReview.SetRows(lines, focusKey ?? PlanScopedReview.SelectedKey);
        UpdateReviewActions();
        if (compactWorkspace)
        {
            _workspaceReview = true;
            _workspaceDraftList = false;
            ArrangeWorkspaceReview();
        }
        else PlanTabs.SelectedItem = PlanReviewTab;
        if (focusKey is not null) Dispatcher.UIThread.Post(() =>
        {
            if (!_closed) PlanScopedReview.FocusIssue(focusKey, showFirstRowHeading: compactWorkspace);
        }, DispatcherPriority.Loaded);
        return;

        string? Issue(string key)
        {
            var notices = preview.Issues.Concat(preview.Notices).Where(issue => issue.Key == key).Select(issue => issue.Message);
            if (_changedKeys.Contains(key)) notices = notices.Prepend("服务器已修改此项，本次未覆盖。");
            string message = string.Join("\n", notices);
            return message.Length == 0 ? null : message;
        }
    }

    private void UpdateReviewActions()
    {
        bool simpleTeachingPreview = _preview is { CanApply: true } preview && preview.TeacherChanges.Length > 0 &&
            preview.Notices.Length == 0 && _changedKeys.Count == 0 && _rules.Count == 0 && _groups.Count == 0;
        PlanReviewActions.IsVisible = !_compact || !_inWorkspace && !simpleTeachingPreview;
    }

    private static string DescribeTeachers(IEnumerable<GradePlanTeacherChange> changes, bool after) =>
        string.Join("\n", changes.Select(change => $"{change.TeacherName}" +
            (change.TeacherIdentity is { } identity ? $"（{identity}）" : "") + $" · {TeachingImpact(change, after)}" +
            (after && change.Unchanged ? "（保持原安排）" : "")));

    private string GroupState(GradePlanCourse? group)
    {
        if (group is null) return "未建立";
        var classes = _catalog?.Classes ?? [];
        string sources = string.Join("、", group.SourceClassIds.Select(key => classes.FirstOrDefault(room => room.Id == key)?.Name ?? key));
        string teachers = string.Join("、", group.Assignments.Select(row => row.ToString()));
        return $"{(group.IsActive ? "启用" : "停用")} · {(group.IsStudentSelectable ? "可自选" : "不可自选")}\n" +
            $"来源  {(sources.Length == 0 ? "无" : sources)}\n任课  {(teachers.Length == 0 ? "无" : teachers)}";
    }

    private void LocateScopedReviewClicked(object? sender, RoutedEventArgs args)
    {
        if (PlanScopedReview.SelectedKey is { } key) FocusKey(key);
    }

    private void ReturnToPlanRowsClicked(object? sender, RoutedEventArgs args)
    {
        LeaveWorkspaceReview();
        PlanTabs.SelectedIndex = 0;
        Render();
        Focus(PlanRows);
    }
}
