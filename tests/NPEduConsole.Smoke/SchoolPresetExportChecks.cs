using System.Text.Json;
using NPEduConsole.Core;

internal static partial class ServerStatusSmoke
{
    internal static async Task VerifySchoolPresetAsync(string output)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        var path = Path.GetFullPath(output);
        string first = """{"role":"OWNER","school":{"id":"school-a","name":"联验学校","code":"SCHOOL-A","terms":[]}}""";
        string second = """{"role":"ADMIN","school":{"id":"school-b","name":"联验学校","code":"SCHOOL-B","terms":[]}}""";
        using var server = Listen();
        string address = Origin(server);
        async Task Reply(string route, string data, string method = "GET", string? expectedBearer = FixtureToken)
        {
            using var connection = await server.AcceptTcpClientAsync(token);
            using var stream = connection.GetStream();
            await ReadRequestAsync(stream, route, token, expectedBearer, method);
            await WriteResponseAsync(stream, "{\"success\":true,\"data\":" + data + "}", "200 OK", token);
        }
        async Task Serve()
        {
            await Reply("/accounts/local/login", "{\"access_token\":\"" + FixtureToken + "\"}", "POST", null);
            await Reply("/api/v2/me/schools", $"[{first},{second}]");
            await Reply("/api/v2/me/schools", $"[{first},{second}]");
            await Reply("/api/v2/me/schools", $"[{first}]");
            await Reply("/api/v2/me/schools", "[" + first.Replace(",\"code\":\"SCHOOL-A\"", "") + "]");
        }
        var serving = Serve();

        using var client = new ConsoleClient(address);
        var schools = await client.LoginAsync("fixture", "fixture", "fixture", token);
        Check(schools.Count == 2, "both same-name schools are listed");
        var preset = await client.ReadSchoolAccessPresetAsync("school-b", token);
        Check(preset.SchoolCode == "SCHOOL-B" && preset.BackendUrl == address, "selected school ID and current KV origin");
        await preset.ExportAsync(path, token);
        var original = await File.ReadAllBytesAsync(path, token);
        using var json = JsonDocument.Parse(original);
        Check(json.RootElement.EnumerateObject().Select(x => x.Name).SequenceEqual(
            ["schemaVersion", "backendUrl", "schoolName", "schoolCode"]), "the file contains only prefill fields and schema version");
        try { await preset.ExportAsync(path, token); throw new InvalidOperationException("existing file was accepted"); }
        catch (IOException) { }
        var preserved = await File.ReadAllBytesAsync(path, token);
        Check(original.SequenceEqual(preserved), "existing portable file is preserved");
        try
        {
            await client.ReadSchoolAccessPresetAsync("school-b", token);
            throw new InvalidOperationException("revoked scope was accepted");
        }
        catch (ConsoleApiException error) when (error.StatusCode == 403) { }

        var legacy = await client.ReadSchoolAccessPresetAsync("school-a", token);
        Check(legacy.SchoolName == "联验学校" && legacy.SchoolCode is null, "optional school code on older membership reply");
        await serving;
        Console.WriteLine("PASS selected school identity, fresh authorization, no active term required, only preset fields, non-overwrite, optional school code.");
    }
}
