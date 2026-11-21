namespace NPEduConsole.Core;

public sealed record StaffTeacher(string Id, string? Name, string? LocalUsername, string? Email, bool LocalDisabled)
{
    public string DisplayName => Name ?? LocalUsername ?? Email ?? "教师";
    public string AccountLabel => LocalUsername ?? Email ?? $"账号 {Id[^Math.Min(8, Id.Length)..]}";
    public override string ToString() => $"{DisplayName} · {AccountLabel}{(LocalDisabled ? " · 已停用" : "")}";
}

public sealed record StaffRole(string AccountId, string Position);
public sealed record StaffAssignment(string AccountId, string Position, StaffTeacher Teacher)
{
    public StaffRole Role => new(AccountId, Position);
}

// AccountId 默认值不能省：Console 开了 RespectRequiredConstructorParameters，
// 旧 KV 的岗位提醒里可能整条缺 accountId，没有默认值会在反序列化阶段整页失败。
public sealed record StaffDiagnostic(string Code, string Severity, string Message, string? AccountId = null);
public sealed record StaffSnapshot(string SchoolId, string TermId, string Kind, string TargetId, string TargetName, string? GradeId,
    string Version, IReadOnlyList<StaffTeacher> Teachers, IReadOnlyList<StaffAssignment> Assignments, IReadOnlyList<StaffDiagnostic> Diagnostics)
{
    internal Guid SessionId { get; init; }
    public bool IsClass => Kind == "classes";
    public string PrimaryRole => IsClass ? "HEAD_TEACHER" : "PRIMARY";
    public string SecondaryRole => IsClass ? "CO_HEAD_TEACHER" : "DEPUTY";
}
public sealed record StaffPreview(string State, StaffSnapshot Current, IReadOnlyList<StaffAssignment> Proposed, IReadOnlyList<StaffDiagnostic> Diagnostics);
public sealed record StaffSaveResult(string State, StaffSnapshot Current);

public sealed partial class ConsoleClient
{
    private const string StaffSuffix = "/current-term/staff-responsibilities/";
    private static bool IsCurrentStaffPath(string path) => path.StartsWith("api/v2/admin/schools/", StringComparison.Ordinal) &&
        path.Contains(StaffSuffix, StringComparison.Ordinal);
    private static string StaffPath(string schoolId, string kind, string targetId) =>
        $"api/v2/admin/schools/{Segment(schoolId)}{StaffSuffix}{Segment(kind)}/{Segment(targetId)}";

    private static ConsoleApiException CurrentStaffError(int status, string? code) => new(status, code switch
    {
        "STAFF_TERM_CHANGED" => "启用学期已切换，请返回并从当前学期重新选择。",
        "STAFF_TARGET_CHANGED" => "原班级已停用，或原职责对象已不在当前学期。请返回核对。",
        // 会命中的两种情况：新增了停用/外校账号，或把已停用的现任教师改成别的岗位。
        // 后者刷新也没用，只能保持原岗位或卸任，所以提示里要说清可选项，而不是让人去刷新。
        "STAFF_ACCOUNT_NOT_FOUND" => "所选教师已停用或不在本校当前办理范围。停用的现任教师只能保持原岗位或卸任，刷新不会改变这一点。",
        "STAFF_MULTIPLE_PRIMARY" => "最多可保留一位主要负责人，请逐一确定其他负责人转为协同或卸任。",
        "STAFF_DRAFT_INVALID" => "职责草稿无效，请核对教师和岗位。",
        _ when status is 404 or 405 or 426 => "当前 KV 暂不支持 Console 办理教师职责，请在原学校管理网页办理或升级 KV。",
        _ => HttpError(status, false).Message
    }, code);

    public async Task<StaffSnapshot> GetStaffResponsibilitiesAsync(string schoolId, string termId, string kind, string targetId,
        CancellationToken ct)
    {
        var session = CurrentSession;
        var current = await RequestAsync<StaffSnapshot>(HttpMethod.Get,
            StaffPath(schoolId, kind, targetId) + "?termId=" + Segment(termId), session.Token, ct);
        CheckCreationSession(session, ct);
        return CheckStaffSnapshot(current, session.Id, schoolId, termId, kind, targetId);
    }

    public async Task<StaffPreview> PreviewStaffResponsibilitiesAsync(StaffSnapshot baseline, IReadOnlyList<StaffRole> assignments,
        CancellationToken ct)
    {
        var session = CheckStaffDraft(baseline, assignments);
        var result = await RequestAsync<StaffPreview>(HttpMethod.Post, StaffPath(baseline.SchoolId, baseline.Kind, baseline.TargetId) + "/preview",
            session.Token, ct, body: new { baseline.TermId, expectedVersion = baseline.Version, assignments });
        CheckCreationSession(session, ct);
        var current = CheckStaffSnapshot(result.Current, session.Id, baseline.SchoolId, baseline.TermId, baseline.Kind, baseline.TargetId);
        if (result.State is not ("READY" or "STALE") || result.Proposed is null || !ValidStaffDiagnostics(result.Diagnostics) ||
            result.State == "STALE" && result.Proposed.Count != 0 || result.State == "READY" &&
            (current.Version != baseline.Version || !ValidStaffAssignments(current, result.Proposed) || !SameStaffRoles(result.Proposed, assignments)))
            throw InvalidResponse();
        return result with { Current = current };
    }

    public async Task<StaffSaveResult> SaveStaffResponsibilitiesAsync(StaffSnapshot baseline, IReadOnlyList<StaffRole> assignments,
        CancellationToken ct)
    {
        var session = CheckStaffDraft(baseline, assignments);
        var result = await RequestAsync<StaffSaveResult>(HttpMethod.Put, StaffPath(baseline.SchoolId, baseline.Kind, baseline.TargetId),
            session.Token, ct, body: new { baseline.TermId, expectedVersion = baseline.Version, assignments });
        CheckCreationSession(session, ct);
        var current = CheckStaffSnapshot(result.Current, session.Id, baseline.SchoolId, baseline.TermId, baseline.Kind, baseline.TargetId);
        if (result.State is not ("APPLIED" or "STALE") || result.State == "APPLIED" && !SameStaffRoles(current.Assignments, assignments))
            throw InvalidResponse();
        return result with { Current = current };
    }

    public static bool SameStaffRoles(IEnumerable<StaffAssignment> rows, IEnumerable<StaffRole> desired) =>
        rows.Select(row => row.Role).ToHashSet().SetEquals(desired);

    private Session CheckStaffDraft(StaffSnapshot baseline, IReadOnlyList<StaffRole> assignments)
    {
        var session = CurrentSession;
        if (baseline.SessionId != session.Id) throw CurrentStaffError(409, "STAFF_TARGET_CHANGED");
        if (assignments.Count > 200 || assignments.Any(row => string.IsNullOrWhiteSpace(row.AccountId) ||
            row.Position != baseline.PrimaryRole && row.Position != baseline.SecondaryRole) ||
            assignments.Select(row => row.AccountId).Distinct().Count() != assignments.Count) throw CurrentStaffError(422, "STAFF_DRAFT_INVALID");
        if (assignments.Count(row => row.Position == baseline.PrimaryRole) > 1) throw CurrentStaffError(422, "STAFF_MULTIPLE_PRIMARY");
        return session;
    }

    private static StaffSnapshot CheckStaffSnapshot(StaffSnapshot current, Guid sessionId, string schoolId, string termId, string kind, string targetId)
    {
        if (current is null || current.SchoolId != schoolId || current.TermId != termId || current.Kind != kind ||
            current.TargetId != targetId || string.IsNullOrWhiteSpace(current.TargetName) || kind == "grades" && current.GradeId != targetId ||
            current.Version is not { Length: 64 } version || version.Any(character => !char.IsAsciiHexDigitLower(character)) ||
            current.Teachers is null || current.Teachers.Any(teacher => string.IsNullOrWhiteSpace(teacher?.Id)) ||
            current.Teachers.Select(teacher => teacher.Id).Distinct().Count() != current.Teachers.Count ||
            !ValidStaffAssignments(current, current.Assignments) || !ValidStaffDiagnostics(current.Diagnostics)) throw InvalidResponse();
        return current with { SessionId = sessionId };
    }

    private static bool ValidStaffAssignments(StaffSnapshot current, IReadOnlyList<StaffAssignment> rows) =>
        rows is not null && rows.All(row => row is not null && !string.IsNullOrWhiteSpace(row.AccountId) && row.Teacher?.Id == row.AccountId &&
            (row.Position == current.PrimaryRole || row.Position == current.SecondaryRole)) &&
        rows.Select(row => row.AccountId).Distinct().Count() == rows.Count;

    private static bool ValidStaffDiagnostics(IReadOnlyList<StaffDiagnostic> rows) =>
        rows is not null && rows.All(row => row is not null && !string.IsNullOrWhiteSpace(row.Code) && !string.IsNullOrWhiteSpace(row.Message));
}
