using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private TeachingWindow? _teachingWindow;

    private async void OpenTeachingClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _school is not { } school || _page != ShellPage.Classes || _academicPages.Count != 0 ||
            _classroom is not { IsActive: true, TermId: { } termId } room) return;
        var offset = ClassWorkspaceScroll.Offset;
        if (!await ShowTeachingPageAsync(client, school, termId, room.Id, null)) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_client != client || _school != school || _page != ShellPage.Classes || _academicPages.Count != 0 ||
                _classroom?.Id != room.Id || _classroom.TermId != termId || !OpenTeachingButton.IsEffectivelyVisible) return;
            ClassWorkspaceScroll.Offset = offset;
            OpenTeachingButton.BringIntoView();
            OpenTeachingButton.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private async Task<bool> ShowTeachingPageAsync(ConsoleClient client, School school, string termId, string workspace,
        string? subject, string? account = null, bool courseGroup = false, string? termName = null)
    {
        if (_client != client || _school != school) return false;
        var view = new TeachingView(client, school, termId, workspace, subject, account, courseGroup, inWorkspace: true, termName: termName);
        view.SessionExpired += error => AcademicScopeUnavailable(error, client, school);
        view.AuthorizationLost += error => AcademicScopeUnavailable(error, client, school);
        view.CloseRequested = () => ReturnFromAcademicPage(view);
        var exit = PushAcademicPage(view, view.CanClose, view.End);
        await view.StartAsync();
        return await exit;
    }

    private async Task ShowTeachingAsync(ConsoleClient client, School school, string termId, string classId, string? subjectId,
        string? accountId = null, bool courseGroup = false, Window? owner = null)
    {
        if (_teachingWindow is not null || _client != client || _school != school) return;
        var window = new TeachingWindow(client, school, termId, classId, subjectId, accountId, courseGroup);
        _teachingWindow = window;
        window.SessionExpired += error =>
        {
            if (_teachingWindow == window && _client == client) ReturnToLoginIfExpired(error);
        };
        try { await window.ShowDialog(owner ?? this); }
        finally { if (_teachingWindow == window) _teachingWindow = null; }
    }

    private void CloseTeaching()
    {
        var window = _teachingWindow;
        _teachingWindow = null;
        window?.CloseForScopeChange();
    }
}
