using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private enum ShellPage { Overview, Classes, Devices, Screen, Audit }

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
        bool compact = size.Width < 860;
        bool shortWindow = size.Height < 575;
        LoginColumns.Margin = new Thickness(28, shortWindow ? 10 : 27);
        LoginCard.Padding = new Thickness(36, shortWindow ? 11 : 28);
        LoginBrand.IsVisible = !compact;
        LoginColumns.ColumnDefinitions[0].Width = compact ? new GridLength(0) : new GridLength(.95, GridUnitType.Star);
        LoginColumns.MaxWidth = compact ? 550 : 955;
        LoginCard.CornerRadius = compact ? new CornerRadius(17) : new CornerRadius(0, 17, 17, 0);
    }

    private void UpdateClassLayout()
    {
        bool directory = _page != ShellPage.Screen;
        ClassDirectoryPanel.IsVisible = directory;
        ClassWorkspaceLayout.ColumnDefinitions[0].Width = new GridLength(
            directory ? Workspace.Classes.Contains("compact") ? 174 : 221 : 0);
    }

    private void UpdateShellPage()
    {
        bool scoped = _school is not null && _client is not null;
        SchoolSelectionNotice.IsVisible = !scoped;
        SchoolOverviewWorkspace.IsVisible = scoped && _showSchoolOverview;
        SchoolAuditWorkspace.IsVisible = scoped && _showSchoolAudit;
        ClassWorkspaceLayout.IsVisible = scoped && _page is ShellPage.Classes or ShellPage.Screen;
        DeviceDirectory.IsVisible = scoped && _page == ShellPage.Devices;
        SchoolAuditWorkspace.SetScope(SchoolAuditWorkspace.IsVisible ? _client : null, SchoolAuditWorkspace.IsVisible ? _school : null);
        SchoolOverviewWorkspace.SetScope(_client, _school);
        ReturnToSchoolOverviewButton.IsVisible = _overviewJourney && ClassWorkspaceLayout.IsVisible;
        ReturnToDeviceDirectoryButton.IsVisible = _directoryReturnScreenId is not null && ClassWorkspaceLayout.IsVisible;
        OpenSchoolOverviewButton.IsEnabled = OpenClassWorkspaceButton.IsEnabled = OpenDeviceDirectoryButton.IsEnabled =
            OpenSchoolAuditButton.IsEnabled = scoped;
        OpenSchoolServerStatusButton.IsEnabled = _client is not null;
        ObjectPicker.IsEnabled = scoped;
        OpenSchoolOverviewButton.Classes.Set("current", _showSchoolOverview && scoped);
        OpenClassWorkspaceButton.Classes.Set("current", _page == ShellPage.Classes);
        OpenDeviceDirectoryButton.Classes.Set("current", _page is ShellPage.Devices or ShellPage.Screen);
        OpenSchoolAuditButton.Classes.Set("current", _showSchoolAudit);
        bool screen = _page == ShellPage.Screen;
        OpenScreenClassButton.IsVisible = screen && _classroom is not null;
        OpenTeachingButton.IsVisible = OpenSubjectSetupButton.IsVisible =
            !screen && _classroom is { IsActive: true, TermId: not null };
        OpenClassRosterButton.IsVisible = !screen && _classroom?.IsActive != false;
        OpenClassMaintenanceButton.IsVisible = !screen && _classroom?.TermId is not null;
        UpdateClassLayout();
    }

    private void OverviewSnapshotChanged(SchoolOverview? snapshot, string status)
    {
        ShellTermName.Text = snapshot?.Term?.Name ?? (snapshot is not null ? "暂无启用学期" : null);
        DeviceDirectory.SetSnapshot(snapshot, status);
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

    private async void OpenClassWorkspaceClicked(object? sender, RoutedEventArgs args) => await OpenClassDirectoryAsync();

    private void OpenScreenClassClicked(object? sender, RoutedEventArgs args)
    {
        if (_page != ShellPage.Screen || _classroom is null) return;
        var fromOverview = _overviewJourney;
        var fromDirectory = _directoryReturnScreenId;
        ChangePage(ShellPage.Classes);
        _overviewJourney = fromOverview;
        _directoryReturnScreenId = fromDirectory;
        UpdateShellPage();
        ClassPicker.Focus();
    }

    private async Task OpenClassDirectoryAsync()
    {
        if (_school is not { } school || _client is null) return;
        if (_page == ShellPage.Classes) return;
        bool interrupted = _overviewNavigation is not null;
        ChangePage(ShellPage.Classes);
        if (interrupted) await LoadSchoolAsync(school);
    }

    private async void OpenDeviceDirectoryClicked(object? sender, RoutedEventArgs args)
    {
        if (_school is null || _client is not { } client) return;
        var school = _school;
        string? priorScreen = _directoryReturnScreenId;
        ChangePage(ShellPage.Devices);
        await SchoolOverviewWorkspace.RefreshAsync();
        if (_page != ShellPage.Devices || _client != client || _school != school || priorScreen is null) return;
        DeviceDirectory.RestorePosition(priorScreen);
    }

    private void ChangePage(ShellPage page)
    {
        bool navigating = _overviewNavigation is not null;
        _overviewNavigation = null;
        if (navigating) CancelRequest();
        _overviewJourney = false;
        _directoryReturnScreenId = _detailScreenId = null;
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
            card.IsVisible = _page != ShellPage.Screen || id == _detailScreenId;

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
            bool fromDevices = source == ShellPage.Devices || _directoryReturnScreenId is not null;
            ChangePage(screenId is null ? ShellPage.Classes : ShellPage.Screen);
            _detailScreenId = screenId;
            _directoryReturnScreenId = fromDevices ? screenId : null;
            _overviewJourney = source == ShellPage.Overview;
            UpdateShellPage();
            ApplyScreenVisibility();
            UpdateStudyWorkspace();
            if (screenId is not null && _overviewScreenCards.TryGetValue(screenId, out var target))
                FocusScreenTarget(target, client, school);
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
