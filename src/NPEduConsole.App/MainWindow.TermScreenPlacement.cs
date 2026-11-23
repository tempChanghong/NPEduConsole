using Avalonia.Controls;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private TermScreenPlacementWindow? _termScreenPlacementWindow;

    private async Task<TermPreparationView.ScreenDestination?> ShowTermScreenPlacementAsync(
        Window owner, ConsoleClient client, School school, string termId, string bindingId)
    {
        if (_termScreenPlacementWindow is not null || _client != client || _school != school) return null;
        var window = new TermScreenPlacementWindow(client, school, termId, bindingId)
        {
            SessionExpired = error => { if (_client == client && _school == school) ReturnToLoginIfExpired(error); }
        };
        _termScreenPlacementWindow = window;
        try { await window.ShowDialog(owner); }
        finally
        {
            if (_termScreenPlacementWindow == window) _termScreenPlacementWindow = null;
        }
        return _client == client && _school == school ? window.Destination : null;
    }

    private void CloseTermScreenPlacement()
    {
        var placement = _termScreenPlacementWindow;
        _termScreenPlacementWindow = null;
        placement?.CloseForScopeChange();
    }

    private async Task OpenTermResultDestinationAsync(ConsoleClient client, School school, TermPreparationView.ScreenDestination target)
    {
        if (_showSchoolOverview)
        {
            await SchoolOverviewWorkspace.RefreshAsync(restoreTask: true);
            if (_client != client || _school != school || !_showSchoolOverview) return;
        }
        await OpenShellTargetAsync(target.ClassId, target.ScreenId, target.TermId);
        if (_client == client && _school == school && _detailScreenId == target.ScreenId && _page == ShellPage.Screen &&
            target.NeedsPairing && _classroom?.Screens.FirstOrDefault(row => row.Id == target.ScreenId) is { } screen &&
            _admissionButtons.TryGetValue(target.ScreenId, out var opener))
            await OpenClassAdmissionAsync(screen, opener);
    }
}
