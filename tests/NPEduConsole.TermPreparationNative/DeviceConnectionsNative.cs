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
            if (phase == "equipment")
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
        catch (Exception error) { Console.Error.WriteLine($"Native connection acceptance failed ({phase}, {step}): {error.GetType().Name}"); return 1; }
    }
}
