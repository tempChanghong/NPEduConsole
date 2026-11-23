using Avalonia.Controls;
using NPEduConsole.Core;

namespace NPEduConsole.App;

// Existing modal callers use the same work page and keep their owner and return contract.
internal sealed class TeacherAllocationWindow : Window
{
    private bool _forceClose;
    private TeacherAllocationWindow? _recovery;
    private readonly Grid _content = new();
    private StaffConfigurationView? _staffConfiguration;
    private TaskCompletionSource? _staffReturned;
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
        _content.Children.Add(View);
        Content = _content;
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
        Closing += (_, args) =>
        {
            if (!_forceClose && _staffConfiguration is { } staff)
            {
                args.Cancel = true;
                if (staff.CanClose()) ReturnFromStaffConfiguration();
            }
            else args.Cancel = !_forceClose && !View.CanClose();
        };
        Closed += (_, _) => { ReturnFromStaffConfiguration(); View.End(); };
    }

    internal async Task<bool> ShowStaffConfigurationAsync(StaffConfigurationView staff)
    {
        if (_forceClose || _staffConfiguration is not null) return false;
        _staffConfiguration = staff;
        var returned = _staffReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        View.IsVisible = false;
        _content.Children.Add(staff);
        staff.ReturnRequested = ReturnFromStaffConfiguration;
        await staff.StartAsync();
        await returned.Task;
        return !_forceClose && staff.Applied;
    }

    private void ReturnFromStaffConfiguration()
    {
        if (_staffConfiguration is not { } staff || !staff.CanClose() && !_forceClose) return;
        _staffConfiguration = null;
        staff.End();
        _content.Children.Remove(staff);
        View.IsVisible = true;
        _staffReturned?.TrySetResult();
        _staffReturned = null;
    }

    internal void CloseForScopeChange()
    {
        _forceClose = true;
        _recovery?.CloseForScopeChange();
        Close();
    }
}
