using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduConsole.Core;

var fixture = JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(args.Single()), JsonSerializerOptions.Web)
    ?? throw new InvalidDataException("Missing class study fixture.");
if (!new Uri(fixture.Origin).IsLoopback || !new Uri(fixture.LostReplyOrigin).IsLoopback)
    throw new InvalidDataException("Class study smoke needs isolated loopback services.");
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(70));
var ct = deadline.Token;
using var admin = new ConsoleClient(fixture.Origin);
await admin.LoginAsync(fixture.SchoolCode, fixture.Username, fixture.Password, ct);
var rooms = await admin.GetClassroomsAsync(fixture.SchoolId, ct);
Check(rooms.Count == 4 && rooms.All(room => room.TermId == fixture.TermId), "All four classrooms carry the active term.");
var inheritedRoom = rooms.Single(room => room.Name == "高二（1）班");
var recurringRoom = rooms.Single(room => room.Name == "高二（2）班");
var blankRoom = rooms.Single(room => room.Name == "高一（1）班");
var legacyRoom = rooms.Single(room => room.Name == "高一（2）班");
var date = fixture.StudyDate;
TimeOnly At(int hour, int minute = 0) => new(hour, minute);
Task<ClassStudySettings> Read(Classroom room, ConsoleClient? client = null) =>
    (client ?? admin).GetClassStudySettingsAsync(fixture.SchoolId, fixture.TermId, room.Id, ct);

using var http = new HttpClient { BaseAddress = new Uri(fixture.Origin) };
string token = await Login(fixture.Username);
string viewer = await Login(fixture.Viewer);
await Expect(403, null, () => admin.GetClassStudySettingsAsync(fixture.OtherSchoolId, fixture.TermId, inheritedRoom.Id, ct));
await Raw("?termId=" + fixture.TermId, viewer, null, 403);
var list = await Raw("?termId=" + fixture.TermId, token);
var initialGrade = list.GetProperty("settings").EnumerateArray().Single(row => row.GetProperty("targetType").GetString() == "GRADE");
string gradeId = initialGrade.GetProperty("targetId").GetString()!;
await Raw("/preview", viewer, id => new { requestId = id, fixture.TermId, targetType = "GRADE", targetId = gradeId,
    expectedRevision = 1, policy = initialGrade.GetProperty("policy"), previewDate = date }, 403);

var inherited = await Read(inheritedRoom);
Check(inherited.Source == StudyPolicySource.Grade && inherited.FreezesGradeInheritance && inherited.NoiseEnabled && inherited.PhotosEnabled &&
    inherited.Daily.Rules.Count == 3 && inherited.Exam.Rules.Count == 1, "Inherited multi-window and collection policy resolves correctly.");
await Expect(400, "STUDY_DRAFT_UNAVAILABLE", () => admin.PreviewClassStudyAsync(inherited, date, At(21), At(21, 30), StudyRepeat.Once, ct));
await Expect(400, "STUDY_DRAFT_UNAVAILABLE", () => admin.PreviewClassStudyAsync(inherited, date.AddDays(2), At(19), At(19, 30), StudyRepeat.Once, ct));
await Expect(400, "STUDY_DRAFT_UNAVAILABLE", () => admin.PreviewClassStudyAsync(inherited, date, At(18, 30), At(19), StudyRepeat.Once, ct));
var stalePreview = await admin.PreviewClassStudyAsync(inherited, date, At(19), At(19, 30), StudyRepeat.Once, ct);
Check(stalePreview.DailyWindows.Select(window => window.Start.Hour).SequenceEqual([18, 19]) &&
    stalePreview.ExamWindows.SequenceEqual(inherited.Exam.WindowsOn(date)), "Preview adds precisely one daily window and keeps the exam window.");
Check((await Read(inheritedRoom)).Revision == 0, "Preview never publishes.");

var recurring = await Read(recurringRoom);
await Expect(400, "STUDY_DRAFT_UNAVAILABLE", () => admin.PreviewClassStudyAsync(recurring, date.AddDays(7), At(20), At(20, 45), StudyRepeat.Weekly, ct));
var weekly = await admin.PreviewClassStudyAsync(recurring, date, At(20), At(20, 45), StudyRepeat.Weekly, ct);
Check(await admin.PublishClassStudyAsync(weekly, Guid.NewGuid(), ct) == 2, "First safe weekly run is published at the selected date.");
var recurringSaved = await Read(recurringRoom);
Check(recurringSaved.Daily.WindowsOn(date.AddDays(-7)).Count == 0 && recurringSaved.Daily.WindowsOn(date.AddDays(7)).Count == 2 &&
    recurringSaved.Daily.Exceptions.SequenceEqual(recurring.Daily.Exceptions) &&
    recurringSaved.Exam.WindowsOn(date).SequenceEqual(recurring.Exam.WindowsOn(date)), "Weekly schedule never backdates or overwrites existing dates.");
await Expect(409, "STUDY_PREVIEW_CHANGED", () => admin.PublishClassStudyAsync(stalePreview, Guid.NewGuid(), ct));

// An intervening grade edit must invalidate an inherited draft even if its hours happen to be identical.
var changedGrade = JsonNode.Parse(initialGrade.GetProperty("policy").GetRawText())!;
changedGrade["settings"]!["homework"]!["rotationSeconds"] = 95;
var gradePreview = await Raw("/preview", token, id => new { requestId = id, fixture.TermId, targetType = "GRADE", targetId = gradeId,
    expectedRevision = 1, policy = changedGrade, previewDate = date });
await Raw("", token, id => new { requestId = id, fixture.TermId, targetType = "GRADE", targetId = gradeId,
    expectedRevision = 1, policy = changedGrade, expectedPreviewFingerprint = gradePreview.GetProperty("previewFingerprint").GetString() });
await Expect(409, "STUDY_SETTING_VERSION_CONFLICT", () => admin.PreviewClassStudyAsync(inherited, date, At(19), At(19, 30), StudyRepeat.Once, ct));
inherited = await Read(inheritedRoom);
var once = await admin.PreviewClassStudyAsync(inherited, date, At(19), At(19, 30), StudyRepeat.Once, ct);
Check(await admin.PublishClassStudyAsync(once, Guid.NewGuid(), ct) == 1, "Inherited daily settings publish a class-only override.");
var after = await Read(inheritedRoom);
foreach (var offset in Enumerable.Range(-2, 37).Where(offset => offset != 0))
    Check(after.Daily.WindowsOn(date.AddDays(offset)).SequenceEqual(inherited.Daily.WindowsOn(date.AddDays(offset))), "Single-date write changed an unrelated occurrence.");
Check(after.Daily.Exceptions.Count == inherited.Daily.Exceptions.Count + 1 && inherited.Daily.Exceptions.All(after.Daily.Exceptions.Contains),
    "Exactly one natural substitution was added, with original exceptions retained.");
var publishedList = await Raw("?termId=" + fixture.TermId, token);
var rows = publishedList.GetProperty("settings").EnumerateArray().ToArray();
var gradeAfter = rows.Single(row => row.GetProperty("targetId").GetString() == gradeId);
var classAfter = rows.Single(row => row.GetProperty("targetId").GetString() == inheritedRoom.Id);
Check(gradeAfter.GetProperty("revision").GetInt32() == 2 && after.Source == StudyPolicySource.Classroom &&
    JsonNode.DeepEquals(changedGrade, JsonNode.Parse(gradeAfter.GetProperty("policy").GetRawText())), "Class publication did not change the grade.");
var expectedOtherSettings = changedGrade["settings"]!.DeepClone().AsObject();
expectedOtherSettings.Remove("schedule");
var actualOtherSettings = JsonNode.Parse(classAfter.GetProperty("policy").GetProperty("settings").GetRawText())!.AsObject();
actualOtherSettings.Remove("schedule");
Check(JsonNode.DeepEquals(expectedOtherSettings, actualOtherSettings), "Exam, homework, presentation, noise and photos were copied verbatim.");

var legacy = await Read(legacyRoom);
Check(legacy.LegacyNoiseActive, "Legacy noise takeover is detected.");
await Expect(409, "STUDY_LEGACY_NOISE_ACTIVE", () => admin.PreviewClassStudyAsync(legacy, date, At(19), At(20), StudyRepeat.Once, ct));
using var relay = new ConsoleClient(fixture.LostReplyOrigin);
await relay.LoginAsync(fixture.SchoolCode, fixture.Username, fixture.Password, ct);
var blank = await Read(blankRoom, relay);
Check(blank.Source == StudyPolicySource.None && blankRoom.Screens.Count == 0, "Study can be scheduled before any device is paired.");
var overnight = await relay.PreviewClassStudyAsync(blank, date, At(23, 30), At(0, 45), StudyRepeat.Once, ct);
Check(overnight.DailyWindows.Single().End.Date == date.AddDays(1).ToDateTime(TimeOnly.MinValue), "Cross-midnight preview names the next day.");
await Expect(502, "STUDY_PUBLISH_UNCONFIRMED", () => relay.PublishClassStudyAsync(overnight, Guid.NewGuid(), ct));
var stored = await Read(blankRoom, relay);
Check(stored.Revision == 1 && stored.Daily.From == date && stored.Daily.To == date && stored.Daily.WindowsOn(date).Count == 1 &&
    stored.Daily.WindowsOn(date.AddDays(7)).Count == 0 && stored.Exam.Rules.Count == 0 &&
    !stored.NoiseEnabled && !stored.PhotosEnabled && !stored.HomeworkEnabled, "A new one-off schedule opens display only and never repeats or starts extra collection.");
stored = await Read(blankRoom);
var secondDate = date.AddDays(1);
var secondSession = await admin.PreviewClassStudyAsync(stored, secondDate, At(19), At(20), StudyRepeat.Once, ct);
Check(await admin.PublishClassStudyAsync(secondSession, Guid.NewGuid(), ct) == 2, "The next day can be added as a separate one-off.");
var twoDays = await Read(blankRoom);
Check(twoDays.Daily.From == date && twoDays.Daily.To == secondDate && twoDays.Daily.Exceptions.Count == 0 &&
    twoDays.Daily.WindowsOn(date).SequenceEqual(stored.Daily.WindowsOn(date)) && twoDays.Daily.WindowsOn(secondDate).Count == 1 &&
    twoDays.Daily.WindowsOn(date.AddDays(7)).Count == 0 && twoDays.Daily.WindowsOn(secondDate.AddDays(7)).Count == 0,
    "A bounded extension preserves yesterday and adds only the chosen day without synthetic exceptions.");
await Expect(400, "STUDY_DRAFT_UNAVAILABLE", () => admin.PreviewClassStudyAsync(twoDays, date.AddDays(7), At(19), At(20), StudyRepeat.Once, ct));
Check((await Read(blankRoom)).Revision == 2, "A cross-week extension that revives an old session cannot publish.");
await relay.LogoutAsync(ct);
await admin.LogoutAsync(ct);
Console.WriteLine("PASS: real KV; selected class, permission, safe one-off and weekly, inheritance and preview conflicts, preserved policies, legacy guard and lost reply.");

async Task<string> Login(string username)
{
    using var response = await http.PostAsJsonAsync("/accounts/local/login", new { fixture.SchoolCode, username, fixture.Password }, ct);
    response.EnsureSuccessStatusCode();
    var envelope = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
    return envelope.GetProperty("data").GetProperty("access_token").GetString()!;
}

async Task<JsonElement> Raw(string suffix, string bearer, Func<Guid, object>? body = null, int status = 200)
{
    using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post,
        $"/api/v2/npep/schools/{fixture.SchoolId}/study-settings{suffix}");
    var requestId = Guid.NewGuid();
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
    request.Headers.Add("X-NPEP-Version", "0.10");
    request.Headers.Add("X-Request-Id", requestId.ToString());
    if (body is not null) request.Content = JsonContent.Create(body(requestId));
    using var response = await http.SendAsync(request, ct);
    Check((int)response.StatusCode == status, $"Unexpected route response {(int)response.StatusCode}, expected {status}.");
    var envelope = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
    return envelope.TryGetProperty("data", out var data) ? data : default;
}

static async Task Expect(int status, string? code, Func<Task> action)
{
    try { await action(); }
    catch (ConsoleApiException error) when (error.StatusCode == status && (code is null || error.Code == code)) { return; }
    throw new InvalidDataException($"Expected status {status} {code}.");
}

static void Check(bool success, string details)
{
    if (!success) throw new InvalidDataException(details);
}

internal sealed record Fixture(string Origin, string LostReplyOrigin, string SchoolCode, string Username, string Viewer,
    string Password, string SchoolId, string OtherSchoolId, string TermId, DateOnly StudyDate);
