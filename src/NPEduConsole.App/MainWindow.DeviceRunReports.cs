using Avalonia.Controls;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private DeviceRunReportsWindow? _deviceRunReportsWindow;
    private DeviceDiagnosticsWindow? _deviceDiagnosticsWindow;

    private async void OpenScreenReportsClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _school is not { } school || _classroom is not { TermId: { } term } room ||
            room.Screens.SingleOrDefault(row => row.Id == _detailScreenId) is not { } screen ||
            sender is not Control anchor || _deviceRunReportsWindow is not null) return;
        var device = _device is { } selected && selected.ScreenId == screen.Id ? screen.Devices.FirstOrDefault(row => row.Id == selected.Device.Id) : null;
        device ??= screen.Devices.Count == 1 ? screen.Devices[0] : null;
        if (device is null) { SetWorkspaceMessage("请先选择这台大屏的原生设备。", isError: false); return; }
        var window = new DeviceRunReportsWindow(client, new(school.Id, term, room.Id, screen.Id, device.Id),
            school.Name, room.Name, screen.Name, device.Name)
        { SessionExpired = error => { if (_client == client && _school == school) ReturnToLoginIfExpired(error); } };
        _deviceRunReportsWindow = window;
        window.Closed += (_, _) => { if (_deviceRunReportsWindow == window) _deviceRunReportsWindow = null; };
        try { await window.ShowDialog(this); }
        finally { if (_deviceRunReportsWindow == window) _deviceRunReportsWindow = null; }
        if (_client != client || _school != school || _deviceRunReportsWindow is not null) return;
        if (window.Revoked) await LoadSchoolAsync(school, room.Id, selectDefaultDevice: false);
        if (_client != client || _school != school) return;
        if (anchor.IsEffectivelyVisible && anchor.IsEffectivelyEnabled && anchor.Focus()) anchor.BringIntoView();
        else if (_overviewScreenCards.TryGetValue(screen.Id, out var card)) { card.BringIntoView(); card.Focus(); }
    }

    private async Task ShowDeviceDiagnosticsAsync(string search, Control anchor)
    {
        if (_client is not { } client || _school is not { } school || _deviceDiagnosticsWindow is not null) return;
        var window = new DeviceDiagnosticsWindow(client, school, search)
        { SessionExpired = error => { if (_client == client && _school == school) ReturnToLoginIfExpired(error); } };
        _deviceDiagnosticsWindow = window;
        window.Closed += (_, _) => { if (_deviceDiagnosticsWindow == window) _deviceDiagnosticsWindow = null; };
        try { await window.ShowDialog(this); }
        finally { if (_deviceDiagnosticsWindow == window) _deviceDiagnosticsWindow = null; }
        if (_client == client && _school == school && _deviceDiagnosticsWindow is null && anchor.IsEffectivelyVisible) anchor.Focus();
    }

    private void CloseDeviceRunReports()
    {
        CloseDeviceConnections();
        CloseClassroomActionPolicy();
        _deviceRunReportsWindow?.Close();
        _deviceDiagnosticsWindow?.Close();
    }
}
