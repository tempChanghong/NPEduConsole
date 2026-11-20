namespace NPEduConsole.Core;

public sealed record School(string Id, string Name);
public sealed record Classroom(string Id, string Name, string? GradeName, IReadOnlyList<Screen> Screens);
public sealed record Screen(string Id, string Name, IReadOnlyList<Device> Devices);
public sealed record Device(string Id, string Name, string Connectivity, DateTimeOffset? LastSeenAt);

public sealed record Equipment(long PublishedRevision, long? AppliedRevision, bool Locked,
    bool? LocallyUnlocked, string? MicrophoneName, DateTimeOffset? ReceivedAt, string? ErrorCode)
{
    public string StatusText => ErrorCode is not null ? "设备报告应用异常"
        : AppliedRevision is null ? "尚无设备回执"
        : AppliedRevision == PublishedRevision ? "已应用"
        : AppliedRevision < PublishedRevision ? "已发布，待设备确认"
        : "版本不一致，请刷新";
}

public sealed class ConsoleApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
