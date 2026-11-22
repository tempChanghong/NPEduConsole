using Avalonia.Controls;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private OrganizationImportWindow? _organizationImportWindow;

    private async void OpenOrganizationImportClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _school is not { } school || _page != ShellPage.Classes) return;
        var draftId = await ShowOrganizationImportAsync(this, client, school, null);
        if (_client != client || _school != school) return;
        if (draftId is not null) await ShowTermPreparationAsync(client, school, initialDraftCheck: draftId);
        else if (_page == ShellPage.Classes) OpenOrganizationImportButton.Focus();
    }

    private async Task<string?> ShowOrganizationImportAsync(Window owner, ConsoleClient client, School school, string? termId,
        ManagedAcademicTerm? archivedTerm = null)
    {
        if (_organizationImportWindow is not null || _client != client || _school != school) return null;
        var window = new OrganizationImportWindow(client, school, termId, archivedTerm is not null)
        {
            SessionExpired = error => { if (_client == client && _school == school) ReturnToLoginIfExpired(error); },
            OpenSubjects = archivedTerm is null ? null : popup => OpenCorrectionSubjectsAsync(popup, client, school)
        };
        _organizationImportWindow = window;
        try { await window.ShowDialog(owner); }
        finally { if (_organizationImportWindow == window) _organizationImportWindow = null; }
        if (_client != client || _school != school) return null;
        if (window.ActiveTermChanged)
        {
            await LoadSchoolAsync(school, _classroom?.Id, _device);
            if (_client != client || _school != school) return null;
            await SchoolOverviewWorkspace.RefreshAsync(restoreTask: _showSchoolOverview);
        }
        if (_client != client || _school != school) return null;
        if (window.GoToTeacherAllocation is { } allocation)
            await ShowTeacherAllocationAsync(owner, client, school, allocation.TermId, allocation.GradeId,
                archivedTerm is not null, window.SelectedReceiptWorkspace);
        if (archivedTerm is not null && window.GoToHistoryClass is { } history && history.TermId == archivedTerm.Id)
        {
            var classes = new TermClassManagementWindow(client, school, archivedTerm, history.WorkspaceId, history.GradeId)
            {
                SessionExpired = error => { if (_client == client && _school == school) ReturnToLoginIfExpired(error); }
            };
            await classes.ShowDialog(owner);
        }
        if (window.GoToTeaching is { } target)
            await ShowTeachingAsync(client, school, target.TermId, target.WorkspaceId, null, courseGroup: target.IsGroup, owner: owner);
        return window.GoToDraftId;
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

    private void CloseOrganizationImport()
    {
        var window = _organizationImportWindow;
        _organizationImportWindow = null;
        window?.CloseForScopeChange();
    }
}
