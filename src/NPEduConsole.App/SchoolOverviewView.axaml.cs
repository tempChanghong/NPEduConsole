using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed record OverviewTaskItem(SchoolOverviewTask Task, string ActionLabel, string Evidence)
{
    public string Message => Task.Message;
    public string AutomationId => "SchoolOverviewTask-" + Task.Code + "-" + (Task.ScreenId ?? Task.ClassId ?? Task.GradeId ?? "school") +
        (Task.Action == "OPEN_TEACHING" ? "-" + Task.SubjectId : Task.AccountId is { } accountId ? "-" + accountId : "");
}

public sealed partial class SchoolOverviewView : UserControl
{
    private ConsoleClient? _client;
    private School? _school;
    private CancellationTokenSource? _request;
    private SchoolOverview? _snapshot;
    private IReadOnlyList<Classroom> _classLinks = [];
    private bool _showQueue;
    private Func<Button, bool>? _returnTarget;
    private Vector _returnOffset;
    private Vector _returnQueueOffset;

    public event Action<ConsoleApiException>? SessionExpired;
    public event Action<SchoolOverview, SchoolOverviewTask>? TaskRequested;
    public event Action<SchoolOverview?, string>? SnapshotChanged;
    public event Action? ClassDirectoryRequested;
    public event Action<Classroom>? ClassRequested;

    public SchoolOverviewView() => InitializeComponent();

    public void SetScope(ConsoleClient? client, School? school)
    {
        if (_client == client && _school == school) return;
        _client = client;
        _school = school;
        _request?.Cancel();
        _request = null;
        _showQueue = false;
        _returnTarget = null;
        _returnOffset = _returnQueueOffset = default;
        OverviewTaskFilter.SelectedIndex = 0;
        OverviewScroll.Offset = default;
        ShowNavigationMessage(null);
        SetReadState("选择学校", "请在顶部选择有权管理的学校。");
        if (client is not null && school is not null) _ = RefreshAsync();
    }

    public bool Shows(SchoolOverview overview) => ReferenceEquals(_snapshot, overview);

    public void ShowNavigationMessage(string? message)
    {
        OverviewNavigationFeedback.Text = message;
        OverviewNavigationFeedback.IsVisible = !string.IsNullOrEmpty(message);
    }

    public async Task RefreshAsync(bool restoreTask = false)
    {
        if (_client is not { } client || _school is not { } school) return;
        _request?.Cancel();
        using var request = new CancellationTokenSource();
        _request = request;
        ShowNavigationMessage(null);
        SetReadState("正在核对", "正在读取学校待办和大屏记录…");
        OverviewProgress.IsVisible = true;
        try
        {
            var overview = await client.GetSchoolOverviewAsync(school.Id, request.Token);
            if (_request != request) return;
            _snapshot = overview;
            var counts = overview.Counts;
            OverviewHeadline.Text = overview.Term is null ? "暂无启用学期" : overview.Tasks.Count == 0
                ? "当前暂无待办" : $"有 {overview.Tasks.Count} 项事务待办理";
            OverviewCounts.Text = overview.Term is null ? null :
                $"{counts.Grades} 个年级   ·   {counts.AdministrativeClasses} 个行政班   ·   {counts.Screens} 台大屏";
            OverviewFeedback.Text = $"核对时间 {Time(overview.GeneratedAt)}";
            OverviewResults.IsVisible = true;
            ShowAllOverviewButton.IsVisible = overview.Tasks.Count > 0;
            OverviewClassLink.IsVisible = overview.Term is not null;
            RenderClassLinks();
            RenderTasks();
            SnapshotChanged?.Invoke(overview, OverviewFeedback.Text);
            if (restoreTask && IsEffectivelyVisible)
                Dispatcher.UIThread.Post(() => RestoreTaskFocus(overview), DispatcherPriority.Loaded);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (ConsoleApiException error)
        {
            if (_request != request) return;
            if (error.StatusCode == 401) SessionExpired?.Invoke(error);
            else SetReadState("学校状态待核对", error.StatusCode is 404 or 426
                ? "当前 KV 未提供学校总览。仍可从班级与教务查看已授权班级。"
                : $"待核对：学校总览读取失败。{error.Message}");
            if (restoreTask && IsEffectivelyVisible) RefreshSchoolOverviewButton.Focus();
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

    private void SetReadState(string heading, string message)
    {
        _snapshot = null;
        OverviewHeadline.Text = heading;
        OverviewCounts.Text = null;
        OverviewFeedback.Text = message;
        OverviewProgress.IsVisible = OverviewResults.IsVisible = ShowAllOverviewButton.IsVisible = false;
        OverviewTasksList.ItemsSource = null;
        EducationTask.Content = DeviceTask.Content = null;
        SnapshotChanged?.Invoke(null, message);
    }

    public void SetClassLinks(IReadOnlyList<Classroom> rooms)
    {
        _classLinks = rooms;
        RenderClassLinks();
    }

    private void RenderClassLinks()
    {
        if (_snapshot?.Term is not { } term)
        {
            OverviewClassLinks.ItemsSource = null;
            return;
        }
        OverviewClassSummary.Text = _snapshot.Counts.AdministrativeClasses == 0 ? "暂无启用行政班" : "当前学期班级";
        OverviewClassLinks.ItemsSource = _classLinks.Where(room => room.TermId == term.Id).Take(3).ToArray();
    }

    private void RenderTasks()
    {
        if (_snapshot is not { } snapshot) return;
        var tasks = snapshot.Tasks.Select(TaskRow).ToArray();
        var education = tasks.FirstOrDefault(row => row.Task.Source != "SCREEN");
        var device = tasks.FirstOrDefault(row => row.Task.Source == "SCREEN");
        EducationTask.Content = education;
        DeviceTask.Content = device;
        EducationSpotlight.IsVisible = education is not null;
        DeviceSpotlight.IsVisible = device is not null;
        ArrangeSpotlights();
        Spotlights.IsVisible = !_showQueue && tasks.Length > 0;
        OverviewQueue.IsVisible = _showQueue && tasks.Length > 0;
        ShowAllOverviewButton.Content = _showQueue ? "收起全部待办" : "查看全部待办";
        var filtered = !_showQueue ? [] : tasks.Where(row => OverviewTaskFilter.SelectedIndex switch
        {
            1 => row.Task.Source is "TERM" or "TEACHING",
            2 => row.Task.Source == "SCREEN",
            3 => row.Task.Source is not ("TERM" or "TEACHING" or "SCREEN"),
            _ => true
        }).ToArray();
        OverviewTasksList.ItemsSource = filtered;
        OverviewNoTasks.IsVisible = _showQueue && filtered.Length == 0;
    }

    private void OverviewSizeChanged(object? sender, SizeChangedEventArgs args) => ArrangeSpotlights();

    private void ArrangeSpotlights()
    {
        bool compact = Bounds.Width < 710;
        bool shortWindow = Bounds.Height > 0 && Bounds.Height < 510;
        OverviewHero.Margin = new Thickness(20, shortWindow ? 10 : 17);
        OverviewHeadline.FontSize = shortWindow ? 26 : 32;
        OverviewIllustration.MaxHeight = shortWindow ? 92 : 142;
        OverviewIllustration.IsVisible = Bounds.Width >= 560;
        OverviewHero.ColumnDefinitions[0].Width = new GridLength(OverviewIllustration.IsVisible ? (Bounds.Width < 820 ? 102 : 156) : 0);
        bool pair = EducationSpotlight.IsVisible && DeviceSpotlight.IsVisible;
        Spotlights.ColumnDefinitions[1].Width = compact || !pair ? new GridLength(0) : GridLength.Star;
        Grid.SetColumn(DeviceSpotlight, compact || !pair ? 0 : 1);
        Grid.SetRow(DeviceSpotlight, compact && pair ? 1 : 0);
    }

    private async void RefreshClicked(object? sender, RoutedEventArgs args) => await RefreshAsync();

    private void ShowAllClicked(object? sender, RoutedEventArgs args)
    {
        _showQueue = !_showQueue;
        RenderTasks();
        if (_showQueue)
            Dispatcher.UIThread.Post(() =>
            {
                OverviewQueue.BringIntoView();
                OverviewTaskFilter.Focus();
            }, DispatcherPriority.Loaded);
    }

    private void FilterChanged(object? sender, SelectionChangedEventArgs args) => RenderTasks();

    private void ClassDirectoryClicked(object? sender, RoutedEventArgs args) => ClassDirectoryRequested?.Invoke();

    private void ClassClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: Classroom room } && _snapshot?.Term?.Id == room.TermId)
        {
            RememberReturn(button => button.Tag is Classroom row && row.Id == room.Id);
            ClassRequested?.Invoke(room);
        }
    }

    private void TaskClicked(object? sender, RoutedEventArgs args)
    {
        if (_snapshot is not { } snapshot || sender is not Button { Tag: OverviewTaskItem item }) return;
        RememberReturn(button => AutomationProperties.GetAutomationId(button) == item.AutomationId);
        TaskRequested?.Invoke(snapshot, item.Task);
    }

    internal void ForgetReturnTarget() => RememberReturn(null);

    private void RememberReturn(Func<Button, bool>? match)
    {
        _returnTarget = match;
        _returnOffset = OverviewScroll.Offset;
        _returnQueueOffset = OverviewTasksList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.Offset ?? default;
    }

    private void RestoreTaskFocus(SchoolOverview overview)
    {
        if (!Shows(overview) || !IsEffectivelyVisible) return;
        OverviewScroll.Offset = _returnOffset;
        if (OverviewTasksList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroll)
            scroll.Offset = _returnQueueOffset;
        Dispatcher.UIThread.Post(() =>
        {
            if (!Shows(overview) || !IsEffectivelyVisible) return;
            var target = _returnTarget is { } match ? this.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(button => button.IsEffectivelyVisible && match(button)) : null;
            if (target is not null) target.Focus();
            else if (_showQueue && OverviewQueue.IsEffectivelyVisible) OverviewTaskFilter.Focus();
            else if (ShowAllOverviewButton.IsEffectivelyVisible) ShowAllOverviewButton.Focus();
            else RefreshSchoolOverviewButton.Focus();
        }, DispatcherPriority.Background);
    }

    private static OverviewTaskItem TaskRow(SchoolOverviewTask task)
    {
        string action = task.Action switch
        {
            "CREATE_GRADE" => "新建年级",
            "CREATE_CLASS" => "进入建班",
            "PREPARE_SCREEN" => "前往登记",
            "OPEN_SCREEN" => "定位大屏",
            "OPEN_TEACHING" => "分配任课",
            "OPEN_CLASS_RESPONSIBILITY" => "办理班主任",
            "OPEN_GRADE_RESPONSIBILITY" => "办理年级组长",
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

    private static string Time(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
