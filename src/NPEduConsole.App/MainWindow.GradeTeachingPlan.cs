using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
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
        if (_client is not { } client || _school is not { } school || _page != ShellPage.Classes || _academicPages.Count != 0) return;
        var offset = ClassWorkspaceScroll.Offset;
        await ShowGradeTeachingPlanAsync(client, school);
        Dispatcher.UIThread.Post(() =>
        {
            if (_client != client || _school != school || _page != ShellPage.Classes || _academicPages.Count != 0 ||
                !OpenGradeTeachingPlanButton.IsEffectivelyVisible) return;
            ClassWorkspaceScroll.Offset = offset;
            OpenGradeTeachingPlanButton.BringIntoView();
            OpenGradeTeachingPlanButton.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private async Task<bool> ShowGradeTeachingPlanAsync(ConsoleClient client, School school, string? grade = null,
        string? subject = null, string? group = null, string? term = null, string? room = null, string? teacher = null,
        string? teachingIssue = null, bool requireInitialTarget = false, string? requiredCurrentTermId = null)
    {
        if (_client != client || _school != school || _academicPages.Any(page => page.Content is GradeTeachingPlanView)) return false;
        var view = new GradeTeachingPlanView(client, school, grade, subject, group, term, room, teacher,
            requireInitialTarget: requireInitialTarget || teachingIssue is not null || requiredCurrentTermId is not null,
            teachingIssue: teachingIssue, inWorkspace: true, requiredCurrentTermId: requiredCurrentTermId)
        {
            SessionExpired = error => AcademicScopeUnavailable(error, client, school)
        };
        view.CloseRequested = () => ReturnFromAcademicPage(view);
        var exit = PushAcademicPage(view, view.CanClose, view.End);
        await view.StartAsync();
        if (!await exit) return false;
        if (view.WorkspaceChanged && _client == client && _school == school && !_showSchoolOverview)
            await SchoolOverviewWorkspace.RefreshAsync(restoreTask: _showSchoolOverview);
        return view.WorkspaceChanged;
    }

    private void CloseGradeTeachingPlan()
    {
        _classRulesWindow?.CloseForScopeChange();
        _classRulesWindow = null;
    }
}
