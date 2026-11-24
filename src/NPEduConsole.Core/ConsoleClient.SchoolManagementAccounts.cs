using System.Text.Json;

namespace NPEduConsole.Core;

public sealed record ManagementWorkspace(string Id, string Name, string Code, string Role);

public sealed record SchoolManagementAccount(string Id, string Name, string? Username, string? Email, string Source,
    string? Role, bool Disabled, DateTimeOffset? LastLoginAt, DateTimeOffset? ManagementLoginAt,
    string? ManagementLoginMethod, bool AwaitsManagementLogin, bool HasPersonalPin, ManagementWorkspace[]? CurrentWorkspaces)
{
    public string Display => $"{Name}（{Username ?? Email ?? Id}）";
    public bool IsManagement => Role is "OWNER" or "ADMIN";
}

public sealed record SchoolManagementDirectory(string SchoolId, string ActorId, string ActorRole, SchoolManagementAccount[] Accounts,
    string? SchoolCode = null)
{
    internal Guid SessionId { get; init; }
}

public sealed record SchoolManagementCommand(string Action, string? AccountId = null, string? Username = null,
    string? Name = null, string? Role = null);

public sealed record SchoolManagementPreview(string Fingerprint, string Title, string[] Consequences, bool CanConfirm,
    string? BlockedCode, bool NeedsPin);

public sealed record SchoolManagementPinProof(string AccountId, string Username, int CredentialVersion);

public sealed record SchoolManagementReceipt(Guid RequestId, string AccountId, string Action, string? Role, bool Replayed = false,
    SchoolManagementPinProof? CredentialDelivery = null);

public sealed partial class ConsoleClient
{
    private static string SchoolManagementPath(string school) => $"api/v2/admin/schools/{Segment(school)}/management-accounts";
    private static bool IsSchoolManagementAccountsPath(string path) => path.StartsWith("api/v2/admin/schools/", StringComparison.Ordinal) &&
        (path.EndsWith("/management-accounts", StringComparison.Ordinal) || path.Contains("/management-accounts/", StringComparison.Ordinal));

    public async Task<SchoolManagementDirectory> GetSchoolManagementAccountsAsync(string school, CancellationToken ct)
    {
        var session = CurrentSession;
        var result = await RequestAsync<SchoolManagementDirectory>(HttpMethod.Get, SchoolManagementPath(school), session.Token, ct);
        CheckCreationSession(session, ct);
        if (result.SchoolId != school || result.ActorRole is not ("OWNER" or "ADMIN") ||
            result.Accounts.Any(row => row is null || row.Id.Length == 0 || row.Source is not ("LOCAL" or "OAUTH")) ||
            !result.Accounts.Any(row => row.Id == result.ActorId) || result.Accounts.DistinctBy(row => row.Id).Count() != result.Accounts.Length)
            throw InvalidResponse();
        return result with { SessionId = session.Id };
    }

    public async Task<SchoolManagementPreview> PreviewSchoolManagementAccountAsync(SchoolManagementDirectory directory,
        SchoolManagementCommand command, CancellationToken ct)
    {
        var session = CheckManagementDirectory(directory);
        var value = await RequestAsync<SchoolManagementPreview>(HttpMethod.Post, SchoolManagementPath(directory.SchoolId) + "/preview",
            session.Token, ct, body: ManagementPayload(command));
        CheckCreationSession(session, ct);
        if (value.Fingerprint.Length != 64 || !value.Fingerprint.All(char.IsAsciiHexDigitLower) ||
            string.IsNullOrWhiteSpace(value.Title) || value.Consequences.Length == 0 || value.CanConfirm != (value.BlockedCode is null))
            throw InvalidResponse();
        return value;
    }

    public async Task<SchoolManagementReceipt> SaveSchoolManagementAccountAsync(SchoolManagementDirectory directory, SchoolManagementCommand command,
        SchoolManagementPreview preview, Guid requestId, string? pin, CancellationToken ct)
    {
        var session = CheckManagementDirectory(directory);
        if (!preview.CanConfirm || requestId == Guid.Empty) throw new ArgumentException("请先核对本次变更影响。");
        ValidateTeacherPin(pin, preview.NeedsPin);
        var body = ManagementPayload(command);
        body["requestId"] = requestId;
        body["expectedFingerprint"] = preview.Fingerprint;
        if (pin is not null)
        {
            body["pin"] = pin;
            if (!string.IsNullOrWhiteSpace(directory.SchoolCode)) body["requestCredentialDelivery"] = true;
        }
        var value = await RequestAsync<SchoolManagementReceipt>(HttpMethod.Post, SchoolManagementPath(directory.SchoolId), session.Token, ct, body: body);
        CheckCreationSession(session, ct);
        var receipt = VerifyManagementReceipt(value, requestId, command);
        if (receipt.CredentialDelivery is { } proof && (receipt.Replayed || pin is null || proof.AccountId != receipt.AccountId ||
            string.IsNullOrWhiteSpace(proof.Username) || proof.CredentialVersion < 1 ||
            command.Username is { } expected && !string.Equals(expected.Trim(), proof.Username, StringComparison.OrdinalIgnoreCase)))
            throw InvalidResponse();
        return receipt;
    }

    public async Task<SchoolManagementReceipt?> GetSchoolManagementReceiptAsync(string school, SchoolManagementCommand command,
        Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        try
        {
            var receipt = await RequestAsync<SchoolManagementReceipt>(HttpMethod.Get, SchoolManagementPath(school) + $"/requests/{requestId:D}", session.Token, ct);
            CheckCreationSession(session, ct);
            return VerifyManagementReceipt(receipt, requestId, command) with { CredentialDelivery = null };
        }
        catch (ConsoleApiException error) when (error is {StatusCode: 404, Code: "MANAGEMENT_ACCOUNT_RECEIPT_NOT_FOUND"})
        {
            CheckCreationSession(session, ct);
            return null;
        }
    }

    public async Task VerifySchoolManagementPinDeliveryAsync(SchoolManagementDirectory directory, Guid requestId,
        SchoolManagementPinProof proof, CancellationToken ct)
    {
        var session = CheckManagementDirectory(directory);
        var path = SchoolManagementPath(directory.SchoolId) + $"/requests/{requestId:D}/pin-delivery";
        var current = await RequestAsync<SchoolManagementPinProof>(HttpMethod.Get, path, session.Token, ct);
        CheckCreationSession(session, ct);
        if (current != proof) throw new ConsoleApiException(409, "账号或凭据已变更。需要新 PIN 时请另行重置。", "MANAGEMENT_PIN_DELIVERY_CHANGED");
    }

    public async Task ChangeOwnSchoolPinAsync(string currentPin, string newPin, CancellationToken ct)
    {
        var session = CurrentSession;
        ValidateTeacherPin(newPin, true);
        if (currentPin.Length is < 4 or > 8 || !currentPin.All(char.IsAsciiDigit))
            throw new ArgumentException("请输入当前的个人 PIN。");
        await SendAsync(HttpMethod.Post, "accounts/local/change-pin", session.Token, ct, body: new {currentPin, newPin});
        CheckCreationSession(session, ct);
    }

    private Session CheckManagementDirectory(SchoolManagementDirectory directory)
    {
        var session = CurrentSession;
        if (session.Id != directory.SessionId) throw new OperationCanceledException();
        return session;
    }

    private static Dictionary<string, object> ManagementPayload(SchoolManagementCommand command)
    {
        var body = new Dictionary<string, object> { ["action"] = command.Action };
        if (command.AccountId is { } id) body["accountId"] = id;
        if (command.Username is { } username) body["username"] = username;
        if (command.Name is { } name) body["name"] = name;
        if (command.Role is { } role) body["role"] = role;
        return body;
    }

    private static SchoolManagementReceipt VerifyManagementReceipt(SchoolManagementReceipt value, Guid requestId, SchoolManagementCommand command)
    {
        if (value.RequestId != requestId || value.Action != command.Action || string.IsNullOrEmpty(value.AccountId) ||
            command.AccountId is { } account && value.AccountId != account || command.Role is { } role && value.Role != role)
            throw InvalidResponse();
        return value;
    }

    private static async Task<ConsoleApiException> SchoolManagementAccountsErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        string? code = null, related = null;
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (document.RootElement is {ValueKind: JsonValueKind.Object} root)
            {
                if (root.TryGetProperty("code", out var kind) && kind.ValueKind == JsonValueKind.String) code = kind.GetString();
                if (root.TryGetProperty("details", out var detail) && detail.ValueKind == JsonValueKind.Object &&
                    detail.TryGetProperty("accountId", out var id) && id.ValueKind == JsonValueKind.String) related = id.GetString();
            }
        }
        catch (JsonException) { }
        return new ConsoleApiException(status, code switch
        {
            "SCHOOL_OWNER_REQUIRED" => "只有本校所有者可以办理管理账号和权限交接。",
            "SCHOOL_ADMIN_REQUIRED" => "当前会话已无本校管理权限，请核对学校或重新登录。",
            "LOCAL_USERNAME_EXISTS" => "该短账号已经存在。可打开原账号另行授权，或改用其他短账号。",
            "OWNER_SUCCESSOR_NOT_READY" => "请先让启用的接任所有者在获得角色后重新登录。",
            "LAST_SCHOOL_OWNER" => "请先增设接任所有者并让其重新登录。",
            "MANAGEMENT_ACCOUNT_PREVIEW_CHANGED" => "学校权限或账号状态已经变化。本次未保存，请重新核对。",
            "MANAGEMENT_ACCOUNT_RECEIPT_NOT_FOUND" => "暂未找到本人本次操作的回执。",
            "MANAGEMENT_ACCOUNT_REQUEST_CONFLICT" => "此编号已经用于另一操作。",
            "MANAGEMENT_TARGET_DISABLED" => "请先恢复账号登录，再授予管理权。",
            "CANNOT_DISABLE_SELF" => "当前账号不可自行停用或注销。本人改 PIN 须验证原 PIN。",
            "MANAGEMENT_ACCOUNT_UNCHANGED" => "账号已经是所选状态。请刷新目录。",
            "INVALID_TEACHER_PIN" => "请输入 4 至 8 位数字 PIN。",
            "INVALID_MANAGEMENT_ACCOUNT_TARGET" => "请核对短账号（2 至 32 位）与目标角色。",
            "INVALID_ACCOUNT_NAME" => "姓名需为 1 至 64 个字符。",
            "MANAGEMENT_ACCOUNT_NOT_FOUND" => "该账号已不在本校目录。",
            "MANAGEMENT_PIN_DELIVERY_CHANGED" => "账号或凭据已变更。需要新 PIN 时请另行重置。",
            null when status == 404 => "当前 KV 版本暂无账号与权限入口，请先更新 KV。",
            _ => HttpError(status, false).Message
        }, code) { RelatedAccountId = related };
    }
}
