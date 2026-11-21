using System.Net.Http;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Styling;
using Avalonia.VisualTree;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private sealed record OverviewAccess(string Origin, string SchoolId, string SchoolCode, string Username, string Password,
        string OtherSchoolId, string ClassSevenId, string ClassEightId, string WaitingScreenId, string ReadyScreenId,
        string RestoredScreenId, bool AllowOverviewTestWrites);

    private static int StartSchoolOverview(string fixturePath, string output)
    {
        var access = JsonSerializer.Deserialize<OverviewAccess>(File.ReadAllText(fixturePath), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Missing school overview fixture.");
        Check(access.AllowOverviewTestWrites && IsLoopback(access.Origin), "Use the loopback overview fixture.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyOverviewAsync(access, output); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyOverviewAsync(OverviewAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        var view = Find<SchoolOverviewView>(main, "SchoolOverviewWorkspace");
        var schools = Find<ComboBox>(main, "SchoolPicker");
        var classes = Find<ListBox>(main, "ClassPicker");
        var classQuery = Find<TextBox>(main, "ClassSearch");
        var classTitle = Find<TextBlock>(main, "ClassWorkspaceTitle");
        var classContext = Find<TextBlock>(main, "ClassContext");
        var tasks = View<ListBox>("OverviewTasksList");
        var screenDirectory = Find<SchoolDeviceDirectoryView>(main, "DeviceDirectory");
        var screens = screenDirectory.FindControl<ListBox>("DeviceDirectoryRows")!;
        var nativeFilter = screenDirectory.FindControl<ComboBox>("NativeFilter")!;
        var browserFilter = screenDirectory.FindControl<ComboBox>("BrowserFilter")!;
        var deviceSearch = screenDirectory.FindControl<TextBox>("DeviceSearch")!;
        var clearDirectory = screenDirectory.FindControl<Button>("ClearFilters")!;
        var nativeRecord = Find<ContentControl>(main, "ScreenNativeEvidence");
        var browserRecord = Find<ContentControl>(main, "ScreenBrowserEvidence");
        var refresh = View<Button>("RefreshSchoolOverviewButton");
        var showTasks = View<Button>("ShowAllOverviewButton");
        var filter = View<ComboBox>("OverviewTaskFilter");
        var overviewNav = Find<Button>(main, "OpenSchoolOverviewButton");
        var classesNav = Find<Button>(main, "OpenClassWorkspaceButton");
        var devicesNav = Find<Button>(main, "OpenDeviceDirectoryButton");
        var picker = Find<ShellObjectPicker>(main, "ObjectPicker");
        var goBack = Find<Button>(main, "ReturnToSchoolOverviewButton");
        using var control = new HttpClient {BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(8)};
        try
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            await Capture(main, output, "00-login");
            main.Width = 840;
            main.Height = 480;
            var loginButton = Find<Button>(main, "LoginButton");
            await Until(() => main.Bounds.Height < 500 && loginButton.TranslatePoint(default, main) is { } point &&
                point.Y + loginButton.Bounds.Height <= main.Bounds.Height, "the entire login button fits in a short window");
            await Capture(main, output, "00-login-compact");
            main.Width = 1160;
            main.Height = 780;
            Find<TextBox>(main, "ServerAddress").Text = access.Origin;
            Find<TextBox>(main, "SchoolCode").Text = access.SchoolCode;
            Find<TextBox>(main, "Username").Text = access.Username;
            Find<TextBox>(main, "Password").Text = access.Password;
            Click(Find<Button>(main, "LoginButton"));
            await Until(() => schools.ItemCount == 2, "both authorized fixture schools");
            Check(!view.IsEffectivelyVisible, "multi-school login waits for the administrator to select a school");
            await Capture(main, output, "00-select-school");
            SelectSchool(access.SchoolId);
            await Until(() => view.IsEffectivelyVisible && showTasks.IsEffectivelyVisible, "overview opens by default");
            var classLinks = View<ItemsControl>("OverviewClassLinks");
            await Until(() => classLinks.Items.Cast<Classroom>().Any(room => room.Id == access.ClassEightId), "overview links current-term classes");
            await Capture(main, output, "01-school-overview");
            var classShortcut = classLinks.GetVisualDescendants().OfType<Button>().Single(button =>
                button.Tag is Classroom room && room.Id == access.ClassEightId);
            Click(classShortcut);
            await Until(() => classes.IsEffectivelyVisible && (classes.SelectedItem as Classroom)?.Id == access.ClassEightId,
                "the overview class shortcut opens the exact class");
            Click(goBack);
            await Until(() => classLinks.GetVisualDescendants().OfType<Button>().Any(button => button.IsFocused &&
                button.Tag is Classroom room && room.Id == access.ClassEightId),
                "returning from a class shortcut restores its focus after refreshing the overview");
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            await Capture(main, output, "01-school-overview-dark");
            Application.Current.RequestedThemeVariant = ThemeVariant.Light;
            picker.Open();
            picker.SearchText = "后排待接入";
            await Until(() => picker.Results is [{ ScreenId: var targetId }] && targetId == access.WaitingScreenId,
                "global search resolves the same rear screen");
            var lookup = picker.FindControl<TextBox>("SearchBox")!;
            var resultsList = picker.FindControl<ListBox>("ResultsList")!;
            await Until(() => resultsList.ContainerFromIndex(0) is ListBoxItem, "keyboard picker prepares its selected result");
            Check(OutlineOf(resultsList, 0).CornerRadius.TopLeft >= 8, "the object picker outlines results like the class directory");
            lookup.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = lookup });
            await Until(() => !picker.IsOpen && IsOnlyScreenVisible(access.WaitingScreenId) && goBack.IsEffectivelyVisible,
                "Enter routes from global search to the exact screen and remembers the overview");
            Click(goBack);
            await Until(() => showTasks.IsEffectivelyVisible, "overview reloads after returning from global search");
            Click(showTasks);
            await Until(() => view.IsEffectivelyVisible && Has("ADMIN_CLASS_WITHOUT_SCREEN", access.ClassSevenId) &&
                Has("SCREEN_AWAITING_PAIRING", screen: access.WaitingScreenId), "overview opens by default with two exact targets");
            Check(!tasks.Items.Cast<OverviewTaskItem>().Any(row => row.Task.ScreenId == access.ReadyScreenId) &&
                Has("SCREEN_REPAIR_REQUIRED", screen: access.RestoredScreenId), "reported screen is clear and restored binding needs re-pairing");
            Check(screens.Items.Cast<OverviewScreenItem>().Count(row => row.Title.Contains("（8）")) == 2 &&
                screens.Items.Cast<OverviewScreenItem>().All(row => !row.Title.Contains("往届")), "the archived same-code screen is isolated");
            await Capture(main, output, "01-classroom-tasks");
            Click(devicesNav);
            await Until(() => screenDirectory.IsEffectivelyVisible && screens.ItemCount > 1, "cross-class device directory opens");
            await Capture(main, output, "02-device-directory");
            Check(OutlineOf(screens, 0).CornerRadius.TopLeft >= 8, "device directory entries use the same rounded outline");
            SelectStatus(nativeFilter, "AWAITING_PAIRING");
            SelectStatus(browserFilter, "ONLINE");
            deviceSearch.Text = "后排";
            await Until(() => OnlyDirectoryScreen(access.WaitingScreenId), "native admission and browser heartbeat filters intersect by binding");
            main.Width = 840;
            main.Height = 480;
            await Capture(main, output, "02a-device-directory-compact");
            var compactOpen = screens.GetVisualDescendants().OfType<Button>().Single(button =>
                button.Tag is OverviewScreenItem row && row.ScreenId == access.WaitingScreenId);
            Check(compactOpen.TranslatePoint(new Point(compactOpen.Bounds.Width, compactOpen.Bounds.Height), main) is { } corner &&
                corner.X <= main.Bounds.Width && corner.Y <= main.Bounds.Height, "the matching screen action fits in the compact viewport");
            SelectStatus(browserFilter, "OFFLINE");
            Check(screens.ItemCount == 0 && clearDirectory.IsEnabled, "no result can be cleared without losing navigation");
            SelectStatus(browserFilter, "ONLINE");
            deviceSearch.Text = access.WaitingScreenId;
            await Until(() => OnlyDirectoryScreen(access.WaitingScreenId), "search matches a binding ID");
            deviceSearch.Text = "后排";
            main.Width = 1160;
            main.Height = 780;
            await OpenDirectoryScreen(access.WaitingScreenId);
            await Until(() => IsOnlyScreenVisible(access.WaitingScreenId), "device directory reaches the exact binding and hides sibling cards");
            Check(Find<TextBlock>(main, "ClassWorkspaceTitle").Text == "后排待接入" &&
                !Find<Button>(main, "OpenClassRosterButton").IsEffectivelyVisible, "the screen title names the binding and keeps class actions off the device page");
            Check(nativeRecord.Content is DeviceEvidence { State: "AWAITING_PAIRING", HasRecord: false } &&
                browserRecord.Content is DeviceEvidence { State: "ONLINE", HasRecord: true }, "the same binding shows independent dated browser and native evidence");
            await Capture(main, output, "02b-single-screen-evidence");
            await Command("fail");
            Click(Find<Button>(main, "RefreshButton"));
            await Until(() => IsOnlyScreenVisible(access.WaitingScreenId) && nativeRecord.Content is DeviceEvidence { State: "UNKNOWN" } &&
                browserRecord.Content is DeviceEvidence { State: "UNKNOWN" } && Find<TextBlock>(main, "ScreenEvidenceTime").Text?.Contains("待核对") == true &&
                !Find<ProgressBar>(main, "WorkspaceProgress").IsVisible,
                "refresh drops prior status when the server cannot verify the detail");
            Click(Find<Button>(main, "RefreshButton"));
            await Until(() => IsOnlyScreenVisible(access.WaitingScreenId) && browserRecord.Content is DeviceEvidence { State: "ONLINE" },
                "retry restores server evidence");
            Click(Find<Button>(main, "OpenScreenClassButton"));
            Check(classes.IsEffectivelyVisible && (classes.SelectedItem as Classroom)?.Id == access.ClassEightId &&
                Find<Button>(main, "OpenClassRosterButton").IsEffectivelyVisible,
                "the screen's class link opens the original class with its operations available");
            var frontEntry = main.GetVisualDescendants().OfType<Button>().Single(button =>
                AutomationProperties.GetAutomationId(button) == "ClassScreenDetails-" + access.ReadyScreenId);
            Click(frontEntry);
            await Until(() => IsOnlyScreenVisible(access.ReadyScreenId) && nativeRecord.Content is DeviceEvidence { State: "REPORTED", HasRecord: true },
                "class opens the same detail on the neighboring binding");
            Click(Find<Button>(main, "ReturnToDeviceDirectoryButton"));
            await Until(() => screenDirectory.IsEffectivelyVisible && OnlyDirectoryScreen(access.WaitingScreenId) &&
                screens.GetVisualDescendants().OfType<Button>().Any(b => b.IsFocused && b.Tag is OverviewScreenItem item && item.ScreenId == access.WaitingScreenId),
                "the originating directory row regains focus with all three filters preserved");
            Click(clearDirectory);
            SelectStatus(nativeFilter, "REPAIR_REQUIRED");
            await Until(() => OnlyDirectoryScreen(access.RestoredScreenId), "repair filter locates the original binding across classes");
            await OpenDirectoryScreen(access.RestoredScreenId);
            await Until(() => IsOnlyScreenVisible(access.RestoredScreenId) && nativeRecord.Content is DeviceEvidence { State: "REPAIR_REQUIRED" } evidence &&
                evidence.Record?.StartsWith("原设备 ") == true, "detail labels the previous device's actual report");
            Click(Find<Button>(main, "ReturnToDeviceDirectoryButton"));
            await Until(() => screenDirectory.IsEffectivelyVisible && OnlyDirectoryScreen(access.RestoredScreenId), "repair scope is retained");
            Click(clearDirectory);
            Click(overviewNav);
            await Until(() => Has("SCREEN_AWAITING_PAIRING", screen: access.WaitingScreenId), "overview retains the expanded queue");
            filter.SelectedIndex = 2;
            await OpenTask("SCREEN_AWAITING_PAIRING", screen: access.WaitingScreenId);
            await Until(() => goBack.IsEffectivelyVisible &&
                Find<TextBlock>(main, "AdmissionScope").Text?.Contains("后排待接入") == true, "the exact rear screen admission is opened");
            Check(classes.SelectedItem is Classroom {Id: var selected} && selected == access.ClassEightId, "the target class is selected by ID");
            await Capture(main, output, "03-exact-screen");
            Click(goBack);
            await Until(() => Has("ADMIN_CLASS_WITHOUT_SCREEN", access.ClassSevenId), "return re-reads the original school");
            Check(filter.SelectedIndex == 2 && IsOnlyScreenVisible(access.WaitingScreenId) == false,
                "return keeps the device filter and leaves the detail");
            await Until(() => tasks.GetVisualDescendants().OfType<Button>().Any(button => button.IsFocused &&
                button.Tag is OverviewTaskItem row && row.Task.ScreenId == access.WaitingScreenId),
                "the originating task regains keyboard focus");
            filter.SelectedIndex = 0;
            Click(classesNav);
            await Until(() => classes.IsEffectivelyVisible && classes.ItemCount > 0, "class directory remains a separate workspace");
            classes.SelectedItem = classes.Items.Cast<Classroom>().Single(room => room.Id == access.ClassEightId);
            Check(IsScreenVisible(access.WaitingScreenId) && IsScreenVisible(access.ReadyScreenId), "both screens remain reachable from the class workspace");
            foreach (var name in new[] { "OpenClassCreationButton", "OpenTeachingButton", "OpenClassRosterButton",
                "OpenSubjectSetupButton", "OpenClassMaintenanceButton", "OpenClassStudyButton" })
                Check(Find<Button>(main, name).IsEffectivelyVisible && Find<Button>(main, name).IsEnabled, "reachable legacy entry " + name);
            Check(classTitle.Text == "高二（8）班" && classContext.Text?.Contains("G2-8") == true, "class heading identifies the selected class and its code");
            await Capture(main, output, "class-home");
            var selectedClassRow = OutlineOf(classes, classes.SelectedIndex);
            Check(selectedClassRow.CornerRadius.TopLeft >= 8 && selectedClassRow.BorderThickness.Left > 0 &&
                Find<Border>(main, "ClassHeader").CornerRadius.TopLeft >= 8, "class entries and the class summary have rounded outlines");
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            await Capture(main, output, "class-home-dark");
            Application.Current.RequestedThemeVariant = ThemeVariant.Light;
            classQuery.Text = "高二 g2-8";
            await Until(() => classes.ItemCount == 1 && (classes.SelectedItem as Classroom)?.Id == access.ClassEightId, "class search combines grade and code");
            var rosterEntry = Find<Button>(main, "OpenClassRosterButton");
            Click(rosterEntry);
            await Until(() => desktop.Windows.OfType<ClassRosterWindow>().Any(w => w.IsVisible), "roster opens from this exact class");
            desktop.Windows.OfType<ClassRosterWindow>().Single().Close();
            await Until(() => rosterEntry.IsFocused && classes.ItemCount == 1 && (classes.SelectedItem as Classroom)?.Id == access.ClassEightId,
                "roster returns focus without clearing the class filter");
            var teacherEntry = Find<Button>(main, "OpenTeachingButton");
            Click(teacherEntry);
            await Until(() => desktop.Windows.OfType<TeachingWindow>().Any(w => w.IsVisible), "teaching opens from the same class");
            desktop.Windows.OfType<TeachingWindow>().Single().Close();
            await Until(() => teacherEntry.IsFocused && (classes.SelectedItem as Classroom)?.Id == access.ClassEightId,
                "teaching returns to the same class and its action");
            var study = Find<Button>(main, "OpenClassStudyButton");
            Click(study);
            Check(Find<ClassStudyView>(main, "StudyWorkspace").IsEffectivelyVisible, "the existing study page opens");
            Click(study);
            Click(Find<Button>(main, "OpenSchoolAuditButton"));
            Check(Find<SchoolAuditView>(main, "SchoolAuditWorkspace").IsEffectivelyVisible, "audit remains available from the shell");
            Click(overviewNav);
            await Until(() => Has("ADMIN_CLASS_WITHOUT_SCREEN", access.ClassSevenId), "overview reload after changing workspaces");

            await OpenTask("ADMIN_CLASS_WITHOUT_SCREEN", access.ClassSevenId);
            var prepare = Find<Button>(main, "PrepareFirstScreenButton");
            await Until(() => prepare.IsEffectivelyVisible && goBack.IsEffectivelyVisible, "open the existing first-screen registration");
            Check(classes.SelectedItem is Classroom room && room.Id == access.ClassSevenId && room.Screens.Count == 0,
                "no write occurred just by opening the task");
            Check(string.IsNullOrEmpty(classQuery.Text), "an exact overview target outside the prior filter is revealed");
            await Command("holdregistration");
            Click(prepare);
            await Command("held");
            classQuery.Text = "G2-8";
            await Until(() => classes.SelectedItem is null && classes.ItemCount == 1, "a filter may hide a class awaiting its registration receipt");
            await Command("release");
            var firstScreenFeedback = Find<TextBlock>(main, "FirstScreenFeedback");
            await Until(() => firstScreenFeedback.Text?.Contains("教室大屏已登记") == true, "the original request retains its receipt while filtered out");
            Check(classes.SelectedItem is null && !firstScreenFeedback.IsEffectivelyVisible, "a late receipt does not change the filtered view");
            classQuery.Clear();
            await Until(() => classes.Items.Cast<Classroom>().Any(room => room.Id == access.ClassSevenId), "reveal the registered class");
            classes.SelectedItem = classes.Items.Cast<Classroom>().First(room => room.Id == access.ClassSevenId);
            var checkRegistration = Find<Button>(main, "RefreshFirstScreenButton");
            await Until(() => checkRegistration.IsEffectivelyVisible && !prepare.IsEffectivelyVisible, "reopening the class offers readback of its registration");
            Click(checkRegistration);
            await Until(() => Find<TextBlock>(main, "AdmissionScope").Text?.Contains("（7）") == true, "registered record opens its existing admission view");
            var sevenScreen = ((Classroom)classes.SelectedItem!).Screens.Single().Id;
            Click(goBack);
            await Until(() => Has("SCREEN_AWAITING_PAIRING", access.ClassSevenId, sevenScreen) &&
                !Has("ADMIN_CLASS_WITHOUT_SCREEN", access.ClassSevenId), "registration changes the next step without claiming a connection");
            await Capture(main, output, "04-after-registration");
            Click(devicesNav);
            await Until(() => screenDirectory.IsEffectivelyVisible && screens.Items.OfType<OverviewScreenItem>().Any(r => r.ScreenId == sevenScreen),
                "registration appears in the directory");
            SelectStatus(nativeFilter, "AWAITING_PAIRING");
            Check(screens.Items.OfType<OverviewScreenItem>().Select(r => r.ScreenId).Order().SequenceEqual(new[] { sevenScreen, access.WaitingScreenId }.Order()),
                "both pending screens in different classes are visible together");
            await Capture(main, output, "04a-cross-class-pairing");
            await OpenDirectoryScreen(sevenScreen);
            await Until(() => IsOnlyScreenVisible(sevenScreen) && (classes.SelectedItem as Classroom)?.Id == access.ClassSevenId,
                "second class opens its own pending screen");

            await Command("pair");
            Click(Find<Button>(main, "ReturnToDeviceDirectoryButton"));
            await Until(() => screenDirectory.IsEffectivelyVisible && OnlyDirectoryScreen(access.WaitingScreenId) && deviceSearch.IsFocused,
                "after pairing the original filter re-reads KV and restores focus when the completed row has gone");
            Click(clearDirectory);
            Click(overviewNav);
            await Until(() => Has("SCREEN_AWAITING_REPORT", access.ClassSevenId, sevenScreen), "authenticated terminal is paired without a first report");
            await Command("renew");
            Click(refresh);
            await Until(() => Has("SCREEN_AWAITING_REPORT", access.ClassSevenId, sevenScreen), "a renewed session on a newly paired device still awaits the first report");
            await OpenTask("SCREEN_AWAITING_REPORT", access.ClassSevenId, sevenScreen);
            await Until(() => goBack.IsEffectivelyVisible && main.GetVisualDescendants().OfType<Border>()
                .Any(card => AutomationProperties.GetAutomationId(card) == "SchoolScreen-" + sevenScreen && card.IsEffectivelyVisible),
                "pending report opens the right binding");
            Click(goBack);
            await Until(() => Has("SCREEN_AWAITING_REPORT", access.ClassSevenId, sevenScreen), "report task stays pending after return");
            await Capture(main, output, "05-awaiting-report");

            await Command("report");
            Click(refresh);
            await Until(() => screens.Items.Cast<OverviewScreenItem>().Any(row => row.Title.Contains("（7）") &&
                row.Native is { State: "REPORTED", HasRecord: true }), "effective report time appears");
            Check(!Has("SCREEN_AWAITING_REPORT", access.ClassSevenId, sevenScreen) && Has("SCREEN_AWAITING_PAIRING", screen: access.WaitingScreenId),
                "only the task for the reported screen clears");
            await Command("restart");
            Click(refresh);
            await Until(() => screens.Items.Cast<OverviewScreenItem>().Any(row => row.Title.Contains("（7）") &&
                row.Native.State == "REPORTED"), "Host session renewal does not resurrect the finished task");
            Click(devicesNav);
            await Until(() => screenDirectory.IsEffectivelyVisible && screens.ItemCount > 1, "open directory after a first report");
            await Capture(main, output, "06-reported-and-restarted");
            Application.Current.RequestedThemeVariant = ConsoleHighContrast.Dark;
            await Capture(main, output, "06a-directory-high-contrast");
            Application.Current.RequestedThemeVariant = ThemeVariant.Light;
            SelectStatus(nativeFilter, "REPORTED");
            deviceSearch.Text = "高二";
            Click(overviewNav);
            await Until(() => Has("SCREEN_AWAITING_PAIRING", screen: access.WaitingScreenId), "back to overview queue");

            await Command("fail");
            Click(refresh);
            await Until(() => View<TextBlock>("OverviewFeedback").Text?.Contains("待核对") == true, "overview read failure");
            Check(tasks.ItemCount == 0 && !View<StackPanel>("OverviewResults").IsVisible, "failure hides old observations and any completion indicator");
            await Capture(main, output, "07-read-failed");
            Click(classesNav);
            var inactive = Find<CheckBox>(main, "InactiveClassesFilter");
            inactive.IsChecked = true;
            await Until(() => inactive.IsChecked == true && !Find<ProgressBar>(main, "WorkspaceProgress").IsVisible,
                "the previous school used the inactive filter");
            Click(overviewNav);
            await Until(() => Has("SCREEN_AWAITING_PAIRING", screen: access.WaitingScreenId), "refresh prior to delaying the old school");
            await Command("hold");
            Click(refresh);
            await Command("held");
            classQuery.Text = "上个学校的班";
            picker.Open();
            picker.SearchText = "后排";
            Check(picker.Results.Any(item => item.ScreenId == access.WaitingScreenId), "school-scoped object picker includes the rear binding");
            SelectSchool(access.OtherSchoolId);
            Check(string.IsNullOrEmpty(classQuery.Text), "switching schools clears the previous class query");
            Check(inactive.IsChecked == false && !picker.IsOpen && picker.Results.All(item => item.ScreenId != access.WaitingScreenId),
                "switching schools resets the inactive filter and clears the open picker immediately");
            Check(nativeFilter.SelectedIndex == 0 && browserFilter.SelectedIndex == 0 && string.IsNullOrEmpty(deviceSearch.Text),
                "switching schools clears both device filters and the previous school's search");
            await Until(() => showTasks.IsEffectivelyVisible, "the new school overview finishes loading");
            Click(showTasks);
            await Until(() => Has("NO_ACTIVE_TERM"), "the new school explains no active term");
            await Command("release");
            await Task.Delay(220);
            Check(Has("NO_ACTIVE_TERM") && screens.ItemCount == 0 && !Has("SCREEN_AWAITING_PAIRING", screen: access.WaitingScreenId),
                "the previous school's late overview cannot replace the new one");
            await Capture(main, output, "08-no-active-term");

            await Command("activate");
            Click(refresh);
            await Until(() => Has("TERM_WITHOUT_GRADE") && Has("TERM_WITHOUT_ADMIN_CLASS"), "empty active term offers setup tasks");
            await OpenTask("TERM_WITHOUT_GRADE");
            var setup = await CreationWindow();
            await Until(() => Find<TextBox>(setup, "NewGradeName").IsEffectivelyVisible, "grade task directly enters the existing grade form");
            Find<TextBox>(setup, "NewGradeName").Text = "高一";
            Find<TextBox>(setup, "NewGradeCode").Text = "G1";
            Click(Find<Button>(setup, "SubmitGradeCreationButton"));
            await Until(() => Find<ComboBox>(setup, "CreationGrade").SelectedItem is ClassCreationGrade {Code: "G1"}, "grade is created on the server");
            setup.Close();
            await Until(() => !Has("TERM_WITHOUT_GRADE") && Has("TERM_WITHOUT_ADMIN_CLASS"), "closing the modal refreshes the setup tasks");
            await OpenTask("TERM_WITHOUT_ADMIN_CLASS");
            setup = await CreationWindow();
            await Until(() => Find<ComboBox>(setup, "CreationGrade").SelectedItem is ClassCreationGrade {Code: "G1"} &&
                Find<Button>(setup, "OpenClassBatchButton").IsEnabled, "class task offers the existing single and batch flows");
            setup.Close();
            SelectSchool(access.SchoolId);
            await Until(() => showTasks.IsEffectivelyVisible, "restore the original school before the compact-window check");
            main.Width = 840;
            main.Height = 480;
            await Until(() => Find<Grid>(main, "Workspace").Classes.Contains("compact"), "condense side navigation in a small window");
            await Capture(main, output, "09-compact-overview");
            Click(classesNav);
            await Until(() => classes.Items.Cast<Classroom>().Any(room => room.Id == access.ClassEightId), "the original classes finish loading");
            classes.SelectedItem = classes.Items.Cast<Classroom>().Single(room => room.Id == access.ClassEightId);
            await Capture(main, output, "10-compact-class");
            foreach (var entry in new[] { rosterEntry, teacherEntry, Find<Button>(main, "OpenSubjectSetupButton") })
                Check(entry.IsEffectivelyVisible && entry.TranslatePoint(default, main) is { } pos &&
                    pos.X >= 0 && pos.X + entry.Bounds.Width <= main.Bounds.Width &&
                    pos.Y >= 0 && pos.Y + entry.Bounds.Height <= main.Bounds.Height,
                    "compact class home shows the complete " + entry.Name + " action");
            await Command("scale");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Click(devicesNav);
            await Until(() => screenDirectory.IsEffectivelyVisible && screens.ItemCount >= 204, "the full directory is available for filtering");
            var loadMs = watch.ElapsedMilliseconds;
            await Until(() => screens.GetVisualDescendants().OfType<ListBoxItem>().Any(), "large directory has visible rows");
            int realized = screens.GetVisualDescendants().OfType<ListBoxItem>().Count();
            Check(realized < screens.ItemCount / 2, "the directory virtualizes rows in a short window");
            watch.Restart();
            deviceSearch.Text = "同步教室综合展示大屏";
            await Until(() => screens.ItemCount == 2 && screens.GetVisualDescendants().OfType<Button>().Count(b =>
                b.IsEffectivelyVisible && b.Tag is OverviewScreenItem row && row.Title.Contains("同步教室")) == 2, "both same-name bindings are available");
            var filterMs = watch.ElapsedMilliseconds;
            var twins = screens.Items.Cast<OverviewScreenItem>().ToArray();
            Check(twins.All(row => row.Title.Contains(row.ScreenId)), "both rows identify their distinct binding");
            await Capture(main, output, "11-duplicate-screens");
            await OpenDirectoryScreen(twins[1].ScreenId);
            await Until(() => IsOnlyScreenVisible(twins[1].ScreenId) && Find<TextBlock>(main, "ClassContext").Text?.Contains(twins[1].ScreenId) == true,
                "detail retains the selected same-name screen ID");
            Click(Find<Button>(main, "ReturnToDeviceDirectoryButton"));
            await Until(() => screens.GetVisualDescendants().OfType<Button>().Any(b => b.IsFocused && b.Tag is OverviewScreenItem row && row.ScreenId == twins[1].ScreenId),
                "return restores focus to the same-name screen by ID");
            Console.WriteLine($"Directory measurement: load 204 bindings {loadMs} ms; filter to two visible bindings {filterMs} ms; {realized} realized list items; scale {main.RenderScaling}.");
            await Command("classlist");
            Click(classesNav);
            Click(Find<Button>(main, "RefreshButton"));
            await Until(() => classes.ItemCount >= 105, "large current-term class directory");
            classQuery.Text = "东区实验班";
            await Until(() => classes.ItemCount == 2, "same-name classes are both available");
            classQuery.Text = "东区 g3-east";
            await Until(() => classes.ItemCount == 1, "search narrows the duplicate by class code");
            classQuery.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = classQuery });
            await Until(() => classes.SelectedItem is Classroom { Code: "G3-EAST" } && classContext.Text?.Contains("高三") == true,
                "Enter selects the intended class by identity");
            classQuery.Text = "不存在的班";
            await Until(() => classes.ItemCount == 0 && classes.SelectedItem is null && !rosterEntry.IsEffectivelyVisible,
                "a no-match search removes actions for the previous class");
            classQuery.Text = "G3-PAUSE";
            inactive.IsChecked = true;
            await Until(() => classes.ItemCount == 1 && classes.Items.OfType<Classroom>().Single().Code == "G3-PAUSE",
                "the retired directory supports code search");
            classes.SelectedIndex = 0;
            await Until(() => classContext.Text?.Contains("已停用") == true && !rosterEntry.IsEffectivelyVisible &&
                Equals(Find<Button>(main, "OpenClassMaintenanceButton").Content, "重新启用"), "retired class exposes its recovery action only");
            await Capture(main, output, "class-retired");
            Finish("PASS: Windows shell, cross-class dual-status filters, binding search, shared class and directory detail, independent dated browser and native evidence, filtered return and keyboard focus, registration → pairing → report, failed and late reads, legacy entries, compact and high-contrast views.", 0);
        }
        catch (Exception error) { Finish("FAIL: " + error, 1); }

        T View<T>(string name) where T : Control => view.FindControl<T>(name) ?? throw new InvalidDataException(name);
        static ContentPresenter OutlineOf(ListBox list, int index)
        {
            var item = list.ContainerFromIndex(index) ?? throw new InvalidDataException("Missing list item");
            return item.GetVisualDescendants().OfType<ContentPresenter>().First(presenter => presenter.TemplatedParent == item);
        }
        void SelectSchool(string id) => schools.SelectedItem = schools.Items.Cast<School>().Single(school => school.Id == id);
        bool Matches(OverviewTaskItem item, string code, string? room, string? screen) =>
            item.Task.Code == code && (room is null || item.Task.ClassId == room) && (screen is null || item.Task.ScreenId == screen);
        bool Has(string code, string? room = null, string? screen = null) =>
            tasks.Items.Cast<OverviewTaskItem>().Any(item => Matches(item, code, room, screen));
        async Task OpenTask(string code, string? room = null, string? screen = null)
        {
            var item = tasks.Items.Cast<OverviewTaskItem>().First(row => Matches(row, code, room, screen));
            tasks.ScrollIntoView(item);
            Button? action = null;
            await Until(() => (action = tasks.GetVisualDescendants().OfType<Button>().FirstOrDefault(button =>
                ReferenceEquals(button.Tag, item) && button.IsEffectivelyVisible)) is not null, "real task control " + code);
            Click(action!);
        }
        bool IsScreenVisible(string id) => main.GetVisualDescendants().OfType<Border>().Any(card =>
            card.IsEffectivelyVisible && AutomationProperties.GetAutomationId(card) == "SchoolScreen-" + id);
        bool IsOnlyScreenVisible(string id) => IsScreenVisible(id) &&
            main.GetVisualDescendants().OfType<Border>().Count(card =>
                card.IsEffectivelyVisible && AutomationProperties.GetAutomationId(card)?.StartsWith("SchoolScreen-", StringComparison.Ordinal) == true) == 1;
        bool OnlyDirectoryScreen(string id) => screens.Items.OfType<OverviewScreenItem>().ToArray() is [var row] && row.ScreenId == id;
        static void SelectStatus(ComboBox box, string status) =>
            box.SelectedItem = box.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, status));
        async Task OpenDirectoryScreen(string id)
        {
            var row = screens.Items.Cast<OverviewScreenItem>().Single(item => item.ScreenId == id);
            screens.ScrollIntoView(row);
            Button? action = null;
            await Until(() => (action = screens.GetVisualDescendants().OfType<Button>().FirstOrDefault(button =>
                ReferenceEquals(button.Tag, row) && button.IsEffectivelyVisible)) is not null, "open the exact directory screen");
            Click(action!);
        }
        async Task Command(string action)
        {
            using var reply = await control.PostAsync("/__school-overview/" + action, null);
            Check(reply.IsSuccessStatusCode, "isolated fixture command " + action);
        }
        async Task<ClassCreationWindow> CreationWindow()
        {
            await Until(() => desktop.Windows.OfType<ClassCreationWindow>().Any(window => window.IsVisible), "show class creation window");
            return desktop.Windows.OfType<ClassCreationWindow>().Single();
        }
        void Finish(string message, int exitCode)
        {
            File.WriteAllText(Path.Combine(output, "result.txt"), message);
            Console.WriteLine(message);
            desktop.Shutdown(exitCode);
        }
    }
}
