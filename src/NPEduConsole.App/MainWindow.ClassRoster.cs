using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private ClassRosterWindow? _classRosterWindow;

    private async void OpenClassRosterClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is { } client && _school is { } school && _classroom is { } room)
            await ShowClassRosterAsync(client, school, room);
    }

    private async Task<bool> ShowClassRosterAsync(ConsoleClient client, School school, Classroom room)
    {
        if (_classRosterWindow is not null || _client != client || _school != school || _classroom != room) return false;
        var window = new ClassRosterWindow(client, school.Id, room.Id, school.Name, ClassroomName(room));
        _classRosterWindow = window;
        window.Closed += (_, _) =>
        {
            if (_classRosterWindow == window) _classRosterWindow = null;
        };
        window.SessionExpired += error =>
        {
            if (_classRosterWindow == window && _client == client) ReturnToLoginIfExpired(error);
        };
        try { await window.ShowDialog(this); }
        finally
        {
            if (_classRosterWindow == window) _classRosterWindow = null;
            if (_classRosterWindow is null && _client == client && _school == school && _classroom == room) OpenClassRosterButton.Focus();
        }
        return window.HasConfirmedSave;
    }

    private void CloseClassRoster()
    {
        var window = _classRosterWindow;
        _classRosterWindow = null;
        window?.CloseForScopeChange();
    }
}
