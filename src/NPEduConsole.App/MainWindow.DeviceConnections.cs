using Avalonia.Controls;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private DeviceConnectionsWindow? _deviceConnectionsWindow;
    private DeviceConnectionsWindow.Pending _deviceConnectionsPending = new();

    private async void OpenScreenConnectionsClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is not Control anchor || _classroom is not { TermId: { } term } room ||
            _detailScreenId is not { } binding || !room.Screens.Any(screen => screen.Id == binding)) return;
        await ShowDeviceConnectionsAsync(anchor, new(term, room.Id, binding));
    }

    private async Task ShowDeviceConnectionsAsync(Control anchor, DeviceConnectionsWindow.InitialTarget? target = null)
    {
        if (_client is not { } client || _school is not { } school || _deviceConnectionsWindow is not null) return;
        var window = new DeviceConnectionsWindow(client, school, target, _deviceConnectionsPending)
        {
            AccessLost = error =>
            {
                if (_client == client && _school == school && !ReturnToLoginIfExpired(error))
                    SetWorkspaceMessage("本校管理访问已失效，请重新选择有权限的学校。", isError: true);
            }
        };
        _deviceConnectionsWindow = window;
        try { await window.ShowDialog(this); }
        finally { if (_deviceConnectionsWindow == window) _deviceConnectionsWindow = null; }
        if (_client != client || _school != school || _deviceConnectionsWindow is not null) return;
        if (window.Changed) await SchoolOverviewWorkspace.RefreshAsync();
        if (_client == client && _school == school && anchor.IsEffectivelyVisible) anchor.Focus();
    }

    private void CloseDeviceConnections()
    {
        _deviceConnectionsWindow?.ClearAndClose();
        _deviceConnectionsPending = new();
    }
}
