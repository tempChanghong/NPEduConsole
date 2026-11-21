using System.Diagnostics;

namespace NPEduConsole.Core;

public sealed record School(string Id, string Name);
public sealed record Classroom(string Id, string Name, string? GradeName, IReadOnlyList<Screen> Screens)
{
    public string? TermId { get; init; }
    public string? GradeId { get; init; }
    public string? Code { get; init; }
    public bool IsActive { get; init; } = true;
    public bool HasInactiveScreens { get; internal init; }
    internal FirstScreenScope? FirstScreen { get; init; }
    public bool CanPrepareFirstScreen => FirstScreen is { } scope && Id == scope.ClassId && TermId == scope.TermId && Screens.Count == 0;
}
internal sealed record FirstScreenScope(Guid SessionId, string SchoolId, string TermId, string ClassId);
public sealed record Screen(string Id, string Name, IReadOnlyList<Device> Devices)
{
    public bool DeviceOverviewUnavailable { get; init; }
}
public sealed record Device(string Id, string Name, string Connectivity, DateTimeOffset? LastSeenAt);
public sealed record Microphone(string Id, string Name);

internal sealed record EquipmentSource(Guid SessionId, string SchoolId, string DeviceId, Policy Policy,
    DateTimeOffset ServerTime, long RequestStarted);

public sealed record Equipment(long PublishedRevision, long? AppliedRevision, bool Locked,
    bool? LocallyUnlocked, string? MicrophoneName, DateTimeOffset? ReceivedAt, string? ErrorCode)
{
    internal EquipmentSource? Source { get; init; }
    public IReadOnlyList<Microphone> Microphones { get; internal init; } = [];
    public string? MicrophoneId => Source?.Policy.MicrophoneId;
    public string? ReportedMicrophoneId { get; internal init; }
    public string? ReportedMicrophoneName { get; internal init; }
    public bool ServiceSupportsLocalMicrophoneChange => Source?.Policy.AllowLocalMicrophoneChange is not null;
    public bool AllowsLocalMicrophoneChange => Source?.Policy.AllowLocalMicrophoneChange == true;
    public bool DeviceSupportsLocalMicrophoneChange { get; internal init; }
    public string? LocalMicrophoneOverrideId { get; internal init; }
    public bool HasCurrentReceipt => ReceivedAt.HasValue && AppliedRevision == PublishedRevision;
    private bool HasReportedLocalMicrophoneOverride => AllowsLocalMicrophoneChange && DeviceSupportsLocalMicrophoneChange &&
        ReceivedAt.HasValue && LocalMicrophoneOverrideId is { } id && id != MicrophoneId;
    public bool HasCurrentLocalMicrophoneOverride => HasCurrentReceipt && HasReportedLocalMicrophoneOverride;
    public bool ReportsLocalMicrophoneOverride => HasReportedLocalMicrophoneOverride &&
        ReportedMicrophoneId == LocalMicrophoneOverrideId && Microphones.Any(m => m.Id == LocalMicrophoneOverrideId);
    public bool HasLocalMicrophoneOverrideToReplace => HasCurrentLocalMicrophoneOverride ||
        (AppliedRevision < PublishedRevision && IsInventoryFresh && ReportsLocalMicrophoneOverride);
    public bool CanGrantLocalMicrophoneChange => Source?.Policy is { Locked: true, AllowLocalMicrophoneChange: false } &&
        DeviceSupportsLocalMicrophoneChange && IsInventoryFresh;
    public bool CanRevokeLocalMicrophoneChange => AllowsLocalMicrophoneChange;
    public bool CanSelectSchoolMicrophone(string id) =>
        (id != MicrophoneId || HasLocalMicrophoneOverrideToReplace) && Microphones.Any(m => m.Id == id);
    public bool MatchesPublishedMicrophone => !HasCurrentLocalMicrophoneOverride &&
        MicrophoneId is not null && ReportedMicrophoneId == MicrophoneId &&
        Microphones.Any(m => m.Id == MicrophoneId);
    public bool MatchesLocalMicrophoneOverride => HasCurrentReceipt && ReportsLocalMicrophoneOverride &&
        Locked && LocallyUnlocked == false;
    public bool IsInventoryFresh
    {
        get
        {
            if (Source is not { } source || ReceivedAt is not { } received) return false;
            var ageAtRead = source.ServerTime - received;
            return ageAtRead >= TimeSpan.Zero &&
                ageAtRead + Stopwatch.GetElapsedTime(source.RequestStarted) <= TimeSpan.FromMinutes(1);
        }
    }
    public string EditingStatusText
    {
        get
        {
            if (ReceivedAt is null) return "暂无设备回执";
            if (AppliedRevision > PublishedRevision) return "回执版本不一致";
            if (!HasCurrentReceipt || ErrorCode == "EQUIPMENT_PENDING") return "策略同步中";
            if (!Locked) return "学校策略未锁定";
            return LocallyUnlocked switch
            {
                true => "现场已解锁",
                false => "已锁定",
                null => "设备未上报编辑状态"
            };
        }
    }

    public string StatusText
    {
        get
        {
            if (ErrorCode is not null) return ErrorCode switch
            {
                "EQUIPMENT_PENDING" => "正在监测，结束后切换",
                "EQUIPMENT_UNAVAILABLE" => "设备报告采集设备不可用，请刷新设备清单",
                _ => "设备报告应用异常"
            };
            if (AppliedRevision is null) return "尚无设备回执";
            if (AppliedRevision < PublishedRevision) return "已发布，待设备确认";
            if (AppliedRevision > PublishedRevision) return "版本不一致，请刷新";
            if (HasCurrentLocalMicrophoneOverride)
                return MatchesLocalMicrophoneOverride ? "现场已换麦克风并重新锁定"
                    : "已收到现场换麦回执，尚未确认实际输入和重新锁定";
            if (MicrophoneId is null || MatchesPublishedMicrophone) return "已应用";
            return ReportedMicrophoneId == MicrophoneId ? "已收到回执，上报清单中未列出指定麦克风"
                : "已收到回执，尚未确认使用指定麦克风";
        }
    }
}

public sealed class ConsoleApiException(int statusCode, string message, string? code = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string? Code { get; } = code;
    public IReadOnlyList<BatchClassIssue> BatchIssues { get; init; } = [];
    public string? ExistingTermId { get; init; }
}
