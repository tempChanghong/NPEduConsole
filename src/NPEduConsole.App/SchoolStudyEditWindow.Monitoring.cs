using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class SchoolStudyEditWindow
{
    private readonly bool _monitoring;
    private string? _dbfsMedium, _dbfsHigh, _estimateMedium, _estimateHigh;
    private bool _draftEstimated;
    private StudyMonitoring _initialMonitoring = null!;
    internal bool OpenGradeMonitoring { get; private set; }

    private void BindMonitoring(SchoolStudyPolicy? basis, string mode, string name, bool canOpenGrade)
    {
        Title = "学校自习监测策略";
        StudyEditTitle.Text = name + " · 监测策略";
        StudyEditFieldsTab.Header = "监测与照片";
        StudyScheduleMode.IsVisible = false;
        StudyMonitorFields.IsVisible = true;
        StudyMonitorGrade.IsVisible = canOpenGrade;
        var config = basis?.Monitoring ?? StudyMonitoring.Read(null);
        _initialMonitoring = config;
        string source = mode == "Disabled" || basis?.Mode == "Disabled" ? "已关闭" :
            basis is null ? "暂无学校自习策略" : mode == "Inherit" ? "继承年级" : _targetType == "GRADE" ? "年级策略" : "本班独立策略";
        StudyMonitorCurrent.Text = "当前 · " + source + (basis?.Mode == "Override" ? " · " + Measure(config) : "");
        MonitorUnit.ItemsSource = new[] { "信号电平 dBFS", "估算声级 dB(A)" };
        _draftEstimated = config.Estimated;
        MonitorUnit.SelectedIndex = config.Estimated ? 1 : 0;
        MonitorEnabled.IsChecked = config.Enabled;
        MonitorMedium.Text = config.Medium?.ToString(CultureInfo.CurrentCulture);
        MonitorHigh.Text = config.High?.ToString(CultureInfo.CurrentCulture);
        MonitorHighSeconds.Text = config.HighSeconds.ToString(CultureInfo.CurrentCulture);
        MonitorReminder.Text = config.ReminderSeconds.ToString(CultureInfo.CurrentCulture);
        MonitorPhotos.IsChecked = config.PhotosEnabled;
        MonitorPhotoSeconds.Text = config.PhotoSeconds.ToString(CultureInfo.CurrentCulture);
        MonitorRetention.Text = config.RetentionDays.ToString(CultureInfo.CurrentCulture);
        MonitorExtended.IsChecked = config.ExtendedRetentionAcknowledged;
    }

    private void MonitorTextChanged(object? sender, TextChangedEventArgs e) => MonitoringChanged();
    private void MonitorToggled(object? sender, RoutedEventArgs e) => MonitoringChanged();

    private void MonitorUnitChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_monitoring || _filling) return;
        bool estimated = MonitorUnit.SelectedIndex == 1;
        if (_draftEstimated == estimated) return;
        if (_draftEstimated) { _estimateMedium = MonitorMedium.Text; _estimateHigh = MonitorHigh.Text; }
        else { _dbfsMedium = MonitorMedium.Text; _dbfsHigh = MonitorHigh.Text; }
        _filling = true;
        MonitorMedium.Text = estimated ? _estimateMedium : _dbfsMedium;
        MonitorHigh.Text = estimated ? _estimateHigh : _dbfsHigh;
        _filling = false;
        _draftEstimated = estimated;
        Changed();
    }

    private void MonitoringChanged()
    {
        if (_monitoring && !_filling) Changed();
    }

    private void UpdateMonitoringControls(bool editing, bool own)
    {
        bool unchangedInherited = _targetType == "CLASS" && _catalog.Policy("CLASS", _targetId)?.Mode is null or "Inherit";
        StudyEditModeNote.Text = Mode switch
        {
            "Inherit" => unchangedInherited ? "查看年级策略，或选择本班独立设置。" : "恢复继承会采用年级完整策略，并清除本班独立设置。",
            "Disabled" => "该范围的学校自习已关闭。",
            _ when unchangedInherited => "本班改为独立后，整套自习策略将停止跟随年级。现有时段、展示和作业来源会复制到本班。",
            _ => "仅保存监测和照片字段，沿用已有排期、展示和作业来源。"
        };
        StudyMonitorFields.IsEnabled = editing && own;
        MonitorExtended.IsVisible = MonitorPhotos.IsChecked == true && int.TryParse(MonitorRetention.Text, out int days) && days > 30;
        StudyMonitorGrade.IsEnabled = editing && !_dirty;
        if (!own && (Mode == "Disabled" || unchangedInherited)) StudyEditPreview.IsEnabled = false;
    }

    private void OpenMonitoringGrade(object? sender, RoutedEventArgs e)
    {
        if (_busy || _dirty) return;
        OpenGradeMonitoring = true;
        Close();
    }

    private StudyMonitoring ReadMonitoring()
    {
        static double? Level(TextBox field)
        {
            if (string.IsNullOrWhiteSpace(field.Text)) return null;
            if (double.TryParse(field.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)) return value;
            field.Focus();
            throw new ArgumentException("请输入有效的阈值数字，或留空。");
        }
        static int Count(TextBox field)
        {
            if (int.TryParse(field.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value)) return value;
            field.Focus();
            throw new ArgumentException("时长、间隔与保留天数须填写整数。");
        }
        return new(MonitorEnabled.IsChecked == true, _draftEstimated, Level(MonitorMedium), Level(MonitorHigh),
            Count(MonitorHighSeconds), Count(MonitorReminder), MonitorPhotos.IsChecked == true, Count(MonitorPhotoSeconds),
            Count(MonitorRetention), MonitorExtended.IsChecked == true);
    }

    private StudyMonitoring MergeMonitoring(StudyMonitoring draft)
    {
        var original = _initialMonitoring;
        var current = _catalog.BasePolicy(_targetType, _targetId)?.Monitoring ?? StudyMonitoring.Read(null);
        bool thresholdsEdited = draft.Estimated != original.Estimated || draft.Medium != original.Medium || draft.High != original.High;
        return current with
        {
            Enabled = draft.Enabled != original.Enabled ? draft.Enabled : current.Enabled,
            Estimated = thresholdsEdited ? draft.Estimated : current.Estimated,
            Medium = thresholdsEdited ? draft.Medium : current.Medium,
            High = thresholdsEdited ? draft.High : current.High,
            HighSeconds = draft.HighSeconds != original.HighSeconds ? draft.HighSeconds : current.HighSeconds,
            ReminderSeconds = draft.ReminderSeconds != original.ReminderSeconds ? draft.ReminderSeconds : current.ReminderSeconds,
            PhotosEnabled = draft.PhotosEnabled != original.PhotosEnabled ? draft.PhotosEnabled : current.PhotosEnabled,
            PhotoSeconds = draft.PhotoSeconds != original.PhotoSeconds ? draft.PhotoSeconds : current.PhotoSeconds,
            RetentionDays = draft.RetentionDays != original.RetentionDays ? draft.RetentionDays : current.RetentionDays,
            ExtendedRetentionAcknowledged = draft.ExtendedRetentionAcknowledged != original.ExtendedRetentionAcknowledged
                ? draft.ExtendedRetentionAcknowledged : current.ExtendedRetentionAcknowledged
        };
    }

    private static string MonitoringSummary(StudyImpactPolicy policy, bool legacy, StudyImpactPolicy? counterpart = null)
    {
        string source = SchoolStudyWindow.SourceText(policy.Source, legacy);
        if (policy.Settings is null) return source;
        var actual = policy.Monitoring;
        string summary = source + "\n" + Measure(actual);
        if (counterpart?.Settings is null) return summary;
        var other = counterpart.Monitoring;
        if (actual.ReminderSeconds != other.ReminderSeconds) summary += $"\n提醒间隔 {actual.ReminderSeconds} 秒";
        if (actual.PhotoSeconds != other.PhotoSeconds) summary += $"\n拍照间隔 {actual.PhotoSeconds} 秒";
        return summary;
    }

    private static string Measure(StudyMonitoring setting)
    {
        string unit = setting.Estimated ? "估算 dB(A)" : "dBFS";
        var monitoring = setting.Enabled ? $"{unit} · 中线 {setting.Medium?.ToString("0.##") ?? "未设"} · 高线 {setting.High?.ToString("0.##") ?? "未设"} · 持续 {setting.HighSeconds} 秒"
            : $"监测关闭 · {unit} · 高线持续 {setting.HighSeconds} 秒";
        return monitoring + $"\n{(setting.PhotosEnabled ? "拍照启用" : "拍照关闭")} · 保留 {setting.RetentionDays} 天";
    }

    private static string RetentionEffect(StudyImpactRow row)
    {
        if (row.Effective.Settings is null) return "";
        int before = row.Before.Monitoring.RetentionDays, after = row.Effective.Monitoring.RetentionDays;
        if (after < before) return "\n按新上限缩短现有照片和退出记录的到期时间。已到期的在线记录将不再显示。";
        return after > before ? "\n既有记录沿用各自到期时间。" : "";
    }
}
