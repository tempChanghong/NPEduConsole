using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private TeacherAccountsWindow? _teacherAccountsWindow;

    private async void OpenTeacherAccountsClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _school is not { } school || _page != ShellPage.Classes) return;
        await ShowTeacherAccountsAsync(client, school, null);
        if (_client == client && _school == school && _page == ShellPage.Classes) OpenTeacherAccountsButton.Focus();
    }

    private async Task ShowTeacherAccountsAsync(ConsoleClient client, School school, string? accountId)
    {
        if (_teacherAccountsWindow is not null || _client != client || _school != school) return;
        var window = new TeacherAccountsWindow(client, school, accountId)
        {
            SessionExpired = error => { if (_client == client && _school == school) ReturnToLoginIfExpired(error); }
        };
        _teacherAccountsWindow = window;
        try { await window.ShowDialog(this); }
        finally { if (_teacherAccountsWindow == window) _teacherAccountsWindow = null; }
        if (_client != client || _school != school || window.Destination is not { } next) return;
        if (next.Kind == "teaching")
            await ShowTeachingAsync(client, school, next.TermId, next.TargetId, next.SubjectId, next.AccountId);
        else if (next.Kind is "grades" or "classes")
            await ShowStaffResponsibilitiesAsync(client, school, next.TermId, next.Kind, next.TargetId, next.AccountId);
    }

    private void CloseTeacherAccounts()
    {
        var window = _teacherAccountsWindow;
        _teacherAccountsWindow = null;
        window?.CloseForScopeChange();
    }
}
