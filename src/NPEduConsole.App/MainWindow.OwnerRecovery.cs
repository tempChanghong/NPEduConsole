using Avalonia.Interactivity;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private void OpenOwnerRecoveryClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not null || _request is not null || _desktopLogin is not null || _signingOut ||
            _serverStatusOpen || !LoginView.IsVisible) return;
        StopBrowserDiscovery();
        Password.Clear();
        LoginView.IsVisible = OpenFirstSchoolSetup.IsVisible = OpenServerStatusButton.IsVisible = false;
        OwnerRecovery.IsVisible = true;
        OwnerRecovery.Open(ServerAddress.Text ?? "", SchoolCode.Text ?? "", Username.Text ?? "");
    }

    private void ReturnFromOwnerRecovery(OwnerRecoveryView.LoginTarget? target)
    {
        if (!OwnerRecovery.IsVisible || !OwnerRecovery.CanExit) return;
        OwnerRecovery.Deactivate();
        OwnerRecovery.IsVisible = false;
        Password.Clear();
        if (target is not null)
        {
            ServerAddress.Text = target.Server.AbsoluteUri.TrimEnd('/');
            ShowKnownLoginServer(target.Server);
            SchoolCode.Text = target.SchoolCode;
            Username.Text = target.Username;
            SetLoginMessage(target.Notice, recoveryAdvice: true);
        }
        LoginView.IsVisible = OpenFirstSchoolSetup.IsVisible = OpenServerStatusButton.IsVisible = true;
        _ = DiscoverBrowserLoginAsync();
        if (target is not null) Password.Focus();
        else OpenOwnerRecoveryButton.Focus();
    }
}
