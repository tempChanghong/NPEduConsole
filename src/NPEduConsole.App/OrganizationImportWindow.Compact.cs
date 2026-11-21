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
// 差异行只给对象和原因；「现在 → 导入后」只在底部摘要里、且要真的有值。
public sealed partial class OrganizationImportWindow : Window
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
    private IDataTemplate? _wideDifferenceTemplate;

    private void ArrangeOrganizationLayout(bool force = false)
    {
        if (_closed) return;
        HookCompactBehaviour();

        var compact = ClientSize.Width > 0 && ClientSize.Width < CompactWidth;
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
        OrgBeforeHeader.IsVisible = !compact;
        OrgAfterHeader.IsVisible = !compact;
        SwapDifferenceTemplate(compact);
        UpdateCompactDifference();
    }

    private void SwapDifferenceTemplate(bool compact)
    {
        if (compact)
        {
            if (ReferenceEquals(OrgDifferences.ItemTemplate, CompactDifferenceTemplate)) return;
            _wideDifferenceTemplate ??= OrgDifferences.ItemTemplate;
            OrgDifferences.ItemTemplate = CompactDifferenceTemplate;
            return;
        }
        if (_wideDifferenceTemplate is not null && !ReferenceEquals(OrgDifferences.ItemTemplate, _wideDifferenceTemplate))
            OrgDifferences.ItemTemplate = _wideDifferenceTemplate;
    }

    // 窄窗行：状态 + 对象 + 原因，没有原因就只占一行；「现在 → 导入后」留给底部摘要。
    private static readonly IDataTemplate CompactDifferenceTemplate =
        new FuncDataTemplate<OrganizationDifference>((row, _) => CompactDifferenceRow(row));

    private static Control CompactDifferenceRow(OrganizationDifference? row)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = 6,
            RowSpacing = 2,
            Margin = new Thickness(2, 1, 2, 1),
        };
        var state = new TextBlock
        {
            Text = row?.ActionLabel ?? "",
            Classes = { "label" },
            VerticalAlignment = VerticalAlignment.Top,
        };
        var target = new TextBlock { Text = row?.Object ?? "", TextWrapping = TextWrapping.Wrap };
        var message = new TextBlock
        {
            Text = row?.Message ?? "",
            Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsVisible = !string.IsNullOrEmpty(row?.Message),
        };
        Grid.SetColumn(target, 1);
        Grid.SetRow(message, 1);
        Grid.SetColumnSpan(message, 2);
        grid.Children.Add(state);
        grid.Children.Add(target);
        grid.Children.Add(message);
        return grid;
    }

    private void DifferenceSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (_closed) return;
        UpdateCompactDifference();
    }

    private void DifferenceKeyDown(object? sender, KeyEventArgs args)
    {
        if (_closed) return;
        switch (args.Key)
        {
            case Key.Enter:
                if (OrgDifferences.SelectedItem is OrganizationDifference chosen)
                {
                    args.Handled = true;
                    LocateDifference(chosen);
                }
                break;
            case Key.F8:
                args.Handled = JumpToBlockingDifference();
                break;
        }
    }

    private void UpdateCompactDifference()
    {
        var row = OrgDifferences.SelectedItem as OrganizationDifference;
        var detail = row is not null && (row.Current is not null || row.After is not null) ? row.NarrowDetails : "";
        OrgCompactDifference.Text = detail;
        OrgCompactDifference.IsVisible = _compact && detail.Length > 0;
        AutomationProperties.SetName(OrgCompactDifference, row is null
            ? "所选差异"
            : detail.Length > 0 ? $"{row.Object}：{detail}" : $"{row.Object} {row.ActionLabel}");
    }

    private void LocateDifference(OrganizationDifference difference)
    {
        // 切 Tab、展开面板、聚焦字段都由本体的 FocusIssue 决定。
        FocusIssue(difference);
        UpdateCompactDifference();
    }

    private bool JumpToBlockingDifference()
    {
        var target = _visibleReview.FirstOrDefault(item => item.Action == "BLOCK" || item.NeedsReview);
        if (target is null) return false;
        if (OrgDifferenceFilter.SelectedIndex != BlockingFilter) OrgDifferenceFilter.SelectedIndex = BlockingFilter;
        OrgDifferences.SelectedItem = target;
        if (!ReferenceEquals(OrgDifferences.SelectedItem, target)) return false;
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
        AutomationProperties.SetLiveSetting(OrgCompactDifference, AutomationLiveSetting.Polite);
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
