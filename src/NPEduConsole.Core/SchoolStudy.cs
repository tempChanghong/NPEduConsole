using System.Text.Json;

namespace NPEduConsole.Core;

public sealed record StudyTargetLabel(string Id, string Name)
{
    public override string ToString() => Name;
}
public sealed record SchoolStudyRoom(string Id, string Name, string? GradeId, string? Code = null);
public sealed record SchoolStudyPolicy(string TargetType, string TargetId, int Revision, JsonElement Policy)
{
    public string Mode => Policy.GetProperty("mode").GetString()!;
    internal JsonElement? Settings => Mode == "Override" ? Policy.GetProperty("settings") : null;
    public StudyMonitoring Monitoring => StudyMonitoring.Read(Settings);
}
public sealed record SchoolStudyCatalog(string? TermId, StudyTargetLabel[] Terms, StudyTargetLabel[] Grades,
    SchoolStudyRoom[] Classes, SchoolStudyPolicy[] Settings)
{
    public SchoolStudyPolicy? Policy(string type, string id) =>
        Settings.FirstOrDefault(row => row.TargetType == type && row.TargetId == id);

    public SchoolStudyPolicy? BasePolicy(string type, string id)
    {
        var own = Policy(type, id);
        if (type != "CLASS" || own?.Mode is "Override" or "Disabled") return own;
        var room = Classes.FirstOrDefault(row => row.Id == id);
        return room?.GradeId is { } grade ? Policy("GRADE", grade) : null;
    }
}
public sealed record SchoolStudyDayMode(int Weekday, string? Exception, StudyWindow[] Windows);
public sealed record SchoolStudyDay(DateOnly Date, SchoolStudyDayMode Daily, SchoolStudyDayMode Exam);
public sealed record SchoolStudyCalendarRoom(string ClassId, string Name, string Source, string LocalMode,
    int PairedDevices, bool LegacyNoise, SchoolStudyDay[] Dates);
public sealed record SchoolStudyCalendar(string TermId, string GradeId, DateOnly From, int Days, SchoolStudyCalendarRoom[] Classes);
public sealed record StudyImpactPolicy(string Source, JsonElement? Settings = null)
{
    public StudyMonitoring Monitoring => StudyMonitoring.Read(Settings);
}
public sealed record StudyImpactRow(string ClassId, string Name, int PairedDevices, bool Changed, StudyImpactPolicy Before,
    StudyImpactPolicy Effective, string ReviewVersion, bool LegacyNoiseReplaced, bool LegacyNoiseBefore, bool LegacyNoiseAfter,
    StudyWindow[] CalendarWindows, StudyWindow[] CalendarExamWindows,
    StudyWindow[] BeforeWindows, StudyWindow[] BeforeExamWindows);
public sealed record SchoolStudyReview(string TermId, string TargetType, string TargetId, int BaseRevision, JsonElement Policy,
    int TotalClasses, int ChangedClasses, StudyImpactRow[] Items, string PreviewFingerprint, DateOnly? PreviewDate, int? Revision = null,
    StudyDisplaySnapshot? Display = null);
public sealed record SchoolStudyEdit(string SchoolId, string TermId, string TargetType, string TargetId, int Revision, JsonElement Policy,
    bool DisplayOnly = false, SchoolStudyPolicy? InheritedGrade = null);
public sealed record StudySeatingVersion(int Revision, string? Hash, int? Width, int? Height, DateTimeOffset? CreatedAt, bool Restorable,
    string Source, string? DeviceId);
public sealed record StudySeatingBinding(string BindingId, string Name, string? DeviceId);
public sealed record StudySeatingObservation(string DeviceId, string? BindingId, int Revision, string? AppliedHash,
    bool Visible, int? RenderAgeMs, DateTimeOffset? ReceivedAt, bool Fresh, long? ObservationAgeMs, bool Current,
    bool Downloaded, DateTimeOffset? DownloadedAt, int? DownloadedRevision, long? DownloadedAgeMs);
public sealed record SchoolStudySeating(StudySeatingVersion Current, StudySeatingVersion[] Versions,
    StudySeatingObservation[] Receipts, StudySeatingBinding[] Bindings);
public sealed record StudySeatingEdit(StudyEvidenceScope Scope, int Revision, string Action, byte[]? Bytes = null, int? RestoreRevision = null);
