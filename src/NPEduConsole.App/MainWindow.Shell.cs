using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private enum ShellPage { Overview, Classes, Devices, Screen, Audit }

    private bool _schoolAffairsSelected;
    private SchoolOverviewTerm? _overviewTerm;
    private ShellPage _page;
    private ShellPage _pageBeforeAudit;
    private string? _detailScreenId;
    private string? _directoryReturnScreenId;
    private IReadOnlyList<Classroom> _searchRooms = [];

    private bool _showSchoolOverview => _page == ShellPage.Overview;
    private bool _showSchoolAudit => _page == ShellPage.Audit;

    private void ShellSizeChanged(object? sender, SizeChangedEventArgs args)
    {
        bool compact = args.NewSize.Width < 1000;
        Workspace.Classes.Set("compact", compact);
        Workspace.ColumnDefinitions[0].Width = new GridLength(compact ? 61 : 184);
        UpdateClassLayout();
    }

    private void ApplyLoginLayout(Size size)
    {
        bool compact = size.Height < 575;
        LoginForm.Margin = new Thickness(28, compact ? 14 : 32);
        LoginForm.Spacing = compact ? 10 : 17;
    }

    private void UpdateClassLayout()
    {
        bool directory = _page != ShellPage.Screen;
        ClassDirectoryPanel.IsVisible = directory;
        ClassWorkspaceLayout.ColumnDefinitions[0].Width = new GridLength(
            directory ? Workspace.Classes.Contains("compact") ? 183 : 218 : 0);
    }

    private void UpdateShellPage()
    {
        bool scoped = _school is not null && _client is not null;
        bool academicPage = _academicPages.Count > 0;
        SchoolSelectionNotice.IsVisible = !scoped;
        SchoolOverviewPanel.IsVisible = scoped && !academicPage && _showSchoolOverview;
        UpdateFirstClassSetup();
        SchoolAuditWorkspace.IsVisible = scoped && !academicPage && _showSchoolAudit;
        ClassWorkspaceLayout.IsVisible = scoped && !academicPage && _page is ShellPage.Classes or ShellPage.Screen;
        DeviceDirectory.IsVisible = scoped && !academicPage && _page == ShellPage.Devices;
        AcademicWorkspace.IsVisible = academicPage;
        SchoolAuditWorkspace.SetScope(SchoolAuditWorkspace.IsVisible ? _client : null, SchoolAuditWorkspace.IsVisible ? _school : null);
        SchoolOverviewWorkspace.SetScope(_client, _school);
        ReturnToSchoolOverviewButton.IsVisible = _overviewJourney && ClassWorkspaceLayout.IsVisible;
        ReturnToSchoolOverviewButton.Content = _openingDeviceReturn is null ? "返回总览" : "返回开学配置";
        ReturnToDeviceDirectoryButton.IsVisible = _directoryReturnScreenId is not null && ClassWorkspaceLayout.IsVisible;
        OpenSchoolOverviewButton.IsEnabled = OpenClassWorkspaceButton.IsEnabled = OpenDeviceDirectoryButton.IsEnabled =
            OpenSchoolAuditButton.IsEnabled = scoped;
        OpenSchoolManagementAccountsButton.IsEnabled = scoped && !academicPage;
        OpenSchoolServerStatusButton.IsEnabled = _client is not null && !academicPage;
        OpenSchoolTransferButton.IsEnabled = scoped && !academicPage;
        ObjectPicker.IsEnabled = scoped && !academicPage;
        OpenSchoolOverviewButton.Classes.Set("current", _showSchoolOverview && scoped);
        OpenClassWorkspaceButton.Classes.Set("current", _page == ShellPage.Classes);
        OpenDeviceDirectoryButton.Classes.Set("current", _page is ShellPage.Devices or ShellPage.Screen);
        OpenSchoolAuditButton.Classes.Set("current", _showSchoolAudit);
        bool screen = _page == ShellPage.Screen;
        OpenSchoolStudyButton.IsVisible = scoped && _page == ShellPage.Classes;
        OpenTermPreparationButton.IsVisible = OpenGradeTeachingPlanButton.IsVisible = OpenCourseGroupsButton.IsVisible = OpenTeacherAccountsButton.IsVisible = OpenSchoolSubjectsButton.IsVisible = scoped && _page == ShellPage.Classes;
        OpenStaffReviewButton.IsVisible = scoped && _page == ShellPage.Classes;
        OpenHomeworkSettingsButton.IsVisible = scoped && _page == ShellPage.Classes;
        OpenOrganizationImportButton.IsVisible = scoped && _page == ShellPage.Classes;
        OpenScreenClassButton.IsVisible = screen && _classroom is not null;
        OpenTeachingButton.IsVisible = OpenSubjectSetupButton.IsVisible =
            !screen && _classroom is { IsActive: true, TermId: not null };
        OpenClassRosterButton.IsVisible = !screen && _classroom?.IsActive != false;
        OpenClassMaintenanceButton.IsVisible = !screen && _classroom?.TermId is not null;
        UpdateClassLayout();
        UpdateClassPane();
    }

    private void UpdateClassPane()
    {
        var showAffairs = _page == ShellPage.Classes && (_schoolAffairsSelected || _classroom is null);
        ClassManagementWorkspace.IsVisible = !showAffairs;
        SchoolAffairsPage.IsVisible = showAffairs;
        OpenSchoolAffairsButton.Classes.Set("accent", showAffairs);
        ClassPicker.Classes.Set("school-affairs", showAffairs);
        OpenClassScreenDirectoryButton.IsVisible = _page == ShellPage.Classes && !_showClassStudy && _classroom?.IsActive == true;
        SchoolAffairsScope.Text = _school?.Name;
        ReturnToClassHomeButton.IsVisible = showAffairs && _classroom is not null;
        RefreshButton.IsVisible = _page == ShellPage.Screen;
        ReturnFromClassStudyButton.IsVisible = _page == ShellPage.Classes && _showClassStudy;
        ClassReturnActions.IsVisible = RefreshButton.IsVisible || ReturnToSchoolOverviewButton.IsVisible ||
            ReturnToDeviceDirectoryButton.IsVisible || ReturnFromClassStudyButton.IsVisible;
    }

    private void OpenSchoolAffairsClicked(object? sender, RoutedEventArgs e)
    {
        if (_page != ShellPage.Classes || _school is null || _academicPages.Count != 0) return;
        if (_showClassStudy)
        {
            _showClassStudy = false;
            UpdateStudyWorkspace();
        }
        _schoolAffairsSelected = true;
        UpdateClassPane();
        SchoolAffairsScroll.Offset = default;
        OpenTermPreparationButton.Focus();
    }

    private void ReturnToClassHomeClicked(object? sender, RoutedEventArgs e)
    {
        if (_page != ShellPage.Classes || _classroom is null || _academicPages.Count != 0) return;
        _schoolAffairsSelected = false;
        UpdateClassPane();
        OpenClassMaintenanceButton.Focus();
    }

    private void ClassListTapped(object? sender, TappedEventArgs e)
    {
        if (_schoolAffairsSelected && e.Source is Visual item &&
            item.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null)
            ReturnToClassHomeClicked(sender, e);
    }

    private void ClassListKeyDown(object? sender, KeyEventArgs e)
    {
        if (_schoolAffairsSelected && e.Key == Key.Enter && _classroom is not null)
        {
            e.Handled = true;
            ReturnToClassHomeClicked(sender, e);
        }
    }

    private void OverviewSnapshotChanged(SchoolOverview? snapshot, string status)
    {
        _overviewTerm = snapshot?.School.Id == _school?.Id ? snapshot?.Term : null;
        if (snapshot?.School.Id == _firstClassSetup?.SchoolId && snapshot is { Counts.AdministrativeClasses: > 0 })
            _firstClassSetup = null;
        UpdateFirstClassSetup();
        if (_page == ShellPage.Classes && !_showClassStudy) UpdateClassContext();
        ShellTermName.Text = snapshot?.Term?.Name ?? (snapshot is not null ? "暂无启用学期" : null);
        DeviceDirectory.SetSnapshot(snapshot, status);
        UpdateScreenEvidence();
    }

    private void SetSearchRooms(IReadOnlyList<Classroom> rooms, string message)
    {
        _searchRooms = rooms;
        SchoolOverviewWorkspace.SetClassLinks(rooms);
        var items = new List<ShellSearchItem>();
        foreach (var room in rooms)
        {
            string label = ClassroomName(room);
            string detail = room.Code ?? room.GradeName ?? room.Id;
            items.Add(new(room.Id, null, label, detail, room.Code));
            foreach (var screen in room.Screens)
            {
                bool duplicate = room.Screens.Count(candidate => candidate.Name == screen.Name) > 1;
                string screenDetail = duplicate ? $"{label} · {screen.Id}" : label;
                items.Add(new(room.Id, screen.Id, screen.Name, screenDetail, room.Code));
            }
        }
        ObjectPicker.SetCatalog(items, message);
    }

    private async void OpenClassWorkspaceClicked(object? sender, RoutedEventArgs args)
    {
        if (LeaveAcademicWorkspace()) await OpenClassDirectoryAsync();
    }

    private void OpenScreenClassClicked(object? sender, RoutedEventArgs args)
    {
        if (_page != ShellPage.Screen || _classroom is null) return;
        var fromOverview = _overviewJourney;
        var fromDirectory = _directoryReturnScreenId;
        var screenId = _detailScreenId;
        var room = _classroom;
        ChangePage(ShellPage.Classes);
        _overviewJourney = fromOverview;
        _directoryReturnScreenId = fromDirectory;
        UpdateShellPage();
        if (screenId is not null && _overviewScreenCards.TryGetValue(screenId, out var card))
            Dispatcher.UIThread.Post(() =>
            {
                if (_page != ShellPage.Classes || room != _classroom) return;
                var action = card.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("class-screen-entry"));
                if (action is not null) { action.BringIntoView(); action.Focus(); }
                else ClassPicker.Focus();
            }, DispatcherPriority.Loaded);
        else ClassPicker.Focus();
    }

    private async Task OpenClassDirectoryAsync()
    {
        if (_school is not { } school || _client is null) return;
        if (_page == ShellPage.Classes)
        {
            _schoolAffairsSelected = false;
            UpdateClassPane();
            return;
        }
        bool interrupted = _overviewNavigation is not null;
        ChangePage(ShellPage.Classes);
        if (interrupted) await LoadSchoolAsync(school);
    }

    private async void OpenDeviceDirectoryClicked(object? sender, RoutedEventArgs args)
    {
        if (_school is null || _client is not { } client || !LeaveAcademicWorkspace()) return;
        var school = _school;
        string? priorScreen = _directoryReturnScreenId;
        ChangePage(ShellPage.Devices);
        await SchoolOverviewWorkspace.RefreshAsync();
        if (_page != ShellPage.Devices || _client != client || _school != school || priorScreen is null) return;
        DeviceDirectory.RestorePosition(priorScreen);
    }

    private void ChangePage(ShellPage page)
    {
        _openingDeviceReturn = null;
        bool navigating = _overviewNavigation is not null;
        _overviewNavigation = null;
        if (navigating) CancelRequest();
        _overviewJourney = false;
        _directoryReturnScreenId = _detailScreenId = null;
        _schoolAffairsSelected = false;
        _showClassStudy = false;
        CloseClassAdmission();
        CloseScreenReplacement();
        CloseSoftwareMaintenance();
        _page = page;
        UpdateShellPage();
        ApplyScreenVisibility();
        UpdateStudyWorkspace();
    }

    private void ApplyScreenVisibility()
    {
        foreach (var (id, card) in _overviewScreenCards)
        {
            card.IsVisible = _page != ShellPage.Screen || id == _detailScreenId;
            card.Classes.Set("single-screen", _page == ShellPage.Screen);
            if (_admissionButtons.TryGetValue(id, out var configure)) configure.Classes.Set("accent", _page == ShellPage.Screen);
        }

        ScreensHeading.IsVisible = _page == ShellPage.Classes && _overviewScreenCards.Count > 0;
        ScreensHeading.Text = $"教室大屏  ·  {_overviewScreenCards.Count} 台";
        UpdateScreenEvidence();
        if (_page == ShellPage.Screen && _detailScreenId is { } expectedScreen && _classroom is not null)
        {
            if (_overviewScreenCards.ContainsKey(expectedScreen))
            {
                SetWorkspaceMessage("");
                return;
            }
            ScreensHeading.IsVisible = false;
            SetWorkspaceMessage("当前班级已找不到这台大屏。请返回并刷新核对。", isError: true);
        }
    }

    private void UpdateScreenEvidence()
    {
        OpenScreenFieldButton.IsEnabled = false;
        OpenScreenReportsButton.IsEnabled = false;
        if (_page != ShellPage.Screen || _detailScreenId is not { } id || _classroom is not { } room ||
            !_overviewScreenCards.ContainsKey(id))
        {
            ScreenEvidenceCard.IsVisible = false;
            return;
        }
        var row = DeviceDirectory.FindEvidence(_school?.Id, room.TermId, room.Id, id);
        ScreenEvidenceCard.IsVisible = true;
        OpenScreenFieldButton.IsEnabled = room.Screens.Any(screen => screen.Id == id && screen.Devices.Count > 0);
        OpenScreenReportsButton.IsEnabled = OpenScreenFieldButton.IsEnabled;
        ScreenNativeEvidence.Content = row?.Native ?? DeviceEvidence.Unknown;
        ScreenBrowserEvidence.Content = row?.Browser ?? DeviceEvidence.Unknown;
        ScreenEvidenceTime.Text = DeviceDirectory.EvidenceStatus(row is not null);
    }

    private async Task OpenSearchTargetAsync(ShellSearchItem item)
    {
        if (_searchRooms.FirstOrDefault(room => room.Id == item.ClassId) is not { TermId: { } termId }) return;
        if (_showSchoolOverview) SchoolOverviewWorkspace.ForgetReturnTarget();
        await OpenShellTargetAsync(item.ClassId, item.ScreenId, termId);
    }

    private async Task OpenDeviceDirectoryTargetAsync(SchoolOverview snapshot, OverviewScreenItem row)
    {
        if (_page != ShellPage.Devices || snapshot.Term is not { } term || !SchoolOverviewWorkspace.Shows(snapshot)) return;
        await OpenShellTargetAsync(row.ClassId, row.ScreenId, term.Id);
    }

    private async Task OpenShellTargetAsync(string classId, string? screenId, string termId)
    {
        if (_client is not { } client || _school is not { } school || _overviewNavigation is not null) return;
        var source = _page;
        var navigation = new object();
        _overviewNavigation = navigation;
        try
        {
            SetInactiveClassesFilter(false);
            bool loaded = await LoadSchoolAsync(school, classId, selectDefaultDevice: screenId is null);
            if (_client != client || _school != school || _page != source || _overviewNavigation != navigation) return;
            if (!loaded || _classroom?.Id != classId || _classroom.TermId != termId ||
                (screenId is not null && !_overviewScreenCards.ContainsKey(screenId)))
            {
                const string message = "待核对：班级或大屏已变化，或列表读取失败。请刷新后重新定位。";
                if (source == ShellPage.Devices) DeviceDirectory.ShowNavigationMessage(message);
                else if (source == ShellPage.Overview) SchoolOverviewWorkspace.ShowNavigationMessage(message);
                else SetWorkspaceMessage(message, isError: true);
                return;
            }
            var returnScreen = source == ShellPage.Devices ? screenId : _directoryReturnScreenId;
            bool fromOverview = source == ShellPage.Overview || _overviewJourney;
            ChangePage(screenId is null ? ShellPage.Classes : ShellPage.Screen);
            _detailScreenId = screenId;
            _directoryReturnScreenId = returnScreen;
            _overviewJourney = fromOverview;
            UpdateShellPage();
            ApplyScreenVisibility();
            UpdateStudyWorkspace();
            if (screenId is not null && ScreenEvidenceCard.IsVisible)
            {
                FocusScreenTarget(ScreenEvidenceCard, client, school);
                if (source == ShellPage.Classes) await SchoolOverviewWorkspace.RefreshAsync();
            }
            else ClassPicker.Focus();
        }
        finally
        {
            if (_overviewNavigation == navigation) _overviewNavigation = null;
        }
    }

    private void FocusScreenTarget(Control target, ConsoleClient client, School school) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_client != client || _school != school || !target.IsEffectivelyVisible || _admissionTarget is not null) return;
            target.BringIntoView();
            target.Focus();
        }, DispatcherPriority.Loaded);
}
