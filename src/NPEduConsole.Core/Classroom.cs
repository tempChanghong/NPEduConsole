using System.Diagnostics;

namespace NPEduConsole.Core;

public sealed record School(string Id, string Name);
public sealed record Classroom(string Id, string Name, string? GradeName, IReadOnlyList<Screen> Screens);
public sealed record Screen(string Id, string Name, IReadOnlyList<Device> Devices);
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
    public bool MatchesPublishedMicrophone => MicrophoneId is not null && ReportedMicrophoneId == MicrophoneId &&
        Microphones.Any(m => m.Id == MicrophoneId);
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
}
