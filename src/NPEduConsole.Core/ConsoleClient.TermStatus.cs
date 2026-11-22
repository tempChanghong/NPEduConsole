namespace NPEduConsole.Core;

public sealed record TermStatusProof(string Version, string TermVersion, string DiagnosticsVersion,
    TermActivationBindingProof[] Screens, long ExpiresAt);

public sealed record TermStatusScreen(string BindingId, string Name, TermDraftOriginalClass? FromClass, TermDraftClass? TargetClass,
    bool Rebound, string NativeImpact, bool ArchiveInvalidatesNative, bool PlacementNeeded, bool IntentionallyRetained,
    string? Status = null, DateTimeOffset? LastReportedAt = null, TermScreenPlacement? Placement = null, TermStatusLiveScreen? Live = null);
public sealed record TermStatusLiveScreen(string ClassId, string ClassName, string TermId, string? TermName, string State,
    DateTimeOffset? LastReportedAt);
public sealed record TermStatusHistoryEntry(Guid RequestId, string TermId, string TermName, string Operation, DateTimeOffset CreatedAt)
{
    public string Label => $"{CreatedAt.ToLocalTime():MM-dd HH:mm:ss} · {Operation switch { "ACTIVATE" => "启用", "ARCHIVE" => "归档", _ => "转回草稿" }}";
    public override string ToString() => $"{Label} · {TermName}";
}

public sealed record TermStatusPreview(string SchoolId, ManagedAcademicTerm Term, ManagedAcademicTerm? ActiveTerm,
    ManagedAcademicTerm? NextActiveTerm, string Action, bool RebindScreens, TermStatusProof Proof, TermStatusScreen[] Screens,
    TermDraftDiagnostic[] BlockingDiagnostics, TermDraftDiagnostic[] Reminders, bool EvidenceComplete, bool ChangesStatus,
    bool CanForce, bool CanApply, bool MigrationEligible);

public sealed record TermStatusReceipt(Guid RequestId, string Operation, bool RebindScreens, bool Forced, ManagedAcademicTerm Term,
    ManagedAcademicTerm PreviousTerm, ManagedAcademicTerm? PreviousActiveTerm, ManagedAcademicTerm? ActiveTermAfterWrite,
    ManagedAcademicTerm? ArchivedTerm, int ReboundScreens, TermStatusScreen[] Screens);

public sealed record TermStatusResult(TermStatusReceipt Receipt, ManagedAcademicTerm? ActiveTerm, TermStatusScreen[] Screens,
    bool IsCurrentRound = false);

public sealed record TermStatusCommit(string State, string[]? Changes = null, TermStatusPreview? Preview = null, TermStatusReceipt? Receipt = null,
    ManagedAcademicTerm? ActiveTerm = null, TermStatusScreen[]? Screens = null, bool IsCurrentRound = false);

public sealed partial class ConsoleClient
{
    private static string TermStatusPath(string school, string term) => $"{TermPreparationPath(school)}/terms/{Segment(term)}/status";

    public async Task<TermStatusPreview> PreviewTermStatusAsync(string school, string term, string action, bool rebindScreens, CancellationToken ct)
    {
        var session = CurrentSession;
        var result = await RequestAsync<TermStatusPreview>(HttpMethod.Post, TermStatusPath(school, term) + "-preview", session.Token, ct,
            body: new {action, rebindScreens});
        CheckCreationSession(session, ct);
        ValidateTermStatusPreview(result, school, term, action, rebindScreens);
        return result;
    }

    public async Task<TermStatusCommit> SaveTermStatusAsync(TermStatusPreview preview, bool force, Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        string school = preview.SchoolId, term = preview.Term.Id, action = preview.Action;
        var result = await RequestAsync<TermStatusCommit>(HttpMethod.Post, TermStatusPath(school, term), session.Token, ct,
            body: new {action, preview.RebindScreens, force, requestId, preview.Proof});
        CheckCreationSession(session, ct);
        if (result is {State: "APPLIED", Receipt: { } receipt, Screens: { } screens})
            ValidateTermStatusResult(new(receipt, result.ActiveTerm, screens, result.IsCurrentRound), requestId, term, action, preview.RebindScreens);
        else if (result is {State: "STALE", Changes.Length: > 0, Preview: { } current})
            ValidateTermStatusPreview(current, school, term, action, preview.RebindScreens);
        else throw InvalidResponse();
        return result;
    }

    public async Task<TermStatusResult?> GetTermStatusReceiptAsync(string school, Guid requestId, CancellationToken ct,
        string? term = null, string? action = null, bool? rebindScreens = null)
    {
        var session = CurrentSession;
        try
        {
            var result = await RequestAsync<TermStatusResult>(HttpMethod.Get, $"{TermPreparationPath(school)}/status-receipts/{requestId:D}",
                session.Token, ct);
            CheckCreationSession(session, ct);
            ValidateTermStatusResult(result, requestId, term, action, rebindScreens);
            return result;
        }
        catch (ConsoleApiException error) when (error.StatusCode == 404 && error.Code == "TERM_STATUS_RECEIPT_NOT_FOUND")
        {
            CheckCreationSession(session, ct);
            return null;
        }
    }

    private static string? StatusForTermAction(string? action) => action switch
    {
        "ACTIVATE" => "ACTIVE", "ARCHIVE" => "ARCHIVED", "DRAFT" => "DRAFT", _ => null
    };

    private static bool ValidTermStatusScreen(TermStatusScreen? screen) => screen is { } row &&
        !string.IsNullOrWhiteSpace(row.BindingId) && !string.IsNullOrWhiteSpace(row.Name) &&
        row.NativeImpact is "VERIFY" or "REPAIR_REQUIRED" or "AWAITING_PAIRING" or "UNCHANGED" &&
        (!row.Rebound || row.FromClass is not null && row.TargetClass is not null && row.FromClass.Id != row.TargetClass.Id);

    private static void ValidateTermStatusPreview(TermStatusPreview view, string school, string term, string action, bool rebindScreens)
    {
        if (view.SchoolId != school || !ValidListedTerm(view.Term) || view.Term.Id != term || view.Action != action ||
            view.RebindScreens != rebindScreens || StatusForTermAction(action) is not { } status ||
            view.ActiveTerm is { } current && !ValidTerm(current, "ACTIVE") ||
            view.NextActiveTerm is { } next && !ValidTerm(next, "ACTIVE") ||
            view.MigrationEligible != (view.NextActiveTerm is not null) || view.ChangesStatus != (view.Term.Status != status) ||
            (action == "ACTIVATE" ? view.NextActiveTerm?.Id != term : view.NextActiveTerm?.Id !=
                (view.ActiveTerm?.Id == term ? null : view.ActiveTerm?.Id)) ||
            view.BlockingDiagnostics is null || view.Reminders is null || view.Screens is null ||
            view.CanForce && (action != "ACTIVATE" || !view.EvidenceComplete || view.BlockingDiagnostics.Length == 0) ||
            view.Screens.Any(row => !ValidTermStatusScreen(row) || row.Rebound && !rebindScreens || row.Status is not null) ||
            view.Screens.Select(row => row.BindingId).Distinct().Count() != view.Screens.Length ||
            view.Proof is not {ExpiresAt: > 0, Screens: { } proofScreens} proof || !ValidActivationVersion(proof.Version) ||
            !ValidActivationVersion(proof.TermVersion) || !ValidActivationVersion(proof.DiagnosticsVersion) ||
            proofScreens.Any(row => row is null || string.IsNullOrWhiteSpace(row.BindingId) || !ValidActivationVersion(row.Version)) ||
            proofScreens.Select(row => row.BindingId).Distinct().Count() != proofScreens.Length ||
            view.Screens.Any(row => !proofScreens.Any(proofRow => proofRow.BindingId == row.BindingId))) throw InvalidResponse();
    }

    private static void ValidateTermStatusResult(TermStatusResult result, Guid requestId, string? term, string? action, bool? rebindScreens)
    {
        if (result.Receipt is not { } receipt || receipt.RequestId != requestId ||
            StatusForTermAction(receipt.Operation) is not { } status || !ValidTerm(receipt.Term, status) ||
            !ValidListedTerm(receipt.PreviousTerm) || receipt.PreviousTerm.Id != receipt.Term.Id ||
            term is not null && receipt.Term.Id != term || action is not null && receipt.Operation != action ||
            rebindScreens is not null && receipt.RebindScreens != rebindScreens ||
            result.ActiveTerm is { } active && !ValidTerm(active, "ACTIVE") ||
            receipt.ActiveTermAfterWrite is { } after && !ValidTerm(after, "ACTIVE") ||
            receipt.Screens is null || result.Screens is null || receipt.Screens.Length != result.Screens.Length ||
            receipt.Screens.Any(row => !ValidTermStatusScreen(row) || row.Status is not null) ||
            receipt.Screens.Select(row => row.BindingId).Distinct().Count() != receipt.Screens.Length ||
            receipt.ReboundScreens != receipt.Screens.Count(row => row.Rebound)) throw InvalidResponse();
        for (int i = 0; i < receipt.Screens.Length; i++)
        {
            var row = result.Screens[i];
            if (row is null || (row with {Status = null, LastReportedAt = null, Placement = null, Live = null}) != receipt.Screens[i] ||
                !PlacementValid(row.Placement) || row.Status is not ("TERM_INACTIVE" or "UNKNOWN" or "NOT_APPLICABLE" or "VERIFY" or
                "REPAIR_REQUIRED" or "AWAITING_PAIRING" or "AWAITING_REPORT" or "REPORTED" or "RETAINED_ORIGINAL") ||
                row.Status == "REPORTED" && row.LastReportedAt is null) throw InvalidResponse();
        }
    }
}
