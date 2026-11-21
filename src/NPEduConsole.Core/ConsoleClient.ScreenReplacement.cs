namespace NPEduConsole.Core;

public enum ScreenReplacementDecision { Approve, Reject }

public sealed record ScreenReplacementList(IReadOnlyList<ScreenReplacement> Items, ScreenReplacementDetail? LatestHandover);
public sealed record ScreenReplacementDetail(ScreenReplacement Request, string Progress, Guid? DeviceId, DateTimeOffset? FirstReportedAt);

public sealed class ScreenReplacement
{
    internal ScreenReplacement(ScreenReplacementWire value, ScreenReplacementScope scope)
    {
        PairingId = value.PairingId;
        DeviceName = value.DeviceName;
        AppVersion = value.AppVersion;
        ExpiresAt = value.ExpiresAt;
        ExpectedDeviceId = value.ExpectedDeviceId;
        CurrentDeviceName = value.CurrentDeviceName;
        ExpectedBindingRevision = value.ExpectedBindingRevision;
        State = value.State;
        Scope = scope;
    }

    public Guid PairingId { get; }
    public string DeviceName { get; }
    public string AppVersion { get; }
    public DateTimeOffset ExpiresAt { get; }
    public Guid ExpectedDeviceId { get; }
    public string CurrentDeviceName { get; }
    public long ExpectedBindingRevision { get; }
    public string State { get; }
    internal ScreenReplacementScope Scope { get; }
}

internal sealed record ScreenReplacementScope(Guid SessionId, string SchoolId, string ScreenId);
internal record ScreenReplacementWire(Guid PairingId, string DeviceName, string AppVersion, DateTimeOffset ExpiresAt,
    Guid ExpectedDeviceId, string CurrentDeviceName, long ExpectedBindingRevision, string State);

public sealed partial class ConsoleClient
{
    public async Task<ScreenReplacementList> GetScreenReplacementsAsync(string schoolId, string screenId, CancellationToken ct)
    {
        var session = CurrentSession;
        var scope = new ScreenReplacementScope(session.Id, schoolId, screenId);
        var response = await ReadReplacementResponseAsync<ReplacementListWire>(session, ReplacementPath(scope), ct, list: true);
        if (response.Items.Length > 32 || response.Items.Any(item => item?.State is not ("PENDING" or "APPROVED")) ||
            response.Items.Select(item => item.PairingId).Distinct().Count() != response.Items.Length) throw InvalidResponse();
        var latest = response.LatestHandover is { } item ? ReplacementDetail(item, scope) : null;
        if (latest is not null && (latest.Request.State != "ACTIVATED" || response.Items.Any(row => row.PairingId == latest.Request.PairingId)))
            throw InvalidResponse();
        return new(Array.AsReadOnly(response.Items.Select(row => ReplacementSnapshot(row, scope)).ToArray()), latest);
    }

    public async Task<ScreenReplacementDetail> GetScreenReplacementAsync(string schoolId, string screenId, Guid pairingId, CancellationToken ct)
    {
        if (pairingId == Guid.Empty) throw new ArgumentException("缺少换机申请编号。", nameof(pairingId));
        var session = CurrentSession;
        var scope = new ScreenReplacementScope(session.Id, schoolId, screenId);
        var response = await ReadReplacementResponseAsync<ReplacementDetailWire>(session, $"{ReplacementPath(scope)}/{pairingId:D}", ct);
        if (response.PairingId != pairingId) throw InvalidResponse();
        return ReplacementDetail(response, scope);
    }

    public async Task<ScreenReplacement> ReviewScreenReplacementAsync(ScreenReplacement snapshot, ScreenReplacementDecision decision,
        Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        var scope = snapshot.Scope;
        if (scope.SessionId != session.Id || snapshot.State != "PENDING")
            throw new ConsoleApiException(409, "请重新读取并核对这台大屏的待审申请。", "SNAPSHOT_CHANGED");
        if (requestId == Guid.Empty) throw new ArgumentException("缺少审核请求编号。", nameof(requestId));
        var action = decision switch
        {
            ScreenReplacementDecision.Approve => "approve",
            ScreenReplacementDecision.Reject => "reject",
            _ => throw new ArgumentOutOfRangeException(nameof(decision))
        };
        var path = $"{ReplacementPath(scope)}/{snapshot.PairingId:D}/{action}";
        var body = new { requestId, snapshot.ExpectedDeviceId, snapshot.ExpectedBindingRevision };
        try
        {
            var envelope = await SendAsync(HttpMethod.Post, path, session.Token, ct, "0.16", body, requestId);
            var receipt = ReplacementSnapshot(ReadData<ScreenReplacementWire>(envelope), scope);
            EnsureReplacementSession(session, ct);
            if (receipt.PairingId != snapshot.PairingId || receipt.ExpectedDeviceId != snapshot.ExpectedDeviceId ||
                receipt.ExpectedBindingRevision != snapshot.ExpectedBindingRevision || receipt.State == "PENDING" ||
                decision == ScreenReplacementDecision.Reject && receipt.State != "CANCELLED") throw InvalidResponse();
            return receipt;
        }
        catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or >= 500)
        {
            throw new ConsoleApiException(error.StatusCode, "学校是否收到本次审核尚未确认，请先核对同一份申请。", "REPLACEMENT_REVIEW_UNCONFIRMED");
        }
        catch (ConsoleApiException error) when (error.StatusCode is 404 or 410 ||
            error.Code is "BINDING_CHANGED" or "PREAUTHORIZATION_CHANGED" or "PAIRING_STATE_CONFLICT" or "IDEMPOTENCY_CONFLICT")
        {
            throw new ConsoleApiException(error.StatusCode, error.Code switch
            {
                "PREAUTHORIZATION_CHANGED" => "这台大屏的接入授权或班级口令已变化，请核对当前申请。",
                "BINDING_CHANGED" => "大屏归属或原设备已变化，请核对当前申请。",
                _ => "这份申请的状态已变化，请核对学校当前记录。"
            }, error.Code);
        }
    }

    private async Task<T> ReadReplacementResponseAsync<T>(Session session, string path, CancellationToken ct, bool list = false)
    {
        try
        {
            var result = await RequestAsync<T>(HttpMethod.Get, path, session.Token, ct, version: "0.16");
            EnsureReplacementSession(session, ct);
            return result;
        }
        catch (ConsoleApiException error) when (error.StatusCode == 426 || list && error.StatusCode == 404)
        {
            throw new ConsoleApiException(error.StatusCode, "无法打开换机审核，请核对此大屏及学校 KV 版本。", "SCREEN_REPLACEMENT_UNAVAILABLE");
        }
    }

    private void EnsureReplacementSession(Session session, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!ReferenceEquals(session, _session)) throw new ConsoleApiException(401, "登录会话已改变，请重新登录。");
    }

    private static string ReplacementPath(ScreenReplacementScope scope) =>
        $"api/v2/npep/schools/{Segment(scope.SchoolId)}/screen-bindings/{Segment(scope.ScreenId)}/replacements";

    private static ScreenReplacement ReplacementSnapshot(ScreenReplacementWire value, ScreenReplacementScope scope)
    {
        if (value.PairingId == Guid.Empty || value.ExpectedDeviceId == Guid.Empty || !ValidReplacementTime(value.ExpiresAt) ||
            value.ExpectedBindingRevision is < 1 or > 9007199254740991L || value.State is not ("PENDING" or "APPROVED" or "ACTIVATED" or "CANCELLED") ||
            string.IsNullOrWhiteSpace(value.DeviceName) || string.IsNullOrWhiteSpace(value.CurrentDeviceName) || string.IsNullOrWhiteSpace(value.AppVersion))
            throw InvalidResponse();
        return new(value, scope);
    }

    private static ScreenReplacementDetail ReplacementDetail(ReplacementDetailWire item, ScreenReplacementScope scope)
    {
        var request = ReplacementSnapshot(item, scope);
        bool hasDevice = item.DeviceId is { } device && device != Guid.Empty && device != request.ExpectedDeviceId;
        bool valid = item.Progress switch
        {
            "AWAITING_REVIEW" => request.State == "PENDING",
            "AWAITING_CONFIRMATION" => request.State == "APPROVED",
            "AWAITING_REPORT" => request.State == "ACTIVATED" && hasDevice && item.FirstReportedAt is null,
            "REPORTED" => request.State == "ACTIVATED" && hasDevice && item.FirstReportedAt is { } at && ValidReplacementTime(at),
            "CANCELLED" => request.State == "CANCELLED",
            "EXPIRED" => request.State is "PENDING" or "APPROVED",
            _ => false
        };
        if (!valid || request.State != "ACTIVATED" && (item.DeviceId is not null || item.FirstReportedAt is not null)) throw InvalidResponse();
        return new(request, item.Progress, item.DeviceId, item.FirstReportedAt);
    }

    private static bool ValidReplacementTime(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;

    private sealed record ReplacementListWire(ScreenReplacementWire[] Items, ReplacementDetailWire? LatestHandover = null);
    private sealed record ReplacementDetailWire(Guid PairingId, string DeviceName, string AppVersion, DateTimeOffset ExpiresAt,
        Guid ExpectedDeviceId, string CurrentDeviceName, long ExpectedBindingRevision, string State,
        string Progress, Guid? DeviceId, DateTimeOffset? FirstReportedAt)
        : ScreenReplacementWire(PairingId, DeviceName, AppVersion, ExpiresAt, ExpectedDeviceId, CurrentDeviceName, ExpectedBindingRevision, State);
}
