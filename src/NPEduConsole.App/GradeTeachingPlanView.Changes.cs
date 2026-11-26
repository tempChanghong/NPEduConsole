using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class GradeTeachingPlanView
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
            items.Add(new(group.Id, $"{pending?.Name ?? group.Name}（{pending?.Code ?? group.Code}）"));
        }
        items.AddRange(_groups.Values.Where(row => row.Operation == "CREATE").Select(row => new Option(row.Key, row.Name)));
        _filling = true;
        PlanTeachingTarget.ItemsSource = items;
        PlanTeachingTarget.SelectedItem = items.FirstOrDefault(row => row.Id == selected) ?? items[0];
        _filling = false;
        BindTeachingExisting();
        BindMixedTeachingTargets();
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
        string? key = _teachingIssue?.Group ?? TeachingTarget?.Id;
        string? roomId = _teachingIssue?.Room ?? (SelectedClasses.Take(2).ToArray() is [var room] ? room.ClassId : null);
        string? subjectId = _teachingIssue?.Subject ?? RuleSubjectId;
        var assignments = key is { Length: > 0 } ? data.Groups.FirstOrDefault(row => row.Id == key)?.Assignments :
            data.Classes.FirstOrDefault(row => row.Id == roomId)?.Assignments.Where(row => row.SubjectId == subjectId).ToArray();
        var items = assignments?.Where(row => row.IsActive && !_assignments.Any(edit => edit.AssignmentId == row.Id) &&
            (_teachingIssue?.Account is not { } account || row.AccountId == account) &&
            (_teachingIssue?.Code != "MULTIPLE_PRIMARY_TEACHERS" || row.Position == "PRIMARY")).ToArray() ?? [];
        PlanTeachingExisting.ItemsSource = items;
        PlanTeachingExisting.SelectedItem = items.FirstOrDefault(row => row.Id == selected?.Id);
    }

    private void OpenTeachingIssue()
    {
        if (_teachingIssue is not { Subject: { } subject } issue) return;
        if (issue.Room is { } room) FocusKey(RowKey(room, subject));
        var target = PlanTeachingTarget.Items.OfType<Option>().FirstOrDefault(row => row.Id == (issue.Group ?? ""));
        PlanTabs.SelectedIndex = 2;
        PlanTeachingTarget.SelectedItem = target;
        BindTeachingExisting();
        var assignments = PlanTeachingExisting.Items.OfType<GradePlanAssignment>().ToArray();
        var original = issue.Account is { } teacher ? assignments.FirstOrDefault(row => row.AccountId == teacher) : null;
        bool stillApplies = issue.Code switch
        {
            "MULTIPLE_PRIMARY_TEACHERS" => assignments.Count(row => row.Position == "PRIMARY") > 1,
            "DISABLED_TEACHER_ASSIGNED" => original?.LocalDisabled == true,
            "TEACHING_ASSIGNMENT_SUBJECT_CONFLICT" => original is not null && issue.Room is { } classId &&
                _catalog?.Classes.FirstOrDefault(row => row.Id == classId) is { } current &&
                !current.Rules.Any(row => row.SubjectId == subject && row.Mode == "ADMIN_CLASS"),
            _ => false
        };
        string name = TargetName(issue.Group ?? RowKey(issue.Room ?? "", subject));
        if (target is null || !stillApplies)
        {
            PlanTeachingExisting.SelectedItem = null;
            _scopeLost = true;
            Notice($"{name}的原任课待办已变化或不再成立，请返回总览刷新。", true);
            return;
        }
        if (original is not null) PlanTeachingExisting.SelectedItem = original;
        Notice(OtherPlanPending ? $"{name}。本学期另有一份方案待核对，请从本学期教学安排打开原回执。" :
            $"正在核对原待办：{name}。请确认实际任课，再将需要移除的关系加入方案。");
        Focus(PlanTeachingExisting);
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

        void AddTeacher(string key, string subject) => PutTeachingEdit(key, subject, teacher.Id, role.Id);
    }

    private void PutTeachingEdit(string space, string subject, string account, string role)
    {
        _assignments.RemoveAll(row => row.WorkspaceKey == space && row.SubjectId == subject && row.AccountId == account);
        _assignments.Add(new(space, subject, "ADD", account, role));
    }

    private void OpenMixedTeaching(object? sender, RoutedEventArgs args)
    {
        PlanMixedTeaching.IsVisible = !PlanMixedTeaching.IsVisible;
        if (!PlanMixedTeaching.IsVisible) return;
        BindMixedTeachingTargets();
        Render();
        Focus(PlanMixedTeachingTargets);
    }

    private void BindMixedTeachingTargets()
    {
        if (!PlanMixedTeaching.IsVisible || _catalog is not { } data || _subjectId is not { } subject) return;
        var selected = PlanMixedTeachingTargets.SelectedItems?.OfType<Option>().Select(row => row.Id).ToHashSet() ?? [];
        var rooms = data.Classes.Where(room => (_rules.GetValueOrDefault(RowKey(room.Id, subject))?.Mode ??
            room.Rules.FirstOrDefault(rule => rule.SubjectId == subject)?.Mode) == "ADMIN_CLASS")
            .Select(room => new Option(room.Id, $"随班 · {room.Name}（{room.Code}）"));
        var groups = data.Groups.Where(group => group.IsActive && group.SubjectId == subject &&
            _groups.GetValueOrDefault(group.Id)?.Operation != "RETIRE")
            .Select(group => new Option(group.Id, $"走班 · {group.Name}（{group.Code}）"));
        var targets = rooms.Concat(groups).ToArray();
        PlanMixedTeachingTargets.ItemsSource = targets;
        if (PlanMixedTeachingTargets.SelectedItems is { } choices)
            foreach (var target in targets.Where(target => selected.Contains(target.Id))) choices.Add(target);
        PlanMixedTeachingInfo.Text = $"{PlanGrade.SelectedItem} · {PlanSubject.SelectedItem}。选定班级后，将上方教师和任课角色加入同一份方案。";
    }

    private void MixedTeachingChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!_filling && _client is not null) Render();
    }

    private void StageMixedTeaching(object? sender, RoutedEventArgs args)
    {
        if (!PlanStageMixedTeaching.IsEnabled || _subjectId is not { } subject || PlanTeacher.SelectedItem is not GradePlanTeacher teacher ||
            PlanTeacherRole.SelectedItem is not Option role || PlanMixedTeachingTargets.SelectedItems is not { } selected) return;
        foreach (var target in selected.OfType<Option>()) PutTeachingEdit(target.Id, subject, teacher.Id, role.Id);
        selected.Clear();
        PlanMixedTeaching.IsVisible = false;
        DraftChanged();
        Focus(PlanPreview);
    }

    private void StageRemoval(object? sender, RoutedEventArgs args)
    {
        if (PlanTeachingExisting.SelectedItem is not GradePlanAssignment assignment) return;
        string? key = _teachingIssue?.Group ?? _teachingIssue?.Room ??
            (TeachingTarget?.Id is { Length: > 0 } group ? group : SelectedClasses.FirstOrDefault()?.ClassId);
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
            if (observed?.MembershipAfterRole is not null || observed?.Unchanged == true)
                access = $"\n{TeachingImpact(observed!, false)} → {TeachingImpact(observed!, true)}";
            changes.Add(new(index.ToString(), $"{TargetName(edit.WorkspaceKey)} · {role} · {teacher}{access}", "移出", "teacher"));
        }
        PlanChangeCount.Text = HasDraft ? $"授课规则 {_rules.Count} 项 · 教学班 {_groups.Count} 项 · 任课 {_assignments.Count} 项" : "选定调整后，可在此统一预览。";
        PlanChangeRows.ItemsSource = changes;
    }

    private static string TeachingImpact(GradePlanTeacherChange change, bool after)
    {
        string? position = after ? change.Action == "REMOVE" ? null : change.Position : change.PreviousPosition;
        string teaching = position switch { "PRIMARY" => "主讲", "CO_TEACHER" => "协同", _ => "无任课" };
        string? role = after ? change.MembershipAfterRole ?? change.MembershipRole : change.MembershipRole;
        string direct = role switch { "OWNER" => "所有者", "MANAGER" => "管理", "TEACHER" => "教师", "ASSISTANT" => "助教", "VIEWER" => "只读", _ => "无直接成员" };
        return $"{teaching} · 直接权限：{direct}";
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
        var room = catalog.Classes.FirstOrDefault(row => row.Id == parts[0]);
        var group = catalog.Groups.FirstOrDefault(row => row.Id == key);
        string name = room is not null ? $"{room.Name}（{room.Code}）" : group is not null ? $"{group.Name}（{group.Code}）" :
            _groups.GetValueOrDefault(key)?.Name ?? key;
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
        if (_inWorkspace && _compact) { OpenWorkspaceChanges(sender, args); return; }
        if (_compact) PlanTabs.SelectedIndex = 2;
        Focus(PlanChangeRows);
    }

    private void FocusKey(string key)
    {
        if (_catalog is not { } catalog) return;
        LeaveWorkspaceReview();
        RenderWorkspaceReview();
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
