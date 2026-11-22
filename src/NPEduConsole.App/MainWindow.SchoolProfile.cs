using Avalonia.Controls;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private SchoolProfileWindow? _schoolProfileWindow;
    private SchoolProfile? _pendingSchoolLabel;

    private async Task<bool> ShowSchoolProfileAsync(Window owner, ConsoleClient client, School school)
    {
        if (_schoolProfileWindow is not null || _client != client || _school != school) return false;
        var window = _schoolProfileWindow = new SchoolProfileWindow(client, school)
        {
            SessionExpired = error => { if (_client == client && _school == school) ReturnToLoginIfExpired(error); }
        };
        try { await window.ShowDialog(owner); }
        finally { if (_schoolProfileWindow == window) _schoolProfileWindow = null; }
        if (_client != client || _school != school || window.SavedProfile is not { } saved) return false;
        if (saved.Name != school.Name) _pendingSchoolLabel = saved;
        ApplyPendingSchoolLabel();
        return true;
    }

    private void ApplyPendingSchoolLabel()
    {
        if (_schoolProfileWindow is not null || _schoolManagementAccountsWindow is not null ||
            _teacherAccountsWindow is not null || _teacherAllocationWindow is not null ||
            _pendingSchoolLabel is not { } saved || _school is not { } selected || selected.Id != saved.Id) return;
        _pendingSchoolLabel = null;
        var renamed = selected with {Name = saved.Name};
        var schools = SchoolPicker.Items.OfType<School>().Select(school => school.Id == renamed.Id ? renamed : school).ToArray();
        _settingSelection = true;
        try
        {
            _school = renamed;
            SchoolPicker.ItemsSource = schools;
            SchoolPicker.SelectedItem = renamed;
        }
        finally { _settingSelection = false; }
        UpdateShellPage();
        UpdateStudyWorkspace();
    }

    private void CloseSchoolProfile()
    {
        _schoolProfileWindow?.CloseForScopeChange();
        _schoolProfileWindow = null;
        _pendingSchoolLabel = null;
    }
}
