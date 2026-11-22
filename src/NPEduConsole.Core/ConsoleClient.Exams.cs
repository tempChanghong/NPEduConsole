using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace NPEduConsole.Core;

public sealed record ExamScope(string SchoolId, string ClassId, string ScreenId, string DeviceId);
public sealed record ExamIdentity(string ServerInstanceId, string DeploymentEpoch, string DeviceId, long BindingRevision, int CredentialGeneration);
public sealed record ExamDeviceContext(ExamIdentity Identity, Guid RunId, Guid SessionId, long StatusEpoch, Guid ControlEpoch);
public sealed record ExamRuntimePolicy(Guid ConsentId, long PolicyRevision, bool Enabled, bool Supported,
    bool PairedExamControl = false, bool RemoteDailyControl = false);
public sealed record ExamRuntimeSample(string RuntimeMode, string RuntimePhase, long RuntimeRevision, long? ModeRevision,
    long ConfigurationRevision, bool RemoteExamPause, string Recording, string Desktop, bool NoticeOpen,
    Guid? OperationId, string? ReasonCode = null, string? Step = null);
public sealed record ExamRuntimeState(ExamRuntimePolicy? Policy, ExamRuntimeSample? Status, DateTimeOffset? SampleAsOf,
    string Connectivity, Guid? ControlEpoch, Guid? UnresolvedOperationId);
public sealed record ExamRuntimeEvidence(string ExamAware, string ClassIsland, bool RemoteExamPause, string Startup);
public sealed record ExamRuntimeOperation(Guid OperationId, Guid RequestId, string SchoolId, string AdministrativeClassId,
    string ScreenBindingId, string DeviceId, string Target, string State, string? Step, string? ReasonCode,
    DateTimeOffset? ProgressReceivedAt, DateTimeOffset? ResolvedAt, JsonElement? Grant, ExamRuntimeEvidence? Evidence);
public sealed record ExamScheduleEntry(string Name, string Start, string End, double AlertTime);
public sealed record ExamPlanSummary(Guid PreparationId, string Sha256, string ExamName, string Message, ExamScheduleEntry[] Exams);
public sealed record ClassroomPlayerSession(string Id, string State, string ExamName);
public sealed record ClassroomPlayer(bool Known, ClassroomPlayerSession[] Sessions, ClassroomPlayerSession? LastSession);
public sealed record ExamPlanStatus(bool Enabled, Guid ConsentId, long PolicyRevision, long Revision,
    bool Available, string? BlockReason, ClassroomPlayer? Player, Guid? PreparedId);
public sealed record ExamPlanOperation(Guid OperationId, Guid RequestId, ExamDeviceContext Context, Guid ConsentId,
    long PolicyRevision, long Revision, string FileName, string Sha256, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    string State, ExamPlanSummary? Summary, string? SessionId, string? ReasonCode, JsonElement? Grant)
{
    public bool IsTerminal => State is "STARTED" or "FAILED" or "UNKNOWN" or "EXPIRED" or "CANCELLED";
}
public sealed record ExamPlanState(ExamDeviceContext? Context, ExamPlanStatus? Status, DateTimeOffset? ReceivedAt, bool Online, ExamPlanOperation[] Items);
public sealed record ClassroomExamState(ExamRuntimeState Runtime, ExamRuntimeOperation[] RuntimeHistory, ExamPlanState Plans,
    DateTimeOffset RuntimeServerTime, DateTimeOffset PlanServerTime, long ReadStartedAt)
{
    public DateTimeOffset RuntimeNow => RuntimeServerTime + Stopwatch.GetElapsedTime(ReadStartedAt);
    public DateTimeOffset PlanNow => PlanServerTime + Stopwatch.GetElapsedTime(ReadStartedAt);
    public bool RuntimeFresh => Runtime.Connectivity == "ONLINE" && Runtime.SampleAsOf is { } sample &&
        RuntimeNow - sample is { TotalSeconds: >= 0 and <= 60 };
    public bool PlanFresh => Plans.Online && Plans.ReceivedAt is { } received &&
        PlanNow - received is { TotalSeconds: >= 0 and < 45 };
}

public sealed class ClassroomExamFile
{
    public const int MaxBytes = 24 * 1024;
    public string Name { get; }
    internal string DataBase64 { get; }

    private ClassroomExamFile(string name, byte[] bytes)
    {
        Name = name;
        DataBase64 = Convert.ToBase64String(bytes);
    }

    public static ClassroomExamFile Read(string name, byte[] bytes)
    {
        if (name.Length is < 1 or > 160 || name.Any(char.IsControl) || name != Path.GetFileName(name) ||
            Path.GetExtension(name).ToLowerInvariant() is not (".ea2" or ".json") || bytes.Length is < 1 or > MaxBytes)
            throw new ArgumentException("请选择不超过 24 KiB 的 UTF-8 .ea2 或 .json 文件，文件名最多 160 字。");
        try
        {
            var contents = bytes.AsSpan();
            if (contents.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) contents = contents[3..];
            using var document = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(contents),
                new JsonDocumentOptions { MaxDepth = 64 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        }
        catch (Exception error) when (error is DecoderFallbackException or JsonException)
        { throw new ArgumentException("考试方案应为 UTF-8 JSON 对象，请用 ExamAware 编辑器核对文件。"); }
        return new ClassroomExamFile(name, bytes);
    }
}

public sealed partial class ConsoleClient
{
    private const string ExamRuntimeVersion = "0.4", ExamPlanVersion = "0.5";
    private const string ExamOperations = "runtime-operations", ExamPlans = "exam-plans";

    public async Task<ClassroomExamState> GetClassroomExamAsync(ExamScope scope, CancellationToken ct)
    {
        var session = CurrentSession;
        var started = Stopwatch.GetTimestamp();
        var runtime = ReadExamAsync(session, scope, "runtime-status", ExamRuntimeVersion, ct);
        var history = ReadExamAsync(session, scope, ExamOperations, ExamRuntimeVersion, ct);
        var plans = ReadExamAsync(session, scope, ExamPlans, ExamPlanVersion, ct);
        await Task.WhenAll(runtime, history, plans);
        var planView = ReadData<ExamPlanState>(plans.Result);
        if (planView.Context is { } context && context.Identity.DeviceId != scope.DeviceId ||
            planView.Items.Any(item => item.Context.Identity.DeviceId != scope.DeviceId))
            throw InvalidResponse();
        var operations = ReadData<ExamRuntimeList>(history.Result).Items;
        foreach (var operation in operations) VerifyRuntimeScope(scope, operation);
        return new(ReadData<ExamRuntimeState>(runtime.Result), operations, planView,
            runtime.Result.ServerTime!.Value, plans.Result.ServerTime!.Value, started);
    }

    public async Task<ExamRuntimeOperation> RequestExamModeAsync(ExamScope scope, ExamRuntimeState view, string target, Guid requestId,
        CancellationToken ct)
    {
        if (target is not ("EXAM" or "DAILY") || view.ControlEpoch is not { } epoch || view.Policy is not { } policy ||
            view.Status is not { ModeRevision: { } modeRevision } status)
            throw new ArgumentException("请先核对大屏的考试环境。");
        var body = new { requestId, target, scope = "EXAM_MODE", expectedRuntimeRevision = status.RuntimeRevision,
            expectedModeRevision = modeRevision, expectedConfigurationRevision = status.ConfigurationRevision,
            policy.ConsentId, policy.PolicyRevision, controlEpoch = epoch };
        return VerifyRuntimeScope(scope, ReadData<ExamRuntimeOperation>(await ReadExamAsync(CurrentSession, scope,
            ExamOperations, ExamRuntimeVersion, ct, body, requestId)));
    }

    public async Task<ExamPlanOperation> DeliverExamPlanAsync(ExamScope scope, ExamPlanState view, ClassroomExamFile file, Guid requestId,
        CancellationToken ct)
    {
        if (view.Context is not { } context || context.Identity.DeviceId != scope.DeviceId || view.Status is not { } status)
            throw new ArgumentException("请先核对大屏的方案校验状态。");
        var body = new { requestId, context, status.ConsentId, status.PolicyRevision, status.Revision, fileName = file.Name, file.DataBase64 };
        var result = await WriteExamPlanAsync(scope, ExamPlans, requestId, body, ct);
        return result.RequestId == requestId ? result : throw InvalidResponse();
    }

    public async Task<ExamPlanOperation> StartExamPlanAsync(ExamScope scope, ExamPlanOperation plan, Guid requestId, CancellationToken ct)
    {
        var summary = plan.Summary ?? throw new ArgumentException("请先核对设备返回的考程。");
        VerifyExamPlanScope(scope, plan);
        var result = await WriteExamPlanAsync(scope, $"{ExamPlans}/{plan.OperationId:D}/start", requestId,
            new { requestId, summary.PreparationId, plan.Sha256 }, ct);
        return result.OperationId == plan.OperationId ? result : throw InvalidResponse();
    }

    public Task<ExamPlanOperation> CancelExamPlanAsync(ExamScope scope, Guid operationId, Guid requestId, CancellationToken ct) =>
        WriteExamPlanAsync(scope, $"{ExamPlans}/{operationId:D}/cancel", requestId, new { requestId }, ct, operationId);

    public async Task<ExamRuntimeOperation> CancelExamModeAsync(ExamScope scope, Guid operationId, Guid requestId, CancellationToken ct)
    {
        var operation = VerifyRuntimeScope(scope, ReadData<ExamRuntimeOperation>(await ReadExamAsync(CurrentSession, scope,
            $"{ExamOperations}/{operationId:D}/cancel", ExamRuntimeVersion, ct, new { requestId }, requestId)));
        return operation.OperationId == operationId ? operation : throw InvalidResponse();
    }

    public async Task<ExamRuntimeOperation?> GetExamModeReceiptAsync(ExamScope scope, Guid requestId, CancellationToken ct)
    {
        var result = await ReadExamReceiptAsync<ExamRuntimeOperation>(scope, $"{ExamOperations}/receipts/{requestId:D}", ExamRuntimeVersion, ct);
        return result is null ? null : VerifyRuntimeScope(scope, result);
    }

    public async Task<ExamPlanOperation?> GetExamPlanReceiptAsync(ExamScope scope, Guid requestId, Guid? startedOperationId, CancellationToken ct)
    {
        var path = startedOperationId is { } id ? $"{ExamPlans}/{id:D}/start/receipts/{requestId:D}" : $"{ExamPlans}/receipts/{requestId:D}";
        var result = await ReadExamReceiptAsync<ExamPlanOperation>(scope, path, ExamPlanVersion, ct);
        if (result is null) return null;
        if (startedOperationId is { } operation ? result.OperationId != operation : result.RequestId != requestId)
            throw InvalidResponse();
        return VerifyExamPlanScope(scope, result);
    }

    public async Task<ExamPlanOperation> GetExamPlanAsync(ExamScope scope, Guid operationId, CancellationToken ct)
    {
        var item = VerifyExamPlanScope(scope, ReadData<ExamPlanOperation>(await ReadExamAsync(CurrentSession, scope,
            $"{ExamPlans}/{operationId:D}", ExamPlanVersion, ct)));
        return item.OperationId == operationId ? item : throw InvalidResponse();
    }

    private async Task<T?> ReadExamReceiptAsync<T>(ExamScope scope, string path, string version, CancellationToken ct) where T : class
    {
        try { return ReadData<T>(await ReadExamAsync(CurrentSession, scope, path, version, ct)); }
        catch (ConsoleApiException error) when (error is { StatusCode: 404, Code: "NOT_FOUND" }) { return null; }
    }

    private async Task<ExamPlanOperation> WriteExamPlanAsync(ExamScope scope, string path, Guid requestId, object body, CancellationToken ct,
        Guid? expectedOperation = null)
    {
        var item = VerifyExamPlanScope(scope, ReadData<ExamPlanOperation>(await ReadExamAsync(CurrentSession, scope, path, ExamPlanVersion, ct, body, requestId)));
        return expectedOperation is null || item.OperationId == expectedOperation ? item : throw InvalidResponse();
    }

    private async Task<Envelope> ReadExamAsync(Session session, ExamScope scope, string path, string version, CancellationToken ct,
        object? mutation = null, Guid? requestId = null)
    {
        var envelope = await SendAsync(mutation is null ? HttpMethod.Get : HttpMethod.Post,
            $"api/v2/npep/schools/{Segment(scope.SchoolId)}/devices/{Segment(scope.DeviceId)}/{path}", session.Token, ct,
            version, mutation, requestId);
        CheckCreationSession(session, ct);
        return envelope;
    }

    private static ExamRuntimeOperation VerifyRuntimeScope(ExamScope scope, ExamRuntimeOperation operation)
    {
        if (operation.SchoolId != scope.SchoolId || operation.AdministrativeClassId != scope.ClassId || operation.ScreenBindingId != scope.ScreenId ||
            operation.DeviceId != scope.DeviceId || operation.OperationId == Guid.Empty)
            throw InvalidResponse();
        return operation;
    }

    private static ExamPlanOperation VerifyExamPlanScope(ExamScope scope, ExamPlanOperation plan)
    {
        if (plan.Context.Identity.DeviceId != scope.DeviceId || plan.OperationId == Guid.Empty ||
            plan.Summary is { } summary && (summary.Sha256 != plan.Sha256 || summary.Exams.Length is < 1 or > 32))
            throw InvalidResponse();
        return plan;
    }

    private sealed record ExamRuntimeList(ExamRuntimeOperation[] Items);

    private static bool IsExamPath(string path) => path.Contains("/exam-plans", StringComparison.Ordinal) ||
        path.Contains("/runtime-", StringComparison.Ordinal);

    private static async Task<ConsoleApiException> ExamErrorAsync(int status, HttpResponseMessage response,
        string version, string requestId, CancellationToken ct)
    {
        Envelope? envelope = null;
        try { envelope = await response.Content.ReadFromJsonAsync<Envelope>(Json, ct); }
        catch (JsonException) { }
        var code = envelope?.ProtocolVersion == version && envelope.RequestId == requestId ? envelope.Error?.Code : null;
        var message = code is not null ? ExamReason.Describe(code) : status switch
        {
            404 or 426 => "该 KV 版本不支持这项考试办理，请使用现有网页入口。",
            _ => HttpError(status, false).Message
        };
        return new ConsoleApiException(status, message, code);
    }
}
