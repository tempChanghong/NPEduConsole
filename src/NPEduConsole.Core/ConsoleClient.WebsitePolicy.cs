namespace NPEduConsole.Core;

public sealed record WebsitePolicyEffective(int SchemaVersion, string SchoolId, string TermId, string ClassId, string Source,
    string[] BlockedHosts, string PolicyId);
public sealed record WebsitePolicyRoom(string Id, string Name, string Code, WebsitePolicyEffective Before, WebsitePolicyEffective After);
public sealed record WebsitePolicyScreen(string ClassId, string BindingId, string BindingName, long BindingRevision, string? DeviceId,
    string? DeviceName, string Status, DateTimeOffset? ReceivedAt);
public sealed record WebsitePolicySnapshot(string SchoolId, string TermId, string ScopeKind, string ScopeId, string ScopeName,
    string[]? BeforeHosts, string[]? AfterHosts, WebsitePolicyRoom[] Rows, WebsitePolicyScreen[] Screens, bool CanApply, string Version);
public sealed record WebsitePolicyReceipt(string SchoolId, string TermId, string ScopeKind, string ScopeId, Guid RequestId,
    long Revision, int ChangedClasses);

public sealed partial class ConsoleClient
{
    private static string WebsitePolicyPath(string schoolId) => $"api/v2/npep/schools/{Segment(schoolId)}/website-policy";

    public async Task<ClassroomActionCatalog> GetWebsitePolicyCatalogAsync(string schoolId, CancellationToken ct)
    {
        var result = await WebsitePolicyRequestAsync<ClassroomActionCatalog>(HttpMethod.Get, WebsitePolicyPath(schoolId), ct);
        if (result.SchoolId != schoolId || RequireNonNullRows(result.Grades).Any(grade => string.IsNullOrWhiteSpace(grade.Id)) ||
            RequireNonNullRows(result.Rooms).Any(room => room.TermId != result.Term?.Id || string.IsNullOrWhiteSpace(room.Id)))
            throw InvalidResponse();
        return result;
    }

    public async Task<WebsitePolicySnapshot> GetWebsitePolicyAsync(string schoolId, ClassroomActionTarget target, CancellationToken ct)
    {
        var path = WebsitePolicyPath(schoolId) + $"/scope/{Segment(target.TermId)}/{Segment(target.ScopeKind)}/{Segment(target.ScopeId)}";
        return CheckWebsiteScope(await WebsitePolicyRequestAsync<WebsitePolicySnapshot>(HttpMethod.Get, path, ct), schoolId, target);
    }

    public async Task<WebsitePolicySnapshot> PreviewWebsitePolicyAsync(string schoolId, ClassroomActionTarget target, string[]? blockedHosts, CancellationToken ct)
    {
        var requestId = Guid.NewGuid();
        var result = await WebsitePolicyRequestAsync<WebsitePolicySnapshot>(HttpMethod.Post, WebsitePolicyPath(schoolId) + "/preview",
            ct, new { requestId, scope = target, blockedHosts }, requestId);
        return CheckWebsiteScope(result, schoolId, target);
    }

    public async Task<WebsitePolicyReceipt> ApplyWebsitePolicyAsync(string schoolId, ClassroomActionTarget target, string[]? blockedHosts,
        string expectedVersion, Guid requestId, CancellationToken ct)
    {
        var result = await WebsitePolicyRequestAsync<WebsitePolicyReceipt>(HttpMethod.Post, WebsitePolicyPath(schoolId), ct,
            new { requestId, scope = target, blockedHosts, expectedVersion }, requestId);
        return CheckWebsiteReceipt(result, schoolId, target, requestId);
    }

    public async Task<WebsitePolicyReceipt> GetWebsitePolicyReceiptAsync(string schoolId, ClassroomActionTarget target, Guid requestId, CancellationToken ct)
    {
        var result = await WebsitePolicyRequestAsync<WebsitePolicyReceipt>(HttpMethod.Get, WebsitePolicyPath(schoolId) + $"/receipts/{requestId:D}", ct);
        return CheckWebsiteReceipt(result, schoolId, target, requestId);
    }

    private async Task<T> WebsitePolicyRequestAsync<T>(HttpMethod method, string path, CancellationToken ct, object? body = null, Guid? requestId = null)
    {
        var session = CurrentSession;
        var data = ReadData<T>(await SendAsync(method, path, session.Token, ct, version: "0.16", body: body, operationId: requestId));
        ct.ThrowIfCancellationRequested();
        if (!ReferenceEquals(session, _session)) throw new ConsoleApiException(401, "登录会话已改变，请重新登录。");
        return data;
    }

    private static WebsitePolicySnapshot CheckWebsiteScope(WebsitePolicySnapshot data, string schoolId, ClassroomActionTarget target)
    {
        var rooms = RequireNonNullRows(data.Rows);
        bool validEffective(WebsitePolicyEffective? policy, string classId) => policy is not null && policy.SchemaVersion == 1 &&
            policy.SchoolId == schoolId && policy.TermId == target.TermId && policy.ClassId == classId &&
            policy.Source is "NONE" or "GRADE" or "CLASS" && RequireNonNullRows(policy.BlockedHosts).Length <= 24;
        if (data.SchoolId != schoolId || data.TermId != target.TermId || data.ScopeKind != target.ScopeKind || data.ScopeId != target.ScopeId ||
            data.Version is null || !ActionPolicyProof.IsMatch(data.Version) ||
            rooms.Any(row => !validEffective(row.Before, row.Id) || !validEffective(row.After, row.Id)) ||
            target.ScopeKind == "CLASS" && (rooms.Length != 1 || rooms[0].Id != target.ScopeId) ||
            RequireNonNullRows(data.Screens).Any(screen => !rooms.Any(row => row.Id == screen.ClassId) ||
                screen.Status is not ("NOT_PAIRED" or "AWAITING_DEVICE" or "RECEIVED_NOT_APPLIED") ||
                screen.Status == "RECEIVED_NOT_APPLIED" && (screen.DeviceId is null || screen.ReceivedAt is null)))
            throw InvalidResponse();
        return data;
    }

    private static WebsitePolicyReceipt CheckWebsiteReceipt(WebsitePolicyReceipt data, string schoolId, ClassroomActionTarget target, Guid requestId)
    {
        if (data.SchoolId != schoolId || data.TermId != target.TermId || data.ScopeKind != target.ScopeKind || data.ScopeId != target.ScopeId ||
            data.RequestId != requestId || data.Revision <= 0 || data.ChangedClasses < 0) throw InvalidResponse();
        return data;
    }
}
