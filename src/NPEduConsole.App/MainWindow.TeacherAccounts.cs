using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private TeacherAllocationWindow? _teacherAllocationWindow;

    private async void OpenTeacherAccountsClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _school is not { } school || _page != ShellPage.Classes || _academicPages.Count != 0) return;
        var offset = ClassWorkspaceScroll.Offset;
        await ShowTeacherAccountsAsync(client, school, null);
        Dispatcher.UIThread.Post(() =>
        {
            if (_client != client || _school != school || _page != ShellPage.Classes || _academicPages.Count != 0 ||
                !OpenTeacherAccountsButton.IsEffectivelyVisible) return;
            ClassWorkspaceScroll.Offset = offset;
            OpenTeacherAccountsButton.BringIntoView();
            OpenTeacherAccountsButton.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private async Task ShowTeacherAccountsAsync(ConsoleClient client, School school, string? accountId)
    {
        if (_client != client || _school != school || _academicPages.Any(page => page.Content is TeacherAccountsView)) return;
        TeacherAccountsView view = null!;
        view = new TeacherAccountsView(client, school, accountId)
        {
            SessionExpired = error => AcademicScopeUnavailable(error, client, school),
            AuthorizationLost = error => AcademicScopeUnavailable(error, client, school),
            OpenBulkAllocation = owner => ShowTeacherAllocationAsync(owner, client, school, view.AllocationLegacyWebLink),
            OpenSchoolAccount = (owner, id) => ShowSchoolManagementAccountsAsync(owner, client, school, id),
            OpenSchoolProfile = owner => ShowSchoolProfileAsync(owner, client, school),
            OpenEditor = OpenEditorAsync,
            OpenDestination = OpenDestinationAsync
        };
        view.CloseRequested = () => ReturnFromAcademicPage(view);
        var exit = PushAcademicPage(view, view.CanClose, view.End);
        await view.StartAsync();
        if (await exit && _client == client && _school == school) ApplyPendingSchoolLabel();

        async Task<bool> OpenEditorAsync(TeacherAccountEditor editor)
        {
            if (!ShowsAcademicPage(view) || _client != client || _school != school) return false;
            editor.AuthorizationLost = error => AcademicScopeUnavailable(error, client, school);
            editor.CloseRequested = () => ReturnFromAcademicPage(editor);
            var returned = PushAcademicPage(editor, editor.CanClose, editor.End);
            await editor.StartAsync();
            return await returned;
        }

        async Task OpenDestinationAsync(TeacherAccountDestination next)
        {
            if (!ShowsAcademicPage(view) || _client != client || _school != school) return;
            if (next.Kind == "teaching")
                await ShowTeachingPageAsync(client, school, next.TermId, next.TargetId, next.SubjectId, next.AccountId);
            else if (next.Kind is "grades" or "classes")
                await ShowStaffResponsibilitiesAsync(client, school, next.TermId, next.Kind, next.TargetId, next.AccountId);
        }
    }

    private void CloseTeacherAccounts()
    {
        var allocation = _teacherAllocationWindow;
        _teacherAllocationWindow = null;
        allocation?.CloseForScopeChange();
    }

    private async Task<bool> ShowTeacherAllocationAsync(Window owner, ConsoleClient client, School school, Uri? legacyWebLink,
        string? termId = null, string? gradeId = null, bool archivedCorrection = false, string? workspaceId = null,
        Action<ConsoleApiException>? scopeUnavailable = null)
    {
        if (_teacherAllocationWindow is not null || _client != client || _school != school) return false;
        var window = new TeacherAllocationWindow(client, school, termId, gradeId, archivedCorrection, workspaceId)
        {
            SessionExpired = error =>
            {
                if (_client != client || _school != school) return;
                if (scopeUnavailable is not null && error.StatusCode == 403)
                {
                    CloseTeacherAccounts();
                    scopeUnavailable(error);
                }
                else ReturnToLoginIfExpired(error);
            },
            OpenSchoolProfile = archivedCorrection ? null : popup => ShowSchoolProfileAsync(popup, client, school),
            LegacyWebLink = legacyWebLink
        };
        if (!archivedCorrection)
            window.View.OpenStaffConfiguration = (file, term) =>
            {
                if (_teacherAllocationWindow != window || _client != client || _school != school) return Task.FromResult(false);
                var staff = CreateStaffConfigurationView(client, school, term, file,
                    () => _teacherAllocationWindow == window && window.IsVisible, window);
                staff.AccessLost = error =>
                {
                    CloseTeacherAccounts();
                    AcademicScopeUnavailable(error, client, school);
                };
                return window.ShowStaffConfigurationAsync(staff);
            };
        _teacherAllocationWindow = window;
        try { await window.ShowDialog(owner); }
        finally { if (_teacherAllocationWindow == window) _teacherAllocationWindow = null; }
        if (_client != client || _school != school) return false;
        if (window.TeachingTarget is { } next)
            await ShowTeachingAsync(client, school, next.TermId, next.WorkspaceId, next.SubjectId, next.AccountId, next.IsGroup, owner);
        if (window.AccessChanged && !archivedCorrection && _client == client && _school == school)
            await SchoolOverviewWorkspace.RefreshAsync(restoreTask: _showSchoolOverview);
        bool changed = window.AccessChanged && _client == client && _school == school;
        ApplyPendingSchoolLabel();
        return changed;
    }
}
