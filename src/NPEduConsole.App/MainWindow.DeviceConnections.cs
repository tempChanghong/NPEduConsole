using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private DeviceConnectionsView.Pending _deviceConnectionsPending = new();

    private async void OpenScreenConnectionsClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is not Control anchor || _classroom is not { TermId: { } term } room ||
            room.Screens.SingleOrDefault(screen => screen.Id == _detailScreenId) is not { } screen) return;
        await ShowDeviceConnectionsAsync(anchor, new(term, room.Id, screen.Id, room.Name, screen.Name));
    }

    private async Task ShowDeviceConnectionsAsync(Control anchor, DeviceConnectionsView.InitialTarget? target = null)
    {
        if (_client is not { } client || _school is not { } school || _academicPages.Count != 0 ||
            _page is not (ShellPage.Devices or ShellPage.Screen)) return;
        var source = _page;
        var view = new DeviceConnectionsView(client, school, target, _deviceConnectionsPending);
        view.CloseRequested = () => ReturnFromAcademicPage(view);
        view.AccessLost = error =>
        {
            if (_client == client && _school == school && !ReturnToLoginIfExpired(error))
            {
                ReturnFromAcademicPage(view);
                const string message = "当前账号已无法管理本校大屏互联，请重新核对学校和权限。";
                if (source == ShellPage.Devices) DeviceDirectory.ShowNavigationMessage(message);
                else SetWorkspaceMessage(message, isError: true);
            }
        };
        var exit = PushAcademicPage(view, view.CanClose, view.End);
        await view.StartAsync();
        if (!await exit || _client != client || _school != school) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_client != client || _school != school || _page != source || _academicPages.Count != 0 ||
                target is not null && (_detailScreenId != target.ScreenId || _classroom?.Id != target.ClassId ||
                    _classroom.TermId != target.TermId) || !anchor.IsEffectivelyVisible || !anchor.IsEffectivelyEnabled) return;
            anchor.BringIntoView();
            anchor.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
        if (view.Changed) await SchoolOverviewWorkspace.RefreshAsync();
    }

    private void CloseDeviceConnections()
    {
        _deviceConnectionsPending = new();
    }
}
