using Avalonia.Controls;
using NPEduConsole.Core;

namespace NPEduConsole.App;

internal sealed class TeacherAccountsWindow : Window
{
    private bool _closingForScope;
    internal TeacherAccountsView View { get; }
    internal Action<ConsoleApiException>? SessionExpired { init => View.SessionExpired = value; }

    internal TeacherAccountsWindow(ConsoleClient client, School school, string accountId)
    {
        Title = "教师账号";
        Width = 1160;
        Height = 780;
        MinWidth = 650;
        MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        View = new(client, school, accountId) {CloseRequested = Close};
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
