using System.Diagnostics;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;

internal static class DeviceConnectionsNative
{
    public static async Task<int> RunAsync(string phase, string fixturePath, string root)
    {
        string step = "read fixture";
        try
        {
            var fixture = NpepProtocol.Parse(await File.ReadAllBytesAsync(fixturePath));
            var origin = NpepApi.ValidateOrigin(fixture.Text("origin"));
            if (!OperatingSystem.IsWindows() || !new Uri(origin).IsLoopback ||
                fixture.Text("kind") != "DEVICE_CONNECTIONS_DISPOSABLE_DATABASE") return 2;
            var binding = fixture.Text("bindingId");
            if (phase is "equipment" or "classroom-actions")
            {
                step = "start Host device policy";
                var equipment = Path.Combine(root, "host-equipment");
                await using var noise = new NoiseService(_ => throw new InvalidOperationException("No capture in this acceptance"),
                    () => [new("acceptance-mic", "验收麦克风")], directory: equipment,
                    management: new NoiseManagementStore(equipment));
                var cameras = new StudyDeviceService(() => [new("acceptance-camera", "验收摄像头")], equipment);
                var management = new SchoolManagementService(noise, cameras, equipment);
                var connection = Guid.NewGuid();
                var bridge = Guid.NewGuid();
                long sequence = 0;
                await using var study = new StudySettingsService(() => new(connection, bridge, Interlocked.Increment(ref sequence),
                    0, DateTime.Now, 0, "Advancing", "synthetic"), () => false, background: false);
                await using var runtime = new NpepRuntime(root, "device-connections-acceptance",
                    () => new(Protocol.Version, Guid.NewGuid(), "Succeeded", null, "Acceptance"), study: study, schoolManagement: management);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
                if (phase == "classroom-actions")
                {
                    string? switchMode = fixture["switchMode"]?.GetValue<string>();
                    while (true)
                    {
                        var receipt = management.ObserveSchool()["actionPolicyReceipt"] as JsonObject;
                        if (receipt?["policyId"]?.GetValue<string>() == fixture.Text("policyId") && receipt["error"] is null)
                        {
                            var state = noise.ManagementHandle(new(Protocol.Version, Guid.NewGuid(), "noise.management.status.classroom-policy.v2")).NoiseProtection;
                            if (state?.ClassroomPolicy is not { } policy || policy.StopScheduled != fixture.Text("stopMode") ||
                                policy.ExitHost != fixture.Text("exitMode") || policy.SwitchClassroomMode != switchMode)
                                throw new NpepException("HOST_ACTION_POLICY_MISMATCH");
                            if (File.Exists(fixture.Text("observed")))
                            {
                                if (policy.ExitHost is "password" or "either" or "both")
                                {
                                    HostResponse Authorize(string? secret) => noise.ManagementHandle(new(Protocol.Version, Guid.NewGuid(),
                                        "noise.management.command", NoiseManagement: new("authorize", secret, Purpose: "maintenance",
                                            TargetRequestId: Guid.NewGuid(), InstanceId: state.InstanceId, SessionId: state.SessionId,
                                            ClassroomPolicyVersion: policy.Version)));
                                    if (policy.ExitHost is "either" or "both" && Authorize(null).ErrorCode != "CLASSROOM_USB_REQUIRED")
                                        throw new NpepException("MISSING_REGISTERED_USB_WAS_NOT_REQUIRED");
                                    var correct = Authorize(fixture.Text("classSecret"));
                                    if (policy.ExitHost == "both")
                                    {
                                        if (correct.ErrorCode != "CLASSROOM_USB_REQUIRED" || correct.NoiseProtection?.Ticket is not null)
                                            throw new NpepException("CLASSROOM_ALL_FACTOR_NOT_ENFORCED");
                                    }
                                    else if (correct.NoiseProtection?.Ticket is null ||
                                        Authorize("fixture-wrong-secret").NoiseProtection?.Ticket is not null)
                                        throw new NpepException("CLASSROOM_CREDENTIAL_NOT_VERIFIED");
                                }
                                if (switchMode == "deny")
                                {
                                    var denied = noise.AuthorizeInterruption(new(Protocol.Version, Guid.NewGuid(), "classroom.set",
                                        ExpectedRevision: 0, ClassroomMode: new("Exam")));
                                    if (denied is not { Outcome: "Rejected", ErrorCode: "CLASSROOM_ACTION_DENIED" })
                                        throw new NpepException("HOST_MODE_SWITCH_WAS_NOT_DENIED");
                                    Console.WriteLine($"PASS Host guarded classroom.set for binding {binding} after applying its v2 policy.");
                                }
                                if (switchMode == "usb")
                                {
                                    var verification = noise.ManagementHandle(new(Protocol.Version, Guid.NewGuid(), "noise.management.command",
                                        NoiseManagement: new("authorize", Purpose: "switch-classroom-mode", TargetRequestId: Guid.NewGuid(),
                                            InstanceId: state.InstanceId, SessionId: state.SessionId, ClassroomPolicyVersion: policy.Version)));
                                    if (verification.ErrorCode != "CLASSROOM_USB_REQUIRED" || verification.NoiseProtection?.Ticket is not null)
                                        throw new NpepException("HOST_MODE_SWITCH_MISSING_USB_NOT_REQUIRED");
                                    Console.WriteLine($"PASS Host requires a registered USB to switch classroom mode for {binding}.");
                                }
                                return 0;
                            }
                        }
                        await Task.Delay(120, deadline.Token);
                    }
                }
                var version = fixture.Number("expectedVersion");
                var locked = fixture["locked"]!.GetValue<bool>();
                while (true)
                {
                    var receipt = management.ObserveSchool();
                    if (receipt["appliedRevision"]!.GetValue<long>() == version && receipt["error"] is null &&
                        cameras.Snapshot().Locked == locked && File.Exists(fixture.Text("observed")))
                    {
                        if (locked && (receipt["selectedCameraId"]?.GetValue<string>() != "acceptance-camera" ||
                            receipt["selectedMicrophoneId"]?.GetValue<string>() != "acceptance-mic"))
                            throw new NpepException("HOST_SELECTED_WRONG_EQUIPMENT");
                        Console.WriteLine($"PASS actual Host applied equipment revision {version} for {binding} (synthetic inventory).");
                        return 0;
                    }
                    await Task.Delay(250, deadline.Token);
                }
            }

            step = "open native profile";
            using var device = new NpepDevice(root);
            switch (phase)
            {
                case "begin":
                    step = "inspect isolated server";
                    var info = await device.InspectServerAsync(origin);
                    step = "start native pairing";
                    string name = fixture.Text("deviceName");
                    var attempt = fixture["classApply"]!.GetValue<bool>()
                        ? await device.BeginClassAsync(origin, info, name, "device-connections-acceptance", binding, null)
                        : await device.BeginAsync(origin, info, name, "device-connections-acceptance");
                    var pair = attempt["pairing"]!.AsObject();
                    step = "deliver temporary fixture code";
                    await File.WriteAllTextAsync(fixture.Text("output"), new JsonObject
                    {
                        ["code"] = pair.Text("userCode"), ["pairingId"] = pair.Text("pairingId")
                    }.ToJsonString());
                    return 0;
                case "confirm":
                    step = "confirm assigned binding";
                    var approval = (await device.PollApprovalAsync())["approval"]!.AsObject();
                    if (approval.Text("schoolId") != fixture.Text("schoolId") || approval.Text("screenBindingId") != binding ||
                        approval.Text("administrativeClassId") != fixture.Text("classId"))
                        throw new NpepException("APPROVAL_TARGET_MISMATCH");
                    await device.ConfirmAsync(approval.Text("approvalId"));
                    Console.WriteLine($"PASS actual native confirmation for {binding}.");
                    return 0;
                case "report":
                    step = "report for the approved binding";
                    await device.OpenSessionAsync();
                    var receipt = await device.ReportAsync(new NpepSample(NpepStatusReader.Map(null, "device-connections-acceptance"),
                        Stopwatch.GetTimestamp()));
                    if (receipt.Number("acceptedSequence") != 1 || device.View()["registration"]!.AsObject().Text("screenBindingId") != binding)
                        throw new NpepException("STATUS_OR_TARGET_MISMATCH");
                    Console.WriteLine($"PASS actual native report for {binding}.");
                    return 0;
                default: return 2;
            }
        }
        catch (NpepException error) { Console.Error.WriteLine($"Native connection acceptance failed ({phase}): {error.Code}"); return 1; }
        catch (UnauthorizedAccessException error)
        {
            Console.Error.WriteLine($"Native connection acceptance failed ({phase}, {step}): {error.Message}");
            return 1;
        }
        catch (Exception error) { Console.Error.WriteLine($"Native connection acceptance failed ({phase}, {step}): {error.GetType().Name}"); return 1; }
    }
}
