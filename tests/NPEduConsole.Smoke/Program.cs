using System.Text.Json;
using NPEduConsole.Core;

if (args.Length is < 1 or > 2 || args.Length == 2 && args[1] != "--view-only")
    throw new ArgumentException("Expected the live KV fixture's desktop.json path and optional --view-only.");
var fixture = JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(args[0]), JsonSerializerOptions.Web)
    ?? throw new InvalidDataException("Missing fixture.");
if (!new Uri(fixture.Origin).IsLoopback) throw new InvalidDataException("Smoke tests require a loopback fixture.");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
var ct = timeout.Token;
using var client = new ConsoleClient(fixture.Origin);
var schools = await client.LoginAsync(fixture.SchoolCode, fixture.Username, fixture.Password, ct);
Check(schools.Select(s => s.Id).SequenceEqual([fixture.SchoolId]), "Only the authorized school is listed.");
var rooms = await client.GetClassroomsAsync(fixture.SchoolId, ct);
Check(rooms.Count == 3, "Three active classrooms expected.");
foreach (var (name, revision, status) in new[] { ("高二（1）班", 5L, "已应用"), ("高二（2）班", 4L, "已发布，待设备确认") })
{
    var room = rooms.Single(c => c.Name == name);
    var device = room.Screens.Single().Devices.Single();
    Check(device.LastSeenAt.HasValue && room.GradeName == "高二", "Missing room context or last report.");
    var eq = await client.GetEquipmentAsync(fixture.SchoolId, device.Id, ct);
    Check(eq.Locked && eq.LocallyUnlocked == false && eq.ReceivedAt.HasValue, "Lock policy and receipt must be independently observable.");
    Check(eq.StatusText == status && eq.PublishedRevision == 5 && eq.AppliedRevision == revision, "Published/applied mapping.");
    Check(eq.MicrophoneName == "讲台 USB 麦克风", "Configured microphone should resolve against inventory.");
    Check(eq.Microphones.Select(m => m.Id).SequenceEqual(["microphone-usb", "microphone-array"]), "Read device-reported microphone identifiers.");
    Check(eq.IsInventoryFresh == (revision == 5), "Only class one has a fresh device inventory.");
    if (revision == 4)
        await ExpectStatus(409, () => client.ChangeMicrophoneAsync(eq, "microphone-array", Guid.NewGuid(), ct), "EQUIPMENT_INVENTORY_STALE");
    await ExpectStatus(403, () => client.GetEquipmentAsync(fixture.OtherSchoolId, device.Id, ct));
}
Check(rooms.Single(c => c.Name == "高二（3）班").Screens.Count == 0, "Unbound classroom must remain selectable.");
await ExpectStatus(409, () => client.GetEquipmentAsync(fixture.SchoolId, fixture.RevokedDeviceId, ct));
Check((await client.GetClassroomsAsync(fixture.SchoolId, ct)).Count == 3, "A revoked device must not sign out its manager.");
await ExpectStatus(403, () => client.GetClassroomsAsync(fixture.OtherSchoolId, ct));
if (args.Length == 1)
{
    // Run writes only for the disposable smoke run; --serve leaves the online
    // classroom at its original revision so desktop scenarios can select either mic.
    var device = rooms.Single(c => c.Name == "高二（1）班").Screens.Single().Devices.Single();
    var before = await client.GetEquipmentAsync(fixture.SchoolId, device.Id, ct);
    const string target = "microphone-array";
    await ExpectStatus(409, () => client.ChangeMicrophoneAsync(before, "removed-from-inventory", Guid.NewGuid(), ct), "EQUIPMENT_UNAVAILABLE");
    await ExpectStatus(400, () => client.ChangeMicrophoneAsync(before, before.MicrophoneId!, Guid.NewGuid(), ct), "INVALID_CHANGE");
    var requestId = Guid.NewGuid();
    var published = await client.ChangeMicrophoneAsync(before, target, requestId, ct);
    Check(published == before.PublishedRevision + 1, "Only the expected revision may advance.");
    Check(await client.ChangeMicrophoneAsync(before, target, requestId, ct) == published, "Replay of the same request must be idempotent.");
    await ExpectStatus(409, () => client.ChangeMicrophoneAsync(before, target, Guid.NewGuid(), ct), "REVISION_CONFLICT");
    var sawBusy = false;
    var sawMismatchedReceipt = false;
    var sawMissingEndpoint = false;
    while (true)
    {
        var observed = await client.GetEquipmentAsync(fixture.SchoolId, device.Id, ct);
        Check(observed.MicrophoneId == target && observed.Locked && observed.PublishedRevision == published, "Published policy changed unexpectedly.");
        if (observed.ErrorCode == "EQUIPMENT_PENDING")
        {
            Check(observed.StatusText == "正在监测，结束后切换" && observed.AppliedRevision == before.AppliedRevision
                && observed.ReportedMicrophoneId == before.MicrophoneId, "An in-progress capture must keep its original input.");
            sawBusy = true;
        }
        if (observed.AppliedRevision == published)
        {
            if (observed.ReportedMicrophoneId != target)
            {
                Check(!observed.MatchesPublishedMicrophone && observed.StatusText != "已应用",
                    "Matching versions with the wrong physical microphone must remain unconfirmed.");
                sawMismatchedReceipt = true;
            }
            else if (observed.Microphones.All(m => m.Id != target))
            {
                Check(!observed.MatchesPublishedMicrophone && observed.StatusText != "已应用",
                    "A stored endpoint ID cannot confirm a disconnected microphone missing from the reported inventory.");
                sawMissingEndpoint = true;
            }
            else
            {
                Check(sawBusy && sawMismatchedReceipt && sawMissingEndpoint && observed.ErrorCode is null && observed.MatchesPublishedMicrophone
                    && observed.StatusText == "已应用", "Completion requires device confirmation of the selected microphone.");
                break;
            }
        }
        await Task.Delay(450, ct);
    }
    Console.WriteLine("PASS: publish, replay, version conflict, capture pending, wrong input, disconnected endpoint with matching IDs, and complete receipt.");
    if (fixture.LostReplyOrigin is null || !new Uri(fixture.LostReplyOrigin).IsLoopback)
        throw new InvalidDataException("Missing local lost-reply gateway.");
    using var retryClient = new ConsoleClient(fixture.LostReplyOrigin);
    await retryClient.LoginAsync(fixture.SchoolCode, fixture.Username, fixture.Password, ct);
    var next = await retryClient.GetEquipmentAsync(fixture.SchoolId, device.Id, ct);
    await ExpectStatus(502, () => retryClient.ChangeMicrophoneAsync(next, before.MicrophoneId!, Guid.NewGuid(), ct), "PUBLISH_UNCONFIRMED");
    var after = await retryClient.GetEquipmentAsync(fixture.SchoolId, device.Id, ct);
    Check(after.PublishedRevision == next.PublishedRevision + 1 && after.MicrophoneId == before.MicrophoneId,
        "Reading back the actual KV policy must resolve an unknown publish outcome.");
    await retryClient.LogoutAsync(ct);
    Console.WriteLine("PASS: KV committed but the gateway returned 502; client reported PUBLISH_UNCONFIRMED and read back the new policy.");
}
await client.LogoutAsync(ct);
await ExpectStatus(401, () => client.GetClassroomsAsync(fixture.SchoolId, ct));
await ExpectStatus(403, () => client.LoginAsync(fixture.SchoolCode, fixture.Viewer, fixture.Password, ct));
var noReceipt = new Equipment(0, null, false, null, null, null, null);
Check(noReceipt.StatusText == "尚无设备回执", "Missing inventory cannot acknowledge revision zero.");
Check((noReceipt with { AppliedRevision = 0, ErrorCode = "UNAVAILABLE" }).StatusText == "设备报告应用异常",
    "A reported error takes precedence over matching revisions.");
Console.WriteLine("PASS: ConsoleClient logged in, read all three classroom states, handled a missing/error receipt, rejected foreign scope and viewer, and signed out.");

static void Check(bool value, string message)
{
    if (!value) throw new InvalidDataException(message);
}

static async Task ExpectStatus(int status, Func<Task> operation, string? code = null)
{
    try { await operation(); }
    catch (ConsoleApiException ex) when (ex.StatusCode == status && (code is null || ex.Code == code)) { return; }
    throw new InvalidDataException($"Expected status {status}.");
}

internal sealed record Fixture(string Origin, string SchoolCode, string Username, string Password,
    string Viewer, string SchoolId, string OtherSchoolId, string RevokedDeviceId, string? LostReplyOrigin);
