using System.Text.Json;

namespace NPEduConsole.Core;

public sealed record ManagedAcademicTerm(string Id, string Name, int AcademicYear, int Semester, string Status,
    DateTimeOffset? StartsAt, DateTimeOffset? EndsAt)
{
    public string Period => $"{AcademicYear} 学年 · 第 {Semester} 学期";
    public string StatusText => Status switch { "ACTIVE" => "启用中", "ARCHIVED" => "已归档", _ => "草稿" };
    [System.Text.Json.Serialization.JsonIgnore]
    public bool StartsYear { get; init; }
    public string YearHeading => $"{AcademicYear} 学年";
    public string Badge => $"第 {Semester} 学期 · {StatusText}";
    public string Dates => StartsAt is null && EndsAt is null ? "未设置起止日期" :
        $"{StartsAt?.ToString("yyyy-MM-dd") ?? "开始未定"} 至 {EndsAt?.ToString("yyyy-MM-dd") ?? "结束未定"}";
    public override string ToString() => $"{Name} · {Period}";
}
public sealed record TermPreparationCatalog(string SchoolId, ManagedAcademicTerm? ActiveTerm, ManagedAcademicTerm[] Drafts,
    bool HasAnyTerm, string? AdminUrl, string? SetupUrl, Guid? LatestActivationRequestId = null, ManagedAcademicTerm[]? Terms = null,
    Guid? LatestStatusRequestId = null, TermStatusHistoryEntry[]? StatusHistory = null, bool DetailsSupported = false);
public sealed record TermDraftSettings(int AcademicYear, int Semester, string Name, string? StartsAt, string? EndsAt,
    bool CarryWorkspaceMembers = true, bool CarryTeachingAssignments = true, bool CarryLeaderships = true, bool CarryPendingInvitations = false);
public sealed record TermDraftTarget(string Name, int AcademicYear, int Semester, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt);
public sealed record TermDraftCarry(bool WorkspaceMembers, bool TeachingAssignments, bool Leaderships, bool PendingInvitations);
public sealed record TermDraftCounts(int Grades, int AdministrativeClasses, int CourseGroups, int Workspaces, int WorkspaceMembers,
    int TeachingAssignments, int GradeLeaderships, int ClassLeaderships, int PendingInvitations, int SourceLinks)
{
    internal bool Valid => Grades >= 0 && AdministrativeClasses >= 0 && CourseGroups >= 0 && Workspaces >= 0 &&
        (long)AdministrativeClasses + CourseGroups <= Workspaces && WorkspaceMembers >= 0 && TeachingAssignments >= 0 &&
        GradeLeaderships >= 0 && ClassLeaderships >= 0 && PendingInvitations >= 0 && SourceLinks >= 0;
}
public sealed record TermDraftPerson(string AccountId, string Name, string Position)
{
    public string Role => Position switch
    {
        "HEAD_TEACHER" => "班主任", "CO_HEAD_TEACHER" => "协同班主任", "PRIMARY" => "主讲",
        "CO_TEACHER" => "协同任课", "DEPUTY" => "副组长", _ => Position
    };
}
public sealed record TermDraftClass(string Id, string Name, string Code);
public sealed record TermDraftWorkspace(string Id, string Name, string Code, string Type, bool IsActive, string? SubjectName,
    TermDraftClass[] SourceClasses, TermDraftPerson[] Assignments, TermDraftPerson[] Duties, int WorkspaceMembers, int PendingInvitations);
public sealed record TermDraftGrade(string Id, string Name, string Code, TermDraftPerson[] Duties);
public sealed record TermDraftPreview(ManagedAcademicTerm Source, TermDraftTarget Target, TermDraftCarry Carry, TermDraftCounts Counts,
    TermDraftWorkspace[] Workspaces, TermDraftGrade[] GradeDuties, string Version);
public sealed record TermDraftReceipt(Guid RequestId, string TermId, TermDraftCounts Counts);
public sealed record TermDraftResult(string State, TermDraftPreview? Preview = null, TermDraftReceipt? Receipt = null);
public sealed record TermDraftDiagnostic(string Source, string Severity, string Code, string Message, string? TargetTab = null,
    string? Consequence = null);
public sealed record TermDraftOriginalClass(string Id, string Name, string Code, string TermId, string TermName);
public record TermDraftScreenImpact(string BindingId, string Name, TermDraftOriginalClass? FromClass,
    TermDraftClass? TargetClass, string NativeImpact, bool ArchiveInvalidatesNative = false)
{
    public string Original => FromClass is { } room ? $"{room.Name}（{room.Code}） · {room.TermName}" : "原班待核对";
    public string Target => TargetClass is { } room ? $"{room.Name}（{room.Code}）" : "无匹配的新班";
    public string Impact => FromClass is null ? "原绑定证据不足，请核对。" : NativeImpact switch
    {
        "REPAIR_REQUIRED" => "启用时改绑；原配对会失效，需到教室重新配对。",
        "NO_REBIND" when ArchiveInvalidatesNative => "无匹配，仍绑定原班；归档会使原配对失效。",
        "NO_REBIND" => "无匹配，仍绑定原班，待安排。原生配对影响待核对。",
        _ => "启用时改绑；原生配对影响待核对。"
    };
}
public sealed record TermPreparationCheck(string SchoolId, ManagedAcademicTerm Term, ManagedAcademicTerm? ActiveTerm,
    TermDraftDiagnostic[] BlockingDiagnostics, TermDraftDiagnostic[] Reminders, TermDraftScreenImpact[] Screens,
    TermActivationProof? ActivationProof = null, bool EvidenceComplete = true);

public sealed partial class ConsoleClient
{
    private const string TermPreparationSuffix = "/term-preparation";
    private static string TermPreparationPath(string school) => $"api/v2/admin/schools/{Segment(school)}{TermPreparationSuffix}";
    private static bool IsTermPreparationPath(string path) => path.Contains(TermPreparationSuffix, StringComparison.Ordinal);

    private static async Task<ConsoleApiException> TermPreparationErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? code = null, existing = null, changeReason = null;
        int status = (int)response.StatusCode;
        try
        {
            using var content = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (content.RootElement.ValueKind == JsonValueKind.Object)
            {
                var root = content.RootElement;
                if (root.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String) code = value.GetString();
                if (root.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object)
                {
                    if (code == "TARGET_TERM_EXISTS" && details.TryGetProperty("termId", out var id) && id.ValueKind == JsonValueKind.String)
                        existing = id.GetString();
                    if (code == "TERM_SCREEN_PLACEMENT_CHANGED" && details.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                        changeReason = reason.GetString();
                }
            }
        }
        catch (JsonException) { }
        return new(status, code switch
        {
            "INVALID_TARGET_TERM" => "请填写有效的学年、学期和草稿名称。",
            "INVALID_TARGET_TERM_DATE" => "请核对学期开始和结束日期。",
            "INVALID_TARGET_TERM_RANGE" => "结束日期应在开始日期当天或之后。",
            "TARGET_TERM_EXISTS" => "该学年和学期已经存在。可检查现有草稿，或返回修改目标。",
            "TERM_DRAFT_SOURCE_CHANGED" => "当前启用学期或来源配置已变化。输入已保留，请重新核对。",
            "TERM_DRAFT_STRUCTURE_INVALID" => "来源学期含跨学期走班关系，请先在原入口核对。",
            "TERM_DRAFT_CHANGED" => "该学期已不再处于准备中，请刷新学期列表。",
            "TERM_DRAFT_RECEIPT_NOT_FOUND" => "暂未找到原请求的草稿回执。",
            "TERM_DRAFT_REQUEST_INVALID" => "请先核对继承预览，再建立草稿。",
            "TERM_DRAFT_REQUEST_CONFLICT" => "该请求编号已用于其他操作，请先核对原结果。",
            "TERM_DETAILS_RECEIPT_NOT_FOUND" => "暂未找到本次学期资料回执。",
            "TERM_DETAILS_REQUEST_INVALID" => "请先预览学期资料，再确认保存。",
            "TERM_DETAILS_REQUEST_CONFLICT" => "此编号已用于其他操作，请核对原结果。",
            "TERM_ACTIVATION_CHANGED" => "当前学期或启用依据已变化，请重新核对。",
            "TERM_ACTIVATION_REQUEST_INVALID" => "请重新读取启用检查后再确认。",
            "TERM_ACTIVATION_REQUEST_CONFLICT" => "启用请求编号已用于其他操作，请先核对原结果。",
            "TERM_ACTIVATION_RECEIPT_NOT_FOUND" => "暂未找到本次启用回执。",
            "TERM_STATUS_REQUEST_INVALID" => "请先核对本次学期操作，再确认。",
            "TERM_STATUS_PREVIEW_REQUIRED" => "请重新读取影响预览后再提交。",
            "TERM_STATUS_CHANGED" => "学期或逐屏影响已变化。请刷新并重新确认。",
            "TERM_STATUS_UNCHANGED" => "该学期已经处于所选状态。请刷新核对。",
            "TERM_STATUS_REQUEST_CONFLICT" => "此编号已有另一笔学期操作。请查原回执。",
            "TERM_STATUS_RECEIPT_NOT_FOUND" => "暂未找到本次学期操作的本人回执。",
            "TERM_SCREEN_EVIDENCE_UNAVAILABLE" => "未能核对全部大屏归属或授权。请补齐依据后刷新。",
            "TERM_NOT_READY" => "本次检查有教务阻断，无法启用。",
            "TERM_SCREEN_PLACEMENT_NOT_AVAILABLE" => "这台大屏当前没有可办理的未匹配记录。",
            "TERM_SCREEN_PLACEMENT_RECEIPT_NOT_FOUND" => "暂未找到本次安排回执。",
            "TERM_SCREEN_PLACEMENT_REQUEST_CONFLICT" => "请求编号已用于其他安排，请先核对结果。",
            "TERM_SCREEN_PLACEMENT_INVALID" => "请重新核对目标班级和现有大屏。",
            "TERM_SCREEN_PLACEMENT_CHANGED" => changeReason switch
            {
                "TERM_CHANGED" => "当前学期已切换。请返回学校总览，核对新学期。",
                "BINDING_CHANGED" => "这台屏的绑定或办理状态已变化。请核对当前归属。",
                "TARGET_CHANGED" => "目标班或该班已有大屏已变化。请核对后重新确认。",
                _ => "大屏、目标班或当前学期已变化，请核对最新资料。"
            },
            "TERM_NOT_FOUND" => "该学期不存在或不在当前学校。",
            _ when status is 404 or 405 or 426 => "当前 KV 尚未提供桌面学期管理，请使用原网页。",
            _ => HttpError(status, false).Message
        }, code) { ExistingTermId = existing };
    }

    public async Task<TermPreparationCatalog> GetTermPreparationAsync(string school, CancellationToken ct)
    {
        var session = CurrentSession;
        var result = await RequestAsync<TermPreparationCatalog>(HttpMethod.Get, TermPreparationPath(school), session.Token, ct);
        CheckCreationSession(session, ct);
        if (result.SchoolId != school || result.Drafts is null ||
            result.ActiveTerm is { } active && !ValidTerm(active, "ACTIVE") ||
            result.Drafts.Any(term => !ValidTerm(term, "DRAFT")) ||
            result.Drafts.Select(term => term.Id).Distinct().Count() != result.Drafts.Length ||
            result.Terms is { } terms && (terms.Any(term => !ValidListedTerm(term)) ||
                terms.Select(term => term.Id).Distinct().Count() != terms.Length ||
                terms.Count(term => term.Status == "ACTIVE") != (result.ActiveTerm is null ? 0 : 1) ||
                result.ActiveTerm is { } current && !terms.Contains(current) ||
                !terms.Where(term => term.Status == "DRAFT").SequenceEqual(result.Drafts))) throw InvalidResponse();
        return result;
    }

    public async Task<TermDraftPreview> PreviewTermDraftAsync(string school, string source, TermDraftSettings target, CancellationToken ct)
    {
        var session = CurrentSession;
        var result = await RequestAsync<TermDraftPreview>(HttpMethod.Post, TermPreparationPath(school) + "/preview",
            session.Token, ct, body: new {sourceTermId = source, target});
        CheckCreationSession(session, ct);
        ValidateTermPreview(result, source, target);
        return result;
    }

    public async Task<TermDraftResult> SaveTermDraftAsync(string school, string source, TermDraftSettings target,
        TermDraftPreview preview, Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        ValidateTermPreview(preview, source, target);
        var result = await RequestAsync<TermDraftResult>(HttpMethod.Post, TermPreparationPath(school), session.Token, ct,
            body: new {sourceTermId = source, target, requestId, expectedVersion = preview.Version});
        CheckCreationSession(session, ct);
        if (result.State == "APPLIED") ValidateTermReceipt(result.Receipt, requestId, preview.Counts);
        else if (result.State == "STALE" && result.Preview is { } observed) ValidateTermPreview(observed, source, target);
        else throw InvalidResponse();
        return result;
    }

    public async Task<TermDraftReceipt?> GetTermDraftReceiptAsync(string school, Guid requestId, TermDraftCounts expected, CancellationToken ct)
    {
        var session = CurrentSession;
        try
        {
            var result = await RequestAsync<TermDraftReceipt>(HttpMethod.Get, $"{TermPreparationPath(school)}/receipts/{requestId:D}", session.Token, ct);
            CheckCreationSession(session, ct);
            ValidateTermReceipt(result, requestId, expected);
            return result;
        }
        catch (ConsoleApiException error) when (error is {StatusCode: 404, Code: "TERM_DRAFT_RECEIPT_NOT_FOUND"})
        {
            CheckCreationSession(session, ct);
            return null;
        }
    }

    public async Task<TermPreparationCheck> GetTermPreparationCheckAsync(string school, string term, CancellationToken ct)
    {
        var session = CurrentSession;
        var result = await RequestAsync<TermPreparationCheck>(HttpMethod.Get, $"{TermPreparationPath(school)}/drafts/{Segment(term)}/check", session.Token, ct);
        CheckCreationSession(session, ct);
        ValidateTermCheck(result, school, term);
        return result;
    }

    private static void ValidateTermCheck(TermPreparationCheck result, string school, string term)
    {
        if (result.SchoolId != school || !ValidTerm(result.Term, "DRAFT") || result.Term.Id != term ||
            result.ActiveTerm is { } active && !ValidTerm(active, "ACTIVE") || result.Screens is null ||
            result.BlockingDiagnostics is null || result.Reminders is null ||
            result.Screens.Any(row => string.IsNullOrWhiteSpace(row.BindingId) || row.NativeImpact is not ("NO_REBIND" or "VERIFY" or "REPAIR_REQUIRED")) ||
            result.Screens.Select(row => row.BindingId).Distinct().Count() != result.Screens.Length ||
            result.ActivationProof is not null && !ValidActivationProof(result.ActivationProof, result.Screens)) throw InvalidResponse();
    }

    private static bool ValidTerm(ManagedAcademicTerm? term, string status) =>
        term is { AcademicYear: >= 2000 and <= 2200, Semester: >= 1 and <= 3 } && !string.IsNullOrWhiteSpace(term.Id) &&
        !string.IsNullOrWhiteSpace(term.Name) && term.Status == status;

    private static bool ValidListedTerm(ManagedAcademicTerm? term) => term?.Status is "ACTIVE" or "ARCHIVED" or "DRAFT" && ValidTerm(term, term.Status);

    private static void ValidateTermPreview(TermDraftPreview result, string source, TermDraftSettings target)
    {
        if (!ValidListedTerm(result.Source) || result.Source.Id != source || result.Target?.Name != target.Name ||
            result.Target.AcademicYear != target.AcademicYear || result.Target.Semester != target.Semester ||
            result.Counts is not { Valid: true } || result.Carry != new TermDraftCarry(target.CarryWorkspaceMembers,
                target.CarryTeachingAssignments, target.CarryLeaderships, target.CarryPendingInvitations) ||
            result.Workspaces is null || result.GradeDuties is null || result.Version is not { Length: 64 } ||
            !result.Version.All(char.IsAsciiHexDigitLower)) throw InvalidResponse();
    }

    private static void ValidateTermReceipt(TermDraftReceipt? result, Guid requestId, TermDraftCounts expected)
    {
        if (result is null || result.RequestId != requestId || result.Counts != expected || string.IsNullOrWhiteSpace(result.TermId))
            throw InvalidResponse();
    }
}
