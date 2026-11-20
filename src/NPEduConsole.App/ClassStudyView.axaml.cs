using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class ClassStudyView : UserControl
{
    private sealed record Target(ConsoleClient Client, string SchoolId, string TermId, Classroom Classroom);
    private Target? _target;
    private CancellationTokenSource? _request;
    private ClassStudySettings? _settings;
    private ClassStudyPreview? _preview;
    private bool _initialized;
    private bool _publishUnconfirmed;

    public event Action<ConsoleApiException>? SessionExpired;

    public ClassStudyView()
    {
        InitializeComponent();
        _initialized = true;
    }

    public void SetScope(ConsoleClient? client, string? schoolId, string? termId, Classroom? classroom)
    {
        var next = client is not null && schoolId is not null && termId is not null && classroom is not null
            ? new Target(client, schoolId, termId, classroom) : null;
        if (_target == next) return;
        var request = _request;
        _request = null;
        request?.Cancel();
        _target = next;
        _settings = null;
        _publishUnconfirmed = false;
        ForgetPreview();
        StudyDateInput.Text = SchoolToday().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        StudyStartInput.Text = "";
        StudyEndInput.Text = "";
        StudyRepeatPicker.SelectedIndex = 0;
        SetMessage(StudyPolicyMessage, null);
        ClassStudyEvidence.SetScope(client, schoolId, termId, classroom);
        StudyPolicyStatus.Text = next is null ? "请选择有当前学期的班级。" : "正在读取本班已有安排…";
        Render();
        if (next is not null) _ = ReadPolicyAsync();
    }

    private async void ReloadPolicyClicked(object? sender, RoutedEventArgs args)
    {
        if (_request is not null || _target is not { } target) return;
        ClassStudyEvidence.SetScope(target.Client, target.SchoolId, target.TermId, target.Classroom);
        await ReadPolicyAsync();
    }

    private async Task ReadPolicyAsync(string? savedMessage = null)
    {
        if (_request is not null || _target is not { } target) return;
        using var request = new CancellationTokenSource();
        _request = request;
        _settings = null;
        ForgetPreview();
        StudyPolicyStatus.Text = "正在读取本班已有安排…";
        SetMessage(StudyPolicyMessage, savedMessage);
        Render();
        try
        {
            var settings = await target.Client.GetClassStudySettingsAsync(target.SchoolId, target.TermId, target.Classroom.Id, request.Token);
            if (_request != request) return;
            _settings = settings;
            StudyPolicyStatus.Text = settings.Source switch
            {
                StudyPolicySource.Grade => "沿用年级自习策略",
                StudyPolicySource.Classroom => $"本班自习策略 · 第 {settings.Revision} 版",
                StudyPolicySource.Disabled => "当前学校自习策略已停用",
                _ => "本班还没有学校自习安排"
            };
            SetMessage(StudyPolicyMessage, settings.LegacyNoiseActive
                ? "本班仍使用旧版定时噪音监测。新自习策略会接管旧排程，请到旧学校管理页核对迁移。"
                : savedMessage ?? (_publishUnconfirmed ? "已读取学校当前安排。请先核对时段和版本，再决定下一步。" : null));
            _publishUnconfirmed = false;
            UpdateCurrentDate();
        }
        catch (Exception error) when (IsStudyError(error))
        {
            if (_request != request || NotifyExpired(error)) return;
            StudyPolicyStatus.Text = "暂无法读取本班自习安排";
            SetMessage(StudyPolicyMessage, savedMessage is null ? UserMessage(error) : $"{savedMessage} {UserMessage(error)}");
        }
        finally { Finish(request); }
    }

    private void StudyDraftChanged(object? sender, RoutedEventArgs args)
    {
        if (!_initialized) return;
        ForgetPreview();
        UpdateCurrentDate();
        Render();
    }

    private void StudyReviewChanged(object? sender, RoutedEventArgs args)
    {
        if (_initialized) Render();
    }

    private async void PreviewStudyClicked(object? sender, RoutedEventArgs args)
    {
        if (_request is not null || _target is not { } target || _settings is not { LegacyNoiseActive: false } settings) return;
        ForgetPreview();
        if (!ReadDraft(out var date, out var start, out var end)) return;
        var repeat = StudyRepeatPicker.SelectedIndex == 1 ? StudyRepeat.Weekly : StudyRepeat.Once;
        using var request = new CancellationTokenSource();
        _request = request;
        Render();
        try
        {
            var preview = await target.Client.PreviewClassStudyAsync(settings, date, start, end, repeat, request.Token);
            if (_request != request) return;
            _preview = preview;
            StudyPreviewHeading.Text = repeat == StudyRepeat.Once ? $"预览 · 仅 {date:yyyy-MM-dd} 新增一场" :
                $"预览 · 每{Weekdays[(int)date.DayOfWeek]}新增，首次适用 {date:yyyy-MM-dd}";
            string endDate = settings.Daily.To is { } until ? $"至 {until:yyyy-MM-dd}" : "无截止日期";
            StudyPreviewWindows.Text = $"日常 · {FormatWindows(preview.DailyWindows)}\n考试 · {FormatWindows(preview.ExamWindows)}";
            StudyPreviewFeatures.Text = $"{(repeat == StudyRepeat.Weekly ? $"{endDate}，按原日期例外计算。\n" : "")}" +
                $"本班已有 {preview.PairedDevices} 台配对设备。{FeatureSummary(settings)}";
            StudyReviewText.Text = settings.FreezesGradeInheritance
                ? "我已核对新增时段和采集设置，确认以后单独维护本班策略。"
                : "我已核对新增时段，并确认沿用本班现有的采集设置。";
            Dispatcher.UIThread.Post(() => { if (_preview == preview) StudyPreviewCard.BringIntoView(); }, DispatcherPriority.Loaded);
        }
        catch (Exception error) when (IsStudyError(error))
        {
            if (_request != request || NotifyExpired(error)) return;
            if (NeedsReload(error)) InvalidateSettings(UserMessage(error));
            else SetMessage(StudyEditorMessage, UserMessage(error));
        }
        finally { Finish(request); }
    }

    private async void PublishStudyClicked(object? sender, RoutedEventArgs args)
    {
        if (_request is not null || _target is not { } target || _preview is not { } preview ||
            RequiresReview && StudyReviewAcknowledged.IsChecked != true) return;

        using var request = new CancellationTokenSource();
        _request = request;
        Render();
        int? published = null;
        try
        {
            published = await target.Client.PublishClassStudyAsync(preview, Guid.NewGuid(), request.Token);
            if (_request != request) return;
            ForgetPreview();
            ClassStudyEvidence.SetScope(target.Client, target.SchoolId, target.TermId, target.Classroom);
        }
        catch (Exception error) when (IsStudyError(error))
        {
            if (_request != request || NotifyExpired(error)) return;
            ForgetPreview();
            _publishUnconfirmed = error is ConsoleApiException { Code: "STUDY_PUBLISH_UNCONFIRMED" };
            if (_publishUnconfirmed || NeedsReload(error)) InvalidateSettings(UserMessage(error));
            else SetMessage(StudyEditorMessage, UserMessage(error));
        }
        finally { Finish(request); }
        if (published is { } revision && _target == target)
        {
            await ReadPolicyAsync($"已发布本班第 {revision} 版安排。设备是否执行请查看下方回执。");
            ShowPolicyCard();
        }
    }

    private bool ReadDraft(out DateOnly date, out TimeOnly start, out TimeOnly end)
    {
        start = end = default;
        if (!TryDate(out date) || date < SchoolToday())
        {
            SetMessage(StudyEditorMessage, "请输入今天或之后的学校日期，格式为 YYYY-MM-DD。");
            StudyDateInput.Focus();
            return false;
        }
        if (!TimeOnly.TryParseExact(StudyStartInput.Text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out start) ||
            !TimeOnly.TryParseExact(StudyEndInput.Text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out end))
        {
            SetMessage(StudyEditorMessage, "请用 24 小时制填写开始与结束时间，例如 19:00 和 20:30。");
            StudyStartInput.Focus();
            return false;
        }
        return true;
    }

    private void UpdateCurrentDate()
    {
        if (_settings is not { } settings || !TryDate(out var date))
        {
            StudyDateBefore.Text = "";
            return;
        }
        var exception = settings.Daily.Exceptions.FirstOrDefault(entry => entry.Date == date);
        string note = exception?.Kind == "Skip" ? " · 日常原规则为停用" : exception?.Weekday is { } day ? $" · 日常按星期{RuleDays[day]}" : "";
        StudyDateBefore.Text = $"{date:yyyy-MM-dd} 原安排{note}\n日常 · {FormatWindows(settings.Daily.WindowsOn(date))}\n考试 · {FormatWindows(settings.Exam.WindowsOn(date))}";
    }

    private bool TryDate(out DateOnly date) => DateOnly.TryParseExact(StudyDateInput.Text, "yyyy-MM-dd",
        CultureInfo.InvariantCulture, DateTimeStyles.None, out date) && date.Year is >= 2000 and <= 9998;

    private void ForgetPreview()
    {
        _preview = null;
        StudyReviewAcknowledged.IsChecked = false;
        SetMessage(StudyEditorMessage, null);
    }

    private void InvalidateSettings(string message)
    {
        _settings = null;
        StudyPolicyStatus.Text = _publishUnconfirmed ? "发布结果待核对" : "班级安排待重新核对";
        SetMessage(StudyPolicyMessage, message);
        ShowPolicyCard();
    }

    private void ShowPolicyCard() => Dispatcher.UIThread.Post(() =>
    {
        if (_target is not null && IsEffectivelyVisible) StudyPolicyCard.BringIntoView();
    }, DispatcherPriority.Loaded);

    private bool RequiresReview => _settings is { } settings && (settings.FreezesGradeInheritance || settings.NoiseEnabled || settings.PhotosEnabled);

    private void Render()
    {
        bool busy = _request is not null;
        var settings = _settings;
        ReloadStudyPolicyButton.IsEnabled = !busy && _target is not null;
        StudyPolicyDetails.IsVisible = settings is not null;
        StudyEditor.IsVisible = settings is { LegacyNoiseActive: false };
        StudyDateInput.IsEnabled = StudyStartInput.IsEnabled = StudyEndInput.IsEnabled = StudyRepeatPicker.IsEnabled = !busy;
        PreviewStudyButton.IsEnabled = !busy && settings is not null;
        StudyPreviewCard.IsVisible = _preview is not null;
        StudyReviewAcknowledged.IsVisible = _preview is not null && RequiresReview;
        StudyReviewAcknowledged.IsEnabled = !busy;
        PublishStudyButton.IsEnabled = !busy && _preview is not null && (!RequiresReview || StudyReviewAcknowledged.IsChecked == true);
        if (settings is null) return;
        StudyPolicyFeatures.Text = FeatureSummary(settings);
        CurrentDailyRules.Text = DescribeRules("日常", settings.Daily);
        CurrentExamRules.Text = DescribeRules("考试", settings.Exam);
        StudyInheritanceNote.IsVisible = settings.FreezesGradeInheritance;
        StudyInheritanceNote.Text = settings.Source == StudyPolicySource.Grade
            ? "发布时会把当前生效的年级策略复制为本班策略，保留现有设置。之后年级修改将不再同步到本班。"
            : "发布将新建本班策略。之后年级修改将不再同步到本班。";
    }

    private void Finish(CancellationTokenSource request)
    {
        if (_request != request) return;
        _request = null;
        Render();
    }

    private bool NotifyExpired(Exception error)
    {
        if (error is not ConsoleApiException { StatusCode: 401 } expired) return false;
        SessionExpired?.Invoke(expired);
        return true;
    }

    private static void SetMessage(TextBlock control, string? message)
    {
        control.Text = message;
        control.IsVisible = !string.IsNullOrEmpty(message);
    }

    private static readonly string[] RuleDays = ["", "一", "二", "三", "四", "五", "六", "日"];
    private static readonly string[] Weekdays = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];
    private static DateOnly SchoolToday() => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).DateTime);
    private static string FeatureSummary(ClassStudySettings settings) =>
        $"作业展示{OnOff(settings.HomeworkEnabled)} · 噪音采集{OnOff(settings.NoiseEnabled)} · 照片{OnOff(settings.PhotosEnabled)}";
    private static string OnOff(bool active) => active ? "开启" : "关闭";

    private static string FormatWindows(IReadOnlyList<StudyWindow> windows) => windows.Count == 0 ? "无时段" :
        string.Join("、", windows.Select(window => $"{window.Start:HH:mm}–{(window.End.Date > window.Start.Date ? "次日 " : "")}{window.End:HH:mm}"));

    private static string DescribeRules(string mode, StudySchedule schedule)
    {
        string range = schedule.From is { } from ? $" · {from:yyyy-MM-dd} 起" : "";
        if (schedule.To is { } to) range += $"至 {to:yyyy-MM-dd}";
        string rules = schedule.Rules.Count == 0 ? "无每周规则" : string.Join("\n", schedule.Rules.Select(rule =>
            $"周{string.Join("、", rule.Days.Select(day => RuleDays[day]))}  {rule.Start:HH:mm}–{(rule.End <= rule.Start ? "次日 " : "")}{rule.End:HH:mm}"));
        return $"{mode}{range} · {schedule.Exceptions.Count} 条日期例外\n{rules}";
    }

    private static bool IsStudyError(Exception error) => error is ConsoleApiException or OperationCanceledException or HttpRequestException;
    private static bool NeedsReload(Exception error) => error is ConsoleApiException
        { Code: "STUDY_SETTING_VERSION_CONFLICT" or "STUDY_PREVIEW_CHANGED" or "TERM_NOT_ACTIVE" or "STUDY_CLASS_CHANGED" or "STUDY_LEGACY_NOISE_ACTIVE" };
    private static string UserMessage(Exception error) => error is ConsoleApiException service ? service.Message : "暂无法连接学校服务，请刷新后重试。";
}
