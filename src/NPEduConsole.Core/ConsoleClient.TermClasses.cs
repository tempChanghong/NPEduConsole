namespace NPEduConsole.Core;

public sealed record TermStructureIdentity(string Id, string SchoolId, string Name, string Status)
{
    public string StatusLabel => Status switch { "ACTIVE" => "启用中", "DRAFT" => "草稿", "ARCHIVED" => "已归档", _ => Status };
}

public sealed record TermAdministrativeClass(string Id, string TermId, string? GradeId, string? GradeName,
    string Code, string Name, bool IsActive, bool IsStudentSelectable, string UpdatedAt);

public sealed record TermClassStructure(TermStructureIdentity Term, TermStructureIdentity? ActiveTerm,
    ClassCreationContext Creation, IReadOnlyList<TermAdministrativeClass> Classes);

public sealed partial class ConsoleClient
{
    private const string TermStructureSegment = "/structure";
    private sealed record TermStructureReply(string SchoolId, TermStructureIdentity Term, TermStructureIdentity? ActiveTerm,
        string ScopeVersion, CreationGradeRow[] Grades, TermAdministrativeClass[] Classes);
    private sealed record ScopedGradeReply(string SchoolId, string TermId, CreationGradeRow Grade);
    private sealed record ScopedClassReceipt(string SchoolId, string TermId, string GradeId, Guid RequestId, string? Id);

    private static string TermStructurePath(string schoolId, string termId) =>
        $"api/v2/admin/schools/{Segment(schoolId)}/academic-terms/{Segment(termId)}{TermStructureSegment}";

    public async Task<TermClassStructure> GetTermClassStructureAsync(string schoolId, string termId, CancellationToken ct)
    {
        var session = CurrentSession;
        var data = await RequestAsync<TermStructureReply>(HttpMethod.Get, TermStructurePath(schoolId, termId), session.Token, ct);
        CheckCreationSession(session, ct);
        if (data.SchoolId != schoolId || data.Term.Id != termId || !ValidTermStructureIdentity(data.Term, schoolId) ||
            data.ActiveTerm is { } active && (!ValidTermStructureIdentity(active, schoolId) || active.Status != "ACTIVE") ||
            (data.ActiveTerm?.Id == termId) != (data.Term.Status == "ACTIVE")) throw InvalidResponse();
        var grades = data.Grades.Select(grade => ReadScopedGrade(grade, termId)).ToArray();
        var gradeIds = grades.Select(grade => grade.Id).ToHashSet(StringComparer.Ordinal);
        if (gradeIds.Count != grades.Length || data.Classes.Any(room => room is null || room.TermId != termId ||
            string.IsNullOrWhiteSpace(room.Id) || room.GradeName is not null && !gradeIds.Contains(room.GradeId ?? "") ||
            string.IsNullOrWhiteSpace(room.Name) || !ValidCreationCode(room.Code) || ValidateUpdatedAt(room.UpdatedAt) is null) ||
            data.Classes.Select(room => room.Id).Distinct(StringComparer.Ordinal).Count() != data.Classes.Length)
            throw InvalidResponse();
        var creation = new ClassCreationContext(session.Id, schoolId, termId, data.Term.Name, grades)
        {
            ScopeVersion = ReadStructureVersion(data.ScopeVersion),
            TermStatus = data.Term.StatusLabel,
            ActiveTermName = data.ActiveTerm?.Name,
        };
        return new(data.Term, data.ActiveTerm, creation, Array.AsReadOnly(data.Classes));
    }

    public async Task<ClassCreationGrade> UpdateTermGradeAsync(ClassCreationContext context, string gradeId, string code,
        string name, int sortOrder, CancellationToken ct)
    {
        var session = CurrentSession;
        if (session.Id != context.SessionId || context.ScopeVersion is null || context.TermId is not { } termId)
            throw new ConsoleApiException(409, "当前学校或学期已变化，请重新载入。", "SNAPSHOT_CHANGED");
        var grade = context.Grades.FirstOrDefault(row => row.Id == gradeId);
        if (grade?.UpdatedAt is null) throw new ConsoleApiException(409, "请刷新所选学期的年级。", "GRADE_NOT_FOUND");
        code = code.Trim().ToUpperInvariant();
        name = name.Trim();
        if (!ValidCreationCode(code) || name.Length is < 1 or > 191 || sortOrder is < -10000 or > 10000)
            throw new ConsoleApiException(422, "请核对年级名称、代码和显示顺序。", "GRADE_INVALID");
        var path = $"{TermStructurePath(context.SchoolId, termId)}/grades/{Segment(gradeId)}";
        var result = await RequestAsync<ScopedGradeReply>(HttpMethod.Patch, path, session.Token, ct,
            body: new { code, name, sortOrder, expectedUpdatedAt = grade.UpdatedAt, expectedScope = context.ScopeVersion });
        CheckCreationSession(session, ct);
        if (result.SchoolId != context.SchoolId || result.TermId != termId || result.Grade.Id != gradeId ||
            result.Grade.Code != code || result.Grade.Name != name || result.Grade.SortOrder != sortOrder) throw InvalidResponse();
        return ReadScopedGrade(result.Grade, termId);
    }

    public async Task<string?> GetScopedClassCreationResultAsync(ClassCreationContext context, string gradeId, Guid requestId,
        CancellationToken ct)
    {
        var session = RequireBatchContext(context, gradeId, requestId);
        if (context.ScopeVersion is null) throw new ArgumentException("A specified term is required.", nameof(context));
        var path = $"{CreationClassesPath(context)}/receipts/{requestId:D}?gradeId={Segment(gradeId)}";
        var receipt = await RequestAsync<ScopedClassReceipt>(HttpMethod.Get, path, session.Token, ct);
        CheckCreationSession(session, ct);
        if (receipt.SchoolId != context.SchoolId || receipt.TermId != context.TermId || receipt.GradeId != gradeId ||
            receipt.RequestId != requestId) throw InvalidResponse();
        if (receipt.Id is { } id) _ = Segment(id);
        return receipt.Id;
    }

    private static string CreationClassesPath(ClassCreationContext context) => context.ScopeVersion is not null
        ? TermStructurePath(context.SchoolId, context.TermId!) + "/classes"
        : CurrentClassesPath(context.SchoolId);

    private static bool ValidTermStructureIdentity(TermStructureIdentity identity, string schoolId) =>
        identity.SchoolId == schoolId && !string.IsNullOrWhiteSpace(identity.Id) && !string.IsNullOrWhiteSpace(identity.Name) &&
        identity.Status is "ACTIVE" or "DRAFT" or "ARCHIVED";

    private static string ReadStructureVersion(string? version) =>
        version is { Length: 64 } && version.All(char.IsAsciiHexDigit) ? version : throw InvalidResponse();

    public static bool IsSameTermStructure(ClassCreationContext first, ClassCreationContext second) =>
        first.IsSpecifiedTerm && first.SessionId == second.SessionId && first.SchoolId == second.SchoolId &&
        first.TermId == second.TermId && first.ScopeVersion == second.ScopeVersion;

    private Session RequireTermStructureContext(ClassCreationContext context)
    {
        var session = CurrentSession;
        if (session.Id != context.SessionId || !context.IsSpecifiedTerm || context.TermId is null)
            throw new ConsoleApiException(409, "学校或登录会话已变化，请返回学期管理。", "SNAPSHOT_CHANGED");
        return session;
    }

    private static void CheckTermStructureVersion(string expected, string? actual)
    {
        if (ReadStructureVersion(actual) != expected)
            throw new ConsoleApiException(409, "所选学期或学校启用学期已变化。请返回学期管理重新核对。", "TERM_STRUCTURE_SCOPE_CHANGED");
    }

    private static ClassCreationGrade ReadScopedGrade(CreationGradeRow grade, string termId)
    {
        if (grade is null || grade.TermId != termId || string.IsNullOrWhiteSpace(grade.Id) ||
            !ValidCreationCode(grade.Code) || string.IsNullOrWhiteSpace(grade.Name) ||
            ValidateUpdatedAt(grade.UpdatedAt) is not { } version) throw InvalidResponse();
        return new(grade.Id, grade.Code, grade.Name) { UpdatedAt = version, SortOrder = grade.SortOrder };
    }

    private static async Task<ConsoleApiException> ScopedStructureErrorAsync(HttpResponseMessage response, string path, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var code = await AcademicErrorCodeAsync(response, ct);
        var message = code switch
        {
            "TERM_STRUCTURE_SCOPE_CHANGED" => "所选学期或学校启用学期已变化。请关闭此表单，重新读取学期后再核对。",
            "TERM_NOT_FOUND" => "所选学期已不可用，请返回学期管理刷新。",
            "ORGANIZATION_VERSION_CONFLICT" when path.Contains("/grades", StringComparison.Ordinal) =>
                "另一位管理员已修改年级。请核对服务器当前名称、代码和顺序。",
            "GRADE_CODE_EXISTS" => "所选学期已有此年级代码，请改用其他代码。",
            "GRADE_NOT_FOUND" => "年级已不属于所选学期，请刷新年级。",
            "CLASS_ROSTER_CLASS_CHANGED" => "班级资料已变化，请载入当前名单后重新核对。",
            "CLASS_ROSTER_CLASS_INACTIVE" => "该班已停用，不能保存名单。",
            "CLASS_ROSTER_CONFLICT" => "其他管理员或大屏已修改名单，请核对最新名单。",
            "CLASS_ROSTER_CLASS_INVALID" => "未找到所选学期的这个班级，请返回班级列表。",
            "CLASS_ROSTER_VERSION_REQUIRED" => "请重新载入名单后再保存。",
            "CLASS_ROSTER_STUDENT_INVALID" => "学生身份与所选班级不匹配，请重新核对。",
            _ => null,
        };
        if (message is not null) return new(status, message, code);
        if (path.Contains("/maintenance", StringComparison.Ordinal)) return ClassMaintenanceError(status, code);
        if (path.Contains("/grades", StringComparison.Ordinal)) return GradeCreationError(status, code);
        if (path.Contains("/classes", StringComparison.Ordinal) && !path.EndsWith("/students", StringComparison.Ordinal))
            return ClassCreationError(status, code);
        return status == 404 && code is null
            ? new(status, "当前 KV 缺少指定学期的班级或名单接口，请升级 KV。", "TERM_STRUCTURE_UNSUPPORTED")
            : new(status, HttpError(status, false).Message, code);
    }
}
