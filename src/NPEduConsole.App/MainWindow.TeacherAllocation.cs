using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private async Task ShowTeacherAllocationPageAsync(ConsoleClient client, School school, string? termId,
        string? gradeId, bool archived, string? workspaceId, Guid? recoveryId = null)
    {
        if (_client != client || _school != school) return;
        var parent = _academicPages.TryPeek(out var prior) ? prior.Content : null;
        var view = new TeacherAllocationView(client, school, termId, gradeId, archived, workspaceId, fixedScope: true)
        {
            RecoveryId = recoveryId,
            SessionExpired = error => AcademicScopeUnavailable(error, client, school),
            OpenSchoolProfile = archived ? null : owner => ShowSchoolProfileAsync(owner, client, school)
        };
        view.CloseRequested = () => ReturnFromAcademicPage(view);
        view.OpenPendingRequest = async (request, term, grade, correction) =>
        {
            if (ShowsAcademicPage(view))
                await ShowTeacherAllocationPageAsync(client, school, term, grade, correction, null, request);
        };
        view.OpenTeaching = async () =>
        {
            if (!ShowsAcademicPage(view) || view.TeachingTarget is not { } next) return;
            if (archived)
                await ShowTeachingAsync(client, school, next.TermId, next.WorkspaceId, next.SubjectId,
                    next.AccountId, next.IsGroup, this);
            else
                await ShowTeachingPageAsync(client, school, next.TermId, next.WorkspaceId, next.SubjectId,
                    next.AccountId, next.IsGroup);
        };
        var exit = PushAcademicPage(view, view.CanClose, view.End);
        await view.StartAsync();
        if (!await exit || _client != client || _school != school) return;
        if (parent is TeacherAllocationView allocation)
            allocation.ObserveAllocation(view);
        else if (view.AccessChanged && !archived)
            await SchoolOverviewWorkspace.RefreshAsync(restoreTask: _showSchoolOverview);
        if (_client == client && _school == school) ApplyPendingSchoolLabel();
    }
}
