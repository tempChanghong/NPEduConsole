using NPEduConsole.Core;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

internal static class NativeClassroom
{
    private sealed record TransferAccess(string Origin, string SchoolId, string SchoolCode, string ClassId,
        string BindingId, string RearId, string ManagementSecret, string Password, string NativeDirectory, bool Isolated);

    internal static async Task VerifyTransferredPairingAsync(string path)
    {
        var access = System.Text.Json.JsonSerializer.Deserialize<TransferAccess>(await File.ReadAllTextAsync(path),
            System.Text.Json.JsonSerializerOptions.Web)!;
        Check(access.Isolated && new Uri(access.Origin).IsLoopback, "Expected isolated target KV.");
        var directory = access.NativeDirectory;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        var ct = timeout.Token;
        const string nativeName = "迁入后现场重新配对的前屏";
        await using (var host = new NpepRuntime(directory, "acceptance", () => new(Protocol.Version, Guid.NewGuid(), "Succeeded", null, "")))
        {
            var settings = await CommandAsync(host, new("inspect", 0, Origin: access.Origin), ct);
            var paired = await CommandAsync(host, new("password-class", 0, access.Origin, nativeName,
                (string?)settings.Server!["serverInstanceId"], (string?)settings.Server["deploymentEpoch"],
                ScreenBindingId: access.BindingId, ManagementSecret: access.ManagementSecret), ct);
            Check(paired.State == "ACTIVE", "The original class credential paired the transferred front screen.");
        }
        using var device = new NpepDevice(directory);
        await device.OpenSessionAsync(ct);
        await device.ReportAsync(await NpepStatusReader.ReadAsync(null, "acceptance", ct), ct);
        using var console = new ConsoleClient(access.Origin);
        await console.LoginAsync(access.SchoolCode, "admin", access.Password, ct);
        var classroom = (await console.GetClassroomsAsync(access.SchoolId, ct)).Single(row => row.Id == access.ClassId);
        Check(classroom.Screens.Single(row => row.Id == access.BindingId).Devices.Single().Name == nativeName,
            "The target reads the actual front-screen report.");
        Check(classroom.Screens.Single(row => row.Id == access.RearId).Devices.Count == 0,
            "No new credential or report exists for the rear screen.");
        await console.LogoutAsync(ct);
        Console.WriteLine("PASS: original class password paired the exact restored front screen using NPEduTools; rear remained unpaired.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    internal static async Task<NpepState> CommandAsync(NpepRuntime host, NpepCommand command, CancellationToken ct, string? expectedError = null)
    {
        var request = new HostRequest(Protocol.Version, Guid.NewGuid(), "npep.command", Npep: command with {Revision = host.Snapshot().Revision});
        using var pipe = new MemoryStream();
        await Protocol.WriteAsync(pipe, request, ct);
        pipe.Position = 0;
        var decoded = await Protocol.ReadAsync<HostRequest>(pipe, ct);
        var validation = Protocol.Validate(decoded);
        Check(validation is null, "Invalid Host command " + command.Action + ": " + validation);
        var handled = host.Handle(decoded);
        Check(handled.Outcome == "Accepted", "Host refused command " + command.Action + ": " + handled.ErrorCode);
        while (host.Snapshot().Busy) await Task.Delay(20, ct);
        var response = host.Handle(new(Protocol.Version, Guid.NewGuid(), "npep.status"));
        pipe.SetLength(0);
        await Protocol.WriteAsync(pipe, response, ct);
        pipe.Position = 0;
        var state = (await Protocol.ReadAsync<HostResponse>(pipe, ct)).Npep!;
        Check(state.Error == expectedError, $"Unexpected result of {command.Action}: {state.State} {state.Error}");
        return state;
    }

    internal static async Task RunReplacementAsync(Fixture f, ConsoleClient admin, string incumbentDirectory,
        Classroom classroom, string binding, string secret, CancellationToken ct)
    {
        string directory = incumbentDirectory + "-candidate";
        const string candidateName = "接替教室终端";
        NpepRuntime StartCandidate() => new(directory, "acceptance", () => new(Protocol.Version, Guid.NewGuid(), "Succeeded", null, ""));
        using var incumbent = new NpepDevice(incumbentDirectory);
        var incumbentId = (string)incumbent.View()["registration"]!["deviceId"]!;
        await incumbent.OpenSessionAsync(ct);
        async Task Heartbeat() => await incumbent.ReportAsync(await NpepStatusReader.ReadAsync(null, "acceptance", ct), ct);

        string pairingId;
        await using (var host = StartCandidate())
        {
            var verified = await CommandAsync(host, new("inspect", 0, f.LostReplyOrigin), ct);
            string instance = (string)verified.Server!["serverInstanceId"]!, epoch = (string)verified.Server["deploymentEpoch"]!;
            var found = await CommandAsync(host, new("find-class", 0, f.LostReplyOrigin, ServerInstanceId: instance,
                DeploymentEpoch: epoch, SchoolQuery: f.SchoolCode, ClassQuery: classroom.Name), ct);
            Check((string?)found.Catalogue!["items"]!.AsArray().Single()!["screenBindingId"] == binding, "Search must select the incumbent screen.");
            var password = new NpepCommand("password-class", 0, f.LostReplyOrigin, candidateName, instance, epoch,
                ScreenBindingId: binding, ManagementSecret: secret);
            var occupied = await CommandAsync(host, password, ct, "BINDING_OCCUPIED");
            Check(occupied.RequestedScreenBindingId == binding && occupied.RequestedDeviceName == candidateName,
                "Occupied admission must retain the candidate identity and the exact screen for the operator.");
            var queue = await admin.GetScreenReplacementsAsync(f.SchoolId, binding, ct);
            Check(queue.Items.Count == 0 && queue.LatestHandover is null, "Ordinary password admission must not request a handover.");
            await Heartbeat();
            var uncertain = await CommandAsync(host, password with {Action = "replacement-class"}, ct, "TEMPORARILY_UNAVAILABLE");
            Check(uncertain.State == "CREATING" && uncertain.PairingSource == "REPLACEMENT", "Keep the original write after a lost reply.");
            var committed = await admin.GetScreenReplacementsAsync(f.SchoolId, binding, ct);
            pairingId = committed.Items.Single().PairingId.ToString("D");
        }

        await using (var recovery = StartCandidate())
        {
            var pending = await CommandAsync(recovery, new("resume-create", 0), ct);
            Check(pending.State == "PENDING" && pending.PairingSource == "REPLACEMENT" && (string?)pending.Pairing?["pairingId"] == pairingId,
                "Retry after a restart must recover the exact replacement acknowledged by KV.");
        }

        string approvalId;
        await using (var waiting = StartCandidate())
        {
            Check(waiting.Snapshot().State == "PENDING" && (string?)waiting.Snapshot().Pairing?["pairingId"] == pairingId,
                "A Host restart must recover the same pending request.");
            var queue = await admin.GetScreenReplacementsAsync(f.SchoolId, binding, ct);
            var item = queue.Items.Single();
            Check(item.PairingId.ToString("D") == pairingId && item.ExpectedDeviceId.ToString("D") == incumbentId && item.DeviceName == candidateName,
                "Console review must identify this exact candidate and incumbent.");
            await admin.ReviewScreenReplacementAsync(item, ScreenReplacementDecision.Approve, Guid.NewGuid(), ct);
            var reviewed = await admin.GetScreenReplacementAsync(f.SchoolId, binding, item.PairingId, ct);
            Check(reviewed.Progress == "AWAITING_CONFIRMATION" && reviewed.DeviceId is null && reviewed.FirstReportedAt is null,
                "Admin approval alone must not be reported as activation or a status sample.");
            var approved = await CommandAsync(waiting, new("poll", 0), ct);
            Check(approved.State == "APPROVED" && approved.PairingSource == "REPLACEMENT" &&
                (string?)approved.Approval?["schoolId"] == f.SchoolId && (string?)approved.Approval?["administrativeClassId"] == classroom.Id &&
                (string?)approved.Approval?["screenBindingId"] == binding, "Polling approval must stop for local review of the expected screen.");
            approvalId = (string)approved.Approval!["approvalId"]!;
            await Heartbeat();
        }

        await using (var ready = StartCandidate())
        {
            Check(ready.Snapshot().State == "APPROVED" && (string?)ready.Snapshot().Approval?["approvalId"] == approvalId,
                "Restarting an approved candidate must still require the same local confirmation.");
            await Heartbeat();
            var active = await CommandAsync(ready, new("confirm", 0, ApprovalId: approvalId), ct);
            Check(active.State == "ACTIVE", "Explicit classroom confirmation must activate the successor.");
            try { await Heartbeat(); throw new InvalidDataException("The old device could still report after handover."); }
            catch (NpepException error) when (error.Status == 401 && error.Code == "AUTH_INVALID") { }
            while (ready.Snapshot().LastReceivedAt is null) await Task.Delay(25, ct);
        }

        using var reopened = new ConsoleClient(f.Origin);
        await reopened.LoginAsync(f.SchoolCode, f.Username, f.Password, ct);
        var room = (await reopened.GetClassroomsAsync(f.SchoolId, ct)).Single(room => room.Id == classroom.Id);
        var screen = room.Screens.Single(screen => screen.Id == binding);
        var history = await reopened.GetScreenReplacementsAsync(f.SchoolId, screen.Id, ct);
        var receipt = history.LatestHandover;
        Check(history.Items.Count == 0 && receipt?.Request.PairingId.ToString("D") == pairingId && receipt.Progress == "REPORTED" &&
            receipt.FirstReportedAt is not null, "Reloading the Console screen must recover the completed handover and its first report.");
        var device = screen.Devices.Single();
        Check(device.Name == candidateName && device.Id == receipt!.DeviceId?.ToString("D") && device.Id != incumbentId && device.LastSeenAt is not null,
            "The active device in the classroom overview must be the reporting successor.");
        await reopened.LogoutAsync(ct);
        Console.WriteLine("PASS: occupied screen, explicit replacement, pending/approved restarts, Console review, local confirmation and first successor report after a fresh Console login.");
    }
}
