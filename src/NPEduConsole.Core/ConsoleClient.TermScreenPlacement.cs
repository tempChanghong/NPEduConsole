namespace NPEduConsole.Core;

public sealed record TermScreenClass(string Id, string Name, string Code, string? GradeName)
{
    public string Display => string.IsNullOrWhiteSpace(GradeName) || Name.StartsWith(GradeName, StringComparison.Ordinal)
        ? $"{Name}（{Code}）" : $"{GradeName} · {Name}（{Code}）";
}

public sealed record ExistingTermScreen(string Id, string Name);
public sealed record TermScreenCandidate(string Id, string Name, string Code, string? GradeName, string Version, ExistingTermScreen[] Screens)
{
    public TermScreenClass Classroom => new(Id, Name, Code, GradeName);
    public string Display => Classroom.Display;
}

public sealed record TermScreenPlacement(TermScreenClass TargetClass, string Source, string Status,
    DateTimeOffset? ArrangedAt, DateTimeOffset? LastReportedAt, bool BindingConfirmed,
    TermScreenClass? RecordedTargetClass = null, DateTimeOffset? RecordedAt = null)
{
    public string StatusText => Status switch
    {
        "AWAITING_PAIRING" => "已改绑 · 等待现场配对",
        "AWAITING_REPORT" => "已配对 · 等待首次有效上报",
        "REPORTED" => $"已收到首次有效上报（{LastReportedAt?.ToLocalTime():yyyy-MM-dd HH:mm}）",
        _ when BindingConfirmed => "已改绑 · 设备状态待核对",
        _ => "历史安排 · 当前绑定待核对"
    };
    public string Evidence => Source == "CONSOLE" ? "有人工安排回执" : RecordedTargetClass is { } previous
        ? $"本次启用后原先安排到 {previous.Display}，现已核对服务器当前绑定" : "已核对服务器当前绑定";
}

public sealed record TermScreenPlacementCheck(string SchoolId, ManagedAcademicTerm Term, string BindingId, string Name,
    TermDraftOriginalClass FromClass, string Version, bool CanArrange, TermScreenCandidate[] Candidates, TermScreenPlacement? Placement,
    Guid? ActivationRequestId = null);
public sealed record TermScreenPlacementReceipt(Guid RequestId, string TermId, string BindingId, string FromClassId,
    string TargetClassId, DateTimeOffset ArrangedAt, Guid? ActivationRequestId = null);
public sealed record TermScreenPlacementResult(TermScreenPlacementReceipt Receipt, TermScreenPlacement Placement, bool? IsCurrentRound = null);

public sealed partial class ConsoleClient
{
    private static string TermScreenPlacementPath(string school, string binding) =>
        $"{TermPreparationPath(school)}/screen-placements/{Segment(binding)}";

    public async Task<TermScreenPlacementCheck> GetTermScreenPlacementAsync(string school, string term, string binding, CancellationToken ct)
    {
        var session = CurrentSession;
        var check = await RequestAsync<TermScreenPlacementCheck>(HttpMethod.Get,
            $"{TermScreenPlacementPath(school, binding)}?termId={Segment(term)}", session.Token, ct);
        CheckCreationSession(session, ct);
        if (check.SchoolId != school || check.Term?.Id != term || check.BindingId != binding ||
            !ValidTerm(check.Term, check.CanArrange ? "ACTIVE" : check.Term.Status) ||
            string.IsNullOrWhiteSpace(check.Name) || check.FromClass is null || !HasValue(check.FromClass.Id) ||
            !ValidActivationVersion(check.Version) || check.ActivationRequestId == Guid.Empty ||
            check.Candidates is null || !check.CanArrange && check.Candidates.Length != 0 ||
            check.CanArrange && check.Placement is not null || !PlacementValid(check.Placement) ||
            check.Candidates.Any(row => row is null || !HasValue(row.Id) || !HasValue(row.Code) || !HasValue(row.Name) ||
                !ValidActivationVersion(row.Version) || row.Screens is null ||
                row.Screens.Any(screen => screen is null || !HasValue(screen.Id) || !HasValue(screen.Name))) ||
            check.Candidates.Select(row => row.Id).Distinct().Count() != check.Candidates.Length)
            throw InvalidResponse();
        return check;
    }

    public async Task<TermScreenPlacementResult> SaveTermScreenPlacementAsync(TermScreenPlacementCheck check, TermScreenCandidate target,
        Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        if (!check.CanArrange || !check.Candidates.Contains(target) || requestId == Guid.Empty)
            throw new ArgumentException("请先核对目标班级。", nameof(target));
        var result = await RequestAsync<TermScreenPlacementResult>(HttpMethod.Post, TermScreenPlacementPath(check.SchoolId, check.BindingId),
            session.Token, ct, body: new {requestId, termId = check.Term.Id, targetClassId = target.Id,
                version = check.Version, targetVersion = target.Version, check.ActivationRequestId});
        CheckCreationSession(session, ct);
        ValidatePlacementReceipt(result, check, target, requestId);
        return result;
    }

    public async Task<TermScreenPlacementResult?> GetTermScreenPlacementReceiptAsync(TermScreenPlacementCheck check, TermScreenCandidate target,
        Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        try
        {
            var result = await RequestAsync<TermScreenPlacementResult>(HttpMethod.Get,
                $"{TermPreparationPath(check.SchoolId)}/screen-placement-receipts/{requestId:D}", session.Token, ct);
            CheckCreationSession(session, ct);
            ValidatePlacementReceipt(result, check, target, requestId);
            return result;
        }
        catch (ConsoleApiException error) when (error is {StatusCode: 404, Code: "TERM_SCREEN_PLACEMENT_RECEIPT_NOT_FOUND"})
        {
            CheckCreationSession(session, ct);
            return null;
        }
    }

    private static void ValidatePlacementReceipt(TermScreenPlacementResult result, TermScreenPlacementCheck check, TermScreenCandidate target,
        Guid requestId)
    {
        if (result.Receipt is not { } receipt || receipt.RequestId != requestId || receipt.TermId != check.Term.Id ||
            receipt.BindingId != check.BindingId || receipt.FromClassId != check.FromClass.Id || receipt.TargetClassId != target.Id ||
            check.ActivationRequestId is { } round && receipt.ActivationRequestId != round ||
            receipt.ArrangedAt == default || result.Placement is null || !PlacementValid(result.Placement))
            throw InvalidResponse();
    }

    private static bool PlacementValid(TermScreenPlacement? value) =>
        value is null || value.TargetClass is { } room && HasValue(room.Id) && HasValue(room.Name) && HasValue(room.Code) &&
        (value.Source is "CONSOLE" or "OBSERVED") &&
        (value.Status is "AWAITING_PAIRING" or "AWAITING_REPORT" or "REPORTED" or "VERIFY") &&
        (value.Status != "REPORTED" || value.LastReportedAt is not null) &&
        (value.Source != "CONSOLE" || value.ArrangedAt is not null) &&
        (value.RecordedTargetClass is null || HasValue(value.RecordedTargetClass.Id) && value.RecordedAt is not null);

    private static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value);
}
