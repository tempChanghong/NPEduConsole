using System.Text.Json;
using NPEduConsole.Core;

var setup = JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(args.Single()), JsonSerializerOptions.Web)!;
if (!new Uri(setup.Origin).IsLoopback || !new Uri(setup.LostReplyOrigin).IsLoopback || !setup.AllowClassCreationTestWrites)
    throw new InvalidDataException("Expected an isolated class creation fixture.");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
var ct = timeout.Token;
using var client = new ConsoleClient(setup.LostReplyOrigin);
await client.LoginAsync(setup.SchoolCode, setup.Username, setup.Password, ct);
var context = await client.GetClassCreationContextAsync(setup.SchoolId, ct);
Check(context.TermId == setup.TermId && context.Grades.Any(g => g.Id == setup.EmptyGradeId), "A grade with no classes must be offered.");
var empty = await client.GetClassCreationContextAsync(setup.EmptySchoolId, ct);
Check(empty.Grades.Count == 1 && (await client.GetClassroomsAsync(setup.EmptySchoolId, ct)).Count == 0, "A school with no classes can open creation.");
await Expect(403, null, () => client.GetClassCreationContextAsync(setup.OtherSchoolId, ct));
var operation = Guid.NewGuid();
Task<string> Create(Guid id, string name = "测试一班") => client.CreateClassAsync(context, setup.EmptyGradeId, "g3-01", name, false, id, ct);
await Expect(502, null, () => Create(operation));
var id = await Create(operation);
var room = (await client.GetClassroomsAsync(setup.SchoolId, ct)).Single(c => c.Id == id);
Check(room.GradeName == "高三" && room.Name == "测试一班" && room.Screens.Count == 0, "Confirmed class is in the right grade.");
var roster = await client.GetClassRosterAsync(setup.SchoolId, id, ct);
Check(roster.Students.Count == 0, "Initial class roster should be empty.");
await client.SaveClassRosterAsync(roster, RosterEditor.Import("001 张同学\n002 李同学", [], false), ct);
using var direct = new ConsoleClient(setup.Origin);
await direct.LoginAsync(setup.SchoolCode, setup.Username, setup.Password, ct);
var fresh = await direct.GetClassCreationContextAsync(setup.SchoolId, ct);
await Expect(409, "WORKSPACE_CODE_EXISTS", () => direct.CreateClassAsync(fresh, setup.EmptyGradeId, "G3-01", "另一个班", true, Guid.NewGuid(), ct));
await Expect(409, "CLASS_CREATION_REQUEST_CONFLICT", () => direct.CreateClassAsync(fresh, setup.EmptyGradeId, "G3-02", "另一个班", true, operation, ct));
await Expect(409, "SNAPSHOT_CHANGED", () => direct.CreateClassAsync(context, setup.EmptyGradeId, "G3-03", "旧会话", true, Guid.NewGuid(), ct));
await direct.LogoutAsync(ct);
await client.LogoutAsync(ct);
Console.WriteLine("PASS: current grade directory, empty school, lost creation acknowledgement, exact returned class, first roster, duplicate code and session change.");

static void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
}
static async Task Expect(int status, string? code, Func<Task> action)
{
    try { await action(); }
    catch (ConsoleApiException e) when (e.StatusCode == status && (code is null || e.Code == code)) { return; }
    throw new Exception("Missing expected HTTP " + status + " " + code);
}
internal sealed record Fixture(string Origin, string LostReplyOrigin, string SchoolCode, string Username, string Password,
    string SchoolId, string OtherSchoolId, string EmptySchoolId, string EmptyGradeId, string TermId, bool AllowClassCreationTestWrites);
