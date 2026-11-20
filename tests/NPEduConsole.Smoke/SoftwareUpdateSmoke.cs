using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NPEduConsole.Core;

internal static class SoftwareUpdateSmoke
{
    private sealed record Fixture(string Origin, string SchoolCode, string Username, string Password,
        string SchoolId, string OtherSchoolId, string ClassroomBearer, Deployment DeviceIdentity);
    private sealed record Deployment(Guid ServerInstanceId, Guid DeploymentEpoch);
    private sealed record Receipt(string PlanId, string State, string? Reason);

    internal static async Task RunAsync(string config)
    {
        var fixture = JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(config), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Missing software update fixture.");
        if (!new Uri(fixture.Origin).IsLoopback) throw new InvalidDataException("Software update smoke needs a loopback service.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var ct = deadline.Token;
        using var admin = new ConsoleClient(fixture.Origin);
        using var device = new HttpClient { BaseAddress = new Uri(fixture.Origin + "/api/v2/npep/device/") };
        device.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.ClassroomBearer);
        await admin.LoginAsync(fixture.SchoolCode, fixture.Username, fixture.Password, ct);
        var classrooms = await admin.GetClassroomsAsync(fixture.SchoolId, ct);
        var screen = classrooms.Single(room => room.Name == "高二（1）班").Screens.Single();
        string id = screen.Devices.Single().Id;
        var otherId = classrooms.Single(room => room.Name == "高二（2）班").Screens.Single().Devices.Single().Id;
        Task<SoftwareUpdate> Read() => admin.GetSoftwareUpdateAsync(fixture.SchoolId, id, ct);
        await ExpectError(403, () => admin.GetSoftwareUpdateAsync(fixture.OtherSchoolId, id, ct));

        JsonElement identity = (await DeviceRequest("school-management", "0.16", null)).GetProperty("identity");
        JsonElement context = await StartSession(1);
        int sequence = 0;
        const string original = "1.1.10.0", next = "1.1.11.0", later = "1.1.12.0";
        var installation = await Read();
        Check(installation.CanInvite && !installation.IsEnrolled && !installation.EligibleReleases.Any(), "legacy binding alone is not managed");
        var invitation = await admin.SetSoftwareUpdateAsync(installation, SoftwareUpdateAction.Enroll, null, Guid.NewGuid(), ct);
        Check(invitation.Enrollment == "INVITED" && invitation.EnrollmentId is not null && !invitation.IsEnrolled, "school invitation stays pending on local consent");
        Check((await admin.GetSoftwareUpdateAsync(fixture.SchoolId, otherId, ct)).CanInvite, "invitation is isolated to a single device");
        await ExpectError(409, () => admin.SetSoftwareUpdateAsync(installation, SoftwareUpdateAction.Enroll, null, Guid.NewGuid(), ct),
            "REVISION_CONFLICT");
        await ExpectError(409, () => admin.SetSoftwareUpdateAsync(invitation, SoftwareUpdateAction.Approve, next, Guid.NewGuid(), ct));
        var consent = invitation.EnrollmentId!.Value;
        await Exchange(original, consent);
        var managed = await Read();
        Check(managed.IsEnrolled && managed.ObservedVersion == original && managed.LastSeenAt.HasValue &&
            managed.EligibleReleases.Select(release => release.Version).Order().SequenceEqual([next, later]), "explicit consent and eligible catalog versions");
        await ExpectError(409, () => admin.SetSoftwareUpdateAsync(managed, SoftwareUpdateAction.Approve, original, Guid.NewGuid(), ct));

        var cancelPlan = await admin.SetSoftwareUpdateAsync(managed, SoftwareUpdateAction.Approve, next, Guid.NewGuid(), ct);
        var cancelled = await admin.SetSoftwareUpdateAsync(cancelPlan, SoftwareUpdateAction.Cancel, null, Guid.NewGuid(), ct);
        Check(cancelled.Plan?.State == "CANCELLED", "pending plan can be withdrawn");
        var approved = await admin.SetSoftwareUpdateAsync(cancelled, SoftwareUpdateAction.Approve, next, Guid.NewGuid(), ct);
        var planId = approved.Plan!.Id;
        await Exchange(original, consent, new(planId, "DEFERRED", "RECORDING_BUSY"));
        var waiting = await Read();
        Check(waiting.Plan?.State == "DEFERRED" && waiting.Plan.Reason == "RECORDING_BUSY", "actual device deferral is surfaced");
        await Exchange(original, consent, new(planId, "DEFERRED", "STATE_UNKNOWN"));
        Check((await Read()).Plan?.Reason == "STATE_UNKNOWN", "unknown classroom state remains deferred");
        await Exchange(original, consent, new(planId, "INSTALLING", null));
        await Exchange(original, consent, new(planId, "AWAITING_RESTART", null));
        var restart = await Read();
        Check(restart.Plan?.State == "AWAITING_RESTART" && restart.ObservedVersion == original,
            "installer outcome cannot imply a successful new runtime");
        await ExpectDeviceError(() => Exchange(next, consent), "UPDATE_VERSION_CHANGED");
        int oldEpoch = context.GetProperty("statusEpoch").GetInt32();
        context = await StartSession(oldEpoch);
        sequence = 0;
        await Exchange(next, consent);
        var updated = await Read();
        Check(updated.Plan?.State == "SUCCEEDED" && updated.ObservedVersion == next, "new Host epoch observed on target version");

        var second = await admin.SetSoftwareUpdateAsync(updated, SoftwareUpdateAction.Approve, later, Guid.NewGuid(), ct);
        await Exchange(next, consent, new(second.Plan!.Id, "INSTALLING", null));
        context = await StartSession(context.GetProperty("statusEpoch").GetInt32());
        sequence = 0;
        await Exchange(later, consent);
        Check((await Read()).Plan?.State == "INSTALLING", "new runtime without the task's installer receipt cannot mark success");
        context = await StartSession(context.GetProperty("statusEpoch").GetInt32());
        sequence = 0;
        await Exchange(next, consent, new(second.Plan.Id, "FAILED", "ROLLED_BACK"));
        var recovered = await Read();
        Check(recovered.Plan?.State == "FAILED" && recovered.Plan.Reason == "ROLLED_BACK" && recovered.ObservedVersion == next,
            "a rollback remains distinct from upgrade success");
        await admin.LogoutAsync(ct);
        Console.WriteLine("PASS: KV software maintenance school/device scope, explicit consent, revision conflict, catalog eligibility, cancellation, recording/unknown deferral, new Host confirmed, rollback reported.");

        async Task<JsonElement> StartSession(int expectedStatusEpoch)
        {
            var runId = Guid.NewGuid();
            var reply = await DeviceRequest("sessions", "0.1",
                requestId => new { requestId, fixture.DeviceIdentity.ServerInstanceId, fixture.DeviceIdentity.DeploymentEpoch, runId, expectedStatusEpoch });
            return JsonSerializer.SerializeToElement(new
            {
                identity, sessionId = reply.GetProperty("sessionId").GetGuid(), runId, statusEpoch = reply.GetProperty("statusEpoch").GetInt32()
            }, JsonSerializerOptions.Web);
        }

        Task<JsonElement> Exchange(string runningVersion, Guid enrollmentId, Receipt? receipt = null) =>
            DeviceRequest("software-update/exchange", "0.18", requestId =>
                new { requestId, context, sequence = ++sequence, runningVersion, localConsent = true, consentEnrollmentId = enrollmentId, receipt });

        async Task<JsonElement> DeviceRequest(string path, string version, Func<Guid, object>? body)
        {
            using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
            var requestId = Guid.NewGuid();
            request.Headers.Add("X-NPEP-Version", version);
            request.Headers.Add("X-Request-Id", requestId.ToString());
            if (body is not null) request.Content = JsonContent.Create(body(requestId));
            using var response = await device.SendAsync(request, ct);
            var envelope = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            if (!response.IsSuccessStatusCode)
            {
                string? errorCode = envelope.GetProperty("error").GetProperty("code").GetString();
                throw new InvalidDataException($"Device route {path} returned {(int)response.StatusCode} {errorCode}.");
            }
            return envelope.GetProperty("data");
        }
    }

    private static void Check(bool condition, string step)
    {
        if (!condition) throw new InvalidDataException(step);
    }

    private static async Task ExpectError(int status, Func<Task> request, string? code = null)
    {
        try { await request(); }
        catch (ConsoleApiException error) when (error.StatusCode == status && (code is null || error.Code == code)) { return; }
        throw new InvalidDataException($"Expected status {status}, code {code}.");
    }

    private static async Task ExpectDeviceError(Func<Task> request, string code)
    {
        try { await request(); }
        catch (InvalidDataException error) when (error.Message.EndsWith($" {code}.", StringComparison.Ordinal)) { return; }
        throw new InvalidDataException($"Expected device error {code}.");
    }
}
