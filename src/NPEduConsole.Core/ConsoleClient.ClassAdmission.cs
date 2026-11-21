using System.Globalization;
using System.Text;

namespace NPEduConsole.Core;

public sealed class ClassAdmission
{
    internal ClassAdmission(bool configured, bool enabled, long revision, ClassAdmissionScope scope)
    {
        Configured = configured;
        Enabled = enabled;
        Revision = revision;
        Scope = scope;
    }

    public bool Configured { get; }
    public bool Enabled { get; }
    public long Revision { get; }
    internal ClassAdmissionScope Scope { get; }
    public bool IsReady => Configured && Enabled;
    public bool SupportsGuardedSave => Scope is { BindingRevision: not null, PairingRevision: not null };

    public static bool IsValidSecret(string secret)
    {
        if (secret.Length is < 8 or > 128) return false;
        foreach (var rune in secret.EnumerateRunes())
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format) return false;
        return true;
    }
}

internal sealed record ClassAdmissionScope(Guid SessionId, string SchoolId, string ClassId, string ScreenId,
    long? BindingRevision = null, long? PairingRevision = null);

public sealed partial class ConsoleClient
{
    private sealed record FirstScreenResponse(string SchoolId, string TermId, string ClassId, string ScreenBindingId);

    public async Task<string> PrepareFirstScreenAsync(Classroom room, Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        if (!room.CanPrepareFirstScreen || room.FirstScreen is not { } scope || scope.SessionId != session.Id)
            throw new ConsoleApiException(409, "请在当前学校重新选择尚无大屏记录的班级。", "SNAPSHOT_CHANGED");
        if (requestId == Guid.Empty) throw new ArgumentException("缺少登记请求编号。", nameof(requestId));
        var path = $"api/v2/npep/schools/{Segment(scope.SchoolId)}/classes/{Segment(scope.ClassId)}/first-screen";
        try
        {
            var envelope = await SendAsync(HttpMethod.Post, path, session.Token, ct, "0.16",
                new { requestId, scope.TermId }, requestId);
            var result = ReadData<FirstScreenResponse>(envelope);
            ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(session, _session)) throw new ConsoleApiException(401, "登录会话已改变，请重新登录。");
            if (result.SchoolId != scope.SchoolId || result.TermId != scope.TermId || result.ClassId != scope.ClassId)
                throw InvalidResponse();
            _ = Segment(result.ScreenBindingId);
            return result.ScreenBindingId;
        }
        catch (ConsoleApiException error) when (error.StatusCode is 404 or 426)
        {
            throw new ConsoleApiException(error.StatusCode, "请核对此班级和 KV 版本，当前服务未能办理首台大屏登记。", "FIRST_SCREEN_UNSUPPORTED");
        }
    }

    public Task<ClassAdmission> GetClassAdmissionAsync(string schoolId, string classId, string screenId, CancellationToken ct) =>
        ReadClassAdmissionAsync(CurrentSession, schoolId, classId, screenId, ct);

    private async Task<ClassAdmission> ReadClassAdmissionAsync(Session session, string schoolId, string classId, string screenId, CancellationToken ct)
    {
        var status = await RequestAsync<ClassAdmissionResponse>(HttpMethod.Get, ClassAdmissionPath(schoolId, screenId), session.Token, ct, version: "0.16");
        return ClassAdmissionSnapshot(status, new(session.Id, schoolId, classId, screenId));
    }

    public async Task<ClassAdmission> EnableClassAdmissionAsync(ClassAdmission snapshot, string? secret, Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        var scope = snapshot.Scope;
        if (scope.SessionId != session.Id)
            throw new ConsoleApiException(409, "请在当前账号下重新读取这台大屏的接入设置。", "SNAPSHOT_CHANGED");
        if (scope.BindingRevision is not { } bindingRevision || scope.PairingRevision is not { } pairingRevision)
            throw new ConsoleApiException(426, "当前学校服务仅支持查看接入设置。请先更新学校服务，再保存。", "ADMISSION_GUARD_UNAVAILABLE");
        if (snapshot.IsReady || snapshot.Configured != (secret is null) ||
            secret is not null && !ClassAdmission.IsValidSecret(secret) || requestId == Guid.Empty)
            throw new ConsoleApiException(400, "请核对大屏接入状态和班级管理口令。", "INVALID_CLASS_ADMISSION");

        try
        {
            var body = new
            {
                requestId, expectedRevision = snapshot.Revision, enabled = true, secret,
                expectedClassId = scope.ClassId, expectedBindingRevision = bindingRevision, expectedPairingRevision = pairingRevision,
                requireUnpaired = true
            };
            var envelope = await SendAsync(HttpMethod.Post, ClassAdmissionPath(scope.SchoolId, scope.ScreenId),
                session.Token, ct, "0.16", body, requestId);
            var saved = ClassAdmissionSnapshot(ReadData<ClassAdmissionResponse>(envelope), scope);
            if (!saved.IsReady || saved.Revision != snapshot.Revision + (secret is null ? 0 : 1) ||
                saved.Scope.BindingRevision != bindingRevision || saved.Scope.PairingRevision != pairingRevision + 1)
                throw InvalidResponse();
            return saved;
        }
        catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or >= 500 || error.Code == "BINDING_RESPONSE_CHANGED")
        {
            throw new ConsoleApiException(error.StatusCode, "学校是否保存本次设置尚未确认。请先读取当前接入状态。", "ADMISSION_UNCONFIRMED");
        }
    }

    private static ClassAdmission ClassAdmissionSnapshot(ClassAdmissionResponse status, ClassAdmissionScope scope)
    {
        if (status.ScreenBindingId != scope.ScreenId || status.ClassId != scope.ClassId)
            throw new ConsoleApiException(409, "大屏归属已变化，请刷新班级列表。", "BINDING_RESPONSE_CHANGED");
        if (status.Revision < 0 || status.Configured != (status.Revision > 0) ||
            status.BindingRevision < 0 || status.PairingRevision < 0) throw InvalidResponse();
        return new(status.Configured, status.Enabled, status.Revision,
            scope with { BindingRevision = status.BindingRevision, PairingRevision = status.PairingRevision });
    }

    private static string ClassAdmissionPath(string schoolId, string screenId) =>
        $"api/v2/npep/schools/{Segment(schoolId)}/class-management/{Segment(screenId)}";

    private sealed record ClassAdmissionResponse(bool Configured, bool Enabled, long Revision, string ScreenBindingId, string ClassId,
        long? BindingRevision = null, long? PairingRevision = null);
}
