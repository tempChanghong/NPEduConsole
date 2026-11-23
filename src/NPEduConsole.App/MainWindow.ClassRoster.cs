using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private ClassRosterView? _classRosterView;

    private async void OpenClassRosterClicked(object? sender, RoutedEventArgs args)
    {
        if (_page == ShellPage.Classes && _client is { } client && _school is { } school &&
            _classroom is { IsActive: true, TermId: not null } room)
            await ShowClassRosterAsync(client, school, room);
    }

    private async Task<bool> ShowClassRosterAsync(ConsoleClient client, School school, Classroom room)
    {
        if (_classRosterView is not null || _academicPages.Count != 0 || _client != client || _school != school ||
            _classroom != room || room.TermId is null) return false;
        var page = _page;
        var offset = ClassWorkspaceScroll.Offset;
        var view = _classRosterView = new ClassRosterView(client, school, room);
        bool returned;
        try { returned = await ShowRosterPageAsync(client, school, view); }
        finally
        {
            if (_classRosterView == view) _classRosterView = null;
        }
        if (returned && page == ShellPage.Classes)
            RestoreClassAction(client, school, room, OpenClassRosterButton, () => ClassWorkspaceScroll.Offset = offset);
        return returned && view.HasConfirmedSave;
    }

    private async Task<bool> ShowRosterPageAsync(ConsoleClient client, School school, ClassRosterView view)
    {
        view.SessionExpired += error => AcademicScopeUnavailable(error, client, school);
        view.CloseRequested = () => ReturnFromAcademicPage(view);
        var exit = PushAcademicPage(view, view.CanClose, view.End);
        await view.StartAsync();
        return await exit;
    }

    private void RestoreClassAction(ConsoleClient client, School school, Classroom room, Control action, Action? beforeFocus = null) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_client != client || _school != school || _page != ShellPage.Classes || _academicPages.Count != 0 ||
                _classroom?.Id != room.Id || _classroom.TermId != room.TermId || !action.IsEffectivelyVisible || !action.IsEffectivelyEnabled) return;
            beforeFocus?.Invoke();
            action.BringIntoView();
            action.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);

    private void CloseClassRoster()
    {
        var view = _classRosterView;
        _classRosterView = null;
        if (view is not null && ShowsAcademicPage(view)) CloseAcademicWorkspace();
    }
}
