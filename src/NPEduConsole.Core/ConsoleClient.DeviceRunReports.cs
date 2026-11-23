namespace NPEduConsole.Core;

public sealed record DeviceRunReportSummary(double SampledSeconds, double ElapsedSeconds, double Coverage,
    double? EnergyMeanDbfs, double? PeakDbfs, double ClippedPercent);
public sealed record DeviceRunEstimateSummary(double SampledSeconds, double Coverage, double? EnergyMeanDbA, double? MaximumDbA);
public sealed record DeviceRunReportEstimate(string TimeWeighting, string Status, DeviceRunEstimateSummary Summary);
public sealed record DeviceRunReport(string SessionId, DateTimeOffset StartedAt, DateTimeOffset EndedAt, DateTimeOffset ReceivedAt,
    string Outcome, string? SessionKind, string? EndReason, DeviceRunReportSummary Summary, DeviceRunReportEstimate? Estimate,
    StudyFragmentTiming? Timing, StudyEvidenceScope? Ownership, StudyFragmentMeta? Study)
{
    public string SourceLabel => SessionKind switch
    {
        "Manual" => "手动自习", "Scheduled" => "学校排程", _ => "来源待核对"
    };
    public string OutcomeLabel => Outcome switch
    {
        "Stopped" => "已结束", "Interrupted" => "程序中断，部分统计", "Faulted" => "采集异常", _ => "结束原因待核对"
    };
}
public sealed record DeviceRunReports(DeviceRunReport[] Items, bool ScheduleReadFailed);
public sealed record DeviceRunClassReport(DeviceRunReport Report, StudyReportGroup Group);

public sealed partial class ConsoleClient
{
    private sealed record DeviceReportWire(string SessionId, DateTimeOffset StartedAt, DateTimeOffset EndedAt, DateTimeOffset ReceivedAt,
        string Outcome, DeviceRunReportSummary Summary, string? SessionKind = null, string? EndReason = null, EstimateWire? Estimate = null,
        StudyFragmentTiming? Timing = null, StudyEvidenceScope? Ownership = null, StudyFragmentMeta? Study = null);
    private sealed record EstimateWire(string TimeWeighting, string State, DeviceRunEstimateSummary Summary, string? Reason = null);
    private sealed record NativeReports(DeviceReportWire[] Reports);
    private sealed record ScheduleRecord(string SessionId, string Version, StudyReportWindow Window);
    private sealed record DeviceSchedule(ScheduleRecord[] Sessions);
    private sealed record ClassRunWire(System.Text.Json.JsonElement[] Items);
    private sealed record ClassRunFragments(DeviceReportWire[] Fragments);

    public async Task<DeviceRunReports> ReadDeviceRunReportsAsync(DeviceFieldScope scope, CancellationToken ct)
    {
        var session = CurrentSession;
        string path = $"api/v2/npep/schools/{Segment(scope.SchoolId)}/devices/{Segment(scope.DeviceId)}";
        var state = await ReadDeviceFieldStateAsync(scope, ct);
        if (state.Target.State != "ACTIVE" || state.CurrentDevice?.DeviceId != scope.DeviceId)
            throw new ConsoleApiException(409, "原设备已更换、解绑或授权到期，无法读取设备历史。可查看按原班保存的场次。", "DEVICE_REPORT_UNAVAILABLE");
        NativeReports reports;
        var schedule = Array.Empty<ScheduleRecord>();
        bool scheduleFailed = false;
        try
        {
            reports = await RequestAsync<NativeReports>(HttpMethod.Get, path + "/noise", session.Token, ct, "0.6");
            try
            {
                schedule = (await RequestAsync<DeviceSchedule>(HttpMethod.Get, path + "/noise-schedule", session.Token, ct, "0.7")).Sessions;
                if (schedule is null || schedule.Length > 200) throw InvalidResponse();
            }
            catch (ConsoleApiException error) when (error.StatusCode is not (401 or 403)) { scheduleFailed = true; schedule = []; }
        }
        catch (ConsoleApiException error) when (error.StatusCode == 401)
        {
            await VerifyDeviceDiagnosticsAccessAsync(scope.SchoolId, ct);
            throw new ConsoleApiException(409, "原设备授权已变化，请返回核对。", "DEVICE_REPORT_UNAVAILABLE");
        }
        var verified = await ReadDeviceFieldStateAsync(scope, ct);
        CheckCreationSession(session, ct);
        if (verified.Target.BindingRevision != state.Target.BindingRevision || verified.Target.State != "ACTIVE" ||
            verified.CurrentDevice?.DeviceId != scope.DeviceId)
            throw new ConsoleApiException(409, "读取期间设备或绑定已变化，请返回核对。", "DEVICE_REPORT_UNAVAILABLE");
        if (reports.Reports is null || reports.Reports.Length > 20 ||
            reports.Reports.Select(row => row?.SessionId).Distinct(StringComparer.Ordinal).Count() != reports.Reports.Length)
            throw InvalidResponse();
        var ownership = new StudyEvidenceScope(scope.SchoolId, scope.TermId, scope.ClassId);
        return new(reports.Reports.Select(row => ConvertDeviceReport(row, ownership, schedule)).ToArray(), scheduleFailed);
    }

    public async Task<DeviceRunClassReport?> FindDeviceRunClassReportAsync(DeviceFieldScope scope, string sessionId, CancellationToken ct)
    {
        if (!ValidRecordId(sessionId)) throw new ArgumentException("无有效的采样会话编号。", nameof(sessionId));
        var current = CurrentSession;
        var ownership = new StudyEvidenceScope(scope.SchoolId, scope.TermId, scope.ClassId);
        string path = StudyReportsPath(ownership) + $"&deviceId={Segment(scope.DeviceId)}&sessionId={Segment(sessionId)}";
        ClassRunWire data;
        try { data = await RequestAsync<ClassRunWire>(HttpMethod.Get, path, current.Token, ct, "0.17"); }
        catch (ConsoleApiException error) when (error.StatusCode == 404) { CheckCreationSession(current, ct); return null; }
        CheckCreationSession(current, ct);
        if (data.Items is not [var item]) throw InvalidResponse();
        var group = ReadValue<StudyReportGroup>(item);
        if (!ValidStudyGroup(group) || group.DeviceId != scope.DeviceId || group.FragmentCount != 1 ||
            group.Fragments is not [{ } fragment] || fragment.SessionId != sessionId || fragment.Ownership != ownership ||
            fragment.Study.Mode != group.Mode || fragment.Study.Window != group.Window ||
            group.Photos is null || group.Photos.Length > 300 || group.Photos.Any(p => p is null || !ValidRecordId(p.EventId)) ||
            group.Events is null || group.Events.Length > 300 || group.Events.Any(e => e is null || !ValidRecordId(e.EventId) ||
                e.PhotoEventId is not null && e.PhotoEventId != e.EventId)) throw InvalidResponse();
        var details = ReadValue<ClassRunFragments>(item);
        if (details.Fragments is not [{ } row]) throw InvalidResponse();
        return new(ConvertDeviceReport(row, ownership, []), group);
    }

    private static DeviceRunReport ConvertDeviceReport(DeviceReportWire? row, StudyEvidenceScope scope, ScheduleRecord[] legacy)
    {
        if (row is null || !ValidRecordId(row.SessionId) || row.Summary is null || row.StartedAt == default || row.ReceivedAt == default ||
            row.EndedAt < row.StartedAt || row.Summary.ElapsedSeconds is < 0 or > 172800 ||
            row.Summary.SampledSeconds is < 0 or > 172800 || row.Summary.Coverage is < 0 or > 1 ||
            row.Summary.ClippedPercent is < 0 or > 100)
            throw InvalidResponse();
        if (row.Ownership is not null && row.Ownership != scope)
            throw new ConsoleApiException(409, "返回的报告归属与原班级不一致，请返回核对原设备和班级。", "DEVICE_REPORT_UNAVAILABLE");
        var matched = legacy.Where(item => item is not null && item.SessionId == row.SessionId).ToArray();
        bool trustedStudy = row.Ownership == scope && row.Study is { } s && s.Mode is "Daily" or "Exam" && ValidWindow(s.Window);
        var study = trustedStudy ? row.Study : null;
        if (study is null && row.SessionKind != "Manual" && matched is [var first, ..] && StudyHash(first.Version) && ValidWindow(first.Window) &&
            matched.All(item => item.Version == first.Version && item.Window == first.Window)) study = new("Daily", first.Window);
        var estimate = row.Estimate is { Summary: { } values } entry && values.Coverage is >= 0 and <= 1 &&
            values.SampledSeconds is >= 0 and <= 172800
            ? new DeviceRunReportEstimate(entry.TimeWeighting, entry.Reason ?? entry.State, values) : null;
        string? source = row.SessionKind == "Manual" ? "Manual" : study is not null ? "Scheduled" : null;
        return new(row.SessionId, row.StartedAt, row.EndedAt, row.ReceivedAt, row.Outcome, source, row.EndReason,
            row.Summary, estimate, row.Timing, trustedStudy ? row.Ownership : null, study);
    }

    private static bool ValidWindow(StudyReportWindow? window) => window is { } && window.Start < window.End &&
        window.Start.Kind == DateTimeKind.Unspecified && window.End.Kind == DateTimeKind.Unspecified;
}
