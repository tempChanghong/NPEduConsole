using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private ClassMaintenanceView? _classMaintenanceView;

    private async void ClassActivityFilterChanged(object? sender, RoutedEventArgs args)
    {
        if (_settingSelection || _client is null || _school is not { } school) return;
        _showClassStudy = false;
        SetClassCreationNotice(null);
        SetClassMaintenanceNotice(null);
        await LoadSchoolAsync(school);
    }

    private void SetInactiveClassesFilter(bool inactive)
    {
        _settingSelection = true;
        InactiveClassesFilter.IsChecked = inactive;
        _settingSelection = false;
    }

    private void ResetClassMaintenance()
    {
        var view = _classMaintenanceView;
        _classMaintenanceView = null;
        if (view is not null && ShowsAcademicPage(view)) CloseAcademicWorkspace();
        SetInactiveClassesFilter(false);
        SetClassMaintenanceNotice(null);
    }

    private void SetClassMaintenanceNotice(string? message)
    {
        ClassMaintenanceNoticeText.Text = message;
        ClassMaintenanceNotice.IsVisible = !string.IsNullOrEmpty(message);
    }

    private async void OpenClassMaintenanceClicked(object? sender, RoutedEventArgs args)
    {
        if (_classMaintenanceView is not null || _academicPages.Count != 0 || _page != ShellPage.Classes ||
            _client is not { } client || _school is not { } school ||
            _classroom is not { TermId: { } } room) return;
        var view = _classMaintenanceView = new ClassMaintenanceView(client, school, room);
        bool returned;
        try { returned = await ShowClassMaintenancePageAsync(client, school, view); }
        finally { if (_classMaintenanceView == view) _classMaintenanceView = null; }
        if (!returned || _client != client || _school != school || _classroom != room) return;
        if (view.ObservedChange is { } updated)
        {
            _showClassStudy = false;
            SetInactiveClassesFilter(!updated.IsActive);
            SetClassCreationNotice(null);
            var message = view.Outcome ?? "已按学校服务的最新状态刷新。";
            SetClassMaintenanceNotice(message);
            bool loaded = await LoadSchoolAsync(school, updated.Id);
            if (_client != client || _school != school) return;
            if (!loaded || _classroom?.Id != updated.Id || _classroom.TermId != updated.TermId)
                SetClassMaintenanceNotice($"{message} 当前列表未能定位该班，请点击「刷新」并核对当前学期。");
        }
        else if (view.Outcome is { } outcome)
        {
            SetClassCreationNotice(null);
            SetClassMaintenanceNotice(outcome);
        }
        RestoreClassAction(client, school, room, OpenClassMaintenanceButton);
    }

    private async Task<bool> ShowClassMaintenancePageAsync(ConsoleClient client, School school, ClassMaintenanceView view)
    {
        view.SessionExpired += error => AcademicScopeUnavailable(error, client, school);
        view.CloseRequested = () => ReturnFromAcademicPage(view);
        var exit = PushAcademicPage(view, view.CanClose, view.End);
        await view.StartAsync();
        return await exit;
    }

    private async Task<bool> ShowTermClassDirectoryAsync(ConsoleClient client, School school, ManagedAcademicTerm term,
        string? classId = null, string? gradeId = null)
    {
        if (_client != client || _school != school) return false;
        var view = new TermClassManagementView(client, school, term, classId, gradeId)
        {
            SessionExpired = error => AcademicScopeUnavailable(error, client, school),
            OpenMaintenance = edit => ShowClassMaintenancePageAsync(client, school, edit),
            OpenRoster = roster => ShowRosterPageAsync(client, school, roster),
        };
        view.CloseRequested = () => ReturnFromAcademicPage(view);
        var exit = PushAcademicPage(view, view.CanClose, view.End);
        await view.StartAsync();
        return await exit;
    }
}
