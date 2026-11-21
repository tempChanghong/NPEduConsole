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
}

public sealed partial class ConsoleClient
{
    private sealed record RosterRow(string Id, string Name, string AdministrativeClassId, string? StudentNumber = null);
    private sealed record RosterResponse(RosterRow[] Students, string Revision);
    private static string RosterPath(string schoolId, string classId) =>
        $"api/v2/admin/schools/{Segment(schoolId)}/administrative-classes/{Segment(classId)}/students";

    public async Task<ClassRoster> GetClassRosterAsync(string schoolId, string classId, CancellationToken ct)
    {
        var session = CurrentSession;
        var data = await RequestAsync<RosterResponse>(HttpMethod.Get, RosterPath(schoolId, classId), session.Token, ct);
        return ReadRoster(data, session, schoolId, classId, ct);
    }

    public async Task<ClassRoster> SaveClassRosterAsync(ClassRoster baseline, IReadOnlyList<RosterStudent> students, CancellationToken ct)
    {
        var session = CurrentSession;
        if (baseline.SessionId != session.Id) throw new ConsoleApiException(409, "登录账号已切换，请重新载入该班名单。");
        var rows = RosterEditor.Validate(students);
        var knownIds = baseline.Students.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        if (rows.Any(row => row.Id is not null && !knownIds.Contains(row.Id)))
            throw new ArgumentException("名单含有不属于原名单的学生身份，请重新核对。");

        var data = await RequestAsync<RosterResponse>(HttpMethod.Put, RosterPath(baseline.SchoolId, baseline.ClassId),
            session.Token, ct, body: new { students = rows, expectedRevision = baseline.Revision, requireActiveTerm = true });
        var result = ReadRoster(data, session, baseline.SchoolId, baseline.ClassId, ct);
        if (!RosterEditor.MatchesSaved(baseline, rows, result)) throw InvalidResponse();
        return result;
    }

    private ClassRoster ReadRoster(RosterResponse data, Session session, string schoolId, string classId, CancellationToken ct)
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
        return new(session.Id, schoolId, classId, data.Revision, rows);
    }
}
