using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private StaffReviewWindow? _staffReview;

    private async void OpenStaffReviewClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _school is not { } school || _page != ShellPage.Classes) return;
        await ShowStaffReviewAsync(client, school);
        if (_client == client && _school == school && _page == ShellPage.Classes) OpenStaffReviewButton.Focus();
    }

    private async Task ShowStaffReviewAsync(ConsoleClient client, School school, string? term = null, string? key = null,
        string? grade = null, string? classroom = null)
    {
        if (_staffReview is not null || _client != client || _school != school) return;
        var window = new StaffReviewWindow(client, school, term, key, grade, classroom)
        {
            SessionExpired = error => { if (_client == client && _school == school) ReturnToLoginIfExpired(error); }
        };
        _staffReview = window;
        window.Closed += (_, _) => { if (_staffReview == window) _staffReview = null; };
        try { await window.ShowDialog(this); }
        finally { if (_staffReview == window) _staffReview = null; }
        if (window.WorkspaceChanged && _client == client && _school == school && !_showSchoolOverview)
            await SchoolOverviewWorkspace.RefreshAsync();
    }

    private void CloseStaffReview()
    {
        var window = _staffReview;
        _staffReview = null;
        window?.CloseForScopeChange();
    }
}
