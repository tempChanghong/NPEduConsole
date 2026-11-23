
namespace NPEduConsole.Core;

public sealed record DeviceFieldScope(string SchoolId, string TermId, string ClassId, string BindingId, string DeviceId);
public sealed record DeviceFieldTarget(string SchoolId, string TermId, string ClassId, string BindingId, string DeviceId,
    long BindingRevision, string DeviceName, string State, DateTimeOffset? LastSeenAt)
{
    public DeviceFieldScope Scope => new(SchoolId, TermId, ClassId, BindingId, DeviceId);
}
public sealed record CurrentFieldDevice(string DeviceId, string DeviceName);
public sealed record DeviceFieldResume(string Reason, string? ExecutionReason, string? Version, string? Mode, StudyReportWindow? Window,
    DateTimeOffset? ObservedAt, string? ReviewToken, DateTimeOffset? CurrentSamplingAt = null);
public sealed record DeviceFieldRevoke(string? ReviewToken);
public sealed record DeviceFieldState(DeviceFieldTarget Target, CurrentFieldDevice? CurrentDevice, DeviceFieldResume Resume, DeviceFieldRevoke Revoke);
public sealed record DeviceFieldRequest(DeviceFieldScope Scope, long ExpectedBindingRevision, string Action, string ReviewToken, Guid Id);
public sealed record DeviceFieldResult(Guid RequestId, string Action, DeviceFieldTarget Target, DateTimeOffset AcceptedAt, Guid? CommandId,
    DateTimeOffset? ExpiresAt, string Outcome, DateTimeOffset? DeviceProcessedAt, DateTimeOffset? SampleAt, bool SampleFresh = false,
    Guid? SampleSessionId = null);

public sealed partial class ConsoleClient
{
    private const string FieldVersion = "0.19";
    private static string FieldPath(DeviceFieldScope scope) =>
        $"api/v2/npep/schools/{Segment(scope.SchoolId)}/devices/{Segment(scope.DeviceId)}/field-actions";
    private static string FieldQuery(DeviceFieldScope scope) => $"?termId={Segment(scope.TermId)}&classId={Segment(scope.ClassId)}&bindingId={Segment(scope.BindingId)}";

    public async Task<DeviceFieldState> ReadDeviceFieldStateAsync(DeviceFieldScope scope, CancellationToken ct)
    {
        var session = CurrentSession;
        var data = await RequestAsync<DeviceFieldState>(HttpMethod.Get, FieldPath(scope) + FieldQuery(scope), session.Token, ct, FieldVersion);
        CheckCreationSession(session, ct);
        VerifyFieldTarget(data.Target, scope);
        if (data.CurrentDevice is { } active && (!Guid.TryParseExact(active.DeviceId, "D", out _) || string.IsNullOrWhiteSpace(active.DeviceName)) ||
            data.Resume.ReviewToken is { } resume && (!StudyHash(resume) || data.Resume.Reason != "READY" || !StudyHash(data.Resume.Version) ||
                data.Resume.Mode is not ("Daily" or "Exam") || data.Resume.Window is not { } window || window.Start >= window.End || data.Resume.ObservedAt is null) ||
            data.Revoke.ReviewToken is { } revoke && !StudyHash(revoke) ||
            (data.Resume.ReviewToken is not null || data.Revoke.ReviewToken is not null) && (data.Target.State != "ACTIVE" || data.CurrentDevice?.DeviceId != scope.DeviceId))
            throw InvalidResponse();
        return data;
    }

    public async Task<DeviceFieldResult> SubmitDeviceFieldActionAsync(DeviceFieldRequest request, CancellationToken ct)
    {
        if (request.Id == Guid.Empty || request.ExpectedBindingRevision < 0 || !StudyHash(request.ReviewToken) ||
            request.Action is not ("RESUME" or "REVOKE")) throw new ArgumentException("请先核对这台设备当前的办理条件。");
        var session = CurrentSession;
        var scope = request.Scope;
        var body = new { requestId = request.Id, termId = scope.TermId, classId = scope.ClassId, bindingId = scope.BindingId,
            expectedBindingRevision = request.ExpectedBindingRevision, action = request.Action, reviewToken = request.ReviewToken };
        var data = ReadData<DeviceFieldResult>(await SendAsync(HttpMethod.Post, FieldPath(scope), session.Token, ct, FieldVersion, body, request.Id));
        CheckCreationSession(session, ct);
        return VerifyFieldResult(data, request);
    }

    public async Task<DeviceFieldResult?> ReadDeviceFieldResultAsync(DeviceFieldRequest request, CancellationToken ct)
    {
        var session = CurrentSession;
        try
        {
            string path = FieldPath(request.Scope) + $"/requests/{request.Id:D}" + FieldQuery(request.Scope);
            var data = await RequestAsync<DeviceFieldResult>(HttpMethod.Get, path, session.Token, ct, FieldVersion);
            CheckCreationSession(session, ct);
            return VerifyFieldResult(data, request);
        }
        catch (ConsoleApiException error) when (error is { StatusCode: 404, Code: "FIELD_ACTION_NOT_FOUND" })
        {
            CheckCreationSession(session, ct);
            return null;
        }
    }

    private static DeviceFieldResult VerifyFieldResult(DeviceFieldResult data, DeviceFieldRequest request)
    {
        VerifyFieldTarget(data.Target, request.Scope);
        if (data.RequestId != request.Id || data.Action != request.Action || data.Target.BindingRevision != request.ExpectedBindingRevision ||
            data.Action == "REVOKE" && (data.Outcome != "REVOKED" || data.Target.State != "REVOKED" || data.CommandId is not null ||
                data.ExpiresAt is not null || data.DeviceProcessedAt is not null || data.SampleAt is not null) ||
            data.Action == "RESUME" && (data.CommandId is null || data.ExpiresAt is null || data.Outcome is not ("QUEUED" or "ACCEPTED" or "REJECTED" or "UNKNOWN") ||
                data.SampleAt is not null && data.Outcome != "ACCEPTED") || data.SampleFresh && data.SampleAt is null ||
            data.SampleSessionId is { } capture && (capture == Guid.Empty || data.Action != "RESUME" || data.SampleAt is null)) throw InvalidResponse();
        return data;
    }

    private static void VerifyFieldTarget(DeviceFieldTarget target, DeviceFieldScope scope)
    {
        if (target.Scope != scope || !Guid.TryParseExact(target.DeviceId, "D", out _) || target.BindingRevision < 0 ||
            string.IsNullOrWhiteSpace(target.DeviceName)) throw InvalidResponse();
    }

    private static async Task<ConsoleApiException> DeviceFieldErrorAsync(int status, HttpResponseMessage response, string requestId, CancellationToken ct)
    {
        if (status == 426) return new(426, "此 KV 版本尚未提供现场处置。请升级服务后重试。", "FIELD_ACTION_UNSUPPORTED");
        Envelope? envelope = null;
        try { envelope = await ReadEnvelopeAsync(response.Content, ct); }
        catch (System.Text.Json.JsonException) { }
        string? code = envelope?.ProtocolVersion == FieldVersion && envelope.RequestId == requestId ? envelope.Error?.Code : null;
        return new(status, code switch
        {
            "FIELD_ACTION_CHANGED" => "待确认的设备或本场条件已变化。本次未办理，请重新核对。",
            "BINDING_CHANGED" => "原大屏的班级、学期或绑定已变化。请返回并重新定位。",
            "FIELD_ACTION_NOT_FOUND" => "未找到这个请求的本人回执。",
            "IDEMPOTENCY_CONFLICT" => "此请求编号已有不同的目标或内容，请重新核对。",
            "SCHOOL_ADMIN_REQUIRED" => "当前账号已无法办理本校现场处置。",
            _ => HttpError(status, false).Message
        }, code);
    }
}
