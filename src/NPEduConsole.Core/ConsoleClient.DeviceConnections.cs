namespace NPEduConsole.Core;

public sealed record ConnectionTerm(string Id, string Name);
public sealed record ConnectionGrade(string Id, string Name)
{
    public override string ToString() => Name;
}
public sealed record ConnectionDevice(string Id, string Name);
public sealed record ConnectionScreen(string Id, string Name, string ClassId, string ClassName, string? ClassCode,
    string? GradeId, long BindingRevision, ConnectionDevice? Device)
{
    public override string ToString() => $"{ClassName}{(ClassCode is null ? "" : $"（{ClassCode}）")} · {Name} · {Id}";
}
public sealed record ConnectionCatalog(string SchoolId, ConnectionTerm? Term, ConnectionGrade[] Grades, ConnectionScreen[] Screens)
{
    internal Guid SessionId { get; init; }
}
public sealed record ConnectionBatchItem(string ScreenBindingId, string Name, string AdministrativeClassId,
    string AdministrativeClassName, bool Enabled, bool Changed)
{
    public override string ToString() => $"{AdministrativeClassName} · {Name} · {ScreenBindingId}　"
        + (Changed ? Enabled ? "开放 → 关闭" : "关闭 → 开放" : Enabled ? "保持开放" : "保持关闭");
}
public sealed record ConnectionMissingCredential(string ClassId, string ClassName);
public sealed record ConnectionBatch(string TermId, string TermName, string TargetType, string? TargetId, string TargetName,
    bool Enabled, string PreviewDigest, int TotalScreens, int ChangedScreens, int UnchangedScreens, int ExcludedScreens,
    ConnectionBatchItem[] Items, bool Truncated, ConnectionMissingCredential[] MissingCredentials, bool Applied = false)
{
    internal Guid SessionId { get; init; }
    internal string SchoolId { get; init; } = "";
    internal Guid RequestId { get; init; }
}
public sealed record ConnectionCandidate(Guid PairingId, string DeviceName, string AppVersion, DateTimeOffset ExpiresAt,
    string State, string Source, string? ScreenBindingId)
{
    internal Guid SessionId { get; init; }
    internal string SchoolId { get; init; } = "";
}
public sealed record ConnectionApproval(Guid PairingId, Guid RequestId, string SchoolId, ConnectionScreen Screen);
public sealed record ConnectionApprovalState(Guid PairingId, Guid RequestId, string SchoolId, string ScreenBindingId,
    string AdministrativeClassId, long BindingRevision, string State, bool BindingCurrent, bool DeviceCurrent,
    Guid? DeviceId, DateTimeOffset? ReportedAt);

public sealed partial class ConsoleClient
{
    private static string ConnectionSchool(string schoolId) => $"api/v2/npep/schools/{Segment(schoolId)}";

    public async Task<ClassAdmission> GetConnectionAdmissionAsync(string schoolId, ConnectionScreen screen, CancellationToken ct)
    {
        var session = CurrentSession;
        var snapshot = await ReadClassAdmissionAsync(session, schoolId, screen.ClassId, screen.Id, ct);
        EnsureConnectionSession(session, ct);
        if (snapshot.Scope.BindingRevision is { } revision && revision != screen.BindingRevision)
            throw new ConsoleApiException(409, "大屏绑定版本已变化，请刷新目录再办理。", "BINDING_CHANGED");
        return snapshot;
    }

    public async Task<ConnectionCatalog> GetDeviceConnectionsAsync(string schoolId, CancellationToken ct)
    {
        var session = CurrentSession;
        var result = await RequestAsync<ConnectionCatalog>(HttpMethod.Get, $"{ConnectionSchool(schoolId)}/management-connections",
            session.Token, ct, version: "0.1");
        EnsureConnectionSession(session, ct);
        if (result.SchoolId != schoolId || result.Grades is null || result.Screens is null ||
            result.Term is null && (result.Grades.Length != 0 || result.Screens.Length != 0) ||
            result.Term is { } term && string.IsNullOrWhiteSpace(term.Id) ||
            result.Grades.Any(g => g is null || string.IsNullOrWhiteSpace(g.Id)) ||
            result.Screens.Any(s => s is null || string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.ClassId) ||
                s.BindingRevision < 1 || s.Device is { } device && !Guid.TryParseExact(device.Id, "D", out _)) ||
            result.Screens.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != result.Screens.Length)
            throw InvalidResponse();
        return result with { SessionId = session.Id };
    }

    public Task<ConnectionBatch> PreviewConnectionBatchAsync(ConnectionCatalog catalog, string? gradeId, bool enabled, CancellationToken ct)
    {
        var session = CurrentSession;
        if (catalog.SessionId != session.Id || catalog.Term is not { } term ||
            gradeId is not null && !catalog.Grades.Any(grade => grade.Id == gradeId))
            throw InvalidResponse();
        return SendConnectionBatchAsync(session, catalog.SchoolId, term.Id, gradeId, enabled, Guid.NewGuid(), null, ct);
    }

    public Task<ConnectionBatch> ApplyConnectionBatchAsync(ConnectionBatch preview, CancellationToken ct)
    {
        var session = CurrentSession;
        if (preview.SessionId != session.Id || preview.Applied || preview.ChangedScreens == 0 || preview.MissingCredentials.Length != 0)
            throw InvalidResponse();
        return SendConnectionBatchAsync(session, preview.SchoolId, preview.TermId, preview.TargetId, preview.Enabled,
            preview.RequestId, preview.PreviewDigest, ct);
    }

    private async Task<ConnectionBatch> SendConnectionBatchAsync(Session session, string schoolId, string termId, string? targetId,
        bool enabled, Guid requestId, string? proof, CancellationToken ct)
    {
        var targetType = targetId is null ? "SCHOOL" : "GRADE";
        var path = $"{ConnectionSchool(schoolId)}/pairing-access/{(proof is null ? "preview" : "batch")}";
        var body = new Dictionary<string, object?> { ["requestId"] = requestId, ["termId"] = termId,
            ["targetId"] = targetId, ["targetType"] = targetType, ["enabled"] = enabled };
        if (proof is not null) body["previewDigest"] = proof;
        try
        {
            var result = ReadData<ConnectionBatch>(await SendAsync(HttpMethod.Post, path, session.Token, ct, "0.1", body, requestId));
            EnsureConnectionSession(session, ct);
            if (result.TermId != termId || result.TargetType != targetType || result.TargetId != targetId ||
                result.Enabled != enabled || result.Applied != (proof is not null) || proof is not null && result.PreviewDigest != proof ||
                result.PreviewDigest is not { Length: 64 } || result.Items is null || result.MissingCredentials is null ||
                result.Items.Any(i => i is null) || result.MissingCredentials.Any(i => i is null) ||
                result.ChangedScreens < 0 || result.UnchangedScreens < 0 ||
                result.TotalScreens != (long)result.ChangedScreens + result.UnchangedScreens)
                throw InvalidResponse();
            return result with { SchoolId = schoolId, SessionId = session.Id, RequestId = requestId };
        }
        catch (ConsoleApiException error) when (proof is not null && OutcomeUnknown(error))
        {
            throw ConnectionUnconfirmed(error, "批量保存结果待核对。请先重读原范围现状。系统没有自动重发，也无法确认这次保存是否由你完成。");
        }
    }

    public async Task<ConnectionCandidate> ResolveConnectionCodeAsync(ConnectionCatalog catalog, string code, CancellationToken ct)
    {
        var session = CurrentSession;
        if (catalog.SessionId != session.Id) throw InvalidResponse();
        var normalized = code.Replace("-", "").Replace(" ", "").ToUpperInvariant();
        if (normalized.Length != 8 || normalized.Any(ch => !"ABCDEFGHJKLMNPQRSTUVWXYZ23456789".Contains(ch)))
            throw new ConsoleApiException(400, "请输入教室设备上显示的 8 位配对短码。", "INVALID_CHANGE");
        var requestId = Guid.NewGuid();
        var body = new { requestId, userCode = normalized };
        var envelope = await SendAsync(HttpMethod.Post, $"{ConnectionSchool(catalog.SchoolId)}/management-connections/resolve",
            session.Token, ct, "0.1", body, requestId);
        var result = ReadData<ConnectionCandidate>(envelope);
        EnsureConnectionSession(session, ct);
        if (result.PairingId == Guid.Empty || result.State is not ("PENDING" or "APPROVED") ||
            result.Source is not ("ADMIN" or "CLASS_APPLY") ||
            (result.Source == "CLASS_APPLY" || result.State == "APPROVED") && string.IsNullOrEmpty(result.ScreenBindingId) ||
            result.ExpiresAt <= envelope.ServerTime || string.IsNullOrWhiteSpace(result.DeviceName))
            throw InvalidResponse();
        return result with { SessionId = session.Id, SchoolId = catalog.SchoolId };
    }

    private sealed record AcceptedConnection(Guid PairingId, string State, Guid ApprovalId, string SchoolId,
        string ScreenBindingId, string AdministrativeClassId, long BindingRevision);

    public async Task ApproveConnectionAsync(ConnectionCatalog catalog, ConnectionCandidate candidate, ConnectionScreen screen,
        ConnectionApproval approval, CancellationToken ct)
    {
        var session = CurrentSession;
        if (catalog.SessionId != session.Id || candidate.SessionId != session.Id || catalog.SchoolId != candidate.SchoolId ||
            catalog.SchoolId != approval.SchoolId || !catalog.Screens.Contains(screen) || candidate.State != "PENDING" ||
            candidate.PairingId != approval.PairingId || approval.Screen != screen || approval.RequestId == Guid.Empty ||
            candidate.ScreenBindingId is { } requested && requested != screen.Id)
            throw InvalidResponse();
        var body = new { approval.RequestId, screenBindingId = screen.Id, expectedBindingRevision = screen.BindingRevision,
            capabilities = new[] { "device.status" } };
        try
        {
            var result = ReadData<AcceptedConnection>(await SendAsync(HttpMethod.Post,
                $"{ConnectionSchool(approval.SchoolId)}/pairings/{approval.PairingId:D}/approve",
                session.Token, ct, "0.1", body, approval.RequestId));
            EnsureConnectionSession(session, ct);
            if (result.PairingId != approval.PairingId || result.ApprovalId == Guid.Empty || result.State != "APPROVED" ||
                result.SchoolId != approval.SchoolId || result.ScreenBindingId != screen.Id ||
                result.AdministrativeClassId != screen.ClassId || result.BindingRevision != screen.BindingRevision)
                throw InvalidResponse();
        }
        catch (ConsoleApiException error) when (OutcomeUnknown(error))
        {
            throw ConnectionUnconfirmed(error, "批准结果待核对。请按此申请重新读取结果。");
        }
    }

    public async Task<ConnectionApprovalState?> ReadConnectionApprovalAsync(ConnectionApproval approval, CancellationToken ct)
    {
        var session = CurrentSession;
        try
        {
            var result = await RequestAsync<ConnectionApprovalState>(HttpMethod.Get,
                $"{ConnectionSchool(approval.SchoolId)}/management-connections/pairings/{approval.PairingId:D}/approvals/{approval.RequestId:D}",
                session.Token, ct, version: "0.1");
            EnsureConnectionSession(session, ct);
            if (result.PairingId != approval.PairingId || result.RequestId != approval.RequestId || result.SchoolId != approval.SchoolId ||
                result.ScreenBindingId != approval.Screen.Id || result.AdministrativeClassId != approval.Screen.ClassId ||
                result.BindingRevision != approval.Screen.BindingRevision ||
                result.State is not ("APPROVED" or "ACTIVATED" or "CANCELLED" or "EXPIRED") ||
                result.State == "ACTIVATED" && result.DeviceId is null ||
                result.ReportedAt is not null && (!result.DeviceCurrent || result.State != "ACTIVATED"))
                throw InvalidResponse();
            return result;
        }
        catch (ConsoleApiException error) when (error.StatusCode == 404) { return null; }
    }

    private sealed record CancelledConnection(Guid PairingId, string State);

    public async Task CancelConnectionAsync(ConnectionApproval approval, CancellationToken ct)
    {
        var session = CurrentSession;
        var requestId = Guid.NewGuid();
        try
        {
            var result = ReadData<CancelledConnection>(await SendAsync(HttpMethod.Post,
                $"{ConnectionSchool(approval.SchoolId)}/pairings/{approval.PairingId:D}/cancel", session.Token, ct,
                "0.1", new { requestId }, requestId));
            EnsureConnectionSession(session, ct);
            if (result.PairingId != approval.PairingId || result.State != "CANCELLED") throw InvalidResponse();
        }
        catch (ConsoleApiException error) when (OutcomeUnknown(error))
        {
            throw ConnectionUnconfirmed(error, "取消结果待核对。请按此申请重新读取结果。");
        }
    }

    private void EnsureConnectionSession(Session session, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!ReferenceEquals(session, _session)) throw new ConsoleApiException(401, "登录会话已改变，请重新登录。");
    }

    private static bool OutcomeUnknown(ConsoleApiException error) => error.StatusCode is 0 or 408 or >= 500;
    private static ConsoleApiException ConnectionUnconfirmed(ConsoleApiException error, string message) =>
        new(error.StatusCode, message, "CONNECTION_UNCONFIRMED");
}
