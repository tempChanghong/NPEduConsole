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
        VerifyBackupEnvironment(status.Backup);
        Console.WriteLine($"PASS: GET /api/v2/ops/status; database={database}; catalog={catalog}; releaseCount={status.UpdateCatalog.ReleaseCount?.ToString(CultureInfo.InvariantCulture) ?? "null"}.");
    }

    private static void VerifyBackupEnvironment(ServerBackup? backup)
    {
        string status = backup?.Status switch
        {
            ServerBackupStatus.Available => "AVAILABLE",
            ServerBackupStatus.NoRecord => "NO_RECORD",
            ServerBackupStatus.NotConfigured => "NOT_CONFIGURED",
            ServerBackupStatus.Unavailable => "UNAVAILABLE",
            _ => "null"
        };
        string timing = backup?.Timeliness switch
        {
            ServerBackupTimeliness.Current => "CURRENT",
            ServerBackupTimeliness.Overdue => "OVERDUE",
            ServerBackupTimeliness.AwaitingFirstSuccess => "AWAITING_FIRST_SUCCESS",
            ServerBackupTimeliness.Unknown => "UNKNOWN",
            _ => "null"
        };
        string result = backup?.LastAttempt?.Result switch
        {
            ServerBackupResult.Succeeded => "SUCCEEDED",
            ServerBackupResult.Failed => "FAILED",
            _ => "null"
        };
        foreach (var (key, actual) in new[] { ("NP_OPS_BACKUP_STATUS", status), ("NP_OPS_BACKUP_TIMELINESS", timing), ("NP_OPS_BACKUP_RESULT", result) })
            Check(Environment.GetEnvironmentVariable(key) is not { } expected || expected == actual, $"backup value matches {key}");
        if (backup is not null) Console.WriteLine($"PASS: backup={status}; timeliness={timing}; lastAttempt={result}.");
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
        Check(available.Backup is null, "older responses remain readable without backup");
        await VerifyBackupAsync(body => RoundTrip(body));

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
        Console.WriteLine("PASS: local HTTP server status contract, optional backup states and invalid-record fallback, credentials, loopback, error classification, redirect isolation, and cancellation.");

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

    private static async Task VerifyBackupAsync(Func<string, Task<ServerStatus>> read)
    {
        string WithBackup(string json) => Snapshot[..^1] + ",\"backup\":" + json + "}";

        const string current = """
            {"status":"AVAILABLE","lastSuccessAt":"2026-11-20T10:00:00Z",
             "lastAttempt":{"result":"FAILED","finishedAt":"2026-11-20T11:00:00Z"},"timeliness":"CURRENT","maxAgeHours":26}
            """;
        var latestFailed = await read(WithBackup(current));
        Check(latestFailed.Backup is { Status: ServerBackupStatus.Available, Timeliness: ServerBackupTimeliness.Current, MaxAgeHours: 26,
            LastAttempt.Result: ServerBackupResult.Failed } backup && backup.LastSuccessAt == new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero) &&
            backup.LastAttempt!.FinishedAt == new DateTimeOffset(2026, 11, 20, 11, 0, 0, TimeSpan.Zero),
            "a recent failure does not erase the still-current prior success");

        const string nearBoundary = """
            {"status":"AVAILABLE","lastSuccessAt":"2026-11-19T10:30:00.124Z",
             "lastAttempt":{"result":"SUCCEEDED","finishedAt":"2026-11-19T10:30:00.124Z","futureDetail":{}},"timeliness":"CURRENT","maxAgeHours":26}
            """;
        Check((await read(WithBackup(nearBoundary))).Backup is { Timeliness: ServerBackupTimeliness.Current, LastAttempt.Result: ServerBackupResult.Succeeded },
            "a current success accepts future add-on fields");
        var atBoundary = nearBoundary.Replace(".124Z", ".123Z").Replace("CURRENT", "OVERDUE");
        Check((await read(WithBackup(atBoundary))).Backup?.Timeliness == ServerBackupTimeliness.Overdue, "the server's overdue state is shown");
        Check((await read(WithBackup(atBoundary.Replace("OVERDUE", "CURRENT")))).Backup?.Timeliness == ServerBackupTimeliness.Current,
            "a snapshot assembled just after the backup observation crosses 26 hours still preserves the observation");

        const string skewed = """
            {"status":"AVAILABLE","lastSuccessAt":"2026-11-20T12:31:00.123Z",
             "lastAttempt":{"result":"SUCCEEDED","finishedAt":"2026-11-20T12:31:00.123Z"},"timeliness":"CURRENT","maxAgeHours":48}
            """;
        var allowedSkew = await read(WithBackup(skewed));
        Check(allowedSkew.Backup is { MaxAgeHours: 48, LastAttempt.Result: ServerBackupResult.Succeeded } skewedBackup &&
            skewedBackup.LastSuccessAt - allowedSkew.CheckedAt == TimeSpan.FromSeconds(60) &&
            skewedBackup.LastAttempt!.FinishedAt == skewedBackup.LastSuccessAt,
            "a configured positive max age and timestamps up to 60 seconds ahead remain intact");

        const string awaiting = """
            {"status":"AVAILABLE","lastSuccessAt":null,"lastAttempt":null,"timeliness":"AWAITING_FIRST_SUCCESS","maxAgeHours":26}
            """;
        Check((await read(WithBackup(awaiting))).Backup is { Timeliness: ServerBackupTimeliness.AwaitingFirstSuccess,
            LastSuccessAt: null, LastAttempt: null }, "an observed initial window may not have any attempts yet");
        Check((await read(WithBackup(awaiting.Replace("AWAITING_FIRST_SUCCESS", "OVERDUE")))).Backup is
            { Timeliness: ServerBackupTimeliness.Overdue, LastSuccessAt: null }, "the first success window can expire without a success");

        const string noRecord = """{"status":"NO_RECORD","lastSuccessAt":null,"lastAttempt":null,"timeliness":"UNKNOWN","maxAgeHours":26}""";
        foreach (var (wire, expected) in new[] { ("NO_RECORD", ServerBackupStatus.NoRecord),
            ("NOT_CONFIGURED", ServerBackupStatus.NotConfigured), ("UNAVAILABLE", ServerBackupStatus.Unavailable) })
            Check((await read(WithBackup(noRecord.Replace("NO_RECORD", wire)))).Backup is
                { Timeliness: ServerBackupTimeliness.Unknown, LastSuccessAt: null, LastAttempt: null } value && value.Status == expected,
                "missing, unconfigured and unreadable states remain distinct without invented evidence");

        foreach (string malformed in new[]
        {
            "null", "[]", "{}", current.Replace("\"status\":\"AVAILABLE\"", "\"status\":\"FUTURE\""), current.Replace("CURRENT", "FUTURE"),
            current.Replace("26", "0"), current.Replace("26", "26.5"),
            current.Replace("\"maxAgeHours\":26", "\"maxAgeHours\":26,\"maxAgeHours\":26"),
            skewed.Replace("12:31:00.123Z", "12:31:00.124Z"), current.Replace("11:00:00Z", "12:31:00.124Z"),
            current.Replace("T10:00:00Z", "T10:00:00"), current.Replace("T11:00:00Z", "T13:00:00Z"),
            current.Replace("T11:00:00Z", "T09:00:00Z"), current.Replace("FAILED", "SUCCEEDED"), current.Replace("FAILED", "FUTURE"),
            current.Replace("CURRENT", "AWAITING_FIRST_SUCCESS"), awaiting.Replace("AWAITING_FIRST_SUCCESS", "CURRENT"),
            noRecord.Replace("UNKNOWN", "CURRENT"), noRecord.Replace("\"lastAttempt\":null", "\"lastAttempt\":[]")
        })
        {
            var result = await read(WithBackup(malformed));
            Check(result is { Backup: null, Database: ServerDatabaseStatus.Available,
                UpdateCatalog: { Status: ServerCatalogStatus.Available, ReleaseCount: 0 }, Version: "1.2.3" },
                "invalid or future backup data has no effect on required status fields");
        }
        Check((await read(WithBackup("null,\"backup\":" + current))).Backup is null,
            "duplicate optional backup data is ignored without discarding the base response");
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
