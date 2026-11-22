using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NPEduConsole.Core;

public sealed partial class ConsoleClient
{
    private static string StudyAdminPath(string school) => $"api/v2/npep/schools/{Segment(school)}/study-settings";
    private static string SeatingAdminPath(string school) => $"api/v2/npep/schools/{Segment(school)}/seating";
    private static string SeatingQuery(StudyEvidenceScope scope) => $"?termId={Segment(scope.TermId)}&classId={Segment(scope.ClassId)}";
    private static bool IsSchoolStudyAdminPath(string path) => path.StartsWith("api/v2/npep/schools/", StringComparison.Ordinal) &&
        (path.Contains("/seating", StringComparison.Ordinal) || path.Contains("/study-settings", StringComparison.Ordinal) &&
            !path.Contains("/devices/", StringComparison.Ordinal));

    public async Task<SchoolStudyCatalog> ReadSchoolStudyAsync(string school, CancellationToken ct)
    {
        var session = CurrentSession;
        var result = await RequestAsync<SchoolStudyCatalog>(HttpMethod.Get, StudyAdminPath(school), session.Token, ct, "0.10");
        CheckCreationSession(session, ct);
        if (result.TermId is { } term && !result.Terms.Any(row => row.Id == term) ||
            result.Settings.Any(row => row.Revision <= 0 || row.Mode is not ("Override" or "Disabled" or "Inherit")))
            throw InvalidResponse();
        return result;
    }

    public async Task<SchoolStudyCalendar> ReadSchoolStudyCalendarAsync(string school, string term, string grade, DateOnly from,
        int days, CancellationToken ct)
    {
        var session = CurrentSession;
        string path = StudyAdminPath(school) + $"/calendar?termId={Segment(term)}&gradeId={Segment(grade)}&from={StudyDate(from)}&days={days}";
        var result = await RequestAsync<SchoolStudyCalendar>(HttpMethod.Get, path, session.Token, ct, "0.10");
        CheckCreationSession(session, ct);
        if (result.TermId != term || result.GradeId != grade || result.From != from || result.Days != days ||
            result.Classes.Any(row => row.Dates.Length != days || row.Dates.Where((d, index) => d.Date != from.AddDays(index)).Any()))
            throw InvalidResponse();
        return result;
    }

    public static StudySchedule SchoolSchedule(SchoolStudyPolicy? policy, bool exam)
    {
        string key = exam ? "examSchedule" : "schedule";
        return policy?.Settings is { } data && data.TryGetProperty(key, out var schedule) ? StudySchedule.Read(schedule) : StudySchedule.Empty;
    }

    public static SchoolStudyEdit BuildSchoolStudyEdit(string school, SchoolStudyCatalog catalog, string type, string id,
        string mode, StudySchedule? daily, StudySchedule? exam)
    {
        if (catalog.TermId is not { } term || type is not ("CLASS" or "GRADE") || mode is not ("Override" or "Disabled" or "Inherit") ||
            type == "GRADE" && mode == "Inherit")
            throw new ArgumentException("请重新选择当前学期的编辑对象。");
        var original = catalog.Policy(type, id);
        JsonElement policy;
        if (mode != "Override")
            policy = JsonSerializer.SerializeToElement(new { mode, settings = (object?)null }, Json);
        else
        {
            var previous = catalog.BasePolicy(type, id)?.Settings;
            var settings = daily is not null || previous is null
                ? WithStudySchedule(previous, daily ?? StudySchedule.Empty).GetProperty("settings")
                : previous.Value;
            var document = JsonNode.Parse(settings.GetRawText())!.AsObject();
            if (exam is not null) document["examSchedule"] = JsonSerializer.SerializeToNode(exam.Wire(), Json);
            policy = JsonSerializer.SerializeToElement(new { mode, settings = document }, Json);
        }
        return new(school, term, type, id, original?.Revision ?? 0, policy);
    }

    public async Task<SchoolStudyReview> PreviewSchoolStudyAsync(SchoolStudyEdit edit, DateOnly date, CancellationToken ct,
        string? displayClassId = null)
    {
        var session = CurrentSession;
        Guid requestId = Guid.NewGuid();
        var body = StudyChangeBody(edit, requestId);
        body["previewDate"] = date;
        if (edit.DisplayOnly && displayClassId is not null) body["displayClassId"] = displayClassId;
        var result = ReadData<SchoolStudyReview>(await SendAsync(HttpMethod.Post, StudyAdminPath(edit.SchoolId) + "/preview",
            session.Token, ct, "0.10", body, requestId));
        CheckCreationSession(session, ct);
        VerifySchoolStudyReview(result, edit, date);
        if (displayClassId is not null)
            VerifyStudyDisplay(result.Display, new(edit.SchoolId, edit.TermId, displayClassId), date);
        return result;
    }

    public async Task<int> SaveSchoolStudyAsync(SchoolStudyEdit edit, SchoolStudyReview preview, Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        var body = StudyChangeBody(edit, requestId);
        body["expectedPreviewFingerprint"] = preview.PreviewFingerprint;
        var result = ReadData<SchoolStudyReview>(await SendAsync(HttpMethod.Post, StudyAdminPath(edit.SchoolId),
            session.Token, ct, "0.10", body, requestId));
        CheckCreationSession(session, ct);
        return VerifySchoolStudyReceipt(result, edit, preview);
    }

    private static Dictionary<string, object> StudyChangeBody(SchoolStudyEdit edit, Guid requestId)
    {
        var body = new Dictionary<string, object>
        {
            ["requestId"] = requestId, ["termId"] = edit.TermId, ["targetType"] = edit.TargetType, ["targetId"] = edit.TargetId,
            ["expectedRevision"] = edit.Revision, ["policy"] = edit.Policy,
        };
        if (edit.DisplayOnly)
            body["displayEdit"] = edit.InheritedGrade is { } grade
                ? new {makeIndependent = true, expectedGradeId = grade.TargetId, expectedGradeRevision = grade.Revision}
                : new {makeIndependent = false};
        return body;
    }

    public async Task<int?> ReadSchoolStudySaveAsync(SchoolStudyEdit edit, SchoolStudyReview preview, Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        try
        {
            var result = await RequestAsync<SchoolStudyReview>(HttpMethod.Get, StudyAdminPath(edit.SchoolId) + $"/requests/{requestId:D}",
                session.Token, ct, "0.10");
            CheckCreationSession(session, ct);
            return VerifySchoolStudyReceipt(result, edit, preview);
        }
        catch (ConsoleApiException error) when (error is {StatusCode: 404, Code: "STUDY_REQUEST_NOT_FOUND"})
        {
            CheckCreationSession(session, ct);
            return null;
        }
    }

    private static int VerifySchoolStudyReceipt(SchoolStudyReview result, SchoolStudyEdit edit, SchoolStudyReview preview)
    {
        VerifySchoolStudyReview(result, edit, null);
        if (result.Revision != edit.Revision + 1 || result.PreviewFingerprint != preview.PreviewFingerprint) throw InvalidResponse();
        return result.Revision.Value;
    }

    private static void VerifySchoolStudyReview(SchoolStudyReview review, SchoolStudyEdit edit, DateOnly? date)
    {
        if (review.TermId != edit.TermId || review.TargetId != edit.TargetId || review.TargetType != edit.TargetType ||
            review.BaseRevision != edit.Revision || review.PreviewDate != date || review.TotalClasses != review.Items.Length ||
            !JsonElement.DeepEquals(review.Policy, edit.Policy) || !StudyHash(review.PreviewFingerprint) ||
            review.Items.Any(row => !StudyHash(row.ReviewVersion)) || edit.TargetType == "CLASS" &&
                (review.Items is not [var row] || row.ClassId != edit.TargetId)) throw InvalidResponse();
    }

    public async Task<SchoolStudySeating> ReadSchoolSeatingAsync(StudyEvidenceScope scope, CancellationToken ct)
    {
        var session = CurrentSession;
        var result = await RequestAsync<SchoolStudySeating>(HttpMethod.Get, SeatingAdminPath(scope.SchoolId) + SeatingQuery(scope),
            session.Token, ct, "0.14");
        CheckCreationSession(session, ct);
        if (result.Versions.Length > 20 || result.Bindings.Select(row => row.BindingId).Distinct().Count() != result.Bindings.Length ||
            !ValidSeatingVersion(result.Current) || result.Versions.Any(row => !ValidSeatingVersion(row))) throw InvalidResponse();
        return result;
    }

    public async Task<byte[]> ReadSchoolSeatingImageAsync(StudyEvidenceScope scope, StudySeatingVersion version, CancellationToken ct)
    {
        var session = CurrentSession;
        var result = await RequestAsync<SeatingImage>(HttpMethod.Get, SeatingAdminPath(scope.SchoolId) + "/image" + SeatingQuery(scope) +
            $"&revision={version.Revision}", session.Token, ct, "0.14");
        CheckCreationSession(session, ct);
        if (result.Revision != version.Revision || result.Hash != version.Hash || result.Image.Length is 0 or > 2097152 ||
            Convert.ToHexStringLower(SHA256.HashData(result.Image)) != version.Hash) throw InvalidResponse();
        return result.Image;
    }

    public async Task<StudySeatingVersion> SaveSchoolSeatingAsync(StudySeatingEdit edit, Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        var body = new Dictionary<string, object>
        {
            ["requestId"] = requestId, ["termId"] = edit.Scope.TermId, ["classId"] = edit.Scope.ClassId,
            ["expectedRevision"] = edit.Revision, ["action"] = edit.Action
        };
        if (edit.Bytes is {Length: > 0 and <= 2097152} bytes && edit.Action == "image") body["image"] = bytes;
        else if (edit.RestoreRevision is > 0 && edit.Action == "restore") body["restoreRevision"] = edit.RestoreRevision.Value;
        else if (edit.Action != "withdraw") throw new ArgumentException("请重新选择要发布的座位图。");
        var version = ReadData<StudySeatingVersion>(await SendAsync(HttpMethod.Post, SeatingAdminPath(edit.Scope.SchoolId),
            session.Token, ct, "0.14", body, requestId));
        CheckCreationSession(session, ct);
        VerifySeatingReceipt(edit, version);
        return version;
    }

    public async Task<StudySeatingVersion?> ReadSchoolSeatingSaveAsync(StudySeatingEdit edit, Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        try
        {
            var version = await RequestAsync<StudySeatingVersion>(HttpMethod.Get, SeatingAdminPath(edit.Scope.SchoolId) +
                $"/requests/{requestId:D}" + SeatingQuery(edit.Scope), session.Token, ct, "0.14");
            CheckCreationSession(session, ct);
            VerifySeatingReceipt(edit, version);
            return version;
        }
        catch (ConsoleApiException error) when (error is {StatusCode: 404, Code: "SEATING_REQUEST_NOT_FOUND"})
        {
            CheckCreationSession(session, ct);
            return null;
        }
    }

    private static void VerifySeatingReceipt(StudySeatingEdit edit, StudySeatingVersion version)
    {
        if (version.Revision != edit.Revision + 1 || !ValidSeatingVersion(version) ||
            (edit.Action == "withdraw") != (version.Hash is null) ||
            edit.Bytes is { } bytes && Convert.ToHexStringLower(SHA256.HashData(bytes)) != version.Hash) throw InvalidResponse();
    }

    private static bool ValidSeatingVersion(StudySeatingVersion version) => version.Revision >= 0 &&
        (version.Hash is null ? version.Width is null && version.Height is null : StudyHash(version.Hash) &&
            version.Width is > 0 and <= 4096 && version.Height is > 0 and <= 4096 && (long)version.Width * version.Height <= 6_000_000);
    private static bool StudyHash(string? value) => value is {Length: 64} && value.All(char.IsAsciiHexDigitLower);
    private static string StudyDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private sealed record SeatingImage(int Revision, string Hash, byte[] Image);
}
