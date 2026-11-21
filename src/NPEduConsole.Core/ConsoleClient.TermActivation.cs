namespace NPEduConsole.Core;

public sealed record TermActivationBindingProof(string BindingId, string Version);
public sealed record TermActivationProof(string Version, string TermVersion, string DiagnosticsVersion, TermActivationBindingProof[] Screens);
public sealed record TermActivatedScreen(string BindingId, string Name, TermDraftOriginalClass? FromClass, TermDraftClass? TargetClass,
    string NativeImpact, bool ArchiveInvalidatesNative, bool Rebound, string? Status = null, DateTimeOffset? LastReportedAt = null,
    TermScreenPlacement? Placement = null)
    : TermDraftScreenImpact(BindingId, Name, FromClass, TargetClass, NativeImpact, ArchiveInvalidatesNative)
{
    public bool NeedsAttention => (Placement?.Status ?? Status) != "REPORTED";
    public string PlacementDetails => Placement is { } row ? $"后来安排　{row.TargetClass.Display} · {row.Evidence}" : "";
    public bool HasPlacement => Placement is not null;
    public string Outcome => Placement?.StatusText ?? (Status switch
    {
        "REPAIR_REQUIRED" => "已改绑 · 原配对失效，需现场重新配对",
        "AWAITING_PAIRING" => "已改绑 · 等待现场接入",
        "AWAITING_REPORT" => "已改绑 · 已配对，等待首次有效上报",
        "REPORTED" => $"已改绑 · 已收到有效上报（{LastReportedAt?.ToLocalTime():yyyy-MM-dd HH:mm}）",
        "RETAINED_ORIGINAL" when ArchiveInvalidatesNative => "无匹配，仍绑定原班 · 原学期已归档，原配对已失效",
        "RETAINED_ORIGINAL" => "无匹配，仍绑定原班 · 待安排",
        _ => "待核对 · 请以当前绑定和设备回执为准"
    });
}
public sealed record TermActivationReceipt(Guid RequestId, ManagedAcademicTerm Term, ManagedAcademicTerm? ArchivedTerm,
    int ReboundScreens, TermActivatedScreen[] Screens);
public sealed record TermActivationResult(string? State = null, TermActivationReceipt? Receipt = null, ManagedAcademicTerm? ActiveTerm = null,
    TermActivatedScreen[]? Screens = null, string[]? Changes = null, TermPreparationCheck? Check = null, bool PlacementSupported = false);

public sealed partial class ConsoleClient
{
    public async Task<TermActivationResult> ActivateTermAsync(string school, TermPreparationCheck check, Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        if (check.SchoolId != school || !ValidActivationProof(check.ActivationProof, check.Screens) ||
            check.BlockingDiagnostics.Length != 0 || requestId == Guid.Empty)
            throw new ArgumentException("请先确认最新启用检查。", nameof(check));
        var result = await RequestAsync<TermActivationResult>(HttpMethod.Post,
            $"{TermPreparationPath(school)}/drafts/{Segment(check.Term.Id)}/activate", session.Token, ct,
            body: new {requestId, proof = check.ActivationProof});
        CheckCreationSession(session, ct);
        if (result.State == "APPLIED") ValidateActivationReceipt(result, requestId, check.Term.Id);
        else if (result.State == "STALE" && result.Changes is {Length: > 0} && result.Check is { } latest)
            ValidateTermCheck(latest, school, check.Term.Id);
        else throw InvalidResponse();
        return result;
    }

    public async Task<TermActivationResult?> GetTermActivationReceiptAsync(string school, Guid requestId, string? termId, CancellationToken ct)
    {
        var session = CurrentSession;
        try
        {
            var result = await RequestAsync<TermActivationResult>(HttpMethod.Get,
                $"{TermPreparationPath(school)}/activation-receipts/{requestId:D}", session.Token, ct);
            CheckCreationSession(session, ct);
            ValidateActivationReceipt(result, requestId, termId);
            return result;
        }
        catch (ConsoleApiException error) when (error is {StatusCode: 404, Code: "TERM_ACTIVATION_RECEIPT_NOT_FOUND"})
        {
            CheckCreationSession(session, ct);
            return null;
        }
    }

    private static bool ValidActivationProof(TermActivationProof? proof, TermDraftScreenImpact[] screens) =>
        proof is {Screens: not null} && ValidActivationVersion(proof.Version) && ValidActivationVersion(proof.TermVersion) &&
        ValidActivationVersion(proof.DiagnosticsVersion) && proof.Screens.Length == screens.Length &&
        proof.Screens.All(row => row is not null && ValidActivationVersion(row.Version)) &&
        proof.Screens.Select(row => row.BindingId).SequenceEqual(screens.Select(row => row.BindingId));

    private static bool ValidActivationVersion(string? value) => value is {Length: 64} && value.All(char.IsAsciiHexDigitLower);

    private static void ValidateActivationReceipt(TermActivationResult result, Guid requestId, string? termId)
    {
        if (result.Receipt is not { } receipt || receipt.RequestId != requestId ||
            !ValidTerm(receipt.Term, "ACTIVE") || termId is not null && receipt.Term.Id != termId ||
            receipt.ArchivedTerm is { } archived && (!ValidTerm(archived, "ARCHIVED") || archived.Id == receipt.Term.Id) ||
            result.ActiveTerm is { } active && !ValidTerm(active, "ACTIVE") ||
            receipt.Screens is null || result.Screens is null || result.Screens.Length != receipt.Screens.Length ||
            receipt.Screens.Any(row => row is null || string.IsNullOrWhiteSpace(row.BindingId) || row.Rebound != (row.TargetClass is not null)) ||
            receipt.Screens.Select(row => row.BindingId).Distinct().Count() != receipt.Screens.Length ||
            receipt.ReboundScreens != receipt.Screens.Count(row => row.Rebound))
            throw InvalidResponse();
        for (int i = 0; i < receipt.Screens.Length; i++)
        {
            var row = result.Screens[i];
            if (row is null || (row with {Status = null, LastReportedAt = null, Placement = null}) != receipt.Screens[i] || !PlacementValid(row.Placement) ||
                row.Status is not ("REPAIR_REQUIRED" or "AWAITING_PAIRING" or "AWAITING_REPORT" or "REPORTED" or "RETAINED_ORIGINAL" or "VERIFY") ||
                row.Status == "REPORTED" && row.LastReportedAt is null)
                throw InvalidResponse();
        }
    }
}
