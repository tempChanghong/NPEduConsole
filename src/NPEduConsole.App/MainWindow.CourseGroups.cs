using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private CourseGroupsWindow? _courseGroupsWindow;

    private async void OpenCourseGroupsClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _school is not { } school || _page != ShellPage.Classes) return;
        await ShowCourseGroupsAsync(client, school);
        if (_client == client && _school == school && _page == ShellPage.Classes) OpenCourseGroupsButton.Focus();
    }

    private async Task ShowCourseGroupsAsync(ConsoleClient client, School school, string? term = null, string? group = null,
        string? room = null, string? subject = null, bool teaching = false)
    {
        if (_courseGroupsWindow is not null || _client != client || _school != school) return;
        var window = new CourseGroupsWindow(client, school, term, group, room, subject, teaching)
        {
            OpenGradePlan = (owner, grade, planSubject, selectedGroup) => ShowGradeTeachingPlanAsync(owner, client, school, grade, planSubject, selectedGroup),
            SessionExpired = error => { if (_client == client && _school == school) ReturnToLoginIfExpired(error); }
        };
        _courseGroupsWindow = window;
        try { await window.ShowDialog(this); }
        finally { if (_courseGroupsWindow == window) _courseGroupsWindow = null; }
    }

    private void CloseCourseGroups()
    {
        CloseGradeTeachingPlan();
        var window = _courseGroupsWindow;
        _courseGroupsWindow = null;
        window?.CloseForScopeChange();
    }
}
