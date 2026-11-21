using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, Border> _overviewScreenCards = new(StringComparer.Ordinal);
    private bool _showSchoolOverview, _overviewJourney;
    private object? _overviewNavigation;
    private (ConsoleClient Client, School School)? _overviewWebReturn;

    private async void SchoolOverviewClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is null || _school is null) return;
        if (_showSchoolOverview)
        {
            await SchoolOverviewWorkspace.RefreshAsync();
            return;
        }
        _overviewNavigation = null;
        _showSchoolAudit = _showClassStudy = false;
        _showSchoolOverview = true;
        _overviewJourney = false;
        ClearOverviewSidebarSelection();
        CloseClassAdmission();
        CloseScreenReplacement();
        CloseSoftwareMaintenance();
        UpdateStudyWorkspace();
        UpdateSchoolAuditWorkspace();
        OpenSchoolOverviewButton.Focus();
    }

    private async Task OpenSchoolOverviewTaskAsync(SchoolOverview overview, SchoolOverviewTask task)
    {
        if (_client is not { } client || _school is not { } school || school.Id != overview.School.Id ||
            !_showSchoolOverview || !SchoolOverviewWorkspace.Shows(overview) || _overviewNavigation is not null) return;
        var navigation = new object();
        _overviewNavigation = navigation;
        try
        {
            if (task.Action == "OPEN_TEACHING" && overview.Term is { } teachingTerm &&
                task.ClassId is { } teachingClass && task.SubjectId is { } subjectId)
            {
                await ShowTeachingAsync(client, school, teachingTerm.Id, teachingClass, subjectId);
                if (StillHere()) await SchoolOverviewWorkspace.RefreshAsync();
                return;
            }
            if (task.Action is "CREATE_GRADE" or "CREATE_CLASS")
            {
                await OpenClassCreationAsync(task.Action == "CREATE_GRADE" ? overview.Term?.Id : null);
                if (StillHere()) await SchoolOverviewWorkspace.RefreshAsync();
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
            bool loaded = await LoadSchoolAsync(school, classId);
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
                    ShowOverviewTarget(PrepareFirstScreenButton);
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
            ShowOverviewTarget(card);
            if (task.Code is "SCREEN_AWAITING_PAIRING" or "SCREEN_REPAIR_REQUIRED" &&
                _admissionButtons.TryGetValue(screenId, out var opener))
                await OpenClassAdmissionAsync(screen, opener);
        }
        finally
        {
            if (_overviewNavigation == navigation)
            {
                _overviewNavigation = null;
                if (_showSchoolOverview) ClearOverviewSidebarSelection();
            }
        }

        bool StillHere() => _client == client && _school == school && _showSchoolOverview && _overviewNavigation == navigation;

        void ShowOverviewTarget(Control target)
        {
            _showSchoolOverview = false;
            _overviewJourney = true;
            UpdateSchoolAuditWorkspace();
            Dispatcher.UIThread.Post(() =>
            {
                if (_client != client || _school != school || _showSchoolOverview || !target.IsEffectivelyVisible) return;
                target.BringIntoView();
                if (_admissionTarget is null) target.Focus();
            }, DispatcherPriority.Loaded);
        }
    }

    private void ClearOverviewSidebarSelection()
    {
        if (ClassPicker.SelectedItem is null) return;
        _settingSelection = true;
        ClassPicker.SelectedItem = null;
        _settingSelection = false;
    }

    private void OpenOverviewWebTask(ConsoleClient client, School school, SchoolOverview overview, SchoolOverviewTask task)
    {
        string section = task.TargetTab is "organization" or "terms" or "structure" or "teachers" or "screens" or "accounts"
            ? task.TargetTab : "overview";
        if (SchoolAdminBrowser.Link(overview.AdminUrl, school.Id, overview.Term?.Id, section, task.ScreenId, task.ClassId) is not { } link)
        {
            SchoolOverviewWorkspace.ShowNavigationMessage("KV 未提供网页地址。请在原 NPClassworks 网页的「学校管理」打开对应入口。");
            return;
        }
        if (SchoolAdminBrowser.Open(link))
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
        if (_showSchoolOverview && _client == scope.Client && _school == scope.School) _ = SchoolOverviewWorkspace.RefreshAsync();
    }
}
