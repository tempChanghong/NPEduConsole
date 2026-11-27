using Avalonia.Controls;
using NPEduConsole.Core;

namespace NPEduConsole.App;

internal sealed class ClassSubjectRulesWindow : Window
{
    private readonly ClassSubjectRulesView _view;
    private bool _closingForScope;
    internal bool WorkspaceChanged => _view.WorkspaceChanged;
    internal Action<ConsoleApiException>? AccessChanged { init => _view.AccessChanged = value; }

    internal ClassSubjectRulesWindow(ConsoleClient client, School school, string termId, string classId, string? subjectId)
    {
        Title = "编辑整班规则";
        Width = 1160;
        Height = 780;
        MinWidth = 640;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _view = new(client, school, termId, classId, initialSubjectId: subjectId) { CloseRequested = Close };
        Content = _view;
        Opened += async (_, _) =>
        {
            BatchWorkspaceWindow.FitSchoolWindow(this);
            await _view.StartAsync();
        };
        Closing += (_, args) => args.Cancel = !_closingForScope && !_view.CanClose();
        Closed += (_, _) => _view.End();
    }

    internal void CloseForScopeChange()
    {
        _closingForScope = true;
        _view.End();
        Close();
    }
}
