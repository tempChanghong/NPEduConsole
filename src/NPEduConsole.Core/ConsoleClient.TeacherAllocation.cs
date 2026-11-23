using System.Globalization;

namespace NPEduConsole.Core;

public sealed partial class ConsoleClient
{
    private static string TeacherAllocationPath(string school) => $"api/v2/admin/schools/{Segment(school)}/teacher-allocation";
    private sealed record ArchivedInvitationResult(string SchoolId, string TermId, string GradeId, string WorkspaceId, string InvitationId);

    public async Task RevokeArchivedInvitationAsync(TeacherAllocationCatalog catalog, AllocationWorkspace room,
        AllocationInvitation invite, CancellationToken ct)
    {
        var term = catalog.Terms.SingleOrDefault(row => row.Id == catalog.TermId);
        if (!catalog.CanRevokeArchivedInvitations || term is not { Status: "ARCHIVED", UpdatedAt: { } termVersion } ||
            catalog.GradeId is not { } grade || !catalog.Workspaces.Contains(room) ||
            !catalog.Invitations.Contains(invite) || invite.WorkspaceId != room.Id || invite.ClaimedAt is not null ||
            invite.UpdatedAt is not { } invitationVersion)
            throw new ArgumentException("请重新读取这条归档待认领邀请。");
        var session = CurrentSession;
        var path = TeacherAllocationPath(catalog.SchoolId) + $"/archived/{Segment(term.Id)}/grades/{Segment(grade)}" +
            $"/workspaces/{Segment(room.Id)}/invitations/{Segment(invite.Id)}";
        static string Timestamp(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var confirmed = await RequestAsync<ArchivedInvitationResult>(HttpMethod.Delete, path, session.Token, ct, body: new
        {
            TermUpdatedAt = Timestamp(termVersion), InvitationUpdatedAt = Timestamp(invitationVersion), invite.NormalizedEmail, invite.Role
        });
        CheckCreationSession(session, ct);
        if (confirmed != new ArchivedInvitationResult(catalog.SchoolId, term.Id, grade, room.Id, invite.Id)) throw InvalidResponse();
    }

    public async Task<TeacherAllocationCatalog> GetTeacherAllocationAsync(string school, string? term, string? grade, bool accountsOnly,
        CancellationToken ct, bool archivedCorrection = false)
    {
        var session = CurrentSession;
        var options = new List<string>();
        if (term is not null) options.Add($"termId={Segment(term)}");
        if (grade is not null) options.Add($"gradeId={Segment(grade)}");
        if (accountsOnly) options.Add("accountsOnly=true");
        if (archivedCorrection) options.Add("archivedCorrection=true");
        var path = TeacherAllocationPath(school) + (options.Count == 0 ? "" : "?" + string.Join("&", options));
        var data = await RequestAsync<TeacherAllocationCatalog>(HttpMethod.Get, path, session.Token, ct);
        CheckCreationSession(session, ct);
        if (data.SchoolId != school || term is not null && data.TermId != term || data.GradeId != grade ||
            data.Mode is not ("LOCAL_PIN" or "SHARED_PASSWORD" or "OAUTH_EMAIL") ||
            data.Terms.Any(row => row.Status is not ("ACTIVE" or "DRAFT") && !(archivedCorrection && row.Status == "ARCHIVED")) ||
            archivedCorrection && !data.Terms.Any(row => row.Id == term && row.Status == "ARCHIVED") ||
            data.TermId is not null && !data.Terms.Any(row => row.Id == data.TermId) ||
            data.GradeId is not null && !data.Grades.Any(row => row.Id == data.GradeId) ||
            data.Workspaces.Any(row => row.TermId != data.TermId || grade is not null && row.GradeId != grade))
            throw InvalidResponse();
        return data;
    }

    public async Task<TeacherAllocationPreview> PreviewTeacherAllocationAsync(string school, TeacherAllocationDraft draft, CancellationToken ct)
    {
        var session = CurrentSession;
        var data = await RequestAsync<TeacherAllocationPreview>(HttpMethod.Post, TeacherAllocationPath(school) + "/preview",
            session.Token, ct, body: draft);
        CheckCreationSession(session, ct);
        VerifyAllocationPreview(data, draft);
        return data;
    }

    public async Task<TeacherAllocationResult> CommitTeacherAllocationAsync(string school, TeacherAllocationDraft draft,
        TeacherAllocationPreview preview, Guid requestId, CancellationToken ct)
    {
        VerifyAllocationPreview(preview, draft);
        var session = CurrentSession;
        var data = await RequestAsync<TeacherAllocationResult>(HttpMethod.Post, TeacherAllocationPath(school), session.Token, ct, body: new
        {
            draft.Mode, draft.TermId, draft.TermStatus, draft.GradeId, draft.Teachers, draft.ArchivedCorrection, requestId,
            ExpectedVersion = preview.Version, ExpectedEvidence = preview.Evidence
        });
        CheckCreationSession(session, ct);
        if (data.State == "APPLIED" && data.Receipt is { } receipt) VerifyAllocationReceipt(receipt, draft, requestId);
        else if (data.State is "STALE" or "BLOCKED" && data.Preview is { } current)
        {
            if (data.State == "STALE" && data.ChangedKeys is null) throw InvalidResponse();
            VerifyAllocationPreview(current, draft);
        }
        else throw InvalidResponse();
        return data;
    }

    public async Task<TeacherAllocationReceipt?> GetTeacherAllocationReceiptAsync(string school, TeacherAllocationDraft draft, Guid requestId,
        CancellationToken ct)
    {
        var session = CurrentSession;
        try
        {
            var data = await RequestAsync<TeacherAllocationReceipt>(HttpMethod.Get, $"{TeacherAllocationPath(school)}/receipts/{requestId:D}",
                session.Token, ct);
            CheckCreationSession(session, ct);
            VerifyAllocationReceipt(data, draft, requestId);
            return data;
        }
        catch (ConsoleApiException error) when (error is {StatusCode: 404, Code: "TEACHER_ALLOCATION_RECEIPT_NOT_FOUND"})
        {
            CheckCreationSession(session, ct);
            return null;
        }
    }

    private static void VerifyAllocationPreview(TeacherAllocationPreview data, TeacherAllocationDraft draft)
    {
        static bool Digest(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
        if (data.Mode != draft.Mode || data.TermId != draft.TermId || data.TermStatus != draft.TermStatus ||
            data.GradeId != draft.GradeId || !Digest(data.Version) ||
            data.Evidence.Any(row => row.Key.Length == 0 || !Digest(row.Version)) ||
            data.Evidence.Select(row => row.Key).Distinct().Count() != data.Evidence.Length ||
            data.Changes.Any(row => row.Action is not ("ADD" or "CHANGE" or "REMOVE" or "KEEP" or "BLOCK")) ||
            data.CanApply == data.Changes.Any(row => row.Action == "BLOCK"))
            throw InvalidResponse();
    }

    private static void VerifyAllocationReceipt(TeacherAllocationReceipt receipt, TeacherAllocationDraft draft, Guid requestId)
    {
        if (receipt.RequestId != requestId || receipt.TermId != draft.TermId || receipt.TermStatus != draft.TermStatus ||
            receipt.GradeId != draft.GradeId || receipt.Teachers.Length != draft.Teachers.Length)
            throw InvalidResponse();
        for (var i = 0; i < draft.Teachers.Length; i++)
        {
            var input = draft.Teachers[i];
            var result = receipt.Teachers[i];
            if (input.AccountId is not null && result.AccountId != input.AccountId || result.Created && (draft.ArchivedCorrection || result.AccountId is null) ||
                input.Username is not null && !string.Equals(input.Username.Trim(), result.Username, StringComparison.OrdinalIgnoreCase) ||
                input.Email is not null && !string.Equals(input.Email.Trim(), result.Email, StringComparison.OrdinalIgnoreCase) ||
                input.Access.Length != result.Access.Length)
                throw InvalidResponse();
            for (var j = 0; j < input.Access.Length; j++)
            {
                var row = input.Access[j];
                var confirmed = result.Access[j];
                if (row.WorkspaceId is not null && confirmed.WorkspaceId != row.WorkspaceId ||
                    confirmed.State is not ("MEMBER" or "INVITED" or "REMOVED" or "REVOKED") ||
                    row.Operation == "SET" && (confirmed.Role != row.Role || confirmed.State is not ("MEMBER" or "INVITED") ||
                        draft.ArchivedCorrection && confirmed.State != "MEMBER") ||
                    row.Operation == "REMOVE" && confirmed.State != "REMOVED" ||
                    row.Operation == "REVOKE" && (confirmed.State != "REVOKED" || confirmed.InvitationId != row.InvitationId))
                    throw InvalidResponse();
            }
        }
    }
}
