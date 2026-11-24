namespace NPEduConsole.Core;

public sealed record SchoolOverviewTerm(string Id, string Name, string? Status = null)
{
    public string StatusLabel => Status switch { "ACTIVE" => "启用中", "DRAFT" => "草稿", "ARCHIVED" => "已归档", _ => "状态待核对" };
}
public sealed record SchoolOverviewCounts(int Grades, int AdministrativeClasses, int Screens);
public sealed record SchoolOverviewMetrics(int CourseGroups, int Teachers, int OnlineScreens, int BrowserScreens, int Errors, int Warnings);
public sealed record SchoolScreenObservation(string State, DateTimeOffset? LastReportedAt);
public sealed record SchoolOverviewScreen(string ClassId, string ClassName, string ScreenId, string ScreenName,
    SchoolScreenObservation Browser, SchoolScreenObservation Native);
public sealed record SchoolOverviewTask(string Code, string Source, string Severity, string Message, string Action,
    string? ClassId = null, string? ScreenId = null, string? TargetTab = null, DateTimeOffset? LastReportedAt = null, string? SubjectId = null,
    string? GradeId = null, string? AccountId = null, string? CourseGroupId = null, string? SourceTermId = null);
public sealed record SchoolOverview(School School, SchoolOverviewTerm? Term, SchoolOverviewCounts Counts,
    IReadOnlyList<SchoolOverviewScreen> Screens, IReadOnlyList<SchoolOverviewTask> Tasks, DateTimeOffset GeneratedAt,
    string? AdminUrl = null, SchoolOverviewTerm? ActiveTerm = null, bool Scoped = false, bool Legacy = false,
    SchoolOverviewMetrics? Metrics = null);

public sealed partial class ConsoleClient
{
    private sealed record LegacyOverviewDiagnostic(string Code, string Source, string Severity, string Message, string? WorkspaceId = null,
        string? AdministrativeClassId = null, string? SubjectId = null, string? GradeId = null, string? AccountId = null,
        string? ScreenId = null, DateTimeOffset? LastReportedAt = null);
    private sealed record ScopedOverviewReply(School School, SchoolOverviewTerm? Term, SchoolOverviewScreen[] Screens,
        DateTimeOffset GeneratedAt, SchoolOverviewTerm? ActiveTerm = null, SchoolOverviewCounts? Counts = null,
        SchoolOverviewTask[]? Tasks = null, string? AdminUrl = null, bool Scoped = false, SchoolOverviewCounts? Summary = null,
        LegacyOverviewDiagnostic[]? Diagnostics = null, SchoolOverviewMetrics? Metrics = null);

    public async Task<SchoolOverview> GetSchoolOverviewAsync(string schoolId, CancellationToken ct)
    {
        var session = CurrentSession;
        var overview = await RequestAsync<SchoolOverview>(HttpMethod.Get,
            $"api/v2/admin/schools/{Segment(schoolId)}/management-overview/current?staffActions=1&staffReview=1&teacherAccountActions=1&managementAccountActions=1&courseGroupActions=1&termPreparation=1&screenPlacement=1&screenDuty=1&structureActions=1&teacherAccessActions=1", session.Token, ct);
        CheckCreationSession(session, ct);
        return ValidateOverview(overview, schoolId);
    }

    public async Task<SchoolOverview> GetTermOverviewAsync(string schoolId, string termId, CancellationToken ct)
    {
        var session = CurrentSession;
        var reply = await RequestAsync<ScopedOverviewReply>(HttpMethod.Get,
            $"api/v2/admin/schools/{Segment(schoolId)}/management-overview?termId={Segment(termId)}&desktop=1", session.Token, ct);
        CheckCreationSession(session, ct);
        if (reply.School?.Id != schoolId || reply.Term?.Id != termId || reply.Term.Status is not ("ACTIVE" or "DRAFT" or "ARCHIVED"))
            throw InvalidResponse();
        if (reply.Scoped && reply.Counts is { } counts && reply.Tasks is { } tasks &&
            (reply.ActiveTerm is null || reply.ActiveTerm.Status == "ACTIVE") &&
            (reply.Term.Status == "ACTIVE") == (reply.ActiveTerm?.Id == termId))
            return ValidateOverview(new(reply.School, reply.Term, counts, reply.Screens, tasks, reply.GeneratedAt, reply.AdminUrl,
                reply.ActiveTerm, Scoped: true, Metrics: reply.Metrics), schoolId);
        if (reply.Scoped || reply.Summary is not { } summary || reply.Diagnostics is not { } diagnostics) throw InvalidResponse();
        var catalog = await GetTermPreparationAsync(schoolId, ct);
        CheckCreationSession(session, ct);
        if ((catalog.ActiveTerm?.Id == termId) != (reply.Term.Status == "ACTIVE") ||
            catalog.Terms?.FirstOrDefault(term => term.Id == termId) is { } recent && recent.Status != reply.Term.Status)
            throw new ConsoleApiException(409, "读取期间学期状态已变化，请重新核对。");
        var active = catalog.ActiveTerm is { } current ? new SchoolOverviewTerm(current.Id, current.Name, "ACTIVE") : null;
        return ValidateOverview(new(reply.School, reply.Term, summary, reply.Screens, diagnostics.Select(LegacyTask).ToArray(),
            reply.GeneratedAt, catalog.AdminUrl, active, Scoped: true, Legacy: true), schoolId);
    }

    private static SchoolOverviewTask LegacyTask(LegacyOverviewDiagnostic item)
    {
        string action = item.Code switch
        {
            "TERM_WITHOUT_GRADE" => "CREATE_GRADE",
            "TERM_WITHOUT_ADMIN_CLASS" => "CREATE_CLASS",
            "ADMIN_CLASS_SUBJECT_WITHOUT_TEACHER" when item.AdministrativeClassId is not null && item.SubjectId is not null => "OPEN_TEACHING",
            "WALKING_SUBJECT_WITHOUT_GROUP" when item.AdministrativeClassId is not null && item.SubjectId is not null => "OPEN_COURSE_GROUP_SOURCE",
            "COURSE_GROUP_WITHOUT_TEACHER" when item.WorkspaceId is not null && item.SubjectId is not null => "OPEN_COURSE_GROUP_TEACHING",
            "COURSE_GROUP_WITHOUT_SOURCE" or "COURSE_GROUP_SOURCE_CONFLICT" when item.WorkspaceId is not null && item.SubjectId is not null => "OPEN_COURSE_GROUP_SOURCE",
            _ when item.Source == "RESPONSIBILITY" && (item.GradeId is not null || item.AdministrativeClassId is not null) => "OPEN_STAFF_REVIEW",
            _ => "INSPECT"
        };
        string? group = item.Code is "COURSE_GROUP_WITHOUT_TEACHER" or "COURSE_GROUP_WITHOUT_SOURCE" or "COURSE_GROUP_SOURCE_CONFLICT" ?
            item.WorkspaceId : null;
        return new(item.Code, item.Source, item.Severity, item.Message, action, item.AdministrativeClassId, item.ScreenId,
            LastReportedAt: item.LastReportedAt, SubjectId: item.SubjectId, GradeId: item.GradeId, AccountId: item.AccountId, CourseGroupId: group);
    }

    private static bool KnownOverviewAction(string? action) => action is "OPEN_WEB" or "OPEN_SCREEN" or "PREPARE_SCREEN" or "CREATE_GRADE" or
        "CREATE_CLASS" or "OPEN_TEACHING" or "OPEN_CLASS_RESPONSIBILITY" or "OPEN_GRADE_RESPONSIBILITY" or "OPEN_STAFF_REVIEW" or
        "OPEN_TEACHER_ACCOUNT" or "OPEN_MANAGEMENT_ACCOUNT" or "OPEN_COURSE_GROUP_SOURCE" or "OPEN_COURSE_GROUP_TEACHING" or "OPEN_TERM_PREPARATION" or
        "OPEN_TERM_SCREEN_PLACEMENT" or "OPEN_SCREEN_DUTY" or "OPEN_TERM_TEACHING" or "OPEN_TEACHER_ACCESS" or "INSPECT";

    private static SchoolOverview ValidateOverview(SchoolOverview overview, string schoolId)
    {
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
        if (overview.Metrics is { } metrics &&
            (overview.Term is null || metrics.CourseGroups < 0 || metrics.Teachers < 0 || metrics.Errors < 0 || metrics.Warnings < 0 ||
                metrics.OnlineScreens != overview.Screens.Count(screen => screen.Browser.State == "ONLINE") ||
                metrics.BrowserScreens != overview.Screens.Count(screen => screen.Browser.State != "NOT_APPLICABLE")))
            throw InvalidResponse();
        foreach (var task in overview.Tasks)
        {
            if (task is null || string.IsNullOrWhiteSpace(task.Code) || string.IsNullOrWhiteSpace(task.Message) ||
                task.Action == "OPEN_TERM_SCREEN_PLACEMENT" && (overview.Term is null || string.IsNullOrWhiteSpace(task.ScreenId)) ||
                task.Action == "OPEN_COURSE_GROUP_TEACHING" && (overview.Term is null || string.IsNullOrWhiteSpace(task.CourseGroupId) || string.IsNullOrWhiteSpace(task.SubjectId)) ||
                task.Action == "OPEN_COURSE_GROUP_SOURCE" && (overview.Term is null || string.IsNullOrWhiteSpace(task.SubjectId) ||
                    string.IsNullOrWhiteSpace(task.ClassId) && string.IsNullOrWhiteSpace(task.CourseGroupId)) ||
                task.Action is "OPEN_TEACHER_ACCOUNT" or "OPEN_MANAGEMENT_ACCOUNT" && string.IsNullOrWhiteSpace(task.AccountId) ||
                task.Action == "OPEN_TEACHER_ACCESS" && (overview.Term is null || string.IsNullOrWhiteSpace(task.SubjectId) ||
                    string.IsNullOrWhiteSpace(task.AccountId) || string.IsNullOrWhiteSpace(task.ClassId) && string.IsNullOrWhiteSpace(task.CourseGroupId)) ||
                task.Action == "OPEN_TERM_TEACHING" && !overview.Scoped && (overview.Term is null ||
                    string.IsNullOrWhiteSpace(task.GradeId) || string.IsNullOrWhiteSpace(task.SubjectId) ||
                    string.IsNullOrWhiteSpace(task.ClassId) && string.IsNullOrWhiteSpace(task.CourseGroupId)) ||
                task.Action == "OPEN_CLASS_RESPONSIBILITY" && (overview.Term is null || string.IsNullOrWhiteSpace(task.ClassId)) ||
                task.Action == "OPEN_GRADE_RESPONSIBILITY" && (overview.Term is null || string.IsNullOrWhiteSpace(task.GradeId)) ||
                task.Action == "OPEN_STAFF_REVIEW" && overview.Term is null ||
                task.Action == "OPEN_TEACHING" && (string.IsNullOrWhiteSpace(task.ClassId) || string.IsNullOrWhiteSpace(task.SubjectId)) ||
                task.Action == "PREPARE_SCREEN" && string.IsNullOrWhiteSpace(task.ClassId) ||
                task.Action is ("OPEN_SCREEN" or "OPEN_SCREEN_DUTY") && (task.ScreenId is null || !screens.TryGetValue(task.ScreenId, out var room) || room != task.ClassId))
                throw InvalidResponse();
        }
        return overview with {Tasks = overview.Tasks.Select(task => KnownOverviewAction(task.Action) ? task : task with {Action = "INSPECT"}).ToArray()};
    }
}
