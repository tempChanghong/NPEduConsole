using System.Globalization;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed class OverviewScreenItem
{
    public SchoolOverviewScreen Screen { get; }
    public string ClassId => Screen.ClassId;
    public string ScreenId => Screen.ScreenId;
    public string DisplayName { get; }
    public bool ShowBinding { get; }
    public string BindingCaption => $"绑定 {ScreenId}";
    public string Title { get; }
    public string AutomationId => "SchoolDevice-" + ScreenId;
    public DeviceEvidence Native { get; }
    public DeviceEvidence Browser { get; }

    public OverviewScreenItem(SchoolOverviewScreen screen, bool duplicateName)
    {
        Screen = screen;
        Native = new(screen.Native);
        Browser = new(screen.Browser);
        // KV appends this exact binding ID when screens in one class share a name.
        string suffix = $"（编号 {screen.ScreenId}）";
        bool includesId = screen.ScreenName.EndsWith(suffix, StringComparison.Ordinal);
        DisplayName = includesId ? screen.ScreenName[..^suffix.Length] : screen.ScreenName;
        ShowBinding = duplicateName || includesId;
        Title = $"{screen.ClassName} · {DisplayName}" + (ShowBinding ? $"（{BindingCaption}）" : "");
    }
}

public sealed class DeviceEvidence(SchoolScreenObservation observation)
{
    public static DeviceEvidence Unknown { get; } = new(new SchoolScreenObservation("UNKNOWN", null));
    public string State => observation.State;
    public string Label => LabelFor(State);
    public bool Confirmed => State is "REPORTED" or "ONLINE";
    public bool Pending => State is "AWAITING_LOGIN" or "AWAITING_HEARTBEAT" or "AWAITING_PAIRING" or "AWAITING_REPORT";
    public bool Attention => State is "REPAIR_REQUIRED" or "DEGRADED" or "OFFLINE";
    public bool HasRecord => observation.LastReportedAt.HasValue;
    public string? Record => observation.LastReportedAt is { } time
        ? (State == "REPAIR_REQUIRED" ? "原设备 " : "") + time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        : null;

    public static string LabelFor(string state) => state switch
    {
        "NOT_APPLICABLE" => "暂无接入记录",
        "AWAITING_LOGIN" => "待首次登录",
        "AWAITING_HEARTBEAT" => "待值守心跳",
        "ONLINE" => "近期有值守心跳",
        "DEGRADED" => "心跳报告异常",
        "OFFLINE" => "近期未收到心跳",
        "AWAITING_PAIRING" => "待现场接入",
        "AWAITING_REPORT" => "待首次上报",
        "REPORTED" => "已收到有效上报",
        "REPAIR_REQUIRED" => "需重新配对",
        _ => "待核对"
    };
}
