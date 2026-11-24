using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Avalonia.Controls.Primitives;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NPEduConsole.Core;
using Calendar = Avalonia.Controls.Calendar;

namespace NPEduConsole.App;

public sealed partial class SchoolAuditView : UserControl
{
    private sealed record FilterOption(string? Code, string Label)
    {
        public override string ToString() => Label;
    }

    private static readonly FilterOption[] Actors = [new(null, "全部来源"), new("ACCOUNT", "管理员与教师"),
        new("CLASSROOM_SCREEN", "班级大屏"), new("SETUP", "系统初始化")];
    private static readonly FilterOption[] Results = [new(null, "全部结果"), new("true", "成功"), new("false", "失败")];
    private static readonly JsonSerializerOptions MetadataJson = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ObservableCollection<SchoolAuditRow> _rows = [];
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private readonly Calendar _calendar = new() { FirstDayOfWeek = DayOfWeek.Monday };
    private readonly Flyout _calendarFlyout;
    private TextBox? _calendarTarget;
    private ConsoleClient? _client;
    private School? _school;
    private CancellationTokenSource? _request;
    private SchoolAuditQuery? _query;
    private string? _nextCursor;
    private bool _settingFilters, _filtersDirty, _moreFailed;

    public event Action? BackRequested;
    public event Action<ConsoleApiException>? SessionExpired;

    public SchoolAuditView()
    {
        InitializeComponent();
        AuditActorFilter.ItemsSource = Actors;
        AuditActionFilter.ItemsSource = new[] { new FilterOption(null, "全部操作") }.Concat(SchoolAuditRow.Actions
            .Select(pair => new FilterOption(pair.Key, pair.Value))
            .OrderBy(option => option.Label, StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), ignoreCase: false))).ToArray();
        AuditResultFilter.ItemsSource = Results;
        AuditList.ItemsSource = _rows;
        AuditActorFilter.SelectionChanged += FiltersChanged;
        AuditActionFilter.SelectionChanged += FiltersChanged;
        AuditResultFilter.SelectionChanged += FiltersChanged;
        AuditFromDate.TextChanging += FiltersChanged;
        AuditToDate.TextChanging += FiltersChanged;
        AuditFromDate.KeyDown += SubmitDateFilter;
        AuditToDate.KeyDown += SubmitDateFilter;

        var clearDate = new Button { Content = "清空日期", Classes = { "action" } };
        clearDate.Click += (_, _) => SetCalendarDate(null);
        _calendarFlyout = new Flyout { Content = new StackPanel { Spacing = 8, Children = { _calendar, clearDate } } };
        _calendarFlyout.Closed += (_, _) => _calendarTarget = null;
        FlyoutBase.SetAttachedFlyout(OpenAuditFromCalendar, _calendarFlyout);
        FlyoutBase.SetAttachedFlyout(OpenAuditToCalendar, _calendarFlyout);
        _calendar.PropertyChanged += (_, args) =>
        {
            if (args.Property == Calendar.SelectedDateProperty && _calendar.SelectedDate is { } date)
                SetCalendarDate(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        };
    }

    public void SetScope(ConsoleClient? client, School? school)
    {
        if (ReferenceEquals(_client, client) && _school == school) return;
        _calendarFlyout.Hide();
        CancelRead();
        ClearResults();
        _client = client;
        _school = school;
        AuditSchoolName.Text = school?.Name;
        SetStatus(null);

        _settingFilters = true;
        AuditActorFilter.SelectedIndex = AuditActionFilter.SelectedIndex = AuditResultFilter.SelectedIndex = 0;
        AuditFromDate.Clear();
        AuditToDate.Clear();
        _settingFilters = false;
        _filtersDirty = false;
        UpdateActions();
        if (client is not null && school is not null) _ = LoadAsync(more: false);
    }

    private void BackClicked(object? sender, RoutedEventArgs args) => BackRequested?.Invoke();

    private void FiltersChanged(object? sender, EventArgs args)
    {
        if (_settingFilters || _school is null) return;
        CancelRead();
        ClearResults();
        _filtersDirty = true;
        SetStatus("筛选条件已变更，请应用筛选。");
        UpdateActions();
    }

    private async void QueryClicked(object? sender, RoutedEventArgs args) => await LoadAsync(more: false);
    private async void ReadMoreClicked(object? sender, RoutedEventArgs args) => await LoadAsync(more: true);

    private async void SubmitDateFilter(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter) return;
        args.Handled = true;
        await LoadAsync(more: false);
    }

    private void OpenCalendarClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is not Button button) return;
        var target = button == OpenAuditFromCalendar ? AuditFromDate : AuditToDate;
        _calendarFlyout.Hide();
        _calendarTarget = null;
        var date = TryReadDate(target.Text, out var parsed) ? parsed?.ToDateTime(TimeOnly.MinValue) : null;
        _calendar.SelectedDate = date;
        _calendar.DisplayMode = CalendarMode.Month;
        _calendar.DisplayDate = date ?? DateTime.Today;
        _calendarTarget = target;
        _calendarFlyout.ShowAt(button);
    }

    private void SetCalendarDate(string? date)
    {
        if (_calendarTarget is not { } target) return;
        target.Text = date;
        _calendarFlyout.Hide();
        target.Focus();
    }

    private async Task LoadAsync(bool more)
    {
        if (_client is not { } client || _school is not { } school || _request is not null) return;

        SchoolAuditQuery query;
        if (more)
        {
            if (_query is not { } previous || _nextCursor is not { } cursor) return;
            query = previous with { Cursor = cursor };
        }
        else
        {
            ClearResults();
            if (!TryBuildQuery(out query))
            {
                SetStatus("请输入有效的开始和结束日期（YYYY-MM-DD），结束日期应在开始日期当天或之后。", isError: true);
                UpdateActions();
                return;
            }
            _query = query;
            _filtersDirty = false;
        }

        using var request = new CancellationTokenSource();
        _request = request;
        SetStatus(more ? "正在加载更多审计记录…" : "正在读取审计记录…");
        UpdateActions();
        try
        {
            var page = await client.GetSchoolAuditLogsAsync(school.Id, query, request.Token);
            if (_request != request) return;
            foreach (var entry in page.Items)
                if (_ids.Add(entry.Id)) _rows.Add(new(entry));
            _nextCursor = page.NextCursor;
            _moreFailed = false;
            SetStatus(_rows.Count == 0 ? "暂无符合条件的审计记录。" : null);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (ConsoleApiException error)
        {
            if (_request != request) return;
            if (error.StatusCode is 401 or 403)
            {
                ClearResults();
                SetStatus(error.StatusCode == 403 ? "当前账号已无权查看该学校的审计记录。可刷新核对权限。" : error.Message, isError: true);
                if (error.StatusCode == 401) SessionExpired?.Invoke(error);
            }
            else
            {
                _moreFailed = more;
                SetStatus((more ? "加载更多失败。" : "读取审计记录失败。") + error.Message, isError: true);
            }
        }
        finally
        {
            if (_request == request)
            {
                _request = null;
                UpdateActions();
            }
        }
    }

    private bool TryBuildQuery(out SchoolAuditQuery query)
    {
        query = new();
        if (!TryReadDate(AuditFromDate.Text, out var first) || !TryReadDate(AuditToDate.Text, out var last) || first > last ||
            last == DateOnly.MaxValue) return false;

        var zone = TimeZoneInfo.Local;
        DateTimeOffset? from = first is { } start ? StartOfLocalDay(start, zone) : null;
        DateTimeOffset? before = last is { } end ? StartOfLocalDay(end.AddDays(1), zone) : null;
        if (from >= before) return false;

        query = new((AuditActorFilter.SelectedItem as FilterOption)?.Code, (AuditActionFilter.SelectedItem as FilterOption)?.Code,
            bool.TryParse((AuditResultFilter.SelectedItem as FilterOption)?.Code, out bool success) ? success : null,
            From: from, To: before?.AddMilliseconds(-1), Before: before);
        return true;
    }

    private static bool TryReadDate(string? text, out DateOnly? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ||
            parsed == DateOnly.MinValue) return false;
        date = parsed;
        return true;
    }

    private static DateTimeOffset StartOfLocalDay(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        // Local midnight can be skipped or repeated when the time zone changes offset.
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private void SetStatus(string? message, bool isError = false)
    {
        AuditStatus.Text = message;
        AuditStatus.IsVisible = message is not null;
        AuditStatus.Classes.Set("audit-failed", isError);
    }

    private void UpdateActions()
    {
        QueryAuditButton.Content = _filtersDirty ? "应用筛选" : "刷新";
        QueryAuditButton.IsEnabled = _school is not null && _request is null;
        MoreAuditButton.Content = _moreFailed ? "重试加载更多" : "加载更多";
        MoreAuditButton.IsVisible = _nextCursor is not null;
        MoreAuditButton.IsEnabled = _request is null;
    }

    private void CancelRead()
    {
        var request = _request;
        _request = null;
        request?.Cancel();
    }

    private void ClearResults()
    {
        _query = null;
        _nextCursor = null;
        _moreFailed = false;
        _rows.Clear();
        _ids.Clear();
        ClearDetails();
    }

    private void AuditListKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || args.Source is Button || AuditList.SelectedItem is not SchoolAuditRow row) return;
        args.Handled = true;
        ShowDetails(row);
    }

    private void ShowDetailsClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: SchoolAuditRow row } && _ids.Contains(row.Entry.Id))
        {
            AuditList.SelectedItem = row;
            ShowDetails(row);
        }
    }

    private void ShowDetails(SchoolAuditRow row)
    {
        var entry = row.Entry;
        AuditDetailTitle.Text = row.Title;
        AuditDetailTitle.Classes.Set("audit-failed", row.Failed);
        AuditDetailActor.Text = $"{row.Actor} · {row.Source}";
        AuditDetailTime.Text = row.LocalTime;
        AuditDetailAction.Text = row.Action;
        AuditDetailEntity.Text = row.Entity(full: true) ?? "未记录具体对象";
        AuditDetailRequest.Text = $"{entry.RequestMethod ?? "—"} {entry.RequestPath ?? "—"}";
        AuditDetailResult.Text = $"HTTP {entry.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "—"} · {row.Result}";
        AuditDetailIp.Text = string.IsNullOrWhiteSpace(entry.ClientIp) ? "未记录" : entry.ClientIp;
        AuditDetailId.Text = entry.Id;
        AuditDetailMetadata.Text = entry.Metadata is { } metadata ? JsonSerializer.Serialize(metadata, MetadataJson) : null;
        AuditDetailParameters.IsVisible = AuditDetailMetadata.Text is not null;
        AuditSearch.IsVisible = false;
        AuditDetails.IsVisible = true;
        AuditDetailScroll.Offset = default;
        CloseAuditDetailsButton.Focus();
    }

    private void CloseDetailsClicked(object? sender, RoutedEventArgs args)
    {
        ClearDetails();
        (AuditList.SelectedItem is { } item && AuditList.ContainerFromItem(item) is { } container ? container : AuditList).Focus();
    }

    private void ClearDetails()
    {
        AuditDetails.IsVisible = false;
        AuditDetailParameters.IsVisible = false;
        AuditDetailTitle.Text = AuditDetailActor.Text = AuditDetailTime.Text = AuditDetailAction.Text = AuditDetailEntity.Text =
            AuditDetailRequest.Text = AuditDetailResult.Text = AuditDetailIp.Text = AuditDetailId.Text = AuditDetailMetadata.Text = null;
        AuditSearch.IsVisible = true;
    }
}

internal sealed class SchoolAuditRow(SchoolAuditEntry entry)
{
    internal static readonly Dictionary<string, string> Actions = new(StringComparer.Ordinal)
    {
        ["SCHOOL_PROFILE_UPDATED"] = "学校设置", ["SUBJECT_CREATED"] = "创建学科", ["SUBJECT_UPDATED"] = "修改学科",
        ["GRADE_CREATED"] = "创建年级", ["GRADE_UPDATED"] = "修改年级", ["ADMIN_CLASS_CREATED"] = "创建行政班",
        ["ADMIN_CLASSES_BATCH_CREATED"] = "批量创建行政班", ["ADMIN_CLASS_UPDATED"] = "修改行政班", ["ORGANIZATION_IMPORT"] = "导入学校组织",
        ["SCHOOL_ORGANIZATION_IMPORT_PREVIEW"] = "预览开学组织导入", ["SCHOOL_ORGANIZATION_IMPORT"] = "导入年级教学组织",
        ["TEACHER_ALLOCATION_PREVIEW"] = "预览教师名册与空间分配", ["TEACHER_ALLOCATION"] = "保存教师名册与空间分配",
        ["ARCHIVED_WORKSPACE_INVITATION_REVOKED"] = "撤回归档教学空间邀请",
        ["SCHOOL_MIGRATION_EXPORT_CREATED"] = "生成整校迁移包", ["SCHOOL_MIGRATION_IMPORT_COMPLETED"] = "导入整校迁移包",
        ["TERM_TRANSITION_PREVIEW"] = "预览学期切换", ["TERM_ACTIVATION_PREVIEW"] = "检查学期启用条件", ["TERM_ACTIVATED"] = "切换启用学期",
        ["TERM_DRAFT_CREATED"] = "建立学期草稿", ["TERM_STATUS_CHANGED"] = "修改学期状态",
        ["TERM_DETAILS_PREVIEW"] = "预览学期资料", ["TERM_DETAILS_CHANGED"] = "保存学期资料", ["SCREEN_COMMAND_ISSUED"] = "下发大屏值守指令",
        ["SCREEN_DEVICE_RESET"] = "重置大屏设备", ["SCREEN_BOUND"] = "绑定班级大屏", ["SCREEN_ACCOUNT_CREATED"] = "创建大屏账号",
        ["FIRST_CLASSROOM_SCREEN_PREPARED"] = "登记首台教室大屏",
        ["SCREEN_ACCOUNT_UPDATED"] = "修改大屏账号", ["TEACHING_ASSIGNMENT_PREVIEW"] = "预览任课关系",
        ["TEACHING_ASSIGNMENT_CHANGED"] = "修改任课关系", ["GRADE_LEADERSHIP_CHANGED"] = "修改年级组长职责",
        ["CLASS_LEADERSHIP_CHANGED"] = "修改班主任职责", ["LOCAL_ACCOUNT_CHANGED"] = "修改本地账号", ["LOCAL_ACCOUNT_PROVISIONED"] = "批量创建本地账号",
        ["TEACHER_ACCOUNT_CREATED"] = "创建教师账号",
        ["SCHOOL_MANAGEMENT_ACCOUNT_PREVIEW"] = "预览学校账号操作", ["SCHOOL_MANAGEMENT_ACCOUNT_CHANGED"] = "办理学校账号操作",
        ["STAFF_CONFIGURATION_IMPORT"] = "导入人员配置",
        ["WORKSPACE_MEMBERSHIP_CHANGED"] = "修改教学空间成员", ["HOMEWORK_SETTINGS_CHANGED"] = "修改作业设置",
        ["SUBJECT_RULES_PREVIEW"] = "预览整班授课规则", ["SUBJECT_RULES_CHANGED"] = "修改授课规则",
        ["COURSE_GROUP_CREATED"] = "创建走班教学班", ["COURSE_GROUP_UPDATED"] = "修改走班教学班", ["COURSE_GROUP_CHANGED"] = "修改走班教学班",
        ["SCREEN_ROSTER_CHANGED"] = "大屏修改学生名单", ["ADMIN_ROSTER_CHANGED"] = "管理员修改学生名单", ["CLASS_ROSTER_SAVED"] = "名单变更记录",
        ["SCREEN_ATTENDANCE_CHANGED"] = "大屏修改考勤", ["SCREEN_PUBLICATION_CREATED"] = "大屏录入作业", ["SCREEN_PUBLICATION_UPDATED"] = "大屏修改作业",
        ["SCREEN_PUBLICATION_RESTORED"] = "大屏恢复作业版本", ["SCREEN_BOARD_COPIED"] = "复制大屏作业板",
        ["POST_ADMIN_OPERATION"] = "新增管理数据", ["PUT_ADMIN_OPERATION"] = "保存管理数据", ["PATCH_ADMIN_OPERATION"] = "修改管理数据",
        ["DELETE_ADMIN_OPERATION"] = "删除管理数据"
    };

    public SchoolAuditEntry Entry { get; } = entry;
    public string Title => string.IsNullOrWhiteSpace(Entry.Summary) ? Action : Entry.Summary;
    public string Actor => new[] { Entry.ActorScreen?.Name, Entry.ActorAccount?.Name, Entry.ActorAccount?.LocalUsername, Entry.ActorAccount?.Email }
        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? (Entry.ActorType == "SETUP" ? "系统" : "未知操作者");
    public string Source => Entry.ActorType switch
    {
        "ACCOUNT" => "管理员与教师", "CLASSROOM_SCREEN" => "班级大屏", "SETUP" => "系统初始化", _ => Entry.ActorType
    };
    public string LocalTime => Entry.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    public string Action => Actions.GetValueOrDefault(Entry.Action, Entry.Action);
    public string ActorAndTime => $"{Actor} · {LocalTime}";
    public string? ActionAndEntity => Title == Action ? Entity(full: false) :
        Entity(full: false) is { } entity ? $"{Action} · {entity}" : Action;
    public bool HasSecondaryLine => ActionAndEntity is not null;
    public string Result => Entry.Success ? "成功" : "失败";
    public bool Failed => !Entry.Success;

    public string? Entity(bool full)
    {
        var type = Entry.EntityType switch
        {
            "SCHOOL" => "学校", "SUBJECT" => "学科", "GRADE" => "年级", "WORKSPACE" => "班级或教学班", "ACCOUNT" => "账号",
            "SCREEN" => "班级大屏", "TERM" => "学期", "PUBLICATION" => "作业或通知", _ => Entry.EntityType
        };
        if (string.IsNullOrWhiteSpace(Entry.EntityId)) return type;
        var id = Entry.EntityId;
        if (!full && id.Length > 8) id = id[..8];
        return string.IsNullOrWhiteSpace(type) ? id : $"{type} {id}";
    }
}
