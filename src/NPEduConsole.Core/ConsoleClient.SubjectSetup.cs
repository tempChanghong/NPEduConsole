namespace NPEduConsole.Core;

public sealed record SubjectSetupClass(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record SubjectSetupSubject(string Id, string Name, string Code)
{
    public override string ToString() => $"{Name}（{Code}）";
}

public sealed record SubjectSetupCatalog(string SchoolId, string TermId, SubjectSetupClass[] Classes, SubjectSetupSubject[] Subjects,
    string? AdminUrl = null);
public sealed record SubjectSetupTarget(string ClassId, string SubjectId);
public sealed record SubjectSetupRow(string ClassId, string ClassName, string SubjectId, string SubjectName, string State,
    string Message, string Version)
{
    public SubjectSetupTarget Target => new(ClassId, SubjectId);
    public bool CanApply => State is "ADD" or "PRESENT";
}
public sealed record SubjectSetupPreview(SubjectSetupRow[] Rows);
public sealed record SubjectSetupResult(string State, SubjectSetupRow[] Rows);

public sealed partial class ConsoleClient
{
    private static string SubjectSetupPath(string schoolId, string classId) =>
        $"{CurrentClassesPath(schoolId)}/{Segment(classId)}/subject-setup";

    private static bool IsSubjectSetupPath(string path) => path.Contains(CurrentClassesSuffix + "/", StringComparison.Ordinal) &&
        path.Contains("/subject-setup", StringComparison.Ordinal);

    private static ConsoleApiException SubjectSetupError(int status, string? code) => new(status, code switch
    {
        "SUBJECT_SETUP_TERM_CHANGED" => "当前启用学期已切换，请返回后重新选择班级。",
        "SUBJECT_SETUP_CLASS_CHANGED" => "入口班级已停用或不在当前学期，请刷新班级列表。",
        "SUBJECT_SETUP_DRAFT_INVALID" => "请核对所选班级和学科。每次最多配置 4000 个班级与学科组合。",
        _ when status is 404 or 405 or 426 => "当前 KV 暂不支持批量配置授课科目。可在网页「组织与班级」办理。",
        _ => HttpError(status, false).Message
    }, code);

    public async Task<SubjectSetupCatalog> GetSubjectSetupAsync(string schoolId, string termId, string classId, CancellationToken ct)
    {
        var session = CurrentSession;
        var result = await RequestAsync<SubjectSetupCatalog>(HttpMethod.Get,
            SubjectSetupPath(schoolId, classId) + "?termId=" + Segment(termId), session.Token, ct);
        CheckCreationSession(session, ct);
        if (result.SchoolId != schoolId || result.TermId != termId || result.Classes is null || result.Subjects is null ||
            result.Classes.Any(room => room is null || string.IsNullOrWhiteSpace(room.Id) || string.IsNullOrWhiteSpace(room.Name)) ||
            result.Classes.Count(room => room.Id == classId) != 1 ||
            result.Classes.Select(room => room.Id).Distinct().Count() != result.Classes.Length ||
            result.Subjects.Any(subject => subject is null || string.IsNullOrWhiteSpace(subject.Id) ||
                string.IsNullOrWhiteSpace(subject.Name) || string.IsNullOrWhiteSpace(subject.Code)) ||
            result.Subjects.Select(subject => subject.Id).Distinct().Count() != result.Subjects.Length) throw InvalidResponse();
        return result;
    }

    public async Task<SubjectSetupPreview> PreviewSubjectSetupAsync(string schoolId, string termId, string classId,
        IReadOnlyList<SubjectSetupTarget> targets, CancellationToken ct)
    {
        CheckSetupCount(targets.Count);
        var session = CurrentSession;
        var result = await RequestAsync<SubjectSetupPreview>(HttpMethod.Post,
            SubjectSetupPath(schoolId, classId) + "/preview", session.Token, ct, body: new { termId, targets });
        CheckCreationSession(session, ct);
        CheckSetupRows(targets, result.Rows, "PREVIEW");
        return result;
    }

    public async Task<SubjectSetupResult> SaveSubjectSetupAsync(string schoolId, string termId, string classId,
        IReadOnlyList<SubjectSetupRow> rows, CancellationToken ct)
    {
        CheckSetupCount(rows.Count);
        var session = CurrentSession;
        var targets = rows.Select(row => row.Target).ToArray();
        var expected = rows.Select(row => new { row.ClassId, row.SubjectId, row.Version }).ToArray();
        var result = await RequestAsync<SubjectSetupResult>(HttpMethod.Post, SubjectSetupPath(schoolId, classId),
            session.Token, ct, body: new { termId, targets, expected });
        CheckCreationSession(session, ct);
        if (result.State is not ("APPLIED" or "STALE" or "BLOCKED")) throw InvalidResponse();
        CheckSetupRows(targets, result.Rows, result.State);
        return result;
    }

    private static void CheckSetupCount(int count)
    {
        if (count is < 1 or > 4000) throw SubjectSetupError(422, "SUBJECT_SETUP_DRAFT_INVALID");
    }

    private static void CheckSetupRows(IReadOnlyList<SubjectSetupTarget> targets, SubjectSetupRow[]? rows, string phase)
    {
        var selected = targets.ToHashSet();
        if (rows is null || rows.Length == 0 || phase != "STALE" && rows.Length != selected.Count ||
            rows.Any(row => row is null || !selected.Contains(row.Target) || row.Version.Length != 64 ||
                string.IsNullOrWhiteSpace(row.Message) || (phase == "APPLIED" ? row.State is not ("ADDED" or "PRESENT")
                    : row.State is not ("ADD" or "PRESENT" or "WALKING_CONFLICT" or "INVALID"))) ||
            rows.Select(row => row.Target).Distinct().Count() != rows.Length) throw InvalidResponse();
    }
}
