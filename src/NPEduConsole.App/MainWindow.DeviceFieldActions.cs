using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;
using CoreScreen = NPEduConsole.Core.Screen;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private DeviceFieldActionWindow? _fieldActionWindow;

    private async void OpenScreenFieldClicked(object? sender, RoutedEventArgs args)
    {
        if (_classroom is { } room && room.Screens.SingleOrDefault(row => row.Id == _detailScreenId) is { } screen && sender is Control anchor)
            await ShowScreenFieldAsync(room, screen, anchor);
    }

    private async Task ShowScreenFieldAsync(Classroom room, CoreScreen screen, Control anchor)
    {
        if (_client is not { } client || _school is not { } school || room.TermId is not { } term || _fieldActionWindow is not null) return;
        var device = _device is { } choice && choice.ScreenId == screen.Id ? screen.Devices.FirstOrDefault(row => row.Id == choice.Device.Id) : null;
        device ??= screen.Devices.Count == 1 ? screen.Devices[0] : null;
        if (device is null)
        {
            SetWorkspaceMessage(screen.Devices.Count > 1 ? "请先选择这台大屏的原生设备。" : "这台大屏暂无有效原生配对。", isError: false);
            return;
        }

        var scope = new DeviceFieldScope(school.Id, term, room.Id, screen.Id, device.Id);
        var window = new DeviceFieldActionWindow(client, scope, school.Name, room.Name, screen.Name, device.Name)
        {
            SessionExpired = error => { if (_client == client && _school == school) ReturnToLoginIfExpired(error); }
        };
        _fieldActionWindow = window;
        try { await window.ShowDialog(this); }
        finally { if (_fieldActionWindow == window) _fieldActionWindow = null; }
        if (_client != client || _school != school) return;
        if (window.Revoked) await LoadSchoolAsync(school, room.Id, selectDefaultDevice: false);
        if (_client == client && _school == school)
            Dispatcher.UIThread.Post(() =>
            {
                if (_client != client || _school != school) return;
                if (anchor.IsEffectivelyVisible && anchor.IsEffectivelyEnabled && anchor.Focus()) anchor.BringIntoView();
                else if (_overviewScreenCards.TryGetValue(screen.Id, out var card)) { card.BringIntoView(); card.Focus(); }
            }, DispatcherPriority.Loaded);
    }

    private void CloseScreenFieldActions() => _fieldActionWindow?.Close();
}
