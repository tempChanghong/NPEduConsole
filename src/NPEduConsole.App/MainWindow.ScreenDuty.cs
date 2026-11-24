using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private ScreenDutyWindow? _screenDutyWindow;

    private async void OpenScreenDutyClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _school is not { } school || _detailScreenId is not { } id) return;
        await ShowScreenDutyAsync(client, school, id, _classroom?.Id, sender as Control, scopeTermId: _classroom?.TermId);
    }

    private async void OpenClassScreenDirectoryClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _school is not { } school || _classroom is not { IsActive: true } room) return;
        await ShowScreenDutyAsync(client, school, null, room.Id, sender as Control, scopeTermId: room.TermId);
    }

    private async Task ShowScreenDutyAsync(ConsoleClient client, School school, string? bindingId, string? classId, Control? anchor = null,
        bool restoreTask = false, Func<ScreenDutyDetail, Task>? openNative = null, string? inactiveTermId = null, string? adminUrl = null,
        string? scopeTermId = null)
    {
        if (_screenDutyWindow is not null || _client != client || _school != school) return;
        TermPreparationView.ScreenDestination? arranged = null;
        var window = new ScreenDutyWindow(client, school, bindingId, classId, inactiveTermId, scopeTermId)
        {
            OpenLegacyBinding = id => SchoolAdminBrowser.Link(adminUrl, school.Id, inactiveTermId, "screens", id, classId) is { } link &&
                (DesktopBrowserLauncher ?? SchoolAdminBrowser.Open)(link),
            SessionExpired = error => { if (_client == client && _school == school) ReturnToLoginIfExpired(error); },
            OpenPlacement = async row =>
            {
                if (_screenDutyWindow is not { } owner || row.PlacementTermId is not { } term || row.ActivationRequestId is not { } round)
                    return false;
                arranged = await ShowTermScreenPlacementAsync(owner, client, school, term, row.Id, round);
                return arranged is not null;
            }
        };
        _screenDutyWindow = window;
        try { await window.ShowDialog(this); }
        finally { if (_screenDutyWindow == window) _screenDutyWindow = null; }
        if (_client != client || _school != school) return;
        if (window.Changed && _page is ShellPage.Classes or ShellPage.Screen)
        {
            var target = _page == ShellPage.Screen && window.Detail is { } detail && detail.Id == _detailScreenId
                ? detail.Room.Id : _classroom?.Id;
            await LoadSchoolAsync(school, target, selectDefaultDevice: false);
        }
        if (_client != client || _school != school) return;
        await SchoolOverviewWorkspace.RefreshAsync(restoreTask: restoreTask);
        if (_client != client || _school != school) return;
        if (arranged is not null)
        {
            await OpenTermResultDestinationAsync(client, school, arranged);
            return;
        }
        if (window.NativeTarget is { Active: true } native)
        {
            if (openNative is not null)
            {
                await openNative(native);
                return;
            }
            _overviewNavigation = null;
            await OpenShellTargetAsync(native.Room.Id, native.Id, native.Room.TermId);
            return;
        }
        if (_page == ShellPage.Devices && bindingId is not null) DeviceDirectory.RestorePosition(bindingId);
        else if (!restoreTask && anchor is not null)
            Dispatcher.UIThread.Post(() => { if (_client == client && _school == school && anchor.IsEffectivelyVisible) anchor.Focus(); });
    }

    private void CloseScreenDuty()
    {
        var window = _screenDutyWindow;
        _screenDutyWindow = null;
        window?.CloseForScopeChange();
    }
}
