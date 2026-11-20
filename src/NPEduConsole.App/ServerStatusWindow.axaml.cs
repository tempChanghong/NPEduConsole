using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class ServerStatusWindow : Window
{
    private CancellationTokenSource? _request;
    private bool _changingInputs = true;

    public ServerStatusWindow() : this(null) { }

    internal ServerStatusWindow(string? initialAddress)
    {
        InitializeComponent();
        OpsServerAddress.Text = initialAddress;
        _changingInputs = false;
        Opened += (_, _) => (string.IsNullOrWhiteSpace(OpsServerAddress.Text) ? OpsServerAddress : OpsMonitoringToken).Focus();
        Closed += (_, _) =>
        {
            _changingInputs = true;
            ResetCheck();
            OpsMonitoringToken.Clear();
        };
        AddHandler(KeyDownEvent, CloseOnEscape, RoutingStrategies.Tunnel);
    }

    private void CloseOnEscape(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape) return;
        args.Handled = true;
        Close();
    }

    private void ServerAddressChanging(object? sender, TextChangingEventArgs args)
    {
        if (_changingInputs) return;
        _changingInputs = true;
        OpsMonitoringToken.Clear();
        _changingInputs = false;
        ResetCheck();
    }

    private void MonitoringTokenChanging(object? sender, TextChangingEventArgs args)
    {
        if (!_changingInputs) ResetCheck();
    }

    private async void CheckStatusClicked(object? sender, RoutedEventArgs args)
    {
        if (_request is not null || _changingInputs) return;
        string address = OpsServerAddress.Text ?? "";
        string token = OpsMonitoringToken.Text ?? "";
        ResetCheck();
        using var request = new CancellationTokenSource();
        _request = request;
        RenderActions();
        try
        {
            var status = await ServerStatusClient.CheckAsync(address, token, request.Token);
            if (_request == request) ShowSnapshot(status);
        }
        catch (ConsoleApiException error)
        {
            if (_request != request) return;
            OpsErrorMessage.Text = error.Message;
            OpsError.IsVisible = true;
            if (error.Code == "OPS_INVALID_ADDRESS") OpsServerAddress.Focus();
            else if (error.Code is "OPS_INVALID_TOKEN" or "OPS_UNAUTHORIZED") OpsMonitoringToken.Focus();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally
        {
            if (_request == request)
            {
                _request = null;
                RenderActions();
            }
        }
    }

    private void CancelStatusClicked(object? sender, RoutedEventArgs args)
    {
        if (_request is null) return;
        ResetCheck();
        OpsActivity.Text = "本次检查已取消。";
        OpsActivity.IsVisible = true;
        CheckServerStatusButton.Focus();
    }

    private void ResetCheck()
    {
        var request = _request;
        _request = null;
        request?.Cancel();
        OpsActivity.IsVisible = OpsError.IsVisible = OpsSnapshot.IsVisible = false;
        OpsActivity.Text = OpsErrorMessage.Text = OpsDatabase.Text = OpsCatalog.Text = OpsVersion.Text = OpsCheckedAt.Text = null;
        OpsDatabaseHint.Text = OpsCatalogHint.Text = null;
        RenderActions();
    }

    private void RenderActions()
    {
        bool checking = _request is not null;
        CheckServerStatusButton.Content = checking ? "检查中…" : OpsSnapshot.IsVisible ? "刷新" : "检查";
        CheckServerStatusButton.IsEnabled = !checking;
        CancelServerStatusButton.IsVisible = checking;
    }

    private void ShowSnapshot(ServerStatus status)
    {
        bool databaseAvailable = status.Database == ServerDatabaseStatus.Available;
        OpsDatabase.Text = databaseAvailable ? "可用" : "不可用";
        OpsDatabase.Classes.Set("server-unavailable", !databaseAvailable);
        OpsDatabaseHint.IsVisible = !databaseAvailable;
        OpsDatabaseHint.Text = databaseAvailable ? null : "请检查 PostgreSQL 服务及 KV 的数据库连接配置。";
        var catalog = status.UpdateCatalog;
        (string text, string? hint) = catalog.Status switch
        {
            ServerCatalogStatus.Available when catalog.ReleaseCount == 0 => ("可用，暂无发行版本", null),
            ServerCatalogStatus.Available => ($"可用 · {catalog.ReleaseCount} 个发行版本", null),
            ServerCatalogStatus.NotConfigured => ("未配置", "如需学校软件更新，请在 KV 部署配置中设置更新目录。"),
            _ => ("读取失败", "请核对更新目录路径、读取权限和清单格式。")
        };
        OpsCatalog.Text = text;
        OpsCatalog.Classes.Set("server-unavailable", catalog.Status == ServerCatalogStatus.Unavailable);
        OpsCatalogHint.Text = hint;
        OpsCatalogHint.IsVisible = hint is not null;
        OpsVersion.Text = status.Version;
        OpsCheckedAt.Text = status.CheckedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        OpsSnapshot.IsVisible = true;
    }
}
