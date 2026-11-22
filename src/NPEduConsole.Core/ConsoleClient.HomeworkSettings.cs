namespace NPEduConsole.Core;

public sealed record HomeworkQuickInput(string Label, string Text, string Group, IReadOnlyList<string> SubjectIds, string InsertMode);
public sealed record HomeworkQuickDeadline(string Label, string Time, int? DayOffset = null, string? DateRule = null, int? Weekday = null);
public sealed record HomeworkSubject(string Id, string Name, string Code);
public sealed record HomeworkSettingsVersions(int QuickInputs, int QuickDeadlines);
public sealed record HomeworkSettingsDefaults(IReadOnlyList<HomeworkQuickInput> QuickInputs, IReadOnlyList<HomeworkQuickDeadline> QuickDeadlines);
public sealed record HomeworkSettings(string SchoolId, IReadOnlyList<HomeworkQuickInput> QuickInputs,
    IReadOnlyList<HomeworkQuickDeadline> QuickDeadlines, HomeworkSettingsVersions Versions, HomeworkSettingsDefaults Defaults,
    IReadOnlyList<HomeworkSubject> Subjects)
{
    internal Guid SessionId { get; init; }
}

public sealed record HomeworkSettingsReceipt(Guid RequestId, IReadOnlyList<string> Applied, HomeworkSettings Settings);

public sealed partial class ConsoleClient
{
    private const string HomeworkSettingsSuffix = "/homework-settings";
    private static string HomeworkSettingsPath(string schoolId) => $"api/v2/admin/schools/{Segment(schoolId)}{HomeworkSettingsSuffix}";
    private static bool IsHomeworkSettingsPath(string path) => path.StartsWith("api/v2/admin/schools/", StringComparison.Ordinal) &&
        (path.EndsWith(HomeworkSettingsSuffix, StringComparison.Ordinal) || path.Contains(HomeworkSettingsSuffix + "/receipts/", StringComparison.Ordinal));

    public async Task<HomeworkSettings> GetHomeworkSettingsAsync(string schoolId, CancellationToken ct)
    {
        var session = CurrentSession;
        var settings = await RequestAsync<HomeworkSettings>(HttpMethod.Get, HomeworkSettingsPath(schoolId), session.Token, ct);
        CheckCreationSession(session, ct);
        return ValidateHomeworkSettings(settings, schoolId, session.Id);
    }

    public async Task<HomeworkSettingsReceipt> SaveHomeworkSettingsAsync(HomeworkSettings baseline,
        IReadOnlyList<HomeworkQuickInput>? words, IReadOnlyList<HomeworkQuickDeadline>? deadlines, Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        if (session.Id != baseline.SessionId)
            throw new ConsoleApiException(409, "登录状态已改变，请重新读取本校作业设置。", "SNAPSHOT_CHANGED");
        var expectedVersions = new Dictionary<string, int>();
        var body = new Dictionary<string, object> { ["requestId"] = requestId, ["expectedVersions"] = expectedVersions };
        if (words is not null)
        {
            body["quickInputs"] = words;
            expectedVersions["quickInputs"] = baseline.Versions.QuickInputs;
        }
        if (deadlines is not null)
        {
            body["quickDeadlines"] = deadlines;
            expectedVersions["quickDeadlines"] = baseline.Versions.QuickDeadlines;
        }
        if (expectedVersions.Count == 0 || requestId == Guid.Empty) throw new ArgumentException("请选择有修改的分区。");
        var receipt = await RequestAsync<HomeworkSettingsReceipt>(HttpMethod.Put, HomeworkSettingsPath(baseline.SchoolId),
            session.Token, ct, body: body);
        CheckCreationSession(session, ct);
        if (receipt.Applied is null || receipt.Applied.Any(section => !expectedVersions.ContainsKey(section))) throw InvalidResponse();
        return ValidateHomeworkReceipt(receipt, requestId, baseline.SchoolId, session.Id);
    }

    public async Task<HomeworkSettingsReceipt?> GetHomeworkSettingsReceiptAsync(string schoolId, Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        try
        {
            var receipt = await RequestAsync<HomeworkSettingsReceipt>(HttpMethod.Get, $"{HomeworkSettingsPath(schoolId)}/receipts/{requestId:D}",
                session.Token, ct);
            CheckCreationSession(session, ct);
            return ValidateHomeworkReceipt(receipt, requestId, schoolId, session.Id);
        }
        catch (ConsoleApiException error) when (error is { StatusCode: 404, Code: "HOMEWORK_SETTINGS_RECEIPT_NOT_FOUND" })
        {
            CheckCreationSession(session, ct);
            return null;
        }
    }

    private static HomeworkSettingsReceipt ValidateHomeworkReceipt(HomeworkSettingsReceipt receipt, Guid requestId, string schoolId, Guid sessionId)
    {
        if (receipt.RequestId != requestId || receipt.Applied is null ||
            receipt.Applied.Any(section => section is not ("quickInputs" or "quickDeadlines")))
            throw InvalidResponse();
        return receipt with { Settings = ValidateHomeworkSettings(receipt.Settings, schoolId, sessionId) };
    }

    private static HomeworkSettings ValidateHomeworkSettings(HomeworkSettings settings, string schoolId, Guid sessionId)
    {
        if (settings.Versions is null) throw HomeworkSettingsError(428, "HOMEWORK_SETTINGS_BASELINE_REQUIRED");
        if (settings.SchoolId != schoolId || settings.Defaults is null || settings.Subjects is null ||
            settings.Versions is not { QuickInputs: >= 0, QuickDeadlines: >= 0 } ||
            !ValidRows(settings.QuickInputs, settings.QuickDeadlines) || !ValidRows(settings.Defaults.QuickInputs, settings.Defaults.QuickDeadlines) ||
            settings.Subjects.Any(subject => subject is null || string.IsNullOrWhiteSpace(subject.Id) || string.IsNullOrWhiteSpace(subject.Name)))
            throw InvalidResponse();
        return settings with { SessionId = sessionId };
    }

    private static bool ValidRows(IReadOnlyList<HomeworkQuickInput>? inputs, IReadOnlyList<HomeworkQuickDeadline>? deadlines) =>
        inputs is { Count: <= 64 } && inputs.All(row => row is { Label: not null, Text: not null, Group: not null,
            InsertMode: "INLINE" or "NEW_LINE", SubjectIds: not null } && row.SubjectIds.All(id => !string.IsNullOrWhiteSpace(id))) &&
        deadlines is { Count: >= 1 and <= 8 } && deadlines.All(row => row is { Label: not null, Time: not null } &&
            (row.DateRule == "next-weekday" ? row.Weekday is >= 0 and <= 6 : row.DayOffset is >= 0 and <= 14));

    private static ConsoleApiException HomeworkSettingsError(int status, string? code) => new(status, code switch
    {
        "HOMEWORK_SETTINGS_BASELINE_REQUIRED" => "KV 需要最新配置版本，请刷新后重新核对。旧版 KV 需要先升级。",
        "HOMEWORK_SETTINGS_CONFLICT" => "其他管理员已修改同一分区，本次未保存。请核对服务器当前配置。",
        "HOMEWORK_SETTINGS_INVALID" => "学校服务未接受配置，请检查截止时间和词条引用的学科。",
        "HOMEWORK_SETTINGS_REQUEST_CONFLICT" => "该保存编号已经用于不同操作，请核对原请求。",
        "HOMEWORK_SETTINGS_RECEIPT_NOT_FOUND" => "暂未查到本次操作的保存回执。",
        _ => HttpError(status, false).Message
    }, code);
}
