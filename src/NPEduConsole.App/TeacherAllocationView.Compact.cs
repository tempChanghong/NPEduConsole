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
// 窄窗的前后权限跟随选中差异行一起滚动。
public sealed partial class TeacherAllocationView
{
    private const double CompactWidth = 940;

    private bool _compact;
    private bool _compactDetail;
    private bool _arranged;
    private bool _lastDetail;
    private bool _hooked;
    private string? _locatedIssueKey;
    private IDataTemplate? _wideDifferenceTemplate;

    private void ArrangeAllocationLayout(bool force = false)
    {
        if (_closed || _client is null) return;
        HookCompactBehaviour();

        var compact = Bounds.Width > 0 && Bounds.Width < CompactWidth;
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
        ArrangeDirectory(compact, AllocationTeacherDirectoryLayout, AllocationTeacherDirectoryList,
            AllocationTeacherDirectoryDetail, AllocationOpenTeacherDetails, AllocationBackFromTeacherDetails);
        ArrangeDirectory(compact, AllocationSpaceDirectoryLayout, AllocationSpaceDirectoryList,
            AllocationSpaceDirectoryDetail, AllocationOpenSpaceDetails, AllocationBackFromSpaceDetails);

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
            ColumnDefinitions = new ColumnDefinitions("76,1.4*,1.4*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            ColumnSpacing = 6,
            RowSpacing = 2,
            Margin = new Thickness(7, 4, 7, 4),
        };
        var state = new TextBlock { Text = row?.ActionLabel ?? "", Classes = { "label", "allocation-review-state" } };
        state.Classes.Set("stale", row?.NeedsReview == true);
        var teacher = new TextBlock { Text = row?.Teacher ?? "", TextWrapping = TextWrapping.Wrap };
        var scope = new TextBlock { Text = row?.Scope ?? "", TextWrapping = TextWrapping.Wrap };
        var message = new TextBlock
        {
            Text = row?.Message ?? "",
            Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap,
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
        if (row is not null && (row.Current is not null || row.After is not null))
        {
            var values = new StackPanel
            {
                Classes = { "allocation-inline-values" },
                Children =
                {
                    Description("现在　" + row.BeforeText),
                    Description("提交后　" + row.AfterText)
                }
            };
            Grid.SetRow(values, 2);
            Grid.SetColumn(values, 1);
            Grid.SetColumnSpan(values, 2);
            grid.Children.Add(values);
        }
        return grid;
    }

    private void AllocationKeyDown(object? sender, KeyEventArgs args)
    {
        if (_closed || _busy || _discard) return;
        if (_stage is Stage.ByTeacher or Stage.BySpace)
        {
            if (!_compact) return;
            if (args.Key == Key.Escape && _showDirectoryDetail)
            {
                DirectoryListClicked(null, args);
                args.Handled = true;
            }
            else if (args.Key == Key.Enter && !_showDirectoryDetail)
            {
                if (_stage == Stage.ByTeacher && AllocationDirectoryTeachers.IsKeyboardFocusWithin)
                {
                    OpenTeacherDetailsClicked(null, args);
                    args.Handled = true;
                }
                else if (_stage == Stage.BySpace && AllocationDirectorySpaces.IsKeyboardFocusWithin)
                {
                    OpenSpaceDetailsClicked(null, args);
                    args.Handled = true;
                }
            }
            return;
        }
        if (_stage != Stage.Review) return;
        if (args.Key == Key.F8)
        {
            args.Handled = JumpToBlockingAllocation();
        }
        else if (args.Key == Key.Enter && AllocationDifferences.IsKeyboardFocusWithin &&
                 AllocationDifferences.SelectedItem is AllocationDifference chosen)
        {
            FocusAllocationIssue(chosen);
            args.Handled = true;
        }
    }

    private void ArrangeDirectory(bool compact, Grid layout, Grid list, Grid detail, Button open, Button back)
    {
        list.IsVisible = !compact || !_showDirectoryDetail;
        detail.IsVisible = !compact || _showDirectoryDetail;
        layout.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(3, GridUnitType.Star);
        layout.ColumnSpacing = compact ? 0 : 9;
        Grid.SetColumn(detail, compact ? 0 : 1);
        open.IsVisible = compact && !_showDirectoryDetail;
        back.IsVisible = compact && _showDirectoryDetail;
    }

    private bool JumpToBlockingAllocation()
    {
        var issues = _reviewRows.Where(item => item.Action == "BLOCK" || item.NeedsReview).ToArray();
        if (issues.Length == 0) return false;
        var last = Array.FindIndex(issues, item => item.Key == _locatedIssueKey);
        var target = last < 0 && AllocationDifferences.SelectedItem is AllocationDifference selected && issues.Contains(selected)
            ? selected : issues[(last + 1) % issues.Length];
        if (!AllocationDifferences.Items.Contains(target)) AllocationFilter.SelectedIndex = 0;
        AllocationDifferences.SelectedItem = target;
        if (!ReferenceEquals(AllocationDifferences.SelectedItem, target)) return false;
        FocusAllocationIssue(target);

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
        if (_stage != Stage.Edit) SetStage(Stage.Edit);
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
        AllocationTeachers.SelectionChanged += (_, _) => SyncOpenEditorButton();
    }

    private void AnnounceAllocationPanes()
    {
        var heading = AllocationEditorHeading.Text?.Trim();
        AutomationProperties.SetName(AllocationOpenEditor,
            string.IsNullOrEmpty(heading) ? "展开班级访问" : $"展开 {heading} 的班级访问");
        AutomationProperties.SetName(AllocationBackToTable, "返回教师表");
    }
}
