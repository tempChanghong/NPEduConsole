using System.Text.Json;
using System.Text.Json.Nodes;

namespace NPEduConsole.Core;

public enum StudyPolicySource { None, Disabled, Grade, Classroom }

internal sealed record StudyScope(Guid SessionId, string SchoolId, string TermId, string ClassId);
internal sealed record StudyPolicyRow(string TargetType, string TargetId, int Revision, JsonElement Policy);

public sealed class ClassStudySettings
{
    internal ClassStudySettings(StudyScope scope, string? gradeId, StudyPolicyRow? local, StudyPolicyRow? grade,
        StudyPolicySource source, JsonElement? settings, StudySchedule daily, StudySchedule exam,
        bool homeworkEnabled, bool noiseEnabled, bool photosEnabled, bool legacyNoiseActive)
    {
        Scope = scope;
        GradeId = gradeId;
        Local = local;
        Grade = grade;
        Source = source;
        EffectiveSettings = settings;
        Daily = daily;
        Exam = exam;
        HomeworkEnabled = homeworkEnabled;
        NoiseEnabled = noiseEnabled;
        PhotosEnabled = photosEnabled;
        LegacyNoiseActive = legacyNoiseActive;
    }

    internal StudyScope Scope { get; }
    internal string? GradeId { get; }
    internal StudyPolicyRow? Local { get; }
    internal StudyPolicyRow? Grade { get; }
    internal JsonElement? EffectiveSettings { get; }
    public StudyPolicySource Source { get; }
    public int Revision => Local?.Revision ?? 0;
    public StudySchedule Daily { get; }
    public StudySchedule Exam { get; }
    public bool HomeworkEnabled { get; }
    public bool NoiseEnabled { get; }
    public bool PhotosEnabled { get; }
    public bool LegacyNoiseActive { get; }
    public bool FreezesGradeInheritance => GradeId is not null &&
        (Local is null || Local.Policy.GetProperty("mode").GetString() == "Inherit");

    internal bool HasSameBasis(ClassStudySettings current) => Scope == current.Scope && GradeId == current.GradeId &&
        SameRow(Local, current.Local) && SameRow(Grade, current.Grade) && LegacyNoiseActive == current.LegacyNoiseActive;

    private static bool SameRow(StudyPolicyRow? before, StudyPolicyRow? after) => before is null ? after is null :
        after is not null && before.Revision == after.Revision && JsonElement.DeepEquals(before.Policy, after.Policy);
}

public sealed class ClassStudyPreview
{
    internal ClassStudyPreview(ClassStudySettings basis, JsonElement policy, string fingerprint, DateOnly date,
        IReadOnlyList<StudyWindow> dailyWindows, IReadOnlyList<StudyWindow> examWindows, int pairedDevices)
    {
        Basis = basis;
        Policy = policy;
        Fingerprint = fingerprint;
        Date = date;
        DailyWindows = dailyWindows;
        ExamWindows = examWindows;
        PairedDevices = pairedDevices;
    }

    internal ClassStudySettings Basis { get; }
    internal JsonElement Policy { get; }
    internal string Fingerprint { get; }
    public DateOnly Date { get; }
    public IReadOnlyList<StudyWindow> DailyWindows { get; }
    public IReadOnlyList<StudyWindow> ExamWindows { get; }
    public int PairedDevices { get; }
}

public sealed partial class ConsoleClient
{
    public async Task<ClassStudySettings> GetClassStudySettingsAsync(string schoolId, string termId, string classId, CancellationToken ct)
    {
        var session = CurrentSession;
        return await ReadClassStudyAsync(session, new(session.Id, schoolId, termId, classId), ct);
    }

    public async Task<ClassStudyPreview> PreviewClassStudyAsync(ClassStudySettings settings, DateOnly date, TimeOnly start,
        TimeOnly end, StudyRepeat repeat, CancellationToken ct)
    {
        var session = StudySession(settings.Scope);
        if (settings.LegacyNoiseActive)
            throw new ConsoleApiException(409, "本班仍使用旧版定时噪音监测。请先在旧学校管理页核对迁移。", "STUDY_LEGACY_NOISE_ACTIVE");
        if (repeat is not (StudyRepeat.Once or StudyRepeat.Weekly)) throw new ArgumentOutOfRangeException(nameof(repeat));
        var schedule = settings.Daily.Add(date, start, end, repeat);
        var policy = WithStudySchedule(settings.EffectiveSettings, schedule);
        var scope = settings.Scope;
        var requestId = Guid.NewGuid();
        var body = new { requestId, termId = scope.TermId, targetType = "CLASS", targetId = scope.ClassId,
            expectedRevision = settings.Revision, policy, previewDate = date };
        var envelope = await SendAsync(HttpMethod.Post, StudyPath(scope) + "/preview", session.Token, ct, "0.10", body, requestId);
        var result = ReadData<StudyResult>(envelope);
        var item = VerifyStudyResult(result, settings, policy, date);
        if (!item.Windows.SequenceEqual(schedule.WindowsOn(date)) || !item.ExamWindows.SequenceEqual(settings.Exam.WindowsOn(date)))
            throw InvalidResponse();

        // The preview fingerprint covers changes after preview. Verify that its input was still current as well.
        await CheckStudyBasisAsync(session, settings, ct);
        return new(settings, policy, result.PreviewFingerprint, date, item.Windows, item.ExamWindows, item.PairedDevices);
    }

    public async Task<int> PublishClassStudyAsync(ClassStudyPreview preview, Guid requestId, CancellationToken ct)
    {
        var settings = preview.Basis;
        var scope = settings.Scope;
        var session = StudySession(scope);
        if (requestId == Guid.Empty) throw new ArgumentException("A publication request ID is required.", nameof(requestId));
        await CheckStudyBasisAsync(session, settings, ct);
        var body = new { requestId, termId = scope.TermId, targetType = "CLASS", targetId = scope.ClassId,
            expectedRevision = settings.Revision, policy = preview.Policy, expectedPreviewFingerprint = preview.Fingerprint };
        try
        {
            var envelope = await SendAsync(HttpMethod.Post, StudyPath(scope), session.Token, ct, "0.10", body, requestId);
            var result = ReadData<StudyResult>(envelope);
            VerifyStudyResult(result, settings, preview.Policy, null);
            if (result.Revision != settings.Revision + 1 || result.PreviewFingerprint != preview.Fingerprint) throw InvalidResponse();
            return result.Revision.Value;
        }
        catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or >= 500)
        {
            throw new ConsoleApiException(error.StatusCode, "发布结果尚未确认。请先刷新班级安排，核对学校当前保存的设置。", "STUDY_PUBLISH_UNCONFIRMED");
        }
    }

    private async Task CheckStudyBasisAsync(Session session, ClassStudySettings settings, CancellationToken ct)
    {
        var current = await ReadClassStudyAsync(session, settings.Scope, ct);
        if (!settings.HasSameBasis(current))
            throw new ConsoleApiException(409, "本班或年级的自习策略已变化。请刷新设置并重新预览。", "STUDY_SETTING_VERSION_CONFLICT");
    }

    private async Task<ClassStudySettings> ReadClassStudyAsync(Session session, StudyScope scope, CancellationToken ct)
    {
        string termQuery = "?termId=" + Segment(scope.TermId);
        var catalog = await RequestAsync<StudyCatalog>(HttpMethod.Get, StudyPath(scope) + termQuery, session.Token, ct, version: "0.10");
        if (catalog.TermId != scope.TermId) throw InvalidResponse();
        var room = catalog.Classes.FirstOrDefault(item => item.Id == scope.ClassId) ??
            throw new ConsoleApiException(409, "选中的班级已变更。请刷新班级列表。", "STUDY_CLASS_CHANGED");
        var local = FindStudyRow(catalog.Settings, "CLASS", room.Id);
        var grade = FindStudyRow(catalog.Settings, "GRADE", room.GradeId);
        try
        {
            string? localMode = StudyPolicyMode(local);
            var chosen = localMode is null or "Inherit" ? grade : local;
            var source = StudyPolicyMode(chosen) switch
            {
                null => StudyPolicySource.None,
                "Disabled" => StudyPolicySource.Disabled,
                "Override" => ReferenceEquals(chosen, local) ? StudyPolicySource.Classroom : StudyPolicySource.Grade,
                _ => throw new JsonException("Invalid inherited study policy.")
            };
            var settings = source is StudyPolicySource.Grade or StudyPolicySource.Classroom ? chosen?.Policy.GetProperty("settings") : null;
            bool legacyNoiseActive = source == StudyPolicySource.None && await HasLegacyNoiseAsync(session, scope, room.GradeId, termQuery, ct);
            var daily = settings is { } policy ? StudySchedule.Read(policy.GetProperty("schedule")) : StudySchedule.Empty;
            var exam = settings is { } value && value.TryGetProperty("examSchedule", out var examSchedule)
                ? StudySchedule.Read(examSchedule) : StudySchedule.Empty;
            bool Enabled(string name) => settings?.GetProperty(name).GetProperty("enabled").GetBoolean() ?? false;
            return new(scope, room.GradeId, local, grade, source, settings, daily, exam,
                Enabled("homework"), Enabled("noise"), Enabled("photos"), legacyNoiseActive);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw InvalidResponse();
        }
    }

    private async Task<bool> HasLegacyNoiseAsync(Session session, StudyScope scope, string? gradeId, string query, CancellationToken ct)
    {
        var catalog = await RequestAsync<LegacyNoiseCatalog>(HttpMethod.Get,
            $"api/v2/npep/schools/{Segment(scope.SchoolId)}/noise-schedules{query}", session.Token, ct, version: "0.7");
        if (catalog.TermId != scope.TermId) throw InvalidResponse();
        var room = FindStudyRow(catalog.Policies, "CLASS", scope.ClassId);
        var chosen = StudyPolicyMode(room) is null or "Inherit" ? FindStudyRow(catalog.Policies, "GRADE", gradeId) : room;
        return StudyPolicyMode(chosen) == "Override" && chosen!.Policy.GetProperty("rules").GetArrayLength() > 0;
    }

    private static StudyPolicyRow? FindStudyRow(StudyPolicyRow[] rows, string type, string? id) =>
        id is null ? null : rows.FirstOrDefault(row => row.TargetType == type && row.TargetId == id);

    private static string? StudyPolicyMode(StudyPolicyRow? row) => row is null ? null : row.Policy.GetProperty("mode").GetString() switch
    {
        "Inherit" => "Inherit",
        "Override" => "Override",
        "Disabled" => "Disabled",
        _ => throw new JsonException("Unknown study policy mode.")
    };

    private Session StudySession(StudyScope scope)
    {
        var session = CurrentSession;
        if (session.Id != scope.SessionId)
            throw new ConsoleApiException(409, "请用当前登录账号重新读取班级设置。", "STUDY_STALE_SESSION");
        return session;
    }

    private static StudyResultItem VerifyStudyResult(StudyResult result, ClassStudySettings settings, JsonElement policy, DateOnly? date)
    {
        if (result.TermId != settings.Scope.TermId || result.TargetType != "CLASS" || result.TargetId != settings.Scope.ClassId ||
            result.BaseRevision != settings.Revision || result.PreviewDate != date || result.TotalClasses != 1 ||
            result.Items is not [{ } item] || item.ClassId != settings.Scope.ClassId || item.PairedDevices < 0 ||
            !JsonElement.DeepEquals(result.Policy, policy) || result.PreviewFingerprint.Length != 64 ||
            !result.PreviewFingerprint.All(Uri.IsHexDigit)) throw InvalidResponse();
        if (!item.Windows.Concat(item.ExamWindows).All(window => window.Start.Kind == DateTimeKind.Unspecified &&
            window.End.Kind == DateTimeKind.Unspecified && window.End > window.Start)) throw InvalidResponse();
        return item;
    }

    private static JsonElement WithStudySchedule(JsonElement? existing, StudySchedule schedule)
    {
        var settings = existing is { } policy ? JsonNode.Parse(policy.GetRawText())!.AsObject() : new JsonObject
        {
            ["presentation"] = JsonSerializer.SerializeToNode(new { noiseDisplay = "Status", temporaryHideSeconds = 180 }, Json),
            ["homework"] = JsonSerializer.SerializeToNode(new { enabled = false, sourceMode = "OwnClass", sourceClassIds = Array.Empty<string>(), rotationSeconds = 60 }, Json),
            ["noise"] = JsonSerializer.SerializeToNode(new { enabled = false, redLineDbfs = (double?)null, mediumLineDbfs = (double?)null,
                disciplineCooldownSeconds = 600, highContinuousSeconds = 3 }, Json),
            ["photos"] = JsonSerializer.SerializeToNode(new { enabled = false, continuousSeconds = 3, cooldownSeconds = 30, retentionDays = 30,
                captureOnExit = true, extendedRetentionAcknowledged = false }, Json)
        };
        settings["schedule"] = JsonSerializer.SerializeToNode(schedule.Wire(), Json);
        return JsonSerializer.SerializeToElement(new { mode = "Override", settings }, Json);
    }

    private static string StudyPath(StudyScope scope) => $"api/v2/npep/schools/{Segment(scope.SchoolId)}/study-settings";

    private sealed record StudyCatalog(string? TermId, StudyClass[] Classes, StudyPolicyRow[] Settings);
    private sealed record LegacyNoiseCatalog(string? TermId, StudyPolicyRow[] Policies);
    private sealed record StudyClass(string Id, string? GradeId);
    private sealed record StudyResult(string TermId, string TargetType, string TargetId, int BaseRevision, int TotalClasses,
        JsonElement Policy, StudyResultItem[] Items, string PreviewFingerprint, DateOnly? PreviewDate, int? Revision = null);
    private sealed record StudyResultItem(string ClassId, int PairedDevices, StudyWindow[] Windows, StudyWindow[] ExamWindows);
}
