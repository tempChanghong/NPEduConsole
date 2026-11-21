using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private ClassMaintenanceWindow? _classMaintenanceWindow;

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
        var window = _classMaintenanceWindow;
        _classMaintenanceWindow = null;
        window?.CloseForScopeChange();
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
        if (_classMaintenanceWindow is not null || _client is not { } client || _school is not { } school ||
            _classroom is not { TermId: { } } room) return;
        var window = new ClassMaintenanceWindow(client, school, room);
        _classMaintenanceWindow = window;
        window.SessionExpired += error =>
        {
            if (_classMaintenanceWindow == window && _client == client) ReturnToLoginIfExpired(error);
        };
        try { await window.ShowDialog(this); }
        finally
        {
            if (_classMaintenanceWindow == window) _classMaintenanceWindow = null;
        }
        if (_client != client || _school != school || _classroom != room) return;
        if (window.ObservedChange is { } updated)
        {
            _showClassStudy = false;
            SetInactiveClassesFilter(!updated.IsActive);
            SetClassCreationNotice(null);
            var message = window.Outcome ?? "已按学校服务的最新状态刷新。";
            SetClassMaintenanceNotice(message);
            bool loaded = await LoadSchoolAsync(school, updated.Id);
            if (_client != client || _school != school) return;
            if (!loaded || _classroom?.Id != updated.Id || _classroom.TermId != updated.TermId)
                SetClassMaintenanceNotice($"{message} 当前列表未能定位该班，请点击「刷新」并核对当前学期。");
        }
        else if (window.Outcome is { } outcome)
        {
            SetClassCreationNotice(null);
            SetClassMaintenanceNotice(outcome);
        }
        if (OpenClassMaintenanceButton.IsVisible) OpenClassMaintenanceButton.Focus();
    }
}
