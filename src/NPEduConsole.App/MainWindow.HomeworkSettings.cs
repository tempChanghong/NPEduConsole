using Avalonia.Interactivity;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private HomeworkSettingsWindow? _homeworkSettingsWindow;

    private async void OpenHomeworkSettingsClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _school is not { } school || _page != ShellPage.Classes || _homeworkSettingsWindow is not null) return;
        var window = new HomeworkSettingsWindow(client, school)
        {
            SessionExpired = error =>
            {
                if (_client == client && _school == school) ReturnToLoginIfExpired(error);
            }
        };
        _homeworkSettingsWindow = window;
        try { await window.ShowDialog(this); }
        finally { if (_homeworkSettingsWindow == window) _homeworkSettingsWindow = null; }
        if (_client == client && _school == school && _page == ShellPage.Classes) OpenHomeworkSettingsButton.Focus();
    }

    private void CloseHomeworkSettings() => _homeworkSettingsWindow?.CloseForScopeChange();
}
