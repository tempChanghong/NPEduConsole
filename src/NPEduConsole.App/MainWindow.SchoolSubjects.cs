using Avalonia.Interactivity;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private SchoolSubjectCatalogWindow? _schoolSubjectWindow;

    private async void OpenSchoolSubjectsClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _school is not { } school || _page != ShellPage.Classes || _schoolSubjectWindow is not null) return;
        var window = new SchoolSubjectCatalogWindow(client, school)
        {
            SessionExpired = error => { if (_client == client && _school == school) ReturnToLoginIfExpired(error); }
        };
        _schoolSubjectWindow = window;
        try { await window.ShowDialog(this); }
        finally
        {
            if (_schoolSubjectWindow == window)
            {
                _schoolSubjectWindow = null;
                if (_client == client && _school == school && _page == ShellPage.Classes) OpenSchoolSubjectsButton.Focus();
            }
        }
    }

    private void CloseSchoolSubjects()
    {
        var window = _schoolSubjectWindow;
        _schoolSubjectWindow = null;
        window?.CloseForScopeChange();
    }
}
