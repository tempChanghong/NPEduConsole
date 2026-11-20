using System.Diagnostics;
using System.Globalization;

namespace NPEduConsole.Core;

public sealed record StudyEvidenceScope(string SchoolId, string TermId, string ClassId);
public sealed record EvidenceIdentity(string DeviceId);
public sealed record StudyReceiptConfig(string Version, EvidenceIdentity Identity, StudyEvidenceScope Scope);
public sealed record StudyObservation(string? Version, string? Phase, string? PolicyState,
    string? Mode = null, StudyReportWindow? Window = null);
public sealed record StudyExecution(string State, string Owner, string Reason, bool SignalFresh, StudyReportWindow? Window = null);
public sealed record StudyPresentation(string State);

public sealed record StudyEvidenceReceipt(StudyReceiptConfig Current, StudyObservation? Observation,
    bool Fresh, bool ConfigurationApplied, long? ObservationAgeMs, DateTimeOffset? ReceivedAt,
    StudyExecution? Execution = null, bool ExecutionFresh = false, string? ExecutionVersion = null,
    long? ExecutionAgeMs = null, bool CaptureVerified = false, StudyPresentation? Presentation = null,
    bool PresentationFresh = false, string? PresentationVersion = null, long? PresentationAgeMs = null,
    long? PresentationRemainingMs = null, bool PresentationVerified = false)
{
    internal long RequestStarted { get; init; } = Stopwatch.GetTimestamp();
    public bool ObservationFresh => Fresh && ObservationAgeMs is >= 0 &&
        ObservationAgeMs + Stopwatch.GetElapsedTime(RequestStarted).TotalMilliseconds < 15000;

    public string Describe()
    {
        if (Observation is null) return "等待教室回执";
        var elapsed = Stopwatch.GetElapsedTime(RequestStarted).TotalMilliseconds;
        bool Recent(long? serverAge, int limit) => serverAge is >= 0 && serverAge.Value + elapsed < limit;
        if (Observation.Version != Current.Version) return "已收到旧配置回执，等待教室应用当前安排";
        if (!ObservationFresh) return "上次回执已过期，刷新后可核对教室当前状态";
        if (!ConfigurationApplied || Observation.PolicyState != "APPLIED") return "教室尚未确认应用当前安排";

        var phase = Observation.Phase switch
        {
            "Preparing" => "准备开始", "Active" => "自习时段内", "Outside" => "自习时段外",
            "Disabled" => "自习已关闭", "Blocked" => "执行条件未满足", _ => "阶段待确认"
        };
        var mode = Observation.Mode switch { null or "Daily" => "日常", "Exam" => "考试", _ => "模式待确认" };
        var display = "展示状态待确认";
        if (PresentationFresh && PresentationVersion == Current.Version && Recent(PresentationAgeMs, 5000) &&
            PresentationRemainingMs > elapsed)
            display = Presentation?.State switch
            {
                "Visible" when PresentationVerified => "大屏已显示自习页", "Hidden" => "自习页已收起",
                "NotRequired" => "当前无需展示", "Blocked" => "展示条件未满足", _ => display
            };
        var capture = "定时采集状态待确认";
        if (ExecutionFresh && ExecutionVersion == Current.Version && Recent(ExecutionAgeMs, 15000) && Execution is { } run)
            capture = run switch
            {
                { Owner: "Schedule", State: "Active", SignalFresh: true, Reason: "WINDOW_ACTIVE", Window: { } running }
                    when CaptureVerified && Observation.Phase == "Active" && Observation.Window == running => "定时采集已收到信号",
                { Owner: "Manual" } => "手动监测中", { Reason: "WINDOW_SKIPPED" } => "本场采集已停止",
                { Reason: "WINDOW_FAILED" } => "本场采集失败", { Reason: "DISABLED" } => "定时采集已关闭",
                { State: "Starting" } => "定时采集正在启动", { Reason: "MICROPHONE_NOT_CONFIGURED" } => "教室尚未选择麦克风",
                { Reason: "OUTSIDE_WINDOW" } => "当前在采集时段外", _ => capture
            };
        var window = Observation.Window is { } interval && Observation.Phase is "Preparing" or "Active"
            ? $" · {interval.Label}" : "";
        return $"已应用当前安排 · {mode} · {phase}{window}\n{display} · {capture}";
    }
}

public sealed record StudyReportWindow(DateTime Start, DateTime End)
{
    public string Label => Start.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " 至 " +
        End.ToString(Start.Date == End.Date ? "HH:mm" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
public sealed record StudyFragmentSummary(double Coverage, double? EnergyMeanDbfs, double? PeakDbfs);
public sealed record StudyEstimateSummary(double Coverage, double? EnergyMeanDbA, double? MaximumDbA);
public sealed record StudyFragmentEstimate(StudyEstimateSummary Summary);
public sealed record StudyFragmentMeta(string Mode, StudyReportWindow Window);
public sealed record StudyFragmentTiming(DateTimeOffset ObservedEndedAt, bool ClockDiscontinuity);
public sealed record StudyReportFragment(string SessionId, DateTimeOffset StartedAt, DateTimeOffset EndedAt,
    DateTimeOffset ReceivedAt, string Outcome, StudyEvidenceScope Ownership, StudyFragmentMeta Study,
    StudyFragmentSummary Summary, StudyFragmentEstimate? Estimate = null, StudyFragmentTiming? Timing = null);
public sealed record StudyReportGroup(string GroupId, string DeviceId, string Mode, string ClockSource,
    StudyReportWindow Window, int FragmentCount, StudyReportFragment[]? Fragments = null);
public sealed record StudyReportPage(StudyReportGroup[] Items, string? Cursor);
