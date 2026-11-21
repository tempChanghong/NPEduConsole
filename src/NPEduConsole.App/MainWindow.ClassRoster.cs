using Avalonia.Interactivity;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private ClassRosterWindow? _classRosterWindow;

    private async void OpenClassRosterClicked(object? sender, RoutedEventArgs args)
    {
        if (_classRosterWindow is not null || _client is not { } client || _school is not { } school || _classroom is not { } room) return;
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
    }

    private void CloseClassRoster()
    {
        var window = _classRosterWindow;
        _classRosterWindow = null;
        window?.CloseForScopeChange();
    }
}
