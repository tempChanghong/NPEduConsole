using Avalonia.Controls;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private sealed record FirstClassSetup(string SchoolId, SetupTerm Term);
    private SchoolSetupView? _schoolSetupView;
    private FirstClassSetup? _firstClassSetup;
    private SchoolTransferExportWindow? _schoolTransferExportWindow;
    internal Func<Task<string?>>? ChooseSchoolTransferPackage { get; set; }
    internal Func<string, Task<string?>>? SaveSchoolTransferPackage { get; set; }

    private void ConnectSchoolSetup()
    {
        SchoolConnectionSetup.SetupRequested += OpenSchoolSetupView;
        SchoolConnectionSetup.ReturnRequested += () => ReturnFromSchoolSetup(null);
        SchoolConnectionSetup.LoginRequested += ReturnFromSchoolSetup;
    }

    private void OpenFirstSchoolSetupClicked(object? sender, RoutedEventArgs e)
    {
        if (_client is not null || _request is not null || _desktopLogin is not null || _signingOut ||
            _serverStatusOpen || !LoginView.IsVisible) return;
        StopBrowserDiscovery();
        Password.Clear();
        LoginView.IsVisible = OpenFirstSchoolSetup.IsVisible = OpenServerStatusButton.IsVisible = false;
        SchoolConnectionSetup.IsVisible = true;
        SchoolConnectionSetup.Open(ServerAddress.Text?.Trim() ?? "");
    }

    private void OpenSchoolSetupView(SchoolConnectionSetupView.Target target, SchoolSetupView.Work work)
    {
        if (_client is not null || !SchoolConnectionSetup.IsVisible || _schoolSetupView is not null) return;
        var view = new SchoolSetupView(target.Kv, work)
        {
            ChoosePackagePath = ChooseSchoolTransferPackage,
            VerifyDeployment = target.VerifyDeclarationAsync,
        };
        _schoolSetupView = view;
        view.ReturnRequested += () =>
        {
            if (_schoolSetupView != view || !view.CanExit) return;
            CloseSchoolSetupView();
            SchoolConnectionSetup.IsVisible = true;
            SchoolConnectionSetup.ClearCheck();
            SchoolConnectionSetup.FocusCheck();
        };
        view.Completed += result =>
        {
            if (_schoolSetupView != view) return;
            CloseSchoolSetupView();
            SchoolConnectionSetup.Reset();
            OpenFirstSchoolSetup.IsVisible = OpenServerStatusButton.IsVisible = true;
            _firstClassSetup = result.FirstTerm is { } term ? new(result.SchoolId, term) : null;
            _openingWebsite = target.Website is { } website ? (result.SchoolId, website) : null;
            ServerAddress.Text = result.Client.ServerUri.AbsoluteUri.TrimEnd('/');
            Password.Clear();
            ShowSignedIn(result.Client, result.Schools, result.AccountName);
            SchoolPicker.SelectedItem = result.Schools.Single(school => school.Id == result.SchoolId);
            SchoolPicker.Focus();
        };
        SchoolConnectionSetup.IsVisible = false;
        SchoolSetupHost.Content = view;
        SchoolSetupHost.IsVisible = true;
        view.FocusKey();
    }

    private bool CanExitSchoolSetup() => _schoolSetupView?.CanExit != false;

    private void CloseSchoolSetupView()
    {
        var view = _schoolSetupView;
        _schoolSetupView = null;
        view?.Deactivate();
        SchoolSetupHost.IsVisible = false;
        SchoolSetupHost.Content = null;
    }

    private void ReturnFromSchoolSetup(Uri? server)
    {
        if (!SchoolConnectionSetup.IsVisible) return;
        SchoolConnectionSetup.IsVisible = false;
        SchoolConnectionSetup.Reset();
        if (server is not null)
        {
            ServerAddress.Text = server.AbsoluteUri.TrimEnd('/');
            ShowKnownLoginServer(server);
        }
        LoginView.IsVisible = OpenFirstSchoolSetup.IsVisible = OpenServerStatusButton.IsVisible = true;
        _ = DiscoverBrowserLoginAsync();
        if (server is null) OpenFirstSchoolSetup.Focus();
        else SchoolCode.Focus();
    }

    private void UpdateFirstClassSetup()
    {
        FirstSchoolClassCard.IsVisible = _firstClassSetup is { } planned && planned.SchoolId == _school?.Id &&
            planned.Term.Id == _overviewTerm?.Id;
        if (FirstSchoolClassCard.IsVisible)
            FirstSchoolClassScope.Text = $"初始化已完成 · {_school!.Name} · {_firstClassSetup!.Term.Name}";
    }

    private async void OpenFirstSchoolClassClicked(object? sender, RoutedEventArgs e)
    {
        if (!FirstSchoolClassCard.IsEffectivelyVisible || _firstClassSetup is not { } planned ||
            _school?.Id != planned.SchoolId || _overviewTerm?.Id != planned.Term.Id) return;
        await OpenClassCreationAsync(planned.Term.Id, requiredActiveTermId: planned.Term.Id);
        if (_firstClassSetup == planned && _school?.Id == planned.SchoolId)
            await SchoolOverviewWorkspace.RefreshAsync();
    }

    private async void OpenSchoolTransferClicked(object? sender, RoutedEventArgs e)
    {
        if (_client is not { } client || _school is not { } school || _schoolTransferExportWindow is not null) return;
        var window = _schoolTransferExportWindow = new SchoolTransferExportWindow(client, school) {ChooseSavePath = SaveSchoolTransferPackage};
        try { await window.ShowDialog(this); }
        finally { if (_schoolTransferExportWindow == window) _schoolTransferExportWindow = null; }
        if (_client == client && _school == school) OpenSchoolTransferButton.Focus();
    }

    private void CloseTransferForScopeChange()
    {
        _schoolTransferExportWindow?.Close();
        _schoolTransferExportWindow = null;
    }
}
