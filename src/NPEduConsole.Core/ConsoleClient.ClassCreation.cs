using System.Globalization;
using System.Text.RegularExpressions;

namespace NPEduConsole.Core;

public sealed record ClassCreationGrade(string Id, string Code, string Name)
{
    public string? UpdatedAt { get; init; }
    public int SortOrder { get; init; }
}

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
    internal string? ScopeVersion { get; init; }
    public bool IsSpecifiedTerm => ScopeVersion is not null;
    public string? TermStatus { get; init; }
    public string? ActiveTermName { get; init; }
}

public sealed partial class ConsoleClient
{
    private sealed record CreationTerm(string Id, string SchoolId, string Name, string Status);
    private sealed record CreationGradeRow(string Id, string TermId, string Code, string Name, string? UpdatedAt = null, int SortOrder = 0);
    private sealed record CreationDirectory(CreationTerm? Term, CreationGradeRow[] Grades);
    private sealed record CreatedClass(string Id, string SchoolId, string TermId, string GradeId, Guid RequestId);
    private sealed record GradeCreationReceipt(string SchoolId, string TermId, Guid RequestId, ClassCreationGrade? Grade);
    private sealed record GradeRenameRow(string Id, string Code, string Name, string? UpdatedAt);
    private sealed record GradeRenameReceipt(string SchoolId, string TermId, GradeRenameRow Grade);

    private const string CurrentClassesSuffix = "/current-term/administrative-classes";
    private const string CurrentGradesSuffix = "/current-term/grades";
    private static string CurrentClassesPath(string schoolId) => $"api/v2/admin/schools/{Segment(schoolId)}{CurrentClassesSuffix}";
    private static string CurrentGradesPath(string schoolId) => $"api/v2/admin/schools/{Segment(schoolId)}{CurrentGradesSuffix}";

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
            grades.Add(new(grade.Id, grade.Code, grade.Name) { UpdatedAt = ValidateUpdatedAt(grade.UpdatedAt) });
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
        if (!ValidCreationCode(code) || name.Length is < 1 or > 191 || requestId == Guid.Empty)
            throw new ConsoleApiException(422, "请核对班级名称和代码。代码为 2 至 64 位，以字母或数字开头。", "ADMIN_CLASS_INVALID");
        var data = await RequestAsync<CreatedClass>(HttpMethod.Post, CreationClassesPath(context), session.Token, ct,
            body: context.IsSpecifiedTerm
                ? new { expectedScope = context.ScopeVersion, gradeId, code, name, isStudentSelectable, requestId }
                : new { context.TermId, gradeId, code, name, isStudentSelectable, requestId });
        CheckCreationSession(session, ct);
        if (data.SchoolId != context.SchoolId || data.TermId != context.TermId || data.GradeId != gradeId ||
            data.RequestId != requestId) throw InvalidResponse();
        _ = Segment(data.Id);
        return data.Id;
    }

    public async Task<ClassCreationGrade> CreateCurrentGradeAsync(ClassCreationContext context, string code, string name,
        Guid requestId, CancellationToken ct) =>
        await RequestGradeReceiptAsync(context, code, name, requestId, ct, create: true) ?? throw InvalidResponse();

    public Task<ClassCreationGrade?> GetCurrentGradeCreationResultAsync(ClassCreationContext context, string code, string name,
        Guid requestId, CancellationToken ct) => RequestGradeReceiptAsync(context, code, name, requestId, ct, create: false);

    public async Task<ClassCreationGrade> RenameCurrentGradeAsync(ClassCreationContext context, string gradeId, string name,
        CancellationToken ct)
    {
        var session = CurrentSession;
        if (session.Id != context.SessionId) throw new ConsoleApiException(409, "登录账号已切换，请重新载入年级。", "SNAPSHOT_CHANGED");
        if (context.TermId is not { } termId)
            throw new ConsoleApiException(422, "当前学校暂无启用学期，无法修改年级名称。", "GRADE_INVALID");
        var current = context.Grades.FirstOrDefault(grade => grade.Id == gradeId);
        if (current is null)
            throw new ConsoleApiException(404, "当前年级已不在本学期，请刷新年级列表。", "GRADE_NOT_FOUND");
        if (current.UpdatedAt is null)
            throw new ConsoleApiException(409, "当前年级缺少版本信息，无法安全修改名称。请刷新年级列表或升级 KV。",
                "GRADE_RENAME_UNAVAILABLE");
        name = name.Trim();
        if (name.Length is < 1 or > 191)
            throw new ConsoleApiException(422, "年级名称需为 1 至 191 个字符。", "GRADE_INVALID");

        var path = $"{CurrentGradesPath(context.SchoolId)}/{Segment(gradeId)}/name";
        var receipt = await RequestAsync<GradeRenameReceipt>(HttpMethod.Patch, path, session.Token, ct,
            body: new { termId, name, expectedUpdatedAt = current.UpdatedAt });
        CheckCreationSession(session, ct);
        if (receipt.SchoolId != context.SchoolId || receipt.TermId != termId || receipt.Grade is not { } grade ||
            grade.Id != gradeId || grade.Code != current.Code || grade.Name != name) throw InvalidResponse();
        _ = Segment(grade.Id);
        return new(grade.Id, grade.Code, grade.Name) { UpdatedAt = ValidateUpdatedAt(grade.UpdatedAt) ?? throw InvalidResponse() };
    }

    private async Task<ClassCreationGrade?> RequestGradeReceiptAsync(ClassCreationContext context, string code, string name,
        Guid requestId, CancellationToken ct, bool create)
    {
        var session = CurrentSession;
        if (session.Id != context.SessionId) throw new ConsoleApiException(409, "登录账号已切换，请重新载入年级。", "SNAPSHOT_CHANGED");
        code = code.Trim().ToUpperInvariant();
        name = name.Trim();
        if (context.TermId is not { } termId || requestId == Guid.Empty || !ValidCreationCode(code) || name.Length is < 1 or > 191)
            throw new ConsoleApiException(422, "请核对当前学期、年级名称和代码。代码为 2 至 64 位，以字母或数字开头。", "GRADE_INVALID");
        var path = context.IsSpecifiedTerm ? TermStructurePath(context.SchoolId, termId) + "/grades" : CurrentGradesPath(context.SchoolId);
        var result = create
            ? await RequestAsync<GradeCreationReceipt>(HttpMethod.Post, path, session.Token, ct, body: context.IsSpecifiedTerm
                ? new { expectedScope = context.ScopeVersion, requestId, code, name }
                : new { termId, requestId, code, name })
            : await RequestAsync<GradeCreationReceipt>(HttpMethod.Get, context.IsSpecifiedTerm
                ? $"{path}/receipts/{requestId:D}" : $"{path}/{requestId:D}?termId={Segment(termId)}", session.Token, ct);
        CheckCreationSession(session, ct);
        if (result.SchoolId != context.SchoolId || result.TermId != termId || result.RequestId != requestId) throw InvalidResponse();
        if (result.Grade is { } grade)
        {
            _ = Segment(grade.Id);
            if (grade.Code != code || grade.Name != name) throw InvalidResponse();
            _ = ValidateUpdatedAt(grade.UpdatedAt);
        }
        return result.Grade;
    }

    private static bool ValidCreationCode(string code) => Regex.IsMatch(code, @"\A[A-Z0-9][A-Z0-9._-]{1,63}\z");

    private static string? ValidateUpdatedAt(string? updatedAt)
    {
        if (updatedAt is null) return null;
        if (!DateTimeOffset.TryParseExact(updatedAt, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out _)) throw InvalidResponse();
        return updatedAt;
    }

    private static bool IsGradeRenamePath(string path) =>
        path.Contains(CurrentGradesSuffix + "/", StringComparison.Ordinal) &&
        path.EndsWith("/name", StringComparison.Ordinal);

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

    private static ConsoleApiException GradeCreationError(int status, string? code)
    {
        if (status is 404 or 426) return new(status, "当前 KV 缺少年级创建或核对接口，请升级 KV 后再试。", "GRADE_CREATION_UNSUPPORTED");
        return new(status, code switch
        {
            "GRADE_CODE_EXISTS" => "该学期已有此年级代码，请换一个年级代码。",
            "GRADE_CREATION_TERM_CHANGED" => "当前学期已变更，请刷新学期与年级。",
            "GRADE_CREATION_TARGET_CHANGED" => "该请求已建立年级，但学期或原记录已变化。请刷新核对。",
            "GRADE_CREATION_REQUEST_CONFLICT" => "此请求无法确认本次创建，请刷新核对年级。",
            "GRADE_INVALID" => "请核对年级名称和代码。",
            _ => HttpError(status, false).Message
        }, code);
    }

    private static ConsoleApiException GradeRenameError(int status, string? code)
    {
        if ((status is 404 or 405 or 426) && code != "GRADE_NOT_FOUND")
            return new(status, "当前 KV 缺少年级改名接口，请升级 KV 后重试。", "GRADE_RENAME_UNSUPPORTED");
        return new(status, code switch
        {
            "ORGANIZATION_VERSION_CONFLICT" => "年级名称已被其他管理员修改，请核对服务器当前名称后再确认。",
            "GRADE_RENAME_TERM_CHANGED" => "当前启用学期已变更，请刷新学期与年级后重新选择。",
            "GRADE_NOT_FOUND" => "该年级已不在当前学期，请刷新年级列表。",
            "GRADE_INVALID" => "请核对年级名称（1 至 191 个字符）。",
            "EXPECTED_UPDATED_AT_INVALID" => "年级版本信息已失效，请刷新年级列表后重新选择。",
            "GRADE_RENAME_UNAVAILABLE" => "当前年级缺少版本信息，请刷新年级列表或升级 KV。",
            _ => status switch
            {
                409 => "年级名称已在服务器变化，请刷新后核对。",
                422 => "KV 未接受该年级名称，请核对后重试。",
                _ => HttpError(status, false).Message
            }
        }, code);
    }
}
