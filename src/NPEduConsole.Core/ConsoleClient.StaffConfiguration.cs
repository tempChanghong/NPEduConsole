using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NPEduConsole.Core;

public sealed record StaffConfigurationTerm(string Id, string Name, string Status, int AcademicYear, int Semester,
    DateTimeOffset? StartsAt, DateTimeOffset? EndsAt);
public sealed record StaffConfigurationScope(string SchoolId, string SchoolCode, string Mode, StaffConfigurationTerm? Term);
public sealed record StaffConfigurationChange(string Kind, string? TargetId, string? SubjectId, string Label,
    string? Before, string? After, bool Changed);
public sealed record StaffConfigurationTeacher(int Index, string Username, string Name, string? AccountId, StaffConfigurationChange[] Changes);
public sealed record StaffConfigurationIssue(string Path, string Code, string Message, int? TeacherIndex, string? Kind,
    string? TargetId, string? SubjectId);
public sealed record StaffConfigurationPreview(string Version, bool CanApply, string SchoolId, string TermId,
    StaffConfigurationTeacher[] Teachers, StaffConfigurationIssue[] Issues, StaffConfigurationIssue[] Warnings)
{
    internal Guid SessionId { get; init; }
}
public sealed record StaffConfigurationSavedTeacher(string Username, string Name, string AccountId, bool Created,
    StaffConfigurationChange[]? Changes = null);
public sealed record StaffConfigurationReceipt(Guid RequestId, string SchoolId, string TermId, StaffConfigurationSavedTeacher[] Teachers);
public sealed class StaffConfigurationAppliedPin
{
    public required string AccountId { get; init; }
    public required string Username { get; init; }
    public required string Pin { get; init; }
    public required int Version { get; init; }
}
public sealed class StaffConfigurationSave
{
    public required string State { get; init; }
    public required StaffConfigurationReceipt Receipt { get; init; }
    public StaffConfigurationAppliedPin[]? AppliedPins { get; init; }
}
public sealed record StaffConfigurationCancellation(string State, StaffConfigurationReceipt? Receipt = null,
    Guid? RequestId = null, string? SchoolId = null, string? TermId = null);

// Keep the document in the originating page only. Never format or serialize this object for navigation, logs, or recovery.
public sealed class StaffConfigurationDocument
{
    private StaffConfigurationDocument(JsonElement json, string? schoolCode, int? year, int? semester, int count)
    {
        Json = json;
        SchoolCode = schoolCode;
        AcademicYear = year;
        Semester = semester;
        TeacherCount = count;
    }

    internal JsonElement Json { get; }
    public string? SchoolCode { get; }
    public int? AcademicYear { get; }
    public int? Semester { get; }
    public int TeacherCount { get; }

    public static StaffConfigurationDocument Read(string text)
    {
        if (text.Length > OrganizationImportDocumentReader.MaximumCharacters)
            throw new ArgumentException("完整师资文件过长，请分批准备。");
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            var teachers = Property(root, "teachers");
            if (teachers.ValueKind != JsonValueKind.Array || Property(root, "assignments").ValueKind != JsonValueKind.Undefined)
                throw new ArgumentException("请选择旧初始化向导导出的完整师资 JSON（包含 teachers）。普通教师分配文件请回原办理页。");
            var term = Property(root, "term");
            var school = Property(root, "schoolCode");
            return new(root.Clone(), school.ValueKind == JsonValueKind.String ? school.GetString()?.Trim() : null,
                Number(Property(term, "academicYear")), Number(Property(term, "semester")), teachers.GetArrayLength());
        }
        catch (JsonException)
        {
            throw new ArgumentException("无法读取完整师资 JSON。请检查文件格式后重试。");
        }
    }

    private static JsonElement Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static int? Number(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number when element.TryGetInt32(out var value) => value,
        JsonValueKind.String when int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
        _ => null
    };
}

public sealed partial class ConsoleClient
{
    private static string StaffConfigurationPath(string school) => $"api/v2/admin/schools/{Segment(school)}/staff-configuration";
    private sealed record StaffConfigurationActor(string Id);

    public async Task<string> GetStaffConfigurationActorAsync(CancellationToken ct)
    {
        var session = CurrentSession;
        var actor = await RequestAsync<StaffConfigurationActor>(HttpMethod.Get, "accounts/profile", session.Token, ct);
        CheckCreationSession(session, ct);
        return !string.IsNullOrWhiteSpace(actor.Id) ? actor.Id : throw InvalidResponse();
    }

    public async Task<StaffConfigurationScope> GetStaffConfigurationAsync(string school, CancellationToken ct)
    {
        var session = CurrentSession;
        var scope = await RequestAsync<StaffConfigurationScope>(HttpMethod.Get, StaffConfigurationPath(school), session.Token, ct);
        CheckCreationSession(session, ct);
        if (scope.SchoolId != school || string.IsNullOrWhiteSpace(scope.SchoolCode) ||
            scope.Mode is not ("LOCAL_PIN" or "SHARED_PASSWORD" or "OAUTH_EMAIL") ||
            scope.Term is { } term && (term.Status != "ACTIVE" || string.IsNullOrWhiteSpace(term.Id))) throw InvalidResponse();
        return scope;
    }

    public async Task<StaffConfigurationPreview> PreviewStaffConfigurationAsync(string school, string term,
        StaffConfigurationDocument document, CancellationToken ct)
    {
        var session = CurrentSession;
        var result = await RequestAsync<StaffConfigurationPreview>(HttpMethod.Post, StaffConfigurationPath(school) + "/preview",
            session.Token, ct, body: new { TermId = term, Document = document.Json });
        CheckCreationSession(session, ct);
        if (result.SchoolId != school || result.TermId != term || !ValidStaffProof(result.Version) ||
            result.Teachers is null || result.Issues is null || result.Warnings is null ||
            result.Issues.Any(issue => !ValidStaffIssue(issue)) || result.Warnings.Any(issue => !ValidStaffIssue(issue)) ||
            result.Teachers.Any(row => row is null || row.Username is null || row.Name is null || row.Changes is null ||
                row.Changes.Any(change => !ValidStaffChange(change))) ||
            result.CanApply && (result.Issues.Length != 0 || result.Teachers.Length != document.TeacherCount ||
                result.Teachers.Any(row => string.IsNullOrWhiteSpace(row.Username)) ||
                result.Teachers.Select(row => row.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Teachers.Length) ||
            result.Teachers.Select(row => row.Index).Distinct().Count() != result.Teachers.Length ||
            result.Teachers.Any(row => row.Index < 0 || row.Index >= document.TeacherCount)) throw InvalidResponse();
        return result with { SessionId = session.Id };
    }

    public async Task<StaffConfigurationSave> CommitStaffConfigurationAsync(string school, StaffConfigurationDocument document,
        StaffConfigurationPreview preview, Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        if (preview.SessionId != session.Id || preview.SchoolId != school || !preview.CanApply || !ValidStaffProof(preview.Version) || requestId == Guid.Empty)
            throw new ArgumentException("请使用本次登录及目标学校重新预览方案。");
        // PIN hashing for a full file can exceed the normal short administrative request deadline.
        using var transport = new HttpClient(new AccountHttpHandler(() => _session) { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = ServerUri, Timeout = TimeSpan.FromMinutes(4), MaxResponseContentBufferSize = 32 * 1024 * 1024
        };
        var result = ReadData<StaffConfigurationSave>(await SendAsync(HttpMethod.Post, StaffConfigurationPath(school), session.Token, ct, body: new
        {
            preview.TermId, Document = document.Json, RequestId = requestId, ExpectedVersion = preview.Version, RequestPinDelivery = true
        }, transport: transport));
        CheckCreationSession(session, ct);
        VerifyStaffReceipt(result, school, preview.TermId, requestId);
        var teachers = preview.Teachers.ToDictionary(row => row.Username, StringComparer.OrdinalIgnoreCase);
        if (teachers.Count != result.Receipt.Teachers.Length || result.Receipt.Teachers.Any(row =>
                !teachers.TryGetValue(row.Username, out var expected) || row.Created != (expected.AccountId is null) ||
                expected.AccountId is { } accountId && row.AccountId != accountId) ||
            result.AppliedPins is { } pins && (pins.Any(pin => pin is null || pin.Version < 0 || pin.Pin is not { Length: >= 4 and <= 8 } ||
                    !pin.Pin.All(char.IsAsciiDigit) || !result.Receipt.Teachers.Any(row => row.Created && row.AccountId == pin.AccountId && row.Username == pin.Username)) ||
                pins.Select(pin => pin.AccountId).Distinct().Count() != pins.Length))
            throw InvalidResponse();
        return result;
    }

    public async Task<StaffConfigurationReceipt?> GetStaffConfigurationReceiptAsync(string school, string term, Guid requestId, CancellationToken ct)
    {
        var session = CurrentSession;
        try
        {
            var result = await RequestAsync<StaffConfigurationSave>(HttpMethod.Get,
                $"{StaffConfigurationPath(school)}/receipts/{requestId:D}", session.Token, ct);
            CheckCreationSession(session, ct);
            VerifyStaffReceipt(result, school, term, requestId);
            if (result.AppliedPins is { Length: > 0 }) throw InvalidResponse();
            return result.Receipt;
        }
        catch (ConsoleApiException error) when (error is { StatusCode: 404, Code: "STAFF_CONFIGURATION_RECEIPT_NOT_FOUND" })
        {
            CheckCreationSession(session, ct);
            return null;
        }
    }

    public async Task<StaffConfigurationReceipt?> CancelStaffConfigurationAsync(string school, string term, Guid requestId,
        string proof, CancellationToken ct)
    {
        if (requestId == Guid.Empty || !ValidStaffProof(proof)) throw new ArgumentException("缺少原申请编号或确认依据。");
        var session = CurrentSession;
        var result = await RequestAsync<StaffConfigurationCancellation>(HttpMethod.Post,
            $"{StaffConfigurationPath(school)}/receipts/{requestId:D}/cancel", session.Token, ct,
            body: new { TermId = term, ExpectedVersion = proof });
        CheckCreationSession(session, ct);
        if (result.State == "CANCELLED" && result.Receipt is null && result.RequestId == requestId && result.SchoolId == school && result.TermId == term)
            return null;
        if (result.State == "APPLIED" && result.Receipt is { } receipt)
        {
            VerifyStaffReceipt(new() { State = result.State, Receipt = receipt }, school, term, requestId);
            return receipt;
        }
        throw InvalidResponse();
    }

    private static void VerifyStaffReceipt(StaffConfigurationSave result, string school, string term, Guid requestId)
    {
        if (result.State != "APPLIED" || result.Receipt is not { } receipt || receipt.RequestId != requestId ||
            receipt.SchoolId != school || receipt.TermId != term || receipt.Teachers is not { Length: > 0 } ||
            receipt.Teachers.Any(row => row is null || string.IsNullOrWhiteSpace(row.AccountId) || string.IsNullOrWhiteSpace(row.Username) ||
                string.IsNullOrWhiteSpace(row.Name) || row.Changes?.Any(change => !ValidStaffChange(change)) == true) ||
            receipt.Teachers.Select(row => row.AccountId).Distinct().Count() != receipt.Teachers.Length ||
            receipt.Teachers.Select(row => row.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count() != receipt.Teachers.Length) throw InvalidResponse();
    }

    private static bool ValidStaffIssue(StaffConfigurationIssue? issue) => issue is not null &&
        !string.IsNullOrWhiteSpace(issue.Code) && !string.IsNullOrWhiteSpace(issue.Message);

    private static bool ValidStaffChange(StaffConfigurationChange? change) => change is not null &&
        change.Kind is "ACCOUNT" or "ACCESS" or "TEACHING" or "GRADE_DUTY" or "CLASS_DUTY" && !string.IsNullOrWhiteSpace(change.Label);

    public static bool ValidStaffProof(string? proof) => proof is { Length: 64 } && proof.All(Uri.IsHexDigit);

    private static bool IsStaffConfigurationPath(string path) => path.Contains("/staff-configuration", StringComparison.Ordinal);
    private static ConsoleApiException StaffConfigurationError(int status, string? code) => new(status, status switch
    {
        404 when code == "STAFF_CONFIGURATION_RECEIPT_NOT_FOUND" => "服务器未找到本人的原申请回执。",
        404 => "此 KV 尚未提供完整师资方案入口，或目标已不可用。",
        409 when code == "STAFF_CONFIGURATION_CANCELLED" => "原申请已结束，服务器不会再执行该编号。",
        409 => "本次确认已过期或与原申请不符。请重新核对学期及方案。",
        422 or 400 => "KV 未接受这份方案，请核对文件和预览中的问题。",
        _ => HttpError(status, false).Message
    }, code);
}
