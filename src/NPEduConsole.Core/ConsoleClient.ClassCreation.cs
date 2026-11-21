using System.Text.RegularExpressions;

namespace NPEduConsole.Core;

public sealed record ClassCreationGrade(string Id, string Code, string Name);

public sealed class ClassCreationContext
{
    internal ClassCreationContext(Guid sessionId, string schoolId, string? termId, string? termName, ClassCreationGrade[] grades)
    {
        SessionId = sessionId;
        SchoolId = schoolId;
        TermId = termId;
        TermName = termName;
        Grades = Array.AsReadOnly(grades);
    }

    internal Guid SessionId { get; }
    public string SchoolId { get; }
    public string? TermId { get; }
    public string? TermName { get; }
    public IReadOnlyList<ClassCreationGrade> Grades { get; }
}

public sealed partial class ConsoleClient
{
    private sealed record CreationTerm(string Id, string SchoolId, string Name, string Status);
    private sealed record CreationGradeRow(string Id, string TermId, string Code, string Name);
    private sealed record CreationDirectory(CreationTerm? Term, CreationGradeRow[] Grades);
    private sealed record CreatedClass(string Id, string SchoolId, string TermId, string GradeId, Guid RequestId);

    private const string CurrentClassesSuffix = "/current-term/administrative-classes";
    private static string CurrentClassesPath(string schoolId) => $"api/v2/admin/schools/{Segment(schoolId)}{CurrentClassesSuffix}";

    public async Task<ClassCreationContext> GetClassCreationContextAsync(string schoolId, CancellationToken ct)
    {
        var session = CurrentSession;
        var directory = await RequestAsync<CreationDirectory>(HttpMethod.Get, CurrentClassesPath(schoolId), session.Token, ct);
        CheckCreationSession(session, ct);
        var term = directory.Term;
        if (term is null)
        {
            if (directory.Grades.Length != 0) throw InvalidResponse();
            return new(session.Id, schoolId, null, null, []);
        }
        if (term.SchoolId != schoolId || term.Status != "ACTIVE" || string.IsNullOrWhiteSpace(term.Id) ||
            string.IsNullOrWhiteSpace(term.Name)) throw InvalidResponse();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var grades = new List<ClassCreationGrade>();
        foreach (var grade in directory.Grades)
        {
            if (grade is null || grade.TermId != term.Id || string.IsNullOrWhiteSpace(grade.Id) || !seen.Add(grade.Id) ||
                string.IsNullOrWhiteSpace(grade.Code) || string.IsNullOrWhiteSpace(grade.Name)) throw InvalidResponse();
            grades.Add(new(grade.Id, grade.Code, grade.Name));
        }
        return new(session.Id, schoolId, term.Id, term.Name, grades.ToArray());
    }

    public async Task<string> CreateClassAsync(ClassCreationContext context, string gradeId, string code, string name,
        bool isStudentSelectable, Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        if (session.Id != context.SessionId) throw new ConsoleApiException(409, "登录账号已切换，请重新载入年级。", "SNAPSHOT_CHANGED");
        if (context.TermId is null || !context.Grades.Any(grade => grade.Id == gradeId))
            throw new ConsoleApiException(422, "请在当前学期选择已有年级。", "GRADE_INVALID");
        code = code.Trim().ToUpperInvariant();
        name = name.Trim();
        if (!Regex.IsMatch(code, @"\A[A-Z0-9][A-Z0-9._-]{1,63}\z") || name.Length is < 1 or > 191 || requestId == Guid.Empty)
            throw new ConsoleApiException(422, "请核对班级名称和代码。代码为 2 至 64 位，以字母或数字开头。", "ADMIN_CLASS_INVALID");
        var data = await RequestAsync<CreatedClass>(HttpMethod.Post, CurrentClassesPath(context.SchoolId), session.Token, ct,
            body: new { context.TermId, gradeId, code, name, isStudentSelectable, requestId });
        CheckCreationSession(session, ct);
        if (data.SchoolId != context.SchoolId || data.TermId != context.TermId || data.GradeId != gradeId ||
            data.RequestId != requestId) throw InvalidResponse();
        _ = Segment(data.Id);
        return data.Id;
    }

    private void CheckCreationSession(Session session, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!ReferenceEquals(session, _session)) throw new ConsoleApiException(401, "登录会话已改变，请重新登录。");
    }

    private static ConsoleApiException ClassCreationError(int status, string? code)
    {
        if (status == 404 && code != "GRADE_INVALID")
            return new(404, "当前 KV 缺少建班接口，请升级 KV 后重试。", "CLASS_CREATION_UNSUPPORTED");
        return new(status, code switch
        {
            "GRADE_INVALID" => "所选年级已失效，请重新载入年级。",
            "CLASS_CREATION_TERM_CHANGED" => "当前学期已变更，请重新载入年级。",
            "WORKSPACE_CODE_EXISTS" => "该学期已有教学空间使用此代码，请换一个班级代码。",
            "CLASS_CREATION_REQUEST_CONFLICT" => "此请求已用于其他建班操作，请核对班级列表。",
            "CLASS_CREATION_TARGET_CHANGED" => "上次请求已建班，该班之后有变动，请刷新班级列表。",
            "ADMIN_CLASS_INVALID" => "请核对班级名称、代码和所属年级。",
            _ => HttpError(status, false).Message
        }, code);
    }
}
