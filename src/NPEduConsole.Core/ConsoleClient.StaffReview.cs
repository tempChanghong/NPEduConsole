namespace NPEduConsole.Core;

public sealed record StaffReviewTerm(string Id, string Name, string Status, int AcademicYear, int Semester)
{
    public string StatusLabel => Status switch { "ACTIVE" => "启用中", "DRAFT" => "草稿", _ => "已归档" };
    public override string ToString() => $"{Name} · {StatusLabel}";
}

public sealed record StaffReviewIssue(string Key, string Code, string Severity, string Message, string? GradeId, string GradeName,
    string? ClassId, string? ClassName, string? AccountId, string? TeacherName, string? TeacherLabel, string Action)
{
    public static string IssueKey(string code, string? gradeId, string? classId, string? accountId) =>
        string.Join(':', new[] { code, gradeId, classId, accountId }.OfType<string>());
}

public sealed record StaffPolicySnapshot(string SchoolId, StaffLeadershipPolicies Policies, IReadOnlyList<StaffReviewTerm> Terms,
    StaffReviewTerm? Term, string ScopeVersion, IReadOnlyList<StaffReviewIssue> Issues)
{
    internal Guid SessionId { get; init; }
}

public sealed record StaffPolicyPreview(string State, StaffPolicySnapshot Current, string? Version, IReadOnlyList<StaffReviewIssue> Added,
    IReadOnlyList<StaffReviewIssue> Removed, IReadOnlyList<string> ChangedFields);
public sealed record StaffPolicyReceipt(Guid RequestId, string SchoolId, StaffLeadershipPolicies Policies,
    IReadOnlyList<string> ChangedFields, DateTimeOffset SavedAt);
public sealed record StaffPolicyImpact(IReadOnlyList<StaffReviewIssue> Added, IReadOnlyList<StaffReviewIssue> Removed);
public sealed record StaffPolicySaveResult(string State, StaffPolicySnapshot Current, IReadOnlyList<string> ChangedFields, StaffPolicyReceipt? Receipt,
    StaffPolicyImpact? CurrentImpact = null);

public sealed partial class ConsoleClient
{
    private const string StaffPolicySegment = "/staff-responsibility-policy";
    private static string StaffPolicyPath(string school) => "api/v2/admin/schools/" + Segment(school) + StaffPolicySegment;
    private static bool IsStaffPolicyPath(string path) => path.Contains(StaffPolicySegment, StringComparison.Ordinal);

    private static ConsoleApiException StaffPolicyError(int status, string? code) => new(status, code switch
    {
        "STAFF_POLICY_RECEIPT_NOT_FOUND" => "暂未查到本账号这次保存的回执。",
        "STAFF_POLICY_REVIEW_REQUIRED" => "请刷新并核对本校当前规则，再预览保存。",
        "STAFF_POLICY_TERM_NOT_FOUND" => "所选学期已不可读取，请返回并重新选择学期。",
        "STAFF_POLICY_REQUEST_CONFLICT" => "原编号已用于其他操作，请先核对。",
        "STAFF_POLICY_INVALID" => "请核对所选学期、明确修改的规则和原值。",
        _ when status is 404 or 405 or 426 => "当前 KV 版本暂不支持桌面师资核对，请使用原学校管理页或更新 KV。",
        _ => HttpError(status, false).Message
    }, code);

    public async Task<StaffPolicySnapshot> GetStaffPolicyAsync(string schoolId, string? termId, CancellationToken ct)
    {
        var session = CurrentSession;
        string query = termId is null ? "" : "?termId=" + Segment(termId);
        var result = await RequestAsync<StaffPolicySnapshot>(HttpMethod.Get, StaffPolicyPath(schoolId) + query, session.Token, ct);
        CheckCreationSession(session, ct);
        return ValidateStaffPolicy(result, session.Id, schoolId, termId);
    }

    public async Task<StaffPolicyPreview> PreviewStaffPolicyAsync(StaffPolicySnapshot baseline, StaffLeadershipPolicies desired, CancellationToken ct)
    {
        var session = CurrentSession;
        var (changes, expected) = StaffPolicyChanges(session, baseline, desired);
        var result = await RequestAsync<StaffPolicyPreview>(HttpMethod.Post, StaffPolicyPath(baseline.SchoolId) + "/preview", session.Token, ct,
            body: new { termId = baseline.Term?.Id, baseline.ScopeVersion, changes, expected });
        CheckCreationSession(session, ct);
        var current = ValidateStaffPolicy(result.Current, session.Id, baseline.SchoolId, baseline.Term?.Id);
        if (!ValidStaffIssueList(result.Added) || !ValidStaffIssueList(result.Removed) || !ValidStaffPolicyFields(result.ChangedFields, changes))
            throw InvalidResponse();
        if (result.State == "READY" && changes.Keys.ToHashSet().SetEquals(result.ChangedFields) && current.ScopeVersion == baseline.ScopeVersion)
            _ = ReadStructureVersion(result.Version);
        else if (result.State != "STALE" || result.Version is not null || result.Added.Count > 0 || result.Removed.Count > 0)
            throw InvalidResponse();
        return result with { Current = current };
    }

    public async Task<StaffPolicySaveResult> SaveStaffPolicyAsync(StaffPolicySnapshot baseline, StaffLeadershipPolicies desired,
        StaffPolicyPreview preview, Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        var (changes, expected) = StaffPolicyChanges(session, baseline, desired);
        if (requestId == Guid.Empty || preview is not { State: "READY", Version: { } version } || preview.Current.SessionId != session.Id ||
            preview.Current.ScopeVersion != baseline.ScopeVersion || !changes.Keys.ToHashSet().SetEquals(preview.ChangedFields))
            throw new ArgumentException("请重新预览岗位联动规则。", nameof(preview));
        var result = await RequestAsync<StaffPolicySaveResult>(HttpMethod.Put, StaffPolicyPath(baseline.SchoolId), session.Token, ct,
            body: new { termId = baseline.Term?.Id, baseline.ScopeVersion, changes, expected, previewVersion = version, requestId });
        CheckCreationSession(session, ct);
        var current = ValidateStaffPolicy(result.Current, session.Id, baseline.SchoolId, baseline.Term?.Id);
        if (!ValidStaffPolicyFields(result.ChangedFields, changes) ||
            result.CurrentImpact is { } impact && (!ValidStaffIssueList(impact.Added) || !ValidStaffIssueList(impact.Removed)))
            throw InvalidResponse();
        if (result.State == "APPLIED" && result.Receipt is { } receipt) CheckStaffPolicyReceipt(receipt, baseline.SchoolId, requestId, changes);
        else if (result.State != "STALE" || result.Receipt is not null) throw InvalidResponse();
        return result with { Current = current };
    }

    public async Task<StaffPolicyReceipt?> GetStaffPolicyReceiptAsync(StaffPolicySnapshot baseline, StaffLeadershipPolicies desired,
        Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        var (changes, _) = StaffPolicyChanges(session, baseline, desired);
        try
        {
            var receipt = await RequestAsync<StaffPolicyReceipt>(HttpMethod.Get,
                $"{StaffPolicyPath(baseline.SchoolId)}/receipts/{requestId:D}", session.Token, ct);
            CheckCreationSession(session, ct);
            CheckStaffPolicyReceipt(receipt, baseline.SchoolId, requestId, changes);
            return receipt;
        }
        catch (ConsoleApiException error) when (error is { StatusCode: 404, Code: "STAFF_POLICY_RECEIPT_NOT_FOUND" })
        {
            CheckCreationSession(session, ct);
            return null;
        }
    }

    public static IReadOnlyDictionary<string, bool> StaffPolicyValues(StaffLeadershipPolicies policy) => new Dictionary<string, bool>
    {
        ["gradeLeaderMustBeHomeroom"] = policy.GradeLeaderMustBeHomeroom,
        ["gradeLeaderMustTeach"] = policy.GradeLeaderMustTeach,
        ["homeroomMustTeach"] = policy.HomeroomMustTeach
    };

    private static (Dictionary<string, bool> Changes, Dictionary<string, bool> Expected)
        StaffPolicyChanges(Session session, StaffPolicySnapshot baseline, StaffLeadershipPolicies desired)
    {
        if (baseline.SessionId != session.Id) throw StaffPolicyError(409, "STAFF_POLICY_REVIEW_REQUIRED");
        var previous = StaffPolicyValues(baseline.Policies);
        var changes = StaffPolicyValues(desired).Where(pair => previous[pair.Key] != pair.Value).ToDictionary();
        if (changes.Count == 0) throw new ArgumentException("没有要保存的规则修改。", nameof(desired));
        return (changes, changes.Keys.ToDictionary(key => key, key => previous[key]));
    }

    private static StaffPolicySnapshot ValidateStaffPolicy(StaffPolicySnapshot value, Guid session, string school, string? term)
    {
        if (value is null || value.SchoolId != school || value.Policies is null || value.Terms is null ||
            value.Terms.Any(row => row is null || string.IsNullOrWhiteSpace(row.Id) || string.IsNullOrWhiteSpace(row.Name) ||
                row.Status is not ("ACTIVE" or "DRAFT" or "ARCHIVED")) ||
            value.Terms.Select(row => row.Id).Distinct().Count() != value.Terms.Count || value.Terms.Count(row => row.Status == "ACTIVE") > 1 ||
            value.Term is { } selected && !value.Terms.Contains(selected) ||
            term is not null && value.Term?.Id != term || !ValidStaffIssueList(value.Issues) ||
            value.Term is null && value.Issues.Count != 0) throw InvalidResponse();
        _ = ReadStructureVersion(value.ScopeVersion);
        return value with { SessionId = session };
    }

    private static bool ValidStaffPolicyFields(IReadOnlyList<string> keys, IReadOnlyDictionary<string, bool> changes) =>
        keys is not null && keys.Distinct().Count() == keys.Count && keys.All(changes.ContainsKey);

    private static bool ValidStaffIssueList(IReadOnlyList<StaffReviewIssue> issues) => issues is not null &&
        issues.All(row => row is not null && row.Key == StaffReviewIssue.IssueKey(row.Code, row.GradeId, row.ClassId, row.AccountId) &&
            !string.IsNullOrWhiteSpace(row.Code) && !string.IsNullOrWhiteSpace(row.Message) && !string.IsNullOrWhiteSpace(row.GradeName) &&
            row.Severity is ("ERROR" or "WARNING") && (row.Action switch
            {
                "TEACHING" => row.GradeId is not null && row.AccountId is not null,
                "CLASS_STAFF" => row.ClassId is not null,
                "GRADE_STAFF" => row.GradeId is not null,
                _ => false
            })) && issues.Select(row => row.Key).Distinct().Count() == issues.Count;

    private static void CheckStaffPolicyReceipt(StaffPolicyReceipt receipt, string school, Guid requestId, IReadOnlyDictionary<string, bool> changes)
    {
        if (receipt.RequestId != requestId || receipt.SchoolId != school || receipt.SavedAt == default || receipt.SavedAt.Offset != TimeSpan.Zero ||
            receipt.Policies is null || !ValidStaffPolicyFields(receipt.ChangedFields, changes) || receipt.ChangedFields.Count != changes.Count)
            throw InvalidResponse();
        var saved = StaffPolicyValues(receipt.Policies);
        if (changes.Any(pair => saved[pair.Key] != pair.Value)) throw InvalidResponse();
    }
}
