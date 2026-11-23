using Avalonia.Controls;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private TermDiagnosticsView? _termDiagnostics;

    private async Task ShowTermDiagnosticsAsync(Window owner, ConsoleClient client, School school, ManagedAcademicTerm term)
    {
        if (_termDiagnostics is not null || _client != client || _school != school ||
            _termPreparationView is not { } preparation || !ShowsAcademicPage(preparation)) return;
        var view = new TermDiagnosticsView(client, school, term)
        {
            ScopeUnavailable = Expired
        };
        view.OpenTask = OpenTaskAsync;
        _termDiagnostics = view;
        view.CloseRequested = () => ReturnFromAcademicPage(view);
        try
        {
            var exit = PushAcademicPage(view, () => true, EndView);
            view.Start();
            await exit;
        }
        finally { if (_termDiagnostics == view) _termDiagnostics = null; }

        void EndView()
        {
            if (_termDiagnostics == view) _termDiagnostics = null;
            view.End();
        }

        void Expired(ConsoleApiException error)
        {
            AcademicScopeUnavailable(error, client, school);
        }

        async Task<bool> OpenTaskAsync(SchoolOverview snapshot, SchoolOverviewTask task)
        {
            if (_client != client || _school != school || _termDiagnostics != view || !ShowsAcademicPage(view) ||
                snapshot.School.Id != school.Id || snapshot.Term is not { } selected || selected.Id != term.Id)
                return false;
            switch (task.Action)
            {
                case "CREATE_GRADE":
                case "CREATE_CLASS":
                    var structure = new TermClassManagementWindow(client, school, term with {Status = selected.Status!, Name = selected.Name},
                        task.ClassId, task.GradeId) {SessionExpired = Expired};
                    await ShowLegacyAsync(structure, structure.CloseForScopeChange);
                    return true;
                case "OPEN_TEACHING" when selected.Status == "ACTIVE" && task.ClassId is { } classroom && task.SubjectId is { } subject:
                    return await ShowTeachingPageAsync(client, school, selected, classroom, subject, task.AccountId);
                case "OPEN_COURSE_GROUP_TEACHING" when selected.Status == "ACTIVE" && task.CourseGroupId is { } group && task.SubjectId is { } groupSubject:
                    return await ShowTeachingPageAsync(client, school, selected, group, groupSubject, task.AccountId, courseGroup: true);
                case "OPEN_TEACHING" when task.GradeId is not null:
                case "OPEN_COURSE_GROUP_TEACHING" when task.GradeId is not null:
                case "OPEN_COURSE_GROUP_SOURCE":
                case "OPEN_TERM_TEACHING":
                    if (task.GradeId is null) return false;
                    var teaching = new GradeTeachingPlanWindow(client, school, task.GradeId, task.SubjectId, task.CourseGroupId,
                        selected.Id, task.ClassId, task.AccountId, requireInitialTarget: true) {SessionExpired = Expired};
                    await ShowLegacyAsync(teaching, teaching.CloseForScopeChange);
                    return true;
                case "OPEN_STAFF_REVIEW":
                    await ShowStaffReviewAsync(client, school, selected.Id,
                        StaffReviewIssue.IssueKey(task.Code, task.GradeId, task.ClassId, task.AccountId), task.GradeId, task.ClassId, owner);
                    return true;
                case "OPEN_TEACHER_ACCOUNT" when task.AccountId is { } account:
                    await ShowTeacherAccountsAsync(client, school, account, owner);
                    return true;
                case "OPEN_CLASS_RESPONSIBILITY" when task.ClassId is { } classTarget:
                    await ShowStaffResponsibilitiesAsync(client, school, selected.Id, "classes", classTarget, task.AccountId, owner);
                    return true;
                case "OPEN_GRADE_RESPONSIBILITY" when task.GradeId is { } gradeTarget:
                    await ShowStaffResponsibilitiesAsync(client, school, selected.Id, "grades", gradeTarget, task.AccountId, owner);
                    return true;
                default: return false;
            }
        }

        async Task ShowLegacyAsync(Window window, Action closeForScopeChange)
        {
            view.CloseLegacyDialog = closeForScopeChange;
            try { await window.ShowDialog(owner); }
            finally { view.CloseLegacyDialog = null; }
        }
    }

    private async Task<bool> ShowTeachingPageAsync(ConsoleClient client, School school, SchoolOverviewTerm term, string workspace,
        string subject, string? account, bool courseGroup = false)
    {
        var view = new TeachingView(client, school, term.Id, workspace, subject, account, courseGroup, inWorkspace: true, termName: term.Name);
        view.SessionExpired += error => AcademicScopeUnavailable(error, client, school);
        view.AuthorizationLost += error => AcademicScopeUnavailable(error, client, school);
        view.CloseRequested = () => ReturnFromAcademicPage(view);
        var exit = PushAcademicPage(view, view.CanClose, view.End);
        await view.StartAsync();
        return await exit;
    }
}
