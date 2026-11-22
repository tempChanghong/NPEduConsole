using System.Security.Cryptography;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NPEduConsole.Core;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

if (args is ["--native-school-transfer", var access])
{
    await NativeClassroom.VerifyTransferredPairingAsync(access);
    return;
}

var file = Path.GetFullPath(args.Single());
var fixture = JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(file), JsonSerializerOptions.Web)!;
if (!new Uri(fixture.Origin).IsLoopback || !new Uri(fixture.LostReplyOrigin).IsLoopback || !fixture.AllowFirstScreenTestWrites)
    throw new InvalidDataException("Expected a disposable local first-screen fixture.");
var directory = Path.Combine(Path.GetDirectoryName(file)!, "native-device-" + Guid.NewGuid().ToString("N"));
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(80));
var ct = timeout.Token;
using var relay = new ConsoleClient(fixture.LostReplyOrigin);
await relay.LoginAsync(fixture.SchoolCode, fixture.Username, fixture.Password, ct);
var rooms = await relay.GetClassroomsAsync(fixture.SchoolId, ct);
var empty = rooms.Single(r => r.Id == fixture.EmptyClassId);
var inactive = rooms.Single(r => r.Id == fixture.InactiveClassId);
Check(empty.CanPrepareFirstScreen && !empty.HasInactiveScreens, "Empty class should offer registration.");
Check(inactive.HasInactiveScreens && !inactive.CanPrepareFirstScreen && inactive.Screens.Count == 0,
    "Retired screens must not be replaced.");
Check(rooms.Where(r => r.Screens.Count > 0).All(r => !r.CanPrepareFirstScreen), "Existing screens do not offer first-screen setup.");
using var admin = new ConsoleClient(fixture.Origin);
await admin.LoginAsync(fixture.SchoolCode, fixture.Username, fixture.Password, ct);
await Expect(409, "SNAPSHOT_CHANGED", () => admin.PrepareFirstScreenAsync(empty, Guid.NewGuid(), ct));
var original = (await admin.GetClassroomsAsync(fixture.SchoolId, ct)).Single(r => r.Id == empty.Id);
var requestId = Guid.NewGuid();
using var http = new HttpClient { BaseAddress = new Uri(fixture.Origin) };
async Task RejectRaw(string account, string schoolId, string classId, string termId, int expected, string expectedCode)
{
    using var login = await http.PostAsJsonAsync("/accounts/local/login", new {fixture.SchoolCode, username = account, fixture.Password}, ct);
    login.EnsureSuccessStatusCode();
    var loginData = await login.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
    using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/v2/npep/schools/{schoolId}/classes/{classId}/first-screen");
    var operation = Guid.NewGuid().ToString();
    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", loginData.GetProperty("data").GetProperty("access_token").GetString());
    message.Headers.Add("X-NPEP-Version", "0.16");
    message.Headers.Add("X-Request-Id", operation);
    message.Content = JsonContent.Create(new {requestId = operation, termId});
    using var reply = await http.SendAsync(message, ct);
    var error = await reply.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
    Check((int)reply.StatusCode == expected && error.GetProperty("error").GetProperty("code").GetString() == expectedCode,
        "Expected scoped first-screen refusal " + expectedCode);
}
await RejectRaw(fixture.Viewer, fixture.SchoolId, empty.Id, empty.TermId!, 403, "SCHOOL_ADMIN_REQUIRED");
await RejectRaw(fixture.Username, fixture.OtherSchoolId, empty.Id, empty.TermId!, 403, "SCHOOL_ADMIN_REQUIRED");
await RejectRaw(fixture.Username, fixture.SchoolId, inactive.Id, empty.TermId!, 409, "FIRST_SCREEN_EXISTS");
await Expect(502, null, () => relay.PrepareFirstScreenAsync(empty, requestId, ct));
var binding = await relay.PrepareFirstScreenAsync(empty, requestId, ct);
await Expect(409, "FIRST_SCREEN_EXISTS", () => admin.PrepareFirstScreenAsync(original, Guid.NewGuid(), ct));
var prepared = await admin.GetClassAdmissionAsync(fixture.SchoolId, empty.Id, binding, ct);
Check(!prepared.Configured && !prepared.Enabled, "Registration alone cannot authorize password admission.");
var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(20));

await using (var runtime = new NpepRuntime(directory, "acceptance", () => new(Protocol.Version, Guid.NewGuid(), "Succeeded", null, "")))
{
    Task<NpepState> Command(NpepCommand command) => NativeClassroom.CommandAsync(runtime, command, ct);
    var inspected = await Command(new("inspect", 0, Origin: fixture.Origin));
    var catalogue = inspected.Catalogue!;
    Check((bool)catalogue["truncated"]! && catalogue["items"]!.AsArray().All(row => (string?)row?["screenBindingId"] != binding),
        "The new screen must be outside the legacy global preview.");
    var find = new NpepCommand("find-class", 0, fixture.Origin, ServerInstanceId: (string?)inspected.Server!["serverInstanceId"],
        DeploymentEpoch: (string?)inspected.Server["deploymentEpoch"], SchoolQuery: "OTHER", ClassQuery: empty.Name);
    var seen = new HashSet<string>();
    string? cursor = null;
    do {
        var page = (await Command(find with { CatalogueAfter = cursor })).Catalogue!;
        var rows = page["items"]!.AsArray();
        Check(rows.Count is > 0 and <= 24 && rows.All(r => (string?)r?["schoolId"] == fixture.OtherSchoolId && seen.Add((string)r!["screenBindingId"]!)),
            "Scope and unique screen identities must survive pagination.");
        cursor = (string?)page["nextCursor"];
    } while (cursor is not null);
    Check(seen.Count == 205, "All 205 same-name screens at the other school must be reachable.");
    foreach (string term in new[] { "高二", "%", "G2-4" })
        Check((await Command(find with {SchoolQuery = fixture.SchoolCode, ClassQuery = term})).Catalogue!["items"]!.AsArray().Count == 0,
            "Partial names, wildcards and retired screens must not match.");
    var scoped = (await Command(find with { SchoolQuery = fixture.SchoolCode.ToLowerInvariant(), ClassQuery = "g2-3" })).Catalogue!;
    var selected = scoped["items"]!.AsArray().Single()!;
    Check((string?)selected["screenBindingId"] == binding && (string?)selected["schoolId"] == fixture.SchoolId, "Search isolates the selected school.");
    Check((string?)selected["classId"] == empty.Id && !(bool)selected["preauthorized"]!, "Native catalogue shows the exact unapproved screen.");
    var enabled = await admin.EnableClassAdmissionAsync(prepared, secret, Guid.NewGuid(), ct);
    Check(enabled.IsReady, "A separate explicit action enables admission.");
    var paired = await Command(new("password-class", 0, fixture.Origin, "验收教室终端", find.ServerInstanceId, find.DeploymentEpoch,
        ScreenBindingId: binding, ManagementSecret: secret));
    Check(paired.State == "ACTIVE" && paired.Catalogue is null, "Selected screen should be active.");
    Check(runtime.Handle(new(Protocol.Version, Guid.NewGuid(), "npep.command", Npep: find with {Revision = paired.Revision})).Outcome == "Rejected",
        "An active device cannot start an admission lookup.");
}
using (var restarted = new NpepDevice(directory))
{
    Check((string?)restarted.View()["state"] == "ACTIVE", "Native credentials survive a restart.");
    await restarted.OpenSessionAsync(ct);
    await restarted.ReportAsync(await NpepStatusReader.ReadAsync(null, "acceptance", ct), ct);
    var room = (await admin.GetClassroomsAsync(fixture.SchoolId, ct)).Single(r => r.Id == empty.Id);
    Check(room.Screens.Single().Id == binding && room.Screens.Single().Devices.Single().Name == "验收教室终端",
        "Console receives the newly connected classroom device.");
}
if (fixture.AllowReplacementTestWrites)
    await NativeClassroom.RunReplacementAsync(fixture, admin, directory, empty, binding, secret, ct);
await relay.LogoutAsync(ct);
await admin.LogoutAsync(ct);
Console.WriteLine("PASS: global overflow, exact multi-school search, all 205 screens, Host IPC, explicit admission, restart and status in Console.");

static void Check(bool value, string message)
{
    if (!value) throw new InvalidDataException(message);
}
static async Task Expect(int status, string? code, Func<Task> run)
{
    try { await run(); }
    catch (ConsoleApiException e) when (e.StatusCode == status && (code is null || e.Code == code)) { return; }
    throw new InvalidDataException($"Missing expected refusal {status} {code}.");
}
internal sealed record Fixture(string Origin, string LostReplyOrigin, string SchoolCode, string Username, string Viewer,
    string Password, string SchoolId, string OtherSchoolId, string EmptyClassId, string InactiveClassId, bool AllowFirstScreenTestWrites,
    bool AllowReplacementTestWrites = false);
