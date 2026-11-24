namespace NPEduConsole.Core;

public sealed record TeacherAccessTarget(string Action, string TermId, string WorkspaceId, string? AccountId = null,
    string? InvitationId = null, string? SubjectId = null);

public sealed record TeacherAccessPreview(string SchoolId, string Action, string TermId, string WorkspaceId, string? AccountId,
    string? InvitationId, string? SubjectId, string RoomName, string RoomCode, string TermName, string TermStatus,
    string? DisplayName, string? Identity, string? CurrentRole, string? NewRole, string[] Subjects, string[] Duties, string? DutyRole,
    bool FutureInvitation, bool CanApply, string? Reason, string Version)
{
    public TeacherAccessTarget Target => new(Action, TermId, WorkspaceId, AccountId, InvitationId, SubjectId);
}

public sealed record TeacherAccessReceipt(string Kind, Guid RequestId, string SchoolId, string Action, string TermId,
    string WorkspaceId, string? AccountId, string? InvitationId, string? SubjectId, string? CurrentRole, string? NewRole)
{
    public TeacherAccessTarget Target => new(Action, TermId, WorkspaceId, AccountId, InvitationId, SubjectId);
}

public sealed record TeacherAccessResult(string State, TeacherAccessPreview? Preview = null, TeacherAccessReceipt? Receipt = null);

public sealed partial class ConsoleClient
{
    private static string TeacherAccessPath(string school) => TeacherAllocationPath(school) + "/targeted-access";

    public async Task<TeacherAccessPreview> PreviewTeacherAccessAsync(string school, TeacherAccessTarget target, CancellationToken ct)
    {
        var session = CurrentSession;
        var preview = await RequestAsync<TeacherAccessPreview>(HttpMethod.Post, TeacherAccessPath(school) + "/preview", session.Token, ct, body: target);
        CheckCreationSession(session, ct);
        VerifyPreview(preview, school, target);
        return preview;
    }

    public async Task<TeacherAccessResult> CommitTeacherAccessAsync(string school, TeacherAccessTarget target, string version, Guid request, CancellationToken ct)
    {
        var session = CurrentSession;
        var data = await RequestAsync<TeacherAccessResult>(HttpMethod.Post, TeacherAccessPath(school), session.Token, ct,
            body: new {target.Action, target.TermId, target.WorkspaceId, target.AccountId, target.InvitationId,
                target.SubjectId, requestId = request, expectedVersion = version});
        CheckCreationSession(session, ct);
        if (data.State == "APPLIED" && data.Receipt is { } receipt) VerifyReceipt(receipt, school, target, request);
        else if (data.State == "STALE" && data.Preview is { } preview) VerifyPreview(preview, school, target);
        else throw InvalidResponse();
        return data;
    }

    public async Task<TeacherAccessReceipt?> GetTeacherAccessReceiptAsync(string school, TeacherAccessTarget target, Guid request, CancellationToken ct)
    {
        var session = CurrentSession;
        try
        {
            var receipt = await RequestAsync<TeacherAccessReceipt>(HttpMethod.Get, TeacherAccessPath(school) + $"/receipts/{request:D}", session.Token, ct);
            CheckCreationSession(session, ct);
            VerifyReceipt(receipt, school, target, request);
            return receipt;
        }
        catch (ConsoleApiException error) when (error.Code == "TEACHER_ACCESS_RECEIPT_NOT_FOUND")
        {
            CheckCreationSession(session, ct);
            return null;
        }
    }

    private static void VerifyPreview(TeacherAccessPreview data, string school, TeacherAccessTarget target)
    {
        if (data.SchoolId != school || data.Target != target || data.Version is not { Length: 64 } || !data.Version.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(data.RoomName) || string.IsNullOrWhiteSpace(data.TermName) ||
            data.TermStatus is not ("ACTIVE" or "DRAFT" or "ARCHIVED") || data.Subjects is null || data.Duties is null ||
            data.CanApply && (string.IsNullOrWhiteSpace(data.DisplayName) ||
                !ExpectedRoles(target.Action, data.CurrentRole, data.NewRole)))
            throw InvalidResponse();
    }

    private static void VerifyReceipt(TeacherAccessReceipt receipt, string school, TeacherAccessTarget target, Guid request)
    {
        if (receipt.Kind != "TARGETED_ACCESS" || receipt.RequestId != request || receipt.SchoolId != school || receipt.Target != target ||
            !ExpectedRoles(target.Action, receipt.CurrentRole, receipt.NewRole))
            throw InvalidResponse();
    }

    private static bool ExpectedRoles(string action, string? current, string? after) => action == "REPAIR"
        ? current is null && after is "TEACHER" or "ASSISTANT" : after is null && current is "TEACHER" or "ASSISTANT" or "VIEWER";
}
