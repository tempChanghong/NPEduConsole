using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class GradeTeachingPlanWindow
{
    private Option? TeachingTarget => PlanTeachingTarget.SelectedItem as Option;

    private void BindTeacherTargets()
    {
        if (_catalog is not { } data) return;
        string? selected = TeachingTarget?.Id;
        var items = new List<Option> { new("", "表格中选中的行政班") };
        foreach (var group in data.Groups.Where(row => row.SubjectId == _subjectId))
        {
            var pending = _groups.GetValueOrDefault(group.Id);
            if (pending?.Operation == "RETIRE" || !group.IsActive && pending?.Operation != "RESTORE") continue;
            items.Add(new(group.Id, pending?.Name ?? group.Name));
        }
        items.AddRange(_groups.Values.Where(row => row.Operation == "CREATE").Select(row => new Option(row.Key, row.Name)));
        _filling = true;
        PlanTeachingTarget.ItemsSource = items;
        PlanTeachingTarget.SelectedItem = items.FirstOrDefault(row => row.Id == selected) ?? items[0];
        _filling = false;
        BindTeachingExisting();
        Render();
    }

    private void TeachingTargetChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_filling || _client is null) return;
        BindTeachingExisting();
        Render();
    }

    private void TeacherChoiceChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!_filling) Render();
    }

    private void BindTeachingExisting()
    {
        if (_catalog is not { } data) return;
        var selected = PlanTeachingExisting.SelectedItem as GradePlanAssignment;
        string? key = TeachingTarget?.Id;
        var assignments = key is { Length: > 0 } ? data.Groups.FirstOrDefault(row => row.Id == key)?.Assignments :
            SelectedClasses.Take(2).ToArray() is [var room] ?
                data.Classes.FirstOrDefault(row => row.Id == room.ClassId)?.Assignments.Where(row => row.SubjectId == RuleSubjectId).ToArray() : null;
        var items = assignments?.Where(row => row.IsActive && !_assignments.Any(edit => edit.AssignmentId == row.Id)).ToArray() ?? [];
        PlanTeachingExisting.ItemsSource = items;
        PlanTeachingExisting.SelectedItem = items.FirstOrDefault(row => row.Id == selected?.Id);
    }

    private void StageTeacher(object? sender, RoutedEventArgs args)
    {
        if (_catalog is not { } data || PlanTeacher.SelectedItem is not GradePlanTeacher teacher ||
            PlanTeacherRole.SelectedItem is not Option role) return;
        if (TeachingTarget?.Id is { Length: > 0 } group)
        {
            string? subject = _groups.GetValueOrDefault(group)?.SubjectId ?? data.Groups.FirstOrDefault(row => row.Id == group)?.SubjectId;
            if (subject is not null) AddTeacher(group, subject);
        }
        else if (RuleSubjectId is { } subject)
        {
            var targets = SelectedClasses.ToArray();
            if (targets.Length == 0)
            {
                Notice("请先在逐班对照中选择行政班，或在这里选择教学班。", true);
                return;
            }
            foreach (var target in targets) AddTeacher(target.ClassId, subject);
        }
        DraftChanged();

        void AddTeacher(string key, string subject)
        {
            _assignments.RemoveAll(row => row.WorkspaceKey == key && row.SubjectId == subject && row.AccountId == teacher.Id);
            _assignments.Add(new(key, subject, "ADD", teacher.Id, role.Id));
        }
    }

    private void StageRemoval(object? sender, RoutedEventArgs args)
    {
        if (PlanTeachingExisting.SelectedItem is not GradePlanAssignment assignment) return;
        string? key = TeachingTarget?.Id is { Length: > 0 } group ? group : SelectedClasses.FirstOrDefault()?.ClassId;
        if (key is null) return;
        _assignments.Add(new(key, assignment.SubjectId, "REMOVE", AssignmentId: assignment.Id));
        DraftChanged();
    }

    private void BindChanges()
    {
        if (_client is null) return;
        var changes = new List<GradeTeachingPlanChangeView>();
        foreach (string key in _changedKeys)
            changes.Add(new(key, key == "scope" ? "学校、学期或科目范围已有变化。" : DescribeChange(key),
                key == "scope" ? null : "定位", "locate"));
        if (_preview is { } preview)
        {
            changes.AddRange(preview.Issues.Select(issue => new GradeTeachingPlanChangeView(issue.Key, issue.Message, "定位", "locate")));
            changes.AddRange(preview.Notices.Select(issue => new GradeTeachingPlanChangeView(issue.Key, issue.Message, "定位", "locate")));
        }
        foreach (var pair in _rules)
            changes.Add(new(pair.Key, $"{TargetName(pair.Key)} · {FormatMode(pair.Value.Mode, pair.Value.Compulsory)}", "移出", "rule"));
        foreach (var group in _groups.Values)
        {
            string verb = group.Operation switch
            {
                "RETIRE" => "停用", "RESTORE" => "恢复", "RECLASSIFY" => "改科", "CREATE" => "另开",
                _ => "修改来源和设置"
            };
            var observed = _preview?.GroupChanges.FirstOrDefault(row => row.Key == group.Key);
            string sources = SourceSummary(group, observed);
            string access = observed is { Current: { } current } ? $"\n独立成员 {current.Members.Length}，邀请 {current.Invitations.Length}，任课记录 {current.AssignmentHistoryCount}" : "";
            changes.Add(new(group.Key, $"{verb} · {group.Name}（{group.Code}）{sources}{access}", "移出", "group"));
        }
        for (int index = 0; index < _assignments.Count; index++)
        {
            var edit = _assignments[index];
            string teacher = _catalog?.Teachers.FirstOrDefault(row => row.Id == edit.AccountId)?.Name ??
                _catalog?.Classes.SelectMany(row => row.Assignments).Concat(_catalog.Groups.SelectMany(row => row.Assignments))
                    .FirstOrDefault(row => row.Id == edit.AssignmentId)?.TeacherName ?? "原任课教师";
            string role = edit.Action == "REMOVE" ? "移除任课" : edit.Position == "PRIMARY" ? "安排主讲" : "安排协同";
            var observed = _preview?.TeacherChanges.FirstOrDefault(row => row.WorkspaceKey == edit.WorkspaceKey &&
                row.SubjectId == edit.SubjectId && row.Action == edit.Action && row.AssignmentId == edit.AssignmentId && row.AccountId == edit.AccountId);
            string access = observed?.Membership switch
            {
                "ADD" => "\n新增独立成员权限",
                "UPGRADE" => "\n提升独立成员权限",
                "KEEP" => observed.MembershipRole is { } retained ? $"\n保留原成员权限（{retained}）" : "\n保留原成员权限",
                "NONE" => "\n该教师在此空间没有独立成员关系",
                _ => ""
            };
            changes.Add(new(index.ToString(), $"{TargetName(edit.WorkspaceKey)} · {role} · {teacher}{access}", "移出", "teacher"));
        }
        PlanChangeCount.Text = HasDraft ? $"授课规则 {_rules.Count} 项 · 教学班 {_groups.Count} 项 · 任课 {_assignments.Count} 项" : "选定调整后，可在此统一预览。";
        PlanChangeRows.ItemsSource = changes;
    }

    private string SourceSummary(GradePlanGroupEdit group, GradePlanGroupChange? observed)
    {
        if (_catalog is not { } catalog || group.Operation == "RETIRE") return "";
        var before = observed?.Current?.SourceClassIds ?? catalog.Groups.FirstOrDefault(row => row.Id == group.GroupId)?.SourceClassIds ?? [];
        var after = observed?.After.SourceClassIds ?? group.SourceClassIds;
        string Names(IEnumerable<string> ids) => string.Join("、", ids.Select(id => catalog.Classes.FirstOrDefault(row => row.Id == id)?.Name ?? id));
        return $"\n新增：{Names(after.Except(before))}  保留：{Names(after.Intersect(before))}  移出：{Names(before.Except(after))}";
    }

    private string DescribeChange(string key)
    {
        string heading = $"当前安排已变化 · {TargetName(key)}";
        var before = _previousPreview?.GroupChanges.FirstOrDefault(row => row.Key == key)?.Current;
        var now = _preview?.GroupChanges.FirstOrDefault(row => row.Key == key)?.Current;
        if (before is null || now is null) return heading;
        var details = new List<string>();
        if (!before.SourceClassIds.SequenceEqual(now.SourceClassIds)) details.Add("来源班");
        if (!before.Assignments.SequenceEqual(now.Assignments)) details.Add("当前任课");
        if (!before.Members.SequenceEqual(now.Members)) details.Add("成员权限");
        if (!before.Invitations.SequenceEqual(now.Invitations)) details.Add("待认领邀请");
        if (before.PublicationCount != now.PublicationCount || before.ActivePublicationCount != now.ActivePublicationCount) details.Add("发布记录");
        if (before.SubjectId != now.SubjectId || before.IsActive != now.IsActive) details.Add("学科或启停状态");
        return details.Count == 0 ? heading : heading + "\n变更项：" + string.Join("、", details);
    }

    private string TargetName(string key)
    {
        if (_catalog is not { } catalog) return key;
        var parts = key.Split(':', 2);
        string name = catalog.Classes.FirstOrDefault(row => row.Id == parts[0])?.Name ??
            catalog.Groups.FirstOrDefault(row => row.Id == key)?.Name ?? _groups.GetValueOrDefault(key)?.Name ?? key;
        return parts.Length == 2 && catalog.Subjects.FirstOrDefault(row => row.Id == parts[1]) is { } subject ?
            $"{name} · {subject.Name}" : name;
    }

    private void ChangeAction(object? sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: GradeTeachingPlanChangeView row }) return;
        if (row.Kind == "locate") { FocusKey(row.Key); return; }
        if (Pending is not null || _busy) return;
        switch (row.Kind)
        {
            case "rule": _rules.Remove(row.Key); break;
            case "group":
                _groups.Remove(row.Key);
                _assignments.RemoveAll(edit => edit.WorkspaceKey == row.Key && !_catalog!.Groups.Any(group => group.Id == row.Key));
                break;
            case "teacher" when int.TryParse(row.Key, out int index) && index >= 0 && index < _assignments.Count:
                _assignments.RemoveAt(index); break;
        }
        DraftChanged();
    }

    private void ShowPlanChanges(object? sender, RoutedEventArgs args)
    {
        if (_compact) PlanTabs.SelectedIndex = 2;
        Focus(PlanChangeRows);
    }

    private void FocusKey(string key)
    {
        if (_catalog is not { } catalog) return;
        var parts = key.Split(':', 2);
        if (parts.Length == 2 && catalog.Subjects.FirstOrDefault(row => row.Id == parts[1]) is { } subject &&
            catalog.Classes.Any(row => row.Id == parts[0]))
        {
            PlanTabs.SelectedIndex = 0;
            PlanRuleSubject.SelectedItem = subject;
            if (PlanRows.Items.OfType<GradeTeachingPlanRowView>().FirstOrDefault(row => row.Key == key) is { } row)
            {
                PlanRows.SelectedItem = row;
                Dispatcher.UIThread.Post(() =>
                {
                    PlanRows.ScrollIntoView(row);
                    if (PlanRows.ContainerFromItem(row) is ListBoxItem item) Focus(item);
                    else Focus(PlanRows);
                }, DispatcherPriority.Loaded);
            }
        }
        else if (catalog.Groups.FirstOrDefault(row => row.Id == key) is { } group)
        {
            PlanTabs.SelectedIndex = 1;
            PlanExistingGroup.SelectedItem = group;
            Focus(PlanGroupOperation);
        }
        else if (_compact) PlanTabs.SelectedIndex = 2;
    }
}
