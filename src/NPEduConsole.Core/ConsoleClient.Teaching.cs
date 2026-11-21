namespace NPEduConsole.Core;

public sealed record TeachingTeacher(string Id, string Name, string? Username)
{
    public override string ToString() => string.IsNullOrEmpty(Username) || Name == Username ? Name : $"{Name}（{Username}）";
}
public sealed record TeachingAssignment(string Id, string AccountId, string TeacherName, string Position)
{
    public override string ToString() => $"{TeacherName} · {(Position == "PRIMARY" ? "主讲" : "协同")}";
}
public sealed record TeachingClass(string Id, string Name, TeachingAssignment[] Assignments);
public sealed record TeachingSubject(string Id, string Name, TeachingClass[] Classes)
{
    public override string ToString() => Name;
}
public sealed record TeachingClassLabel(string Id, string Name);
public sealed record TeachingCatalog(string SchoolId, string TermId, TeachingClassLabel Classroom, TeachingTeacher[] Teachers, TeachingSubject[] Subjects);
public sealed record TeachingDraft(string TermId, string SubjectId, string Action, string[] ClassIds,
    string? AccountId, string? Position, string? AssignmentId);
public sealed record TeachingRow(string ClassId, string ClassName, string State, string Message, string Version,
    TeachingAssignment[] Assignments, string? AssignmentId = null);
public sealed record TeachingPreview(TeachingRow[] Rows)
{
    public bool CanApply => Rows.All(row => row.State is not ("OTHER_PRIMARY" or "EXISTING_ROLE" or "INVALID"));
    public bool HasChanges => Rows.Any(row => row.State is "ADD" or "CHANGE" or "REMOVE");
}
public sealed record TeachingResult(string State, TeachingRow[] Rows);

public sealed partial class ConsoleClient
{
    private static string TeachingPath(string school, string classroom) =>
        $"{CurrentClassesPath(school)}/{Segment(classroom)}/teaching-assignments";

    private static bool IsCurrentTeachingPath(string path) =>
        path.Contains(CurrentClassesSuffix + "/", StringComparison.Ordinal) &&
        path.Contains("/teaching-assignments", StringComparison.Ordinal);

    private static ConsoleApiException CurrentTeachingError(int status, string? code) => new(status, code switch
    {
        "TEACHING_TERM_CHANGED" => "当前启用学期已切换，请关闭后刷新学校总览。",
        "TEACHING_CLASS_CHANGED" => "原班级已停用或不在当前学期，请刷新学校总览。",
        "TEACHING_SUBJECT_CHANGED" => "目标学科已变化，请关闭后核对当前授课规则。",
        "TEACHING_DRAFT_INVALID" => "请核对科目、教师和班级（每次最多 64 班）。",
        _ when status is 404 or 405 or 426 => "当前 KV 暂不支持 Console 任课办理，可使用原网页学校管理。",
        _ => HttpError(status, false).Message
    }, code);

    public async Task<TeachingCatalog> GetTeachingAsync(string school, string term, string classroom, CancellationToken ct)
    {
        var session = CurrentSession;
        var reply = await RequestAsync<TeachingCatalog>(HttpMethod.Get, TeachingPath(school, classroom) + "?termId=" + Segment(term),
            session.Token, ct);
        CheckCreationSession(session, ct);
        if (reply.SchoolId != school || reply.TermId != term || reply.Classroom?.Id != classroom ||
            reply.Teachers is null || reply.Subjects is null ||
            reply.Subjects.Any(subject => subject?.Classes?.Count(room => room.Id == classroom) != 1) ||
            reply.Subjects.SelectMany(subject => subject.Classes).Any(room => room.Assignments is null)) throw InvalidResponse();
        return reply;
    }

    public async Task<TeachingPreview> PreviewTeachingAsync(string school, string classroom, TeachingDraft draft, CancellationToken ct)
    {
        var session = CurrentSession;
        var reply = await RequestAsync<TeachingPreview>(HttpMethod.Post, TeachingPath(school, classroom) + "/preview", session.Token, ct, body: draft);
        CheckCreationSession(session, ct);
        CheckTeachingRows(draft, reply.Rows, partial: false);
        return reply;
    }

    public async Task<TeachingResult> SaveTeachingAsync(string school, string classroom, TeachingDraft draft,
        TeachingPreview preview, CancellationToken ct)
    {
        var session = CurrentSession;
        object body = new { draft.TermId, draft.SubjectId, draft.Action, draft.ClassIds, draft.AccountId, draft.Position, draft.AssignmentId,
            expected = preview.Rows.Select(row => new { row.ClassId, row.Version }).ToArray() };
        var reply = await RequestAsync<TeachingResult>(HttpMethod.Post, TeachingPath(school, classroom), session.Token, ct, body: body);
        CheckCreationSession(session, ct);
        if (reply.State is not ("APPLIED" or "STALE" or "BLOCKED")) throw InvalidResponse();
        CheckTeachingRows(draft, reply.Rows, partial: reply.State == "STALE");
        return reply;
    }

    private static void CheckTeachingRows(TeachingDraft draft, TeachingRow[]? rows, bool partial)
    {
        if (rows is null || rows.Length == 0 || !partial && rows.Length != draft.ClassIds.Length ||
            rows.Any(row => row is null || !draft.ClassIds.Contains(row.ClassId) || row.Version?.Length != 64 ||
                row.Message is null || row.Assignments is null || row.State is not
                ("ADD" or "UNCHANGED" or "OTHER_PRIMARY" or "EXISTING_ROLE" or "CHANGE" or "REMOVE" or "ALREADY_REMOVED" or "INVALID")) ||
            rows.Select(row => row.ClassId).Distinct().Count() != rows.Length) throw InvalidResponse();
    }
}
