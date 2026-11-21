namespace NPEduConsole.Core;

public sealed record GradePlanLabel(string Id, string Name, string Code)
{
    public override string ToString() => $"{Name}（{Code}）";
}
public sealed record GradePlanTeacher(string Id, string Name, string? Username)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Username) ? Name : $"{Name}（{Username}）";
}
public sealed record GradePlanAssignment(string Id, string SubjectId, string AccountId, string Position, string TeacherName, bool IsActive)
{
    public override string ToString() => $"{TeacherName}（{(Position == "PRIMARY" ? "主讲" : "协同")}）";
}
public sealed record GradePlanRule(string SubjectId, string Mode, bool Compulsory);
public sealed record GradePlanClass(string Id, string Name, string Code, GradePlanRule[] Rules, GradePlanAssignment[] Assignments);
public sealed record GradePlanMember(string AccountId, string Name, string Role);
public sealed record GradePlanInvitation(string Id, string Email, string Role);
public sealed record GradePlanCourse(string Id, string Name, string Code, string SubjectId, bool IsActive, bool IsStudentSelectable,
    string[] SourceClassIds, GradePlanAssignment[] Assignments, int AssignmentHistoryCount, int PublicationCount,
    int ActivePublicationCount, GradePlanMember[] Members, GradePlanInvitation[] Invitations)
{
    public override string ToString() => $"{Name}（{Code}）{(IsActive ? "" : " · 已停用")}";
}
public sealed record GradePlanCatalog(string SchoolId, string? TermId, string? TermName,
    GradePlanLabel[] Grades, GradePlanLabel[] Subjects, GradePlanClass[] Classes, GradePlanCourse[] Groups, GradePlanTeacher[] Teachers);

public sealed record GradePlanRuleEdit(string ClassId, string SubjectId, string Mode, bool Compulsory);
public sealed record GradePlanGroupEdit(string Key, string? GroupId, string Operation, string SubjectId,
    string Name, string Code, bool IsStudentSelectable, string[] SourceClassIds);
public sealed record GradePlanTeacherEdit(string WorkspaceKey, string SubjectId, string Action,
    string? AccountId = null, string? Position = null, string? AssignmentId = null);
public sealed record GradePlanDraft(string TermId, string GradeId, string SubjectId, GradePlanRuleEdit[] Rules,
    GradePlanGroupEdit[] Groups, GradePlanTeacherEdit[] Assignments)
{
    public bool HasEdits => Rules.Length + Groups.Length + Assignments.Length > 0;
}

public sealed record GradePlanChoice(string Id, string Name, string Code);
public sealed record GradePlanLinkedGroup(string Id, string Name, string Code, bool IsStudentSelectable, GradePlanAssignment[] Assignments);
public sealed record GradePlanRoomState(string Mode, bool Compulsory, GradePlanChoice[] Choices, GradePlanAssignment[] Assignments,
    GradePlanLinkedGroup[] Groups);
public sealed record GradePlanRow(string Key, string ClassId, string ClassName, string Code, string SubjectId,
    string SubjectName, GradePlanRoomState Current, GradePlanRoomState After);
public sealed record GradePlanIssue(string Key, string Code, string Message);
public sealed record GradePlanGroupChange(string Key, string Operation, GradePlanCourse? Current, GradePlanCourse After);
public sealed record GradePlanTeacherChange(string Key, string WorkspaceKey, string SubjectId, string Action, string TeacherName, string Membership, string? AssignmentId = null, string? AccountId = null);
public sealed record GradePlanEvidence(string Key, string Version);
public sealed record GradePlanPreview(string Version, GradePlanEvidence[] Evidence, GradePlanRow[] Rows, GradePlanIssue[] Issues,
    GradePlanIssue[] Notices, GradePlanGroupChange[] GroupChanges, GradePlanTeacherChange[] TeacherChanges, bool CanApply);
public sealed record GradePlanCreatedGroup(string Key, string Id);
public sealed record GradePlanReceipt(Guid RequestId, string TermId, string GradeId, string SubjectId, GradePlanCreatedGroup[] CreatedGroups);
public sealed record GradePlanResult(string State, GradePlanReceipt? Receipt = null, GradePlanPreview? Preview = null, string[]? ChangedKeys = null);

public sealed partial class ConsoleClient
{
    private const string GradePlanSegment = "/current-term/grade-teaching-plans";
    private static bool IsGradePlanPath(string path) => path.Contains(GradePlanSegment, StringComparison.Ordinal);
    private static string GradePlanPath(string school) => $"api/v2/admin/schools/{Segment(school)}{GradePlanSegment}";

    public async Task<GradePlanCatalog> GetGradePlanAsync(string school, string? term, string? grade, string? subject, CancellationToken ct)
    {
        var session = CurrentSession;
        string query = term is null ? "" : $"?termId={Segment(term)}";
        if (grade is not null && subject is not null && term is not null)
            query += $"&gradeId={Segment(grade)}&subjectId={Segment(subject)}";
        var result = await RequestAsync<GradePlanCatalog>(HttpMethod.Get, GradePlanPath(school) + query, session.Token, ct);
        CheckCreationSession(session, ct);
        if (result.SchoolId != school || term is not null && result.TermId != term ||
            result.Grades is null || result.Subjects is null || result.Classes is null || result.Groups is null || result.Teachers is null ||
            grade is not null && !result.Grades.Any(row => row.Id == grade) ||
            subject is not null && !result.Subjects.Any(row => row.Id == subject))
            throw InvalidResponse();
        return result;
    }

    public async Task<GradePlanPreview> PreviewGradePlanAsync(string school, GradePlanDraft draft, CancellationToken ct)
    {
        var session = CurrentSession;
        var result = await RequestAsync<GradePlanPreview>(HttpMethod.Post, GradePlanPath(school) + "/preview", session.Token, ct, body: draft);
        CheckCreationSession(session, ct);
        ValidateGradePreview(result);
        return result;
    }

    public async Task<GradePlanResult> SaveGradePlanAsync(string school, GradePlanDraft draft, GradePlanPreview preview, Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        var result = await RequestAsync<GradePlanResult>(HttpMethod.Post, GradePlanPath(school), session.Token, ct,
            body: new {draft.TermId, draft.GradeId, draft.SubjectId, draft.Rules, draft.Groups, draft.Assignments, requestId,
                expectedVersion = preview.Version, expectedEvidence = preview.Evidence});
        CheckCreationSession(session, ct);
        if (result.State == "APPLIED" && result.Receipt is { } receipt) ValidateGradeReceipt(receipt, draft, requestId);
        else if (result.State is "STALE" or "BLOCKED" && result.Preview is { } observed) ValidateGradePreview(observed);
        else throw InvalidResponse();
        return result;
    }

    public async Task<GradePlanReceipt?> GetGradePlanReceiptAsync(string school, GradePlanDraft draft, Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        try
        {
            var receipt = await RequestAsync<GradePlanReceipt>(HttpMethod.Get, $"{GradePlanPath(school)}/receipts/{requestId:D}", session.Token, ct);
            CheckCreationSession(session, ct);
            ValidateGradeReceipt(receipt, draft, requestId);
            return receipt;
        }
        catch (ConsoleApiException error) when (error is { StatusCode: 404, Code: "GRADE_PLAN_RECEIPT_NOT_FOUND" })
        {
            CheckCreationSession(session, ct);
            return null;
        }
    }

    private static void ValidateGradeReceipt(GradePlanReceipt receipt, GradePlanDraft draft, Guid requestId)
    {
        if (receipt.RequestId != requestId || receipt.TermId != draft.TermId || receipt.GradeId != draft.GradeId ||
            receipt.SubjectId != draft.SubjectId || receipt.CreatedGroups is null ||
            receipt.CreatedGroups.Any(row => string.IsNullOrWhiteSpace(row.Id)) ||
            !receipt.CreatedGroups.Select(row => row.Key).Order().SequenceEqual(draft.Groups.Where(row => row.Operation == "CREATE")
                .Select(row => row.Key).Order()))
            throw InvalidResponse();
    }

    private static void ValidateGradePreview(GradePlanPreview preview)
    {
        if (!ValidActivationVersion(preview.Version) || preview.Rows is null || preview.Evidence is null ||
            preview.Issues is null || preview.Notices is null || preview.GroupChanges is null || preview.TeacherChanges is null ||
            preview.Evidence.Any(row => !ValidActivationVersion(row.Version)))
            throw InvalidResponse();
    }

    private static ConsoleApiException GradePlanError(int status, string? code) => new(status, code switch
    {
        "GRADE_PLAN_TERM_CHANGED" => "学校已切换当前学期。请刷新范围后重新建立方案。",
        "GRADE_PLAN_SCOPE_CHANGED" => "年级或科目已变化，请重新读取。",
        "GRADE_PLAN_INVALID" => "请核对调整项目和教学班代码。",
        "GRADE_PLAN_REQUEST_CONFLICT" => "此编号对应另一份方案。本次未写入，请重新预览。",
        "GRADE_PLAN_RECEIPT_NOT_FOUND" => "服务器暂未找到本次调整回执。",
        "WORKSPACE_CODE_EXISTS" => "教学班代码已占用，请核对后改码。",
        _ when status is 404 or 426 => "当前 KV 尚未提供年级教学安排，请更新服务。",
        _ => HttpError(status, false).Message,
    }, code);
}
