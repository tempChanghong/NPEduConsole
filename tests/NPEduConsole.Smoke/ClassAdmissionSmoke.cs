using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using NPEduConsole.Core;

internal static class ClassAdmissionSmoke
{
    private sealed record Fixture(string Origin, string SchoolCode, string Username, string Viewer, string Password,
        string SchoolId, string OtherSchoolId, string LostReplyOrigin);

    internal static async Task RunAsync(string config)
    {
        var fixture = JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(config), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Missing class admission fixture.");
        if (!new Uri(fixture.Origin).IsLoopback || !new Uri(fixture.LostReplyOrigin).IsLoopback)
            throw new InvalidDataException("Class admission smoke needs loopback services.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        var ct = deadline.Token;
        using var admin = new ConsoleClient(fixture.Origin);
        await admin.LoginAsync(fixture.SchoolCode, fixture.Username, fixture.Password, ct);
        var classrooms = await admin.GetClassroomsAsync(fixture.SchoolId, ct);
        var first = classrooms.Single(room => room.Name == "高二（2）班");
        var second = classrooms.Single(room => room.Name == "高二（3）班");
        var reused = first.Screens.Single(screen => screen.Name == "沿用口令大屏");
        var occupied = first.Screens.Single(screen => screen.Devices.Count == 1);
        var existing = occupied.Devices.Single();
        var target = second.Screens.Single(screen => screen.Name == "主讲大屏");
        var other = second.Screens.Single(screen => screen.Name == "后排大屏");
        Check(target.Devices.Count == 0 && other.Devices.Count == 0 && reused.Devices.Count == 0, "Empty screens remain individually identifiable.");

        Task<ClassAdmission> Read(string classId, string screenId) => admin.GetClassAdmissionAsync(fixture.SchoolId, classId, screenId, ct);
        await Expect(403, null, () => admin.GetClassAdmissionAsync(fixture.OtherSchoolId, second.Id, target.Id, ct));
        await Expect(409, "BINDING_RESPONSE_CHANGED", () => Read(first.Id, target.Id));
        using var http = new HttpClient { BaseAddress = new Uri(fixture.Origin) };
        string adminToken = await Login(fixture.Username);
        string viewerToken = await Login(fixture.Viewer);
        var raw = await Request(target.Id, adminToken, null, 200);
        Check(raw.TryGetProperty("bindingRevision", out var bindingRevision) && bindingRevision.GetInt64() > 0,
            "KV publishes the screen binding revision.");
        var pairingRevision = raw.GetProperty("pairingRevision").GetInt64();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        object Write(Guid requestId, string classId, long pairing) => new
        {
            requestId, expectedRevision = 0, enabled = true, secret, expectedClassId = classId,
            expectedBindingRevision = raw.GetProperty("bindingRevision").GetInt64(), expectedPairingRevision = pairing,
            requireUnpaired = true
        };
        await Request(target.Id, viewerToken, null, 403);
        await Request(target.Id, viewerToken, id => Write(id, second.Id, pairingRevision), 403);
        await Request(target.Id, adminToken, id => Write(id, first.Id, pairingRevision), 409, "BINDING_CHANGED");
        var staleTarget = await Read(second.Id, target.Id);
        await Request(target.Id, adminToken, id => new { requestId = id, expectedRevision = 0, enabled = false, secret = (string?)null }, 200);
        await Request(target.Id, adminToken, id => Write(id, second.Id, pairingRevision), 409, "REVISION_CONFLICT");
        await Expect(409, "REVISION_CONFLICT", () => admin.EnableClassAdmissionAsync(staleTarget, secret, Guid.NewGuid(), ct));

        var before = await Read(second.Id, target.Id);
        var sibling = await Read(second.Id, other.Id);
        Check(before.SupportsGuardedSave && !before.Configured && !before.Enabled, "Current untouched destination can be configured.");
        var ready = await admin.EnableClassAdmissionAsync(before, secret, Guid.NewGuid(), ct);
        var untouched = await Read(second.Id, other.Id);
        Check(ready.IsReady && ready.Revision == before.Revision + 1 && untouched.Configured && !untouched.Enabled &&
            untouched.Revision == ready.Revision, "Shared credential created once and preauthorization limited to the selected screen.");
        await Expect(409, "REVISION_CONFLICT", () => admin.EnableClassAdmissionAsync(sibling, secret, Guid.NewGuid(), ct));
        var published = await Request(target.Id, adminToken, null, 200);
        Check(!published.TryGetProperty("secret", out _) && !published.TryGetProperty("verifier", out _) &&
            !published.GetRawText().Contains(secret, StringComparison.Ordinal), "Readback contains no credential material.");

        var configured = await Read(first.Id, reused.Id);
        Check(configured.Configured && !configured.Enabled, "An existing credential can be reused.");
        await Expect(400, "INVALID_CLASS_ADMISSION", () => admin.EnableClassAdmissionAsync(configured, secret, Guid.NewGuid(), ct));
        var occupiedSettings = await Read(first.Id, occupied.Id);
        await Expect(409, "BINDING_OCCUPIED", () => admin.EnableClassAdmissionAsync(occupiedSettings, null, Guid.NewGuid(), ct));
        Check((await Read(first.Id, occupied.Id)).Enabled == occupiedSettings.Enabled, "An occupied screen cannot be changed by first-time setup.");
        using var relay = new ConsoleClient(fixture.LostReplyOrigin);
        await relay.LoginAsync(fixture.SchoolCode, fixture.Username, fixture.Password, ct);
        await Expect(409, "SNAPSHOT_CHANGED", () => relay.EnableClassAdmissionAsync(configured, null, Guid.NewGuid(), ct));
        var viaRelay = await relay.GetClassAdmissionAsync(fixture.SchoolId, first.Id, reused.Id, ct);
        await Expect(502, "ADMISSION_UNCONFIRMED", () => relay.EnableClassAdmissionAsync(viaRelay, null, Guid.NewGuid(), ct));
        var recovered = await relay.GetClassAdmissionAsync(fixture.SchoolId, first.Id, reused.Id, ct);
        Check(recovered.IsReady && recovered.Revision == configured.Revision, "Readback confirms current state without rotating the credential.");
        var roomsAfter = await admin.GetClassroomsAsync(fixture.SchoolId, ct);
        Check(roomsAfter.Single(room => room.Id == first.Id).Screens.SelectMany(screen => screen.Devices).Single().Id == existing.Id,
            "The existing device binding remains active.");
        var software = await admin.GetSoftwareUpdateAsync(fixture.SchoolId, existing.Id, ct);
        Check(software.CanInvite, "Class admission is independent from software maintenance consent.");
        await relay.LogoutAsync(ct);
        await admin.LogoutAsync(ct);
        Console.WriteLine("PASS: real KV class admission, multi-screen scope, permissions, binding and revision guards, existing secret retained, no credential disclosure, and lost acknowledgement readback.");

        async Task<string> Login(string username)
        {
            using var response = await http.PostAsJsonAsync("/accounts/local/login",
                new { fixture.SchoolCode, username, fixture.Password }, ct);
            response.EnsureSuccessStatusCode();
            var envelope = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            return envelope.GetProperty("data").GetProperty("access_token").GetString()!;
        }

        async Task<JsonElement> Request(string screen, string token, Func<Guid, object>? body, int expected, string? code = null)
        {
            using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post,
                $"/api/v2/npep/schools/{fixture.SchoolId}/class-management/{screen}");
            var requestId = Guid.NewGuid();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-NPEP-Version", "0.16");
            request.Headers.Add("X-Request-Id", requestId.ToString());
            if (body is not null) request.Content = JsonContent.Create(body(requestId));
            using var response = await http.SendAsync(request, ct);
            var envelope = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            Check((int)response.StatusCode == expected && (code is null || envelope.GetProperty("error").GetProperty("code").GetString() == code),
                $"Class admission HTTP response should be {expected} {code}.");
            return envelope.TryGetProperty("data", out var data) ? data : default;
        }
    }

    private static async Task Expect(int status, string? code, Func<Task> operation)
    {
        try { await operation(); }
        catch (ConsoleApiException error) when (error.StatusCode == status && (code is null || error.Code == code)) { return; }
        throw new InvalidDataException($"Expected Console status {status} {code}.");
    }

    private static void Check(bool condition, string detail)
    {
        if (!condition) throw new InvalidDataException(detail);
    }
}
