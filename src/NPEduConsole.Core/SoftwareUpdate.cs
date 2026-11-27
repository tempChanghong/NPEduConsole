using System.Diagnostics;
using System.Globalization;

namespace NPEduConsole.Core;

public enum SoftwareUpdateAction { Enroll, Approve, Cancel, WithdrawInvitation }

public sealed record SoftwareRelease(string Version)
{
    internal static Version? Parse(string? text) =>
        System.Version.TryParse(text, out var value) && value.Revision >= 0 &&
        value.Major <= 65535 && value.Minor <= 65535 && value.Build <= 65535 && value.Revision <= 65535 &&
        value.ToString(4) == text ? value : null;
}

public sealed record SoftwareRollbackConfirmation(string Version, DateTimeOffset ConfirmedAt);

public sealed record SoftwareUpdatePlan(string Id, string TargetVersion, string State, string? Reason, DateTimeOffset UpdatedAt)
{
    public string? PreviousVersion { get; init; }
    public string? RollbackConfirmedAt { get; init; }
    public bool IsActive => State is "APPROVED" or "DEFERRED" or "INSTALLING" or "AWAITING_RESTART";
    public bool CanCancel => State is "APPROVED" or "DEFERRED";
    public bool HasRollbackReport => State == "FAILED" && Reason is ("ROLLED_BACK" or "ROLLBACK_SUCCEEDED");
    public string? ValidPreviousVersion => SoftwareRelease.Parse(PreviousVersion) is { } previous &&
        SoftwareRelease.Parse(TargetVersion) is { } target && previous < target ? PreviousVersion : null;

    public SoftwareRollbackConfirmation? RollbackConfirmation
    {
        get
        {
            if (!HasRollbackReport || ValidPreviousVersion is not { } version ||
                !Guid.TryParseExact(Id, "D", out var id) || id == Guid.Empty || RollbackConfirmedAt is not { } timestamp ||
                !(timestamp.EndsWith('Z') || timestamp.EndsWith("+00:00", StringComparison.Ordinal)) ||
                !DateTimeOffset.TryParseExact(timestamp, ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"],
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var confirmedAt) || confirmedAt.Offset != TimeSpan.Zero)
                return null;

            return new(version, confirmedAt);
        }
    }

    internal bool IsKnown => IsActive || State is "SUCCEEDED" or "FAILED" or "CANCELLED";
}

internal sealed record SoftwareUpdateScope(Guid SessionId, string SchoolId, string DeviceId);
internal sealed record SoftwarePilotRead(DateTimeOffset StartedAt, long Timestamp);
public sealed record SoftwarePilotEvidence(DateTimeOffset? CheckedAt, DateTimeOffset? ObservedVersionValidUntil);
public sealed record SoftwarePilotPreflight(string Observation, string? ObservedVersion, DateTimeOffset? ObservedAt,
    string Enrollment, string CatalogStatus, string RecoveryPackage, IReadOnlyList<string> TargetVersions, bool HasActiveTask);

public sealed record SoftwareUpdate(string Product, bool Managed, string Enrollment, Guid? EnrollmentId, long Revision,
    IReadOnlyList<SoftwareRelease> Catalog, SoftwareUpdatePlan? Plan, string? ObservedVersion, DateTimeOffset? LastSeenAt,
    string? CatalogStatus = null, bool? CanWithdrawInvitation = null, DateTimeOffset? InvitationLastExchangeAt = null)
{
    internal SoftwareUpdateScope? Scope { get; init; }
    internal SoftwarePilotRead? PilotRead { get; init; }
    public SoftwarePilotEvidence? PilotEvidence { get; init; }

    public bool CanInvite => !Managed && Enrollment == "UNMANAGED" && EnrollmentId is null;
    public bool CanWithdraw => CanWithdrawInvitation == true && !Managed && Enrollment == "INVITED" &&
        EnrollmentId is { } invitation && invitation != Guid.Empty;
    public bool CanCancel => IsEnrolled && Plan?.CanCancel == true;
    public bool IsEnrolled => Managed && Enrollment == "MANAGED" && EnrollmentId is not null;

    public SoftwarePilotPreflight GetPilotPreflight(DateTimeOffset now)
    {
        var observed = SoftwareRelease.Parse(ObservedVersion);
        bool hasObservation = observed is not null && LastSeenAt is not null;
        bool current = hasObservation && HasCurrentPilotObservation(now);
        bool pending = !Managed && Enrollment == "INVITED" && EnrollmentId is { } invitation && invitation != Guid.Empty;
        string enrollment = IsEnrolled && EnrollmentId != Guid.Empty ? "MANAGED" :
            pending ? "INVITED" : CanInvite ? "UNMANAGED" : "UNKNOWN";
        string catalog = CatalogStatus is "AVAILABLE" or "NOT_CONFIGURED" or "UNAVAILABLE" ? CatalogStatus : "UNKNOWN";
        bool knownCatalog = current && catalog == "AVAILABLE";
        string recovery = !knownCatalog ? "UNKNOWN" : Catalog.Any(entry => entry.Version == ObservedVersion) ? "IN_CATALOG" : "MISSING";
        var targets = knownCatalog ? Catalog.Where(entry => SoftwareRelease.Parse(entry.Version) > observed)
            .Select(entry => entry.Version).ToArray() : [];
        return new(current ? "CURRENT" : hasObservation ? "HISTORICAL" : "UNKNOWN", hasObservation ? ObservedVersion : null,
            hasObservation ? LastSeenAt : null, enrollment, catalog, recovery, targets, Plan?.IsActive == true);
    }

    private bool HasCurrentPilotObservation(DateTimeOffset now)
    {
        if (PilotRead is not { } read || LastSeenAt is not { } observedAt ||
            PilotEvidence is not { CheckedAt: { } checkedAt, ObservedVersionValidUntil: { } validUntil }) return false;
        var lifetime = validUntil - checkedAt;
        // Deduct the entire read time, without assuming the local and server clocks agree.
        return observedAt <= checkedAt && validUntil - observedAt <= TimeSpan.FromSeconds(60) && lifetime > TimeSpan.Zero &&
            now - read.StartedAt < lifetime && Stopwatch.GetElapsedTime(read.Timestamp) < lifetime;
    }

    public IEnumerable<SoftwareRelease> EligibleReleases
    {
        get
        {
            if (!IsEnrolled || SoftwareRelease.Parse(ObservedVersion) is not { } installed ||
                CatalogStatus is not (null or "AVAILABLE") ||
                !Catalog.Any(release => release.Version == ObservedVersion) ||
                Plan is { } plan && (plan.IsActive || !plan.IsKnown))
                return [];
            return Catalog.Where(entry => SoftwareRelease.Parse(entry.Version) is { } version && version > installed);
        }
    }
}
