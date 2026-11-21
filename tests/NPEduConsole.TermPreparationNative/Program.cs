using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

if (args is not [var phase, var fixtureFile, var dataRoot] || phase is not ("pair" or "report" or "invalidated" or "repair-first" or "report-first") || !OperatingSystem.IsWindows()) return 2;
string step = "read fixture";
try
{
    var fixture = NpepProtocol.Parse(await File.ReadAllBytesAsync(fixtureFile));
    string origin = NpepApi.ValidateOrigin(fixture.Text("origin"));
    if (!new Uri(origin).IsLoopback || fixture.Text("kind") != "TERM_PREPARATION_DISPOSABLE_DATABASE") return 2;
    if (phase == "pair" && Directory.Exists(dataRoot)) return 2;
    using var admin = new HttpClient(new SocketsHttpHandler {AllowAutoRedirect = false, UseCookies = false})
        {BaseAddress = new Uri(origin + "/api/v2/npep/"), Timeout = TimeSpan.FromSeconds(10)};
    admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Text("adminToken"));
    string schoolId = fixture.Text("schoolId"), classId = fixture.Text("classId");
    var bindingIds = ((JsonArray)fixture["bindingIds"]!).Select(row => row!.GetValue<string>()).ToArray();
    var classIds = fixture["classIds"] is JsonArray supplied
        ? supplied.Select(row => row!.GetValue<string>()).ToArray() : bindingIds.Select(_ => classId).ToArray();
    if (bindingIds.Length is < 2 or > 3 || bindingIds.Distinct().Count() != bindingIds.Length || classIds.Length != bindingIds.Length) return 2;
    bool firstOnly = phase is "repair-first" or "report-first";

    for (int slot = 0; slot < (firstOnly ? 1 : bindingIds.Length); slot++)
    {
        string binding = bindingIds[slot];
        string data = Path.Combine(dataRoot, $"device-{slot + 1}");
        step = "open isolated native profile";
        using var device = new NpepDevice(data);
        if (phase == "invalidated")
        {
            step = "check rejected original credential";
            try { await device.OpenSessionAsync(); }
            catch (NpepException error) when (error is {Code: "AUTH_INVALID", Status: 401})
            {
                Console.WriteLine($"PASS old credential rejected for display {slot + 1} ({binding}).");
                continue;
            }
            throw new NpepException("ORIGINAL_CREDENTIAL_STILL_VALID");
        }
        if (phase is "pair" or "repair-first")
        {
            if (phase == "repair-first") await device.UnpairAsync();
            step = "inspect isolated server";
            var info = await device.InspectServerAsync(origin);
            step = "begin native pairing";
            var attempt = await device.BeginAsync(origin, info, $"隔离验收大屏 {slot + 1}", "1.1.10");
            var pairing = (JsonObject)attempt["pairing"]!;
            var resolveBody = NpepProtocol.Body();
            resolveBody["userCode"] = pairing.Text("userCode");
            var resolved = await AdminAsync($"schools/{schoolId}/pairings/resolve", "resolvedPairingResponse", resolveBody);
            if (resolved.Text("pairingId") != pairing.Text("pairingId")) throw new NpepException("PAIRING_MISMATCH");
            var approvalBody = NpepProtocol.Body();
            approvalBody["screenBindingId"] = binding;
            approvalBody["capabilities"] = new JsonArray("device.status");
            await AdminAsync($"schools/{schoolId}/pairings/{pairing.Text("pairingId")}/approve", "approvedPairingResponse", approvalBody);
            var approved = (JsonObject)(await device.PollApprovalAsync())["approval"]!;
            if (approved.Text("schoolId") != schoolId || approved.Text("administrativeClassId") != classIds[slot] ||
                approved.Text("screenBindingId") != binding) throw new NpepException("BINDING_MISMATCH");
            step = "confirm native pairing";
            await device.ConfirmAsync(approved.Text("approvalId"));
        }
        if (device.View().Text("state") != "ACTIVE") throw new NpepException("ORIGINAL_PAIRING_NOT_ACTIVE");
        if (phase == "repair-first")
        {
            Console.WriteLine($"PASS display 1 ({binding}) re-paired to {classIds[slot]} without a report.");
            continue;
        }
        step = "open session for native device";
        await device.OpenSessionAsync();
        var status = new NpepSample(NpepStatusReader.Map(null, "1.1.10"), Stopwatch.GetTimestamp());
        var receipt = await device.ReportAsync(status);
        if (receipt.Number("acceptedSequence") != 1) throw new NpepException("STATUS_NOT_ACCEPTED");
        var registration = (JsonObject)device.View()["registration"]!;
        if (registration.Text("schoolId") != schoolId || registration.Text("administrativeClassId") != classIds[slot] ||
            registration.Text("screenBindingId") != binding) throw new NpepException("ORIGINAL_BINDING_CHANGED");
        Console.WriteLine($"PASS {phase} display {slot + 1}: verified binding, active DPAPI pairing and accepted native status.");
    }
    return 0;

    async Task<JsonObject> AdminAsync(string path, string schema, JsonObject body)
    {
        string requestId = body.Text("requestId");
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-NPEP-Version", "0.1");
        request.Headers.Add("X-Request-Id", requestId);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await admin.SendAsync(request);
        var data = NpepProtocol.Parse(await response.Content.ReadAsByteArrayAsync());
        if (!response.IsSuccessStatusCode) throw new NpepException(((JsonObject)data["error"]!).Text("code"), (int)response.StatusCode);
        if (data.Text("requestId") != requestId) throw new NpepException("RESPONSE_REQUEST_MISMATCH");
        NpepProtocol.Validate(schema, data);
        return (JsonObject)data["data"]!;
    }
}
catch (NpepException error) { Console.Error.WriteLine($"Native acceptance failed during {step}: {error.Code}"); return 1; }
catch (Exception error) { Console.Error.WriteLine($"Native acceptance failed during {step}: {error.GetType().Name}"); return 1; }
