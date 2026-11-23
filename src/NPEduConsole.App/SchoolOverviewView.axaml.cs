using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed record OverviewTaskItem(SchoolOverviewTask Task, string ActionLabel, string Evidence)
{
    public string Message => Task.Message;
    public bool CanOpen => Task.Action != "INSPECT" && (Task.Action != "OPEN_WEB" || WebSection(Task) is not null);
    internal static string? WebSection(SchoolOverviewTask task) => task.Code switch
    {
        "NO_ACTIVE_TERM" => "terms",
        "ADMIN_CLASS_SCREEN_INACTIVE" when task.ClassId is not null => "screens",
        "SCREEN_NOT_ACTIVATED" or "SCREEN_WEB_AWAITING_HEARTBEAT" or "SCREEN_OFFLINE" or "SCREEN_DEGRADED" or
            "SCREEN_WEB_NEEDS_VERIFICATION" or "TERM_SCREEN_RETAINED_ORIGINAL" when task.ScreenId is not null => "screens",
        "DISABLED_ACCOUNT_HAS_ASSIGNMENT" or "LOCAL_ACCOUNT_NEVER_LOGGED_IN" when task.AccountId is not null => "accounts",
        "DISABLED_TEACHER_ASSIGNED" or "TEACHER_ACCESS_MISSING" when task.ClassId is not null || task.CourseGroupId is not null => "teachers",
        "TEACHING_ASSIGNMENT_SUBJECT_CONFLICT" or "MULTIPLE_PRIMARY_TEACHERS" when task.ClassId is not null => "structure",
        _ => null
    };
    public string AutomationId => "SchoolOverviewTask-" + Task.Code + "-" + (Task.ScreenId ?? Task.CourseGroupId ?? Task.ClassId ?? Task.GradeId ?? "school") +
        (Task.Action is "OPEN_TEACHING" or "OPEN_COURSE_GROUP_SOURCE" or "OPEN_COURSE_GROUP_TEACHING" ? "-" + Task.SubjectId + (Task.CourseGroupId is not null && Task.ClassId is not null ? "-" + Task.ClassId : "") : Task.AccountId is { } accountId ? "-" + accountId : "");
}

public sealed partial class SchoolOverviewView : UserControl
{
    private ConsoleClient? _client;
    private School? _school;
    private string? _termId;
    private CancellationTokenSource? _request;
    private SchoolOverview? _snapshot;
    private IReadOnlyList<Classroom> _classLinks = [];
    private Func<Button, bool>? _returnTarget;
    private string? _returnTaskId;
    private Vector _returnOffset;
    private Vector _returnQueueOffset;

    public event Action<ConsoleApiException>? SessionExpired;
    internal event Action<ConsoleApiException>? AuthorizationLost;
    public event Action<SchoolOverview, SchoolOverviewTask>? TaskRequested;
    public event Action<SchoolOverview?, string>? SnapshotChanged;
    public event Action? ClassDirectoryRequested;
    public event Action<Classroom>? ClassRequested;
    internal event Action? OpeningRequested;

    public SchoolOverviewView() => InitializeComponent();

    public void SetScope(ConsoleClient? client, School? school, string? termId = null)
    {
        if (_client == client && _school == school && _termId == termId) return;
        _client = client;
        _school = school;
        _termId = termId;
        _request?.Cancel();
        _request = null;
        OverviewTitle.Text = termId is null ? "学校总览" : "学期诊断";
        OpenSchoolOpeningButton.IsVisible = termId is null;
        OverviewTitle.FontSize = termId is null ? 29 : 19;
        RefreshSchoolOverviewButton.Content = termId is null ? "刷新总览" : "刷新诊断";
        OverviewContent.Margin = termId is null ? new Thickness(25, 20, 25, 24) : new Thickness(16, 8, 16, 12);
        _returnTarget = null;
        _returnTaskId = null;
        _returnOffset = _returnQueueOffset = default;
        OverviewTaskFilter.SelectedIndex = 0;
        OverviewScroll.Offset = default;
        ShowNavigationMessage(null);
        SetReadState("选择学校", "请在顶部选择有权管理的学校。");
        if (client is not null && school is not null) _ = RefreshAsync();
    }

    public bool Shows(SchoolOverview overview) => ReferenceEquals(_snapshot, overview);

    private void OpeningClicked(object? sender, RoutedEventArgs e) => OpeningRequested?.Invoke();

    internal void FocusOpeningEntry() => OpenSchoolOpeningButton.Focus(NavigationMethod.Tab);

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
            var overview = _termId is { } termId ? await client.GetTermOverviewAsync(school.Id, termId, request.Token) :
                await client.GetSchoolOverviewAsync(school.Id, request.Token);
            if (_request != request) return;
            _snapshot = overview;
            var counts = overview.Counts;
            OverviewHeadline.Text = overview.Term is null ? "暂无启用学期" : overview.Tasks.Count == 0
                ? "当前暂无待办" : $"有 {overview.Tasks.Count} 项事务待办理";
            OverviewCounts.Text = overview.Term is null ? null :
                $"{counts.Grades} 个年级   ·   {counts.AdministrativeClasses} 个行政班   ·   {counts.Screens} 台大屏";
            OverviewFeedback.Text = $"核对时间 {Time(overview.GeneratedAt)}" +
                (overview.Legacy ? " · 旧 KV 仅提供部分办理入口" : "");
            OverviewResults.IsVisible = true;
            OverviewClassLink.IsVisible = !overview.Scoped && overview.Term is not null;
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
            else if (error.StatusCode == 403 && _termId is not null)
            {
                SetReadState("学期诊断不可用", "当前账号已无本校管理权限。");
                AuthorizationLost?.Invoke(error);
            }
            else SetReadState("学校状态待核对", error.StatusCode is 404 or 426
                ? (_termId is null ? "当前 KV 未提供学校总览。仍可从班级与教务查看已授权班级。" :
                    "所选学期不可用，或当前 KV 尚未提供指定学期诊断。可返回学期目录核对。")
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
        OverviewProgress.IsVisible = OverviewResults.IsVisible = false;
        OverviewTasksList.ItemsSource = null;
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
        OverviewQueue.IsVisible = tasks.Length > 0;
        var filtered = tasks.Where(row => OverviewTaskFilter.SelectedIndex switch
        {
            1 => row.Task.Source is "TERM" or "TEACHING",
            2 => row.Task.Source == "SCREEN",
            3 => row.Task.Source is not ("TERM" or "TEACHING" or "SCREEN"),
            _ => true
        }).ToArray();
        OverviewTasksList.ItemsSource = filtered;
        OverviewNoTasks.IsVisible = filtered.Length == 0;
    }

    private async void RefreshClicked(object? sender, RoutedEventArgs args) => await RefreshAsync();

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
        string id = item.AutomationId;
        RememberReturn(button => AutomationProperties.GetAutomationId(button) == id, id);
        TaskRequested?.Invoke(snapshot, item.Task);
    }

    internal void ForgetReturnTarget() => RememberReturn(null);

    private void RememberReturn(Func<Button, bool>? match, string? taskId = null)
    {
        _returnTarget = match;
        _returnTaskId = taskId;
        _returnOffset = OverviewScroll.Offset;
        _returnQueueOffset = OverviewTasksList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.Offset ?? default;
    }

    private void RestoreTaskFocus(SchoolOverview overview)
    {
        if (!Shows(overview) || !IsEffectivelyVisible) return;
        OverviewScroll.Offset = _returnOffset;
        if (OverviewTasksList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroll)
            scroll.Offset = _returnQueueOffset;
        if (_returnTaskId is { } id && OverviewTasksList.Items.OfType<OverviewTaskItem>()
            .FirstOrDefault(item => item.AutomationId == id) is { } row)
        {
            OverviewTasksList.SelectedItem = row;
            OverviewTasksList.ScrollIntoView(row);
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (!Shows(overview) || !IsEffectivelyVisible) return;
            var target = _returnTarget is { } match ? this.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(button => button.IsEffectivelyVisible && match(button)) : null;
            if (target is not null)
            {
                target.BringIntoView();
                if (target.Focus(NavigationMethod.Tab)) return;
            }
            if (OverviewQueue.IsEffectivelyVisible) OverviewTaskFilter.Focus();
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
            "OPEN_SCREEN_DUTY" => task.Code == "SCREEN_WEB_ACCOUNT_REQUIRED" ? "配置网页账号" : "查看网页值守",
            "OPEN_TEACHING" => "分配任课",
            "OPEN_TERM_TEACHING" => "核对本学期教学安排",
            "OPEN_COURSE_GROUP_TEACHING" => "分配走班任课",
            "OPEN_COURSE_GROUP_SOURCE" => "核对走班来源",
            "OPEN_TERM_PREPARATION" => "打开学期管理",
            "OPEN_TEACHER_ACCOUNT" => "查看教师账号",
            "OPEN_CLASS_RESPONSIBILITY" => "办理班主任",
            "OPEN_GRADE_RESPONSIBILITY" => "办理年级组长",
            "OPEN_STAFF_REVIEW" => "打开师资核对",
            _ => "网页办理"
        };
        string source = task.Source switch
        {
            "TERM" => "学期与班级",
            "SCREEN" when task.Action == "OPEN_WEB" && task.ScreenId is not null => "网页大屏",
            "SCREEN" when task.Action == "OPEN_SCREEN" => "NPEduTools",
            "SCREEN" when task.Action == "OPEN_SCREEN_DUTY" => "网页值守",
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
