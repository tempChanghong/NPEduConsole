using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private TermPreparationView? _termPreparationView;

    private async void OpenTermPreparationClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _school is not { } school || _page != ShellPage.Classes) return;
        await ShowTermPreparationAsync(client, school);
        if (_client == client && _school == school && _page == ShellPage.Classes) OpenTermPreparationButton.Focus();
    }

    private async Task ShowTermPreparationAsync(ConsoleClient client, School school, Action? beforeNavigate = null, string? initialDraftCheck = null)
    {
        if (_termPreparationView is not null || _client != client || _school != school) return;
        var view = new TermPreparationView(client, school, initialDraftCheck)
        {
            SessionExpired = error => AcademicScopeUnavailable(error, client, school),
            OpenPlacement = (owner, termId, bindingId) => ShowTermScreenPlacementAsync(owner, client, school, termId, bindingId),
            OpenOrganizationImport = (owner, term) => ShowOrganizationImportAsync(owner, client, school, term.Id, term.Status == "ARCHIVED" ? term : null),
            OpenTermDiagnostics = (owner, term) => ShowTermDiagnosticsAsync(owner, client, school, term),
            OpenTermClassDirectory = term => ShowTermClassDirectoryAsync(client, school, term),
        };
        _termPreparationView = view;
        view.CloseRequested = () => ReturnFromAcademicPage(view);
        bool returned;
        try
        {
            var exit = PushAcademicPage(view, view.CanClose, EndView);
            await view.StartAsync();
            returned = await exit;
        }
        finally
        {
            if (_termPreparationView == view) _termPreparationView = null;
        }
        if (!returned || _client != client || _school != school || _termPreparationView is not null || !view.ActivationObserved) return;
        SetInactiveClassesFilter(false);
        if (view.ActivationDestination is { } target)
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

        void EndView()
        {
            if (_termPreparationView == view) _termPreparationView = null;
            view.End();
        }

    }

    private void CloseTermPreparation()
    {
        CloseTermScreenPlacement();
        _termDiagnostics = null;
        _termPreparationView = null;
        CloseAcademicWorkspace();
    }
}
