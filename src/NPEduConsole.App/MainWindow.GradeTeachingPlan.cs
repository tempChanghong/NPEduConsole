using Avalonia.Controls;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private GradeTeachingPlanWindow? _gradeTeachingPlanWindow;
    private ClassSubjectRulesWindow? _classRulesWindow;

    private async void OpenCurrentClassRulesClicked(object? sender, RoutedEventArgs args)
    {
        if (_classRulesWindow is not null || _client is not { } client || _school is not { } school ||
            _classroom is not { TermId: { } term, IsActive: true } room || _page != ShellPage.Classes) return;
        var window = new ClassSubjectRulesWindow(client, school, term, room.Id, currentEntry: true)
        {
            AccessChanged = error => { if (_client == client && _school == school) ReturnToLoginIfExpired(error); }
        };
        _classRulesWindow = window;
        try { await window.ShowDialog(this); }
        finally { if (_classRulesWindow == window) _classRulesWindow = null; }
        if (_client != client || _school != school) return;
        if (window.WorkspaceChanged) await SchoolOverviewWorkspace.RefreshAsync(restoreTask: _showSchoolOverview);
        if (_classroom?.Id == room.Id && _page == ShellPage.Classes) OpenClassRulesButton.Focus();
    }

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
        _classRulesWindow?.CloseForScopeChange();
        _classRulesWindow = null;
        var window = _gradeTeachingPlanWindow;
        _gradeTeachingPlanWindow = null;
        window?.CloseForScopeChange();
    }
}
