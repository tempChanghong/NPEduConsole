using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

internal static class ExamWorkbenchNative
{
    public static async Task<int> RunAsync(string fixturePath, string profileRoot)
    {
        try
        {
            var fixture = NpepProtocol.Parse(await File.ReadAllBytesAsync(fixturePath));
            if (!OperatingSystem.IsWindows() || fixture.Text("kind") != "EXAM_WORKBENCH_DISPOSABLE_DATABASE" ||
                !new Uri(fixture.Text("origin")).IsLoopback) return 2;
            var port = checked((int)fixture.Number("port"));
            byte[] key = Convert.FromHexString(fixture.Text("key"));
            if (port is < 1 or > 65535 || key.Length != 32) return 2;
            string finish = Path.GetFullPath(fixture.Text("finish"));
            var frontMode = new ModeEffects();
            var rearMode = new ModeEffects();
            var modeOnlyEffects = new ModeEffects();
            HostResponse Sample() => new(1, Guid.NewGuid(), "Succeeded", null, "isolated fixture", ClassroomMode: new(0, "Daily"),
                Recording: new("Idle", "isolated fixture"));
            await using var front = new NpepRuntime(Path.Combine(profileRoot, "device-1"), "1.1.10", Sample, frontMode);
            await using var rear = new NpepRuntime(Path.Combine(profileRoot, "device-2"), "1.1.10", Sample, rearMode,
                new BridgeEffects(rearMode, port, key, Path.Combine(Path.GetDirectoryName(finish)!, "hide-player-observation")));
            await using var modeOnly = new NpepRuntime(Path.Combine(profileRoot, "device-3"), "1.1.10", Sample, modeOnlyEffects);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
            while (!front.ControlPolicy().CanEnable || !rear.ControlPolicy().CanEnable || !rear.PlanPolicy().CanEnable ||
                !modeOnly.ControlPolicy().CanEnable)
                await Task.Delay(150, deadline.Token);
            var rearPolicy = rear.PlanPolicy();
            rear.SetPlanConsent(new("plan-consent", rearPolicy.Revision, true, rearPolicy.Scope));
            Console.WriteLine("PASS isolated NPEP components started; rear uses the actual ExamAware bridge. OS mode effects are simulated.");
            while (!File.Exists(finish)) await Task.Delay(150, deadline.Token);
            if (frontMode.Executions != 0 || rearMode.Executions != 4 || rearMode.InExam ||
                modeOnlyEffects.Executions != 2 || modeOnlyEffects.InExam)
                throw new InvalidOperationException("Unexpected mode transitions");
            Console.WriteLine("PASS front stayed daily; rear completed two exam cycles; the environment-only screen returned to daily.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Exam workbench native fixture failed: {(error is NpepException n ? n.Code : error.Message)}");
            return 1;
        }
    }

    private sealed class ModeEffects : INpepRuntimeControl
    {
        private readonly Dictionary<Guid, JsonObject> _results = new();
        public int Executions { get; private set; }
        public bool InExam { get; private set; }

        public Task<JsonObject> ObserveAsync(CancellationToken ct) => Task.FromResult(new JsonObject
        {
            ["runtimeMode"] = InExam ? "EXAM" : "DAILY", ["runtimePhase"] = InExam ? "EXAM_READY" : "IDLE",
            ["runtimeRevision"] = Executions, ["modeRevision"] = Executions, ["configurationRevision"] = 0,
            ["remoteExamPause"] = InExam, ["recording"] = "IDLE", ["desktop"] = "INTERACTIVE", ["noticeOpen"] = false,
            ["operationId"] = null, ["observedAt"] = NpepRuntimeProtocol.UtcNow()
        });

        public async Task ExecuteAsync(JsonObject op, Func<bool, CancellationToken, Task> authorize, CancellationToken ct)
        {
            await authorize(false, ct);
            await authorize(true, ct);
            InExam = op.Text("target") == "EXAM";
            Executions++;
            var id = Guid.Parse(op.Text("operationId"));
            _results.Add(id, new JsonObject { ["state"] = "SUCCEEDED", ["step"] = "VERIFY", ["reasonCode"] = null,
                ["evidence"] = new JsonObject { ["examAware"] = InExam ? "READY" : "EXITED",
                    ["classIsland"] = InExam ? "EXITED" : "READY", ["remoteExamPause"] = InExam,
                    ["startup"] = InExam ? "EXAM_MODE_APPLIED" : "DAILY_MODE_APPLIED", ["sideEffects"] = "APPLIED",
                    ["alreadySatisfied"] = false, ["observedAt"] = NpepRuntimeProtocol.UtcNow(), ["configurationRevision"] = 0 } });
        }

        public Task RecoverAsync(JsonObject op, CancellationToken ct) => throw new InvalidOperationException("Unexpected recovery");
        public bool HasOperation(Guid id) => _results.ContainsKey(id);
        public bool LocallyEnded(Guid id) => false;
        public Task<JsonObject> ResultAsync(Guid id, CancellationToken ct) => Task.FromResult(_results[id].Copy());
    }

    private sealed class BridgeEffects(ModeEffects mode, int port, byte[] key, string hideObservation) : INpepExamPlans
    {
        public JsonObject Observe()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var status = CallAsync("examaware.status", Guid.NewGuid(), timeout.Token).GetAwaiter().GetResult().ExamAware;
            bool hidden = File.Exists(hideObservation);
            var block = !mode.InExam ? "EXAM_MODE_REQUIRED" : status is not { BridgeState: "Connected", CanPresent: true }
                ? "BRIDGE_DISCONNECTED" : hidden || status.Player is not { Known: true } ? "PLAYER_UNKNOWN" :
                status.Player.Sessions.Length > 0 ? "PLAYER_BUSY" : null;
            var player = hidden || status?.Player is not { } observed ? null : new JsonObject
            {
                ["known"] = observed.Known, ["sessions"] = JsonSerializer.SerializeToNode(observed.Sessions, Protocol.Json),
                // The mode fixture does not restart the actual Electron app. Model the loss of its old
                // playback observation after the first return to daily, as a process restart would.
                ["lastSession"] = mode.Executions >= 2 ? null : JsonSerializer.SerializeToNode(observed.LastSession, Protocol.Json)
            };
            return new() { ["revision"] = status?.Revision ?? 0, ["available"] = block is null, ["blockReason"] = block,
                ["player"] = player, ["preparedId"] = status?.PreparedPlan?.PreparationId.ToString("D") };
        }

        public async Task<JsonObject> ExecuteAsync(JsonObject op, bool start, Guid commandId, Action authorize, CancellationToken ct)
        {
            if (!mode.InExam) throw new InvalidOperationException("Fixture player is not ready");
            authorize();
            var input = start
                ? new ExamAwarePlanInput("start", PreparationId: Guid.Parse(((JsonObject)op["summary"]!).Text("preparationId")))
                : new ExamAwarePlanInput("prepare", op.Text("dataBase64"));
            var call = await CallAsync("examaware.plan", commandId, ct, input, op.Number("revision"));
            if (call.Outcome != "Accepted")
            {
                Console.Error.WriteLine($"Isolated ExamAware Host declined the plan: {call.ErrorCode}");
                return Result("FAILED", "LOCAL_REQUEST_REJECTED");
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(16));
            while (!deadline.IsCancellationRequested)
            {
                var status = (await CallAsync("examaware.status", Guid.NewGuid(), deadline.Token)).ExamAware;
                if (status?.PlanOperation is { } observed && observed.RequestId == commandId)
                {
                    if (observed.State == "Prepared" && status.PreparedPlan is { } summary)
                        return Result("PREPARED", summary: summary);
                    if (observed.State == "Started" && observed.SessionId is { } id)
                        return Result("STARTED", sessionId: id);
                    if (observed.State is "Busy" or "Denied" or "Invalid" or "Expired")
                        return Result("FAILED", observed.State == "Busy" ? "PLAYER_BUSY" : "INVALID_PLAN");
                }
                await Task.Delay(100, deadline.Token);
            }
            return Result("UNKNOWN", "UNKNOWN_RESULT");
        }

        private static JsonObject Result(string state, string? reason = null, ExamAwarePlanSummary? summary = null, string? sessionId = null) =>
            new() { ["state"] = state, ["reasonCode"] = reason, ["summary"] = JsonSerializer.SerializeToNode(summary, Protocol.Json), ["sessionId"] = sessionId };

        private async Task<HostResponse> CallAsync(string capability, Guid id, CancellationToken ct, ExamAwarePlanInput? input = null,
            long? revision = null)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, ct);
            var channel = client.GetStream();
            await channel.WriteAsync(key, ct);
            var request = new HostRequest(Protocol.Version, id, capability, ExpectedRevision: revision, ExamPlan: input);
            await Protocol.WriteAsync(channel, request, ct);
            var reply = await Protocol.ReadAsync<HostResponse>(channel, ct);
            return reply.RequestId == id ? reply : throw new InvalidDataException("Fixture Host request mismatch");
        }
    }
}
