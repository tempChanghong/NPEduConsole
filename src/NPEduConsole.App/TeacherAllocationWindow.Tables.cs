using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class TeacherAllocationWindow
{
    private sealed class TeacherRow(AllocationTeacher value)
    {
        internal string Identity = value.Username ?? value.Email ?? "", Name = value.Name ?? "", Pin = value.Pin ?? "";
        internal string? AccountId = value.AccountId;
        internal bool ConfirmSameName = value.ConfirmSameName;
        internal readonly List<AllocationAccess> Access = [.. value.Access];

        internal AllocationTeacher Snapshot(string mode) => new(
            mode == "OAUTH_EMAIL" ? null : Identity.Trim(), mode == "OAUTH_EMAIL" ? Identity.Trim() : null, AccountId,
            string.IsNullOrWhiteSpace(Name) ? null : Name.Trim(), mode == "LOCAL_PIN" && Pin.Length != 0 ? Pin : null,
            ConfirmSameName, [.. Access]);
    }

    private sealed record WorkspaceOption(AllocationWorkspace? Room, string Text)
    {
        public override string ToString() => Text;
    }

    private readonly List<TeacherRow> _teachers = [];
    private readonly Dictionary<string, Control> _allocationFields = new();
    private bool _buildingRows;
    private TeacherRow? SelectedTeacher => AllocationTeachers.SelectedIndex is var index && index >= 0 && index < _teachers.Count ?
        _teachers[index] : null;
    private static readonly string[] RoleValues = ["TEACHER", "ASSISTANT", "VIEWER"];
    private static readonly string[] Roles = ["教师", "助教", "只读"];

    private static string RoleLabel(string? value) => value switch
    { "TEACHER" => "教师", "ASSISTANT" => "助教", "VIEWER" => "只读", "OWNER" => "所有者", _ => "—" };

    private void SetTable(IEnumerable<AllocationTeacher> rows, bool dirty)
    {
        foreach (var row in _teachers) row.Pin = "";
        _teachers.Clear();
        _teachers.AddRange(rows.Select(row => new TeacherRow(row with
        {
            Access = row.Access.Select(access =>
            {
                if (access.WorkspaceId is not null || access.WorkspaceCode is null) return access;
                var room = _catalog?.Workspaces.FirstOrDefault(board => board.Code == access.WorkspaceCode);
                return room is null ? access : access with { WorkspaceId = room.Id };
            }).ToArray()
        })));
        RebuildEditors();
        if (dirty) Edited();
    }

    private void ClearPins()
    {
        foreach (var row in _teachers) row.Pin = "";
        if (!_closed) RebuildEditors();
    }

    private void RebuildEditors()
    {
        var previous = AllocationTeachers.SelectedIndex;
        _buildingRows = true;
        try
        {
            _allocationFields.Clear();
            AllocationTeachers.ItemsSource = _teachers.Select((row, i) =>
            {
                var path = $"teachers[{i}]";
                var identity = _archivedCorrection ?
                    _catalog?.Accounts.FirstOrDefault(account => account.Id == row.AccountId)?.ToString() ?? row.AccountId ?? "" :
                    row.Identity;
                var person = Input(path + (EmailMode ? ".email" : ".username"), identity, EmailMode ? "邮箱" : "短账号",
                    EmailMode ? 191 : 32, changed =>
                    {
                        row.Identity = changed;
                        row.AccountId = null;
                        if (SelectedTeacher == row) UpdateAllocationEditorTitle(row);
                    });
                var name = Input(path + ".name", row.Name, "姓名", 64, changed =>
                {
                    row.Name = changed;
                    if (SelectedTeacher == row) UpdateAllocationEditorTitle(row);
                });
                if (_archivedCorrection)
                {
                    person.IsReadOnly = name.IsReadOnly = true;
                    _allocationFields[path + ".accountId"] = person;
                }
                var known = _catalog?.Accounts.Any(item => item.Username is not null &&
                    string.Equals(item.Username, row.Identity, StringComparison.OrdinalIgnoreCase)) == true;
                var pin = PinMode ? Input(path + ".pin", row.Pin, known ? "沿用原凭据" : "初始 PIN", 8, value => row.Pin = value, secret: true) :
                    new TextBlock { IsVisible = false } as Control;
                if (known) pin.IsEnabled = false;
                var line = Columns(PinMode ? "1.3*,1*,1*,Auto" : "1.3*,1*,0,Auto",
                    person, name, pin, RemoveButton("移出教师表", () => { row.Pin = ""; _teachers.Remove(row); RebuildEditors(); Edited(); }));
                line.GotFocus += (_, _) => { if (AllocationTeachers.SelectedIndex != i) AllocationTeachers.SelectedIndex = i; };
                _allocationFields[path] = person;
                return line;
            }).ToArray();
            AllocationTeachers.SelectedIndex = _teachers.Count == 0 ? -1 : Math.Clamp(previous, 0, _teachers.Count - 1);
        }
        finally { _buildingRows = false; }
        RebuildAccessEditor();
    }

    private void UpdateAllocationEditorTitle(TeacherRow? teacher) => AllocationEditorHeading.Text = teacher is null ? "选择一行教师" :
        $"{(string.IsNullOrWhiteSpace(teacher.Name) ? teacher.Identity : teacher.Name)} · 班级访问";

    private void RebuildAccessEditor()
    {
        if (_buildingRows) return;
        var teacher = SelectedTeacher;
        _buildingRows = true;
        try
        {
            UpdateAllocationEditorTitle(teacher);
            AllocationConfirmName.IsVisible = teacher is not null && !EmailMode && !_archivedCorrection;
            AllocationConfirmName.IsChecked = teacher?.ConfirmSameName ?? false;
            AllocationAddAccess.IsVisible = teacher is not null && Term is not null;
            AllocationAccessRows.Children.Clear();
            if (teacher is null) return;
            var path = $"teachers[{AllocationTeachers.SelectedIndex}]";
            _allocationFields[path + ".confirmSameName"] = AllocationConfirmName;
            if (teacher.Access.Count == 0)
                AllocationAccessRows.Children.Add(new TextBlock
                {
                    Text = _archivedCorrection ? "请选择要办理直接访问的行政班或走班教学班。" :
                        EmailMode ? "选择至少一个行政班或走班教学班。" : "留空只建普通学校账号。班级访问可稍后分配。",
                    Classes = { "muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap
                });
            for (var i = 0; i < teacher.Access.Count; i++)
            {
                var index = i;
                var row = teacher.Access[index];
                var at = path + $".access[{index}]";
                var options = (_catalog?.Workspaces ?? []).Select(item => new WorkspaceOption(item, item.ToString())).ToList();
                var selected = options.FirstOrDefault(item => row.WorkspaceId is not null ? item.Room?.Id == row.WorkspaceId :
                    item.Room?.Code == row.WorkspaceCode);
                if (selected is null && (row.WorkspaceId is not null || row.WorkspaceCode is not null))
                {
                    selected = new(null, $"原范围 · {row.WorkspaceCode ?? row.WorkspaceId}");
                    options.Insert(0, selected);
                }
                var workspace = new ComboBox { ItemsSource = options, SelectedItem = selected,
                    HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "选择行政班或走班教学班", MinWidth = 0 };
                AutomationProperties.SetName(workspace, "目标教学空间");
                workspace.IsEnabled = row.Operation != "REVOKE";
                workspace.SelectionChanged += (_, _) =>
                {
                    if (workspace.SelectedItem is WorkspaceOption { Room: { } room } && teacher.Access[index].WorkspaceId != room.Id)
                    {
                        teacher.Access[index] = teacher.Access[index] with { WorkspaceId = room.Id, WorkspaceCode = room.Code };
                        Edited();
                    }
                };
                ComboBox operation;
                if (row.Operation == "REVOKE")
                {
                    operation = new ComboBox { ItemsSource = new[] { "撤回指定邀请" }, SelectedIndex = 0, IsEnabled = false };
                }
                else
                {
                    operation = new ComboBox { ItemsSource = new[] { "分配角色", "移除直接成员" },
                        SelectedIndex = row.Operation == "SET" ? 0 : 1 };
                    operation.SelectionChanged += (_, _) =>
                    {
                        var selectedOperation = operation.SelectedIndex == 1 ? "REMOVE" : "SET";
                        if (operation.SelectedIndex < 0 || teacher.Access[index].Operation == selectedOperation) return;
                        teacher.Access[index] = teacher.Access[index] with
                        {
                            Operation = selectedOperation,
                            Role = operation.SelectedIndex == 1 ? null : teacher.Access[index].Role ?? (_archivedCorrection ? "VIEWER" : "TEACHER"),
                            ConfirmReduction = false
                        };
                        RebuildAccessEditor();
                        Edited();
                    };
                }
                AutomationProperties.SetName(operation, "访问操作");
                var role = new ComboBox { ItemsSource = Roles, SelectedIndex = Array.IndexOf(RoleValues, row.Role),
                    PlaceholderText = "角色", IsVisible = row.Operation == "SET", MinWidth = 0 };
                var confirm = new CheckBox {Content = "明确同意降低此项角色", IsChecked = row.ConfirmReduction, IsVisible = row.Operation == "SET"};
                AutomationProperties.SetName(role, "空间角色");
                role.SelectionChanged += (_, _) =>
                {
                    if (role.SelectedIndex >= 0 && teacher.Access[index].Role != RoleValues[role.SelectedIndex])
                    {
                        teacher.Access[index] = teacher.Access[index] with { Role = RoleValues[role.SelectedIndex], ConfirmReduction = false };
                        confirm.IsChecked = false;
                        Edited();
                    }
                };
                confirm.IsCheckedChanged += (_, _) =>
                {
                    teacher.Access[index] = teacher.Access[index] with { ConfirmReduction = confirm.IsChecked == true };
                    Edited();
                };
                _allocationFields[at] = workspace;
                _allocationFields[at + ".workspaceId"] = workspace;
                _allocationFields[at + ".role"] = role.IsVisible ? role : operation;
                _allocationFields[at + ".confirmReduction"] = confirm;
                var actions = Columns("1.5*,1*,Auto", operation, role,
                    RemoveButton("移出草稿", () => { teacher.Access.RemoveAt(index); RebuildAccessEditor(); Edited(); }));
                var panel = new StackPanel {Spacing = 4, Children = {workspace, actions, confirm}};
                AllocationAccessRows.Children.Add(new Border {Classes = { "card" }, Padding = new Thickness(7), Child = panel});
            }
        }
        finally { _buildingRows = false; }
    }

    private static Grid Columns(string columns, params Control[] items)
    {
        var grid = new Grid {ColumnDefinitions = new(columns), ColumnSpacing = 5, Margin = new Thickness(0, 2)};
        for (var index = 0; index < items.Length; index++)
        {
            Grid.SetColumn(items[index], index);
            grid.Children.Add(items[index]);
        }
        return grid;
    }

    private TextBox Input(string path, string value, string label, int max, Action<string> change, bool secret = false)
    {
        var box = new TextBox { Text = value, PlaceholderText = label, MaxLength = max, MinWidth = 0 };
        if (secret) box.PasswordChar = '●';
        AutomationProperties.SetName(box, label);
        _allocationFields[path] = box;
        box.PropertyChanged += (_, args) =>
        {
            if (args.Property != TextBox.TextProperty) return;
            change(box.Text ?? "");
            Edited();
        };
        return box;
    }

    private static Button RemoveButton(string text, Action remove)
    {
        var button = new Button { Content = text, Classes = { "action" }, VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => remove();
        return button;
    }

    private void SearchExistingChanged(object? sender, TextChangedEventArgs args) { if (_client is not null) RefreshExisting(); }

    private void RefreshExisting()
    {
        var query = AllocationSearchExisting.Text?.Trim() ?? "";
        AllocationExisting.ItemsSource = (_catalog?.Accounts ?? []).Where(row => row.Manageable &&
            (row.ToString().Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    private void ExistingChosen(object? sender, SelectionChangedEventArgs args)
    {
        if (_binding || !CanEdit || AllocationExisting.SelectedItem is not AllocationAccount account) return;
        var row = GetOrAdd(account.Id, account.Username, account.Email, account.Name);
        RebuildEditors();
        AllocationTeachers.SelectedIndex = _teachers.IndexOf(row);
        AllocationExisting.SelectedIndex = -1;
        if (account.Disabled) Status("该账号已停用，请先在教师账号目录核对并恢复。");
        Edited();
    }

    private TeacherRow GetOrAdd(string? id, string? username, string? email, string? name)
    {
        var identity = EmailMode ? email : username;
        var row = _teachers.FirstOrDefault(item => id is not null && item.AccountId == id ||
            !_archivedCorrection && identity is not null && item.Identity.Equals(identity, StringComparison.OrdinalIgnoreCase));
        if (row is not null) return row;
        row = new(new(username, email, id, name, null, false, []));
        _teachers.Add(row);
        return row;
    }

    private void AddTeacherClicked(object? sender, RoutedEventArgs args)
    {
        _teachers.Add(new(new(null, null, null, null, null, false, [])));
        RebuildEditors();
        AllocationTeachers.SelectedIndex = _teachers.Count - 1;
        FocusAllocationField($"teachers[{AllocationTeachers.SelectedIndex}]");
        Edited();
    }

    private void TeacherSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (!_buildingRows && ReferenceEquals(args.Source, AllocationTeachers)) RebuildAccessEditor();
    }

    private void AddAccessClicked(object? sender, RoutedEventArgs args)
    {
        if (SelectedTeacher is not { } teacher || Term is null) return;
        var initial = _archivedCorrection && teacher.Access.Count == 0 ?
            _catalog?.Workspaces.FirstOrDefault(room => room.Id == _initialWorkspaceId) : null;
        teacher.Access.Add(new(initial?.Id, initial?.Code, "SET", _archivedCorrection ? "VIEWER" : "TEACHER"));
        RebuildAccessEditor();
        FocusAllocationField($"teachers[{AllocationTeachers.SelectedIndex}].access[{teacher.Access.Count - 1}]");
        Edited();
    }

    private void ConfirmNameChanged(object? sender, RoutedEventArgs args)
    {
        if (_buildingRows || SelectedTeacher is not { } teacher) return;
        teacher.ConfirmSameName = AllocationConfirmName.IsChecked == true;
        Edited();
    }

    private void LocateClicked(object? sender, RoutedEventArgs args)
    {
        if (AllocationDifferences.SelectedItem is AllocationDifference row) FocusAllocationIssue(row);
    }

    private void FocusAllocationIssue(AllocationDifference row)
    {
        if (!CanEdit) return;
        var match = Regex.Match(row.Path, @"^(teachers\[(\d+)\])");
        if (!match.Success || !int.TryParse(match.Groups[2].Value, out var index) || index >= _teachers.Count)
        {
            AllocationTerm.Focus(NavigationMethod.Tab);
            return;
        }
        AllocationTabs.SelectedIndex = 0;
        AllocationTeachers.SelectedIndex = index;
        if (_archivedCorrection && row.Path.EndsWith(".accountId", StringComparison.Ordinal))
        {
            RevealArchivedIdentity();
            Status(row.Message ?? "请在教师目录重新核对原账号。");
        }
        else if (row.Path.Contains(".access", StringComparison.Ordinal) || row.Path.EndsWith(".confirmSameName", StringComparison.Ordinal))
            RevealAllocationAccess();
        FocusAllocationField(row.Path, match.Groups[1].Value);
    }

    private void FocusAllocationField(string path, string? fallback = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed) return;
            var field = _allocationFields.GetValueOrDefault(path);
            if (field?.IsEffectivelyEnabled != true && fallback is not null) field = _allocationFields.GetValueOrDefault(fallback);
            field?.BringIntoView();
            field?.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private async void OpenPasteClicked(object? sender, RoutedEventArgs args)
    {
        if (!CanEdit) return;
        var columns = Mode switch
        {
            "LOCAL_PIN" => "短账号、姓名、初始 PIN、班级代码、角色。已有教师可留空 PIN。",
            "SHARED_PASSWORD" => "短账号、姓名、班级代码、角色。无需填写学校通用口令。",
            _ => "邮箱、姓名（可空）、班级代码、角色。未登录邮箱可等待认领。"
        };
        var error = new TextBlock {TextWrapping = Avalonia.Media.TextWrapping.Wrap};
        var read = new Button {Content = "读取 Excel 剪贴板并载入", Classes = { "action", "accent" }};
        var cancel = new Button {Content = "取消", Classes = { "action" }};
        var dialog = new Window { Title = "从 Excel 粘贴教师", Width = 560, Height = 240, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel {Margin = new Thickness(16), Spacing = 9,
                Children = {new TextBlock {Text = "从 Excel 复制以下顺序的列。班级代码可用逗号分隔；角色可填教师、助教或只读。",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap},
                    new TextBlock {Text = columns, TextWrapping = Avalonia.Media.TextWrapping.Wrap}, error,
                    new StackPanel {Orientation = Orientation.Horizontal, Spacing = 7, Children = {cancel, read}}}}};
        cancel.Click += (_, _) => dialog.Close();
        read.Click += async (_, _) =>
        {
            read.IsEnabled = false;
            try
            {
                var text = dialog.Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;
                if (text is null) throw new ArgumentException("剪贴板中没有可读取的 Excel 列。");
                LoadAllocationText(text, legacy: false);
                dialog.Close();
            }
            catch (ArgumentException input) { error.Text = input.Message; }
            catch (InvalidOperationException) { error.Text = "暂时无法读取剪贴板，请重新复制。"; }
            finally { read.IsEnabled = true; }
        };
        await dialog.ShowDialog(this);
        if (!_closed) AllocationPaste.Focus(NavigationMethod.Tab);
    }
}
