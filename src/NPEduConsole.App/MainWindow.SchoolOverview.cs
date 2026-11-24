using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, Border> _overviewScreenCards = new(StringComparer.Ordinal);
    private bool _overviewJourney;
    private object? _overviewNavigation;
    private (ConsoleClient Client, School School)? _overviewWebReturn;

    private async void SchoolOverviewClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is null || _school is null || !LeaveAcademicWorkspace()) return;
        if (sender == ReturnToSchoolOverviewButton && _openingDeviceReturn is { } opening)
        {
            ChangePage(ShellPage.Overview);
            await OpenSchoolOpeningAsync(opening);
            return;
        }
        if (_showSchoolOverview)
        {
            await SchoolOverviewWorkspace.RefreshAsync();
            return;
        }
        bool returnFromTask = _overviewJourney;
        ChangePage(ShellPage.Overview);
        await SchoolOverviewWorkspace.RefreshAsync(restoreTask: returnFromTask);
        if (!returnFromTask && _showSchoolOverview) OpenSchoolOverviewButton.Focus();
    }

    private async Task OpenSchoolOverviewTaskAsync(SchoolOverview overview, SchoolOverviewTask task)
    {
        if (_client is not { } client || _school is not { } school || school.Id != overview.School.Id ||
            !_showSchoolOverview || !SchoolOverviewWorkspace.Shows(overview) || _overviewNavigation is not null) return;
        var navigation = new object();
        _overviewNavigation = navigation;
        try
        {
            if (task.Action == "OPEN_TERM_PREPARATION")
            {
                await ShowTermPreparationAsync(client, school, () => { if (StillHere()) _overviewNavigation = null; });
                if (StillHere()) await SchoolOverviewWorkspace.RefreshAsync(restoreTask: true);
                return;
            }
            if (overview.Term is { } courseTerm && task.Action is ("OPEN_COURSE_GROUP_SOURCE" or "OPEN_COURSE_GROUP_TEACHING"))
            {
                await ShowCourseGroupsAsync(client, school, courseTerm.Id, task.CourseGroupId, task.ClassId, task.SubjectId,
                    task.Action == "OPEN_COURSE_GROUP_TEACHING");
                if (StillHere()) await SchoolOverviewWorkspace.RefreshAsync(restoreTask: true);
                return;
            }
            if (task.Action == "OPEN_TEACHER_ACCOUNT" && task.AccountId is { } teacherId)
            {
                await ShowTeacherAccountsAsync(client, school, teacherId);
                if (StillHere()) await SchoolOverviewWorkspace.RefreshAsync(restoreTask: true);
                return;
            }
            if (task.Action == "OPEN_SCREEN_DUTY" && task.ScreenId is { } dutyBinding)
            {
                await ShowScreenDutyAsync(client, school, dutyBinding, task.ClassId, restoreTask: true);
                return;
            }
            if (task.Action == "OPEN_STAFF_REVIEW" && overview.Term is { } reviewTerm)
            {
                await ShowStaffReviewAsync(client, school, reviewTerm.Id,
                    StaffReviewIssue.IssueKey(task.Code, task.GradeId, task.ClassId, task.AccountId), task.GradeId, task.ClassId);
                if (StillHere()) await SchoolOverviewWorkspace.RefreshAsync(restoreTask: true);
                return;
            }
            if (overview.Term is { } staffTerm &&
                (task.Action == "OPEN_CLASS_RESPONSIBILITY" ? task.ClassId : task.Action == "OPEN_GRADE_RESPONSIBILITY" ? task.GradeId : null) is { } staffTarget)
            {
                await ShowStaffResponsibilitiesAsync(client, school, staffTerm.Id,
                    task.Action == "OPEN_CLASS_RESPONSIBILITY" ? "classes" : "grades", staffTarget, task.AccountId);
                if (StillHere()) await SchoolOverviewWorkspace.RefreshAsync(restoreTask: true);
                return;
            }
            if (task.Action == "OPEN_TEACHING" && overview.Term is { } teachingTerm &&
                task.ClassId is { } teachingClass && task.SubjectId is { } subjectId)
            {
                if (await ShowTeachingPageAsync(client, school, teachingTerm.Id, teachingClass, subjectId, termName: teachingTerm.Name) && StillHere())
                    await SchoolOverviewWorkspace.RefreshAsync(restoreTask: true);
                return;
            }
            if (task.Action is "CREATE_GRADE" or "CREATE_CLASS")
            {
                await OpenClassCreationAsync(task.Action == "CREATE_GRADE" ? overview.Term?.Id : null);
                if (StillHere()) await SchoolOverviewWorkspace.RefreshAsync(restoreTask: true);
                return;
            }
            if (task.Action == "OPEN_TERM_SCREEN_PLACEMENT" && overview.Term is { } placementTerm &&
                task.ScreenId is { } placementBinding)
            {
                var target = await ShowTermScreenPlacementAsync(this, client, school, placementTerm.Id, placementBinding);
                if (!StillHere()) return;
                if (target is not null)
                {
                    _overviewNavigation = null;
                    await OpenTermResultDestinationAsync(client, school, target);
                }
                else await SchoolOverviewWorkspace.RefreshAsync(restoreTask: true);
                return;
            }
            if (task.Action == "OPEN_WEB")
            {
                OpenOverviewWebTask(client, school, overview, task);
                return;
            }

            if (overview.Term is not { } term || task.ClassId is not { } classId) return;
            SchoolOverviewWorkspace.ShowNavigationMessage("正在核对待办对应的班级和大屏…");
            SetInactiveClassesFilter(false);
            bool loaded = await LoadSchoolAsync(school, classId, selectDefaultDevice: false);
            if (!StillHere() || !SchoolOverviewWorkspace.Shows(overview)) return;
            if (!loaded)
            {
                SchoolOverviewWorkspace.ShowNavigationMessage("待核对：班级列表读取失败。请刷新后再办理。");
                return;
            }
            if (_classroom is not { IsActive: true } room || room.Id != classId || room.TermId != term.Id)
            {
                await SchoolOverviewWorkspace.RefreshAsync();
                if (StillHere()) SchoolOverviewWorkspace.ShowNavigationMessage("班级或当前学期已变化。请核对刷新后的待办。");
                return;
            }
            if (task.Action == "PREPARE_SCREEN")
            {
                if (room.CanPrepareFirstScreen)
                {
                    ShowOverviewTarget(PrepareFirstScreenButton, null);
                }
                else
                {
                    await SchoolOverviewWorkspace.RefreshAsync();
                    if (StillHere()) SchoolOverviewWorkspace.ShowNavigationMessage("当前班级无法继续登记首台大屏，请核对刷新后的待办。");
                }
                return;
            }
            if (task.ScreenId is not { } screenId || !_overviewScreenCards.TryGetValue(screenId, out var card) ||
                room.Screens.FirstOrDefault(screen => screen.Id == screenId) is not { } screen)
            {
                await SchoolOverviewWorkspace.RefreshAsync();
                if (StillHere()) SchoolOverviewWorkspace.ShowNavigationMessage("大屏已变更。请核对刷新后的待办。");
                return;
            }
            ShowOverviewTarget(card, screen.Id);
            if (task.Code is "SCREEN_AWAITING_PAIRING" or "SCREEN_REPAIR_REQUIRED" &&
                _admissionButtons.TryGetValue(screenId, out var opener))
                await OpenClassAdmissionAsync(screen, opener);
        }
        finally
        {
            if (_overviewNavigation == navigation)
                _overviewNavigation = null;
        }

        bool StillHere() => _client == client && _school == school && _showSchoolOverview && _overviewNavigation == navigation;

        void ShowOverviewTarget(Control target, string? screenId)
        {
            _overviewNavigation = null;
            ChangePage(screenId is null ? ShellPage.Classes : ShellPage.Screen);
            _detailScreenId = screenId;
            _overviewJourney = true;
            UpdateShellPage();
            ApplyScreenVisibility();
            UpdateStudyWorkspace();
            FocusScreenTarget(target, client, school);
        }
    }

    private void OpenOverviewWebTask(ConsoleClient client, School school, SchoolOverview overview, SchoolOverviewTask task)
    {
        if (OverviewTaskItem.WebSection(task) is not { } section) return;
        if (SchoolAdminBrowser.Link(overview.AdminUrl, school.Id, task.SourceTermId ?? overview.Term?.Id, section, task.ScreenId, task.ClassId) is not { } link)
        {
            SchoolOverviewWorkspace.ShowNavigationMessage("KV 未提供网页地址。请在原 NPClassworks 网页的「学校管理」打开对应入口。");
            return;
        }
        if ((DesktopBrowserLauncher ?? SchoolAdminBrowser.Open)(link))
        {
            _overviewWebReturn = (client, school);
            SchoolOverviewWorkspace.ShowNavigationMessage("已打开网页学校管理。办理后返回此窗口即可核对最新状态。");
        }
        else
            SchoolOverviewWorkspace.ShowNavigationMessage("暂时无法打开浏览器。请在原 NPClassworks 网页办理。");
    }

    private void RefreshAfterWebOverviewTask()
    {
        if (_overviewWebReturn is not { } scope) return;
        _overviewWebReturn = null;
        if (_showSchoolOverview && _client == scope.Client && _school == scope.School) _ = SchoolOverviewWorkspace.RefreshAsync(restoreTask: true);
    }
}
