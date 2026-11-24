namespace NPEduConsole.Core;

public sealed record TermDetailsDraft(string? TermId, string? Name, int AcademicYear, int Semester,
    bool ChangeStart, string? StartsAt, bool ChangeEnd, string? EndsAt);
public sealed record TermDetailsValues(string Name, int AcademicYear, int Semester, string Status,
    DateTimeOffset? StartsAt, DateTimeOffset? EndsAt);
public sealed record TermDetailsPreview(string SchoolId, ManagedAcademicTerm? Before, TermDetailsValues After, string Version);
public sealed record TermDetailsReceipt(Guid RequestId, string SchoolId, ManagedAcademicTerm Term);
public sealed record TermDetailsResult(string State, TermDetailsReceipt? Receipt = null, TermDetailsPreview? Preview = null);

public sealed partial class ConsoleClient
{
    private static Dictionary<string, object?> TermDetailsBody(TermDetailsDraft draft)
    {
        var body = new Dictionary<string, object?> { ["termId"] = draft.TermId };
        if (draft.Name is not null) body["name"] = draft.Name;
        if (draft.TermId is null)
        {
            body["academicYear"] = draft.AcademicYear;
            body["semester"] = draft.Semester;
        }
        if (draft.ChangeStart) body["startsAt"] = draft.StartsAt;
        if (draft.ChangeEnd) body["endsAt"] = draft.EndsAt;
        return body;
    }

    public async Task<TermDetailsPreview> PreviewTermDetailsAsync(string schoolId, TermDetailsDraft draft, CancellationToken ct)
    {
        var session = CurrentSession;
        var result = await RequestAsync<TermDetailsPreview>(HttpMethod.Post, TermPreparationPath(schoolId) + "/details/preview",
            session.Token, ct, body: TermDetailsBody(draft));
        CheckCreationSession(session, ct);
        ValidateTermDetailsPreview(result, schoolId, draft);
        return result;
    }

    public async Task<TermDetailsResult> CommitTermDetailsAsync(string schoolId, TermDetailsDraft draft, string version,
        Guid requestId, CancellationToken ct)
    {
        var body = TermDetailsBody(draft);
        body["expectedVersion"] = version;
        body["requestId"] = requestId;
        var session = CurrentSession;
        var result = await RequestAsync<TermDetailsResult>(HttpMethod.Post, TermPreparationPath(schoolId) + "/details", session.Token, ct, body: body);
        CheckCreationSession(session, ct);
        if (result is { State: "APPLIED", Receipt: { } receipt }) ValidateTermDetailsReceipt(receipt, schoolId, requestId, draft);
        else if (result is { State: "STALE", Preview: { } preview }) ValidateTermDetailsPreview(preview, schoolId, draft);
        else throw InvalidResponse();
        return result;
    }

    public async Task<TermDetailsReceipt> GetTermDetailsReceiptAsync(string schoolId, TermDetailsDraft draft,
        Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        var receipt = await RequestAsync<TermDetailsReceipt>(HttpMethod.Get,
            TermPreparationPath(schoolId) + $"/details/receipts/{requestId:D}", session.Token, ct);
        CheckCreationSession(session, ct);
        ValidateTermDetailsReceipt(receipt, schoolId, requestId, draft);
        return receipt;
    }

    private static void ValidateTermDetailsPreview(TermDetailsPreview result, string schoolId, TermDetailsDraft draft)
    {
        if (result.SchoolId != schoolId || result.Before?.Id != draft.TermId || result.After is not { } after ||
            after.Status != (result.Before?.Status ?? "DRAFT") || after.Name != (draft.Name ?? result.Before?.Name) ||
            after.AcademicYear != (result.Before?.AcademicYear ?? draft.AcademicYear) || after.Semester != (result.Before?.Semester ?? draft.Semester) ||
            result.Version is not { Length: 64 } || !result.Version.All(char.IsAsciiHexDigitLower)) throw InvalidResponse();
    }

    private static void ValidateTermDetailsReceipt(TermDetailsReceipt receipt, string schoolId, Guid requestId, TermDetailsDraft draft)
    {
        if (receipt.RequestId != requestId || receipt.SchoolId != schoolId || receipt.Term is not { } term ||
            !ValidTerm(term, term.Status) || term.Status is not ("ACTIVE" or "DRAFT" or "ARCHIVED") || draft.Name is not null && term.Name != draft.Name ||
            (draft.TermId is { } id ? term.Id != id : term.Status != "DRAFT" || term.AcademicYear != draft.AcademicYear || term.Semester != draft.Semester))
            throw InvalidResponse();
    }
}
