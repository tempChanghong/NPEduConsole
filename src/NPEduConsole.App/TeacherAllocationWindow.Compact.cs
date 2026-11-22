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

// 窄窗布局：教师表与班级访问详情同一位置切换，列表和编辑器都不重挂。
// 差异行在窄窗只留状态／教师／教学空间（加原因）；「现在 → 提交后」只在选中行底部、且要有值。
public sealed partial class TeacherAllocationWindow : Window
{
    private const double CompactWidth = 940;
    private const int BlockingFilter = 3;

    private bool _compact;
    private bool _compactDetail;
    private bool _arranged;
    private bool _lastDetail;
    private bool _hooked;
    private int _lastTab = -1;
    private IDataTemplate? _wideDifferenceTemplate;

    private void ArrangeAllocationLayout(bool force = false)
    {
        if (_closed) return;
        HookCompactBehaviour();

        var compact = ClientSize.Width > 0 && ClientSize.Width < CompactWidth;
        if (!compact) _compactDetail = false;
        var detail = compact && _compactDetail;
        if (!force && _arranged && compact == _compact && detail == _lastDetail) return;

        var editorWasVisible = AllocationAccessEditor.IsVisible;

        // 只改宽度，列定义数量全程不变。
        var columns = AllocationEditLayout.ColumnDefinitions;
        if (columns.Count >= 2)
        {
            columns[0].Width = new GridLength(compact ? 1 : 3, GridUnitType.Star);
            columns[1].Width = compact ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        }
        AllocationEditLayout.ColumnSpacing = compact ? 0 : 9;

        AllocationTeacherTable.IsVisible = !detail;
        AllocationAccessEditor.IsVisible = !compact || detail;
        Grid.SetColumn(AllocationAccessEditor, detail ? 0 : 1);
        AllocationBackToTable.IsVisible = detail;

        Classes.Set("allocation-compact", compact);
        ArrangeAllocationDifferences(compact);

        _compact = compact;
        _lastDetail = detail;
        _arranged = true;

        SyncOpenEditorButton();
        AnnounceAllocationPanes();
        RescueHiddenAccessFocus(editorWasVisible);
    }

    private void ArrangeAllocationDifferences(bool compact)
    {
        AllocationCurrentHeader.IsVisible = !compact;
        AllocationAfterHeader.IsVisible = !compact;
        var columns = AllocationReviewHeader.ColumnDefinitions;
        if (columns.Count >= 5)
        {
            columns[3].Width = compact ? new GridLength(0) : new GridLength(1.6, GridUnitType.Star);
            columns[4].Width = compact ? new GridLength(0) : new GridLength(1.6, GridUnitType.Star);
        }
        SwapDifferenceTemplate(compact);
        UpdateCompactComparison();
    }

    private void SwapDifferenceTemplate(bool compact)
    {
        if (compact)
        {
            if (ReferenceEquals(AllocationDifferences.ItemTemplate, CompactDifferenceTemplate)) return;
            _wideDifferenceTemplate ??= AllocationDifferences.ItemTemplate;
            AllocationDifferences.ItemTemplate = CompactDifferenceTemplate;
            return;
        }
        if (_wideDifferenceTemplate is not null && !ReferenceEquals(AllocationDifferences.ItemTemplate, _wideDifferenceTemplate))
            AllocationDifferences.ItemTemplate = _wideDifferenceTemplate;
    }

    private static readonly IDataTemplate CompactDifferenceTemplate =
        new FuncDataTemplate<AllocationDifference>((row, _) => CompactAllocationRow(row));

    private static Control CompactAllocationRow(AllocationDifference? row)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1*,1.4*,1.4*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = 6,
            RowSpacing = 2,
            Margin = new Thickness(3, 2, 3, 2),
        };
        var state = new TextBlock
        {
            Text = row?.ActionLabel ?? "",
            Classes = { "label" },
            VerticalAlignment = VerticalAlignment.Top,
        };
        var teacher = new TextBlock { Text = row?.Teacher ?? "", TextWrapping = TextWrapping.Wrap };
        var scope = new TextBlock { Text = row?.Scope ?? "", TextWrapping = TextWrapping.Wrap };
        var message = new TextBlock
        {
            Text = row?.Message ?? "",
            Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsVisible = !string.IsNullOrWhiteSpace(row?.Message),
        };
        Grid.SetColumn(teacher, 1);
        Grid.SetColumn(scope, 2);
        Grid.SetRow(message, 1);
        Grid.SetColumnSpan(message, 3);
        grid.Children.Add(state);
        grid.Children.Add(teacher);
        grid.Children.Add(scope);
        grid.Children.Add(message);
        return grid;
    }

    private void AllocationDifferenceSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (_closed) return;
        UpdateCompactComparison();
    }

    private void AllocationDifferenceKeyDown(object? sender, KeyEventArgs args)
    {
        if (_closed) return;
        switch (args.Key)
        {
            case Key.Enter:
                if (AllocationDifferences.SelectedItem is AllocationDifference chosen)
                {
                    args.Handled = true;
                    FocusAllocationIssue(chosen);
                    UpdateCompactComparison();
                }
                break;
            case Key.F8:
                args.Handled = JumpToBlockingAllocation();
                break;
        }
    }

    private void UpdateCompactComparison()
    {
        var row = AllocationDifferences.SelectedItem as AllocationDifference;
        var hasValues = row is not null && (row.Current is not null || row.After is not null);
        var detail = hasValues ? $"现在：{row!.BeforeText}　→　提交后：{row.AfterText}" : "";
        AllocationCompactComparison.Text = detail;
        AllocationCompactComparison.IsVisible = _compact && detail.Length > 0;
        AutomationProperties.SetName(AllocationCompactComparison, row is null
            ? "所选差异"
            : detail.Length > 0 ? $"{row.Teacher}：{detail}" : $"{row.Teacher} {row.ActionLabel}");
    }

    private bool JumpToBlockingAllocation()
    {
        var target = _reviewRows.FirstOrDefault(item => item.Action == "BLOCK" || item.NeedsReview);
        if (target is null) return false;
        if (AllocationFilter.SelectedIndex != BlockingFilter) AllocationFilter.SelectedIndex = BlockingFilter;
        AllocationDifferences.SelectedItem = target;
        if (!ReferenceEquals(AllocationDifferences.SelectedItem, target)) return false;
        FocusAllocationIssue(target);
        UpdateCompactComparison();
        return true;
    }

    private void RevealArchivedIdentity()
    {
        if (_compact && _compactDetail) RevealAllocationAccess(false);
    }

    private void OpenEditorClicked(object? sender, RoutedEventArgs args)
    {
        if (_closed || !_compact || _compactDetail || SelectedTeacher is null) return;
        _compactDetail = true;
        ArrangeAllocationLayout(force: true);
        FocusAccessRows();
    }

    private void BackToTableClicked(object? sender, RoutedEventArgs args)
    {
        if (_closed || !_compactDetail) return;
        _compactDetail = false;
        ArrangeAllocationLayout(force: true);
        RestoreTeacherFocus();
    }

    // 本体在定位班级访问行时调用；宽窗两栏天然并排，空操作。
    private void RevealAllocationAccess(bool reveal = true)
    {
        if (_closed || !_compact) return;
        if (!reveal)
        {
            if (!_compactDetail) return;
            _compactDetail = false;
            ArrangeAllocationLayout(force: true);
            return;
        }
        if (AllocationTabs.SelectedIndex != 0) AllocationTabs.SelectedIndex = 0;
        if (_compactDetail) return;
        _compactDetail = true;
        ArrangeAllocationLayout(force: true);
    }

    private void FocusAccessRows()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed || !_compact || !_compactDetail) return;
            var target = AllocationAccessRows.GetVisualDescendants().OfType<Control>()
                .FirstOrDefault(row => row.Focusable && row.IsEffectivelyVisible && row.IsEffectivelyEnabled);
            target ??= AllocationBackToTable;
            target.BringIntoView();
            target.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private void RestoreTeacherFocus()
    {
        var item = AllocationTeachers.SelectedItem;
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed) return;
            if (item is not null && AllocationTeachers.ContainerFromItem(item) is Control container)
            {
                container.BringIntoView();
                if (container.IsEffectivelyVisible && container.IsEffectivelyEnabled) container.Focus(NavigationMethod.Tab);
                return;
            }
            AllocationTeachers.BringIntoView();
            AllocationTeachers.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    // 由宽变窄收起详情时，只有焦点原本在详情里才交回教师表。
    private void RescueHiddenAccessFocus(bool editorWasVisible)
    {
        if (!editorWasVisible || AllocationAccessEditor.IsVisible) return;
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not Visual focused) return;
        if (!ReferenceEquals(focused, AllocationAccessEditor) &&
            !focused.GetVisualAncestors().Any(row => ReferenceEquals(row, AllocationAccessEditor))) return;
        RestoreTeacherFocus();
    }

    private void SyncOpenEditorButton()
    {
        AllocationOpenEditor.IsVisible = _compact && !_compactDetail && SelectedTeacher is not null;
    }

    private void HookCompactBehaviour()
    {
        if (_hooked) return;
        _hooked = true;
        AutomationProperties.SetLiveSetting(AllocationCompactComparison, AutomationLiveSetting.Polite);
        AllocationTeachers.SelectionChanged += (_, _) => SyncOpenEditorButton();
        AllocationTabs.SelectionChanged += (_, _) => TrackAllocationTab();
    }

    // 窄窗开着详情时切到别的 Tab，详情要收起。
    private void TrackAllocationTab()
    {
        var index = AllocationTabs.SelectedIndex;
        if (index == _lastTab) return;
        _lastTab = index;
        if (_closed || !_compact || !_compactDetail) return;
        if (index == 0) return;
        _compactDetail = false;
        ArrangeAllocationLayout(force: true);
    }

    private void AnnounceAllocationPanes()
    {
        var heading = AllocationEditorHeading.Text?.Trim();
        AutomationProperties.SetName(AllocationOpenEditor,
            string.IsNullOrEmpty(heading) ? "展开班级访问" : $"展开 {heading} 的班级访问");
        AutomationProperties.SetName(AllocationBackToTable, "返回教师表");
    }
}
