using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed record CourseGroupDirectoryRow(ManagedCourseGroup Group, string Subject, string Sources, string Primary)
{
    public string AutomationId => "CourseGroup-" + Group.Id;
    public override string ToString() => $"{Group.Name}（{Group.Code}）";
}

public sealed partial class CourseGroupsWindow : Window
{
    private sealed record PendingOperation(CourseGroupDraft Draft, CourseGroupPreview Preview, Guid RequestId);

    /** 预览里每个班级的容器，以及（如有）能直接跳到该班任课的按钮。 */
    private sealed record PreviewRowView(Border Container, Button? Action);
    private static readonly ConditionalWeakTable<ConsoleClient, Dictionary<string, PendingOperation>> PendingByClient = new();
    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckBox> _sourceChecks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PreviewRowView> _previewRows = new(StringComparer.Ordinal);
    private readonly string? _initialTerm, _initialGroup, _initialClass, _initialSubject;
    private readonly bool _initialTeaching;
    private CourseGroupCatalog? _catalog;
    private Dictionary<string, string> _courseClassNames = new(StringComparer.Ordinal);
    private Dictionary<string, string> _courseSubjectNames = new(StringComparer.Ordinal);
    private CourseGroupPreview? _preview;
    private TeachingWindow? _teaching;
    private ManagedCourseGroup? _editingGroup;
    private string? _directoryId, _editorGrade;
    private Vector _directoryOffset;
    private bool _editing, _readyToSave, _canRetry, _binding, _dirty, _scopeLost, _busy, _writing, _closed, _allowClose;

    public CourseGroupsWindow() => InitializeComponent();

    internal CourseGroupsWindow(ConsoleClient client, School school, string? term, string? group, string? room, string? subject, bool teaching) : this()
    {
        _client = client;
        _school = school;
        _initialTerm = term;
        _initialGroup = group;
        _initialClass = room;
        _initialSubject = subject;
        _initialTeaching = teaching;
        CourseScope.Text = school.Name;
        SizeChanged += (_, _) => LayoutEditor();
        Opened += (_, _) =>
        {
            BatchWorkspaceWindow.FitSchoolWindow(this);
            _ = RunAsync(OpenInitialAsync);
        };
        Closing += (_, args) =>
        {
            args.Cancel = !_allowClose && (_writing || _dirty && Pending is null);
            if (args.Cancel && !_writing) ConfirmDiscard();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
        Render();
    }

    internal Action<ConsoleApiException>? SessionExpired { get; init; }
    private Dictionary<string, PendingOperation> Operations => PendingByClient.GetOrCreateValue(_client);
    private PendingOperation? Pending => Operations.GetValueOrDefault(_school.Id);
    private CourseGroupLabel? Grade => CourseGrade.SelectedItem as CourseGroupLabel;
    private CourseGroupLabel? Subject => CourseSubject.SelectedItem as CourseGroupLabel;
    private CourseGroupDirectoryRow? DirectorySelection => CourseGroupRows.SelectedItem as CourseGroupDirectoryRow;

    private async Task OpenInitialAsync()
    {
        Feedback("正在读取当前学期走班教学班…");
        await LoadAsync(Pending is null ? _initialTerm : null);
        if (_closed) return;
        if (Pending is { } pending)
        {
            if (_catalog?.TermId == pending.Draft.TermId) RestoreEditor(pending);
            await ReadReceiptAsync(pending);
            return;
        }
        Feedback(null);
        if (_initialGroup is { } groupId)
        {
            if (_catalog?.Groups.FirstOrDefault(row => row.Id == groupId && row.SubjectId == _initialSubject) is not { } group)
            {
                Feedback("原教学班或学科已变化。请核对当前列表。");
                return;
            }
            _directoryId = group.Id;
            FilterDirectory();
            if (_initialTeaching) await OpenTeachingAsync(group.Id, group.SubjectId, true, CourseTeaching);
            else OpenEditor(group, group.GradeId, group.SubjectId, _initialClass);
            return;
        }
        if (_initialClass is { } id && _initialSubject is { } subject)
        {
            if (_catalog?.Classes.FirstOrDefault(row => row.Id == id) is not { } room || room.Rules.All(row => row.SubjectId != subject || row.Mode != "COURSE_GROUP"))
                Feedback("原来源班或走班规则已变化。请核对当前列表。");
            else OpenEditor(null, room.GradeId, subject, id);
            return;
        }
        Focus(CourseGradeFilter);
    }

    private async Task LoadAsync(string? term = null)
    {
        var result = await _client.GetCourseGroupsAsync(_school.Id, term, _lifetime.Token);
        if (_closed) return;
        string? oldGrade = (CourseGradeFilter.SelectedItem as CourseGroupLabel)?.Id;
        string? oldSubject = (CourseSubjectFilter.SelectedItem as CourseGroupLabel)?.Id;
        _catalog = result;
        _courseClassNames = result.Classes.ToDictionary(row => row.Id, row => row.Name, StringComparer.Ordinal);
        _courseSubjectNames = result.Subjects.ToDictionary(row => row.Id, row => row.Name, StringComparer.Ordinal);
        _binding = true;
        var grades = new[] { new CourseGroupLabel("", "全部年级") }.Concat(result.Grades).ToArray();
        var subjects = new[] { new CourseGroupLabel("", "全部学科") }.Concat(result.Subjects).ToArray();
        CourseGradeFilter.ItemsSource = grades;
        CourseSubjectFilter.ItemsSource = subjects;
        CourseGradeFilter.SelectedItem = grades.FirstOrDefault(row => row.Id == oldGrade) ?? grades[0];
        CourseSubjectFilter.SelectedItem = subjects.FirstOrDefault(row => row.Id == oldSubject) ?? subjects[0];
        _binding = false;
        CourseScope.Text = result.TermName is null ? $"{_school.Name} · 暂无启用学期" : $"{_school.Name} · {result.TermName}";
        FilterDirectory();
    }

    private CourseGroupDirectoryRow DirectoryRow(ManagedCourseGroup group)
    {
        string members = string.Join("、", group.SourceClassIds.Select(id => _courseClassNames.GetValueOrDefault(id, "待核对来源班")));
        string primary = string.Join("、", group.Assignments.Where(row => row.Position == "PRIMARY").Select(row => row.TeacherName));
        return new(group, _courseSubjectNames.GetValueOrDefault(group.SubjectId, "待核对学科"),
            members.Length > 0 ? members : "暂无来源", primary.Length > 0 ? primary : group.Assignments.Length > 0 ? "仅协同任课" : "待分配");
    }

    private void FilterDirectory()
    {
        if (_binding || _catalog is not { } catalog) return;
        string grade = (CourseGradeFilter.SelectedItem as CourseGroupLabel)?.Id ?? "";
        string subject = (CourseSubjectFilter.SelectedItem as CourseGroupLabel)?.Id ?? "";
        var rows = catalog.Groups.Where(group =>
            (grade.Length == 0 || grade == group.GradeId) && (subject.Length == 0 || subject == group.SubjectId) &&
            (CourseOnlyUnassigned.IsChecked != true || group.Assignments.Length == 0)).Select(DirectoryRow).ToArray();
        _binding = true;
        CourseGroupRows.ItemsSource = rows;
        CourseGroupRows.SelectedItem = rows.FirstOrDefault(row => row.Group.Id == _directoryId);
        _binding = false;
        CourseDirectoryEmpty.Text = catalog.TermId is null ? "当前没有启用学期。请先在学校管理中办理学期启用。" :
            catalog.Grades.Length == 0 || catalog.Subjects.Length == 0 ? "请先在「班级与教务」维护年级和学科。" :
            catalog.Groups.Length == 0 ? "当前学期尚未开设走班教学班。" : rows.Length == 0 ? "没有符合筛选条件的教学班。" : null;
        CourseDirectoryEmpty.IsVisible = CourseDirectoryEmpty.Text is not null;
        Render();
    }

    private void FilterChanged(object? sender, SelectionChangedEventArgs args) => FilterDirectory();
    private void FilterToggled(object? sender, RoutedEventArgs args) => FilterDirectory();
    private void DirectorySelected(object? sender, SelectionChangedEventArgs args)
    {
        if (_binding) return;
        _directoryId = DirectorySelection?.Group.Id;
        Render();
    }

    private void DirectoryKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || !CourseEdit.IsEnabled) return;
        args.Handled = true;
        EditClicked(sender, args);
    }

    private void RememberPosition()
    {
        if (CourseGroupRows.Scroll is ScrollViewer scroll) _directoryOffset = scroll.Offset;
    }

    private void OpenEditor(ManagedCourseGroup? group, string? gradeId, string? subjectId, string? sourceId = null)
    {
        if (_catalog is not { TermId: not null } catalog) return;
        RememberPosition();
        _editing = true;
        _editingGroup = group;
        _dirty = false;
        _scopeLost = false;
        _selected.Clear();
        if (group is not null) _selected.UnionWith(group.SourceClassIds);
        if (sourceId is not null) _selected.Add(sourceId);
        _binding = true;
        CourseGrade.ItemsSource = catalog.Grades;
        CourseSubject.ItemsSource = catalog.Subjects;
        CourseGrade.SelectedItem = catalog.Grades.FirstOrDefault(row => row.Id == gradeId) ?? (catalog.Grades.Length == 1 ? catalog.Grades[0] : null);
        CourseSubject.SelectedItem = catalog.Subjects.FirstOrDefault(row => row.Id == subjectId) ?? (catalog.Subjects.Length == 1 ? catalog.Subjects[0] : null);
        CourseName.Text = group?.Name;
        CourseCode.Text = group?.Code;
        CourseSelectable.IsChecked = group?.IsStudentSelectable ?? true;
        _binding = false;
        _editorGrade = Grade?.Id;
        ClearPreview();
        BindSources();
        CourseEditorScroll.Offset = default;
        CourseDiscard.IsVisible = false;
        CourseHeading.Text = group is null ? "新建走班教学班" : $"维护 {group.Name}";
        Render();
        Focus(sourceId is null ? (Grade is null ? CourseGrade : CourseName) : _sourceChecks.GetValueOrDefault(sourceId));
    }

    private void RestoreEditor(PendingOperation pending)
    {
        var draft = pending.Draft;
        OpenEditor(_catalog?.Groups.FirstOrDefault(row => row.Id == draft.GroupId), draft.GradeId, draft.SubjectId);
        _binding = true;
        CourseName.Text = draft.Name;
        CourseCode.Text = draft.Code;
        CourseSelectable.IsChecked = draft.IsStudentSelectable;
        _selected.Clear();
        _selected.UnionWith(draft.SourceClassIds);
        _binding = false;
        _dirty = true;
        BindSources();
        ShowPreview(pending.Preview, false);
    }

    private void BindSources()
    {
        if (_catalog is not { } catalog) return;
        string? subjectId = Subject?.Id;
        var candidates = catalog.Classes.Where(room => room.GradeId == Grade?.Id).ToArray();
        CourseSourceRows.Children.Clear();
        _sourceChecks.Clear();
        CourseSourcesEmpty.Text = Grade is null || Subject is null ? "请选择年级和学科。" :
            candidates.Length == 0 ? "本年级暂无启用行政班。" : null;
        CourseSourcesEmpty.IsVisible = CourseSourcesEmpty.Text is not null;
        foreach (var room in subjectId is null ? [] : candidates)
        {
            var rule = room.Rules.FirstOrDefault(row => row.SubjectId == subjectId);
            var other = catalog.Groups.Where(group => group.SubjectId == subjectId && group.SourceClassIds.Contains(room.Id))
                .Select(group => group.Name).ToArray();
            string description = rule?.Mode switch
            {
                "ADMIN_CLASS" => rule.Assignments.Length == 0 ? "随班授课" :
                    "随班授课 · " + string.Join("、", rule.Assignments.Select(teacher => teacher.ToString())),
                "COURSE_GROUP" => other.Length == 0 ? "已设走班，暂无教学班覆盖" : "已设走班 · " + string.Join("、", other),
                _ => "尚未开科"
            };
            AddSource(room.Id, room.Name, description);
        }
        if (_editingGroup is not null || Pending is not null)
            foreach (string id in _selected.Where(id => !_sourceChecks.ContainsKey(id)).ToArray())
                AddSource(id, "原来源班 " + id, "当前不再可用，可取消勾选后重新预览。");
        var reusable = _editingGroup is null && Grade is { } grade && Subject is { } subject
            ? catalog.Groups.Where(row => row.GradeId == grade.Id && row.SubjectId == subject.Id).Select(DirectoryRow).ToArray() : [];
        CourseExistingEntry.IsVisible = reusable.Length > 0;
        CourseExistingPicker.ItemsSource = reusable;
        CourseExistingPicker.SelectedIndex = reusable.Length == 1 ? 0 : -1;
        UpdateSourceCount();
    }

    private void AddSource(string id, string name, string description)
    {
        var check = new CheckBox {Content = name, IsChecked = _selected.Contains(id)};
        AutomationProperties.SetAutomationId(check, "CourseSource-" + id);
        var row = new StackPanel {Spacing = 1};
        row.Children.Add(check);
        row.Children.Add(new TextBlock {Text = description, Margin = new Thickness(27, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap, Classes = {"muted"}});
        CourseSourceRows.Children.Add(row);
        _sourceChecks[id] = check;
        check.IsCheckedChanged += (_, _) =>
        {
            if (check.IsChecked == true) _selected.Add(id);
            else _selected.Remove(id);
            UpdateSourceCount();
            DraftChanged();
        };
    }

    private void UpdateSourceCount() => CourseSourcesHeading.Text = $"来源行政班 · 已选 {_selected.Count} 班";

    private void LayoutEditor()
    {
        bool narrow = ClientSize.Width < 900;
        int columns = narrow ? 1 : 2;
        if (CourseEditorColumns.ColumnDefinitions.Count != columns)
        {
            CourseEditorColumns.ColumnDefinitions = new(narrow ? "*" : "*,1.12*");
            CourseEditorColumns.RowDefinitions = new(narrow ? "Auto,Auto" : "Auto");
        }
        Grid.SetColumn(CourseReviewCard, narrow ? 0 : 1);
        Grid.SetRow(CourseReviewCard, narrow ? 1 : 0);
        CourseReviewCard.Margin = narrow ? new Thickness(0, 8, 0, 0) : new Thickness(8, 0, 0, 0);
    }

    private void EditorScopeChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_binding || !_editing) return;
        if (Grade?.Id != _editorGrade) _selected.Clear();
        _editorGrade = Grade?.Id;
        BindSources();
        DraftChanged();
    }

    private void EditorTextChanged(object? sender, TextChangedEventArgs args) => DraftChanged();
    private void EditorFlagChanged(object? sender, RoutedEventArgs args) => DraftChanged();
    private void ExistingChanged(object? sender, SelectionChangedEventArgs args) { if (!_binding && _client is not null) Render(); }

    private void DraftChanged()
    {
        if (_binding || !_editing) return;
        _dirty = true;
        CourseDiscard.IsVisible = false;
        ClearPreview();
        Feedback(null);
        Render();
    }

    private void ClearPreview()
    {
        _readyToSave = false;
        _preview = null;
        _previewRows.Clear();
        CoursePreviewRows.Children.Clear();
        CoursePreviewIdentity.IsVisible = false;
        CoursePreviewStatus.Text = "先核对已选来源，再预览本次改动。";
    }

    private void UseExistingClicked(object? sender, RoutedEventArgs args)
    {
        if (!CourseUseExisting.IsEnabled || CourseExistingPicker.SelectedItem is not CourseGroupDirectoryRow existing) return;
        string[] choices = [.. _selected];
        OpenEditor(existing.Group, existing.Group.GradeId, existing.Group.SubjectId);
        _selected.UnionWith(choices);
        BindSources();
        _dirty = true;
        Feedback($"已改为维护「{existing.Group.Name}」。原教学班信息保留，已勾选的来源班仍在草稿中。");
    }

    private void EditClicked(object? sender, RoutedEventArgs args)
    {
        if (CourseEdit.IsEnabled && DirectorySelection?.Group is { } group) OpenEditor(group, group.GradeId, group.SubjectId);
    }

    private async void PrimaryClicked(object? sender, RoutedEventArgs args)
    {
        if (!CoursePrimary.IsEnabled) return;
        if (!_editing && Pending is null)
        {
            OpenEditor(null, (CourseGradeFilter.SelectedItem as CourseGroupLabel)?.Id, (CourseSubjectFilter.SelectedItem as CourseGroupLabel)?.Id);
            return;
        }
        if (Pending is { } pending)
        {
            await RunAsync(() => _canRetry ? SubmitAsync(pending) : ReadReceiptAsync(pending));
            return;
        }
        if (MakeDraft() is not { } draft) return;
        await RunAsync(async () =>
        {
            if (_readyToSave && _preview is { } confirmed)
            {
                var operation = new PendingOperation(draft, confirmed, Guid.NewGuid());
                Operations[_school.Id] = operation;
                await SubmitAsync(operation);
                return;
            }
            var observed = await _client.PreviewCourseGroupAsync(_school.Id, draft, _lifetime.Token);
            if (_closed) return;
            ShowPreview(observed, true);
            if (!observed.CanApply) FocusProblem(observed);
            else Focus(CoursePrimary);
        });
    }

    private CourseGroupDraft? MakeDraft()
    {
        if (_catalog?.TermId is not { } term) return null;
        string name = (CourseName.Text ?? "").Trim();
        string code = (CourseCode.Text ?? "").Trim().ToUpperInvariant();
        if (Grade is not { } grade || Subject is not { } subject)
        {
            Feedback("请选择年级和学科。");
            Focus(Grade is null ? CourseGrade : CourseSubject);
            return null;
        }
        if (name.Length is < 1 or > 191 || !Regex.IsMatch(code, @"\A[A-Z0-9][A-Z0-9._-]{1,63}\z"))
        {
            Feedback(name.Length == 0 ? "请填写教学班名称。" : "代码需为 2 至 64 位字母、数字、点、横线或下划线。");
            Focus(name.Length == 0 ? CourseName : CourseCode);
            return null;
        }
        if (_selected.Count is < 1 or > 500)
        {
            Feedback("请选择 1 至 500 个来源行政班。");
            Focus(_sourceChecks.Values.FirstOrDefault());
            return null;
        }
        return new(term, _editingGroup?.Id, grade.Id, subject.Id, name, code, CourseSelectable.IsChecked == true, [.. _selected]);
    }

    private void ShowPreview(CourseGroupPreview preview, bool freshlyReviewed)
    {
        _preview = preview;
        _readyToSave = freshlyReviewed && preview.CanApply;
        var desired = new CourseGroupIdentity((CourseName.Text ?? "").Trim(), (CourseCode.Text ?? "").Trim().ToUpperInvariant(), CourseSelectable.IsChecked == true);
        CoursePreviewIdentity.Text = preview.CurrentGroup is { } earlier && earlier != desired
            ? $"当前：{DescribeIdentity(earlier)}\n保存后：{DescribeIdentity(desired)}" : DescribeIdentity(desired);
        CoursePreviewIdentity.IsVisible = true;
        _previewRows.Clear();
        CoursePreviewRows.Children.Clear();
        CoursePreviewStatus.Text = preview.CanApply
            ? $"新增 {preview.Rows.Count(row => row.Change == "ADD")}、保留 {preview.Rows.Count(row => row.Change == "KEEP")}、移出 {preview.Rows.Count(row => row.Change == "REMOVE")} 班"
            : "存在需要处理的项目。整次改动尚未写入。";
        foreach (var issue in preview.Issues)
            CoursePreviewRows.Children.Add(new TextBlock {Text = issue.Message, TextWrapping = TextWrapping.Wrap, Classes = {"label"}});
        foreach (var row in preview.Rows.OrderByDescending(row => row.Blocked).ThenBy(row => row.ClassName, StringComparer.CurrentCulture))
        {
            var content = new StackPanel {Spacing = 4};
            string change = row.Change switch {"ADD" => "新增", "REMOVE" => "移出", _ => "保留"};
            content.Children.Add(new TextBlock {Text = $"{change} · {row.ClassName}" + (row.Blocked ? " · 需处理" : ""),
                Classes = {"label"}, TextWrapping = TextWrapping.Wrap});
            content.Children.Add(new TextBlock {Text = row.Message, TextWrapping = TextWrapping.Wrap});
            if (row.Groups.Length > 0)
                content.Children.Add(new TextBlock {Text = "现有走班：" + string.Join("、", row.Groups.Select(group => group.Name)), TextWrapping = TextWrapping.Wrap, Classes = {"muted"}});
            Button? action = null;
            if (row.Blocked && row.Assignments.Length > 0)
            {
                // 先写教师，再给动作：窄窗里即使只露出这一块，也能看到是谁、接着按键处理。
                content.Children.Add(new TextBlock {Text = "当前任课：" + string.Join("、", row.Assignments.Select(teacher => teacher.ToString())), TextWrapping = TextWrapping.Wrap});
                if (row.Mode == "ADMIN_CLASS" && Subject is { } subject)
                {
                    action = new Button {Content = "打开该班任课", Classes = {"action"}};
                    AutomationProperties.SetAutomationId(action, "CourseConflictTeaching-" + row.ClassId);
                    Button target = action;
                    target.Click += async (_, _) => await RunAsync(() => OpenTeachingAsync(row.ClassId, subject.Id, false, target));
                    content.Children.Add(target);
                }
            }
            var container = new Border {Child = content, Classes = {"card"}, Padding = new Thickness(8)};
            if (row.Blocked) container.Classes.Add("blocked");
            CoursePreviewRows.Children.Add(container);
            _previewRows[row.ClassId] = new PreviewRowView(container, action);
        }
    }

    private static string DescribeIdentity(CourseGroupIdentity group) =>
        $"{group.Name}（{group.Code}）· {(group.IsStudentSelectable ? "允许学生自行选择" : "未开放学生自选")}";

    private void FocusProblem(CourseGroupPreview preview)
    {
        Feedback(string.Join(" ", preview.Issues.Select(row => row.Message).Concat(preview.Rows.Where(row => row.Blocked)
            .Take(3).Select(row => $"{row.ClassName}：{row.Message}"))));
        if (preview.Issues.Any(row => row.Code == "WORKSPACE_CODE_EXISTS")) { Focus(CourseCode); return; }
        if (preview.Rows.FirstOrDefault(row => row.Blocked) is not { } blocked)
        {
            Focus(CoursePrimary);
            return;
        }
        // 关键是别把视口滚到来源列表：先让这条冲突整块进入视野，再把焦点交给它自己的动作。
        RevealBlockedRow(blocked.ClassId);
    }

    private void RevealBlockedRow(string classId)
    {
        if (!_previewRows.TryGetValue(classId, out var view))
        {
            CoursePrimary.BringIntoView();
            Focus(CoursePrimary);
            return;
        }
        view.Container.BringIntoView();
        if (view.Action is { } action) Focus(action);
        // 没有可执行动作（例如来源班已停用）时，唯一出路是取消勾选，焦点就该回到那个来源班。
        else if (_sourceChecks.GetValueOrDefault(classId) is { } source) Focus(source);
        else Focus(CoursePrimary);
    }

    private async Task SubmitAsync(PendingOperation pending)
    {
        _writing = true;
        _canRetry = false;
        Feedback("正在保存走班配置…");
        Render();
        CourseGroupResult result;
        try { result = await _client.SaveCourseGroupAsync(_school.Id, pending.Draft, pending.Preview, pending.RequestId, _lifetime.Token); }
        catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or >= 500)
        {
            if (!_closed) await ReadReceiptAsync(pending);
            return;
        }
        catch (ConsoleApiException)
        {
            if (!_closed)
            {
                Operations.Remove(_school.Id);
                _readyToSave = false;
            }
            throw;
        }
        if (_closed) return;
        if (result.Receipt is { } receipt) { await CompleteAsync(receipt); return; }
        Operations.Remove(_school.Id);
        ShowPreview(result.Preview!, false);
        if (result.State == "STALE") Feedback("服务器安排已变化，整次零写入。请核对后重新预览。");
        if (!result.Preview!.CanApply)
        {
            FocusProblem(result.Preview);
            if (result.State == "STALE") Feedback("服务器安排已变化，整次零写入。 " + CourseNotice.Text);
        }
        else Focus(CoursePrimary);
    }

    private async Task ReadReceiptAsync(PendingOperation pending)
    {
        _canRetry = false;
        Feedback("正在按原请求编号只读核对走班回执…");
        var receipt = await _client.GetCourseGroupReceiptAsync(_school.Id, pending.Draft, pending.RequestId, _lifetime.Token);
        if (_closed) return;
        if (receipt is not null) { await CompleteAsync(receipt); return; }
        var current = await _client.GetCourseGroupsAsync(_school.Id, null, _lifetime.Token);
        if (_closed) return;
        _canRetry = current.TermId == pending.Draft.TermId;
        Feedback(_canRetry
            ? "暂未找到本次回执。可以继续核对，或用同一个请求编号重试。列表中的同码教学班无法证明本次已保存。"
            : "暂未找到本次回执，启用学期已变化。仍可核对原回执，或结束本次核对。");
        CourseDiscard.IsVisible = !_canRetry;
        // 「按原编号重试」或「结束核对」只留一个可用，焦点必须跟着走，否则会停在刚被隐藏的按钮上。
        Focus(_canRetry ? CoursePrimary : CourseDiscard);
    }

    private async Task CompleteAsync(CourseGroupReceipt receipt)
    {
        Operations.Remove(_school.Id);
        _dirty = _canRetry = false;
        _directoryId = receipt.GroupId;
        _writing = false;
        ShowDirectory();
        Feedback("已收到本次走班办理回执，正在刷新教学班列表…");
        try { await LoadAsync(); }
        catch (ConsoleApiException error) when (error.StatusCode is not (401 or 403))
        {
            if (!_closed) Feedback($"走班已保存。教学班列表暂未刷新，请点击刷新。编号：{receipt.GroupId}");
            return;
        }
        if (_closed) return;
        if (_catalog?.TermId != receipt.TermId || _catalog.Groups.FirstOrDefault(row => row.Id == receipt.GroupId) is not { } saved)
        {
            Feedback($"走班已保存。教学班现已变更或不在当前学期。编号：{receipt.GroupId}");
            return;
        }
        if (saved.Assignments.Length > 0) CourseOnlyUnassigned.IsChecked = false;
        if (CourseGradeFilter.SelectedItem is CourseGroupLabel {Id.Length: > 0} grade && grade.Id != saved.GradeId) CourseGradeFilter.SelectedIndex = 0;
        if (CourseSubjectFilter.SelectedItem is CourseGroupLabel {Id.Length: > 0} subject && subject.Id != saved.SubjectId) CourseSubjectFilter.SelectedIndex = 0;
        FilterDirectory();
        Feedback($"已确认「{DirectorySelection?.Group.Name}」的走班配置。可继续分配任课。");
        // 重新载入会把列表滚回顶部，这里补一次「选中 + 偏移」恢复，回来仍停在原来的位置。
        RestoreDirectoryView();
        Focus(CourseTeaching);
    }

    private void RestoreDirectoryView() => Dispatcher.UIThread.Post(() =>
    {
        if (_closed || _editing) return;
        if (CourseGroupRows.Scroll is ScrollViewer scroll) scroll.Offset = _directoryOffset;
        if (DirectorySelection is { } row)
        {
            CourseGroupRows.ScrollIntoView(row);
            if (CourseGroupRows.ContainerFromItem(row) is ListBoxItem item) item.BringIntoView();
        }
    }, DispatcherPriority.Loaded);

    private async void TeachingClicked(object? sender, RoutedEventArgs args)
    {
        if (CourseTeaching.IsEnabled && DirectorySelection?.Group is { } group)
        {
            // 列表视口要记住：回来时不能回到顶部。
            RememberPosition();
            await RunAsync(() => OpenTeachingAsync(group.Id, group.SubjectId, true, CourseTeaching));
        }
    }

    private async Task OpenTeachingAsync(string targetId, string subjectId, bool courseGroup, Control returnTo)
    {
        if (_catalog?.TermId is not { } term) return;
        var window = new TeachingWindow(_client, _school, term, targetId, subjectId, courseGroup: courseGroup);
        window.SessionExpired += error => SessionExpired?.Invoke(error);
        _teaching = window;
        try { await window.ShowDialog(this); }
        finally { _teaching = null; }
        if (_closed) return;
        await LoadAsync(term);
        if (_closed) return;
        if (_editing)
        {
            ClearPreview();
            BindSources();
            Feedback("已返回走班草稿。请重新预览来源班的当前安排。");
            Focus(_sourceChecks.GetValueOrDefault(targetId) ?? (Control)CoursePrimary);
        }
        else
        {
            RestoreDirectoryView();
            Focus(returnTo);
        }
    }

    private async void RefreshClicked(object? sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        if (Pending is { } pending) { await ReadReceiptAsync(pending); return; }
        if (!_editing) { _scopeLost = false; await LoadAsync(); if (!_closed) Feedback(null); return; }
        if (_catalog?.TermId is not { } term) return;
        await LoadAsync(term);
        if (!_closed) { ClearPreview(); BindSources(); Feedback("已刷新原学期的来源列表，草稿已保留。请重新预览。"); }
    });

    private void ShowDirectory()
    {
        _editing = _dirty = false;
        _editingGroup = null;
        CourseDiscard.IsVisible = false;
        CourseHeading.Text = "走班教学班";
        Render();
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed || _editing) return;
            if (CourseGroupRows.Scroll is ScrollViewer scroll) scroll.Offset = _directoryOffset;
            if (DirectorySelection is { } row && CourseGroupRows.ContainerFromItem(row) is ListBoxItem item) item.Focus();
            else Focus(CourseGradeFilter);
        }, DispatcherPriority.Loaded);
    }

    private void BackClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy || _writing) return;
        if (Pending is not null || !_editing) { Close(); return; }
        if (_dirty) ConfirmDiscard();
        else { ShowDirectory(); Feedback(null); }
    }

    private void ConfirmDiscard()
    {
        CourseDiscard.Content = "放弃草稿";
        CourseDiscard.IsVisible = true;
        Feedback("草稿尚未保存。可继续编辑，或放弃草稿后返回。");
        Focus(CourseDiscard);
    }

    private void DiscardClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy) return;
        if (Pending is not null && _canRetry) return;
        Operations.Remove(_school.Id);
        ShowDirectory();
        Feedback("已返回教学班列表。可刷新读取服务器当前安排。");
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
            _scopeLost |= error.Code == "COURSE_GROUP_TERM_CHANGED" || error.StatusCode is 401 or 403;
            if (error.StatusCode is 401 or 403)
            {
                _catalog = null;
                CourseGroupRows.ItemsSource = null;
                CourseSourceRows.Children.Clear();
                CoursePreviewRows.Children.Clear();
                _editing = false;
            }
            Feedback(Pending is null ? error.Message : $"保存回执仍待核对。{error.Message}");
            if (error.StatusCode == 401) SessionExpired?.Invoke(error);
        }
        finally { _busy = _writing = false; if (!_closed) Render(); }
    }

    private void Render()
    {
        if (_client is null || CoursePrimary is null) return;
        bool pending = Pending is not null;
        bool editable = !_busy && !_scopeLost && !pending;
        bool existing = DirectorySelection is not null;
        CourseDirectory.IsVisible = !_editing;
        CourseEditorScroll.IsVisible = _editing;
        CourseGrade.IsEnabled = CourseSubject.IsEnabled = editable && _editingGroup is null;
        CourseName.IsEnabled = CourseCode.IsEnabled = CourseSelectable.IsEnabled = CourseSourceRows.IsEnabled = editable;
        CourseUseExisting.IsEnabled = editable && CourseExistingPicker.SelectedItem is CourseGroupDirectoryRow;
        CourseExistingPicker.IsEnabled = editable;
        CourseEdit.IsVisible = CourseTeaching.IsVisible = !_editing && !pending;
        CourseEdit.IsEnabled = CourseTeaching.IsEnabled = editable && existing;
        CourseRefresh.IsEnabled = !_busy;
        CourseRefresh.Content = pending ? "核对回执" : "刷新";
        CourseBack.IsEnabled = !_busy;
        CourseBack.Content = pending ? "关闭，稍后核对" : _editing ? "返回列表" : "关闭";
        CoursePrimary.Content = pending ? _canRetry ? "按原编号重试" : "核对原请求" :
            !_editing ? "新建教学班" : _readyToSave ? "确认保存" : _preview is null ? "预览改动" : "重新预览";
        CoursePrimary.IsVisible = !pending || _canRetry;
        CoursePrimary.IsEnabled = !_busy && (pending || !_scopeLost && _catalog?.TermId is not null &&
            (_editing || _catalog.Grades.Length > 0 && _catalog.Subjects.Length > 0));
        if (pending) CourseDiscard.Content = "结束核对";
        LayoutEditor();
    }

    private void Feedback(string? message)
    {
        CourseNotice.Text = message;
        CourseNotice.IsVisible = !string.IsNullOrWhiteSpace(message);
    }

    private void Focus(Control? target) => Dispatcher.UIThread.Post(() =>
    {
        if (!_closed && target is {IsEffectivelyVisible: true, IsEffectivelyEnabled: true})
        {
            target.BringIntoView();
            target.Focus(NavigationMethod.Tab);
        }
    }, DispatcherPriority.Loaded);

    internal void CloseForScopeChange()
    {
        _allowClose = _closed = true;
        _teaching?.CloseForScopeChange();
        Close();
    }
}
