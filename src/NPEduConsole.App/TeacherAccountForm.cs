using Avalonia.Controls;
using NPEduConsole.Core;

namespace NPEduConsole.App;

internal sealed class TeacherAccountForm : Window
{
    private bool _closingForScope;
    internal TeacherAccountEditor View { get; }
    internal TeacherAccountReceipt? Receipt => View.Receipt;
    internal bool AllocationChanged => View.AllocationChanged;
    internal string? AllocatedAccountId => View.AllocatedAccountId;
    internal string? AllocationTermId { init => View.AllocationTermId = value; }
    internal string? AllocationGradeId { init => View.AllocationGradeId = value; }
    internal string? AllocationWorkspaceId { init => View.AllocationWorkspaceId = value; }
    internal Action<ConsoleApiException>? SessionExpired { init => View.SessionExpired = value; }

    internal TeacherAccountForm(ConsoleClient client, School school, string? resetId = null, string? createLabel = null)
        : this(new TeacherAccountEditor(client, school, resetId, createLabel)) { }

    internal TeacherAccountForm(TeacherAccountEditor view)
    {
        View = view;
        View.CloseRequested = Close;
        Title = View.Caption;
        Width = 790;
        Height = 510;
        MinWidth = 640;
        MinHeight = 455;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = View;
        Opened += async (_, _) =>
        {
            BatchWorkspaceWindow.FitSchoolWindow(this);
            await View.StartAsync();
        };
        Closing += (_, args) => args.Cancel = !_closingForScope && !View.CanClose();
        Closed += (_, _) => View.End();
    }

    internal void CloseForScopeChange()
    {
        _closingForScope = true;
        View.End();
        Close();
    }
}
