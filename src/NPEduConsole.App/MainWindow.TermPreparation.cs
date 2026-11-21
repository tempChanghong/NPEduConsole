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

    private async Task ShowTermPreparationAsync(ConsoleClient client, School school)
    {
        if (_termPreparationWindow is not null || _client != client || _school != school) return;
        var window = new TermPreparationWindow(client, school)
        {
            SessionExpired = error => { if (_client == client && _school == school) ReturnToLoginIfExpired(error); }
        };
        _termPreparationWindow = window;
        window.Closed += Release;
        try { await window.ShowDialog(this); }
        finally
        {
            window.Closed -= Release;
            Release(null, EventArgs.Empty);
        }

        void Release(object? sender, EventArgs args)
        {
            if (_termPreparationWindow == window) _termPreparationWindow = null;
        }
    }

    private void CloseTermPreparation()
    {
        var window = _termPreparationWindow;
        _termPreparationWindow = null;
        window?.CloseForScopeChange();
    }
}
