using System.Text.Json.Serialization;

namespace NPEduConsole.Core;

public sealed partial class ConsoleClient
{
    public Task<SoftwareUpdate> GetSoftwareUpdateAsync(string schoolId, string deviceId, CancellationToken ct) =>
        RequestSoftwareUpdateAsync(HttpMethod.Get, CurrentSession, schoolId, deviceId, ct);

    public async Task<SoftwareUpdate> SetSoftwareUpdateAsync(SoftwareUpdate snapshot, SoftwareUpdateAction action,
        string? targetVersion, Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        if (snapshot.Scope is not { } scope || scope.SessionId != session.Id)
            throw new ConsoleApiException(409, "请在当前账号下重新读取这台设备的软件维护状态。", "SNAPSHOT_CHANGED");

        string operation = action switch
        {
            SoftwareUpdateAction.Enroll when snapshot.CanInvite && targetVersion is null => "ENROLL",
            SoftwareUpdateAction.Approve when snapshot.EligibleReleases.Any(release => release.Version == targetVersion) => "APPROVE",
            SoftwareUpdateAction.Cancel when snapshot.CanCancel && targetVersion is null => "CANCEL",
            _ => throw new ConsoleApiException(409, "当前设备状态不允许此操作，请刷新并核对。", "INVALID_UPDATE_ACTION")
        };
        if (requestId == Guid.Empty)
            throw new ConsoleApiException(400, "维护操作缺少请求标识。", "INVALID_UPDATE_ACTION");

        var body = new SoftwareUpdateWrite(requestId, snapshot.Revision, operation, targetVersion);
        try
        {
            var saved = await RequestSoftwareUpdateAsync(HttpMethod.Post, session, scope.SchoolId, scope.DeviceId, ct, body);
            if (saved.Revision <= snapshot.Revision || !MatchesSoftwareUpdate(saved, body))
                throw InvalidResponse();
            return saved;
        }
        catch (ConsoleApiException ex) when (ex.StatusCode is 0 or 408 or >= 500)
        {
            throw new ConsoleApiException(ex.StatusCode, "学校是否接受本次操作尚未确认。请先刷新软件维护状态。",
                "UPDATE_UNCONFIRMED");
        }
    }

    private static bool MatchesSoftwareUpdate(SoftwareUpdate saved, SoftwareUpdateWrite request) => request.Action switch
    {
        "ENROLL" => saved.Enrollment is "INVITED" or "MANAGED" && saved.EnrollmentId is not null,
        "APPROVE" => saved.Plan?.TargetVersion == request.TargetVersion,
        "CANCEL" => saved.Plan?.State == "CANCELLED",
        _ => false
    };

    private async Task<SoftwareUpdate> RequestSoftwareUpdateAsync(HttpMethod method, Session session, string schoolId,
        string deviceId, CancellationToken ct, SoftwareUpdateWrite? body = null)
    {
        var schoolPath = $"api/v2/npep/schools/{Segment(schoolId)}";
        var path = $"{schoolPath}/devices/{Segment(deviceId)}/software-update";
        Envelope envelope;
        try
        {
            envelope = await SendAsync(method, path, session.Token, ct, "0.18", body, body?.RequestId);
        }
        catch (ConsoleApiException ex) when (ex.StatusCode == 401)
        {
            await RequestAsync<OverviewPage>(HttpMethod.Get, schoolPath + "/device-overview?limit=1", session.Token, ct, version: "0.1");
            throw new ConsoleApiException(409, "这台设备的绑定已改变或已过期，请刷新班级列表。");
        }

        var status = ReadData<SoftwareUpdate>(envelope);
        if (status.Product != "NPEduTools" || status.Revision < 0 ||
            (status.ObservedVersion is not null && SoftwareRelease.Parse(status.ObservedVersion) is null) ||
            status.Catalog.Any(release => SoftwareRelease.Parse(release.Version) is null) ||
            status.Plan is { } plan && SoftwareRelease.Parse(plan.TargetVersion) is null)
            throw InvalidResponse();
        return status with { Scope = new SoftwareUpdateScope(session.Id, schoolId, deviceId) };
    }

    private sealed record SoftwareUpdateWrite(Guid RequestId, long ExpectedRevision, string Action,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TargetVersion);
}
