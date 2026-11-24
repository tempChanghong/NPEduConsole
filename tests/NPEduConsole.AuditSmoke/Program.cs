using System.Text.Json;
using NPEduConsole.Core;

var setup = JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(args.Single()), JsonSerializerOptions.Web)
    ?? throw new InvalidDataException("Missing isolated fixture.");
if (!new Uri(setup.Origin).IsLoopback) throw new InvalidDataException("Expected a loopback fixture.");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
var ct = timeout.Token;
using var client = new ConsoleClient(setup.Origin);
var schools = await client.LoginAsync(setup.SchoolCode, setup.Username, setup.Password, ct);
Check(schools.Count == 2 && schools.Any(s => s.Id == setup.SecondarySchoolId), "Both managed schools are available.");
Check((await client.GetClassroomsAsync(setup.SecondarySchoolId, ct)).Count == 0, "The second school has no active classes.");
var secondary = await client.GetSchoolAuditLogsAsync(setup.SecondarySchoolId, new(), ct);
Check(secondary.Items.Single() is { Id: "audit-secondary", Action: "FUTURE_ACTION", ActorAccount: null }, "School-level audit and unknown actions.");
Check((await client.GetSchoolAuditLogsAsync(setup.SchoolId, new(ActorType: "SETUP"), ct)).Items.Single().Action == "SCHOOL_MIGRATION_IMPORT_COMPLETED",
    "School migration records can be found by their setup source.");
Check((await client.GetSchoolAuditLogsAsync(setup.SchoolId, new(Action: "STAFF_CONFIGURATION_IMPORT"), ct)).Items.Single().Id == "audit-staff",
    "Staff configuration imports can be filtered by action.");

await CheckReviewedChange("TEACHING_ASSIGNMENT_PREVIEW", "TEACHING_ASSIGNMENT_CHANGED", "WORKSPACE", setup.AuditClassId, 2);
await CheckReviewedChange("TERM_DETAILS_PREVIEW", "TERM_DETAILS_CHANGED", "TERM", setup.AuditTermId, 1);
var accountPreview = (await client.GetSchoolAuditLogsAsync(setup.SchoolId, new(Action: "SCHOOL_MANAGEMENT_ACCOUNT_PREVIEW"), ct)).Items.Single();
Check(accountPreview is { EntityType: "ACCOUNT", Success: true } && accountPreview.EntityId == setup.AuditTeacherId,
    "An account preview identifies the original account.");
Check((await client.GetSchoolAuditLogsAsync(setup.SchoolId, new(Action: "SCHOOL_MANAGEMENT_ACCOUNT_CHANGED"), ct)).Items.Count == 0,
    "Opening the disable-account preview records no account change.");

async Task CheckReviewedChange(string previewAction, string saveAction, string entityType, string entityId, int refusedCount)
{
    var previews = await client.GetSchoolAuditLogsAsync(setup.SchoolId, new(Action: previewAction), ct);
    Check(previews.Items.Count == 2 && previews.Items.All(row => row.RequestPath!.EndsWith("/preview")),
        $"{previewAction} must find the two actual previews separately.");
    var saved = (await client.GetSchoolAuditLogsAsync(setup.SchoolId, new(Action: saveAction, Success: true), ct)).Items.Single();
    var refused = await client.GetSchoolAuditLogsAsync(setup.SchoolId, new(Action: saveAction, Success: false), ct);
    Check(saved.EntityType == entityType && saved.EntityId == entityId && refused.Items.Count == refusedCount &&
        refused.Items.All(row => row.StatusCode == 200 && row.EntityId == saved.EntityId && row.Summary!.Contains("未保存")) &&
        refused.Items.Count(row => row.Summary!.Contains("预览已过期")) == 1 &&
        refused.Items.Count(row => row.Summary!.Contains("未满足办理条件")) == refusedCount - 1,
        $"{saveAction} must show the correct target and unsuccessful no-write responses under the failure filter.");
}

var start = new DateTimeOffset(setup.AuditDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local));
var end = new DateTimeOffset(setup.AuditDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local));
var selection = new SchoolAuditQuery(From: start, To: end.AddMilliseconds(-1), Before: end);
var first = await client.GetSchoolAuditLogsAsync(setup.SchoolId, selection, ct);
Check(first.Items.Count == 50 && first.NextCursor != null, "First page of records from the selected local day.");
var second = await client.GetSchoolAuditLogsAsync(setup.SchoolId, selection with {Cursor = first.NextCursor}, ct);
Check(second.NextCursor == null && second.Items.Count == 3, $"Expected three remaining entries on the selected day, received {second.Items.Count}.");
var entries = first.Items.Concat(second.Items).ToArray();
Check(entries.Select(e => e.Id).SequenceEqual(new[] { "audit-last-microsecond" }
    .Concat(Enumerable.Range(0, 52).Reverse().Select(i => $"audit-tie-{i:D2}"))), "Stable ordering across timestamp ties at the cursor, including the day's final microsecond.");
var historical = (await client.GetSchoolAuditLogsAsync(setup.SchoolId, selection with
    {ActorType = "CLASSROOM_SCREEN", Action = "CLASS_ROSTER_SAVED", Success = false}, ct)).Items.Single();
Check(historical is { StatusCode: 409, ClientIp: "127.0.0.1", RequestMethod: "PUT", RequestPath: "/historic/students",
    ActorScreen.Name.Length: > 0, EntityType: "WORKSPACE" }, "Failure details and screen actor.");
var parameters = historical.Metadata!.Value;
Check(parameters.GetProperty("after").GetArrayLength() == 65 && parameters.GetProperty("nested").GetProperty("pin").GetString() == "[REDACTED]",
    "Legacy parameters are masked without losing the end of a roster.");
var writes = await client.GetSchoolAuditLogsAsync(setup.SchoolId, new(ActorType: "ACCOUNT", Action: "SCHOOL_PROFILE_UPDATED"), ct);
Check(writes.Items.Count == 2 && writes.Items.All(e => e.ActorAccount?.Name == "admin") &&
    writes.Items.Any(e => e.Success && e.StatusCode == 200) && writes.Items.Any(e => !e.Success && e.StatusCode == 422), "Both real manager operations were recorded.");
Check((await client.GetSchoolAuditLogsAsync(setup.SchoolId, selection with {ActorType = "CLASSROOM_SCREEN", Success = true}, ct)).Items.Count == 0,
    "An empty filtered result remains empty.");
await ExpectDenied(() => client.GetSchoolAuditLogsAsync(setup.OtherSchoolId, new(), ct));
await client.LogoutAsync(ct);
await ExpectDenied(() => client.LoginAsync(setup.SchoolCode, setup.Viewer, setup.Password, ct));
Console.WriteLine("PASS: actual Console school audit, no-term school, pagination, local dates, all filters, details and school permissions.");

static void Check(bool passed, string message)
{
    if (!passed) throw new InvalidDataException(message);
}

static async Task ExpectDenied(Func<Task> attempt)
{
    try { await attempt(); }
    catch (ConsoleApiException error) when (error.StatusCode == 403) { return; }
    throw new InvalidDataException("Expected a denied school scope.");
}

internal sealed record Fixture(string Origin, string SchoolId, string SecondarySchoolId, string OtherSchoolId,
    string SchoolCode, string Username, string Viewer, string Password, DateOnly AuditDate,
    string AuditTeacherId, string AuditClassId, string AuditTermId);
