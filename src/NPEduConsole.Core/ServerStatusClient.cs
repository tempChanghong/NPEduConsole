using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NPEduConsole.Core;

public enum ServerDatabaseStatus { Available, Unavailable }
public enum ServerCatalogStatus { Available, NotConfigured, Unavailable }

public sealed record ServerUpdateCatalog(ServerCatalogStatus Status, int? ReleaseCount);
public sealed record ServerStatus(string Version, DateTimeOffset CheckedAt, ServerDatabaseStatus Database, ServerUpdateCatalog UpdateCatalog);

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

    public static async Task<ServerStatus> CheckAsync(string serverAddress, string monitoringToken, CancellationToken cancellationToken = default)
    {
        var origin = StatusAddress(serverAddress);
        if (monitoringToken.Length is < 1 or > 8192 || !BearerToken.IsMatch(monitoringToken))
            throw new ConsoleApiException(0, "请输入部署时配置的监控凭据。请只填 METRICS_TOKEN 的值，去掉 Bearer 前缀和空格。", "OPS_INVALID_TOKEN");

        var http = origin.Scheme == Uri.UriSchemeHttps ? Https : LoopbackHttp;
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, "api/v2/ops/status"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", monitoringToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable && await MonitoringNotConfiguredAsync(response.Content, deadline.Token))
                throw new ConsoleApiException(503, "服务器尚未配置监控凭据。请核对运行中的 KV 是否设置了 METRICS_TOKEN。", "OPS_NOT_CONFIGURED");
            if (response.StatusCode != HttpStatusCode.OK) throw StatusError((int)response.StatusCode);

            using var document = await ReadJsonAsync(response.Content, deadline.Token);
            return ParseStatus(document.RootElement);
        }
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
        return JsonDocument.Parse(await content.ReadAsByteArrayAsync(cancellationToken),
            new JsonDocumentOptions { AllowDuplicateProperties = false });
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
            !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String ||
            version.GetString() is not { Length: > 0 } versionText || string.IsNullOrWhiteSpace(versionText) || versionText.Any(char.IsControl) ||
            !root.TryGetProperty("checkedAt", out var time) || time.ValueKind != JsonValueKind.String ||
            time.GetString() is not { } timestamp || !(timestamp.EndsWith('Z') || timestamp.EndsWith("+00:00", StringComparison.Ordinal)) ||
            !time.TryGetDateTimeOffset(out var checkedAt) || checkedAt.Offset != TimeSpan.Zero ||
            !root.TryGetProperty("database", out var database) || database.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("updateCatalog", out var catalog) || catalog.ValueKind != JsonValueKind.Object ||
            !catalog.TryGetProperty("status", out var catalogStatus) || catalogStatus.ValueKind != JsonValueKind.String ||
            !catalog.TryGetProperty("releaseCount", out var count)) throw new JsonException();

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
        return new(versionText, checkedAt, databaseState, updates);
    }

    private static ConsoleApiException StatusError(int status) => status switch
    {
        401 or 403 => new(status, "本次未获得读取权限。请核对所选服务器和部署时配置的监控凭据。", "OPS_UNAUTHORIZED"),
        404 => new(status, "该地址未提供服务器状态接口。请核对 KV 版本和服务地址的路径前缀。", "OPS_UNSUPPORTED"),
        >= 300 and < 400 => new(status, "状态接口返回了重定向。请核对并直接填写 KV 的最终 HTTPS 地址。", "OPS_REDIRECT"),
        >= 500 => new(status, "KV 服务或网关暂不可用。请核对服务运行状态和网关连接。", "OPS_SERVICE_UNAVAILABLE"),
        _ => new(status, $"服务器状态检查未成功（HTTP {status}）。请核对 KV 服务地址。", "OPS_REQUEST_FAILED")
    };
}
