using System.Text.Json;
using NPEduConsole.Core;

if (args.Length is < 1 or > 2 ||
    args.Length == 2 && args[1] is not ("--view-only" or "--local-authorization" or "--view-local-authorization"))
    throw new ArgumentException("Expected the live KV fixture path and an optional test mode.");
VerifyEditingStatus();
var fixture = JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(args[0]), JsonSerializerOptions.Web)
    ?? throw new InvalidDataException("Missing fixture.");
if (!new Uri(fixture.Origin).IsLoopback) throw new InvalidDataException("Smoke tests require a loopback fixture.");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
var ct = timeout.Token;
using var client = new ConsoleClient(fixture.Origin);
var schools = await client.LoginAsync(fixture.SchoolCode, fixture.Username, fixture.Password, ct);
Check(schools.Select(s => s.Id).SequenceEqual([fixture.SchoolId]), "Only the authorized school is listed.");
var rooms = await client.GetClassroomsAsync(fixture.SchoolId, ct);
Check(rooms.Count == 3, "Three active classrooms expected.");
if (args.Length == 2 && args[1] is "--local-authorization" or "--view-local-authorization")
{
    await VerifyLocalAuthorization(args[1] == "--view-local-authorization", fixture, client, rooms, ct);
    await client.LogoutAsync(ct);
    return;
}

foreach (var (name, revision, status) in new[] { ("高二（1）班", 5L, "已应用"), ("高二（2）班", 4L, "已发布，待设备确认") })
{
    var room = rooms.Single(c => c.Name == name);
    var device = room.Screens.Single().Devices.Single();
    Check(device.LastSeenAt.HasValue && room.GradeName == "高二", "Missing room context or last report.");
    var eq = await client.GetEquipmentAsync(fixture.SchoolId, device.Id, ct);
    Check(eq.Locked && eq.LocallyUnlocked == false && eq.ReceivedAt.HasValue, "Lock policy and receipt must be independently observable.");
    Check(eq.EditingStatusText == (revision == 5 ? "已锁定" : "策略同步中"), "Editing status distinguishes a current lock from an earlier receipt.");
    Check(eq.StatusText == status && eq.PublishedRevision == 5 && eq.AppliedRevision == revision, "Published/applied mapping.");
    Check(eq.MicrophoneName == "讲台 USB 麦克风", "Configured microphone should resolve against inventory.");
    Check(eq.Microphones.Select(m => m.Id).SequenceEqual(["microphone-usb", "microphone-array"]), "Read device-reported microphone identifiers.");
    Check(eq.IsInventoryFresh == (revision == 5), "Only class one has a fresh device inventory.");
    Check(!eq.DeviceSupportsLocalMicrophoneChange && !eq.CanGrantLocalMicrophoneChange,
        "A legacy terminal must never be offered a grant, even with a fresh receipt.");
    if (!eq.ServiceSupportsLocalMicrophoneChange)
        await ExpectStatus(409, () => client.SetLocalMicrophoneChangeAsync(eq, true, Guid.NewGuid(), ct), "LOCAL_MICROPHONE_CHANGE_UNSUPPORTED");
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

static async Task VerifyLocalAuthorization(bool viewOnly, Fixture fixture, ConsoleClient client, IReadOnlyList<Classroom> rooms,
    CancellationToken ct)
{
    string DeviceId(string name) => rooms.Single(room => room.Name == name).Screens.Single().Devices.Single().Id;
    const string schoolMicrophone = "microphone-usb";
    const string localMicrophone = "microphone-array";
    string deviceId = DeviceId("高二（1）班");
    string offlineId = DeviceId("高二（2）班");
    Task<Equipment> Read() => client.GetEquipmentAsync(fixture.SchoolId, deviceId, ct);
    var initial = await Read();
    Check(initial.PublishedRevision == 5 && initial.MicrophoneId == schoolMicrophone && initial.Locked &&
        initial.CanGrantLocalMicrophoneChange && !initial.AllowsLocalMicrophoneChange &&
        initial.Microphones.All(item => item.Id != schoolMicrophone),
        "A present, capable terminal may be authorized even if the school endpoint is unplugged.");
    var offline = await client.GetEquipmentAsync(fixture.SchoolId, offlineId, ct);
    Check(offline.CanRevokeLocalMicrophoneChange && !offline.IsInventoryFresh && !offline.DeviceSupportsLocalMicrophoneChange,
        "School may withdraw an existing grant when the terminal no longer reports.");
    if (viewOnly) return;

    long offlineRevision = await client.SetLocalMicrophoneChangeAsync(offline, false, Guid.NewGuid(), ct);
    var revokedOffline = await client.GetEquipmentAsync(fixture.SchoolId, offlineId, ct);
    Check(revokedOffline.PublishedRevision == offlineRevision && !revokedOffline.AllowsLocalMicrophoneChange &&
        !revokedOffline.HasCurrentReceipt && revokedOffline.MicrophoneId == schoolMicrophone && revokedOffline.Locked,
        "Offline revocation must preserve the policy and wait for the terminal.");
    await ExpectStatus(409, () => client.SetLocalMicrophoneChangeAsync(revokedOffline, true, Guid.NewGuid(), ct),
        "LOCAL_MICROPHONE_CHANGE_UNAVAILABLE");

    var grant = Guid.NewGuid();
    long granted = await client.SetLocalMicrophoneChangeAsync(initial, true, grant, ct);
    Check(await client.SetLocalMicrophoneChangeAsync(initial, true, grant, ct) == granted, "Grant replay must not republish.");
    await ExpectStatus(409, () => client.SetLocalMicrophoneChangeAsync(initial, true, Guid.NewGuid(), ct), "REVISION_CONFLICT");
    var armed = await WaitFor(eq => eq.AppliedRevision == granted && !eq.HasCurrentLocalMicrophoneOverride);
    Check(armed.AllowsLocalMicrophoneChange && armed.MicrophoneId == schoolMicrophone,
        "Granting permission must retain the school input before any local override.");
    var partial = await WaitFor(eq => eq.AppliedRevision < granted && eq.ReportsLocalMicrophoneOverride);
    Check(partial.CanSelectSchoolMicrophone(schoolMicrophone) && !partial.MatchesLocalMicrophoneOverride &&
        !partial.MatchesPublishedMicrophone && partial.StatusText != "已应用",
        "A recent input report makes A selectable even though another part of the policy is not applied.");
    var used = await WaitFor(eq => eq.MatchesLocalMicrophoneOverride && eq.ErrorCode is null);
    Check(used.PublishedRevision == granted && used.MicrophoneId == schoolMicrophone && used.ReportedMicrophoneId == localMicrophone &&
        !used.MatchesPublishedMicrophone && used.StatusText == "现场已换麦克风并重新锁定" && used.EditingStatusText == "已锁定",
        "The actual classroom input must be B and the versioned school preference must stay A.");
    Check(used.CanSelectSchoolMicrophone(schoolMicrophone), "A can be selected again to clear the local override.");
    long restored = await client.ChangeMicrophoneAsync(partial, schoolMicrophone, Guid.NewGuid(), ct);
    var reset = await WaitFor(eq => eq.AppliedRevision == restored && eq.MatchesPublishedMicrophone);
    Check(reset.AllowsLocalMicrophoneChange && !reset.HasCurrentLocalMicrophoneOverride && reset.Locked,
        "Reselecting A must restore the school input but preserve the local permission.");

    if (fixture.LostReplyOrigin is null || !new Uri(fixture.LostReplyOrigin).IsLoopback)
        throw new InvalidDataException("Missing local lost-reply gateway.");
    using var relay = new ConsoleClient(fixture.LostReplyOrigin);
    await relay.LoginAsync(fixture.SchoolCode, fixture.Username, fixture.Password, ct);
    var snapshot = await relay.GetEquipmentAsync(fixture.SchoolId, deviceId, ct);
    await ExpectStatus(502, () => relay.SetLocalMicrophoneChangeAsync(snapshot, false, Guid.NewGuid(), ct), "PUBLISH_UNCONFIRMED");
    var saved = await Read();
    Check(saved.PublishedRevision == restored + 1 && !saved.AllowsLocalMicrophoneChange,
        "Re-read must resolve a committed revoke with a lost response.");
    await relay.LogoutAsync(ct);
    Console.WriteLine("PASS: authorization with A unplugged, idempotent grant, revision conflict, A/B receipt and re-lock, restore A from a partial receipt with authorization preserved, offline and lost-response revocation.");

    async Task<Equipment> WaitFor(Func<Equipment, bool> predicate)
    {
        while (true)
        {
            var equipment = await Read();
            if (predicate(equipment)) return equipment;
            await Task.Delay(300, ct);
        }
    }
}

static void VerifyEditingStatus()
{
    var locked = new Equipment(5, 5, true, false, null, DateTimeOffset.UtcNow, null);
    var states = new (Equipment Snapshot, string Expected)[]
    {
        (locked, "已锁定"),
        (locked with { LocallyUnlocked = true }, "现场已解锁"),
        (locked with { Locked = false }, "学校策略未锁定"),
        (locked with { ReceivedAt = null, AppliedRevision = null, LocallyUnlocked = null }, "暂无设备回执"),
        (locked with { AppliedRevision = 4, LocallyUnlocked = true }, "策略同步中"),
        (locked with { ErrorCode = "EQUIPMENT_PENDING" }, "策略同步中"),
        (locked with { AppliedRevision = 6 }, "回执版本不一致"),
        (locked with { LocallyUnlocked = null }, "设备未上报编辑状态")
    };
    foreach (var (snapshot, expected) in states)
        Check(snapshot.EditingStatusText == expected, $"Editing status: expected {expected}, got {snapshot.EditingStatusText}.");
}

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
