namespace NPEduConsole.Core;

public sealed record AllocationAccount(string Id, string Name, string? Username, string? Email, bool Disabled, bool Manageable)
{
    public override string ToString() => $"{Name}（{Username ?? Email ?? Id}）{(Disabled ? " · 已停用" : "")}";
}
public sealed record AllocationSource(string AdministrativeClassId);
public sealed record AllocationWorkspace(string Id, string TermId, string Code, string Name, string? GradeId, string Type,
    string? SubjectId, string? SubjectName, AllocationSource[] SourceClasses)
{
    public bool IsGroup => Type == "COURSE_GROUP";
    public override string ToString() => $"{Name}（{Code}）{(IsGroup ? " · 走班" : "")}";
}
public sealed record AllocationMember(string WorkspaceId, string AccountId, string Role);
public sealed record AllocationInvitation(string Id, string WorkspaceId, string NormalizedEmail, string Role,
    DateTimeOffset? ClaimedAt, string? ClaimedByAccountId, DateTimeOffset? UpdatedAt = null);
public sealed record AllocationTeachingSubject(string Id, string Name);
public sealed record AllocationTeaching(string WorkspaceId, string AccountId, string Position, AllocationTeachingSubject Subject);
public sealed record AllocationDuty(string AccountId, string Position, string Type, string TargetId, string TargetName)
{
    public string Title => TargetName + (Type == "GRADE" ? Position == "DEPUTY" ? "副组长" : "年级组长" :
        Position == "CO_HEAD_TEACHER" ? "协同班主任" : "班主任");
}
public sealed record TeacherAllocationCatalog(string SchoolId, string Mode, OrganizationTerm[] Terms, string? TermId,
    OrganizationGradeOption[] Grades, string? GradeId, AllocationAccount[] Accounts, AllocationWorkspace[] Workspaces,
    AllocationMember[] Members, AllocationInvitation[] Invitations, AllocationTeaching[] Teaching, AllocationDuty[] Duties,
    bool CanRevokeArchivedInvitations = false);
public sealed record AllocationAccess(string? WorkspaceId, string? WorkspaceCode, string Operation, string? Role,
    bool ConfirmReduction = false, string? InvitationId = null);
public sealed record AllocationTeacher(string? Username, string? Email, string? AccountId, string? Name, string? Pin,
    bool ConfirmSameName, AllocationAccess[] Access);
public sealed record TeacherAllocationDraft(string Mode, string? TermId, string? TermStatus, string? GradeId,
    AllocationTeacher[] Teachers, bool ArchivedCorrection = false)
{
    public TeacherAllocationDraft WithoutPins() => this with { Teachers = Teachers.Select(row => row with { Pin = null }).ToArray() };
}
public sealed record AllocationDifference(string Key, string Path, string Action, string Teacher, string? WorkspaceId,
    string? Workspace, string? Current, string? After, string? Message, bool NeedsReview = false)
{
    public string ActionLabel => NeedsReview ? "需复核" : Action switch
    { "ADD" => "新增", "CHANGE" => "修改", "REMOVE" => "移除", "BLOCK" => "阻断", _ => "一致" };
    public string Scope => Workspace ?? "学校账号";
    public string BeforeText => Current ?? "—";
    public string AfterText => After ?? "—";
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
}
public sealed record TeacherAllocationPreview(string Mode, string? TermId, string? TermStatus, string? GradeId,
    bool CanApply, string Version, OrganizationEvidence[] Evidence, AllocationDifference[] Changes);
public sealed record AllocationSavedAccess(string WorkspaceId, string? Role, string State, string? InvitationId);
public sealed record AllocationSavedTeacher(string? AccountId, string? Username, string? Email, string Name, bool Created,
    AllocationSavedAccess[] Access);
public sealed record TeacherAllocationReceipt(Guid RequestId, string? TermId, string? TermStatus, string? GradeId, AllocationSavedTeacher[] Teachers);
public sealed record TeacherAllocationResult(string State, TeacherAllocationReceipt? Receipt = null, TeacherAllocationPreview? Preview = null,
    string[]? ChangedKeys = null);
