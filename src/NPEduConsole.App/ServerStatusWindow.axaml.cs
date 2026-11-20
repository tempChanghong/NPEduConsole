using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class ServerStatusWindow : Window
{
    private CancellationTokenSource? _request;
    private readonly Action? _refreshSchoolStatus;
    private bool _changingInputs = true;

    public ServerStatusWindow() : this(initialAddress: null) { }

    internal ServerStatusWindow(string? initialAddress)
    {
        InitializeComponent();
        OpsServerAddress.Text = initialAddress;
        _changingInputs = false;
        Opened += (_, _) =>
        {
            if (_refreshSchoolStatus is not null) RefreshSchoolStatusButton.Focus();
            else (string.IsNullOrWhiteSpace(OpsServerAddress.Text) ? OpsServerAddress : OpsMonitoringToken).Focus();
        };
        Closed += (_, _) =>
        {
            _changingInputs = true;
            ResetCheck();
            OpsMonitoringToken.Clear();
        };
        AddHandler(KeyDownEvent, CloseOnEscape, RoutingStrategies.Tunnel);
    }

    internal ServerStatusWindow(Action refreshSchoolStatus) : this(initialAddress: null)
    {
        _refreshSchoolStatus = refreshSchoolStatus;
        Height = 580;
        OpsCredentialsCard.IsVisible = false;
        StatusWindowCaption.Text = "使用当前登录的学校账号检查 KV，约每 60 秒自动刷新。";
        RefreshSchoolStatusButton.IsVisible = true;
    }

    internal void PresentSchoolStatus(ServerStatus? status, string? error, bool checking)
    {
        RefreshSchoolStatusButton.IsEnabled = !checking;
        OpsActivity.Text = checking ? (status is null ? "正在检查服务器状态…" : "正在刷新，下方是上次检查的结果。") : null;
        OpsActivity.IsVisible = checking;
        OpsErrorMessage.Text = error;
        OpsError.IsVisible = error is not null;
        if (status is null) ClearSnapshot();
        else ShowSnapshot(status);
    }

    private void RefreshSchoolStatusClicked(object? sender, RoutedEventArgs args) => _refreshSchoolStatus?.Invoke();

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
        OpsActivity.IsVisible = OpsError.IsVisible = false;
        OpsActivity.Text = OpsErrorMessage.Text = null;
        ClearSnapshot();
        RenderActions();
    }

    private void ClearSnapshot()
    {
        OpsSnapshot.IsVisible = false;
        OpsDatabase.Text = OpsCatalog.Text = OpsVersion.Text = OpsCheckedAt.Text = null;
        OpsDatabaseHint.Text = OpsCatalogHint.Text = null;
        OpsBackupEvidence.IsVisible = false;
        OpsBackupSummary.Text = OpsBackupHint.Text = OpsBackupLastSuccess.Text = OpsBackupLastResult.Text = OpsBackupLastFinished.Text = null;
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
        OpsCheckedAt.Text = FormatTime(status.CheckedAt);
        ShowBackup(status.Backup);
        OpsSnapshot.IsVisible = true;
    }

    private void ShowBackup(ServerBackup? backup)
    {
        (string summary, string? hint) = backup switch
        {
            { Status: ServerBackupStatus.Available, Timeliness: ServerBackupTimeliness.Current } =>
                ("上次成功仍在时限内", backup.LastAttempt?.Result == ServerBackupResult.Failed ? "请核对最近一次任务的失败日志。" : null),
            { Status: ServerBackupStatus.Available, Timeliness: ServerBackupTimeliness.Overdue } =>
                ("超过成功时限", backup.LastSuccessAt.HasValue
                    ? $"距上次成功已达 {backup.MaxAgeHours} 小时。请检查定时任务和备份日志。"
                    : $"从开始观测已达 {backup.MaxAgeHours} 小时，尚无成功记录。请检查定时任务和备份日志。"),
            { Status: ServerBackupStatus.Available, Timeliness: ServerBackupTimeliness.AwaitingFirstSuccess } =>
                ("等待首次成功记录", $"仍在首个 {backup.MaxAgeHours} 小时观测窗口内。"),
            { Status: ServerBackupStatus.NoRecord } => ("暂无备份观测记录", "可在服务器核对定时备份任务的日志。"),
            { Status: ServerBackupStatus.NotConfigured } => ("KV 未接入备份记录", "请核对 KV 与定时备份任务的状态记录配置。"),
            { Status: ServerBackupStatus.Unavailable } => ("备份记录读取失败", "请核对 KV 读取备份状态记录的路径、权限和格式。"),
            _ => ("暂无可识别的备份状态", null)
        };
        OpsBackupSummary.Text = summary;
        OpsBackupSummary.Classes.Set("server-unavailable", backup?.Status == ServerBackupStatus.Unavailable ||
            backup?.Timeliness == ServerBackupTimeliness.Overdue);
        OpsBackupHint.Text = hint;
        OpsBackupHint.IsVisible = hint is not null;
        OpsBackupEvidence.IsVisible = backup?.Status == ServerBackupStatus.Available;
        OpsBackupLastSuccess.Text = backup?.LastSuccessAt is { } success ? FormatTime(success) : "暂无成功记录";
        OpsBackupLastResult.Text = backup?.LastAttempt?.Result switch
        {
            ServerBackupResult.Succeeded => "成功",
            ServerBackupResult.Failed => "失败",
            _ => "暂无任务记录"
        };
        OpsBackupLastResult.Classes.Set("server-unavailable", backup?.LastAttempt?.Result == ServerBackupResult.Failed);
        OpsBackupLastFinished.IsVisible = backup?.LastAttempt is not null;
        OpsBackupLastFinished.Text = backup?.LastAttempt is { } attempt ? FormatTime(attempt.FinishedAt) : null;
    }

    private static string FormatTime(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
