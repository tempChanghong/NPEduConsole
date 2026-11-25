namespace NPEduConsole.Core;

public sealed record ClassroomActionRule(string Mode, string? Factor);
public sealed record ClassroomActionRules(ClassroomActionRule? StopScheduledMonitoring, ClassroomActionRule? ExitBackground,
    ClassroomActionRule? SwitchClassroomMode = null, ClassroomActionRule? StartManualStudy = null);
public sealed record ClassroomActionOverride(ClassroomActionRule? Rule);
public sealed record ClassroomActionOverrides(ClassroomActionOverride? StopScheduledMonitoring, ClassroomActionOverride? ExitBackground,
    ClassroomActionOverride? SwitchClassroomMode, ClassroomActionOverride? StartManualStudy = null)
{
    internal bool HasOverrides => StopScheduledMonitoring is not null || ExitBackground is not null ||
        SwitchClassroomMode is not null || StartManualStudy is not null;
}
public sealed record ClassroomActionOrigins(string StopScheduledMonitoring, string ExitBackground, string SwitchClassroomMode,
    string? StartManualStudy = null);
public sealed record ClassroomActionTerm(string Id, string Name);
public sealed record ClassroomActionGrade(string Id, string Name, string Code)
{
    public override string ToString() => $"{Name} · {Code}";
}
public sealed record ClassroomActionClass(string Id, string Name, string Code, string TermId, string? GradeId)
{
    public override string ToString() => $"{Name} · {Code}";
}
public sealed record ClassroomActionCatalog(string SchoolId, ClassroomActionTerm? Term, ClassroomActionGrade[] Grades, ClassroomActionClass[] Rooms,
    bool SupportsClassroomModeSwitch = false, bool SupportsClassroomActionInheritance = false, bool SupportsManualStudyPolicy = false);
public sealed record ClassroomActionTarget(string TermId, string ScopeKind, string ScopeId);
public sealed record ClassroomUsbKey(string KeyId, string Label);
public sealed record ClassroomActionHistoricalTerm(string Id, string Name, string Status);
public sealed record ClassroomHistoricalUsbKey(string KeyId, string Label, ClassroomActionHistoricalTerm Term, string ScopeKind, string ScopeId, string ScopeName, string ScopeState)
{
    public ClassroomActionTarget Target => new(Term.Id, ScopeKind, ScopeId);
}
public sealed record ClassroomHistoricalUsbRegistry(string SchoolId, ClassroomHistoricalUsbKey[] Keys);
public sealed record ClassroomUsbMaterial(string KeyId, string MediaHash, string SecretHash);
public sealed record ClassroomUsbProof(string KeyId, string Label, string MediaHash, string SecretHash, ClassroomUsbMaterial? Replaces = null);
public sealed record ClassroomActionChange(string TermId, string ScopeKind, string ScopeId, string Operation,
    ClassroomActionRules? Actions, ClassroomUsbProof? UsbKey, string? KeyId, bool IncludeClassroomModeSwitch = false,
    ClassroomActionOverrides? ClassActionOverrides = null, bool IncludeManualStudyPolicy = false)
{
    public ClassroomActionTarget Target => new(TermId, ScopeKind, ScopeId);
    public bool RestoresInheritedPolicy => Operation == "SET_POLICY" && Actions is null && ClassActionOverrides?.HasOverrides != true;
}
public sealed record ClassroomEffectiveAction(string Source, ClassroomActionRules? Actions, string PolicyId, int UsbCount, long RequiredRevision,
    ClassroomActionOrigins? ActionOrigins = null, int? MinimumReceiptSchema = null);
public sealed record ClassroomActionRoom(string Id, string Name, string Code, bool PasswordConfigured,
    ClassroomEffectiveAction Before, ClassroomEffectiveAction After);
public sealed record ClassroomActionDeviceReceipt(int SchemaVersion, long? Revision, string? PolicyId, string? Error);
public sealed record ClassroomActionScreen(string ClassId, string BindingId, string BindingName, long BindingRevision,
    string? DeviceId, string? DeviceName, DateTimeOffset? ObservedAt, bool ObservationCurrent, ClassroomActionDeviceReceipt? Observed,
    ClassroomActionDeviceReceipt? LastApplied, DateTimeOffset? LastAppliedAt);
public record ClassroomActionScope(string SchoolId, string TermId, string ScopeKind, string ScopeId, string ScopeName,
    ClassroomActionRules? SavedActions, ClassroomActionRoom[] Rooms, ClassroomActionScreen[] Screens, ClassroomUsbKey[] UsbKeys)
{
    public ClassroomActionOverrides? SavedClassActionOverrides { get; init; }
    public ClassroomActionRules? InheritedGradeActions { get; init; }
}
public sealed record ClassroomActionIssue(string ClassId, string Action, bool PasswordConfigured, int UsbCount);
public sealed record ClassroomActionPreview(string SchoolId, string TermId, string ScopeKind, string ScopeId, string ScopeName,
    ClassroomActionRules? SavedActions, ClassroomActionRoom[] Rooms, ClassroomActionScreen[] Screens, ClassroomUsbKey[] UsbKeys,
    string Version, ClassroomActionIssue[] Issues, bool CanApply, ClassroomActionHistoricalTerm? HistoricalTerm = null, string? HistoricalScopeState = null)
    : ClassroomActionScope(SchoolId, TermId, ScopeKind, ScopeId, ScopeName, SavedActions, Rooms, Screens, UsbKeys);
public sealed record ClassroomActionReceipt(Guid RequestId, string SchoolId, string TermId, string ScopeId, string ScopeKind,
    string Operation, long Revision, string? KeyId, int ChangedClasses);
