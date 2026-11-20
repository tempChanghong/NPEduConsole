using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer _schoolStatusTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private CancellationTokenSource? _schoolStatusRequest;
    private ServerStatus? _schoolStatus;
    private ConsoleApiException? _schoolStatusError;
    private ServerStatusWindow? _schoolStatusWindow;

    private void PollSchoolServerStatus(object? sender, EventArgs args) => _ = RefreshSchoolServerStatusAsync();

    private async Task RefreshSchoolServerStatusAsync()
    {
        if (_client is not { } client || _schoolStatusRequest is not null) return;
        _schoolStatusTimer.Stop();
        using var request = new CancellationTokenSource();
        _schoolStatusRequest = request;
        _schoolStatusError = null;
        RenderSchoolServerStatus();
        try
        {
            var status = await client.GetServerStatusAsync(request.Token);
            if (_schoolStatusRequest == request) _schoolStatus = status;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (ConsoleApiException error)
        {
            if (_schoolStatusRequest == request)
            {
                _schoolStatus = null;
                _schoolStatusError = error;
            }
        }
        finally
        {
            if (_schoolStatusRequest == request)
            {
                _schoolStatusRequest = null;
                RenderSchoolServerStatus();
                _schoolStatusTimer.Start();
            }
        }
    }

    private void OpenSchoolServerStatusClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is null) return;
        if (_schoolStatusWindow is { } open)
        {
            open.Activate();
            return;
        }

        var window = new ServerStatusWindow(() => _ = RefreshSchoolServerStatusAsync());
        _schoolStatusWindow = window;
        window.Closed += (_, _) =>
        {
            if (_schoolStatusWindow != window) return;
            _schoolStatusWindow = null;
            OpenSchoolServerStatusButton.Focus();
        };
        RenderSchoolServerStatus();
        window.Show(this);
    }

    private void ResetSchoolServerStatus()
    {
        _schoolStatusTimer.Stop();
        var request = _schoolStatusRequest;
        _schoolStatusRequest = null;
        request?.Cancel();
        _schoolStatus = null;
        _schoolStatusError = null;
        var window = _schoolStatusWindow;
        _schoolStatusWindow = null;
        window?.Close();
        RenderSchoolServerStatus();
    }

    private void RenderSchoolServerStatus()
    {
        bool checking = _schoolStatusRequest is not null;
        (string label, bool warning) = checking ? (_schoolStatus is null ? "检查中…" : "正在刷新…", false) : GetSchoolStatusSummary();
        SchoolServerStatusSummary.Text = label;
        SchoolServerStatusSummary.Classes.Set("school-server-warning", warning);
        string? description = _schoolStatusError?.Message;
        if (description is null && _schoolStatus is { } status)
            description = "上次检查 " + status.CheckedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        ToolTip.SetTip(OpenSchoolServerStatusButton, description);
        AutomationProperties.SetHelpText(OpenSchoolServerStatusButton, description is null ? label : $"{label}。{description}");
        _schoolStatusWindow?.PresentSchoolStatus(_schoolStatus, _schoolStatusError?.Message, checking);
    }

    private (string Label, bool Warning) GetSchoolStatusSummary()
    {
        if (_schoolStatusError is { } error)
            return error.Code switch
            {
                "SCHOOL_STATUS_UNSUPPORTED" => ("当前 KV 不支持", false),
                "SCHOOL_STATUS_FORBIDDEN" => ("无状态查看权限", true),
                "SCHOOL_STATUS_SESSION_UNVERIFIED" => ("暂无法核实登录", true),
                "OPS_CONNECTION" => ("服务器暂不可达", true),
                "OPS_TIMEOUT" => ("检查超时", true),
                _ => ("检查失败", true)
            };

        return _schoolStatus switch
        {
            null => ("等待检查", false),
            { Database: ServerDatabaseStatus.Unavailable } => ("数据库不可用", true),
            { UpdateCatalog.Status: ServerCatalogStatus.Unavailable } => ("更新目录读取失败", true),
            { Backup.Status: ServerBackupStatus.Unavailable } => ("备份状态读取失败", true),
            { Backup.Timeliness: ServerBackupTimeliness.Overdue } => ("备份成功记录逾期", true),
            { Backup.LastAttempt.Result: ServerBackupResult.Failed } => ("最近备份任务失败", true),
            _ => ("数据库可用", false)
        };
    }
}
