using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private SchoolStudyWindow? _schoolStudyWindow;

    private async void SchoolStudyClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _school is not { } school || _schoolStudyWindow is not null || _page != ShellPage.Classes) return;
        var window = new SchoolStudyWindow(client, school, _classroom?.IsActive == true ? _classroom.Id : null);
        _schoolStudyWindow = window;
        window.Closed += (_, _) => { if (_schoolStudyWindow == window) _schoolStudyWindow = null; };
        try { await window.ShowDialog(this); }
        finally { if (_schoolStudyWindow == window) _schoolStudyWindow = null; }
        if (_client == client && _school == school && _page == ShellPage.Classes) OpenSchoolStudyButton.Focus();
    }

    private void CloseSchoolStudy()
    {
        var window = _schoolStudyWindow;
        _schoolStudyWindow = null;
        window?.CloseForScopeChange();
    }
}
