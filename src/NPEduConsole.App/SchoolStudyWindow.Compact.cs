using System.Globalization;
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
//   · 返回入口不随详情滚动，离开详情把焦点还给原班级。
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
        StudyWeekNavigation.IsVisible = StudyDays.IsVisible = !detail;
        StudyRoomDate.IsVisible = StudyRoomBackButton.IsVisible = detail;
        SchoolStudyClose.Content = detail ? "退出学校自习" : "返回";
        StudyCalendarContent.RowSpacing = detail ? 0 : 8;
        StudyDetailPane.Padding = detail ? new Thickness(12, 8) : new Thickness(12);

        StudyDetailActions.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        StudyDetailActions.ColumnSpacing = compact ? 0 : 8;
        StudyDetailActions.RowSpacing = compact ? 6 : 0;
        Grid.SetColumn(StudyEvidenceActions, compact ? 0 : 1);
        Grid.SetRow(StudyEvidenceActions, compact ? 1 : 0);
        StudySettingsActions.Columns = StudyRecordsActions.Columns = compact ? 3 : 1;

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
        StudyDetailScroll.Offset = default;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_closed && _compactDetail && StudyRoomBackButton.IsEffectivelyVisible)
                StudyRoomBackButton.Focus(NavigationMethod.Tab);
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
            if (_closed || _compactDetail || !ReferenceEquals(StudyRooms.SelectedItem, item)) return;
            if (item is null)
            {
                StudyRooms.Focus(NavigationMethod.Tab);
                return;
            }
            StudyRooms.ScrollIntoView(item);
            Dispatcher.UIThread.Post(() =>
            {
                if (_closed || _compactDetail || !ReferenceEquals(StudyRooms.SelectedItem, item)) return;
                if (StudyRooms.ContainerFromItem(item) is Control container && container.Focus(NavigationMethod.Tab)) return;
                StudyRooms.Focus(NavigationMethod.Tab);
            }, DispatcherPriority.Background);
        }, DispatcherPriority.Loaded);
    }

    private void AnnounceDetail()
    {
        var title = StudyRoomTitle.Text?.Trim();
        var name = string.IsNullOrEmpty(title) ? "本班详情" : title;
        StudyRoomDate.Text = _date.ToString("M 月 d 日 dddd", CultureInfo.GetCultureInfo("zh-CN")) + " · 学校时间";
        AutomationProperties.SetName(StudyRoomBackButton, $"返回当天班级列表 · {name}");
        AutomationProperties.SetName(StudyRoomOpenButton, string.IsNullOrEmpty(title) ? "打开本班详情" : $"打开 {name} 的详情");
    }
}
