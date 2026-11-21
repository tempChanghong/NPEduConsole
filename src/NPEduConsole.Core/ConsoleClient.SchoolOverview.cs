namespace NPEduConsole.Core;

public sealed record SchoolOverviewTerm(string Id, string Name);
public sealed record SchoolOverviewCounts(int Grades, int AdministrativeClasses, int Screens);
public sealed record SchoolScreenObservation(string State, DateTimeOffset? LastReportedAt);
public sealed record SchoolOverviewScreen(string ClassId, string ClassName, string ScreenId, string ScreenName,
    SchoolScreenObservation Browser, SchoolScreenObservation Native);
public sealed record SchoolOverviewTask(string Code, string Source, string Severity, string Message, string Action,
    string? ClassId = null, string? ScreenId = null, string? TargetTab = null, DateTimeOffset? LastReportedAt = null, string? SubjectId = null,
    string? GradeId = null, string? AccountId = null, string? CourseGroupId = null);
public sealed record SchoolOverview(School School, SchoolOverviewTerm? Term, SchoolOverviewCounts Counts,
    IReadOnlyList<SchoolOverviewScreen> Screens, IReadOnlyList<SchoolOverviewTask> Tasks, DateTimeOffset GeneratedAt,
    string? AdminUrl = null);

public sealed partial class ConsoleClient
{
    public async Task<SchoolOverview> GetSchoolOverviewAsync(string schoolId, CancellationToken ct)
    {
        var session = CurrentSession;
        var overview = await RequestAsync<SchoolOverview>(HttpMethod.Get,
            $"api/v2/admin/schools/{Segment(schoolId)}/management-overview/current?staffActions=1&teacherAccountActions=1&courseGroupActions=1", session.Token, ct);
        CheckCreationSession(session, ct);
        if (overview.School?.Id != schoolId || overview.Counts is not { } counts || overview.Screens is null ||
            overview.Tasks is null || overview.GeneratedAt == default || counts.Grades < 0 || counts.AdministrativeClasses < 0 ||
            counts.Screens != overview.Screens.Count) throw InvalidResponse();
        if (overview.Term is null)
        {
            if (counts != new SchoolOverviewCounts(0, 0, 0) || !overview.Tasks.Any(task => task?.Code == "NO_ACTIVE_TERM"))
                throw InvalidResponse();
        }
        else if (string.IsNullOrWhiteSpace(overview.Term.Id) || string.IsNullOrWhiteSpace(overview.Term.Name))
            throw InvalidResponse();

        var screens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var screen in overview.Screens)
        {
            if (screen is null || string.IsNullOrWhiteSpace(screen.ClassId) || string.IsNullOrWhiteSpace(screen.ClassName) ||
                string.IsNullOrWhiteSpace(screen.ScreenId) || string.IsNullOrWhiteSpace(screen.ScreenName) ||
                !screens.TryAdd(screen.ScreenId, screen.ClassId) ||
                screen.Browser?.State is not ("NOT_APPLICABLE" or "AWAITING_LOGIN" or "AWAITING_HEARTBEAT" or "ONLINE" or "DEGRADED" or "OFFLINE" or "UNKNOWN") ||
                screen.Native?.State is not ("NOT_APPLICABLE" or "AWAITING_PAIRING" or "AWAITING_REPORT" or "REPORTED" or "REPAIR_REQUIRED" or "UNKNOWN") ||
                screen.Native.State == "REPORTED" && screen.Native.LastReportedAt is null)
                throw InvalidResponse();
        }
        foreach (var task in overview.Tasks)
        {
            if (task is null || string.IsNullOrWhiteSpace(task.Code) || string.IsNullOrWhiteSpace(task.Message) ||
                task.Action is not ("OPEN_WEB" or "OPEN_SCREEN" or "PREPARE_SCREEN" or "CREATE_GRADE" or "CREATE_CLASS" or "OPEN_TEACHING" or
                    "OPEN_CLASS_RESPONSIBILITY" or "OPEN_GRADE_RESPONSIBILITY" or "OPEN_TEACHER_ACCOUNT" or
                    "OPEN_COURSE_GROUP_SOURCE" or "OPEN_COURSE_GROUP_TEACHING") ||
                task.Action == "OPEN_COURSE_GROUP_TEACHING" && (overview.Term is null || string.IsNullOrWhiteSpace(task.CourseGroupId) || string.IsNullOrWhiteSpace(task.SubjectId)) ||
                task.Action == "OPEN_COURSE_GROUP_SOURCE" && (overview.Term is null || string.IsNullOrWhiteSpace(task.SubjectId) ||
                    string.IsNullOrWhiteSpace(task.ClassId) && string.IsNullOrWhiteSpace(task.CourseGroupId)) ||
                task.Action == "OPEN_TEACHER_ACCOUNT" && string.IsNullOrWhiteSpace(task.AccountId) ||
                task.Action == "OPEN_CLASS_RESPONSIBILITY" && (overview.Term is null || string.IsNullOrWhiteSpace(task.ClassId)) ||
                task.Action == "OPEN_GRADE_RESPONSIBILITY" && (overview.Term is null || string.IsNullOrWhiteSpace(task.GradeId)) ||
                task.Action == "OPEN_TEACHING" && (string.IsNullOrWhiteSpace(task.ClassId) || string.IsNullOrWhiteSpace(task.SubjectId)) ||
                task.Action == "PREPARE_SCREEN" && string.IsNullOrWhiteSpace(task.ClassId) ||
                task.Action == "OPEN_SCREEN" && (task.ScreenId is null || !screens.TryGetValue(task.ScreenId, out var room) || room != task.ClassId))
                throw InvalidResponse();
        }
        return overview;
    }
}
