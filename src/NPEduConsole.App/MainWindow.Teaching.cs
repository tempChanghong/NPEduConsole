using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private TeachingWindow? _teachingWindow;

    private async void OpenTeachingClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _school is not { } school ||
            _classroom is not { IsActive: true, TermId: { } termId } room) return;
        await ShowTeachingAsync(client, school, termId, room.Id, null);
        if (_client == client && _school == school && _classroom?.Id == room.Id) OpenTeachingButton.Focus();
    }

    private async Task ShowTeachingAsync(ConsoleClient client, School school, string termId, string classId, string? subjectId)
    {
        if (_teachingWindow is not null || _client != client || _school != school) return;
        var window = new TeachingWindow(client, school, termId, classId, subjectId);
        _teachingWindow = window;
        window.SessionExpired += error =>
        {
            if (_teachingWindow == window && _client == client) ReturnToLoginIfExpired(error);
        };
        try { await window.ShowDialog(this); }
        finally { if (_teachingWindow == window) _teachingWindow = null; }
    }

    private void CloseTeaching()
    {
        var window = _teachingWindow;
        _teachingWindow = null;
        window?.CloseForScopeChange();
    }
}
