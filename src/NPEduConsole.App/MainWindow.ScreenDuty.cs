using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private ScreenDutyView? _screenDutyView;

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
        if (_screenDutyView is not null || _client != client || _school != school) return;
        var source = _page;
        TermPreparationView.ScreenDestination? arranged = null;
        var view = new ScreenDutyView(client, school, bindingId, classId, inactiveTermId, scopeTermId)
        {
            OpenLegacyBinding = id => SchoolAdminBrowser.Link(adminUrl, school.Id, inactiveTermId ?? scopeTermId, "screens", id, classId) is { } link &&
                (DesktopBrowserLauncher ?? SchoolAdminBrowser.Open)(link),
            SessionExpired = error => { if (_client == client && _school == school) ReturnToLoginIfExpired(error); },
            OpenPlacement = async row =>
            {
                if (_screenDutyView is not { } active || !ShowsAcademicPage(active) ||
                    row.PlacementTermId is not { } term || row.ActivationRequestId is not { } round)
                    return false;
                arranged = await ShowTermScreenPlacementAsync(this, client, school, term, row.Id, round);
                return arranged is not null;
            }
        };
        _screenDutyView = view;
        view.CloseRequested = () => ReturnFromAcademicPage(view);
        var exit = PushAcademicPage(view, view.CanClose, () =>
        {
            view.End();
            if (_screenDutyView == view) _screenDutyView = null;
        });
        await view.StartAsync();
        if (!await exit || _client != client || _school != school || _page != source) return;
        if (view.Changed && _page is ShellPage.Classes or ShellPage.Screen)
        {
            var target = bindingId is not null && view.Detail is { } detail && detail.Id == bindingId
                ? detail.Room.Id : _classroom?.Id;
            await LoadSchoolAsync(school, target, selectDefaultDevice: false);
        }
        if (_client != client || _school != school || _page != source) return;
        await SchoolOverviewWorkspace.RefreshAsync(restoreTask: restoreTask);
        if (_client != client || _school != school || _page != source || _screenDutyView is not null) return;
        if (arranged is not null)
        {
            await OpenTermResultDestinationAsync(client, school, arranged);
            return;
        }
        if (view.NativeTarget is { Active: true } native)
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
        if (!restoreTask)
            Dispatcher.UIThread.Post(() =>
            {
                if (_client != client || _school != school || _page != source || _screenDutyView is not null) return;
                if (source == ShellPage.Devices && bindingId is not null) DeviceDirectory.RestorePosition(bindingId);
                else
                {
                    var originalAction = source == ShellPage.Classes && bindingId is not null &&
                        _overviewScreenCards.TryGetValue(bindingId, out var card)
                        ? card.GetVisualDescendants().OfType<Button>().FirstOrDefault(button =>
                            AutomationProperties.GetAutomationId(button) == "ClassScreenDuty-" + bindingId)
                        : anchor;
                    if (originalAction is not { IsEffectivelyVisible: true, IsEffectivelyEnabled: true }) return;
                    originalAction.BringIntoView();
                    originalAction.Focus(NavigationMethod.Tab);
                }
            }, DispatcherPriority.Loaded);
    }

    private void CloseScreenDuty()
    {
        _screenDutyView?.End();
    }
}
