using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NPEduConsole.Core;

internal static class ServerStatusSmoke
{
    private const string FixtureToken = "isolated-fixture-token._~+/-=";
    private const string Snapshot = """
        {"version":"1.2.3","checkedAt":"2026-11-20T12:30:00.123Z","database":"AVAILABLE",
         "updateCatalog":{"status":"AVAILABLE","releaseCount":0}}
        """;

    internal static async Task ProbeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var status = await ServerStatusClient.CheckAsync(RequiredEnvironment("NP_OPS_ORIGIN"),
            RequiredEnvironment("NP_OPS_TOKEN"), deadline.Token);
        string database = status.Database == ServerDatabaseStatus.Available ? "AVAILABLE" : "UNAVAILABLE";
        string catalog = status.UpdateCatalog.Status switch
        {
            ServerCatalogStatus.Available => "AVAILABLE",
            ServerCatalogStatus.NotConfigured => "NOT_CONFIGURED",
            _ => "UNAVAILABLE"
        };
        var expectedDatabase = Environment.GetEnvironmentVariable("NP_OPS_DATABASE");
        var expectedCatalog = Environment.GetEnvironmentVariable("NP_OPS_CATALOG");
        var expectedCount = Environment.GetEnvironmentVariable("NP_OPS_RELEASE_COUNT");
        int? releaseCount = null;
        if (expectedCount is not null)
        {
            if (!int.TryParse(expectedCount, NumberStyles.None, CultureInfo.InvariantCulture, out int count))
                throw new InvalidDataException("NP_OPS_RELEASE_COUNT must be a nonnegative integer or unset.");
            releaseCount = count;
        }
        Check(expectedDatabase is null || database == expectedDatabase, "database state matches NP_OPS_DATABASE");
        Check(expectedCatalog is null || catalog == expectedCatalog, "update catalog state matches NP_OPS_CATALOG");
        Check(status.UpdateCatalog.ReleaseCount == releaseCount, "release count matches NP_OPS_RELEASE_COUNT (unset means null)");
        Console.WriteLine($"PASS: GET /api/v2/ops/status; database={database}; catalog={catalog}; releaseCount={status.UpdateCatalog.ReleaseCount?.ToString(CultureInfo.InvariantCulture) ?? "null"}.");
    }

    private static string RequiredEnvironment(string key) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } value
        ? value : throw new InvalidDataException($"Set {key} in the test process environment.");

    internal static async Task VerifyAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        var available = await RoundTrip(Snapshot, host: "localhost", prefix: "/kv/proxy");
        Check(available is { Version: "1.2.3", Database: ServerDatabaseStatus.Available, UpdateCatalog: { Status: ServerCatalogStatus.Available, ReleaseCount: 0 } } &&
            available.CheckedAt == new DateTimeOffset(2026, 11, 20, 12, 30, 0, 123, TimeSpan.Zero), "successful results keep UTC and an empty catalog distinct");
        var partial = await RoundTrip("""
            {"version":"1.2.3","checkedAt":"2026-11-20T12:30:00+00:00","database":"UNAVAILABLE",
             "queue":{"status":"AVAILABLE"},"updateCatalog":{"status":"NOT_CONFIGURED","releaseCount":null,"extra":true}}
            """);
        Check(partial is { Database: ServerDatabaseStatus.Unavailable, UpdateCatalog: { Status: ServerCatalogStatus.NotConfigured, ReleaseCount: null } },
            "database outage is still a snapshot, and additive fields preserve compatibility");
        var unreadable = await RoundTrip(Snapshot.Replace("\"status\":\"AVAILABLE\",\"releaseCount\":0", "\"status\":\"UNAVAILABLE\",\"releaseCount\":null"));
        Check(unreadable.UpdateCatalog is { Status: ServerCatalogStatus.Unavailable, ReleaseCount: null }, "unreadable is distinct from not configured");

        await Expect(401, "OPS_UNAUTHORIZED", () => RoundTrip("credential-looking text that must not appear", "401 Unauthorized", "text/plain"));
        await Expect(404, "OPS_UNSUPPORTED", () => RoundTrip("<html>old server</html>", "404 Not Found", "text/html"));
        await Expect(503, "OPS_NOT_CONFIGURED", () => RoundTrip("""{"code":"MONITORING_NOT_CONFIGURED"}""", "503 Service Unavailable"));
        await Expect(503, "OPS_SERVICE_UNAVAILABLE", () => RoundTrip("<html>gateway</html>", "503 Service Unavailable", "text/html"));
        await Expect(503, "OPS_SERVICE_UNAVAILABLE", () => RoundTrip("""{"code":"UPSTREAM_UNREACHABLE"}""", "503 Service Unavailable"));
        await Expect(201, "OPS_REQUEST_FAILED", () => RoundTrip(Snapshot, "201 Created"));
        await Expect(0, "OPS_INVALID_RESPONSE", () => RoundTrip(Snapshot, contentType: "text/html"));
        foreach (var invalid in new[]
        {
            "{", Snapshot.Replace("\"version\":\"1.2.3\",", ""), Snapshot.Replace("\"1.2.3\"", "123"),
            Snapshot.Replace(".123Z", ""), Snapshot.Replace(".123Z", "+08:00"), Snapshot.Replace("12:30:00", "99:00:00"),
            Snapshot.Replace("\"database\":\"AVAILABLE\"", "\"database\":\"UNKNOWN\""),
            Snapshot.Replace("\"database\":\"AVAILABLE\"", "\"database\":\"UNAVAILABLE\",\"database\":\"AVAILABLE\""),
            Snapshot.Replace("\"releaseCount\":0", "\"releaseCount\":-1"), Snapshot.Replace("\"releaseCount\":0", "\"releaseCount\":1.23"),
            Snapshot.Replace("\"releaseCount\":0", "\"releaseCount\":null"), Snapshot.Replace("\"releaseCount\":0", "\"releaseCount\":\"0\""),
            Snapshot.Replace("\"status\":\"AVAILABLE\"", "\"status\":\"UNAVAILABLE\""),
            Snapshot.Replace("\"status\":\"AVAILABLE\"", "\"status\":\"NOT_CONFIGURED\""),
            Snapshot.Replace("\"status\":\"AVAILABLE\"", "\"status\":\"UNKNOWN\""), new string(' ', 17 * 1024)
        }) await Expect(0, "OPS_INVALID_RESPONSE", () => RoundTrip(invalid));

        using var trap = Listen();
        foreach (string address in new[] { "http://example.test", "file:///tmp/status", "https://user:secret@example.test",
            "https://example.test?token=invalid", "https://example.test/#fragment", "https://example.test\\invalid" })
            await Expect(0, "OPS_INVALID_ADDRESS", () => ServerStatusClient.CheckAsync(address, FixtureToken, ct));
        foreach (string token in new[] { "", "Bearer preset", "ends-in-space ", "start\r\nHeader: injected", "known\u0000value", "=invalid", "a=b" })
            await Expect(0, "OPS_INVALID_TOKEN", () => ServerStatusClient.CheckAsync(Origin(trap), token, ct));
        Check(!trap.Pending(), "invalid credentials are blocked before requesting loopback");
        await Expect(302, "OPS_REDIRECT", () => RoundTrip("", "302 Found", additionalHeader: $"Location: {Origin(trap)}/accounts/local/login\r\n"));
        Check(!trap.Pending(), "redirect never reaches even a loopback destination or school login route");

        using var slow = Listen();
        using var userCancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var cancelled = ServerStatusClient.CheckAsync(Origin(slow), FixtureToken, userCancel.Token);
        using var connection = await slow.AcceptTcpClientAsync(ct);
        using var stream = connection.GetStream();
        await ReadRequestAsync(stream, "/api/v2/ops/status", ct);
        userCancel.Cancel();
        try
        {
            await cancelled;
            throw new InvalidDataException("Expected user cancellation.");
        }
        catch (OperationCanceledException) when (userCancel.IsCancellationRequested) { }
        Console.WriteLine("PASS: local HTTP server status contract, additive fields, credentials, loopback, error classification, redirect isolation, and cancellation.");

        async Task<ServerStatus> RoundTrip(string body, string status = "200 OK", string contentType = "application/json",
            string host = "127.0.0.1", string prefix = "", string additionalHeader = "")
        {
            using var listener = Listen();
            var checking = ServerStatusClient.CheckAsync(Origin(listener, host) + prefix, FixtureToken, ct);
            using var accepted = await listener.AcceptTcpClientAsync(ct);
            using var stream = accepted.GetStream();
            await ReadRequestAsync(stream, prefix + "/api/v2/ops/status", ct);
            byte[] data = Encoding.UTF8.GetBytes(body);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {data.Length}\r\nConnection: close\r\n{additionalHeader}\r\n"), ct);
            if (data.Length > 0) await stream.WriteAsync(data, ct);
            return await checking;
        }
    }

    private static TcpListener Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    private static string Origin(TcpListener listener, string host = "127.0.0.1") => $"http://{host}:{((IPEndPoint)listener.LocalEndpoint).Port}";

    private static async Task ReadRequestAsync(Stream stream, string path, CancellationToken token)
    {
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        Check(await reader.ReadLineAsync(token) == $"GET {path} HTTP/1.1", "request only uses the protected GET route with no query");
        string? authorization = null;
        while (await reader.ReadLineAsync(token) is { Length: > 0 } header)
            if (header.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) authorization = header[14..].Trim();
        Check(authorization == "Bearer " + FixtureToken, "synthetic monitor credential was sent only as Bearer authorization");
    }

    private static async Task Expect(int status, string code, Func<Task> action)
    {
        try { await action(); }
        catch (ConsoleApiException error) when (error.StatusCode == status && error.Code == code)
        {
            Check(!error.Message.Contains(FixtureToken, StringComparison.Ordinal) && !error.Message.Contains("credential-looking text", StringComparison.Ordinal),
                "errors never echo a token or untrusted response body");
            return;
        }
        throw new InvalidDataException($"Expected status {status} and error {code}.");
    }

    private static void Check(bool success, string step)
    {
        if (!success) throw new InvalidDataException(step);
    }
}
