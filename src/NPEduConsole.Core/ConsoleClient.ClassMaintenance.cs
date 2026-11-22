namespace NPEduConsole.Core;

public sealed record ClassDeactivationCounts(int WorkspaceMembers, int TeachingAssignments, int PendingInvitations,
    int PublicationHistory, int ActivePublications, int ScreenBindings, int Students, int Leaderships, int SourceLinks);

public sealed class ClassMaintenanceSnapshot
{
    internal ClassMaintenanceSnapshot(Guid sessionId, string schoolId, string id, string termId, string? gradeName,
        string code, string name, bool isStudentSelectable, bool isActive, string updatedAt)
    {
        SessionId = sessionId;
        SchoolId = schoolId;
        Id = id;
        TermId = termId;
        GradeName = gradeName;
        Code = code;
        Name = name;
        IsStudentSelectable = isStudentSelectable;
        IsActive = isActive;
        UpdatedAt = updatedAt;
    }

    internal Guid SessionId { get; }
    internal string? ScopeVersion { get; init; }
    public string SchoolId { get; }
    public string Id { get; }
    public string TermId { get; }
    public string? GradeName { get; }
    public string Code { get; }
    public string Name { get; }
    public bool IsStudentSelectable { get; }
    public bool IsActive { get; }
    public string UpdatedAt { get; }
}

public sealed record ClassDeactivationPreview
{
    internal ClassDeactivationPreview(ClassMaintenanceSnapshot classroom, ClassDeactivationCounts counts, string token)
    {
        Classroom = classroom;
        Counts = counts;
        Token = token;
    }

    public ClassMaintenanceSnapshot Classroom { get; }
    public ClassDeactivationCounts Counts { get; }
    internal string Token { get; }
}

public sealed partial class ConsoleClient
{
    private const string MaintenanceSuffix = CurrentClassesSuffix + "/maintenance";
    private sealed record MaintenanceTerm(string Id, string Name);
    private sealed record MaintenanceClassRow(string Id, string TermId, string? GradeName, string Code, string Name,
        bool? IsStudentSelectable, bool? IsActive, string? UpdatedAt);
    private sealed record InactiveClassDirectory(string SchoolId, MaintenanceTerm? Term, MaintenanceClassRow[] Classes);
    private sealed record ImpactRow(int? WorkspaceMembers, int? TeachingAssignments, int? PendingInvitations,
        int? PublicationHistory, int? ActivePublications, int? ScreenBindings, int? Students, int? Leaderships, int? SourceLinks);
    private sealed record MaintenanceReply(string SchoolId, MaintenanceClassRow AdministrativeClass,
        ImpactRow? Counts = null, string? ImpactToken = null, string? ScopeVersion = null);

    private static string ClassMaintenancePath(string schoolId) => CurrentClassesPath(schoolId) + "/maintenance";
    private static bool IsClassMaintenancePath(string path) => path.Contains(MaintenanceSuffix, StringComparison.Ordinal);

    public async Task<IReadOnlyList<ClassMaintenanceSnapshot>> GetInactiveCurrentClassesAsync(string schoolId, CancellationToken ct)
    {
        var session = CurrentSession;
        var result = await RequestAsync<InactiveClassDirectory>(HttpMethod.Get, ClassMaintenancePath(schoolId), session.Token, ct);
        CheckCreationSession(session, ct);
        if (result.SchoolId != schoolId || result.Classes is null) throw InvalidResponse();
        if (result.Term is null) return result.Classes.Length == 0 ? [] : throw InvalidResponse();
        _ = Segment(result.Term.Id);
        if (string.IsNullOrWhiteSpace(result.Term.Name)) throw InvalidResponse();
        var classes = result.Classes.Select(row => ToClassSnapshot(row, session, schoolId, result.Term.Id)).ToArray();
        if (classes.Any(row => row.IsActive) || classes.Select(row => row.Id).Distinct().Count() != classes.Length)
            throw InvalidResponse();
        return classes;
    }

    public async Task<ClassMaintenanceSnapshot> GetCurrentClassMaintenanceAsync(string schoolId, string termId, string classId,
        CancellationToken ct)
    {
        var session = CurrentSession;
        var path = $"{ClassMaintenancePath(schoolId)}/{Segment(classId)}?termId={Segment(termId)}";
        var result = await RequestAsync<MaintenanceReply>(HttpMethod.Get, path, session.Token, ct);
        return CheckClassReply(result, session, schoolId, termId, classId, ct);
    }

    public async Task<ClassMaintenanceSnapshot> GetTermClassMaintenanceAsync(ClassCreationContext context, string classId,
        CancellationToken ct)
    {
        var session = RequireTermStructureContext(context);
        var result = await RequestAsync<MaintenanceReply>(HttpMethod.Get, ScopedMaintenancePath(context.SchoolId, context.TermId!, classId),
            session.Token, ct);
        return CheckClassReply(result, session, context.SchoolId, context.TermId!, classId, ct, context.ScopeVersion);
    }

    public async Task<ClassDeactivationPreview> PreviewClassDeactivationAsync(ClassMaintenanceSnapshot baseline, CancellationToken ct)
    {
        var session = GetMaintenanceSession(baseline);
        var path = baseline.ScopeVersion is null
            ? $"{ClassMaintenancePath(baseline.SchoolId)}/{Segment(baseline.Id)}/deactivation-impact?termId={Segment(baseline.TermId)}"
            : ScopedMaintenancePath(baseline.SchoolId, baseline.TermId, baseline.Id) + "/deactivation-impact";
        var result = await RequestAsync<MaintenanceReply>(HttpMethod.Get, path, session.Token, ct);
        var room = CheckClassReply(result, session, baseline.SchoolId, baseline.TermId, baseline.Id, ct, baseline.ScopeVersion);
        if (!room.IsActive || result.Counts is not { } c || result.ImpactToken is not { Length: 64 } token ||
            !token.All(char.IsAsciiHexDigit)) throw InvalidResponse();
        int?[] values = [c.WorkspaceMembers, c.TeachingAssignments, c.PendingInvitations, c.PublicationHistory,
            c.ActivePublications, c.ScreenBindings, c.Students, c.Leaderships, c.SourceLinks];
        if (values.Any(value => value is null or < 0) || c.ActivePublications > c.PublicationHistory) throw InvalidResponse();
        var counts = values.Select(value => value.GetValueOrDefault()).ToArray();
        return new(room, new(counts[0], counts[1], counts[2], counts[3], counts[4], counts[5], counts[6], counts[7], counts[8]), token);
    }

    public Task<ClassMaintenanceSnapshot> EditCurrentClassAsync(ClassMaintenanceSnapshot baseline, string name, string code,
        bool isStudentSelectable, CancellationToken ct)
    {
        name = name.Trim();
        code = code.Trim().ToUpperInvariant();
        if (name.Length is < 1 or > 191 || !ValidCreationCode(code))
            throw new ConsoleApiException(422, "请核对班级名称（1–191 字）和代码（2–64 位，以字母或数字开头）。", "ADMIN_CLASS_INVALID");
        return SaveCurrentClassAsync(baseline, "EDIT", ct, name, code, isStudentSelectable);
    }

    public Task<ClassMaintenanceSnapshot> DeactivateCurrentClassAsync(ClassDeactivationPreview preview, CancellationToken ct)
    {
        var baseline = preview.Classroom;
        return SaveCurrentClassAsync(baseline, "DEACTIVATE", ct, impactToken: preview.Token);
    }

    public Task<ClassMaintenanceSnapshot> ReactivateCurrentClassAsync(ClassMaintenanceSnapshot baseline, CancellationToken ct) =>
        SaveCurrentClassAsync(baseline, "REACTIVATE", ct);

    private async Task<ClassMaintenanceSnapshot> SaveCurrentClassAsync(ClassMaintenanceSnapshot baseline, string action,
        CancellationToken ct, string? name = null, string? code = null, bool? isStudentSelectable = null, string? impactToken = null)
    {
        var session = GetMaintenanceSession(baseline);
        var path = baseline.ScopeVersion is null ? $"{ClassMaintenancePath(baseline.SchoolId)}/{Segment(baseline.Id)}"
            : ScopedMaintenancePath(baseline.SchoolId, baseline.TermId, baseline.Id);
        var body = new Dictionary<string, object> { ["termId"] = baseline.TermId, ["action"] = action, ["expectedUpdatedAt"] = baseline.UpdatedAt };
        if (baseline.ScopeVersion is { } scope) body["expectedScope"] = scope;
        if (name is not null && code is not null && isStudentSelectable is { } selectable)
        {
            body["name"] = name;
            body["code"] = code;
            body["isStudentSelectable"] = selectable;
        }
        if (impactToken is not null) body["impactToken"] = impactToken;
        var result = await RequestAsync<MaintenanceReply>(HttpMethod.Patch, path,
            session.Token, ct, body: body);
        return CheckClassReply(result, session, baseline.SchoolId, baseline.TermId, baseline.Id, ct, baseline.ScopeVersion);
    }

    private static string ScopedMaintenancePath(string schoolId, string termId, string classId) =>
        $"{TermStructurePath(schoolId, termId)}/classes/{Segment(classId)}/maintenance";

    private Session GetMaintenanceSession(ClassMaintenanceSnapshot baseline)
    {
        var session = CurrentSession;
        if (session.Id != baseline.SessionId)
            throw new ConsoleApiException(409, "登录账号已切换，请重新选择班级。", "SNAPSHOT_CHANGED");
        return session;
    }

    private ClassMaintenanceSnapshot CheckClassReply(MaintenanceReply result, Session session, string schoolId, string termId,
        string classId, CancellationToken ct, string? expectedScope = null)
    {
        CheckCreationSession(session, ct);
        if (result.SchoolId != schoolId || result.AdministrativeClass?.Id != classId) throw InvalidResponse();
        if (expectedScope is not null) CheckTermStructureVersion(expectedScope, result.ScopeVersion);
        return ToClassSnapshot(result.AdministrativeClass, session, schoolId, termId, expectedScope);
    }

    private static ClassMaintenanceSnapshot ToClassSnapshot(MaintenanceClassRow? row, Session session, string schoolId, string termId,
        string? scopeVersion = null)
    {
        if (row is null || row.TermId != termId || string.IsNullOrWhiteSpace(row.Name) || string.IsNullOrWhiteSpace(row.Code) ||
            row.IsActive is not { } active || row.IsStudentSelectable is not { } selectable ||
            ValidateUpdatedAt(row.UpdatedAt) is not { } updatedAt) throw InvalidResponse();
        _ = Segment(row.Id);
        return new(session.Id, schoolId, row.Id, termId, row.GradeName, row.Code, row.Name, selectable, active, updatedAt)
            { ScopeVersion = scopeVersion };
    }

    private static ConsoleApiException ClassMaintenanceError(int status, string? code)
    {
        if ((status is 404 or 405 or 426) && code != "ADMIN_CLASS_NOT_FOUND")
            return new(status, "当前 KV 缺少行政班维护接口，请升级 KV 后再试。", "CLASS_MAINTENANCE_UNSUPPORTED");
        return new(status, code switch
        {
            "WORKSPACE_CODE_EXISTS" => "该学期已有教学空间使用此代码，请换一个班级代码。",
            "ORGANIZATION_VERSION_CONFLICT" => "另一位管理员已修改该班。请核对服务器当前内容，再决定是否保存草稿。",
            "CLASS_MAINTENANCE_TERM_CHANGED" => "当前启用学期已切换。请返回班级列表刷新后重新选择。",
            "CLASS_MAINTENANCE_STATE_CHANGED" => "该班的启用状态已改变，请核对服务器当前状态。",
            "CLASS_DEACTIVATION_IMPACT_CHANGED" => "班级关联已变化。请重新核对停用影响，再确认。",
            "CLASS_DEACTIVATION_PREVIEW_REQUIRED" => "请先读取停用影响并确认。",
            "ADMIN_CLASS_NOT_FOUND" => "该班已不在当前学期，请刷新班级列表。",
            "ADMIN_CLASS_INVALID" => "请核对班级名称、代码和学生自选设置。",
            "EXPECTED_UPDATED_AT_INVALID" => "班级版本信息已变化，请重新读取。",
            _ => HttpError(status, false).Message
        }, code);
    }
}
