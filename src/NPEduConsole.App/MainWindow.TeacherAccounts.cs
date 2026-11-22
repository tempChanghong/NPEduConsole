using Avalonia.Controls;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private TeacherAccountsWindow? _teacherAccountsWindow;
    private TeacherAllocationWindow? _teacherAllocationWindow;

    private async void OpenTeacherAccountsClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _school is not { } school || _page != ShellPage.Classes) return;
        await ShowTeacherAccountsAsync(client, school, null);
        if (_client == client && _school == school && _page == ShellPage.Classes) OpenTeacherAccountsButton.Focus();
    }

    private async Task ShowTeacherAccountsAsync(ConsoleClient client, School school, string? accountId)
    {
        if (_teacherAccountsWindow is not null || _client != client || _school != school) return;
        var window = new TeacherAccountsWindow(client, school, accountId)
        {
            SessionExpired = error => { if (_client == client && _school == school) ReturnToLoginIfExpired(error); },
            OpenBulkAllocation = owner => ShowTeacherAllocationAsync(owner, client, school),
            OpenSchoolAccount = (owner, id) => ShowSchoolManagementAccountsAsync(owner, client, school, id),
            OpenSchoolProfile = owner => ShowSchoolProfileAsync(owner, client, school)
        };
        _teacherAccountsWindow = window;
        try { await window.ShowDialog(this); }
        finally { if (_teacherAccountsWindow == window) _teacherAccountsWindow = null; }
        if (_client == client && _school == school && window.Destination is { } next)
        {
            if (next.Kind == "teaching")
                await ShowTeachingAsync(client, school, next.TermId, next.TargetId, next.SubjectId, next.AccountId);
            else if (next.Kind is "grades" or "classes")
                await ShowStaffResponsibilitiesAsync(client, school, next.TermId, next.Kind, next.TargetId, next.AccountId);
        }
        ApplyPendingSchoolLabel();
    }

    private void CloseTeacherAccounts()
    {
        var allocation = _teacherAllocationWindow;
        _teacherAllocationWindow = null;
        allocation?.CloseForScopeChange();
        var window = _teacherAccountsWindow;
        _teacherAccountsWindow = null;
        window?.CloseForScopeChange();
    }

    private async Task<bool> ShowTeacherAllocationAsync(Window owner, ConsoleClient client, School school,
        string? termId = null, string? gradeId = null, bool archivedCorrection = false, string? workspaceId = null)
    {
        if (_teacherAllocationWindow is not null || _client != client || _school != school) return false;
        var window = new TeacherAllocationWindow(client, school, termId, gradeId, archivedCorrection, workspaceId)
        {
            SessionExpired = error => { if (_client == client && _school == school) ReturnToLoginIfExpired(error); },
            OpenSchoolProfile = archivedCorrection ? null : popup => ShowSchoolProfileAsync(popup, client, school)
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
