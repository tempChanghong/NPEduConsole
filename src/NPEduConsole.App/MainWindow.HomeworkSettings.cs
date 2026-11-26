using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private async void OpenHomeworkSettingsClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _school is not { } school || _page != ShellPage.Classes || _academicPages.Count != 0) return;
        var view = new HomeworkSettingsView(client, school)
        {
            SessionExpired = error =>
            {
                if (_client == client && _school == school) ReturnToLoginIfExpired(error);
            }
        };
        view.CloseRequested = () => ReturnFromAcademicPage(view);
        var returned = PushAcademicPage(view, view.CanClose, view.End);
        await view.StartAsync();
        if (!await returned) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_client == client && _school == school && _page == ShellPage.Classes && _academicPages.Count == 0 &&
                OpenHomeworkSettingsButton.IsEffectivelyVisible)
                OpenHomeworkSettingsButton.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }
}
