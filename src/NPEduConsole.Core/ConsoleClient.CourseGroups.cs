namespace NPEduConsole.Core;

public sealed record CourseGroupLabel(string Id, string Name, string Code = "")
{
    public override string ToString() => Name;
}
public sealed record CourseGroupRule(string SubjectId, string Mode, TeachingAssignment[] Assignments);
public sealed record CourseGroupClass(string Id, string Name, string Code, string GradeId, CourseGroupRule[] Rules);
public sealed record ManagedCourseGroup(string Id, string Name, string Code, string GradeId, string SubjectId,
    bool IsStudentSelectable, string[] SourceClassIds, TeachingAssignment[] Assignments);
public sealed record CourseGroupCatalog(string SchoolId, string? TermId, string? TermName,
    CourseGroupLabel[] Grades, CourseGroupLabel[] Subjects, CourseGroupClass[] Classes, ManagedCourseGroup[] Groups);
public sealed record CourseGroupDraft(string TermId, string? GroupId, string GradeId, string SubjectId, string Name,
    string Code, bool IsStudentSelectable, string[] SourceClassIds);
public sealed record CourseGroupSourceChange(string ClassId, string ClassName, string Change, string Mode,
    CourseGroupLabel[] Groups, TeachingAssignment[] Assignments, bool Blocked, string Message);
public sealed record CourseGroupIssue(string Code, string Message);
public sealed record CourseGroupIdentity(string Name, string Code, bool IsStudentSelectable);
public sealed record CourseGroupPreview(string Version, bool CanApply, CourseGroupSourceChange[] Rows, CourseGroupIssue[] Issues,
    CourseGroupIdentity? CurrentGroup = null);
public sealed record CourseGroupReceipt(Guid RequestId, string TermId, string GroupId);
public sealed record CourseGroupResult(string State, CourseGroupPreview? Preview = null, CourseGroupReceipt? Receipt = null);

public sealed partial class ConsoleClient
{
    private const string CourseGroupsSuffix = "/current-term/course-groups";
    private static string CourseGroupsPath(string school) => $"api/v2/admin/schools/{Segment(school)}{CourseGroupsSuffix}";
    private static bool IsCourseGroupsPath(string path) => path.Contains(CourseGroupsSuffix, StringComparison.Ordinal);

    private static ConsoleApiException CourseGroupError(int status, string? code) => new(status, code switch
    {
        "COURSE_GROUP_TERM_CHANGED" => "当前启用学期已切换。请返回学校总览核对后再办理。",
        "COURSE_GROUP_REQUEST_CONFLICT" => "原请求编号已被使用，请核对回执。",
        "COURSE_GROUP_RECEIPT_NOT_FOUND" => "暂未找到本次办理回执。",
        "COURSE_GROUP_INVALID" => "请核对年级、学科、代码、名称和来源班。",
        "WORKSPACE_CODE_EXISTS" => "该代码已属于其他教学空间，请更换后重新预览。",
        _ when status is 404 or 405 or 426 => "当前 KV 暂不支持桌面走班办理。请升级 KV 或使用原网页。",
        _ => HttpError(status, false).Message
    }, code);

    public async Task<CourseGroupCatalog> GetCourseGroupsAsync(string school, string? term, CancellationToken ct)
    {
        var session = CurrentSession;
        var path = CourseGroupsPath(school) + (term is null ? "" : "?termId=" + Segment(term));
        var reply = await RequestAsync<CourseGroupCatalog>(HttpMethod.Get, path, session.Token, ct);
        CheckCreationSession(session, ct);
        if (reply.SchoolId != school || term is not null && reply.TermId != term || reply.Grades is null || reply.Subjects is null ||
            reply.Classes is null || reply.Groups is null || reply.Classes.Any(row => row.Rules is null) ||
            reply.Groups.Any(row => row.SourceClassIds is null || row.Assignments is null)) throw InvalidResponse();
        return reply;
    }

    public async Task<CourseGroupPreview> PreviewCourseGroupAsync(string school, CourseGroupDraft draft, CancellationToken ct)
    {
        var session = CurrentSession;
        var reply = await RequestAsync<CourseGroupPreview>(HttpMethod.Post, CourseGroupsPath(school) + "/preview", session.Token, ct, body: draft);
        CheckCreationSession(session, ct);
        CheckCourseGroupPreview(draft, reply);
        return reply;
    }

    public async Task<CourseGroupResult> SaveCourseGroupAsync(string school, CourseGroupDraft draft, CourseGroupPreview preview,
        Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        var body = new {draft.TermId, draft.GroupId, draft.GradeId, draft.SubjectId, draft.Name, draft.Code,
            draft.IsStudentSelectable, draft.SourceClassIds, requestId, expectedVersion = preview.Version};
        var reply = await RequestAsync<CourseGroupResult>(HttpMethod.Post, CourseGroupsPath(school), session.Token, ct, body: body);
        CheckCreationSession(session, ct);
        if (reply.State == "APPLIED")
            CheckCourseGroupReceipt(reply.Receipt, requestId, draft.TermId, draft.GroupId);
        else if (reply.State is "STALE" or "BLOCKED" && reply.Preview is { } observed)
            CheckCourseGroupPreview(draft, observed);
        else throw InvalidResponse();
        return reply;
    }

    public async Task<CourseGroupReceipt?> GetCourseGroupReceiptAsync(string school, CourseGroupDraft draft, Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        try
        {
            var reply = await RequestAsync<CourseGroupReceipt>(HttpMethod.Get, $"{CourseGroupsPath(school)}/receipts/{requestId:D}", session.Token, ct);
            CheckCreationSession(session, ct);
            CheckCourseGroupReceipt(reply, requestId, draft.TermId, draft.GroupId);
            return reply;
        }
        catch (ConsoleApiException error) when (error is {StatusCode: 404, Code: "COURSE_GROUP_RECEIPT_NOT_FOUND"})
        {
            CheckCreationSession(session, ct);
            return null;
        }
    }

    private static void CheckCourseGroupReceipt(CourseGroupReceipt? reply, Guid requestId, string term, string? group)
    {
        if (reply is null || reply.RequestId != requestId || reply.TermId != term || string.IsNullOrWhiteSpace(reply.GroupId) ||
            group is not null && reply.GroupId != group) throw InvalidResponse();
    }

    private static void CheckCourseGroupPreview(CourseGroupDraft draft, CourseGroupPreview reply)
    {
        if (reply.Version is not {Length: 64} || !reply.Version.All(char.IsAsciiHexDigitLower) || reply.Rows is null || reply.Issues is null ||
            reply.Rows.Select(row => row.ClassId).Distinct().Count() != reply.Rows.Length ||
            !reply.Rows.Where(row => row.Change != "REMOVE").Select(row => row.ClassId).ToHashSet().SetEquals(draft.SourceClassIds) ||
            reply.Rows.Any(row => row.Groups is null || row.Assignments is null || row.Change is not ("ADD" or "KEEP" or "REMOVE")) ||
            reply.CanApply != (reply.Issues.Length == 0 && reply.Rows.All(row => !row.Blocked))) throw InvalidResponse();
    }
}
