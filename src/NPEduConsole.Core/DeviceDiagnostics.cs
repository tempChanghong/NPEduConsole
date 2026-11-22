using System.Text.Json;
using System.Text.RegularExpressions;

namespace NPEduConsole.Core;

public sealed record DeviceDiagnosticBridge(string? Connection, string? BridgeVersion);
public sealed record DeviceDiagnosticStatus(string? AppVersion, string? Mode, string? ModePhase, long? ModeRevision,
    string? AutomaticRecording, string? Recording, DeviceDiagnosticBridge? ClassIsland, DeviceDiagnosticBridge? ExamAware);
public sealed record DeviceDiagnosticRow(string DeviceId, string SchoolId, string? AdministrativeClassId, string? ScreenBindingId,
    long? BindingRevision, bool? BindingChanged, string? State, string? Connectivity, DateTimeOffset? LastSeenAt,
    string[]? Issues, DeviceDiagnosticStatus? Status, string? DeviceName = null, string? ScreenBindingName = null);
public sealed record DeviceDiagnosticPage(DateTimeOffset AsOf, int MatchedTotal, DeviceDiagnosticRow[] Items, string? NextCursor);
public sealed record DeviceDiagnosticItem(string? DeviceId, string? SchoolId, string? AdministrativeClassId, string? ScreenBindingId,
    long? BindingRevision, bool? BindingChanged, string State, string Connectivity, DateTimeOffset? LastSeenAt,
    string[] Issues, DeviceDiagnosticStatus? Status);
public sealed record DeviceDiagnosticsExport(int SchemaVersion, string Kind, string? BackendOrigin, string? AppVersion,
    string? SchoolId, DateTimeOffset GeneratedAt, DateTimeOffset OldestObservationAt, DateTimeOffset LatestObservationAt,
    bool Stale, string Filter, bool HasSearch, int MatchedTotal, int LoadedCount, int ExportedCount, int OmittedLoadedCount,
    bool HasMore, DeviceDiagnosticItem[] Items);

public static partial class DeviceDiagnostics
{
    public const int Limit = 200;
    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static DeviceDiagnosticsExport Create(Uri server, string? appVersion, string schoolId, string filter, bool hasSearch,
        IReadOnlyList<DeviceDiagnosticPage> pages, DateTimeOffset now)
    {
        if (pages.Count == 0 || pages.Any(page => page.AsOf == default || page.MatchedTotal < 0))
            throw new ArgumentException("请先读取设备目录。", nameof(pages));
        if (pages.SelectMany(page => page.Items).Any(row => row.SchoolId != schoolId))
            throw new ArgumentException("加载的设备与当前学校不一致。", nameof(pages));
        var latest = pages[^1];
        var rows = pages.SelectMany(page => page.Items).DistinctBy(row => row.DeviceId, StringComparer.OrdinalIgnoreCase).ToArray();
        var chosen = rows.Take(Limit).Select(Project).ToArray();
        var oldest = pages.Min(page => page.AsOf).ToUniversalTime();
        var newest = pages.Max(page => page.AsOf).ToUniversalTime();
        return new(1, "npep-school-overview-diagnostics", SafeOrigin(server), Version(appVersion), BusinessId(schoolId), now.ToUniversalTime(),
            oldest, newest, newest > now || now - oldest > TimeSpan.FromMinutes(1), Choice(filter, "ALL", "ATTENTION", "ONLINE", "INACTIVE"),
            hasSearch, latest.MatchedTotal, rows.Length, chosen.Length, rows.Length - chosen.Length, latest.NextCursor is not null, chosen);
    }

    public static string Serialize(DeviceDiagnosticsExport snapshot) => JsonSerializer.Serialize(snapshot, ExportJson);

    private static DeviceDiagnosticItem Project(DeviceDiagnosticRow row) => new(Uuid(row.DeviceId), BusinessId(row.SchoolId),
        BusinessId(row.AdministrativeClassId), BusinessId(row.ScreenBindingId), Nonnegative(row.BindingRevision), row.BindingChanged,
        Choice(row.State, "ACTIVE", "REVOKED", "EXPIRED", "INVALIDATED"), Choice(row.Connectivity, "ONLINE", "OFFLINE"),
        row.LastSeenAt?.ToUniversalTime(), (row.Issues ?? []).Select(code => Choice(code, "UNREPORTED", "OFFLINE", "MODE_NOT_READY",
            "MODE_INCOMPLETE", "RECORDING_UNKNOWN", "CLASSISLAND_NOT_CONNECTED", "EXAMAWARE_NOT_CONNECTED")).Distinct().ToArray(),
        row.Status is { } status ? new(Version(status.AppVersion), Choice(status.Mode, "DAILY", "EXAM", "UNCONFIGURED"),
            Choice(status.ModePhase, "IDLE", "CHECKING", "SWITCHING", "RUNNING", "INCOMPLETE", "UNAVAILABLE"), Nonnegative(status.ModeRevision),
            Choice(status.AutomaticRecording, "ENABLED", "DISABLED"), Choice(status.Recording, "IDLE", "RECORDING", "PAUSED", "FINALIZING"),
            Bridge(status.ClassIsland), Bridge(status.ExamAware)) : null);

    private static DeviceDiagnosticBridge Bridge(DeviceDiagnosticBridge? value) =>
        new(Choice(value?.Connection, "READY", "DISCONNECTED"), Version(value?.BridgeVersion));
    private static string Choice(string? value, params string[] legal) => value is not null && legal.Contains(value, StringComparer.Ordinal) ? value : "UNKNOWN";
    private static long? Nonnegative(long? value) => value is >= 0 and <= 9007199254740991L ? value : null;
    private static string? Uuid(string? value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id.ToString("D") : null;
    private static string? BusinessId(string? value) => Uuid(value) ?? (value is { Length: 24 or 25 } && BusinessIdPattern().IsMatch(value) ? value : null);
    private static string? SafeOrigin(Uri uri) => uri.IsAbsoluteUri && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0
        ? uri.GetLeftPart(UriPartial.Authority) : null;
    private static string? Version(string? value)
    {
        if (value is null || value.Length > 160) return null;
        if (VersionPattern().IsMatch(value)) return value;
        var branded = BrandedVersionPattern().Match(value);
        return branded.Success ? branded.Groups[1].Value + branded.Groups[2].Value : null;
    }

    [GeneratedRegex(@"\A[a-z][a-z0-9]{23,24}\z", RegexOptions.CultureInvariant)]
    private static partial Regex BusinessIdPattern();
    [GeneratedRegex(@"\A(?:InDev [0-9]{8}|[0-9]+\.[0-9]+\.[0-9]+(?:[-.][a-zA-Z0-9.]+)?)(?:\+[a-fA-F0-9]{7,64})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
    [GeneratedRegex(@"\Av([0-9]+\.[0-9]+\.[0-9]+) · EVA-02（二号机）(\+[a-fA-F0-9]{7,64})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex BrandedVersionPattern();
}

public sealed partial class ConsoleClient
{
    public Task VerifyDeviceDiagnosticsAccessAsync(string schoolId, CancellationToken ct) =>
        ReadDeviceDiagnosticPageAsync(schoolId, "ALL", "", null, ct, 1);

    public async Task<DeviceDiagnosticPage> ReadDeviceDiagnosticPageAsync(string schoolId, string filter, string search,
        string? cursor, CancellationToken ct, int limit = 100)
    {
        if (filter is not ("ALL" or "ATTENTION" or "ONLINE" or "INACTIVE") || search.Length > 100 || limit is < 1 or > 100 ||
            cursor is not null && !Guid.TryParseExact(cursor, "D", out _)) throw new ArgumentException("设备诊断筛选无效。");
        var session = CurrentSession;
        string path = $"api/v2/npep/schools/{Segment(schoolId)}/device-overview?limit={limit}&filter={filter}&search={Uri.EscapeDataString(search)}";
        if (cursor is not null) path += "&cursor=" + Segment(cursor);
        var page = await RequestAsync<DeviceDiagnosticPage>(HttpMethod.Get, path, session.Token, ct, "0.1");
        CheckCreationSession(session, ct);
        if (page.AsOf == default || page.MatchedTotal < 0 || page.Items is null || page.Items.Length > limit ||
            page.Items.Any(row => row is null || row.SchoolId != schoolId || !Guid.TryParseExact(row.DeviceId, "D", out _) ||
                cursor is not null && string.CompareOrdinal(row.DeviceId, cursor) <= 0) ||
            page.Items.Select(row => row.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != page.Items.Length ||
            page.NextCursor is { } next && (page.Items.Length != limit || next != page.Items[^1].DeviceId)) throw InvalidResponse();
        return page;
    }
}
