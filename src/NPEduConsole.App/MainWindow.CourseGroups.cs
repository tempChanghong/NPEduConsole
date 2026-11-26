using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private async void OpenCourseGroupsClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _school is not { } school || _page != ShellPage.Classes || _academicPages.Count != 0) return;
        var offset = ClassWorkspaceScroll.Offset;
        if (!await ShowCourseGroupsAsync(client, school)) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_client != client || _school != school || _page != ShellPage.Classes || _academicPages.Count != 0 ||
                !OpenCourseGroupsButton.IsEffectivelyVisible) return;
            ClassWorkspaceScroll.Offset = offset;
            OpenCourseGroupsButton.BringIntoView();
            OpenCourseGroupsButton.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private async Task<bool> ShowCourseGroupsAsync(ConsoleClient client, School school, string? term = null, string? group = null,
        string? room = null, string? subject = null, bool teaching = false)
    {
        if (_client != client || _school != school || _academicPages.Any(page => page.Content is CourseGroupsView)) return false;
        var view = new CourseGroupsView(client, school, term, group, room, subject, teaching)
        {
            OpenGradePlan = (grade, planSubject, selectedGroup) =>
                ShowGradeTeachingPlanAsync(this, client, school, grade, planSubject, selectedGroup),
            OpenTeaching = (termId, target, course, isGroup, termName) =>
                ShowTeachingPageAsync(client, school, termId, target, course, courseGroup: isGroup, termName: termName),
            AccessLost = error => AcademicScopeUnavailable(error, client, school)
        };
        view.CloseRequested = () => ReturnFromAcademicPage(view);
        var returned = PushAcademicPage(view, view.CanClose, view.End);
        await view.StartAsync();
        return await returned;
    }
}
