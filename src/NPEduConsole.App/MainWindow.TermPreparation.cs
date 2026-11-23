using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private TermPreparationWindow? _termPreparationWindow;

    private async void OpenTermPreparationClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _school is not { } school || _page != ShellPage.Classes) return;
        await ShowTermPreparationAsync(client, school);
        if (_client == client && _school == school && _page == ShellPage.Classes) OpenTermPreparationButton.Focus();
    }

    private async Task ShowTermPreparationAsync(ConsoleClient client, School school, Action? beforeNavigate = null, string? initialDraftCheck = null)
    {
        if (_termPreparationWindow is not null || _client != client || _school != school) return;
        var window = new TermPreparationWindow(client, school, initialDraftCheck)
        {
            SessionExpired = error => { if (_client == client && _school == school) ReturnToLoginIfExpired(error); },
            OpenPlacement = (owner, termId, bindingId) => ShowTermScreenPlacementAsync(owner, client, school, termId, bindingId),
            OpenOrganizationImport = (owner, term) => ShowOrganizationImportAsync(owner, client, school, term.Id, term.Status == "ARCHIVED" ? term : null),
            OpenTermDiagnostics = (owner, term) => ShowTermDiagnosticsAsync(owner, client, school, term)
        };
        _termPreparationWindow = window;
        window.Closed += Release;
        try { await window.ShowDialog(this); }
        finally
        {
            window.Closed -= Release;
            Release(null, EventArgs.Empty);
        }
        if (_client != client || _school != school || !window.ActivationObserved) return;
        SetInactiveClassesFilter(false);
        if (window.ActivationDestination is { } target)
        {
            beforeNavigate?.Invoke();
            await OpenTermResultDestinationAsync(client, school, target);
        }
        else
        {
            await LoadSchoolAsync(school);
            if (_client == client && _school == school)
                await SchoolOverviewWorkspace.RefreshAsync(restoreTask: _showSchoolOverview);
        }

        void Release(object? sender, EventArgs args)
        {
            if (_termPreparationWindow == window) _termPreparationWindow = null;
        }
    }

    private void CloseTermPreparation()
    {
        CloseTermScreenPlacement();
        CloseTermDiagnostics();
        var window = _termPreparationWindow;
        _termPreparationWindow = null;
        window?.CloseForScopeChange();
    }
}
