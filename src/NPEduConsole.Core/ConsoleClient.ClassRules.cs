using System.Text.Json.Serialization;

namespace NPEduConsole.Core;

public sealed record ClassRuleTerm(string Id, string Name, string Status)
{
    public string StatusLabel => Status switch { "ACTIVE" => "启用中", "DRAFT" => "草稿", _ => "已归档" };
}
public sealed record ClassRuleClassroom(string Id, string Name, string Code, string? GradeId, [property: JsonRequired] bool IsActive);
public sealed record ClassRuleSourceClass(string Id, string Name, string Code);
public sealed record ClassRuleGroup(string Id, string Name, string Code, [property: JsonRequired] bool IsActive,
    [property: JsonRequired] bool IsStudentSelectable, ClassRuleSourceClass[] Sources, [property: JsonRequired] bool ScopeValid);
public sealed record ClassRuleAssignment(string Id, string AccountId, string TeacherName, string Position, [property: JsonRequired] bool IsActive);
public sealed record ClassRuleRow(string SubjectId, string Code, string Name, string Category, string Mode, [property: JsonRequired] bool Compulsory,
    ClassRuleGroup[] Groups, ClassRuleAssignment[] Assignments)
{
    public ClassRuleChoice Choice => new(SubjectId, Mode, Compulsory);
}
public sealed record ClassRuleChoice(string SubjectId, string Mode, bool Compulsory);
public sealed record ClassRuleSnapshot(string SchoolId, ClassRuleTerm Term, ClassRuleClassroom Classroom, string Version, ClassRuleRow[] Rows)
{
    internal Guid SessionId { get; init; }
}
public sealed record ClassRuleRemoval(string SubjectId, string GroupId, string GroupName, string GroupCode, bool IsActive,
    ClassRuleSourceClass[] RemainingSources);
public sealed record ClassRuleIssue(string SubjectId, string Code, string Message, string Action);
public sealed record ClassRulePreview(string State, ClassRuleSnapshot Current, string? Version, ClassRuleChoice[] Changes,
    ClassRuleRemoval[] Removals, ClassRuleIssue[] Issues, ClassRuleIssue[] Notices,
    [property: JsonRequired] bool ClearsClass, [property: JsonRequired] bool CanApply);
public sealed record ClassRuleReceipt(string SchoolId, string TermId, string ClassId, Guid RequestId, DateTimeOffset SavedAt,
    ClassRuleChoice[] Changes, string[] RemovedSourceIds);
public sealed record ClassRuleSaveResult(string State, ClassRuleSnapshot Current, ClassRuleReceipt? Receipt, ClassRulePreview? Preview);

public sealed partial class ConsoleClient
{
    private static string ClassRulePath(string school, string classroom) =>
        $"api/v2/admin/schools/{Segment(school)}/administrative-classes/{Segment(classroom)}/subject-rules";
    private static bool IsClassRulePath(string path) => path.Contains("/administrative-classes/", StringComparison.Ordinal) &&
        path.Contains("/subject-rules", StringComparison.Ordinal);
    private static ConsoleApiException ClassRuleError(int status, string? code) => new(status, code switch
    {
        "CLASS_RULE_REVIEW_REQUIRED" => "请刷新整班规则，核对完整科目表再预览保存。",
        "CLASS_RULE_ROOM_NOT_FOUND" => "原行政班已不存在或不在本校。",
        "CLASS_RULE_TERM_CHANGED" => "行政班不在所选学期，请回到学期管理核对。",
        "CLASS_RULE_ROOM_INACTIVE" => "原班已停用，可查看原安排。请恢复班级后再编辑。",
        "CLASS_RULE_EXISTING_SCOPE_INVALID" => "已有关系无法按所选班及完整科目目录核对。请先核对原安排。",
        "CLASS_RULE_ARCHIVE_CONFIRM_REQUIRED" => "请先明确更正归档学期的安排。",
        "CLASS_RULE_REQUEST_CONFLICT" => "保存编号已关联另一项操作，请重新核对。",
        "CLASS_RULE_RECEIPT_NOT_FOUND" => "暂未找到本账号的本次保存回执。",
        "CLASS_RULE_INVALID" => "需核对完整科目表、授课方式和必修属性。",
        _ when status is 404 or 405 or 426 => "当前 KV 暂不支持新版整班授课编排，请先更新服务。",
        _ => HttpError(status, false).Message
    }, code);

    public async Task<ClassRuleSnapshot> GetClassRulesAsync(string school, string term, string classroom, CancellationToken ct)
    {
        var session = CurrentSession;
        var value = await RequestAsync<ClassRuleSnapshot>(HttpMethod.Get, ClassRulePath(school, classroom) + "?termId=" + Segment(term), session.Token, ct);
        CheckCreationSession(session, ct);
        return CheckClassRules(value, session.Id, school, term, classroom);
    }

    public async Task<ClassRulePreview> PreviewClassRulesAsync(ClassRuleSnapshot baseline, ClassRuleChoice[] rows, bool editArchived,
        CancellationToken ct)
    {
        var session = CheckClassRuleDraft(baseline, rows, editArchived);
        var outcome = await RequestAsync<ClassRulePreview>(HttpMethod.Post, ClassRulePath(baseline.SchoolId, baseline.Classroom.Id) + "/preview",
            session.Token, ct, body: new { termId = baseline.Term.Id, baselineVersion = baseline.Version, rows, editArchived });
        CheckCreationSession(session, ct);
        return CheckClassRulePreview(outcome, baseline, session.Id);
    }

    public async Task<ClassRuleSaveResult> SaveClassRulesAsync(ClassRuleSnapshot baseline, ClassRuleChoice[] rows, bool editArchived,
        ClassRulePreview preview, string[] confirmedSourceIds, bool confirmClear, Guid requestId, CancellationToken ct)
    {
        var session = CheckClassRuleDraft(baseline, rows, editArchived);
        if (preview is not { State: "READY", CanApply: true, Version: { } version } ||
            preview.Current.Version != baseline.Version || preview.Current.SessionId != session.Id || requestId == Guid.Empty ||
            preview.ClearsClass && !confirmClear || !SameIds(confirmedSourceIds, preview.Removals.Select(row => row.GroupId)) ||
            !preview.Changes.ToHashSet().SetEquals(rows.Where(row => !baseline.Rows.Any(original => original.Choice == row))))
            throw new ArgumentException("请确认完整预览及本次涉及的来源。", nameof(preview));
        var outcome = await RequestAsync<ClassRuleSaveResult>(HttpMethod.Put, ClassRulePath(baseline.SchoolId, baseline.Classroom.Id),
            session.Token, ct, body: new { termId = baseline.Term.Id, baselineVersion = baseline.Version, rows, editArchived,
                previewVersion = version, confirmedSourceIds, confirmClear, requestId });
        CheckCreationSession(session, ct);
        var current = CheckClassRules(outcome.Current, session.Id, baseline.SchoolId, baseline.Term.Id, baseline.Classroom.Id);
        if (outcome.State == "APPLIED" && outcome.Receipt is { } receipt && outcome.Preview is null)
            CheckClassRuleReceipt(receipt, baseline, preview, requestId);
        else if (outcome.State is ("STALE" or "BLOCKED") && outcome.Receipt is null && outcome.Preview is { } observed)
            outcome = outcome with { Preview = CheckClassRulePreview(observed, baseline, session.Id) };
        else throw InvalidResponse();
        return outcome with { Current = current };
    }

    public async Task<ClassRuleReceipt?> GetClassRuleReceiptAsync(ClassRuleSnapshot baseline, ClassRulePreview preview, Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        if (baseline.SessionId != session.Id || preview.Current.SessionId != session.Id || requestId == Guid.Empty)
            throw ClassRuleError(409, "CLASS_RULE_REVIEW_REQUIRED");
        try
        {
            var receipt = await RequestAsync<ClassRuleReceipt>(HttpMethod.Get,
                $"{ClassRulePath(baseline.SchoolId, baseline.Classroom.Id)}/receipts/{requestId:D}", session.Token, ct);
            CheckCreationSession(session, ct);
            CheckClassRuleReceipt(receipt, baseline, preview, requestId);
            return receipt;
        }
        catch (ConsoleApiException error) when (error is { StatusCode: 404, Code: "CLASS_RULE_RECEIPT_NOT_FOUND" })
        {
            CheckCreationSession(session, ct);
            return null;
        }
    }

    private Session CheckClassRuleDraft(ClassRuleSnapshot baseline, ClassRuleChoice[] rows, bool editArchived)
    {
        var session = CurrentSession;
        if (baseline.SessionId != session.Id) throw ClassRuleError(409, "CLASS_RULE_REVIEW_REQUIRED");
        if (!baseline.Classroom.IsActive) throw ClassRuleError(409, "CLASS_RULE_ROOM_INACTIVE");
        if (baseline.Term.Status == "ARCHIVED" && !editArchived) throw ClassRuleError(409, "CLASS_RULE_ARCHIVE_CONFIRM_REQUIRED");
        if (rows.Any(row => !ValidRuleChoice(row)) || !SameIds(rows.Select(row => row.SubjectId), baseline.Rows.Select(row => row.SubjectId)))
            throw new ArgumentException("必须提交所读取的完整科目表。", nameof(rows));
        return session;
    }

    private static ClassRuleSnapshot CheckClassRules(ClassRuleSnapshot value, Guid session, string school, string term, string classroom)
    {
        if (value is null || value.SchoolId != school || value.Term?.Id != term || value.Classroom?.Id != classroom ||
            value.Term.Status is not ("ACTIVE" or "DRAFT" or "ARCHIVED") || string.IsNullOrWhiteSpace(value.Term.Name) ||
            string.IsNullOrWhiteSpace(value.Classroom.Name) || value.Rows is null ||
            value.Rows.Any(row => row is null || !ValidRuleChoice(row.Choice) || string.IsNullOrWhiteSpace(row.Name) ||
                row.Category is not ("CORE" or "ELECTIVE" or "OTHER") || row.Groups is null || row.Assignments is null ||
                row.Groups.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id) || item.Sources is null) ||
                row.Assignments.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id))) ||
            value.Rows.Select(row => row.SubjectId).Distinct().Count() != value.Rows.Length) throw InvalidResponse();
        _ = ReadStructureVersion(value.Version);
        return value with { SessionId = session };
    }

    private static ClassRulePreview CheckClassRulePreview(ClassRulePreview preview, ClassRuleSnapshot baseline, Guid session)
    {
        var current = CheckClassRules(preview.Current, session, baseline.SchoolId, baseline.Term.Id, baseline.Classroom.Id);
        if (preview.Changes is null || preview.Changes.Any(row => !ValidRuleChoice(row) ||
                !current.Rows.Any(original => original.SubjectId == row.SubjectId)) ||
            preview.Changes.Select(row => row.SubjectId).Distinct().Count() != preview.Changes.Length ||
            preview.Removals is null || preview.Removals.Any(row => row is null || row.RemainingSources is null ||
                !preview.Changes.Any(change => change.SubjectId == row.SubjectId) ||
                !current.Rows.Any(subject => subject.SubjectId == row.SubjectId && subject.Groups.Any(item => item.Id == row.GroupId && item.ScopeValid))) ||
            preview.Removals.Select(row => row.GroupId).Distinct().Count() != preview.Removals.Length ||
            !ValidIssues(preview.Issues) || !ValidIssues(preview.Notices)) throw InvalidResponse();
        if (preview.State == "READY") _ = ReadStructureVersion(preview.Version);
        else if (preview.State != "STALE" || preview.CanApply || preview.Version is not null) throw InvalidResponse();
        return preview with { Current = current };
    }

    private static bool ValidIssues(IEnumerable<ClassRuleIssue>? issues) => issues is not null && issues.All(row =>
        row is not null && !string.IsNullOrWhiteSpace(row.SubjectId) && !string.IsNullOrWhiteSpace(row.Message) &&
        row.Action is "TEACHING" or "COURSE_GROUPS" or "NONE");

    private static bool ValidRuleChoice(ClassRuleChoice? row) => row is not null && !string.IsNullOrWhiteSpace(row.SubjectId) &&
        (row.Mode is "ADMIN_CLASS" or "COURSE_GROUP" || row.Mode == "NOT_OFFERED" && !row.Compulsory);

    private static bool SameIds(IEnumerable<string> left, IEnumerable<string> right) =>
        left.Order(StringComparer.Ordinal).SequenceEqual(right.Order(StringComparer.Ordinal));

    private static void CheckClassRuleReceipt(ClassRuleReceipt receipt, ClassRuleSnapshot baseline, ClassRulePreview preview, Guid requestId)
    {
        if (receipt.SchoolId != baseline.SchoolId || receipt.ClassId != baseline.Classroom.Id || receipt.TermId != baseline.Term.Id ||
            receipt.RequestId != requestId || receipt.SavedAt == default || receipt.Changes is null || receipt.RemovedSourceIds is null ||
            receipt.Changes.Length != preview.Changes.Length || !receipt.Changes.ToHashSet().SetEquals(preview.Changes) ||
            !SameIds(receipt.RemovedSourceIds, preview.Removals.Select(row => row.GroupId))) throw InvalidResponse();
    }
}
