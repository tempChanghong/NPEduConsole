using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed record TermTeachingSubjectLine(string Id, string Name, string Detail);

public sealed partial class GradeTeachingPlanWindow
{
    private CancellationTokenSource? _staffRead;
    private StaffResponsibilitiesWindow? _staffEditor;
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
        if (_specifiedTermId is null || _closed || args.Source != PlanTabs) return;
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

    private async Task OpenStaffAsync(StaffSnapshot? snapshot, Button source)
    {
        if (_staffEditor is not null || !source.IsEnabled || snapshot is null || _catalog is not { Term: { } term } catalog) return;
        var editor = new StaffResponsibilitiesWindow(_client, _school, term.Id, snapshot.Kind, snapshot.TargetId,
            null, term, catalog.ActiveTerm, catalog.ScopeVersion) { SessionExpired = SessionExpired };
        _staffEditor = editor;
        Render();
        try { await editor.ShowDialog(this); }
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
        PlanOpenGradeStaff.IsEnabled = editable && _staffEditor is null && _staffReadError is null && _gradeStaff is not null;
        PlanOpenClassStaff.IsEnabled = editable && _staffEditor is null && _staffReadError is null && _classStaff is not null;
        PlanContinueTeaching.IsVisible = _continueTeachingKey is not null && !HasDraft;
        PlanContinueTeaching.IsEnabled = editable;
        PlanRuleMode.IsEnabled = PlanCompulsory.IsEnabled = editable;
        PlanTeacher.IsEnabled = PlanTeacherRole.IsEnabled = editable;
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
        foreach (var row in preview.Rows)
        {
            string before = State(row.Current);
            if (_changedKeys.Contains(row.Key) && oldRooms.TryGetValue(row.Key, out var old))
                before = $"上次核对\n{State(old.Current)}{RemovalFacts(_previousPreview!, row.Key)}\n\n服务器当前\n{before}";
            lines.Add(new(row.Key, $"{row.ClassName} · {row.SubjectName}", before,
                State(row.After) + RemovalFacts(preview, row.Key), Issue(row.Key)));
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
        foreach (string key in _changedKeys.Concat(preview.Issues.Select(issue => issue.Key)).Distinct().Except(lines.Select(row => row.Key)))
            lines.Add(new(key, TargetName(key), "原预览", "请重新核对", Issue(key)));
        PlanScopedReview.SetRows(lines, focusKey ?? PlanScopedReview.SelectedKey);
        PlanTabs.SelectedItem = PlanReviewTab;
        if (focusKey is not null) Dispatcher.UIThread.Post(() => { if (!_closed) PlanScopedReview.FocusIssue(focusKey); }, DispatcherPriority.Loaded);
        return;

        string? Issue(string key)
        {
            var notices = preview.Issues.Concat(preview.Notices).Where(issue => issue.Key == key).Select(issue => issue.Message);
            if (_changedKeys.Contains(key)) notices = notices.Prepend("服务器已修改此项，本次未覆盖。");
            string message = string.Join("\n", notices);
            return message.Length == 0 ? null : message;
        }
    }

    private static string RemovalFacts(GradePlanPreview preview, string key) =>
        string.Concat(preview.TeacherChanges.Where(row => row.Key == key && row.Action == "REMOVE").Select(row =>
            $"\n移除 {row.TeacherName} 任课 · " + (row.MembershipRole is { } role ? $"保留独立成员 {role}" : "无独立成员关系")));

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
        PlanTabs.SelectedIndex = 0;
        Focus(PlanRows);
    }
}
