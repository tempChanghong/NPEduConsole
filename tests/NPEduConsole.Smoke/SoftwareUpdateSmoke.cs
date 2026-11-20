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
        VerifyLegacyInvitation();
        VerifyRollbackContract();
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
        await Exchange(original, null);
        var available = await Read();
        var invitation = await admin.SetSoftwareUpdateAsync(available, SoftwareUpdateAction.Enroll, null, Guid.NewGuid(), ct);
        Check(invitation.CanWithdraw && invitation.InvitationLastExchangeAt is null && invitation.LastSeenAt.HasValue,
            "a historical version receipt does not confirm communication for the current invitation");
        Check((await admin.GetSoftwareUpdateAsync(fixture.SchoolId, otherId, ct)).CanInvite, "invitation is isolated to a single device");
        await ExpectError(409, () => admin.SetSoftwareUpdateAsync(available, SoftwareUpdateAction.Enroll, null, Guid.NewGuid(), ct),
            "REVISION_CONFLICT");
        await ExpectError(409, () => admin.SetSoftwareUpdateAsync(invitation, SoftwareUpdateAction.Approve, next, Guid.NewGuid(), ct));
        await ExpectError(409, () => Withdraw(invitation with { CanWithdrawInvitation = null }), "INVALID_UPDATE_ACTION");

        var offline = await admin.GetSoftwareUpdateAsync(fixture.SchoolId, otherId, ct);
        var offlineInvitation = await admin.SetSoftwareUpdateAsync(offline, SoftwareUpdateAction.Enroll, null, Guid.NewGuid(), ct);
        Check(offlineInvitation.CanWithdraw && offlineInvitation.InvitationLastExchangeAt is null, "pending offline invitation without a device exchange");
        Check((await Withdraw(offlineInvitation)).CanInvite, "offline invitation can be withdrawn");

        await Exchange(original, null);
        var exchanged = await Read();
        Check(exchanged.CanWithdraw && exchanged.InvitationLastExchangeAt.HasValue && !exchanged.IsEnrolled,
            "current invitation reports a maintenance exchange without implying consent");
        var withdrawn = await Withdraw(exchanged);
        Check(withdrawn.CanInvite && !withdrawn.CanWithdraw && withdrawn.Revision > exchanged.Revision &&
            withdrawn.InvitationLastExchangeAt is null && withdrawn.ObservedVersion == original,
            "withdrawal resets only software invitation, not version history");
        await ExpectDeviceError(() => Exchange(original, invitation.EnrollmentId!.Value), "UPDATE_ENROLLMENT_CHANGED");
        Check((await admin.GetClassroomsAsync(fixture.SchoolId, ct)).Any(room => room.Screens.Any(binding =>
            binding.Devices.Any(device => device.Id == id))), "withdrawal preserves the device binding");

        var reissued = await admin.SetSoftwareUpdateAsync(withdrawn, SoftwareUpdateAction.Enroll, null, Guid.NewGuid(), ct);
        Check(reissued.CanWithdraw && reissued.EnrollmentId != invitation.EnrollmentId && reissued.InvitationLastExchangeAt is null,
            "replacement invitation has a fresh ID and no inherited receipt");
        await ExpectError(409, () => Withdraw(exchanged), "REVISION_CONFLICT");
        await ExpectError(409, () => Withdraw(exchanged with { Revision = reissued.Revision }), "UPDATE_ENROLLMENT_CHANGED");
        Check((await Read()).EnrollmentId == reissued.EnrollmentId, "stale withdrawal cannot cancel a replacement invitation");

        var consent = reissued.EnrollmentId!.Value;
        await Exchange(original, consent);
        await ExpectError(409, () => Withdraw(reissued), "REVISION_CONFLICT");
        var managed = await Read();
        Check(managed.IsEnrolled && !managed.CanWithdraw && managed.ObservedVersion == original && managed.LastSeenAt.HasValue &&
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
        await Exchange(original, consent, new(planId, "AWAITING_RESTART", "INSTALLER_BUSY"));
        Check((await Read()).Plan is { State: "AWAITING_RESTART", Reason: "INSTALLER_BUSY", RollbackConfirmation: null },
            "an installer-busy receipt remains distinct from installation completion");
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
        Check(second.Plan is { ValidPreviousVersion: next, RollbackConfirmation: null }, "the approved task carries its own previous version");
        await Exchange(next, consent, new(second.Plan!.Id, "INSTALLING", null));
        context = await StartSession(context.GetProperty("statusEpoch").GetInt32());
        sequence = 0;
        await Exchange(later, consent);
        Check((await Read()).Plan?.State == "INSTALLING", "new runtime without the task's installer receipt cannot mark success");
        await Exchange(later, consent, new(second.Plan.Id, "FAILED", "ROLLED_BACK"));
        var reported = await Read();
        Check(reported.Plan is { State: "FAILED", Reason: "ROLLED_BACK", ValidPreviousVersion: next, RollbackConfirmation: null } &&
            reported.ObservedVersion == later, "the installer report does not prove the old version is running");

        context = await StartSession(context.GetProperty("statusEpoch").GetInt32());
        sequence = 0;
        await ReportStatus(next);
        Check((await Read()).Plan is { HasRollbackReport: true, RollbackConfirmation: null },
            "a normal heartbeat from the previous version is not task recovery proof");
        await Exchange(next, consent);
        var recovered = await Read();
        var confirmation = recovered.Plan?.RollbackConfirmation ?? throw new InvalidDataException("Expected task recovery confirmation.");
        Check(recovered.Plan is { State: "FAILED", Reason: "ROLLED_BACK" } && recovered.Plan.Id == second.Plan.Id &&
            confirmation.Version == next && confirmation.ConfirmedAt.Offset == TimeSpan.Zero && recovered.ObservedVersion == next,
            "a restarted original runtime confirms this task while its upgrade remains failed");
        await Exchange(next, consent);
        Check((await Read()).Plan?.RollbackConfirmation == confirmation, "regular later exchanges preserve the first confirmation time");

        context = await StartSession(context.GetProperty("statusEpoch").GetInt32());
        sequence = 0;
        await ReportStatus(original);
        await Exchange(original, consent);
        var newerObservation = await Read();
        Check(newerObservation.ObservedVersion == original && newerObservation.Plan?.RollbackConfirmation == confirmation &&
            newerObservation.EligibleReleases.Any(release => release.Version == later),
            "the latest running version and the earlier confirmation stay separate without disabling approval");

        var third = await admin.SetSoftwareUpdateAsync(newerObservation, SoftwareUpdateAction.Approve, later, Guid.NewGuid(), ct);
        Check(third.Plan is { ValidPreviousVersion: original, RollbackConfirmation: null } && third.Plan.Id != second.Plan.Id,
            "confirmation is never inherited by a new task");
        await Exchange(original, consent, new(third.Plan!.Id, "INSTALLING", null));
        await Exchange(original, consent, new(third.Plan.Id, "FAILED", "ROLLBACK_SUCCEEDED"));
        Check((await Read()).Plan is { HasRollbackReport: true, RollbackConfirmation: null },
            "the original install run cannot prove the successful restart of either rollback reason");

        await admin.LogoutAsync(ct);
        Console.WriteLine("PASS: KV invitation withdrawal, offline and historical/current device exchange, old consent rejected, reissue kept, and consent-won enrollment kept.");
        Console.WriteLine("PASS: KV software maintenance school/device scope, consent, cancellation, installer busy/restart, rollback report vs original runtime, later version, and proof tied to one task.");

        Task<SoftwareUpdate> Withdraw(SoftwareUpdate snapshot) =>
            admin.SetSoftwareUpdateAsync(snapshot, SoftwareUpdateAction.WithdrawInvitation, null, Guid.NewGuid(), ct);

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

        Task<JsonElement> Exchange(string runningVersion, Guid? enrollmentId, Receipt? receipt = null) =>
            DeviceRequest("software-update/exchange", "0.18", requestId =>
                new { requestId, context, sequence = ++sequence, runningVersion, localConsent = enrollmentId.HasValue, consentEnrollmentId = enrollmentId, receipt });

        Task<JsonElement> ReportStatus(string version) => DeviceRequest("status", "0.1", requestId =>
        {
            var bridge = new { connection = "UNKNOWN", bridgeVersion = (string?)null };
            return new { requestId, fixture.DeviceIdentity.ServerInstanceId, fixture.DeviceIdentity.DeploymentEpoch,
                sessionId = context.GetProperty("sessionId").GetGuid(), statusEpoch = context.GetProperty("statusEpoch").GetInt32(),
                sequence = 1, sampleAgeMs = 0, status = new { appVersion = version, mode = "DAILY", modePhase = "RUNNING",
                    modeRevision = 1, automaticRecording = "DISABLED", recording = "IDLE", classIsland = bridge, examAware = bridge } };
        });

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

    private static void VerifyLegacyInvitation()
    {
        const string json = """
            {"product":"NPEduTools","managed":false,"enrollment":"INVITED",
             "enrollmentId":"14ac6dfc-4310-4365-8b5b-90f01f569b33","revision":1,
             "catalog":[],"plan":null,"observedVersion":"1.1.10.0","lastSeenAt":"2026-01-01T00:00:00Z"}
            """;
        var legacy = JsonSerializer.Deserialize<SoftwareUpdate>(json, JsonSerializerOptions.Web);
        Check(legacy is { CanWithdraw: false, CanWithdrawInvitation: null, InvitationLastExchangeAt: null },
            "legacy JSON without withdrawal capability remains readable but cannot be withdrawn");
    }

    private static void VerifyRollbackContract()
    {
        const string json = """
            {"product":"NPEduTools","managed":true,"enrollment":"MANAGED",
             "enrollmentId":"14ac6dfc-4310-4365-8b5b-90f01f569b33","revision":3,
             "catalog":[{"version":"1.1.10.0"},{"version":"1.1.11.0"}],
             "plan":{"id":"862c4efc-fa08-4206-9bcd-28b099c2934b","targetVersion":"1.1.11.0","state":"FAILED",
                     "reason":"ROLLED_BACK","updatedAt":"2026-01-01T00:00:00Z"},
             "observedVersion":"1.1.10.0","lastSeenAt":"2026-01-01T00:00:01Z"}
            """;
        var legacy = JsonSerializer.Deserialize<SoftwareUpdate>(json, JsonSerializerOptions.Web)!;
        Check(legacy.Plan is { PreviousVersion: null, RollbackConfirmedAt: null, HasRollbackReport: true, RollbackConfirmation: null } &&
            legacy.EligibleReleases.Any(), "older responses retain their conservative presentation and existing operations");
        var plan = legacy.Plan! with { PreviousVersion = "1.1.10.0", RollbackConfirmedAt = "2026-01-01T00:00:01.123Z" };
        Check(plan.RollbackConfirmation == new SoftwareRollbackConfirmation("1.1.10.0",
            new DateTimeOffset(2026, 1, 1, 0, 0, 1, 123, TimeSpan.Zero)), "valid task-scoped UTC confirmation is read exactly");
        Check((plan with { Reason = "ROLLBACK_SUCCEEDED", RollbackConfirmedAt = "2026-01-01T00:00:01+00:00" }).RollbackConfirmation is not null,
            "both rollback reasons and an explicit zero offset are accepted");
        foreach (var invalid in new[]
        {
            plan with { PreviousVersion = null }, plan with { PreviousVersion = "1.1.10" }, plan with { PreviousVersion = "1.1.12.0" },
            plan with { PreviousVersion = plan.TargetVersion }, plan with { RollbackConfirmedAt = null },
            plan with { RollbackConfirmedAt = "2026-01-01T00:00:01" }, plan with { RollbackConfirmedAt = "2026-01-01T08:00:01+08:00" },
            plan with { RollbackConfirmedAt = "invalid" }, plan with { Id = Guid.Empty.ToString("D") },
            plan with { State = "SUCCEEDED" }, plan with { Reason = "RECOVERY_REQUIRED" }
        }) Check(invalid.RollbackConfirmation is null, "incomplete or unrelated rollback data is never shown as confirmation");
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
