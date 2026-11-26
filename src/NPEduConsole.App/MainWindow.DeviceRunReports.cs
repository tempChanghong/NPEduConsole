using Avalonia.Controls;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private DeviceDiagnosticsWindow? _deviceDiagnosticsWindow;

    private async void OpenScreenReportsClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _school is not { } school || _classroom is not { TermId: { } term } room ||
            room.Screens.SingleOrDefault(row => row.Id == _detailScreenId) is not { } screen ||
            sender is not Control anchor || _academicPages.Count != 0) return;
        var device = _device is { } selected && selected.ScreenId == screen.Id ? screen.Devices.FirstOrDefault(row => row.Id == selected.Device.Id) : null;
        device ??= screen.Devices.Count == 1 ? screen.Devices[0] : null;
        if (device is null) { SetWorkspaceMessage("请先选择这台大屏的原生设备。", isError: false); return; }
        var view = new DeviceRunReportsView(client, new(school.Id, term, room.Id, screen.Id, device.Id),
            school.Name, room.Name, screen.Name, device.Name)
        { SessionExpired = error => { if (_client == client && _school == school) ReturnToLoginIfExpired(error); } };
        if (await ShowDeviceRunReportsAsync(view))
            await RestoreDeviceEvidenceSourceAsync(client, school, room, screen.Id, anchor, view.Revoked);
    }

    private async Task<bool> ShowDeviceRunReportsAsync(DeviceRunReportsView view)
    {
        view.CloseRequested = () => ReturnFromAcademicPage(view);
        view.OpenFieldPage = field => ShowsAcademicPage(view) ? ShowDeviceFieldActionAsync(field) : Task.FromResult(false);
        var returned = PushAcademicPage(view, () => true, view.End);
        await view.StartAsync();
        return await returned;
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
        _deviceDiagnosticsWindow?.Close();
    }
}
