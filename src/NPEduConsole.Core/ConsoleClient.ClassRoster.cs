namespace NPEduConsole.Core;

public sealed class ClassRoster
{
    internal ClassRoster(Guid sessionId, string schoolId, string classId, string revision, RosterStudent[] students)
    {
        SessionId = sessionId;
        SchoolId = schoolId;
        ClassId = classId;
        Revision = revision;
        Students = Array.AsReadOnly(students);
    }

    internal Guid SessionId { get; }
    public string SchoolId { get; }
    public string ClassId { get; }
    public string Revision { get; }
    public IReadOnlyList<RosterStudent> Students { get; }
    public bool IsActive { get; init; } = true;
    public string? ClassName { get; init; }
    public string? ClassCode { get; init; }
    internal string? TermId { get; init; }
    internal string? ScopeVersion { get; init; }
    internal string? ClassVersion { get; init; }
}

public sealed partial class ConsoleClient
{
    private sealed record RosterRow(string Id, string Name, string AdministrativeClassId, string? StudentNumber = null);
    private sealed record RosterResponse(RosterRow[] Students, string Revision, string? SchoolId = null, string? TermId = null,
        string? ClassId = null, string? ScopeVersion = null, string? ClassVersion = null, bool? IsActive = null,
        string? ClassName = null, string? ClassCode = null);
    private static string RosterPath(string schoolId, string classId) =>
        $"api/v2/admin/schools/{Segment(schoolId)}/administrative-classes/{Segment(classId)}/students";

    public async Task<ClassRoster> GetClassRosterAsync(string schoolId, string classId, CancellationToken ct)
    {
        var session = CurrentSession;
        var data = await RequestAsync<RosterResponse>(HttpMethod.Get, RosterPath(schoolId, classId), session.Token, ct);
        return ReadRoster(data, session, schoolId, classId, ct);
    }

    public async Task<ClassRoster> GetTermClassRosterAsync(ClassCreationContext context, string classId, CancellationToken ct,
        bool compare = false)
    {
        var session = RequireTermStructureContext(context);
        var path = ScopedRosterPath(context.SchoolId, context.TermId!, classId);
        var data = await RequestAsync<RosterResponse>(HttpMethod.Get, path, session.Token, ct);
        if (!compare) CheckTermStructureVersion(context.ScopeVersion!, data.ScopeVersion);
        return ReadRoster(data, session, context.SchoolId, classId, ct, context.TermId);
    }

    public static bool IsInTermStructure(ClassRoster roster, ClassCreationContext context) =>
        roster.SchoolId == context.SchoolId && roster.TermId == context.TermId && roster.ScopeVersion == context.ScopeVersion;

    public async Task<ClassRoster> SaveClassRosterAsync(ClassRoster baseline, IReadOnlyList<RosterStudent> students, CancellationToken ct)
    {
        var session = CurrentSession;
        if (baseline.SessionId != session.Id) throw new ConsoleApiException(409, "登录账号已切换，请重新载入该班名单。");
        var rows = RosterEditor.Validate(students);
        var knownIds = baseline.Students.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        if (rows.Any(row => row.Id is not null && !knownIds.Contains(row.Id)))
            throw new ArgumentException("名单含有不属于原名单的学生身份，请重新核对。");

        if (!baseline.IsActive) throw new ConsoleApiException(409, "该班已停用，不能保存名单。", "CLASS_ROSTER_CLASS_INACTIVE");
        var path = baseline.TermId is { } termId ? ScopedRosterPath(baseline.SchoolId, termId, baseline.ClassId)
            : RosterPath(baseline.SchoolId, baseline.ClassId);
        var data = await RequestAsync<RosterResponse>(HttpMethod.Put, path, session.Token, ct,
            body: baseline.TermId is not null
                ? new { students = rows, expectedRevision = baseline.Revision, expectedScope = baseline.ScopeVersion, expectedClassVersion = baseline.ClassVersion }
                : new { students = rows, expectedRevision = baseline.Revision, requireActiveTerm = true });
        if (baseline.ScopeVersion is { } scope) CheckTermStructureVersion(scope, data.ScopeVersion);
        var result = ReadRoster(data, session, baseline.SchoolId, baseline.ClassId, ct, baseline.TermId);
        if (!RosterEditor.MatchesSaved(baseline, rows, result)) throw InvalidResponse();
        return result;
    }

    private static string ScopedRosterPath(string schoolId, string termId, string classId) =>
        $"{TermStructurePath(schoolId, termId)}/classes/{Segment(classId)}/students";

    private ClassRoster ReadRoster(RosterResponse data, Session session, string schoolId, string classId, CancellationToken ct,
        string? termId = null)
    {
        ct.ThrowIfCancellationRequested();
        if (!ReferenceEquals(session, _session)) throw new ConsoleApiException(401, "登录会话已改变，请重新登录。");
        if (string.IsNullOrWhiteSpace(data.Revision)) throw InvalidResponse();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var rows = new RosterStudent[data.Students.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            var row = data.Students[i];
            if (row is null || row.AdministrativeClassId != classId || string.IsNullOrWhiteSpace(row.Id) || !ids.Add(row.Id))
                throw InvalidResponse();
            rows[i] = new(row.Id, row.Name, row.StudentNumber);
        }
        if (termId is null) return new(session.Id, schoolId, classId, data.Revision, rows);
        if (data.SchoolId != schoolId || data.TermId != termId || data.ClassId != classId || data.IsActive is not { } active ||
            string.IsNullOrWhiteSpace(data.ClassVersion) || string.IsNullOrWhiteSpace(data.ClassName) ||
            string.IsNullOrWhiteSpace(data.ClassCode)) throw InvalidResponse();
        return new(session.Id, schoolId, classId, data.Revision, rows)
        {
            TermId = termId, ScopeVersion = ReadStructureVersion(data.ScopeVersion), ClassVersion = data.ClassVersion, IsActive = active,
            ClassName = data.ClassName, ClassCode = data.ClassCode,
        };
    }
}
