using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private async void OpenOrganizationImportClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _school is not { } school || _page != ShellPage.Classes || _academicPages.Count != 0) return;
        if (!await ShowOrganizationImportAsync(client, school, null)) return;
        if (_client == client && _school == school && _page == ShellPage.Classes && _academicPages.Count == 0)
        {
            OpenOrganizationImportButton.BringIntoView();
            OpenOrganizationImportButton.Focus(NavigationMethod.Tab);
        }
    }

    private async Task<bool> ShowOrganizationImportAsync(ConsoleClient client, School school, string? termId,
        bool archived = false, Guid? requestId = null)
    {
        if (_client != client || _school != school) return false;
        var caller = _academicPages.TryPeek(out var parent) ? parent.Content : null;
        var view = new OrganizationImportView(client, school, termId, archived)
        {
            RecoveryRequestId = requestId,
            SessionExpired = error => AcademicScopeUnavailable(error, client, school),
            OpenSubjects = archived ? popup => OpenCorrectionSubjectsAsync(popup, client, school) : null
        };
        if (!archived && termId is null) view.OpenDeclaredTerm = draft =>
        {
            if (!TermDetailsView.HasPending(client, school.Id)) return ShowTermDetailsAsync(client, school, null, draft);
            view.NavigationNotice("学期管理中有操作待核对。请先从学期管理核对原操作，再为此文件建立草稿。");
            return Task.FromResult<ManagedAcademicTerm?>(null);
        };
        view.OpenTeacherAllocation = OpenTeachersAsync;
        view.OpenReceiptTarget = async current =>
        {
            if (!ShowsAcademicPage(current) || current.ConfirmedReceipt is not { } receipt ||
                !await current.HasCurrentTargetAsync(receipt.TermId, receipt.GradeId) || !ShowsAcademicPage(current)) return;
            if (current.GoToDraftId == receipt.TermId)
            {
                if (TermPreparationView.HasPendingSchoolRequest(client, school.Id))
                {
                    current.NavigationNotice("学期管理中有操作待核对。请先核对原请求，再打开这份草稿的启用前检查。");
                    return;
                }
                await ShowTermPreparationAsync(client, school, initialDraftCheck: receipt.TermId);
            }
            else if (current.GoToHistoryClass is { } history && history.TermId == receipt.TermId && history.GradeId == receipt.GradeId &&
                current.ReceiptTerm is { } original && receipt.AdministrativeClasses.Any(row => row.Id == history.WorkspaceId))
            {
                await ShowTermClassDirectoryAsync(client, school, original, history.WorkspaceId, receipt.GradeId);
            }
            else if (current.GoToTeaching is { } target && target.TermId == receipt.TermId &&
                (target.IsGroup ? receipt.CourseGroups : receipt.AdministrativeClasses).Any(row => row.Id == target.WorkspaceId))
            {
                if (receipt.TermStatus == "ARCHIVED")
                {
                    var subject = target.IsGroup ? current.ReceiptSubjectForGroup(target.WorkspaceId) : null;
                    if (target.IsGroup && subject is null)
                    {
                        current.NavigationNotice("这份回执中找不到所选教学班的学科。请从该学期的教学安排核对。");
                        return;
                    }
                    await ShowGradeTeachingPlanAsync(client, school, receipt.GradeId, subject, target.IsGroup ? target.WorkspaceId : null,
                        receipt.TermId, target.IsGroup ? null : target.WorkspaceId, requireInitialTarget: true);
                }
                else await ShowTeachingPageAsync(client, school, receipt.TermId, target.WorkspaceId, null,
                    courseGroup: target.IsGroup, termName: current.ReceiptTerm?.Name);
            }
        };
        view.OpenPendingRequest = async (id, term, correction) =>
        {
            if (ShowsAcademicPage(view)) await ShowOrganizationImportAsync(client, school, term, correction, id);
        };
        view.CloseRequested = () => ReturnFromAcademicPage(view);
        var exit = PushAcademicPage(view, view.CanClose, view.End);
        await view.StartAsync();
        var returned = await exit;
        if (!returned || _client != client || _school != school) return false;
        if (view.ActiveTermChanged && caller is OrganizationImportView importParent)
            importParent.ObserveActiveImport();
        else if (view.ActiveTermChanged && (caller is null ? _academicPages.Count == 0 : ShowsAcademicPage(caller)))
        {
            await LoadSchoolAsync(school, _classroom?.Id, _device);
            if (_client != client || _school != school) return false;
            await SchoolOverviewWorkspace.RefreshAsync(restoreTask: _showSchoolOverview);
        }
        return _client == client && _school == school && (caller is null ? _academicPages.Count == 0 : ShowsAcademicPage(caller));

        async Task OpenTeachersAsync(string term, string grade, string? workspace)
        {
            if (!ShowsAcademicPage(view) || !await view.HasCurrentTargetAsync(term, grade) || !ShowsAcademicPage(view)) return;
            await ShowTeacherAllocationPageAsync(client, school, term, grade, archived, workspace);
        }
    }

    private async Task<SchoolSubject?> OpenCorrectionSubjectsAsync(Window owner, ConsoleClient client, School school)
    {
        if (_schoolSubjectWindow is not null || _client != client || _school != school) return null;
        var catalog = new SchoolSubjectCatalogWindow(client, school, returnToSetup: true, returnDestination: "批量更正")
        {
            SessionExpired = error => { if (_client == client && _school == school) ReturnToLoginIfExpired(error); }
        };
        _schoolSubjectWindow = catalog;
        try { await catalog.ShowDialog(owner); }
        finally { if (_schoolSubjectWindow == catalog) _schoolSubjectWindow = null; }
        return _client == client && _school == school ? catalog.CreatedSubject : null;
    }

}
