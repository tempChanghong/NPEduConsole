using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;
using CoreScreen = NPEduConsole.Core.Screen;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private async void OpenScreenFieldClicked(object? sender, RoutedEventArgs args)
    {
        if (_classroom is { } room && room.Screens.SingleOrDefault(row => row.Id == _detailScreenId) is { } screen && sender is Control anchor)
            await ShowScreenFieldAsync(room, screen, anchor);
    }

    private async Task ShowScreenFieldAsync(Classroom room, CoreScreen screen, Control anchor)
    {
        if (_client is not { } client || _school is not { } school || room.TermId is not { } term || _academicPages.Count != 0) return;
        var device = _device is { } choice && choice.ScreenId == screen.Id ? screen.Devices.FirstOrDefault(row => row.Id == choice.Device.Id) : null;
        device ??= screen.Devices.Count == 1 ? screen.Devices[0] : null;
        if (device is null)
        {
            SetWorkspaceMessage(screen.Devices.Count > 1 ? "请先选择这台大屏的原生设备。" : "这台大屏暂无有效原生配对。", isError: false);
            return;
        }

        var scope = new DeviceFieldScope(school.Id, term, room.Id, screen.Id, device.Id);
        var view = new DeviceFieldActionView(client, scope, school.Name, room.Name, screen.Name, device.Name)
        {
            SessionExpired = error => { if (_client == client && _school == school) ReturnToLoginIfExpired(error); }
        };
        if (await ShowDeviceFieldActionAsync(view))
            await RestoreDeviceEvidenceSourceAsync(client, school, room, screen.Id, anchor, view.Revoked);
    }

    private async Task<bool> ShowDeviceFieldActionAsync(DeviceFieldActionView view)
    {
        view.CloseRequested = () => ReturnFromAcademicPage(view);
        view.OpenReportsPage = reports => ShowsAcademicPage(view) ? ShowDeviceRunReportsAsync(reports) : Task.FromResult(false);
        var returned = PushAcademicPage(view, view.CanClose, view.End);
        await view.StartAsync();
        return await returned;
    }

    private async Task RestoreDeviceEvidenceSourceAsync(ConsoleClient client, School school, Classroom room, string bindingId,
        Control anchor, bool revoked)
    {
        if (!StillAtSource()) return;
        if (revoked) await LoadSchoolAsync(school, room.Id, selectDefaultDevice: false);
        Dispatcher.UIThread.Post(() =>
        {
            if (!StillAtSource()) return;
            if (anchor.IsEffectivelyVisible && anchor.IsEffectivelyEnabled && anchor.Focus()) anchor.BringIntoView();
            else if (_overviewScreenCards.TryGetValue(bindingId, out var card)) { card.BringIntoView(); card.Focus(); }
        }, DispatcherPriority.Loaded);

        bool StillAtSource() => _client == client && _school == school && _page == ShellPage.Screen &&
            _classroom?.Id == room.Id && _classroom.TermId == room.TermId && _detailScreenId == bindingId && _academicPages.Count == 0;
    }
}
