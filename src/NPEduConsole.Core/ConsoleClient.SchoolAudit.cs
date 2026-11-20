using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NPEduConsole.Core;

public sealed record SchoolAuditQuery(string? ActorType = null, string? Action = null, bool? Success = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null, string? Cursor = null, int Limit = 50, DateTimeOffset? Before = null);

public sealed record SchoolAuditAccount(string? Name = null, string? LocalUsername = null, string? Email = null);
public sealed record SchoolAuditScreen(string? Name = null);

public sealed record SchoolAuditEntry(string Id, string ActorType, string Action, bool Success, DateTimeOffset CreatedAt,
    string? SchoolId = null, string? Summary = null, string? EntityType = null, string? EntityId = null,
    string? RequestMethod = null, string? RequestPath = null, int? StatusCode = null, string? ClientIp = null,
    SchoolAuditAccount? ActorAccount = null, SchoolAuditScreen? ActorScreen = null, JsonElement? Metadata = null);

public sealed record SchoolAuditPage(IReadOnlyList<SchoolAuditEntry> Items, string? NextCursor = null);

public sealed partial class ConsoleClient
{
    // Older KV versions may return previously stored metadata without reapplying the server's redaction rules.
    private static readonly Regex AuditSensitiveKey = new("password|passphrase|pin|token|secret|authorization|cookie|hash|setupKey",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public async Task<SchoolAuditPage> GetSchoolAuditLogsAsync(string schoolId, SchoolAuditQuery query, CancellationToken ct)
    {
        if (query.Limit is < 1 or > 100 || query.From > query.To || query.From >= query.Before)
            throw new ArgumentException("审计记录的日期范围或每页条数无效。", nameof(query));

        var session = CurrentSession;
        var parameters = new[]
        {
            ("limit", query.Limit.ToString(CultureInfo.InvariantCulture)), ("actorType", query.ActorType), ("action", query.Action),
            ("success", query.Success is { } success ? success ? "true" : "false" : null),
            ("from", FormatAuditBoundary(query.From)), ("to", FormatAuditBoundary(query.To)),
            ("before", FormatAuditBoundary(query.Before)), ("cursor", query.Cursor)
        };
        string filters = string.Join('&', parameters.Where(p => !string.IsNullOrEmpty(p.Item2))
            .Select(p => p.Item1 + "=" + Uri.EscapeDataString(p.Item2!)));
        string path = $"api/v2/admin/schools/{Segment(schoolId)}/audit-logs?{filters}";
        var page = await RequestAsync<SchoolAuditPage>(HttpMethod.Get, path, session.Token, ct);
        ct.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_session, session)) throw new ConsoleApiException(401, "登录已失效，请重新登录管理账号。");

        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        if (page.Items.Count > query.Limit || page.Items.Any(row => row is null || string.IsNullOrWhiteSpace(row.Id) ||
                !identifiers.Add(row.Id) || string.IsNullOrWhiteSpace(row.ActorType) || string.IsNullOrWhiteSpace(row.Action) ||
                row.SchoolId != schoolId) ||
            (page.NextCursor is { } cursor && (page.Items.Count == 0 || string.IsNullOrWhiteSpace(cursor) || cursor == query.Cursor)))
            throw InvalidResponse();

        return page with { Items = page.Items.Select(RedactAuditEntry).ToArray() };
    }

    private static string? FormatAuditBoundary(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static SchoolAuditEntry RedactAuditEntry(SchoolAuditEntry row)
    {
        string? path = row.RequestPath?.Split('?', '#')[0];
        if (path is not null && !path.StartsWith('/'))
            path = Uri.TryCreate(path, UriKind.Absolute, out var address) && address.Scheme is "http" or "https" ? address.AbsolutePath : null;
        if (path?.StartsWith("//", StringComparison.Ordinal) == true) path = null;
        return row with { RequestPath = path, Metadata = RedactAuditMetadata(row.Metadata) };
    }

    private static JsonElement? RedactAuditMetadata(JsonElement? metadata)
    {
        if (metadata is not { ValueKind: not JsonValueKind.Null } value) return null;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteAuditMetadata(writer, value);
        using var sanitized = JsonDocument.Parse(stream.ToArray());
        return sanitized.RootElement.Clone();
    }

    private static void WriteAuditMetadata(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (AuditSensitiveKey.IsMatch(property.Name)) writer.WriteStringValue("[REDACTED]");
                    else WriteAuditMetadata(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteAuditMetadata(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
