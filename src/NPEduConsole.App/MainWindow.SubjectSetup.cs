using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private readonly SubjectSetupFlow _subjectSetup = new();

    private async void OpenSubjectSetupClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _school is not { } school ||
            _classroom is not { IsActive: true, TermId: { } termId } room) return;
        await _subjectSetup.OpenAsync(this, client, school, termId, room.Id, [room.Id], error =>
        {
            if (_client == client && _school == school) ReturnToLoginIfExpired(error);
        });
        if (_client == client && _school == school && _classroom?.Id == room.Id) OpenSubjectSetupButton.Focus();
    }
}
