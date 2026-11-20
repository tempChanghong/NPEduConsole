using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NPEduConsole.Core;

public enum ServerDatabaseStatus { Available, Unavailable }
public enum ServerCatalogStatus { Available, NotConfigured, Unavailable }
public enum ServerBackupStatus { Available, NoRecord, NotConfigured, Unavailable }
public enum ServerBackupTimeliness { Current, Overdue, AwaitingFirstSuccess, Unknown }
public enum ServerBackupResult { Succeeded, Failed }

public sealed record ServerUpdateCatalog(ServerCatalogStatus Status, int? ReleaseCount);
public sealed record ServerBackupAttempt(ServerBackupResult Result, DateTimeOffset FinishedAt);
public sealed record ServerBackup(ServerBackupStatus Status, DateTimeOffset? LastSuccessAt, ServerBackupAttempt? LastAttempt,
    ServerBackupTimeliness Timeliness, int MaxAgeHours);

public sealed record ServerStatus(string Version, DateTimeOffset CheckedAt, ServerDatabaseStatus Database, ServerUpdateCatalog UpdateCatalog)
{
    public ServerBackup? Backup { get; init; }
}

public static class ServerStatusClient
{
    private static readonly HttpClient Https = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    });

    private static readonly HttpClient LoopbackHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false, ConnectCallback = ConnectLoopbackAsync
    });

    private static readonly Regex BearerToken = new(@"\A[A-Za-z0-9._~+/\-]+=*\z", RegexOptions.NonBacktracking);
    private const int MaxResponseBytes = 16 * 1024;
    private static readonly TimeSpan MaxBackupClockSkew = TimeSpan.FromSeconds(60);

    public static async Task<ServerStatus> CheckAsync(string serverAddress, string monitoringToken, CancellationToken cancellationToken = default)
    {
        var origin = StatusAddress(serverAddress);
        if (monitoringToken.Length is < 1 or > 8192 || !BearerToken.IsMatch(monitoringToken))
            throw new ConsoleApiException(0, "请输入部署时配置的监控凭据。请只填 METRICS_TOKEN 的值，去掉 Bearer 前缀和空格。", "OPS_INVALID_TOKEN");

        var http = origin.Scheme == Uri.UriSchemeHttps ? Https : LoopbackHttp;
        return await ReadAsync(http, new Uri(origin, "api/v2/ops/status"), monitoringToken, schoolSession: false, cancellationToken);
    }

    internal static Task<ServerStatus> CheckSchoolSessionAsync(HttpClient http, string token, CancellationToken cancellationToken) =>
        ReadAsync(http, new Uri("api/v2/me/server-status", UriKind.Relative), token, schoolSession: true, cancellationToken);

    private static async Task<ServerStatus> ReadAsync(HttpClient http, Uri endpoint, string token, bool schoolSession, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!schoolSession && response.StatusCode == HttpStatusCode.ServiceUnavailable &&
                await MonitoringNotConfiguredAsync(response.Content, deadline.Token))
                throw new ConsoleApiException(503, "服务器尚未配置监控凭据。请核对运行中的 KV 是否设置了 METRICS_TOKEN。", "OPS_NOT_CONFIGURED");
            if (response.StatusCode != HttpStatusCode.OK)
                throw schoolSession ? SchoolStatusError((int)response.StatusCode) : StatusError((int)response.StatusCode);

            using var document = await ReadJsonAsync(response.Content, deadline.Token);
            var root = document.RootElement;
            if (!schoolSession) return ParseStatus(root);
            if (root.ValueKind != JsonValueKind.Object || !TryGetUniqueProperty(root, "success", out var success) ||
                success.ValueKind != JsonValueKind.True || !TryGetUniqueProperty(root, "data", out var data)) throw new JsonException();
            return ParseStatus(data);
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ConsoleApiException(0, "检查超时。请核对 KV 地址、网络和服务器负载后再检查。", "OPS_TIMEOUT");
        }
        catch (HttpRequestException)
        {
            throw new ConsoleApiException(0, "无法完成服务器检查。请核对地址、网络和 HTTPS 证书。", "OPS_CONNECTION");
        }
        catch (JsonException)
        {
            throw new ConsoleApiException(0, "服务器返回了无法识别的诊断数据。请核对 KV 版本和服务地址。", "OPS_INVALID_RESPONSE");
        }
    }

    private static Uri StatusAddress(string serverAddress)
    {
        try
        {
            if (!serverAddress.Contains('\\') && !serverAddress.Trim().Any(char.IsControl))
                return ConsoleClient.NormalizeKvAddress(serverAddress);
        }
        catch (ConsoleApiException) { }
        throw new ConsoleApiException(0, "请输入 KV 的 HTTPS 服务地址。HTTP 仅用于 localhost 或回环 IP。地址中不要包含账号、查询参数或片段。", "OPS_INVALID_ADDRESS");
    }

    private static async ValueTask<Stream> ConnectLoopbackAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        string host = context.DnsEndPoint.Host;
        // The localhost spelling must stay local even if host resolution changes.
        IPAddress[] addresses = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? [IPAddress.IPv6Loopback, IPAddress.Loopback]
            : IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip) ? [ip] : [];
        if (addresses.Length == 0) throw new HttpRequestException("Expected a loopback endpoint.");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (!string.Equals(content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
            content.Headers.ContentLength > MaxResponseBytes) throw new JsonException();
        await content.LoadIntoBufferAsync(MaxResponseBytes, cancellationToken);
        return JsonDocument.Parse(await content.ReadAsByteArrayAsync(cancellationToken));
    }

    private static async Task<bool> MonitoringNotConfiguredAsync(HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await ReadJsonAsync(content, cancellationToken);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.GetPropertyCount() == 1 &&
                root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String &&
                code.ValueEquals("MONITORING_NOT_CONFIGURED");
        }
        catch (Exception error) when (error is JsonException or HttpRequestException) { return false; }
    }

    private static ServerStatus ParseStatus(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetUniqueProperty(root, "version", out var version) || version.ValueKind != JsonValueKind.String ||
            version.GetString() is not { Length: > 0 } versionText || string.IsNullOrWhiteSpace(versionText) || versionText.Any(char.IsControl) ||
            !TryGetUniqueProperty(root, "checkedAt", out var time) || !TryReadUtc(time, out var checkedAt) ||
            !TryGetUniqueProperty(root, "database", out var database) || database.ValueKind != JsonValueKind.String ||
            !TryGetUniqueProperty(root, "updateCatalog", out var catalog) || catalog.ValueKind != JsonValueKind.Object ||
            !TryGetUniqueProperty(catalog, "status", out var catalogStatus) || catalogStatus.ValueKind != JsonValueKind.String ||
            !TryGetUniqueProperty(catalog, "releaseCount", out var count)) throw new JsonException();

        var databaseState = database.GetString() switch
        {
            "AVAILABLE" => ServerDatabaseStatus.Available,
            "UNAVAILABLE" => ServerDatabaseStatus.Unavailable,
            _ => throw new JsonException()
        };
        var updates = catalogStatus.GetString() switch
        {
            "AVAILABLE" when count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var releases) && releases >= 0 =>
                new ServerUpdateCatalog(ServerCatalogStatus.Available, releases),
            "NOT_CONFIGURED" when count.ValueKind == JsonValueKind.Null => new ServerUpdateCatalog(ServerCatalogStatus.NotConfigured, null),
            "UNAVAILABLE" when count.ValueKind == JsonValueKind.Null => new ServerUpdateCatalog(ServerCatalogStatus.Unavailable, null),
            _ => throw new JsonException()
        };
        return new(versionText, checkedAt, databaseState, updates)
        {
            Backup = TryGetUniqueProperty(root, "backup", out var backup) ? ParseBackup(backup, checkedAt) : null
        };
    }

    private static ServerBackup? ParseBackup(JsonElement data, DateTimeOffset checkedAt)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !TryGetUniqueProperty(data, "status", out var statusValue) || statusValue.ValueKind != JsonValueKind.String ||
            !TryGetUniqueProperty(data, "timeliness", out var timingValue) || timingValue.ValueKind != JsonValueKind.String ||
            !TryGetUniqueProperty(data, "maxAgeHours", out var maxAge) || maxAge.ValueKind != JsonValueKind.Number ||
            !maxAge.TryGetInt32(out int hours) || hours <= 0 ||
            !TryGetUniqueProperty(data, "lastSuccessAt", out var successValue) ||
            !TryGetUniqueProperty(data, "lastAttempt", out var attemptValue)) return null;

        ServerBackupStatus? status = statusValue.GetString() switch
        {
            "AVAILABLE" => ServerBackupStatus.Available,
            "NO_RECORD" => ServerBackupStatus.NoRecord,
            "NOT_CONFIGURED" => ServerBackupStatus.NotConfigured,
            "UNAVAILABLE" => ServerBackupStatus.Unavailable,
            _ => null
        };
        ServerBackupTimeliness? timing = timingValue.GetString() switch
        {
            "CURRENT" => ServerBackupTimeliness.Current,
            "OVERDUE" => ServerBackupTimeliness.Overdue,
            "AWAITING_FIRST_SUCCESS" => ServerBackupTimeliness.AwaitingFirstSuccess,
            "UNKNOWN" => ServerBackupTimeliness.Unknown,
            _ => null
        };
        if (status is null || timing is null) return null;
        if (status != ServerBackupStatus.Available)
            return successValue.ValueKind == JsonValueKind.Null && attemptValue.ValueKind == JsonValueKind.Null && timing == ServerBackupTimeliness.Unknown
                ? new(status.Value, null, null, timing.Value, hours) : null;

        DateTimeOffset? lastSuccess = null;
        if (successValue.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadUtc(successValue, out var successAt) || successAt - checkedAt > MaxBackupClockSkew) return null;
            lastSuccess = successAt;
        }
        ServerBackupAttempt? lastAttempt = null;
        if (attemptValue.ValueKind != JsonValueKind.Null)
        {
            lastAttempt = ParseBackupAttempt(attemptValue);
            if (lastAttempt is null || lastAttempt.FinishedAt - checkedAt > MaxBackupClockSkew || lastSuccess > lastAttempt.FinishedAt ||
                lastAttempt.Result == ServerBackupResult.Succeeded && lastSuccess != lastAttempt.FinishedAt) return null;
        }
        // The server observes the backup before it finishes the entire diagnostic snapshot.
        if (timing != ServerBackupTimeliness.Overdue && timing != (lastSuccess.HasValue
                ? ServerBackupTimeliness.Current : ServerBackupTimeliness.AwaitingFirstSuccess)) return null;

        return new(status.Value, lastSuccess, lastAttempt, timing.Value, hours);
    }

    private static ServerBackupAttempt? ParseBackupAttempt(JsonElement attempt)
    {
        if (attempt.ValueKind != JsonValueKind.Object ||
            !TryGetUniqueProperty(attempt, "result", out var result) || result.ValueKind != JsonValueKind.String ||
            !TryGetUniqueProperty(attempt, "finishedAt", out var time) || !TryReadUtc(time, out var finishedAt)) return null;
        return result.GetString() switch
        {
            "SUCCEEDED" => new(ServerBackupResult.Succeeded, finishedAt),
            "FAILED" => new(ServerBackupResult.Failed, finishedAt),
            _ => null
        };
    }

    private static bool TryReadUtc(JsonElement time, out DateTimeOffset utc)
    {
        utc = default;
        return time.ValueKind == JsonValueKind.String && time.GetString() is { } value &&
            (value.EndsWith('Z') || value.EndsWith("+00:00", StringComparison.Ordinal)) &&
            time.TryGetDateTimeOffset(out utc) && utc.Offset == TimeSpan.Zero;
    }

    // Validate duplicates within each projection so a bad optional backup does not break the base snapshot.
    private static bool TryGetUniqueProperty(JsonElement data, string name, out JsonElement value)
    {
        value = default;
        foreach (var property in data.EnumerateObject())
        {
            if (!property.NameEquals(name)) continue;
            if (value.ValueKind != JsonValueKind.Undefined) return false;
            value = property.Value;
        }
        return value.ValueKind != JsonValueKind.Undefined;
    }

    private static ConsoleApiException StatusError(int status) => status switch
    {
        401 or 403 => new(status, "本次未获得读取权限。请核对所选服务器和部署时配置的监控凭据。", "OPS_UNAUTHORIZED"),
        404 => new(status, "该地址未提供服务器状态接口。请核对 KV 版本和服务地址的路径前缀。", "OPS_UNSUPPORTED"),
        >= 300 and < 400 => new(status, "状态接口返回了重定向。请核对并直接填写 KV 的最终 HTTPS 地址。", "OPS_REDIRECT"),
        >= 500 => new(status, "KV 服务或网关暂不可用。请核对服务运行状态和网关连接。", "OPS_SERVICE_UNAVAILABLE"),
        _ => new(status, $"服务器状态检查未成功（HTTP {status}）。请核对 KV 服务地址。", "OPS_REQUEST_FAILED")
    };

    private static ConsoleApiException SchoolStatusError(int status) => status switch
    {
        401 => new(status, "KV 暂时无法核实登录。控制台会自动重试，若持续出现可重新登录。", "SCHOOL_STATUS_SESSION_UNVERIFIED"),
        403 => new(status, "当前账号无权查看服务器状态，请核对学校管理权限。", "SCHOOL_STATUS_FORBIDDEN"),
        404 => new(status, "此 KV 版本未提供登录后检查。升级 KV 后可使用自动服务器状态。", "SCHOOL_STATUS_UNSUPPORTED"),
        _ => StatusError(status)
    };
}
