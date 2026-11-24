using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private (string SchoolId, Uri Website)? _openingWebsite;
    private SchoolOpeningView.Position? _openingDeviceReturn;

    private async Task OpenSchoolOpeningAsync(SchoolOpeningView.Position? restore = null)
    {
        if (_client is not { } client || _school is not { } school || !_showSchoolOverview || _academicPages.Count != 0) return;
        var website = _openingWebsite is { } configured && configured.SchoolId == school.Id ? configured.Website : null;
        SchoolOpeningView view = null!;
        view = new SchoolOpeningView(client, school, website, restore)
        {
            OpenTarget = (target, snapshot, ct) => OpenSchoolOpeningTargetAsync(view, client, school, target, snapshot, ct)
        };
        view.AccessLost += error => AcademicScopeUnavailable(error, client, school);
        view.ReturnRequested += () => ReturnFromAcademicPage(view);
        var exit = PushAcademicPage(view, () => true, view.End);
        await view.StartAsync();
        if (!await exit || _client != client || _school != school || !_showSchoolOverview) return;
        await SchoolOverviewWorkspace.RefreshAsync();
        if (_client == client && _school == school && _showSchoolOverview && _academicPages.Count == 0)
            Dispatcher.UIThread.Post(SchoolOverviewWorkspace.FocusOpeningEntry, DispatcherPriority.Loaded);
    }

    private async Task OpenSchoolOpeningTargetAsync(SchoolOpeningView view, ConsoleClient client, School school,
        OpeningTarget target, SchoolOverview snapshot, CancellationToken ct)
    {
        if (!Current()) return;
        var latest = await client.GetSchoolOverviewAsync(school.Id, ct);
        if (!Current()) return;
        var term = latest.Term;
        if (term?.Id != snapshot.Term?.Id)
            throw new ConsoleApiException(409, "当前启用学期已变化。请在刷新后的清单中重新选择。");
        if (target.Action == OpeningAction.Terms)
        {
            await ShowTermPreparationAsync(client, school);
            return;
        }
        if (target.Action == OpeningAction.Accounts)
        {
            await ShowTeacherAccountsAsync(client, school, target.AccountId);
            return;
        }
        if (target.Action == OpeningAction.ManagementAccounts && target.AccountId is { } accountId)
        {
            await ShowSchoolManagementAccountsAsync(this, client, school, accountId);
            return;
        }
        if (target.Action == OpeningAction.StaffConfiguration)
        {
            await ShowStaffConfigurationAsync(client, school, term?.Id);
            return;
        }
        if (term is null) return;

        switch (target.Action)
        {
            case OpeningAction.Import:
                await ShowOrganizationImportAsync(client, school, term.Id);
                break;
            case OpeningAction.Class:
                await OpenClassCreationAsync(target.CreateGrade ? term.Id : null, requiredActiveTermId: term.Id);
                break;
            case OpeningAction.ClassDirectory when target.ClassId is { } classId:
                var calendar = await client.GetTermPreparationAsync(school.Id, ct);
                if (!Current()) return;
                if (calendar.ActiveTerm is not { } active || active.Id != term.Id)
                    throw new ConsoleApiException(409, "当前启用学期已变化。请在刷新后的清单中重新选择。");
                await ShowTermClassDirectoryAsync(client, school, active, classId, target.GradeId, "返回开学配置");
                break;
            case OpeningAction.Access:
                await ShowTeacherAllocationPageAsync(client, school, term.Id, target.GradeId, false, target.ClassId);
                break;
            case OpeningAction.Teaching when (target.GroupId ?? target.ClassId) is { } room && target.SubjectId is { } subject:
                await ShowTeachingPageAsync(client, school, term.Id, room, subject, target.AccountId,
                    courseGroup: target.GroupId is not null, termName: term.Name);
                break;
            case OpeningAction.Duty when (target.ClassId ?? target.GradeId) is { } duty:
                await ShowStaffResponsibilitiesAsync(client, school, term.Id, target.ClassId is null ? "grades" : "classes", duty,
                    target.AccountId, currentTermName: term.Name);
                break;
            case OpeningAction.StaffReview:
                await ShowStaffReviewAsync(client, school, term.Id, target.IssueKey, target.GradeId, target.ClassId);
                break;
            case OpeningAction.CourseGroups:
                await ShowCourseGroupsAsync(client, school, term.Id, target.GroupId, target.ClassId, target.SubjectId);
                break;
            case OpeningAction.InactiveScreens when target.ClassId is { } inactiveClass:
                await ShowScreenDutyAsync(client, school, null, inactiveClass, inactiveTermId: term.Id, adminUrl: latest.AdminUrl);
                break;
            case OpeningAction.Screen:
                if (target.BindingId is { } exact && !latest.Screens.Any(row => row.ScreenId == exact && row.ClassId == target.ClassId))
                    throw new ConsoleApiException(409, "这台大屏的归属已变化。请从更新后的清单重新选择。");
                await ShowScreenDutyAsync(client, school, target.BindingId, target.ClassId,
                    openNative: screen => OpenDeviceAsync(new(screen.Room.Id, screen.Id, screen.Room.TermId, true)));
                break;
            case OpeningAction.Placement when target.BindingId is { } binding:
                if (await ShowTermScreenPlacementAsync(this, client, school, term.Id, binding) is { } placed)
                    await OpenDeviceAsync(placed);
                break;
        }

        bool Current() => _client == client && _school == school && ShowsAcademicPage(view);

        async Task OpenDeviceAsync(TermPreparationView.ScreenDestination device)
        {
            if (!Current() || device.TermId != term.Id) return;
            var position = view.RememberPosition();
            ReturnFromAcademicPage(view);
            await OpenTermResultDestinationAsync(client, school, device);
            if (_client != client || _school != school || _classroom?.Id != device.ClassId ||
                _classroom.TermId != device.TermId || _detailScreenId != device.ScreenId || _page != ShellPage.Screen) return;
            _openingDeviceReturn = position;
            UpdateShellPage();
        }
    }
}
