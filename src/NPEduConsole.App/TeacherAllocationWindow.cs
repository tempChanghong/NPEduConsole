using Avalonia.Controls;
using NPEduConsole.Core;

namespace NPEduConsole.App;

// Existing modal callers use the same work page and keep their owner and return contract.
internal sealed class TeacherAllocationWindow : Window
{
    private bool _forceClose;
    private TeacherAllocationWindow? _recovery;
    internal TeacherAllocationView View { get; }
    internal bool AccessChanged => View.AccessChanged;
    internal string? AllocatedAccountId => View.AllocatedAccountId;
    internal (string TermId, string WorkspaceId, string? SubjectId, string AccountId, bool IsGroup)? TeachingTarget => View.TeachingTarget;
    internal Action<ConsoleApiException>? SessionExpired { init => View.SessionExpired = value; }
    internal Func<Window, Task<bool>>? OpenSchoolProfile { init => View.OpenSchoolProfile = value; }
    internal Uri? LegacyWebLink { init => View.LegacyWebLink = value; }
    internal string? InitialInvitationId { init => View.InitialInvitationId = value; }

    internal TeacherAllocationWindow(ConsoleClient client, School school, string? termId, string? gradeId,
        bool archivedCorrection = false, string? initialWorkspaceId = null, Guid? recoveryId = null)
    {
        Title = archivedCorrection ? "归档学期 · 教师访问" : "批量导入与分配";
        Width = 1160;
        Height = 780;
        MinWidth = 705;
        MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        View = new TeacherAllocationView(client, school, termId, gradeId, archivedCorrection, initialWorkspaceId,
            fixedScope: recoveryId is not null) { RecoveryId = recoveryId };
        Content = View;
        View.CloseRequested = () => Close();
        View.OpenPendingRequest = async (id, term, grade, archived) =>
        {
            if (_forceClose || _recovery is not null) return;
            var recovery = new TeacherAllocationWindow(client, school, term, grade, archived, recoveryId: id)
            {
                SessionExpired = View.SessionExpired
            };
            _recovery = recovery;
            try { await recovery.ShowDialog(this); }
            finally { _recovery = null; }
            View.ObserveAllocation(recovery.View);
        };
        Opened += async (_, _) =>
        {
            BatchWorkspaceWindow.FitSchoolWindow(this);
            await View.StartAsync();
        };
        Closing += (_, args) => args.Cancel = !_forceClose && !View.CanClose();
        Closed += (_, _) => View.End();
    }

    internal void CloseForScopeChange()
    {
        _forceClose = true;
        _recovery?.CloseForScopeChange();
        Close();
    }
}
