using System.Net.Http.Json;
using System.Text.Json;
using NPEduConsole.Core;

var setup = JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(args.Single()), JsonSerializerOptions.Web)!;
if (!new Uri(setup.Origin).IsLoopback || !new Uri(setup.LostReplyOrigin).IsLoopback) throw new InvalidDataException("Expected isolated servers.");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
var ct = timeout.Token;
using var client = new ConsoleClient(setup.Origin);
await client.LoginAsync(setup.SchoolCode, setup.Username, setup.Password, ct);
var rooms = await client.GetClassroomsAsync(setup.SchoolId, ct);
Check(rooms.Single(r => r.Id == setup.ClassId).Screens.Single().DeviceOverviewUnavailable &&
    rooms.Single(r => r.Id == setup.EmptyClassId).Screens.Count == 0, "Class selection works with NPEP disabled.");

var first = await client.GetClassRosterAsync(setup.SchoolId, setup.ClassId, ct);
Check(first.Students.Count == 3, "Expected the initial roster.");
var edits = RosterEditor.Import("002\t陈辰\r\n001\t陈晨\r\n003\t王同学", first.Students, replace: true);
Check(edits[0].Id == "roster-b" && edits[1].Id == "roster-a" && edits[2].Id is null, "Same names preserve distinct ids and leading-zero numbers.");
Check(RosterEditor.Changes(first.Students, edits).Count == 5, "Preview the edit, two order changes, removal and addition.");
var saved = await client.SaveClassRosterAsync(first, edits, ct);
Check(RosterEditor.MatchesSaved(first, edits, saved), "Saved class matches the preview.");

using var screen = new HttpClient { BaseAddress = new Uri(setup.Origin) };
screen.DefaultRequestHeaders.Add("X-Classworks-Screen-Token", setup.ScreenToken);
var screenRead = await screen.GetFromJsonAsync<JsonElement>("/api/v2/classroom-screens/students", ct);
Check(screenRead.GetProperty("rosterRevision").GetString() == saved.Revision, "Classroom and Console share the committed revision.");
var screenEdit = saved.Students.Select((s, i) => i == 0 ? s with { Name = "大屏更新" } : s).ToArray();
var changed = await screen.PutAsJsonAsync("/api/v2/classroom-screens/students", new { students = screenEdit, expectedRevision = saved.Revision }, ct);
changed.EnsureSuccessStatusCode();
var stale = saved.Students.Select((s, i) => i == 1 ? s with { Name = "管理员草稿" } : s).ToArray();
await Expect(409, "CLASS_ROSTER_CONFLICT", () => client.SaveClassRosterAsync(saved, stale, ct));
Check(stale[1].Name == "管理员草稿" && (await client.GetClassRosterAsync(setup.SchoolId, setup.ClassId, ct)).Students[0].Name == "大屏更新",
    "A conflict preserves the draft and the screen's change.");

using var relay = new ConsoleClient(setup.LostReplyOrigin);
await relay.LoginAsync(setup.SchoolCode, setup.Username, setup.Password, ct);
await Expect(409, null, () => relay.SaveClassRosterAsync(saved, stale, ct));
var baseline = await relay.GetClassRosterAsync(setup.SchoolId, setup.ClassId, ct);
var pending = RosterEditor.Import("004 赵同学", baseline.Students, false);
await Expect(502, null, () => relay.SaveClassRosterAsync(baseline, pending, ct));
var actual = await relay.GetClassRosterAsync(setup.SchoolId, setup.ClassId, ct);
Check(RosterEditor.MatchesSaved(baseline, pending, actual), "Confirm an uncertain response through a read.");
await relay.LogoutAsync(ct);

var empty = await client.GetClassRosterAsync(setup.SchoolId, setup.EmptyClassId, ct);
Check(empty.Students.Count == 0, "An unbound class can be edited.");
var full = Enumerable.Range(1, 120).Select(i => new RosterStudent(null, "学生", i.ToString("D3"))).ToArray();
var fullSaved = await client.SaveClassRosterAsync(empty, full, ct);
Check(fullSaved.Students.Count == 120, "Save a full-size roster without a screen.");
var cleared = await client.SaveClassRosterAsync(fullSaved, [], ct);
Check(cleared.Students.Count == 0, "A confirmed empty roster is saved.");
var archived = await client.GetClassRosterAsync(setup.SchoolId, setup.ArchivedClassId, ct);
await Expect(409, "CLASS_ROSTER_TERM_CHANGED", () => client.SaveClassRosterAsync(archived, full, ct));
await Expect(403, null, () => client.GetClassRosterAsync(setup.OtherSchoolId, setup.ClassId, ct));
await Expect(404, null, () => client.GetClassRosterAsync(setup.SchoolId, "missing-class", ct));
await client.LogoutAsync(ct);

var twins = new[] { new RosterStudent("a", "李明", null), new RosterStudent("b", "李明", null) };
ExpectInvalid(() => RosterEditor.Import("\t李明", twins, true));
ExpectInvalid(() => RosterEditor.Import("李明\n李明", [], false));
ExpectInvalid(() => RosterEditor.Import("001\t甲\n001\t乙", [], false));
ExpectInvalid(() => RosterEditor.Import("001\t", [], true));
ExpectInvalid(() => RosterEditor.Import("001\t甲\t乙", [], true));
ExpectInvalid(() => RosterEditor.Validate([..full, new(null, "多出一人", null)]));
ExpectInvalid(() => RosterEditor.Validate([new(null, new string('字', 65), null)]));
Check(RosterEditor.Validate(twins).Count == 2 && RosterEditor.Import("\t欧阳 同学", [], false).Single().Name == "欧阳 同学",
    "Manual same-name editing and blank spreadsheet cells.");
Console.WriteLine("PASS: Console roster entry, identity, append/replace, bounded input, save, term guards, screen conflict and lost-reply reconciliation.");

static void Check(bool ok, string message) { if (!ok) throw new InvalidDataException(message); }
static void ExpectInvalid(Action operation)
{
    try { operation(); }
    catch (ArgumentException) { return; }
    throw new InvalidDataException("Expected invalid roster input.");
}
static async Task Expect(int status, string? code, Func<Task> operation)
{
    try { await operation(); }
    catch (ConsoleApiException e) when (e.StatusCode == status && (code is null || code == e.Code)) { return; }
    throw new InvalidDataException($"Expected HTTP {status} {code}.");
}
internal sealed record Fixture(string Origin, string LostReplyOrigin, string SchoolCode, string Username, string Password,
    string SchoolId, string OtherSchoolId, string ClassId, string EmptyClassId, string ArchivedClassId, string ScreenToken);
