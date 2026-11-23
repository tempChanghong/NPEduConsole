using Avalonia.Controls;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed class TeachingWindow : Window
{
    private readonly TeachingView _view;
    private bool _scopeClosing;
    public event Action<ConsoleApiException>? SessionExpired;

    public TeachingWindow(ConsoleClient client, School school, string termId, string classId, string? subjectId,
        string? accountId = null, bool courseGroup = false)
    {
        Title = courseGroup ? "走班任课" : "行政班任课";
        Width = 1160;
        Height = 780;
        MinWidth = 740;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _view = new TeachingView(client, school, termId, classId, subjectId, accountId, courseGroup) { CloseRequested = Close };
        _view.SessionExpired += error => SessionExpired?.Invoke(error);
        Content = _view;
        Opened += async (_, _) => { BatchWorkspaceWindow.FitSchoolWindow(this); await _view.StartAsync(); };
        Closing += (_, args) => args.Cancel = !_scopeClosing && !_view.CanClose();
        Closed += (_, _) => _view.End();
    }

    public void CloseForScopeChange()
    {
        _scopeClosing = true;
        _view.End();
        Close();
    }
}
