using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed record OverviewTaskItem(SchoolOverviewTask Task, string ActionLabel, string Evidence)
{
    public string Message => Task.Message;
    public string AutomationId => "SchoolOverviewTask-" + Task.Code + "-" + (Task.ScreenId ?? Task.ClassId ?? "school");
}

public sealed record OverviewScreenItem(string Title, string Browser, string Native);

public sealed partial class SchoolOverviewView : UserControl
{
    private ConsoleClient? _client;
    private School? _school;
    private CancellationTokenSource? _request;
    private SchoolOverview? _snapshot;

    public event Action<ConsoleApiException>? SessionExpired;
    public event Action<SchoolOverview, SchoolOverviewTask>? TaskRequested;

    public SchoolOverviewView() => InitializeComponent();

    public void SetScope(ConsoleClient? client, School? school)
    {
        if (_client == client && _school == school) return;
        _client = client;
        _school = school;
        _request?.Cancel();
        _request = null;
        ClearSnapshot();
        OverviewNavigationFeedback.Text = null;
        OverviewSchool.Text = school?.Name;
        SchoolOverviewTabs.SelectedIndex = 0;
        if (client is not null && school is not null) _ = RefreshAsync();
    }

    public bool Shows(SchoolOverview overview) => ReferenceEquals(_snapshot, overview);
    public void ShowNavigationMessage(string? message) => OverviewNavigationFeedback.Text = message;

    public async Task RefreshAsync()
    {
        if (_client is not { } client || _school is not { } school) return;
        _request?.Cancel();
        using var request = new CancellationTokenSource();
        _request = request;
        ClearSnapshot();
        OverviewNavigationFeedback.Text = null;
        OverviewFeedback.Text = "正在读取学校待办和大屏记录…";
        OverviewProgress.IsVisible = true;
        try
        {
            var overview = await client.GetSchoolOverviewAsync(school.Id, request.Token);
            if (_request != request) return;
            _snapshot = overview;
            OverviewTermName.Text = overview.Term?.Name ?? "暂无启用学期";
            var counts = overview.Counts;
            OverviewCounts.Text = $"{counts.Grades} 个年级 · {counts.AdministrativeClasses} 个启用行政班 · {counts.Screens} 台启用大屏";
            OverviewFeedback.Text = $"核对时间 {Time(overview.GeneratedAt)} · {overview.Tasks.Count} 项待办";
            OverviewTasksList.ItemsSource = overview.Tasks.OrderBy(task => task.Source switch { "TERM" => 0, "SCREEN" => 1, _ => 2 })
                .Select(TaskRow).ToArray();
            OverviewScreensList.ItemsSource = overview.Screens.Select(ScreenRow).ToArray();
            OverviewNoTasks.IsVisible = overview.Tasks.Count == 0;
            OverviewNoScreens.IsVisible = overview.Screens.Count == 0;
            OverviewResults.IsVisible = true;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (ConsoleApiException error)
        {
            if (_request != request) return;
            if (error.StatusCode == 401) SessionExpired?.Invoke(error);
            else OverviewFeedback.Text = error.StatusCode is 404 or 426
                ? "待核对：当前 KV 未提供学校总览，可继续使用班级列表或升级 KV。"
                : $"待核对：学校总览读取失败。{error.Message}";
        }
        finally
        {
            if (_request == request)
            {
                _request = null;
                OverviewProgress.IsVisible = false;
            }
        }
    }

    private void ClearSnapshot()
    {
        _snapshot = null;
        OverviewProgress.IsVisible = OverviewResults.IsVisible = false;
        OverviewTasksList.ItemsSource = OverviewScreensList.ItemsSource = null;
        OverviewFeedback.Text = null;
    }

    private async void RefreshClicked(object? sender, RoutedEventArgs args) => await RefreshAsync();

    private void TaskClicked(object? sender, RoutedEventArgs args)
    {
        if (_snapshot is { } snapshot && sender is Button { Tag: OverviewTaskItem item })
            TaskRequested?.Invoke(snapshot, item.Task);
    }

    private static OverviewTaskItem TaskRow(SchoolOverviewTask task)
    {
        string action = task.Action switch
        {
            "CREATE_GRADE" => "新建年级",
            "CREATE_CLASS" => "进入建班",
            "PREPARE_SCREEN" => "前往登记",
            "OPEN_SCREEN" => "定位大屏",
            _ => "网页办理"
        };
        string source = task.Source switch
        {
            "TERM" => "学期与班级",
            "SCREEN" when task.Action == "OPEN_WEB" && task.ScreenId is not null => "网页大屏",
            "SCREEN" when task.Action == "OPEN_SCREEN" => "NPEduTools",
            "SCREEN" => "教室大屏",
            "TEACHING" => "任课关系",
            "RESPONSIBILITY" => "教师职责",
            "ACCOUNT" => "教师账号",
            _ => "学校配置"
        };
        return new(task, action, task.LastReportedAt is { } time ? $"{source} · 最近记录 {Time(time)}" : source);
    }

    private static OverviewScreenItem ScreenRow(SchoolOverviewScreen screen) => new($"{screen.ClassName} · {screen.ScreenName}",
        "网页大屏 · " + Observation(screen.Browser), "NPEduTools · " + Observation(screen.Native));

    private static string Observation(SchoolScreenObservation observation)
    {
        string title = observation.State switch
        {
            "NOT_APPLICABLE" => "暂无该方式的接入记录",
            "AWAITING_LOGIN" => "等待首次登录",
            "AWAITING_HEARTBEAT" => "已接入，等待值守心跳",
            "ONLINE" => "近期有值守心跳",
            "DEGRADED" => "最近心跳报告异常",
            "OFFLINE" => "近期未收到心跳",
            "AWAITING_PAIRING" => "等待现场接入",
            "AWAITING_REPORT" => "已配对，等待首次有效上报",
            "REPORTED" => "已收到有效上报",
            "REPAIR_REQUIRED" => "旧配对授权失效，需现场重新配对",
            _ => "待核对"
        };
        if (observation.LastReportedAt is not { } time) return title;
        return $"{title} · {(observation.State == "REPAIR_REQUIRED" ? "原设备记录" : "最近记录")} {Time(time)}";
    }

    private static string Time(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
