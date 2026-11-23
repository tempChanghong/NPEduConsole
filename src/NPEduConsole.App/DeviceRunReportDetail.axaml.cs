using Avalonia.Controls;
using NPEduConsole.Core;

namespace NPEduConsole.App;

/// <summary>
/// 一份教室设备运行报告（原生噪音会话）的只读详情。宿主只需调用 <see cref="SetReport"/>；
/// 传 null 时清空并回到「选择一份运行报告」的空态。
/// 口径沿用原网页的 NoiseReportList / nativeNoisePresentation：-160 是数值下限、-100 是趋势下限，
/// dBFS 与估算 dB(A) 分开展示，估算覆盖与原始覆盖分开说，绝不把原始读数标成 dB(A)。
/// </summary>
public sealed partial class DeviceRunReportDetail : UserControl
{
    private const double NumericalFloorDbfs = -160;
    private const double TrendMinimumDbfs = -100;

    public DeviceRunReportDetail()
    {
        InitializeComponent();
    }

    public DeviceRunReport? Report { get; private set; }

    public void SetReport(DeviceRunReport? report)
    {
        if (Report?.SessionId != report?.SessionId)
        {
            ReportIdentity.IsExpanded = false;
            ReportScroll.ScrollToHome();
        }
        Report = report;
        if (report is null)
        {
            Clear();
            return;
        }

        ReportEmpty.IsVisible = false;
        ReportBody.IsVisible = true;
        ReportHeading.Text = report.SourceLabel + " · " + report.OutcomeLabel;
        // 颜色交给 AXAML 的 .warn 样式（DynamicResource），主题切换时自动跟随，不在代码里缓存画笔。
        ReportHeading.Classes.Set("warn", report.Outcome is "Interrupted" or "Faulted");
        string periodLabel = report.Timing?.ClockDiscontinuity == true ? "报告起止（结束按设备计时推算）" : "报告起止";
        ReportWindow.Text = $"{periodLabel} {Moment(report.StartedAt)} → {Moment(report.EndedAt)}";

        SummarySampled.Text = Duration(report.Summary.SampledSeconds);
        SummaryElapsed.Text = Duration(report.Summary.ElapsedSeconds);
        SummaryCoverage.Text = Ratio(report.Summary.Coverage);
        SummaryClipped.Text = Ratio(report.Summary.ClippedPercent / 100);
        SummaryEnergy.Text = Dbfs(report.Summary.EnergyMeanDbfs);
        SummaryPeak.Text = Dbfs(report.Summary.PeakDbfs);

        var estimate = report.Estimate;
        EstimateMeasurements.IsVisible = estimate is not null;
        EstimateEnergyLabel.Text = estimate is null || string.IsNullOrWhiteSpace(estimate.TimeWeighting)
            ? "A 加权平滑电平能量均值"
            : "A/" + estimate.TimeWeighting + " 平滑电平能量均值";
        EstimateSampled.Text = estimate is null ? "—" : Duration(estimate.Summary.SampledSeconds);
        EstimateCoverage.Text = estimate is null ? "—" : Ratio(estimate.Summary.Coverage);
        EstimateEnergy.Text = estimate is null ? "无有效估算值" : Dba(estimate.Summary.EnergyMeanDbA);
        EstimatePeak.Text = estimate is null ? "无有效估算值" : Dba(estimate.Summary.MaximumDbA);
        EstimateStatus.Text = EstimateLabel(estimate?.Status);

        // 归属只描述入库那一刻的事实：视图可能已换班、换机，不能说成「当前班级」。
        Ownership.Text = report.Ownership is null
            ? "待核对"
            : "按入库时学校、学期和班级归属";
        // 学校时段沿用课表时钟，跨日时显示两侧日期。
        StudyWindow.Text = report.Study is { } study
            ? (study.Mode == "Exam" ? "考试" : "日常") + " · 学校时段（课表时钟） "
              + Calendar(study.Window.Start) + " → " + Calendar(study.Window.End)
            : "无自习规则信息";
        SessionText.Text = report.SessionId;

        var endReason = EndReasonText(report.SessionKind, report.EndReason);
        EndReasonLabel.IsVisible = EndReason.IsVisible = endReason is not null;
        EndReason.Text = endReason ?? string.Empty;

        // 收到时间是服务器事实；设备墙钟观测时刻是设备事实，两者并排但不互相解释。
        ReportTrace.Text = report.Timing is { } timing
            ? "服务器收到于 " + Moment(report.ReceivedAt) + " · 设备墙钟观测结束 " + Moment(timing.ObservedEndedAt)
            : "服务器收到于 " + Moment(report.ReceivedAt);

        var hints = Hints(report);
        ReportHintPanel.IsVisible = hints.Count > 0;
        ReportHints.Text = string.Join("\n", hints);
    }

    private void Clear()
    {
        ReportEmpty.IsVisible = true;
        ReportBody.IsVisible = false;
        foreach (var field in new[]
                 {
                     ReportHeading, ReportWindow, SummarySampled, SummaryElapsed, SummaryCoverage, SummaryClipped,
                     SummaryEnergy, SummaryPeak, EstimateSampled, EstimateCoverage, EstimateEnergy, EstimatePeak,
                     EstimateStatus, Ownership, StudyWindow, SessionText, EndReason, ReportTrace, ReportHints
                 })
            field.Text = string.Empty;
        EstimateEnergyLabel.Text = "平滑电平能量均值";
        ReportHintPanel.IsVisible = false;
        EstimateMeasurements.IsVisible = false;
        EndReasonLabel.IsVisible = EndReason.IsVisible = false;
        ReportHeading.Classes.Set("warn", false);
    }

    private static string Moment(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    // 课表时钟：不换时区、不省略日期，跨午夜时起止两侧各自带日期才读得对。
    internal static string Calendar(DateTime value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    // 原网页只对人工发起的会话解释结束原因；自动排程的结束由课表决定，不在这里编故事。
    internal static string? EndReasonText(string? sessionKind, string? endReason)
    {
        if (string.IsNullOrWhiteSpace(endReason)) return null;
        if (!string.Equals(sessionKind, "Manual", StringComparison.Ordinal)) return null;
        return endReason switch
        {
            "ScheduledTakeover" => "被学校定时自习接管，原手动会话已结束，不自动续开。",
            "ThreeHourLimit" => "达到三小时上限。",
            "PlannedEnd" => "到达指定结束时间。",
            "UserEnded" => "主动结束。",
            _ => "手动会话中断或结束；请核对本机报告。"
        };
    }

    // 时长不可为了「好看」把 0.6 秒四舍五入成 1 秒：4 秒分母 + 13.3% 覆盖会因此被读成 1/4。
    // 小于一分钟保留一位小数；分/秒按需保留同样分辨率；整值才用整数写法。
    internal static string Duration(double seconds)
    {
        var value = double.IsFinite(seconds) ? Math.Max(0, seconds) : 0;
        var total = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        if (total < 60) return Seconds(total) + " 秒";
        var minutes = (long)Math.Floor(total / 60);
        var rest = Math.Round(total - minutes * 60, 1);
        if (rest >= 60)
        {
            minutes += 1;
            rest -= 60;
        }
        return minutes < 60
            ? minutes.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 分 " + Seconds(rest, pad: true) + " 秒"
            : (minutes / 60).ToString(System.Globalization.CultureInfo.InvariantCulture) + " 小时 "
              + (minutes % 60).ToString("00", System.Globalization.CultureInfo.InvariantCulture) + " 分";
    }

    private static string Seconds(double value, bool pad = false)
    {
        var integral = Math.Abs(value - Math.Round(value)) < 1e-9;
        var whole = ((long)Math.Round(value)).ToString(pad ? "00" : "0", System.Globalization.CultureInfo.InvariantCulture);
        return integral ? whole : value.ToString(pad ? "00.0" : "0.0", System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static string Ratio(double value)
    {
        var clamped = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
        return (clamped * 100).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";
    }

    internal static string Dbfs(double? value) => value is not { } level || !double.IsFinite(level)
        ? "无有效值"
        : level <= NumericalFloorDbfs ? "≤ -160.0 dBFS" : level.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " dBFS";

    internal static string Dba(double? value) => value is { } level && double.IsFinite(level)
        ? level.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " 估算 dB(A)"
        : "无有效估算值";

    // Status 是「原始 reason 优先、回退 state」，所以这里按网页同一张表解释，不认识的只显示「估算状态未知」。
    internal static string EstimateLabel(string? status) => status switch
    {
        "DEVICE_CHANGED" => "麦克风变化，参考失效",
        "INPUT_SETTINGS_CHANGED" => "输入设置变化，参考失效",
        "INPUT_SETTINGS_UNAVAILABLE" => "无法核对输入设置，参考失效",
        "A_WEIGHTING_UNAVAILABLE" => "A 加权不可用",
        "REFERENCE_STORE_UNAVAILABLE" => "参考文件不可用",
        "Active" => "估算有效",
        "WarmingUp" => "A 加权预热中",
        "NoData" => "等待有效估算",
        "Stopped" => "已结束",
        "Invalidated" => "参考失效",
        null or "" => "尚无外部参考",
        _ => "估算状态未知"
    };

    internal static List<string> Hints(DeviceRunReport report)
    {
        var hints = new List<string>();
        if (report.Outcome is "Interrupted" or "Faulted")
            hints.Add("这份报告是部分统计：结束前的时间可能没有采到样本。");
        if (report.Timing?.ClockDiscontinuity == true)
            hints.Add("设备墙钟与监测计时存在差异，报告起止时间仍待核对。");
        if (report.Estimate?.Status is "DEVICE_CHANGED" or "INPUT_SETTINGS_CHANGED" or "INPUT_SETTINGS_UNAVAILABLE" or
            "A_WEIGHTING_UNAVAILABLE" or "REFERENCE_STORE_UNAVAILABLE" or "Invalidated")
            hints.Add(EstimateLabel(report.Estimate.Status) + "。");
        if (report.Summary.SampledSeconds <= 0)
            hints.Add("这段会话没有采集到有效样本。");
        else if (report.Summary.SampledSeconds * 2 < report.Summary.ElapsedSeconds)
            hints.Add("有效采样不足总时长的一半，统计只代表已采集的区间。");
        if (report.Summary.ClippedPercent > 0.1)
            hints.Add("削波超过 0.1%，含削波的区间不计入参考估算。");
        if (report.Summary.EnergyMeanDbfs is { } mean && mean <= TrendMinimumDbfs)
            hints.Add("原始输入接近静音（≤ -100 dBFS），读数可能受静音或降噪处理影响；请现场确认输入响应。");
        if (report.Summary.EnergyMeanDbfs is { } floor && floor <= NumericalFloorDbfs)
            hints.Add("原始读数已到数值下限，更低的输入不再显示精确值。");
        if (report.Estimate is null)
            hints.Add("没有可用的外部参考，本页不给出 dB(A) 估算。");
        else if (report.Estimate.Summary.Coverage + 0.05 < report.Summary.Coverage)
            hints.Add("估算覆盖明显低于原始覆盖，dB(A) 数值只代表部分区间。");
        // 迟到上报：数据来自会话结束之后才到达服务器，界面上要说明这不是「刚刚采集」。
        var lag = report.ReceivedAt - report.EndedAt;
        if (lag < TimeSpan.Zero)
            hints.Add("服务器收到时间早于设备报告的结束时间，时间依据待核对。");
        else if (report.Timing?.ClockDiscontinuity != true && lag >= TimeSpan.FromMinutes(2))
            hints.Add($"服务器在报告所示结束时刻约 {Duration(lag.TotalSeconds)} 后收到。");
        return hints;
    }
}
