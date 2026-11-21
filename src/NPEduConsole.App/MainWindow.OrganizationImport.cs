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

    private async Task<string?> ShowOrganizationImportAsync(Window owner, ConsoleClient client, School school, string? termId)
    {
        if (_organizationImportWindow is not null || _client != client || _school != school) return null;
        var window = new OrganizationImportWindow(client, school, termId)
        {
            SessionExpired = error => { if (_client == client && _school == school) ReturnToLoginIfExpired(error); }
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
            await ShowTeacherAllocationAsync(owner, client, school, allocation.TermId, allocation.GradeId);
        if (window.GoToTeaching is { } target)
            await ShowTeachingAsync(client, school, target.TermId, target.WorkspaceId, null, courseGroup: target.IsGroup, owner: owner);
        return window.GoToDraftId;
    }

    private void CloseOrganizationImport()
    {
        var window = _organizationImportWindow;
        _organizationImportWindow = null;
        window?.CloseForScopeChange();
    }
}
