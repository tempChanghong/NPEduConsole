using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace NPEduConsole.App;

// 窄窗布局与列表/详情切换。约定：
//   · 宽窗始终两栏（3*:2*），窄窗只留一栏（*:0），列宽原地改，不重挂控件；
//   · 窄窗要么显示班级列表，要么显示本班详情，靠 StudyDetailPane 的列位与可见性切换；
//   · StudyRooms 与它的 SelectedItem 在整个切换过程中始终是同一个实例；
//   · 进入详情把焦点交给详情首个按钮，返回把焦点还给列表里的同一个容器。
public sealed partial class SchoolStudyWindow : Window
{
    private const double CompactWidth = 900;

    private bool _compact;
    private bool _compactDetail;
    private bool _arranged;
    private bool _lastDetail;

    private void ArrangeStudyWindow(bool force = false)
    {
        if (_closed) return;
        var compact = ClientSize.Width < CompactWidth;
        if (!compact) _compactDetail = false;
        var detail = compact && _compactDetail;
        if (!force && _arranged && compact == _compact && detail == _lastDetail) return;

        var firstArrangement = !_arranged;
        var wasDetailVisible = StudyDetailPane.IsVisible;

        var columns = StudyMainColumns.ColumnDefinitions;
        if (columns.Count >= 2)
        {
            columns[0].Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(3, GridUnitType.Star);
            columns[1].Width = compact ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        }
        StudyMainColumns.ColumnSpacing = compact ? 0 : 10;

        Grid.SetColumn(StudyRoomPane, 0);
        Grid.SetColumn(StudyDetailPane, detail ? 0 : 1);
        StudyRoomPane.IsVisible = !detail;
        StudyDetailPane.IsVisible = !compact || detail;
        StudyRoomOpenButton.IsVisible = compact && !detail;
        StudyRoomBackButton.IsVisible = detail;

        _compact = compact;
        _lastDetail = detail;
        _arranged = true;

        if (!firstArrangement && wasDetailVisible && !StudyDetailPane.IsVisible) RestoreStudyRoomFocus();
        AnnounceDetail();
    }

    private void StudyRoomOpen(object? sender, RoutedEventArgs args)
    {
        if (_closed || !_compact || _compactDetail || Room is null) return;
        _compactDetail = true;
        ArrangeStudyWindow(force: true);
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed || !_compactDetail) return;
            if (StudyEditClassButton.IsEffectivelyVisible && StudyEditClassButton.IsEffectivelyEnabled)
                StudyEditClassButton.Focus(NavigationMethod.Tab);
            else if (StudyRoomBackButton.IsEffectivelyVisible) StudyRoomBackButton.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private void StudyRoomBack(object? sender, RoutedEventArgs args)
    {
        if (_closed || !_compactDetail) return;
        _compactDetail = false;
        // 详情面板在这一步变为不可见，ArrangeStudyWindow 会在同一轮里把焦点还给列表容器。
        ArrangeStudyWindow(force: true);
    }

    private void RestoreStudyRoomFocus()
    {
        if (_closed) return;
        var item = StudyRooms.SelectedItem;
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed || _compactDetail) return;
            if (item is not null && StudyRooms.ContainerFromItem(item) is Control container)
            {
                container.BringIntoView();
                if (container.IsEffectivelyVisible && container.IsEffectivelyEnabled) container.Focus(NavigationMethod.Tab);
                return;
            }
            StudyRooms.BringIntoView();
            StudyRooms.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private void AnnounceDetail()
    {
        var title = StudyRoomTitle.Text?.Trim();
        var name = string.IsNullOrEmpty(title) ? "本班详情" : title;
        AutomationProperties.SetName(StudyRoomBackButton, $"返回当天班级列表 · {name}");
        AutomationProperties.SetName(StudyRoomOpenButton, string.IsNullOrEmpty(title) ? "打开本班详情" : $"打开 {name} 的详情");
    }
}
