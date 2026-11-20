using System.Text.Json;
using NPEduConsole.Core;

if (args.Length != 1) throw new ArgumentException("Expected the live KV fixture's desktop.json path.");
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
    await ExpectStatus(403, () => client.GetEquipmentAsync(fixture.OtherSchoolId, device.Id, ct));
}
Check(rooms.Single(c => c.Name == "高二（3）班").Screens.Count == 0, "Unbound classroom must remain selectable.");
await ExpectStatus(409, () => client.GetEquipmentAsync(fixture.SchoolId, fixture.RevokedDeviceId, ct));
Check((await client.GetClassroomsAsync(fixture.SchoolId, ct)).Count == 3, "A revoked device must not sign out its manager.");
await ExpectStatus(403, () => client.GetClassroomsAsync(fixture.OtherSchoolId, ct));
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

static async Task ExpectStatus(int status, Func<Task> operation)
{
    try { await operation(); }
    catch (ConsoleApiException ex) when (ex.StatusCode == status) { return; }
    throw new InvalidDataException($"Expected status {status}.");
}

internal sealed record Fixture(string Origin, string SchoolCode, string Username, string Password,
    string Viewer, string SchoolId, string OtherSchoolId, string RevokedDeviceId);
