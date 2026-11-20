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

public sealed record SoftwareUpdate(string Product, bool Managed, string Enrollment, Guid? EnrollmentId, long Revision,
    IReadOnlyList<SoftwareRelease> Catalog, SoftwareUpdatePlan? Plan, string? ObservedVersion, DateTimeOffset? LastSeenAt,
    string? CatalogStatus = null, bool? CanWithdrawInvitation = null, DateTimeOffset? InvitationLastExchangeAt = null)
{
    internal SoftwareUpdateScope? Scope { get; init; }

    public bool CanInvite => !Managed && Enrollment == "UNMANAGED" && EnrollmentId is null;
    public bool CanWithdraw => CanWithdrawInvitation == true && !Managed && Enrollment == "INVITED" &&
        EnrollmentId is { } invitation && invitation != Guid.Empty;
    public bool CanCancel => IsEnrolled && Plan?.CanCancel == true;
    public bool IsEnrolled => Managed && Enrollment == "MANAGED" && EnrollmentId is not null;

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
