using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private async void OpenCurrentClassRulesClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _school is not { } school ||
            _classroom is not { TermId: { } term, IsActive: true } room || _page != ShellPage.Classes || _academicPages.Count != 0) return;
        var offset = ClassWorkspaceScroll.Offset;
        var (returned, _) = await ShowClassRulesAsync(client, school, term, room.Id, currentEntry: true);
        if (!returned) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_client != client || _school != school || _classroom?.Id != room.Id || _page != ShellPage.Classes ||
                _academicPages.Count != 0 || !OpenClassRulesButton.IsEffectivelyVisible) return;
            ClassWorkspaceScroll.Offset = offset;
            OpenClassRulesButton.BringIntoView();
            OpenClassRulesButton.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private async Task<(bool Returned, bool Changed)> ShowClassRulesAsync(ConsoleClient client, School school, string term, string room,
        bool currentEntry = false, string? subject = null)
    {
        if (_client != client || _school != school || _academicPages.Any(page => page.Content is ClassSubjectRulesView)) return default;
        var view = new ClassSubjectRulesView(client, school, term, room, currentEntry, subject)
        {
            AccessChanged = error => AcademicScopeUnavailable(error, client, school),
            OpenConflict = (targetTerm, grade, targetClass, targetSubject, action) =>
                ShowGradeTeachingPlanAsync(client, school, grade, targetSubject, term: targetTerm, room: targetClass,
                    requireInitialTarget: true, ruleAction: action)
        };
        view.CloseRequested = () => ReturnFromAcademicPage(view);
        var exit = PushAcademicPage(view, view.CanClose, view.End);
        await view.StartAsync();
        if (!await exit || _client != client || _school != school) return default;
        if (view.WorkspaceChanged) await SchoolOverviewWorkspace.RefreshAsync(restoreTask: _showSchoolOverview);
        return (true, view.WorkspaceChanged);
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
        string? teachingIssue = null, bool requireInitialTarget = false, string? requiredCurrentTermId = null,
        string? ruleAction = null)
    {
        if (_client != client || _school != school) return false;
        bool fromClassRules = ruleAction is not null && _academicPages.TryPeek(out var current) && current.Content is ClassSubjectRulesView;
        if (ruleAction is not null && !fromClassRules) return false;
        if (_academicPages.Any(page => page.Content is GradeTeachingPlanView) && !fromClassRules) return false;
        var view = new GradeTeachingPlanView(client, school, grade, subject, group, term, room, teacher, ruleAction,
            requireInitialTarget: requireInitialTarget || teachingIssue is not null || requiredCurrentTermId is not null,
            teachingIssue: teachingIssue, inWorkspace: true, requiredCurrentTermId: requiredCurrentTermId)
        {
            SessionExpired = error => AcademicScopeUnavailable(error, client, school),
            OpenClassRules = fromClassRules ? null : (targetTerm, targetClass, targetSubject) =>
                ShowClassRulesAsync(client, school, targetTerm, targetClass, subject: targetSubject)
        };
        view.CloseRequested = () => ReturnFromAcademicPage(view);
        var exit = PushAcademicPage(view, view.CanClose, view.End);
        await view.StartAsync();
        if (!await exit) return false;
        if (view.WorkspaceChanged && _client == client && _school == school && !_showSchoolOverview)
            await SchoolOverviewWorkspace.RefreshAsync(restoreTask: _showSchoolOverview);
        return view.WorkspaceChanged;
    }
}
