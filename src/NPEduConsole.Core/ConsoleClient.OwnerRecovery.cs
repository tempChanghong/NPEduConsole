namespace NPEduConsole.Core;

public sealed record OwnerRecoverySchool(string Id, string Code, string Name);

public sealed partial class ConsoleClient
{
    private const string OwnerRecoveryPath = "accounts/local/recover-owner";

    public async Task<IReadOnlyList<OwnerRecoverySchool>> GetOwnerRecoverySchoolsAsync(CancellationToken ct)
    {
        var rows = RequireNonNullRows(await RequestAsync<OwnerRecoverySchool[]>(HttpMethod.Get, "api/v2/catalog/schools", null, ct));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Id) || string.IsNullOrWhiteSpace(row.Code) || string.IsNullOrWhiteSpace(row.Name) ||
                !ids.Add(row.Id) || !codes.Add(row.Code.Trim())) throw InvalidResponse();
        }
        return rows;
    }

    public async Task RecoverLocalOwnerAsync(string schoolCode, string username, string setupKey, string newPin, CancellationToken ct)
    {
        if (_session is not null) throw new ConsoleApiException(0, "请从登录页办理所有者恢复。");
        if (string.IsNullOrWhiteSpace(schoolCode) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(setupKey))
            throw new ConsoleApiException(400, "请核对学校代码、所有者短账号和服务器恢复密钥。");
        ValidateTeacherPin(newPin, true);
        ct.ThrowIfCancellationRequested();
        try
        {
            await SendAsync(HttpMethod.Post, OwnerRecoveryPath, null, ct,
                body: new { schoolCode = schoolCode.Trim(), username = username.Trim(), setupKey, newPin });
        }
        catch (ConsoleApiException error) when (error.StatusCode == 0 && error.Code != "OWNER_RECOVERY_UNCONFIRMED")
        {
            throw UnconfirmedOwnerRecovery();
        }
        catch (OperationCanceledException) { throw UnconfirmedOwnerRecovery(); }
    }

    private static ConsoleApiException UnconfirmedOwnerRecovery() => new(0,
        "本次恢复结果待核对。请返回登录，用刚填写的新 PIN 核对原校账号。", "OWNER_RECOVERY_UNCONFIRMED");

    private static ConsoleApiException OwnerRecoveryError(int status, string? code) => (status, code) switch
    {
        (401, "INVALID_RECOVERY_KEY") => new(status, "服务器恢复密钥不正确，请向本服务的部署人员核对。", code),
        (404, "LOCAL_OWNER_NOT_FOUND") => new(status, "当前学校和短账号未匹配到本地学校所有者。", code),
        (400, "INVALID_TEACHER_PIN") => new(status, "新 PIN 需为 4 至 8 位数字。", code),
        (429, _) => new(status, "请求过于频繁，请稍后再办理。", "OWNER_RECOVERY_RATE_LIMITED"),
        (404, null or "NOT_FOUND") or (405, _) => new(status, "该地址未提供恢复接口，请核对 KV 服务地址和版本。", "OWNER_RECOVERY_UNAVAILABLE"),
        _ => UnconfirmedOwnerRecovery()
    };
}
