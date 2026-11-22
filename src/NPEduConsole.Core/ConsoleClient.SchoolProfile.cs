namespace NPEduConsole.Core;

public sealed record SchoolProfile(string Id, string Code, string Name, string TeacherAuthMode, bool AllowOAuthTeacherLogin,
    bool HasSharedTeacherPassword, DateTimeOffset UpdatedAt, bool SupportsLoginPolicy = false)
{
    internal Guid SessionId { get; init; }
}

public sealed record SchoolProfileDraft(string? Name, string? TeacherAuthMode, bool? AllowOAuthTeacherLogin, bool RotateSharedPassword);

public sealed record TeacherLoginImpactRow(string Kind, string Id, string Name, string? Identity, string Evidence, string NextLogin,
    string CurrentAccess, string? WorkspaceName, string? Action, string? TermId = null, string? WorkspaceId = null, bool NeedsAttention = false);

public sealed record TeacherLoginImpact(int AccountCount, int InvitationCount, int NeedsAttentionCount, IReadOnlyList<TeacherLoginImpactRow> Rows);

public sealed record SchoolProfilePreview(SchoolProfile Profile, SchoolProfile Proposed, bool RequiresReview,
    string Fingerprint, TeacherLoginImpact Impact);

public sealed record SchoolProfileReceipt(Guid RequestId, SchoolProfile Profile, bool PolicyChanged, bool PasswordRotated);

public sealed partial class ConsoleClient
{
    private sealed record SchoolProfileSaved(string Id, SchoolProfileReceipt Receipt);
    private const string SchoolProfileSuffix = "/profile";
    private static string SchoolProfilePath(string schoolId) => $"api/v2/admin/schools/{Segment(schoolId)}{SchoolProfileSuffix}";

    private static bool IsSchoolProfilePath(string path) => path.StartsWith("api/v2/admin/schools/", StringComparison.Ordinal) &&
        (path.EndsWith(SchoolProfileSuffix, StringComparison.Ordinal) || path.Contains(SchoolProfileSuffix + "/", StringComparison.Ordinal));

    public async Task<SchoolProfile> GetSchoolProfileAsync(string schoolId, CancellationToken ct)
    {
        var session = CurrentSession;
        var profile = await RequestAsync<SchoolProfile>(HttpMethod.Get, SchoolProfilePath(schoolId), session.Token, ct);
        CheckCreationSession(session, ct);
        return BindProfile(profile, schoolId, session);
    }

    public async Task<SchoolProfilePreview> PreviewSchoolProfileAsync(SchoolProfile profile, SchoolProfileDraft draft, CancellationToken ct)
    {
        var session = ProfileSession(profile);
        var body = ProfileFields(profile, draft);
        body["rotateSharedPassword"] = draft.RotateSharedPassword;
        var preview = await RequestAsync<SchoolProfilePreview>(HttpMethod.Post, SchoolProfilePath(profile.Id) + "/preview",
            session.Token, ct, body: body);
        CheckCreationSession(session, ct);
        var current = BindProfile(preview.Profile, profile.Id, session);
        var proposed = BindProfile(preview.Proposed, profile.Id, session);
        var impact = preview.Impact;
        if (current.UpdatedAt != profile.UpdatedAt || proposed.UpdatedAt != profile.UpdatedAt || current.Code != profile.Code ||
            proposed.Code != profile.Code || preview.Fingerprint.Length != 64 || !preview.Fingerprint.All(char.IsAsciiHexDigitLower) ||
            impact.Rows is null || impact.AccountCount != impact.Rows.Count(row => row.Kind == "ACCOUNT") ||
            impact.InvitationCount != impact.Rows.Count(row => row.Kind == "INVITATION") || impact.NeedsAttentionCount < 0 ||
            impact.NeedsAttentionCount != impact.Rows.Count(row => row.NeedsAttention) || impact.Rows.Count != impact.AccountCount + impact.InvitationCount ||
            impact.Rows.Any(row => row.Id.Length == 0 || row.Action is not (null or "TEACHER_ACCOUNT" or "WORKSPACE_INVITATION") ||
                row.Action == "WORKSPACE_INVITATION" && (row.Kind != "INVITATION" || row.TermId is null || row.WorkspaceId is null) ||
                row.Action == "TEACHER_ACCOUNT" && row.Kind != "ACCOUNT") ||
            impact.Rows.DistinctBy(row => (row.Kind, row.Id)).Count() != impact.Rows.Count)
            throw InvalidResponse();
        return preview with {Profile = current, Proposed = proposed};
    }

    public async Task<SchoolProfileReceipt> SaveSchoolProfileAsync(SchoolProfile profile, SchoolProfileDraft draft, string? fingerprint,
        Guid requestId, string? sharedPassword, CancellationToken ct)
    {
        var session = ProfileSession(profile);
        if (draft.RotateSharedPassword && (sharedPassword is null || sharedPassword.Length is < 8 or > 64))
            throw SchoolProfileError(422, "SHARED_TEACHER_PASSWORD_INVALID");
        var body = ProfileFields(profile, draft);
        body["requestId"] = requestId;
        if (fingerprint is not null) body["expectedFingerprint"] = fingerprint;
        if (draft.RotateSharedPassword) body["sharedPassword"] = sharedPassword!;
        var response = await RequestAsync<SchoolProfileSaved>(HttpMethod.Patch, SchoolProfilePath(profile.Id), session.Token, ct, body: body);
        CheckCreationSession(session, ct);
        if (response.Id != profile.Id) throw InvalidResponse();
        return BindReceipt(response.Receipt, profile.Id, requestId, session);
    }

    public async Task<SchoolProfileReceipt?> GetSchoolProfileReceiptAsync(string schoolId, Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        try
        {
            var receipt = await RequestAsync<SchoolProfileReceipt>(HttpMethod.Get,
                SchoolProfilePath(schoolId) + $"/requests/{requestId:D}", session.Token, ct);
            CheckCreationSession(session, ct);
            return BindReceipt(receipt, schoolId, requestId, session);
        }
        catch (ConsoleApiException error) when (error is {StatusCode: 404, Code: "SCHOOL_PROFILE_RECEIPT_NOT_FOUND"})
        {
            CheckCreationSession(session, ct);
            return null;
        }
    }

    private Session ProfileSession(SchoolProfile profile)
    {
        var session = CurrentSession;
        if (profile.SessionId != session.Id) throw new ConsoleApiException(409, "登录会话已变化，请重新读取学校设置。");
        return session;
    }

    private static Dictionary<string, object> ProfileFields(SchoolProfile profile, SchoolProfileDraft draft)
    {
        var body = new Dictionary<string, object> { ["expectedUpdatedAt"] = profile.UpdatedAt };
        if (draft.Name is { } name) body["name"] = name;
        if (draft.TeacherAuthMode is { } mode) body["teacherAuthMode"] = mode;
        if (draft.AllowOAuthTeacherLogin is { } fallback) body["allowOAuthTeacherLogin"] = fallback;
        return body;
    }

    private static SchoolProfile BindProfile(SchoolProfile profile, string schoolId, Session session)
    {
        if (profile.Id != schoolId || profile.Name.Length == 0 || profile.Code.Length == 0 || profile.UpdatedAt == default ||
            profile.TeacherAuthMode is not ("LOCAL_PIN" or "SHARED_PASSWORD" or "OAUTH_EMAIL")) throw InvalidResponse();
        if (!profile.SupportsLoginPolicy) throw SchoolProfileError(426, null);
        return profile with {SessionId = session.Id};
    }

    private static SchoolProfileReceipt BindReceipt(SchoolProfileReceipt receipt, string schoolId, Guid requestId, Session session)
    {
        if (receipt.RequestId != requestId) throw InvalidResponse();
        return receipt with {Profile = BindProfile(receipt.Profile, schoolId, session)};
    }

    private static ConsoleApiException SchoolProfileError(int status, string? code) => new(status, code switch
    {
        "ORGANIZATION_VERSION_CONFLICT" => "另一位管理员已更新学校设置。请对照服务器当前值，草稿保留。",
        "SCHOOL_PROFILE_IMPACT_CHANGED" => "教师账号、邀请或教学关系已有变化。本次未保存，请重新预览。",
        "SCHOOL_PROFILE_PREVIEW_REQUIRED" or "SCHOOL_PROFILE_VERSION_REQUIRED" => "请重新读取学校设置和影响后再保存。",
        "SHARED_TEACHER_PASSWORD_REQUIRED" => "首次启用学校通用口令时，请设置 8 至 64 个字符的口令。",
        "SHARED_TEACHER_PASSWORD_INVALID" => "学校通用教师口令须为 8 至 64 个字符。",
        "SHARED_TEACHER_PASSWORD_NOT_ALLOWED" => "选择学校通用口令后才能设置或轮换口令。",
        "SCHOOL_NAME_INVALID" => "学校名称须为 1 至 191 个字符。",
        "SCHOOL_ADMIN_REQUIRED" => "你已无权办理当前学校设置，请核对所选学校和权限。",
        "SCHOOL_PROFILE_RECEIPT_NOT_FOUND" => "暂未找到本人的本次操作回执。",
        "SCHOOL_PROFILE_REQUEST_CONFLICT" => "此编号已经用于其他操作，请读取原请求。",
        _ when status is 404 or 405 or 426 => "当前 KV 暂不支持此学校设置入口，请先升级。",
        _ => HttpError(status, false).Message,
    }, code);
}
