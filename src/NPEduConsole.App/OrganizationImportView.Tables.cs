using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class OrganizationImportView
{
    private sealed class ClassRow(OrganizationClass value)
    {
        internal string Code = value.Code, Name = value.Name;
        internal bool? Selectable = value.IsStudentSelectable;
        internal bool Replace = value.ReplaceSubjectRules;
        internal readonly List<OrganizationSubjectRule> Rules = [.. value.SubjectRules];
        internal OrganizationClass Snapshot() => new(Code, Name, Selectable, Replace, [.. Rules]);
    }

    private sealed class GroupRow(OrganizationGroup value)
    {
        internal string Code = value.Code, Name = value.Name, Subject = value.SubjectCode;
        internal bool? Selectable = value.IsStudentSelectable;
        internal bool Replace = value.ReplaceSources;
        internal readonly List<string> Sources = [.. value.SourceClassCodes];
        internal OrganizationGroup Snapshot() => new(Code, Name, Subject, Selectable, Replace, [.. Sources]);
    }

    private sealed class SubjectRow(OrganizationSubject value)
    {
        internal string Code = value.Code, Name = value.Name, Category = value.Category, Order = value.SortOrder.ToString();
        internal OrganizationSubject Snapshot()
        {
            if (!int.TryParse(Order, out var order)) throw new ArgumentException($"学科 {Code} 的排序需为整数。");
            return new(Code, Name, Category, order);
        }
    }

    private readonly List<ClassRow> _classes = [];
    private readonly List<GroupRow> _groups = [];
    private readonly List<SubjectRow> _subjects = [];
    private readonly Dictionary<string, Control> _fields = new(StringComparer.Ordinal);
    private bool _bindingDetails;
    private static readonly string[] SelectableLabels = ["沿用 / 默认", "允许", "不允许"];
    private static readonly string[] Modes = ["随行政班", "走班"];
    private static readonly string[] Categories = ["核心", "选修", "其他"];
    private static readonly string[] CategoryValues = ["CORE", "ELECTIVE", "OTHER"];

    private void RebuildTables()
    {
        var classIndex = OrgClasses.SelectedIndex;
        var groupIndex = OrgGroups.SelectedIndex;
        var prev = _bindingDetails;
        _bindingDetails = true;
        try
        {
            _fields.Clear();
            OrgClasses.ItemsSource = _classes.Select((row, i) =>
            {
                var path = $"administrativeClasses[{i}]";
                var grid = RowGrid("1*,1.4*,1*,Auto",
                    Entry(path + ".code", row.Code, "班级代码", 64, value => row.Code = value),
                    Entry(path + ".name", row.Name, "班级名称", 191, value => { row.Name = value; if (i == OrgClasses.SelectedIndex) OrgClassTitle.Text = ClassTitle(row); }),
                    Select(path + ".isStudentSelectable", SelectableLabels, SelectionIndex(row.Selectable), "学生自选",
                        index => row.Selectable = FromSelection(index)),
                    Remove("移出导入表中的行政班", () => { _classes.Remove(row); RebuildTables(); Edited(); }));
                SetupTableRow(grid, path, OrgClasses, i);
                return grid;
            }).ToArray();
            OrgGroups.ItemsSource = _groups.Select((row, i) =>
            {
                var path = $"courseGroups[{i}]";
                var grid = RowGrid("1*,1.4*,1*,1*,Auto",
                    Entry(path + ".code", row.Code, "教学班代码", 64, value => row.Code = value),
                    Entry(path + ".name", row.Name, "教学班名称", 191, value => { row.Name = value; if (i == OrgGroups.SelectedIndex) OrgGroupTitle.Text = GroupTitle(row); }),
                    Entry(path + ".subjectCode", row.Subject, "学科代码", 64, value => row.Subject = value),
                    Select(path + ".isStudentSelectable", SelectableLabels, SelectionIndex(row.Selectable), "学生自选",
                        index => row.Selectable = FromSelection(index)),
                    Remove("移出导入表中的教学班", () => { _groups.Remove(row); RebuildTables(); Edited(); }));
                SetupTableRow(grid, path, OrgGroups, i);
                return grid;
            }).ToArray();
            OrgSubjects.ItemsSource = _subjects.Select((row, i) =>
            {
                var path = $"subjects[{i}]";
                var grid = RowGrid("1*,2*,1*,0.6*,Auto",
                    Entry(path + ".code", row.Code, "学科代码", 64, value => row.Code = value),
                    Entry(path + ".name", row.Name, "学科名称", 191, value => row.Name = value),
                    Select(path + ".category", Categories, Array.IndexOf(CategoryValues, row.Category.ToUpperInvariant()), "学科类别",
                        index => row.Category = CategoryValues[index]),
                    Entry(path + ".sortOrder", row.Order, "排序", 6, value => row.Order = value),
                    Remove("移出导入表中的学科", () => { _subjects.Remove(row); RebuildTables(); Edited(); }));
                SetupTableRow(grid, path, OrgSubjects, i);
                return grid;
            }).ToArray();
            OrgClasses.SelectedIndex = _classes.Count == 0 ? -1 : Math.Clamp(classIndex, 0, _classes.Count - 1);
            OrgGroups.SelectedIndex = _groups.Count == 0 ? -1 : Math.Clamp(groupIndex, 0, _groups.Count - 1);
        }
        finally { _bindingDetails = prev; }
        RebuildDetails();
    }

    private static int SelectionIndex(bool? value) => value is null ? 0 : value.Value ? 1 : 2;
    private static bool? FromSelection(int index) => index == 0 ? null : index == 1;
    private static string ClassTitle(ClassRow row) => string.IsNullOrWhiteSpace(row.Name) ? "本行授课规则" : row.Name + " · 授课规则";
    private static string GroupTitle(GroupRow row) => string.IsNullOrWhiteSpace(row.Name) ? "本行来源班级" : row.Name + " · 来源班级";

    private void SetupTableRow(Grid row, string path, ListBox list, int index)
    {
        _fields[path] = row.Children[0];
        row.GotFocus += (_, _) => { if (list.SelectedIndex != index) list.SelectedIndex = index; };
    }

    private static Grid RowGrid(string columns, params Control[] children)
    {
        var row = new Grid {ColumnDefinitions = new(columns), ColumnSpacing = 6, Margin = new Thickness(0, 2)};
        for (var index = 0; index < children.Length; index++)
        {
            Grid.SetColumn(children[index], index);
            row.Children.Add(children[index]);
        }
        return row;
    }

    private TextBox Entry(string path, string value, string label, int maxLength, Action<string> change)
    {
        var box = new TextBox { Text = value, MaxLength = maxLength, PlaceholderText = label, MinWidth = 0 };
        box.Classes.Add("compact-field");
        Avalonia.Automation.AutomationProperties.SetName(box, label);
        _fields[path] = box;
        box.TextChanged += (_, _) => { var text = box.Text ?? ""; change(text); Edited(); };
        return box;
    }

    private ComboBox Select(string path, string[] values, int selected, string label, Action<int> change)
    {
        var combo = new ComboBox { ItemsSource = values, SelectedIndex = selected, HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = label, MinWidth = 0 };
        Avalonia.Automation.AutomationProperties.SetName(combo, label);
        _fields[path] = combo;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0) { change(combo.SelectedIndex); Edited(); }
        };
        return combo;
    }

    private static Button Remove(string label, Action action)
    {
        var button = new Button { Content = "移出表", Classes = { "action" }, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, label);
        button.Click += (_, _) => action();
        return button;
    }

    private void AddClassClicked(object? sender, RoutedEventArgs args)
    {
        _classes.Add(new(new("", "", null, false, [])));
        RebuildTables();
        OrgClasses.SelectedIndex = _classes.Count - 1;
        FocusField($"administrativeClasses[{OrgClasses.SelectedIndex}].code");
        Edited();
    }

    private void AddGroupClicked(object? sender, RoutedEventArgs args)
    {
        _groups.Add(new(new("", "", "", null, false, [])));
        RebuildTables();
        OrgGroups.SelectedIndex = _groups.Count - 1;
        FocusField($"courseGroups[{OrgGroups.SelectedIndex}].code");
        Edited();
    }

    private void AddSubjectClicked(object? sender, RoutedEventArgs args)
    {
        _subjects.Add(new(new("", "")));
        RebuildTables();
        OrgSubjects.SelectedIndex = _subjects.Count - 1;
        FocusField($"subjects[{OrgSubjects.SelectedIndex}].code");
        Edited();
    }

    private void CatalogSubjectSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (_binding || !CanEdit || OrgExistingSubject.SelectedItem is not OrganizationSubject subject) return;
        if (_subjects.All(row => !string.Equals(row.Code, subject.Code, StringComparison.OrdinalIgnoreCase)))
        {
            _subjects.Add(new(subject));
            RebuildTables();
            OrgSubjects.SelectedIndex = _subjects.Count - 1;
            FocusField($"subjects[{OrgSubjects.SelectedIndex}].name");
            Edited();
        }
        OrgExistingSubject.SelectedIndex = -1;
    }

    private ClassRow? SelectedClass => OrgClasses.SelectedIndex is var index && index >= 0 && index < _classes.Count ? _classes[index] : null;
    private GroupRow? SelectedGroup => OrgGroups.SelectedIndex is var index && index >= 0 && index < _groups.Count ? _groups[index] : null;
    private void ClassSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (!_bindingDetails && ReferenceEquals(args.Source, OrgClasses)) RebuildClassEditor();
    }
    private void GroupSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (!_bindingDetails && ReferenceEquals(args.Source, OrgGroups)) RebuildGroupEditor();
    }
    private void RebuildDetails() { RebuildClassEditor(); RebuildGroupEditor(); }

    private void RebuildClassEditor()
    {
        var room = SelectedClass;
        _bindingDetails = true;
        OrgClassTitle.Text = room is null ? "选择一行行政班" : ClassTitle(room);
        OrgReplaceRules.IsVisible = OrgAddRule.IsVisible = room is not null;
        OrgReplaceRules.IsChecked = room?.Replace ?? false;
        OrgRules.Children.Clear();
        if (room is not null)
        {
            var path = $"administrativeClasses[{OrgClasses.SelectedIndex}]";
            _fields[path + ".replaceSubjectRules"] = OrgReplaceRules;
            for (var i = 0; i < room.Rules.Count; i++)
            {
                var index = i;
                var rule = room.Rules[index];
                var key = path + $".subjectRules[{i}]";
                Control required;
                if (rule.NeedsCompulsoryChoice)
                    required = Select(key + ".isCompulsory", ["必修", "选修"], -1, "请选择必修或选修",
                        value => room.Rules[index] = room.Rules[index] with { IsCompulsory = value == 0, NeedsCompulsoryChoice = false });
                else
                {
                    var optional = new CheckBox { Content = "必修", IsChecked = rule.IsCompulsory };
                    optional.IsCheckedChanged += (_, _) => { room.Rules[index] = room.Rules[index] with { IsCompulsory = optional.IsChecked == true }; Edited(); };
                    required = optional;
                }
                var grid = RowGrid("1*,1*,Auto,Auto",
                    Entry(key + ".subjectCode", rule.SubjectCode, "学科代码", 64,
                        value => room.Rules[index] = room.Rules[index] with { SubjectCode = value }),
                    Select(key + ".deliveryMode", Modes, rule.DeliveryMode.ToUpperInvariant() switch
                    { "ADMIN_CLASS" => 0, "COURSE_GROUP" => 1, _ => -1 }, "授课方式",
                        value => room.Rules[index] = room.Rules[index] with { DeliveryMode = value == 0 ? "ADMIN_CLASS" : "COURSE_GROUP" }),
                    required, Remove("移出导入表中的此学科规则", () => { room.Rules.RemoveAt(index); RebuildClassEditor(); Edited(); }));
                _fields[key] = grid.Children[0];
                _fields[key + ".isCompulsory"] = required;
                OrgRules.Children.Add(grid);
            }
        }
        _bindingDetails = false;
    }

    private void AddRuleClicked(object? sender, RoutedEventArgs args)
    {
        if (SelectedClass is not { } room) return;
        room.Rules.Add(new("", "ADMIN_CLASS", true));
        RebuildClassEditor();
        FocusField($"administrativeClasses[{OrgClasses.SelectedIndex}].subjectRules[{room.Rules.Count - 1}].subjectCode");
        Edited();
    }

    private void ReplaceRulesChanged(object? sender, RoutedEventArgs args)
    {
        if (_bindingDetails || SelectedClass is not { } room) return;
        room.Replace = OrgReplaceRules.IsChecked == true;
        Edited();
    }

    private void RebuildGroupEditor()
    {
        var group = SelectedGroup;
        _bindingDetails = true;
        OrgGroupTitle.Text = group is null ? "选择一行教学班" : GroupTitle(group);
        OrgReplaceSources.IsVisible = OrgSourceCodes.IsVisible = group is not null;
        OrgReplaceSources.IsChecked = group?.Replace ?? false;
        OrgSourceCodes.Text = group is null ? null : string.Join("，", group.Sources);
        if (group is not null)
        {
            var path = $"courseGroups[{OrgGroups.SelectedIndex}]";
            _fields[path + ".sourceClassCodes"] = OrgSourceCodes;
            _fields[path + ".replaceSources"] = OrgReplaceSources;
        }
        FillSourceChoices(group);
        _bindingDetails = false;
    }

    private void FillSourceChoices(GroupRow? group)
    {
        OrgSources.Children.Clear();
        if (group is null) return;
        var gradeId = _catalog?.Grades.FirstOrDefault(row =>
            string.Equals(row.Code, OrgGradeCode.Text?.Trim(), StringComparison.OrdinalIgnoreCase))?.Id;
        var choices = (_catalog?.AdministrativeClasses ?? []).Where(row => row.GradeId == gradeId && gradeId is not null)
            .Select(row => (row.Code, row.Name)).Concat(_classes.Where(row => !string.IsNullOrWhiteSpace(row.Code))
                .Select(row => (row.Code.Trim(), row.Name.Trim()))).DistinctBy(row => row.Item1, StringComparer.OrdinalIgnoreCase)
            .OrderBy(row => row.Item1, StringComparer.OrdinalIgnoreCase);
        foreach (var (code, name) in choices)
        {
            var choice = new CheckBox { Content = $"{name}（{code}）",
                IsChecked = group.Sources.Contains(code, StringComparer.OrdinalIgnoreCase) };
            choice.IsCheckedChanged += (_, _) =>
            {
                if (_bindingDetails || SelectedGroup != group) return;
                group.Sources.RemoveAll(value => string.Equals(value, code, StringComparison.OrdinalIgnoreCase));
                if (choice.IsChecked == true) group.Sources.Add(code);
                _bindingDetails = true;
                OrgSourceCodes.Text = string.Join("，", group.Sources);
                _bindingDetails = false;
                Edited();
            };
            OrgSources.Children.Add(choice);
        }
    }

    private void ReplaceSourcesChanged(object? sender, RoutedEventArgs args)
    {
        if (_bindingDetails || SelectedGroup is not { } group) return;
        group.Replace = OrgReplaceSources.IsChecked == true;
        Edited();
    }

    private void SourceCodesChanged(object? sender, TextChangedEventArgs args)
    {
        if (_bindingDetails || SelectedGroup is not { } row) return;
        var sources = (OrgSourceCodes.Text ?? "").Split([',', '，', ';', '；', '、'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (row.Sources.SequenceEqual(sources)) return;
        row.Sources.Clear();
        row.Sources.AddRange(sources);
        FillSourceChoices(row);
        Edited();
    }

    private void LocateErrorClicked(object? sender, RoutedEventArgs args)
    {
        if (OrgDifferences.SelectedItem is OrganizationDifference difference) LocateDifference(difference);
    }

    private void FocusIssue(OrganizationDifference difference)
    {
        var path = difference.Path;
        if (!CanEdit) return;
        if (FocusArchivedGrade(difference)) return;
        if (path.StartsWith("declaration.", StringComparison.Ordinal))
        {
            SetStage(WorkStage.Table);
            FocusLater(path.StartsWith("declaration.grade", StringComparison.Ordinal) ? OrgGrade : OrgOpenFile);
            Status(difference.Message ?? "请核对文件声明和目标。");
            return;
        }
        if (path.StartsWith("grade", StringComparison.Ordinal))
        {
            SetStage(WorkStage.Table);
            FocusLater(path.EndsWith(".code", StringComparison.Ordinal) ? OrgGradeCode : OrgGradeName);
            return;
        }
        var match = Regex.Match(path, @"^(administrativeClasses|courseGroups|subjects)\[(\d+)\]");
        if (!match.Success || !int.TryParse(match.Groups[2].Value, out var index))
        {
            Status(difference.Message ?? "请在服务器差异中核对对象，再重新预览。");
            return;
        }
        var section = match.Groups[1].Value;
        var list = section == "administrativeClasses" ? OrgClasses : section == "courseGroups" ? OrgGroups : OrgSubjects;
        SetStage(WorkStage.Table);
        OrgSections.SelectedIndex = section == "administrativeClasses" ? 0 : section == "courseGroups" ? 1 : 2;
        list.SelectedIndex = index < list.ItemCount ? index : -1;
        if (list.SelectedItem is { } item) list.ScrollIntoView(item);
        if (section == "administrativeClasses" && (path.Contains(".subjectRules", StringComparison.Ordinal) || path.EndsWith(".replaceSubjectRules", StringComparison.Ordinal)))
            RevealOrganizationEditor(false);
        if (section == "courseGroups" && (path.Contains(".sourceClassCodes", StringComparison.Ordinal) || path.EndsWith(".replaceSources", StringComparison.Ordinal)))
            RevealOrganizationEditor(true);
        FocusField(path, match.Value);
    }

    private void FocusField(string path, string? fallback = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed) return;
            var field = _fields.GetValueOrDefault(path) ?? (fallback is not null ? _fields.GetValueOrDefault(fallback) : null);
            if (field is null) return;
            field.BringIntoView();
            field.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private void PasteClicked(object? sender, RoutedEventArgs args)
    {
        if (!CanEdit || _replacement is not null || _stage != WorkStage.Table) return;
        _showExcel = true;
        OrgExcelTable.SelectedIndex = OrgSections.SelectedIndex == 1 ? 1 : 0;
        UpdateExcelHint();
        Render();
        FocusLater(OrgExcelText);
    }

    private void ExcelTableChanged(object? sender, SelectionChangedEventArgs args) => UpdateExcelHint();

    private void UpdateExcelHint()
    {
        if (OrgExcelHint is null) return;
        OrgExcelHint.Text = OrgExcelTable.SelectedIndex == 1
            ? "教学班代码、名称、学科代码、来源班代码（逗号分隔）、学生自选（是／否）。前三列必填。"
            : "班级代码、班名、学科代码、学科名称、授课方式（随班／走班）、必修、学生自选。前两列必填，一个班有多科时可粘贴多行。";
        OrgExcelError.IsVisible = false;
    }

    private async void ReadExcelClipboardClicked(object? sender, RoutedEventArgs args)
    {
        if (!_showExcel || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try
        {
            var text = await clipboard.TryGetTextAsync();
            if (!_closed && _showExcel && text is not null) OrgExcelText.Text = text;
        }
        catch (Exception error) when (error is InvalidOperationException or PlatformNotSupportedException)
        {
            if (!_closed && _showExcel) ShowExcelError("暂时无法读取剪贴板。可以在输入框按 Ctrl+V 粘贴。");
        }
    }

    private void ApplyExcelClicked(object? sender, RoutedEventArgs args)
    {
        if (!_showExcel || !CanEdit) return;
        try
        {
            if (OrgExcelTable.SelectedIndex == 1)
            {
                var imported = OrganizationImportDocumentReader.ReadGroupColumns(OrgExcelText.Text ?? "");
                if (imported.Length == 0) throw new ArgumentException("没有可载入的教学班。");
                _groups.Clear();
                _groups.AddRange(imported.Select(row => new GroupRow(row)));
                OrgSections.SelectedIndex = 1;
            }
            else
            {
                var imported = OrganizationImportDocumentReader.ReadClassColumns(OrgExcelText.Text ?? "");
                if (imported.Classes.Length == 0) throw new ArgumentException("没有可载入的行政班。");
                var incoming = imported.Subjects.ToDictionary(row => row.Code, StringComparer.OrdinalIgnoreCase);
                _classes.Clear();
                _classes.AddRange(imported.Classes.Select(row => new ClassRow(row)));
                _subjects.RemoveAll(row => incoming.ContainsKey(row.Code));
                _subjects.AddRange(imported.Subjects.Select(row => new SubjectRow(row)));
                OrgSections.SelectedIndex = 0;
            }
            RebuildTables();
            EndExcel();
            Edited();
            Status("已将 Excel 列载入草稿。请预览并核对导入差异。");
        }
        catch (ArgumentException error) { ShowExcelError(error.Message); }
    }

    private void ShowExcelError(string message)
    {
        OrgExcelError.Text = message;
        OrgExcelError.IsVisible = true;
        FocusLater(OrgExcelText);
    }

    private void CancelExcelClicked(object? sender, RoutedEventArgs args) => EndExcel();

    private void EndExcel()
    {
        _showExcel = false;
        OrgExcelText.Clear();
        OrgExcelError.IsVisible = false;
        Render();
        FocusLater(OrgExcel);
    }
}
