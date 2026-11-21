using Avalonia.Controls;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private GradeTeachingPlanWindow? _gradeTeachingPlanWindow;

    private async void OpenGradeTeachingPlanClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _school is not { } school || _page != ShellPage.Classes) return;
        await ShowGradeTeachingPlanAsync(this, client, school);
        if (_client == client && _school == school && _page == ShellPage.Classes) OpenGradeTeachingPlanButton.Focus();
    }

    private async Task<bool> ShowGradeTeachingPlanAsync(Window owner, ConsoleClient client, School school, string? grade = null,
        string? subject = null, string? group = null)
    {
        if (_gradeTeachingPlanWindow is not null || _client != client || _school != school) return false;
        var window = new GradeTeachingPlanWindow(client, school, grade, subject, group)
        {
            SessionExpired = error => { if (_client == client && _school == school) ReturnToLoginIfExpired(error); }
        };
        _gradeTeachingPlanWindow = window;
        window.Closed += (_, _) => { if (_gradeTeachingPlanWindow == window) _gradeTeachingPlanWindow = null; };
        try { await window.ShowDialog(owner); }
        finally { if (_gradeTeachingPlanWindow == window) _gradeTeachingPlanWindow = null; }
        if (window.WorkspaceChanged && _client == client && _school == school)
            await SchoolOverviewWorkspace.RefreshAsync(restoreTask: _showSchoolOverview);
        return window.WorkspaceChanged;
    }

    private void CloseGradeTeachingPlan()
    {
        var window = _gradeTeachingPlanWindow;
        _gradeTeachingPlanWindow = null;
        window?.CloseForScopeChange();
    }
}
