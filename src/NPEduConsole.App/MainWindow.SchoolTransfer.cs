using Avalonia.Controls;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _setupEntryRead;
    private SchoolSetupWindow? _schoolSetupWindow;
    private SchoolTransferExportWindow? _schoolTransferExportWindow;
    internal Func<Task<string?>>? ChooseSchoolTransferPackage { get; set; }
    internal Func<string, Task<string?>>? SaveSchoolTransferPackage { get; set; }

    private async Task RefreshSetupEntryAsync()
    {
        _setupEntryRead?.Cancel();
        LoginSetupOptions.IsVisible = false;
        var address = ServerAddress.Text?.Trim() ?? "";
        if (!LoginView.IsVisible || address.Length == 0 || _client is not null) return;
        using var read = _setupEntryRead = new CancellationTokenSource();
        try
        {
            await Task.Delay(400, read.Token);
            using var probe = new ConsoleClient(address);
            var status = await probe.GetSetupStatusAsync(read.Token);
            if (_setupEntryRead != read || !LoginView.IsVisible || address != ServerAddress.Text?.Trim() ||
                !status.SupportsDesktopSetup || status.State == "COMPLETED") return;
            bool empty = status.State == "NEW";
            InitializeNewSchool.IsVisible = RestoreSchoolTransfer.IsVisible = empty;
            ResumeSchoolSetup.IsVisible = status.State == "CONFIGURING";
            InitializeNewSchool.IsEnabled = RestoreSchoolTransfer.IsEnabled = ResumeSchoolSetup.IsEnabled = status.CanStart;
            LoginSetupNotice.Text = status.CanStart
                ? empty ? "这台服务器尚未初始化。" : "服务器已有设置，仍需完成初始化。"
                : $"服务器还不能启动初始化。{string.Join("；", status.Checks.Where(check => !check.Ok && check.Severity == "ERROR").Select(check => check.Message))}";
            LoginSetupOptions.IsVisible = true;
        }
        catch (Exception error) when (error is ConsoleApiException or OperationCanceledException) { }
        finally { if (_setupEntryRead == read) _setupEntryRead = null; }
    }

    private async void InitializeSchoolClicked(object? sender, RoutedEventArgs e) => await OpenSetupAsync(SchoolSetupWindow.Work.NewSchool);
    private async void RestoreTransferClicked(object? sender, RoutedEventArgs e) => await OpenSetupAsync(SchoolSetupWindow.Work.Import);
    private async void ResumeSetupClicked(object? sender, RoutedEventArgs e) => await OpenSetupAsync(SchoolSetupWindow.Work.Resume);

    private async Task OpenSetupAsync(SchoolSetupWindow.Work work)
    {
        if (_client is not null || _request is not null || _desktopLogin is not null || _schoolSetupWindow is not null ||
            _signingOut || !LoginView.IsVisible) return;
        try
        {
            var address = ServerAddress.Text?.Trim() ?? "";
            var window = _schoolSetupWindow = new SchoolSetupWindow(address, work) {ChoosePackagePath = ChooseSchoolTransferPackage};
            await window.ShowDialog(this);
            if (window.Result is { } result)
            {
                Password.Clear();
                ShowSignedIn(result.Client, result.Schools, result.AccountName);
                FocusSchoolSelection();
            }
        }
        catch (ConsoleApiException error) { SetLoginMessage(error.Message); }
        finally
        {
            _schoolSetupWindow = null;
            if (_client is null) _ = RefreshSetupEntryAsync();
        }
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
