using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.Core;

namespace NPEduConsole.App;

// 窄窗布局与差异摘要：列宽原地改、面板换显示，列表与编辑器都不重挂。
// 窄窗把选中对象的前后值展开在同一行下方。
public sealed partial class OrganizationImportView : UserControl
{
    private const double CompactWidth = 940;
    private const int BlockingFilter = 4;

    private bool _compact;
    private bool _compactDetail;
    private bool _compactDetailGroup;
    private bool _arranged;
    private bool _lastDetail;
    private bool _lastDetailGroup;
    private bool _hooked;
    private int _lastSection = -1;
    private OrganizationDifference? _lastLocatedDifference;

    private void ArrangeOrganizationLayout(bool force = false)
    {
        if (_closed) return;
        HookCompactBehaviour();

        var compact = Bounds.Width > 0 && Bounds.Width < CompactWidth;
        if (!compact)
        {
            _compactDetail = false;
            _compactDetailGroup = false;
        }
        var detail = compact && _compactDetail;
        if (!force && _arranged && compact == _compact && detail == _lastDetail && _compactDetailGroup == _lastDetailGroup) return;

        var classDetail = detail && !_compactDetailGroup;
        var groupDetail = detail && _compactDetailGroup;
        var classWasVisible = OrgClassEditor.IsVisible;
        var groupWasVisible = OrgGroupEditor.IsVisible;

        ArrangePane(OrgClassLayout, OrgClassTable, OrgClassEditor, OrgBackClassTable, compact, classDetail);
        ArrangePane(OrgGroupLayout, OrgGroupTable, OrgGroupEditor, OrgBackGroupTable, compact, groupDetail);

        Classes.Set("org-compact", compact);
        ArrangeDifferences(compact);

        _compact = compact;
        _lastDetail = detail;
        _lastDetailGroup = _compactDetailGroup;
        _arranged = true;

        SyncOpenButtons();
        AnnouncePanes();
        RescueHiddenFocus(classWasVisible, groupWasVisible);
    }

    private static void ArrangePane(Grid layout, Control table, Control editor, Button back, bool compact, bool detail)
    {
        // 只改宽度，列定义数量全程不变。
        var columns = layout.ColumnDefinitions;
        if (columns.Count >= 2)
        {
            columns[0].Width = new GridLength(compact ? 1 : 3, GridUnitType.Star);
            columns[1].Width = compact ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        }
        layout.ColumnSpacing = compact ? 0 : 8;

        table.IsVisible = !detail;
        editor.IsVisible = !compact || detail;
        Grid.SetColumn(editor, detail ? 0 : 1);
        back.IsVisible = detail;
    }

    private void ArrangeDifferences(bool compact)
    {
        // Hidden headers remain grid children during a resize. Keep all four column indices valid.
        var columns = OrgDifferenceHeaders.ColumnDefinitions;
        columns[1].Width = new GridLength(compact ? 1 : 1.35, GridUnitType.Star);
        columns[2].Width = columns[3].Width = compact ? new GridLength(0) : GridLength.Star;
        OrgBeforeHeader.IsVisible = OrgAfterHeader.IsVisible = !compact;
        OrgDifferences.ItemTemplate = compact ? CompactDifferenceTemplate : WideDifferenceTemplate;
    }

    private static readonly IDataTemplate CompactDifferenceTemplate =
        new FuncDataTemplate<OrganizationDifference>((row, _) => DifferenceRow(row, compact: true));

    private static readonly IDataTemplate WideDifferenceTemplate =
        new FuncDataTemplate<OrganizationDifference>((row, _) => DifferenceRow(row, compact: false));

    private static Control DifferenceRow(OrganizationDifference? row, bool compact)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new(compact ? "76,*" : "76,1.35*,*,*"),
            RowDefinitions = new("Auto,Auto,Auto"),
            ColumnSpacing = 9,
            RowSpacing = 3,
        };
        var border = new Border { Classes = { "org-difference-row" }, Padding = new(10, 7), Child = grid };
        if (row is null) return border;
        bool issue = row.Action == "BLOCK" || row.NeedsReview;
        bool declaration = row.Path.StartsWith("declaration.", StringComparison.Ordinal);
        AutomationProperties.SetAutomationId(border, $"OrgDifference-{row.Key}-{row.Path}");

        if (issue)
        {
            var label = new TextBlock { Text = row.ActionLabel, FontWeight = FontWeight.SemiBold };
            grid.Children.Add(new Border
            {
                Classes = { "org-issue-label" }, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top, Child = label
            });
        }
        else Add(row.ActionLabel, 0, emphasized: true);
        Add(row.Object, 1);
        if (!compact)
        {
            Add(row.BeforeText, 2);
            Add(row.AfterText, 3);
        }
        if (row.HasMessage) Add(row.Message!, 0, line: 1, span: compact ? 2 : 4);
        if (compact && (row.Current is not null || row.After is not null))
        {
            var values = new StackPanel { Classes = { "org-comparison-values" }, Spacing = 3 };
            values.Children.Add(new TextBlock
            {
                Text = (declaration ? "所选目标　" : "现在　") + row.BeforeText, TextWrapping = TextWrapping.Wrap
            });
            values.Children.Add(new TextBlock
            {
                Text = (declaration ? "文件声明　" : "导入后　") + row.AfterText, TextWrapping = TextWrapping.Wrap
            });
            Grid.SetRow(values, 2);
            Grid.SetColumn(values, 1);
            grid.Children.Add(values);
        }
        return border;

        void Add(string value, int column, bool emphasized = false, int line = 0, int span = 1)
        {
            var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
            text.Classes.Set("label", emphasized);
            Grid.SetColumn(text, column);
            Grid.SetColumnSpan(text, span);
            Grid.SetRow(text, line);
            grid.Children.Add(text);
        }
    }

    private void WorkspaceKeyDown(object? sender, KeyEventArgs args)
    {
        if (_closed || _busy || _showExcel || _replacement is not null || _discard) return;
        if (args.Key == Key.F8)
        {
            args.Handled = JumpToBlockingDifference();
            return;
        }
        if (args.Key == Key.Enter && _stage == WorkStage.Difference && OrgDifferences.IsKeyboardFocusWithin &&
            OrgDifferences.SelectedItem is OrganizationDifference chosen)
        {
            args.Handled = true;
            LocateDifference(chosen);
        }
    }

    private void LocateDifference(OrganizationDifference difference)
    {
        _lastLocatedDifference = difference;
        FocusIssue(difference);
    }

    private bool FocusArchivedGrade(OrganizationDifference difference)
    {
        if (!_archivedCorrection || difference.Path != "grade") return false;
        SetStage(WorkStage.Table);
        var reordered = GradeOrder(difference.Current) != GradeOrder(difference.After);
        Control target = (reordered ? FirstUsable(OrgGradeOrder) : null)
            ?? FirstUsable(OrgGradeName) ?? FirstUsable(OrgGradeCode) ?? OrgGrade;
        Status(reordered
            ? "归档学期的年级排序有变化：确认这里的顺序，再返回预览。"
            : "归档学期的年级名称有变化：确认这里的名称，再返回预览。");
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed) return;
            target.BringIntoView();
            target.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
        return true;
    }

    private static int? GradeOrder(string? text)
    {
        var match = GradeOrderText.Match(text ?? "");
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : null;
    }

    private static Control? FirstUsable(Control control) =>
        control.IsEffectivelyVisible && control.IsEffectivelyEnabled ? control : null;

    private static readonly Regex GradeOrderText = new(@" · 顺序 (-?\d+)$", RegexOptions.CultureInvariant);

    private bool JumpToBlockingDifference()
    {
        if (!CanEdit) return false;
        var issues = _visibleReview.Where(item => item.Action == "BLOCK" || item.NeedsReview).ToArray();
        if (issues.Length == 0) return false;
        var index = Array.IndexOf(issues, _lastLocatedDifference);
        var target = issues[(index + 1) % issues.Length];
        if (OrgDifferenceFilter.SelectedIndex != BlockingFilter) OrgDifferenceFilter.SelectedIndex = BlockingFilter;
        OrgDifferences.SelectedItem = target;
        LocateDifference(target);
        return true;
    }

    private void OpenClassEditorClicked(object? sender, RoutedEventArgs args)
    {
        if (_closed || !_compact || SelectedClass is null) return;
        if (_compactDetail && !_compactDetailGroup) return;
        _compactDetail = true;
        _compactDetailGroup = false;
        ArrangeOrganizationLayout(force: true);
        FocusDetail(false);
    }

    private void BackClassTableClicked(object? sender, RoutedEventArgs args)
    {
        if (_closed || !_compactDetail || _compactDetailGroup) return;
        _compactDetail = false;
        ArrangeOrganizationLayout(force: true);
        RestoreTableFocus(OrgClasses);
    }

    private void OpenGroupEditorClicked(object? sender, RoutedEventArgs args)
    {
        if (_closed || !_compact || SelectedGroup is null) return;
        if (_compactDetail && _compactDetailGroup) return;
        _compactDetail = true;
        _compactDetailGroup = true;
        ArrangeOrganizationLayout(force: true);
        FocusDetail(true);
    }

    private void BackGroupTableClicked(object? sender, RoutedEventArgs args)
    {
        if (_closed || !_compactDetail || !_compactDetailGroup) return;
        _compactDetail = false;
        ArrangeOrganizationLayout(force: true);
        RestoreTableFocus(OrgGroups);
    }

    // 本体只在定位规则/来源字段时调用；宽窗天然可见，空操作。
    private void RevealOrganizationEditor(bool group)
    {
        if (_closed || !_compact) return;
        var section = group ? 1 : 0;
        if (OrgSections.SelectedIndex != section) OrgSections.SelectedIndex = section;
        if (_compactDetail && _compactDetailGroup == group) return;
        _compactDetail = true;
        _compactDetailGroup = group;
        ArrangeOrganizationLayout(force: true);
    }

    private void FocusDetail(bool group)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed || !_compact || !_compactDetail) return;
            var scope = group ? (Control)OrgSources : OrgRules;
            var target = scope.GetVisualDescendants().OfType<Control>()
                .FirstOrDefault(row => row.Focusable && row.IsEffectivelyVisible && row.IsEffectivelyEnabled);
            target ??= group ? (Control)OrgSourceCodes : (Control)OrgAddRule;
            target.BringIntoView();
            target.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private void RestoreTableFocus(ListBox list)
    {
        var item = list.SelectedItem;
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed) return;
            if (item is not null && list.ContainerFromItem(item) is Control container)
            {
                container.BringIntoView();
                if (container.IsEffectivelyVisible && container.IsEffectivelyEnabled) container.Focus(NavigationMethod.Tab);
                return;
            }
            list.BringIntoView();
            list.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    // 由宽变窄收起编辑器时，只有焦点原本就在编辑器里才交回表格。
    private void RescueHiddenFocus(bool classWasVisible, bool groupWasVisible)
    {
        if (classWasVisible && !OrgClassEditor.IsVisible) RestoreTableFocusIfInside(OrgClassEditor, OrgClasses);
        if (groupWasVisible && !OrgGroupEditor.IsVisible) RestoreTableFocusIfInside(OrgGroupEditor, OrgGroups);
    }

    private void RestoreTableFocusIfInside(Visual pane, ListBox list)
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not Visual focused) return;
        if (!ReferenceEquals(focused, pane) && !focused.GetVisualAncestors().Any(row => ReferenceEquals(row, pane))) return;
        RestoreTableFocus(list);
    }

    private void SyncOpenButtons()
    {
        var classReady = _compact && !(_compactDetail && !_compactDetailGroup) && OrgClasses.SelectedItem is not null;
        var groupReady = _compact && !(_compactDetail && _compactDetailGroup) && OrgGroups.SelectedItem is not null;
        OrgOpenClassEditor.IsVisible = classReady;
        OrgOpenGroupEditor.IsVisible = groupReady;
    }

    private void HookCompactBehaviour()
    {
        if (_hooked) return;
        _hooked = true;
        OrgClasses.SelectionChanged += (_, _) => SyncOpenButtons();
        OrgGroups.SelectionChanged += (_, _) => SyncOpenButtons();
        OrgSections.SelectionChanged += (_, _) => TrackSection();
    }

    // 窄窗开着详情时切到别的 Tab，详情要收起。
    private void TrackSection()
    {
        var index = OrgSections.SelectedIndex;
        if (index == _lastSection) return;
        _lastSection = index;
        if (_closed || !_compact || !_compactDetail) return;
        if (index == (_compactDetailGroup ? 1 : 0)) return;
        _compactDetail = false;
        ArrangeOrganizationLayout(force: true);
    }

    private void AnnouncePanes()
    {
        var classTitle = OrgClassTitle.Text?.Trim();
        var groupTitle = OrgGroupTitle.Text?.Trim();
        AutomationProperties.SetName(OrgOpenClassEditor,
            string.IsNullOrEmpty(classTitle) ? "展开本班授课规则" : $"展开 {classTitle} 的授课规则");
        AutomationProperties.SetName(OrgBackClassTable, "返回班级表");
        AutomationProperties.SetName(OrgOpenGroupEditor,
            string.IsNullOrEmpty(groupTitle) ? "展开本组来源班" : $"展开 {groupTitle} 的来源班");
        AutomationProperties.SetName(OrgBackGroupTable, "返回教学班表");
    }
}
