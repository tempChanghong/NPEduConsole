using Avalonia.Controls;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class GradeTeachingPlanView
{
    private string? _newKey, _replaceOriginal;
    private GradePlanCourse? Existing => PlanExistingGroup.SelectedItem as GradePlanCourse;

    private void BindEditors()
    {
        if (_catalog is not { } data) return;
        BindRuleSubjects();
        _filling = true;
        PlanGroupSubject.ItemsSource = data.Subjects;
        PlanTeacher.ItemsSource = data.Teachers;
        PlanSources.ItemsSource = data.Classes.Select(row => new Option(row.Id, $"{row.Name}（{row.Code}）")).ToArray();
        var focal = data.Groups.Where(row => row.SubjectId == _subjectId).ToArray();
        PlanExistingGroup.ItemsSource = focal;
        PlanExistingGroup.SelectedItem = focal.FirstOrDefault(row => row.Id == (_openingGroup ?? _selectedGroupKey));
        _filling = false;
        if (Existing is { } group) SetGroupEditor(group);
        else StartNewGroup();
        if (_openingGroup is not null)
        {
            _openingGroup = null;
            if (Existing is not null) { PlanTabs.SelectedIndex = 1; Focus(PlanGroupOperation); }
        }
        BindRows();
        BindTeacherTargets();
    }

    private void BindRuleSubjects()
    {
        if (_catalog is not { } catalog) return;
        string? choice = RuleSubjectId;
        var ids = _groups.Values.Select(row => row.SubjectId).Append(_subjectId).ToHashSet();
        var choices = catalog.Subjects.Where(row => ids.Contains(row.Id)).ToArray();
        _filling = true;
        PlanRuleSubject.ItemsSource = choices;
        PlanRuleSubject.SelectedItem = choices.FirstOrDefault(row => row.Id == choice) ??
            choices.FirstOrDefault(row => row.Id == _subjectId);
        _filling = false;
    }

    private void RuleSubjectChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_filling || _client is null) return;
        BindRows();
        BindTeacherTargets();
    }

    private void BindRows()
    {
        if (_catalog is not { } data || RuleSubjectId is not { } subject) return;
        var selected = SelectedClasses.Select(row => row.ClassId).ToHashSet();
        var previewRows = _preview?.Rows.ToDictionary(row => row.Key) ?? [];
        var beforeRows = _previousPreview?.Rows.ToDictionary(row => row.Key) ?? [];
        var rows = data.Classes.Select(room =>
        {
            string key = RowKey(room.Id, subject);
            if (previewRows.TryGetValue(key, out var row))
            {
                var issues = _preview!.Issues.Concat(_preview.Notices).Where(item => item.Key == key).ToArray();
                bool stale = _changedKeys.Contains(key);
                return new GradeTeachingPlanRowView(key, room.Id, $"{row.ClassName}\n{row.Code}", State(row.Current), State(row.After),
                    stale ? DescribeRoomDifference(beforeRows.GetValueOrDefault(key)?.Current, row.Current) :
                        string.Join("\n", issues.Select(item => item.Message)),
                    stale || _preview.Issues.Any(item => item.Key == key));
            }
            var rule = room.Rules.FirstOrDefault(row => row.SubjectId == subject);
            var linked = data.Groups.Where(row => row.SubjectId == subject && row.IsActive && row.SourceClassIds.Contains(room.Id)).ToArray();
            var choices = linked.Where(row => row.IsStudentSelectable).Select(row => new GradePlanChoice(row.Id, row.Name, row.Code)).ToArray();
            var teachers = room.Assignments.Where(row => row.SubjectId == subject).ToArray();
            var state = new GradePlanRoomState(rule?.Mode ?? "UNCONFIGURED", rule?.Compulsory == true, choices, teachers,
                linked.Select(row => new GradePlanLinkedGroup(row.Id, row.Name, row.Code, row.IsStudentSelectable, row.Assignments)).ToArray());
            string action = state.Mode switch
            {
                "ADMIN_CLASS" when teachers.Length == 0 => "待分配随班任课",
                "COURSE_GROUP" when linked.Length == 0 => "待补走班来源",
                "COURSE_GROUP" when linked.Any(row => row.Assignments.Length == 0) =>
                    "待分配任课：" + string.Join("、", linked.Where(row => row.Assignments.Length == 0).Select(row => row.Name)),
                _ => ""
            };
            string after = _rules.GetValueOrDefault(key) is { } pending ? FormatMode(pending.Mode, pending.Compulsory) + "\n待预览" : "沿用当前安排";
            return new GradeTeachingPlanRowView(key, room.Id, $"{room.Name}\n{room.Code}", State(state), after, action, false);
        }).ToArray();
        _filling = true;
        PlanRows.ItemsSource = rows;
        foreach (var row in rows.Where(row => selected.Contains(row.ClassId))) PlanRows.SelectedItems?.Add(row);
        _filling = false;
        UpdateRowDetail();
        Render();
    }

    private static string FormatMode(string mode, bool compulsory) => mode == "UNCONFIGURED" ? "未开设" :
        (mode == "ADMIN_CLASS" ? "随行政班" : "走班") + (compulsory ? " · 必修" : " · 选修");

    private static string DescribeRoomDifference(GradePlanRoomState? before, GradePlanRoomState current)
    {
        if (before is null) return "服务器当前安排有变化。";
        var changes = new List<string>();
        if (before.Mode != current.Mode || before.Compulsory != current.Compulsory) changes.Add("授课方式或必修设置");
        if (!before.Choices.SequenceEqual(current.Choices)) changes.Add("学生可选项");
        var oldTeachers = before.Assignments.Concat(before.Groups.SelectMany(group => group.Assignments));
        var newTeachers = current.Assignments.Concat(current.Groups.SelectMany(group => group.Assignments));
        if (!oldTeachers.SequenceEqual(newTeachers)) changes.Add("当前任课");
        return changes.Count == 0 ? "服务器版本已有变化。" : string.Join("、", changes) + "已变化。";
    }

    private static string State(GradePlanRoomState state)
    {
        string choices = state.Mode == "COURSE_GROUP" ? "\n可选：" + (state.Choices.Length == 0 ? "暂无" :
            string.Join("、", state.Choices.Select(row => row.Name))) : "";
        var teaching = state.Assignments.Select(row => row.ToString()).Concat(state.Groups.SelectMany(group =>
            group.Assignments.Select(row => $"{group.Name} · {row}"))).ToArray();
        return FormatMode(state.Mode, state.Compulsory) + choices +
            (teaching.Length == 0 ? "" : "\n任课：" + string.Join("、", teaching));
    }

    private void RowSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_filling || _client is null) return;
        UpdateRowDetail();
        BindTeachingExisting();
        Render();
    }

    private void UpdateRowDetail()
    {
        var selected = SelectedClasses.ToArray();
        PlanRowDetail.Text = selected.Length == 1 && !string.IsNullOrWhiteSpace(selected[0].Action) ? selected[0].Action :
            selected.Length == 0 ? "可多选行政班，并为所选班级添加规则或任课。" : $"已选 {selected.Length} 个行政班。";
    }

    private void StageRules(object? sender, RoutedEventArgs args)
    {
        if (_catalog is not { } catalog || RuleSubjectId is not { } subject ||
            PlanRuleMode.SelectedItem is not Option mode || PlanCompulsory.SelectedItem is not Option compulsory) return;
        foreach (var row in SelectedClasses.ToArray())
        {
            var current = catalog.Classes.First(room => room.Id == row.ClassId).Rules.FirstOrDefault(rule => rule.SubjectId == subject);
            var prior = _rules.GetValueOrDefault(row.Key);
            bool required = compulsory.Id == "REQUIRED" || compulsory.Id == "KEEP" && (prior?.Compulsory ?? current?.Compulsory) == true;
            _rules[row.Key] = new(row.ClassId, subject, mode.Id, required);
        }
        DraftChanged();
    }

    private void GroupSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (_filling || _client is null || Existing is not { } group) return;
        SetGroupEditor(group);
        Render();
    }

    private void SetGroupEditor(GradePlanCourse group)
    {
        _selectedGroupKey = group.Id;
        _newKey = _replaceOriginal = null;
        PlanRetireFormer.IsVisible = false;
        var staged = _groups.GetValueOrDefault(group.Id);
        var options = new List<Option>();
        if (group.IsActive) options.AddRange([new("UPDATE", "调整来源或资料"), new("RETIRE", "停用这班")]);
        else options.Add(new("RESTORE", "恢复原班"));
        if (group.PublicationCount + group.AssignmentHistoryCount == 0) options.Add(new("RECLASSIFY", "原编号改科"));
        _filling = true;
        PlanGroupOperation.ItemsSource = options;
        PlanGroupOperation.SelectedItem = options.FirstOrDefault(row => row.Id == staged?.Operation) ?? options[0];
        PlanGroupName.Text = staged?.Name ?? group.Name;
        PlanGroupCode.Text = staged?.Code ?? group.Code;
        PlanGroupSubject.SelectedItem = _catalog?.Subjects.FirstOrDefault(row => row.Id == (staged?.SubjectId ?? group.SubjectId));
        PlanGroupSelectable.IsChecked = staged?.IsStudentSelectable ?? group.IsStudentSelectable;
        SelectSources(staged?.SourceClassIds ?? group.SourceClassIds);
        PlanMergeTarget.ItemsSource = _catalog?.Groups.Where(row => row.IsActive && row.SubjectId == group.SubjectId && row.Id != group.Id).ToArray();
        PlanMergeTarget.SelectedItem = null;
        _filling = false;
        string roles = string.Join("、", group.Members.Select(row => $"{row.Name}（{row.Role}）"));
        string invites = string.Join("、", group.Invitations.Select(row => $"{row.Email}（{row.Role}）"));
        PlanGroupImpact.Text = $"{group} · {group.AssignmentHistoryCount} 条任课历史 · {group.PublicationCount} 条发布历史。" +
            (group.PublicationCount + group.AssignmentHistoryCount > 0 ? "换科须按新科目另开班。" : "可核对来源与权限后原编号改科。") +
            (roles.Length > 0 ? $"\n成员权限：{roles}" : "") + (invites.Length > 0 ? $"\n待认领邀请：{invites}" : "");
        ApplyOperation();
    }

    private void SelectSources(IEnumerable<string> selected)
    {
        var ids = selected.ToHashSet();
        PlanSources.SelectedItems?.Clear();
        foreach (var row in PlanSources.Items.OfType<Option>().Where(option => ids.Contains(option.Id))) PlanSources.SelectedItems?.Add(row);
    }

    private void NewGroup(object? sender, RoutedEventArgs args) => StartNewGroup();
    private void ReplaceGroup(object? sender, RoutedEventArgs args)
    {
        var original = Existing;
        if (original is null) return;
        StartNewGroup(original);
        Focus(PlanGroupSubject);
    }

    private void StartNewGroup(GradePlanCourse? former = null)
    {
        _filling = true;
        PlanExistingGroup.SelectedItem = null;
        _filling = false;
        _selectedGroupKey = null;
        _newKey = Guid.NewGuid().ToString("N");
        _replaceOriginal = former?.Id;
        PlanRetireFormer.IsVisible = former?.IsActive == true;
        PlanRetireFormer.IsChecked = false;
        PlanGroupOperation.ItemsSource = new Option[] {new("CREATE", "另开教学班")};
        PlanGroupOperation.SelectedIndex = 0;
        PlanGroupSubject.SelectedItem = _catalog?.Subjects.FirstOrDefault(row => row.Id == _subjectId);
        PlanGroupName.Text = "";
        PlanGroupCode.Text = "";
        PlanGroupSelectable.IsChecked = true;
        SelectSources([]);
        PlanMergeTarget.ItemsSource = null;
        PlanGroupImpact.Text = former is null ? "新班使用新的代码和编号。" :
            $"正在为 {former.Name} 另开新班。请明确选择科目、来源及是否停用原班，原历史和权限保留在原班。";
        ApplyOperation();
        Render();
    }

    private void GroupOperationChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!_filling && _client is not null) ApplyOperation();
    }

    private void ApplyOperation()
    {
        var action = (PlanGroupOperation.SelectedItem as Option)?.Id;
        PlanGroupName.IsEnabled = PlanGroupCode.IsEnabled = PlanGroupSelectable.IsEnabled = PlanSources.IsEnabled = action != "RETIRE";
        PlanGroupSubject.IsEnabled = action is "CREATE" or "RECLASSIFY";
        PlanSetSourceRules.IsEnabled = action != "RETIRE";
        PlanSetSourceRules.IsChecked = false;
    }

    private void StageGroup(object? sender, RoutedEventArgs args)
    {
        if (PlanGroupOperation.SelectedItem is not Option op || PlanGroupSubject.SelectedItem is not GradePlanLabel subject ||
            (_newKey ?? Existing?.Id) is not { } key) return;
        var selected = PlanSources.SelectedItems?.OfType<Option>().Select(row => row.Id).ToArray() ?? [];
        string name = PlanGroupName.Text?.Trim() ?? "";
        string code = PlanGroupCode.Text?.Trim().ToUpperInvariant() ?? "";
        if (op.Id != "RETIRE" && (name.Length == 0 || code.Length < 2 || selected.Length == 0))
        {
            Notice("请填写教学班名称、代码并选择来源班级。", true);
            return;
        }
        var group = new GradePlanGroupEdit(key, Existing?.Id, op.Id, subject.Id, name, code, PlanGroupSelectable.IsChecked == true, selected);
        _groups[key] = group;
        if (_replaceOriginal is { } original && PlanRetireFormer.IsChecked == true && _catalog?.Groups.FirstOrDefault(row => row.Id == original) is { } former)
            _groups[former.Id] = Retire(former);
        if (op.Id != "RETIRE" && PlanSetSourceRules.IsChecked == true && _catalog is { } catalog)
        {
            foreach (var room in catalog.Classes.Where(row => selected.Contains(row.Id)))
            {
                string roomKey = RowKey(room.Id, subject.Id);
                bool compulsory = _rules.TryGetValue(roomKey, out var current) ? current.Compulsory :
                    room.Rules.FirstOrDefault(rule => rule.SubjectId == subject.Id)?.Compulsory == true;
                _rules[roomKey] = new(room.Id, subject.Id, "COURSE_GROUP", compulsory);
            }
        }
        DraftChanged();
    }

    private static GradePlanGroupEdit Retire(GradePlanCourse group) =>
        new(group.Id, group.Id, "RETIRE", group.SubjectId, group.Name, group.Code, group.IsStudentSelectable, group.SourceClassIds);

    private void MergeGroups(object? sender, RoutedEventArgs args)
    {
        if (Existing is not { IsActive: true } source || PlanMergeTarget.SelectedItem is not GradePlanCourse target) return;
        if (_groups.TryGetValue(target.Id, out var edited) && edited.Operation != "UPDATE")
        {
            Notice("目标教学班在方案中已有其他操作，请先核对。", true);
            return;
        }
        var selected = (_groups.GetValueOrDefault(source.Id)?.SourceClassIds ?? source.SourceClassIds)
            .Concat(edited?.SourceClassIds ?? target.SourceClassIds).Distinct().ToArray();
        _groups[target.Id] = edited is not null ? edited with {SourceClassIds = selected} :
            new(target.Id, target.Id, "UPDATE", target.SubjectId, target.Name, target.Code, target.IsStudentSelectable, selected);
        _groups[source.Id] = Retire(source);
        DraftChanged();
        Notice("已加入并入方案。会合并来源并停用原班，任课和成员权限仍需逐项核对。");
    }
}
