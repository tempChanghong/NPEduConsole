namespace NPEduConsole.Core;

public sealed record ScreenDutyRoom(string Id, string Name, string Code, string? GradeName, string TermId, string TermName, bool Active)
{
    public string Label => string.IsNullOrWhiteSpace(GradeName) || Name.StartsWith(GradeName, StringComparison.Ordinal)
        ? Name : $"{GradeName} · {Name}";
    public override string ToString() => $"{Label}（{Code}）";
}

public sealed record ScreenDutyBinding(string Id, string Name, string? LoginCode, bool HasPin, bool Active, bool BrowserBound, ScreenDutyRoom Room);
public sealed record ScreenDutyCommand(string Id, string Type, string State, DateTimeOffset CreatedAt, DateTimeOffset? DeliveredAt,
    DateTimeOffset? AcknowledgedAt, DateTimeOffset ExpiresAt);
public sealed record ScreenDutyDetail(string Id, string Name, string? LoginCode, bool HasPin, bool Active, bool BrowserBound, ScreenDutyRoom Room,
    string BrowserState, DateTimeOffset? FirstLoginAt, DateTimeOffset? HeartbeatAt, DateTimeOffset? PreviousHeartbeatAt,
    int? PendingUploads, bool SyncProblem, string NativeState, DateTimeOffset? NativeReportedAt, DateTimeOffset? NativePairedAt,
    ScreenDutyCommand[] Commands, int? CredentialVersion = null)
{
    public string Display => $"{Room.Label} · {Name} · {Id}{(Active ? "" : " · 已停用")}";
}
public sealed record ScreenDutyCatalog(string SchoolId, string? TermId, ScreenDutyRoom[] Rooms, ScreenDutyDetail[] Screens, string? SchoolCode = null);
public sealed record ScreenDutyPlan(string Action, string? BindingId = null, string? ClassId = null, string? Name = null,
    string? LoginCode = null, string? CommandType = null);
public sealed record ScreenDutyPeer(string Id, string Name, bool Active);
public sealed record ScreenDutyImpact(string Browser, string Native);
public sealed record ScreenDutyPreview(string Version, string Action, ScreenDutyBinding? Screen, ScreenDutyRoom? Target,
    ScreenDutyPeer[] TargetScreens, ScreenDutyImpact Impact);
public sealed record ScreenDutyReceipt(Guid RequestId, string Action, string BindingId, string? CommandId, int? AppliedPinVersion = null);

public sealed partial class ConsoleClient
{
    private static string ScreenDutyPath(string schoolId) => $"api/v2/admin/schools/{Segment(schoolId)}/screen-duty";
    private static bool IsScreenDutyPath(string path) => path.StartsWith("api/v2/admin/schools/", StringComparison.Ordinal) &&
        (path.EndsWith("/screen-duty", StringComparison.Ordinal) || path.Contains("/screen-duty/", StringComparison.Ordinal) ||
         path.Contains("/screen-duty?", StringComparison.Ordinal));

    private static ConsoleApiException ScreenDutyError(int status, string? code) => new(status, code switch
    {
        "SCREEN_DUTY_STALE" => "大屏、网页凭据或目标班级已有变化，请重新核对。本次未写入。",
        "SCREEN_DUTY_CLASS_CHANGED" => "大屏或目标班级已不在当前启用班级，请重新核对。未匹配的大屏请使用学期安排入口。",
        "SCREEN_DUTY_TARGET_INVALID" => "请选择准确的大屏和目标班级，调班时请选择与原班不同的班级。",
        "SCREEN_DUTY_INACTIVE" => "大屏已停用，可先核对并恢复原大屏。",
        "SCREEN_DUTY_ALREADY_ACTIVE" => "大屏已经启用，请核对最新状态。",
        "SCREEN_DUTY_ACCOUNT_EXISTS" => "网页账号已配置，可修改短账号或重置网页 PIN。",
        "SCREEN_DUTY_ACCOUNT_REQUIRED" => "请先配置本屏的网页账号。",
        "SCREEN_DUTY_LOGIN_UNCHANGED" => "短账号未变，无需保存。",
        "SCREEN_DUTY_REQUEST_CONFLICT" => "该请求编号已用于其他操作，请核对设备状态。",
        "SCREEN_DUTY_PREVIEW_REQUIRED" => "请先核对办理影响。",
        "SCREEN_DUTY_RECEIPT_NOT_FOUND" => "暂未找到本次大屏操作的回执。",
        "SCREEN_LOGIN_CODE_EXISTS" => "本校已使用该网页短账号，请换一个短账号。",
        "SCREEN_LOGIN_CODE_INVALID" => "请输入 3 至 32 位字母、数字、点、横线或下划线，首位为字母或数字。",
        "SCREEN_NAME_INVALID" => "请输入 1 至 191 字的大屏名称。",
        "SCREEN_PIN_INVALID" => "网页 PIN 需为 4 至 8 位数字。",
        "SCREEN_BINDING_NOT_FOUND" => "未找到所选大屏，请刷新设备目录。",
        _ => HttpError(status, false).Message
    }, code);

    public async Task<ScreenDutyCatalog> GetScreenDutyAsync(string schoolId, string? bindingId, CancellationToken ct)
    {
        var session = CurrentSession;
        var path = ScreenDutyPath(schoolId) + (bindingId is null ? "" : "?bindingId=" + Segment(bindingId));
        var data = await RequestAsync<ScreenDutyCatalog>(HttpMethod.Get, path, session.Token, ct);
        CheckCreationSession(session, ct);
        if (data.SchoolId != schoolId || data.Rooms.Any(room => room.TermId != data.TermId || !room.Active) ||
            bindingId is not null && (data.Screens.Length != 1 || data.Screens[0].Id != bindingId) ||
            bindingId is null && data.Screens.Any(screen => screen.Room.TermId != data.TermId))
            throw InvalidResponse();
        return data;
    }

    public async Task<ScreenDutyPreview> PreviewScreenDutyAsync(string schoolId, ScreenDutyPlan plan, CancellationToken ct)
    {
        var session = CurrentSession;
        var data = await RequestAsync<ScreenDutyPreview>(HttpMethod.Post, ScreenDutyPath(schoolId) + "/preview", session.Token, ct, body: plan);
        CheckCreationSession(session, ct);
        CheckScreenDutyPreview(plan, data);
        return data;
    }

    public async Task<ScreenDutyReceipt> CommitScreenDutyAsync(string schoolId, ScreenDutyPlan plan, ScreenDutyPreview preview,
        string? pin, Guid requestId, CancellationToken ct)
    {
        CheckScreenDutyPreview(plan, preview);
        var session = CurrentSession;
        var receipt = await RequestAsync<ScreenDutyReceipt>(HttpMethod.Post, ScreenDutyPath(schoolId), session.Token, ct,
            body: new {plan.Action, plan.BindingId, plan.ClassId, plan.Name, plan.LoginCode, plan.CommandType, pin, requestId, requestPinDelivery = pin is not null, expectedVersion = preview.Version});
        CheckCreationSession(session, ct);
        CheckScreenDutyReceipt(plan, requestId, receipt);
        return receipt;
    }

    public async Task<ScreenDutyReceipt?> GetScreenDutyReceiptAsync(string schoolId, ScreenDutyPlan plan, Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        try
        {
            var receipt = await RequestAsync<ScreenDutyReceipt>(HttpMethod.Get, $"{ScreenDutyPath(schoolId)}/receipts/{requestId:D}", session.Token, ct);
            CheckCreationSession(session, ct);
            CheckScreenDutyReceipt(plan, requestId, receipt);
            return receipt;
        }
        catch (ConsoleApiException error) when (error is {StatusCode: 404, Code: "SCREEN_DUTY_RECEIPT_NOT_FOUND"})
        {
            CheckCreationSession(session, ct);
            return null;
        }
    }

    private static void CheckScreenDutyPreview(ScreenDutyPlan plan, ScreenDutyPreview preview)
    {
        if (preview.Action != plan.Action || preview.Screen?.Id != plan.BindingId || preview.Target?.Id != plan.ClassId ||
            preview.Version.Length != 64 || !preview.Version.All(Uri.IsHexDigit))
            throw InvalidResponse();
    }

    private static void CheckScreenDutyReceipt(ScreenDutyPlan plan, Guid requestId, ScreenDutyReceipt receipt)
    {
        if (receipt.Action != plan.Action || receipt.RequestId != requestId || string.IsNullOrWhiteSpace(receipt.BindingId) ||
            plan.BindingId is not null && receipt.BindingId != plan.BindingId ||
            (plan.Action == "COMMAND") != !string.IsNullOrWhiteSpace(receipt.CommandId))
            throw InvalidResponse();
    }
}
