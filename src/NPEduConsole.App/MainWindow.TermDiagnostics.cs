using Avalonia.Controls;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private TermDiagnosticsWindow? _termDiagnostics;

    private async Task ShowTermDiagnosticsAsync(Window owner, ConsoleClient client, School school, ManagedAcademicTerm term)
    {
        if (_termDiagnostics is not null || _client != client || _school != school) return;
        var window = new TermDiagnosticsWindow(client, school, term)
        {
            SessionExpired = Expired,
            OpenTask = OpenTaskAsync
        };
        _termDiagnostics = window;
        try { await window.ShowDialog(owner); }
        finally { if (_termDiagnostics == window) _termDiagnostics = null; }

        void Expired(ConsoleApiException error)
        {
            if (_client == client && _school == school) ReturnToLoginIfExpired(error);
        }

        async Task<bool> OpenTaskAsync(Window diagnostics, SchoolOverview snapshot, SchoolOverviewTask task)
        {
            if (_client != client || _school != school || _termDiagnostics != diagnostics ||
                snapshot.School.Id != school.Id || snapshot.Term is not { } selected || selected.Id != term.Id)
                return false;
            switch (task.Action)
            {
                case "CREATE_GRADE":
                case "CREATE_CLASS":
                    var structure = new TermClassManagementWindow(client, school, term with {Status = selected.Status!, Name = selected.Name},
                        task.ClassId, task.GradeId) {SessionExpired = Expired};
                    await structure.ShowDialog(diagnostics);
                    return true;
                case "OPEN_TEACHING" when selected.Status == "ACTIVE" && task.ClassId is { } classroom && task.SubjectId is { } subject:
                    await ShowTeachingAsync(client, school, selected.Id, classroom, subject, task.AccountId, owner: diagnostics);
                    return true;
                case "OPEN_COURSE_GROUP_TEACHING" when selected.Status == "ACTIVE" && task.CourseGroupId is { } group && task.SubjectId is { } groupSubject:
                    await ShowTeachingAsync(client, school, selected.Id, group, groupSubject, task.AccountId, courseGroup: true, owner: diagnostics);
                    return true;
                case "OPEN_TEACHING" when task.GradeId is not null:
                case "OPEN_COURSE_GROUP_TEACHING" when task.GradeId is not null:
                case "OPEN_COURSE_GROUP_SOURCE":
                case "OPEN_TERM_TEACHING":
                    if (task.GradeId is null) return false;
                    var teaching = new GradeTeachingPlanWindow(client, school, task.GradeId, task.SubjectId, task.CourseGroupId,
                        selected.Id, task.ClassId, task.AccountId, requireInitialTarget: true) {SessionExpired = Expired};
                    await teaching.ShowDialog(diagnostics);
                    return true;
                case "OPEN_STAFF_REVIEW":
                    await ShowStaffReviewAsync(client, school, selected.Id,
                        StaffReviewIssue.IssueKey(task.Code, task.GradeId, task.ClassId, task.AccountId), task.GradeId, task.ClassId, diagnostics);
                    return true;
                case "OPEN_TEACHER_ACCOUNT" when task.AccountId is { } account:
                    await ShowTeacherAccountsAsync(client, school, account, diagnostics);
                    return true;
                case "OPEN_CLASS_RESPONSIBILITY" when task.ClassId is { } classTarget:
                    await ShowStaffResponsibilitiesAsync(client, school, selected.Id, "classes", classTarget, task.AccountId, diagnostics);
                    return true;
                case "OPEN_GRADE_RESPONSIBILITY" when task.GradeId is { } gradeTarget:
                    await ShowStaffResponsibilitiesAsync(client, school, selected.Id, "grades", gradeTarget, task.AccountId, diagnostics);
                    return true;
                default: return false;
            }
        }
    }

    private void CloseTermDiagnostics()
    {
        var window = _termDiagnostics;
        _termDiagnostics = null;
        window?.Close();
    }
}
