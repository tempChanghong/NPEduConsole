using Avalonia.Controls;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private SchoolManagementAccountsWindow? _schoolManagementAccountsWindow;

    private async void SchoolManagementAccountsClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _school is not { } school) return;
        await ShowSchoolManagementAccountsAsync(this, client, school, null);
        if (_client == client && _school == school) OpenSchoolManagementAccountsButton.Focus();
    }

    private async Task ShowSchoolManagementAccountsAsync(Window owner, ConsoleClient client, School school, string? account)
    {
        if (_schoolManagementAccountsWindow is not null || _client != client || _school != school) return;
        var window = _schoolManagementAccountsWindow = new SchoolManagementAccountsWindow(client, school, account)
        {
            OpenSchoolProfile = popup => ShowSchoolProfileAsync(popup, client, school)
        };
        try { await window.ShowDialog(owner); }
        finally { if (_schoolManagementAccountsWindow == window) _schoolManagementAccountsWindow = null; }
        if (_client != client || _school != school) return;
        if (window.RequiresSignIn) await SignOutAsync();
        else if (window.Changed) await SchoolOverviewWorkspace.RefreshAsync(restoreTask: _showSchoolOverview);
        ApplyPendingSchoolLabel();
    }

    private void CloseSchoolManagementAccounts()
    {
        _schoolManagementAccountsWindow?.CloseForScopeChange();
        _schoolManagementAccountsWindow = null;
    }
}
