using NPEduConsole.Core;

namespace NPEduConsole.App;

internal enum OpeningStage { Organization, Teachers, Classrooms }
internal enum OpeningAction { Terms, Import, Class, ClassDirectory, Accounts, ManagementAccounts, Access, StaffConfiguration, Teaching, Duty, StaffReview, CourseGroups, Screen, InactiveScreens, Placement }
internal sealed record OpeningTarget(OpeningAction Action, string? ClassId = null, string? GradeId = null,
    string? SubjectId = null, string? AccountId = null, string? GroupId = null, string? BindingId = null,
    string? IssueKey = null, bool CreateGrade = false);

public sealed record SchoolOpeningRow(string Key, string Title, string Summary, string Evidence, string? ActionLabel = null)
{
    internal OpeningTarget? Target { get; init; }
}

internal sealed record SchoolOpeningSnapshot(SchoolOverview Overview, TeacherAllocationCatalog? Teachers, ScreenDutyCatalog? Devices)
{
    internal static string RecordTime(DateTimeOffset? value) => value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "暂无记录";

    internal string Summary(OpeningStage stage) => stage switch
    {
        OpeningStage.Organization => Overview.Term is null ? "请先在学期管理核对启用学期。" :
            $"{Overview.Counts.Grades} 个年级 · {Overview.Counts.AdministrativeClasses} 个启用行政班",
        OpeningStage.Teachers when Overview.Term is null => "当前没有启用学期。可先准备教师账号。",
        OpeningStage.Teachers when Teachers is { } staff =>
            $"{staff.Accounts.Count(row => row.Manageable && !row.Disabled)} 位可分配教师 · {staff.Members.Length} 条直接访问 · " +
            $"{staff.Teaching.Length} 条任课 · {staff.Duties.Length} 条岗位",
        OpeningStage.Teachers => "教师分配目录待核对。已读到的学校待办仍可查看。",
        OpeningStage.Classrooms when Overview.Term is null => "当前没有启用学期。教室接入待核对。",
        OpeningStage.Classrooms => $"{Overview.Counts.Screens} 台启用大屏 · 按绑定核对网页登录、当前心跳、配对和上报" +
            (Devices is null ? " · 详细证据待核对" : ""),
        _ => "",
    };

    internal IEnumerable<SchoolOpeningRow> Rows(OpeningStage stage) => stage switch
    {
        OpeningStage.Organization => Issues(stage).Concat(OrganizationRows()),
        OpeningStage.Teachers => Issues(stage).Concat(TeacherRows()),
        OpeningStage.Classrooms => Issues(stage).Concat(ScreenRows()),
        _ => [],
    };

    private IEnumerable<SchoolOpeningRow> OrganizationRows()
    {
        if (Teachers is not { } data) yield break;
        foreach (var room in data.Workspaces)
        {
            if (!room.IsGroup)
            {
                yield return new($"class/{room.Id}", $"{room.Name}（{room.Code}）", "行政班 · 已启用",
                    "此班属于当前启用学期。可继续核对班级资料和学生名单。", "查看班级与名单")
                    { Target = new(OpeningAction.ClassDirectory, ClassId: room.Id, GradeId: room.GradeId) };
                continue;
            }
            var sources = room.SourceClasses.Length;
            yield return new($"group/{room.Id}", $"{room.Name}（{room.Code}）", $"走班 · {room.SubjectName ?? "学科待核对"}",
                $"服务器当前记录 {sources} 个来源班。科目来源及任课可分别核对。", "查看该走班")
                { Target = new(OpeningAction.CourseGroups, SubjectId: room.SubjectId, GroupId: room.Id) };
        }
    }

    private IEnumerable<SchoolOpeningRow> Issues(OpeningStage stage)
    {
        var accounts = Teachers?.Accounts.ToDictionary(row => row.Id);
        var duties = Teachers?.Duties.ToLookup(row => (row.Type, row.TargetId, row.AccountId));
        foreach (var task in Overview.Tasks.Where(task => BelongsTo(task, stage)))
        {
            var key = string.Join("/", "issue", task.Code, task.ClassId, task.GradeId, task.SubjectId,
                task.CourseGroupId, task.AccountId, task.ScreenId);
            var (target, label) = IssueAction(task);
            var summary = task.Source switch
            {
                "RESPONSIBILITY" => "岗位待核对",
                "ACCOUNT" => "教师账号待核对",
                "TEACHING" => "教学安排待核对",
                "SCREEN" => "教室接入待核对",
                _ => "班级与课程待核对",
            };
            if (task.AccountId is { } accountId)
            {
                var account = accounts?.GetValueOrDefault(accountId);
                var identity = account?.Username ?? account?.Email ?? $"账号编号 {accountId}";
                summary += $" · {identity}";
                var duty = task.ClassId is { } classId ? duties?[("CLASS", classId, accountId)].FirstOrDefault() :
                    task.GradeId is { } gradeId ? duties?[("GRADE", gradeId, accountId)].FirstOrDefault() : null;
                if (task.Source == "RESPONSIBILITY" && duty is not null) summary += $" · 现任{duty.Title}";
            }
            yield return new(key, task.Message, summary, $"来源：学校当前学期诊断。核对时间 {RecordTime(Overview.GeneratedAt)}。", label)
                { Target = target };
        }
    }

    private static bool BelongsTo(SchoolOverviewTask task, OpeningStage stage) => stage switch
    {
        OpeningStage.Organization => task.Source == "TERM" || task.Source == "TEACHING" &&
            task.Code is "WALKING_SUBJECT_WITHOUT_GROUP" or "COURSE_GROUP_WITHOUT_SOURCE" or "COURSE_GROUP_SOURCE_CONFLICT",
        OpeningStage.Teachers => task.Source is "ACCOUNT" or "RESPONSIBILITY" || task.Source == "TEACHING" &&
            task.Code is not ("WALKING_SUBJECT_WITHOUT_GROUP" or "COURSE_GROUP_WITHOUT_SOURCE" or "COURSE_GROUP_SOURCE_CONFLICT"),
        OpeningStage.Classrooms => task.Source == "SCREEN" &&
            (task.ScreenId is null || task.Code == "TERM_SCREEN_RETAINED_ORIGINAL"),
        _ => false,
    };

    private static (OpeningTarget?, string?) IssueAction(SchoolOverviewTask task) => task.Action switch
    {
        "OPEN_TERM_PREPARATION" => (new(OpeningAction.Terms), "打开学期管理"),
        "CREATE_GRADE" => (new(OpeningAction.Class, CreateGrade: true), "新建年级"),
        "CREATE_CLASS" => (new(OpeningAction.Class), "进入建班"),
        "OPEN_COURSE_GROUP_SOURCE" => (new(OpeningAction.CourseGroups, ClassId: task.ClassId, SubjectId: task.SubjectId,
            GroupId: task.CourseGroupId), "核对走班来源"),
        "OPEN_COURSE_GROUP_TEACHING" when task.CourseGroupId is not null && task.SubjectId is not null =>
            (new(OpeningAction.Teaching, SubjectId: task.SubjectId, GroupId: task.CourseGroupId), "分配走班任课"),
        "OPEN_TEACHING" when task.ClassId is not null && task.SubjectId is not null =>
            (new(OpeningAction.Teaching, ClassId: task.ClassId, SubjectId: task.SubjectId, AccountId: task.AccountId), "分配任课"),
        "OPEN_CLASS_RESPONSIBILITY" => (new(OpeningAction.Duty, ClassId: task.ClassId, AccountId: task.AccountId), "核对班级岗位"),
        "OPEN_GRADE_RESPONSIBILITY" => (new(OpeningAction.Duty, GradeId: task.GradeId, AccountId: task.AccountId), "核对年级岗位"),
        "OPEN_STAFF_REVIEW" => (new(OpeningAction.StaffReview, ClassId: task.ClassId, GradeId: task.GradeId,
            AccountId: task.AccountId, IssueKey: StaffReviewIssue.IssueKey(task.Code, task.GradeId, task.ClassId, task.AccountId)), "核对师资"),
        "OPEN_TEACHER_ACCOUNT" => (new(OpeningAction.Accounts, AccountId: task.AccountId), "查看教师账号"),
        "OPEN_MANAGEMENT_ACCOUNT" => (new(OpeningAction.ManagementAccounts, AccountId: task.AccountId), "核对原账号与权限"),
        "PREPARE_SCREEN" => (new(OpeningAction.Screen, ClassId: task.ClassId), "登记本班大屏"),
        "OPEN_TERM_SCREEN_PLACEMENT" => (new(OpeningAction.Placement, BindingId: task.ScreenId), "安排至当前学期"),
        "OPEN_WEB" when task.Code == "ADMIN_CLASS_SCREEN_INACTIVE" && task.ClassId is not null =>
            (new(OpeningAction.InactiveScreens, ClassId: task.ClassId), "核对停用大屏"),
        _ => (null, null),
    };

    private IEnumerable<SchoolOpeningRow> TeacherRows()
    {
        if (Teachers is not { } data) yield break;
        var rooms = data.Workspaces.ToDictionary(row => row.Id);
        var accounts = data.Accounts.ToDictionary(row => row.Id);
        string Person(string accountId) => accounts.GetValueOrDefault(accountId)?.ToString() ?? "教师身份待核对";
        foreach (var row in data.Teaching)
        {
            if (!rooms.TryGetValue(row.WorkspaceId, out var room)) continue;
            var title = $"{room.Name}（{room.Code}） · {row.Subject.Name} · {Person(row.AccountId)}";
            var role = row.Position == "PRIMARY" ? "主讲" : "协同任课";
            var target = new OpeningTarget(OpeningAction.Teaching, ClassId: room.IsGroup ? null : room.Id,
                GradeId: room.GradeId, SubjectId: row.Subject.Id, AccountId: row.AccountId, GroupId: room.IsGroup ? room.Id : null);
            yield return new($"teaching/{room.Id}/{row.Subject.Id}/{row.AccountId}", title, $"任课 · {role}",
                $"服务器已记录该班科的{role}关系。直接教学空间访问和岗位另行核对。", "查看该班科任课") { Target = target };
        }
        foreach (var row in data.Duties)
        {
            var target = new OpeningTarget(OpeningAction.Duty, ClassId: row.Type == "CLASS" ? row.TargetId : null,
                GradeId: row.Type == "GRADE" ? row.TargetId : null, AccountId: row.AccountId);
            yield return new($"duty/{row.Type}/{row.TargetId}/{row.AccountId}", $"{row.Title} · {Person(row.AccountId)}",
                "岗位 · 已有记录", "服务器已记录该岗位。岗位和学科任课分别办理。", "查看该岗位") { Target = target };
        }
        foreach (var row in data.Members)
        {
            if (!rooms.TryGetValue(row.WorkspaceId, out var room)) continue;
            yield return new($"access/{room.Id}/{row.AccountId}", $"{room.Name}（{room.Code}） · {Person(row.AccountId)}",
                $"直接访问 · {Role(row.Role)}", "服务器已记录此账号的直接访问。请另行核对该教师是否有任课或岗位。", "查看空间访问")
                { Target = new(OpeningAction.Access, ClassId: room.Id, GradeId: room.GradeId) };
        }
        foreach (var row in data.Invitations.Where(row => row.ClaimedAt is null))
        {
            if (!rooms.TryGetValue(row.WorkspaceId, out var room)) continue;
            yield return new($"invite/{row.Id}", $"{room.Name}（{room.Code}） · {row.NormalizedEmail}", $"邮箱邀请 · 待认领",
                $"拟授予{Role(row.Role)}权限，等待符合条件的身份认领。", "查看空间访问")
                { Target = new(OpeningAction.Access, ClassId: room.Id, GradeId: room.GradeId) };
        }
    }

    private IEnumerable<SchoolOpeningRow> ScreenRows()
    {
        var details = Devices?.Screens.ToDictionary(row => (row.Id, row.Room.Id));
        foreach (var screen in Overview.Screens)
        {
            var detail = details?.GetValueOrDefault((screen.ScreenId, screen.ClassId));
            if (detail is { Active: false }) detail = null;
            var web = DeviceEvidence.LabelFor(detail?.BrowserState ?? screen.Browser.State);
            if (detail?.BrowserState == "NEEDS_ACCOUNT") web = "待配置网页账号";
            var native = DeviceEvidence.LabelFor(detail?.NativeState ?? screen.Native.State);
            var accounts = detail is null ? "网页账号和首次登录待核对" :
                $"网页账号 {(detail is { HasPin: true, LoginCode: not null } ? $"已配置（{detail.LoginCode}）" : "待配置")} · " +
                $"历史首次网页登录 {RecordTime(detail.FirstLoginAt)}";
            var paired = detail is null ? "当前配对时间待核对" : $"当前配对 {RecordTime(detail.NativePairedAt)}";
            var nativeAt = detail is null ? screen.Native.LastReportedAt : detail.NativeReportedAt;
            var webAt = detail is null ? screen.Browser.LastReportedAt : detail.HeartbeatAt;
            var nativeTimestamp = (detail?.NativeState ?? screen.Native.State) == "REPAIR_REQUIRED" ? "原设备上报" : "有效上报记录";
            var evidence = $"绑定 {screen.ScreenId}\n{accounts}\n网页值守：{web} · 当前心跳 {RecordTime(webAt)}\n" +
                $"NPEduTools：{native} · {paired}\n{nativeTimestamp} {RecordTime(nativeAt)}";
            yield return new($"screen/{screen.ScreenId}", $"{screen.ClassName} · {screen.ScreenName}", $"网页：{web}　原生：{native}",
                evidence, "打开该屏的值守与接入") { Target = new(OpeningAction.Screen, ClassId: screen.ClassId, BindingId: screen.ScreenId) };
        }
    }

    private static string Role(string role) => role switch
    {
        "TEACHER" => "教师", "ASSISTANT" => "助教", "VIEWER" => "只读", "OWNER" => "负责人", _ => role,
    };
}
