namespace NPEduConsole.Core;

public sealed record TeacherAccountAssignment(string TermId, string WorkspaceId, string WorkspaceName, string WorkspaceType,
    string SubjectId, string SubjectName, string Position)
{
    public string Label => $"{WorkspaceName} · {SubjectName} · {(Position == "PRIMARY" ? "主讲" : "协同")}";
}

public sealed record TeacherAccountDuty(string Kind, string TargetId, string TargetName, string Position)
{
    public string Label => $"{TargetName} · {Position switch
    {
        "HEAD_TEACHER" => "班主任", "CO_HEAD_TEACHER" => "协同班主任", "PRIMARY" => "年级组长", _ => "副组长"
    }}";
}

public sealed record ManagedTeacherAccount(string Id, string Name, string Username, bool Disabled, DateTimeOffset? LastLoginAt,
    string Version, IReadOnlyList<TeacherAccountAssignment> Assignments, IReadOnlyList<TeacherAccountDuty> Duties)
{
    public string Display => $"{Name}（{Username}）";
    public string StateLabel => Disabled ? "已停用" : "已启用";
}

public sealed record TeacherAccountDirectory(string SchoolId, string Mode, IReadOnlyList<ManagedTeacherAccount> Accounts,
    string? ActiveTermId = null, string? AdminUrl = null, string? SchoolCode = null)
{
    internal Guid SessionId { get; init; }
    public bool SupportsLocalWrites => Mode is "LOCAL_PIN" or "SHARED_PASSWORD";
    public bool UsesPin => Mode == "LOCAL_PIN";
}

public sealed record TeacherAccountReceipt(Guid RequestId, string Action, ManagedTeacherAccount Account, bool PinApplied = false);

public sealed partial class ConsoleClient
{
    private const string TeacherAccountSuffix = "/teacher-accounts";
    private static string TeacherAccountsPath(string schoolId) => $"api/v2/admin/schools/{Segment(schoolId)}{TeacherAccountSuffix}";
    private static bool IsTeacherAccountsPath(string path) => path.StartsWith("api/v2/admin/schools/", StringComparison.Ordinal) &&
        (path.EndsWith(TeacherAccountSuffix, StringComparison.Ordinal) || path.Contains(TeacherAccountSuffix + "/", StringComparison.Ordinal));

    private static ConsoleApiException TeacherAccountError(int status, string? code) => new(status, code switch
    {
        "TEACHER_USERNAME_EXISTS" => "本校已有此短账号。请查看原账号，或换用其他短账号。",
        "TEACHER_MODE_CHANGED" => "学校登录方式已改变，请刷新目录后重新核对。",
        "TEACHER_MODE_UNSUPPORTED" => "当前学校登录方式不支持此操作。",
        "TEACHER_PIN_NOT_ALLOWED" => "此操作无需个人 PIN。",
        "INVALID_TEACHER_PIN" => "个人 PIN 需为 4 至 8 位数字。",
        "INVALID_LOCAL_USERNAME" => "短账号为 2 至 32 位，以字母或数字开头，可用点、横线和下划线。",
        "INVALID_ACCOUNT_NAME" => "姓名需为 1 至 64 个字符。",
        "TEACHER_VERSION_CHANGED" => "这位教师的账号状态已变化，请刷新并核对当前值。",
        "TEACHER_ACCOUNT_NOT_FOUND" => "该账号已不在本校普通教师目录中，请使用对应管理入口核对。",
        "TEACHER_RECEIPT_NOT_FOUND" => "暂未找到本次操作的回执。",
        "TEACHER_REQUEST_CONFLICT" => "此请求编号已用于其他账号操作，请核对原请求。",
        "TEACHER_INPUT_INVALID" => "请核对账号、学校登录方式与操作。",
        _ when status is 404 or 405 or 426 => "当前 KV 暂不支持 Console 教师账号目录，请升级 KV 或在原网页办理。",
        _ => HttpError(status, false).Message
    }, code);

    public async Task<TeacherAccountDirectory> GetTeacherAccountsAsync(string schoolId, CancellationToken ct)
    {
        var session = CurrentSession;
        var directory = await RequestAsync<TeacherAccountDirectory>(HttpMethod.Get, TeacherAccountsPath(schoolId), session.Token, ct);
        CheckCreationSession(session, ct);
        if (directory.SchoolId != schoolId || directory.Mode is not ("LOCAL_PIN" or "SHARED_PASSWORD" or "OAUTH_EMAIL") ||
            directory.Accounts is null || directory.Accounts.Any(account => !ValidTeacherAccount(account)) ||
            directory.Accounts.Select(account => account.Id).Distinct().Count() != directory.Accounts.Count ||
            directory.Accounts.Any(account => account.Assignments.Any(row => row.TermId != directory.ActiveTermId))) throw InvalidResponse();
        return directory with { SessionId = session.Id };
    }

    public async Task<TeacherAccountReceipt> CreateTeacherAccountAsync(TeacherAccountDirectory directory, string username, string name,
        string? pin, Guid requestId, CancellationToken ct, bool requestPinDelivery = false)
    {
        var session = TeacherAccountSession(directory);
        username = username.Trim().ToLowerInvariant();
        name = name.Trim();
        if (!directory.SupportsLocalWrites) throw TeacherAccountError(409, "TEACHER_MODE_UNSUPPORTED");
        if (!System.Text.RegularExpressions.Regex.IsMatch(username, @"\A[a-z0-9][a-z0-9._-]{1,31}\z"))
            throw TeacherAccountError(422, "INVALID_LOCAL_USERNAME");
        if (name.Length is < 1 or > 64) throw TeacherAccountError(422, "INVALID_ACCOUNT_NAME");
        ValidateTeacherPin(pin, directory.UsesPin);
        object body = pin is null ? new { requestId, expectedMode = directory.Mode, username, name } :
            new { requestId, expectedMode = directory.Mode, username, name, pin, requestPinDelivery };
        var result = await RequestAsync<TeacherAccountReceipt>(HttpMethod.Post, TeacherAccountsPath(directory.SchoolId), session.Token, ct, body: body);
        CheckCreationSession(session, ct);
        ValidateTeacherReceipt(result, requestId, "CREATE");
        if (result.Account.Username != username) throw InvalidResponse();
        return result;
    }

    public async Task<TeacherAccountReceipt> UpdateTeacherAccountAsync(TeacherAccountDirectory directory, string accountId, string action,
        string? pin, Guid requestId, CancellationToken ct, bool requestPinDelivery = false)
    {
        var session = TeacherAccountSession(directory);
        var baseline = directory.Accounts.SingleOrDefault(account => account.Id == accountId) ?? throw TeacherAccountError(404, "TEACHER_ACCOUNT_NOT_FOUND");
        if (action is not ("DISABLE" or "RESTORE" or "RESET_PIN")) throw TeacherAccountError(422, "TEACHER_INPUT_INVALID");
        if (!directory.SupportsLocalWrites || action == "RESET_PIN" && !directory.UsesPin) throw TeacherAccountError(409, "TEACHER_MODE_UNSUPPORTED");
        ValidateTeacherPin(pin, action == "RESET_PIN");
        object body = pin is null ? new { requestId, expectedMode = directory.Mode, expectedVersion = baseline.Version, action } :
            new { requestId, expectedMode = directory.Mode, expectedVersion = baseline.Version, action, pin, requestPinDelivery };
        var result = await RequestAsync<TeacherAccountReceipt>(HttpMethod.Patch, $"{TeacherAccountsPath(directory.SchoolId)}/{Segment(accountId)}",
            session.Token, ct, body: body);
        CheckCreationSession(session, ct);
        ValidateTeacherReceipt(result, requestId, action);
        if (result.Account.Id != accountId) throw InvalidResponse();
        return result;
    }

    public async Task<TeacherAccountReceipt?> GetTeacherAccountReceiptAsync(string schoolId, Guid requestId, string action, CancellationToken ct)
    {
        var session = CurrentSession;
        try
        {
            var result = await RequestAsync<TeacherAccountReceipt>(HttpMethod.Get, $"{TeacherAccountsPath(schoolId)}/receipts/{requestId:D}", session.Token, ct);
            CheckCreationSession(session, ct);
            ValidateTeacherReceipt(result, requestId, action);
            return result;
        }
        catch (ConsoleApiException error) when (error is { StatusCode: 404, Code: "TEACHER_RECEIPT_NOT_FOUND" })
        {
            CheckCreationSession(session, ct);
            return null;
        }
    }

    private Session TeacherAccountSession(TeacherAccountDirectory directory)
    {
        var session = CurrentSession;
        if (directory.SessionId != session.Id) throw new ConsoleApiException(409, "账号会话已变化，请重新打开教师目录。", "SNAPSHOT_CHANGED");
        return session;
    }

    private static void ValidateTeacherReceipt(TeacherAccountReceipt result, Guid requestId, string action)
    {
        if (result.RequestId != requestId || result.Action != action || !ValidTeacherAccount(result.Account)) throw InvalidResponse();
    }

    private static bool ValidTeacherAccount(ManagedTeacherAccount account) => account is not null && !string.IsNullOrWhiteSpace(account.Id) &&
        !string.IsNullOrWhiteSpace(account.Name) && !string.IsNullOrWhiteSpace(account.Username) &&
        account.Version is { Length: 64 } && account.Version.All(char.IsAsciiHexDigitLower) && account.Assignments is not null &&
        account.Assignments.All(row => row is not null && !string.IsNullOrWhiteSpace(row.WorkspaceId) && !string.IsNullOrWhiteSpace(row.SubjectId)) &&
        account.Duties is not null && account.Duties.All(row => row is not null && !string.IsNullOrWhiteSpace(row.TargetId) && row.Kind is "classes" or "grades");

    private static void ValidateTeacherPin(string? pin, bool required)
    {
        if (!required && pin is not null) throw TeacherAccountError(422, "TEACHER_PIN_NOT_ALLOWED");
        if (required && (pin is null || pin.Length is < 4 or > 8 || pin.Any(character => !char.IsAsciiDigit(character))))
            throw TeacherAccountError(422, "INVALID_TEACHER_PIN");
    }
}
