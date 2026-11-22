using System.Text.Json;
using System.Text.Json.Nodes;

namespace NPEduConsole.Core;

public sealed record StudyDisplayHomework(bool Enabled, string SourceMode, string[] SourceClassIds, int RotationSeconds);
public sealed record StudyDisplayStyle(string NoiseDisplay);
public sealed record StudyDisplaySource(string Id, string Name, string? Code, string Type, string? SubjectName, string[] SourceClassIds);
public sealed record StudyDisplayItemSource(string Id, string Name);
public sealed record StudyDisplayItem(string Id, int Revision, string Subject, string Title, string Content, StudyDisplayItemSource[] Sources);
public sealed record StudyDisplaySnapshot(string SchoolId, string TermId, string ClassId, DateOnly Date, string Source, string State,
    StudyDisplayStyle? Presentation, StudyDisplayHomework? Homework, StudyDisplaySource[] Sources, StudyDisplayItem[] Items, string? Reason = null);

public sealed record StudyDisplayOptions(string NoiseDisplay, bool HomeworkEnabled, string SourceMode, string[] SourceClassIds, int RotationSeconds)
{
    public static StudyDisplayOptions Read(SchoolStudyPolicy? policy)
    {
        if (policy?.Settings is not { } settings) return new("StatusTrend", true, "OwnClass", [], 60);
        var homework = settings.GetProperty("homework");
        return new(settings.GetProperty("presentation").GetProperty("noiseDisplay").GetString()!, homework.GetProperty("enabled").GetBoolean(),
            homework.GetProperty("sourceMode").GetString()!, homework.GetProperty("sourceClassIds").EnumerateArray().Select(row => row.GetString()!).ToArray(),
            homework.GetProperty("rotationSeconds").GetInt32());
    }

    public bool SameSources(StudyDisplayOptions other) =>
        SourceMode == other.SourceMode && SourceClassIds.ToHashSet(StringComparer.Ordinal).SetEquals(other.SourceClassIds);

    public bool SameSettings(StudyDisplayOptions other) => NoiseDisplay == other.NoiseDisplay &&
        HomeworkEnabled == other.HomeworkEnabled && RotationSeconds == other.RotationSeconds && SameSources(other);
}

public sealed partial class ConsoleClient
{
    public async Task<StudyDisplaySnapshot> ReadSchoolStudyDisplayAsync(StudyEvidenceScope scope, DateOnly date, CancellationToken ct)
    {
        var session = CurrentSession;
        var result = await RequestAsync<StudyDisplaySnapshot>(HttpMethod.Get, StudyAdminPath(scope.SchoolId) + "/display?termId=" +
            Segment(scope.TermId) + "&classId=" + Segment(scope.ClassId) + "&date=" + StudyDate(date), session.Token, ct, "0.10");
        CheckCreationSession(session, ct);
        VerifyStudyDisplay(result, scope, date);
        return result;
    }

    public static SchoolStudyEdit BuildStudyDisplayEdit(string school, SchoolStudyCatalog catalog, string type, string id,
        StudyDisplayOptions options, bool makeIndependent)
    {
        if (catalog.TermId is not { } term || type is not ("GRADE" or "CLASS") ||
            catalog.BasePolicy(type, id) is not { Settings: { } basis } original ||
            makeIndependent != (type == "CLASS" && original.TargetType == "GRADE"))
            throw new ArgumentException("请先核对正在使用的学期和自习策略来源。");
        if (options.NoiseDisplay is not ("Status" or "StatusTrend" or "StatusDbfs") ||
            options.SourceMode is not ("OwnClass" or "SelectedClasses") || options.RotationSeconds is < 15 or > 600 ||
            options.SourceClassIds.Length > 100 || options.SourceMode == "OwnClass" && options.SourceClassIds.Length > 0 ||
            options.SourceMode == "SelectedClasses" && options.HomeworkEnabled && options.SourceClassIds.Length == 0)
            throw new ArgumentException("请核对作业来源和轮播间隔（每页 15 至 600 秒）。");
        if (options.SourceClassIds.Distinct().Count() != options.SourceClassIds.Length ||
            options.SourceClassIds.Any(source => catalog.Classes.All(row => row.Id != source)))
            throw new ArgumentException("所选来源班已经不可用。请在来源列表重新核对。");
        var fields = JsonNode.Parse(basis.GetRawText())!.AsObject();
        fields["presentation"]!["noiseDisplay"] = options.NoiseDisplay;
        fields["homework"] = JsonSerializer.SerializeToNode(new StudyDisplayHomework(options.HomeworkEnabled, options.SourceMode,
            options.SourceClassIds.Order(StringComparer.Ordinal).ToArray(), options.RotationSeconds), Json);
        return new(school, term, type, id, catalog.Policy(type, id)?.Revision ?? 0,
            JsonSerializer.SerializeToElement(new {mode = "Override", settings = fields}, Json), true, makeIndependent ? original : null);
    }

    private static void VerifyStudyDisplay(StudyDisplaySnapshot? result, StudyEvidenceScope scope, DateOnly date)
    {
        if (result is null || result.SchoolId != scope.SchoolId || result.TermId != scope.TermId || result.ClassId != scope.ClassId ||
            result.Date != date || result.Source is not ("None" or "Class" or "Grade" or "Disabled") ||
            result.State is not ("NoPolicy" or "Off" or "Available" or "Empty" or "Unavailable") ||
            result.Items is null || result.Sources is null ||
            (result.State == "Available") != (result.Items.Length > 0) || result.Items.Length > 500 ||
            result.Sources.Any(source => source is null || source.SourceClassIds is null || string.IsNullOrWhiteSpace(source.Id)) ||
            result.Items.Any(item => item is null || item.Sources is null || string.IsNullOrWhiteSpace(item.Id)) ||
            result.Items.Select(item => item.Id).Distinct().Count() != result.Items.Length)
            throw InvalidResponse();
    }
}
