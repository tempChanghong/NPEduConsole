using Avalonia.Controls;
using NPEduConsole.Core;

namespace NPEduConsole.App;

internal sealed class GradeTeachingPlanWindow : Window
{
    private bool _scopeClosing;
    internal GradeTeachingPlanView View { get; }
    internal bool WorkspaceChanged => View.WorkspaceChanged;
    internal Action<ConsoleApiException>? SessionExpired { init => View.SessionExpired = value; }

    internal GradeTeachingPlanWindow(ConsoleClient client, School school, string? gradeId, string? subjectId, string? groupId,
        string? specifiedTermId = null, string? classId = null, string? teacherId = null, string? ruleAction = null,
        bool requireInitialTarget = false, string? teachingIssue = null)
    {
        Title = specifiedTermId is null ? "年级教学安排" : "教学安排与师资";
        Width = 1160;
        Height = 780;
        MinWidth = 680;
        MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        View = new(client, school, gradeId, subjectId, groupId, specifiedTermId, classId, teacherId, ruleAction,
            requireInitialTarget, teachingIssue) { CloseRequested = Close };
        Content = View;
        Opened += async (_, _) =>
        {
            BatchWorkspaceWindow.FitSchoolWindow(this);
            await View.StartAsync();
        };
        Closing += (_, args) => args.Cancel = !_scopeClosing && !View.CanClose();
        Closed += (_, _) => View.End();
    }

    internal void CloseForScopeChange()
    {
        _scopeClosing = true;
        View.End();
        Close();
    }
}
