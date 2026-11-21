using System.Net.Http;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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
        var tasks = View<ListBox>("OverviewTasksList");
        var screenDirectory = Find<SchoolDeviceDirectoryView>(main, "DeviceDirectory");
        var screens = screenDirectory.FindControl<ListBox>("DeviceDirectoryRows")!;
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
            await OpenDirectoryScreen(access.WaitingScreenId);
            await Until(() => IsOnlyScreenVisible(access.WaitingScreenId), "device directory reaches the exact binding and hides sibling cards");
            Check(Find<TextBlock>(main, "ClassWorkspaceTitle").Text == "后排待接入" &&
                !Find<Button>(main, "OpenClassRosterButton").IsEffectivelyVisible, "the screen title names the binding and keeps class actions off the device page");
            Click(Find<Button>(main, "OpenScreenClassButton"));
            Check(classes.IsEffectivelyVisible && (classes.SelectedItem as Classroom)?.Id == access.ClassEightId &&
                Find<Button>(main, "OpenClassRosterButton").IsEffectivelyVisible,
                "the screen's class link opens the original class with its operations available");
            Click(Find<Button>(main, "ReturnToDeviceDirectoryButton"));
            await Until(() => screenDirectory.IsEffectivelyVisible && screens.ItemCount > 1, "device detail returns to refreshed directory");
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
            Click(prepare);
            await Until(() => Find<TextBlock>(main, "AdmissionScope").Text?.Contains("（7）") == true, "registered record opens its existing admission view");
            var sevenScreen = ((Classroom)classes.SelectedItem!).Screens.Single().Id;
            Click(goBack);
            await Until(() => Has("SCREEN_AWAITING_PAIRING", access.ClassSevenId, sevenScreen) &&
                !Has("ADMIN_CLASS_WITHOUT_SCREEN", access.ClassSevenId), "registration changes the next step without claiming a connection");
            await Capture(main, output, "04-after-registration");

            await Command("pair");
            Click(refresh);
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
                row.Native.Contains("已收到有效上报") && row.Native.Contains("最近记录")), "effective report time appears");
            Check(!Has("SCREEN_AWAITING_REPORT", access.ClassSevenId, sevenScreen) && Has("SCREEN_AWAITING_PAIRING", screen: access.WaitingScreenId),
                "only the task for the reported screen clears");
            await Command("restart");
            Click(refresh);
            await Until(() => screens.Items.Cast<OverviewScreenItem>().Any(row => row.Title.Contains("（7）") &&
                row.Native.Contains("已收到有效上报")), "Host session renewal does not resurrect the finished task");
            Click(devicesNav);
            await Until(() => screenDirectory.IsEffectivelyVisible && screens.ItemCount > 1, "open directory after a first report");
            await Capture(main, output, "06-reported-and-restarted");
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
            picker.Open();
            picker.SearchText = "后排";
            Check(picker.Results.Any(item => item.ScreenId == access.WaitingScreenId), "school-scoped object picker includes the rear binding");
            SelectSchool(access.OtherSchoolId);
            Check(inactive.IsChecked == false && !picker.IsOpen && picker.Results.All(item => item.ScreenId != access.WaitingScreenId),
                "switching schools resets the inactive filter and clears the open picker immediately");
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
            Check(Find<Button>(main, "OpenClassRosterButton").IsEffectivelyVisible && classes.IsEffectivelyVisible,
                "class list and operation strip stay reachable in a compact window");
            Finish("PASS: Windows shell, login and school selection, exact targets in tasks and device directory, filtered return and focus, registration → pairing → report, fail-closed reads, late school response, legacy entry points, light/dark and compact layout.", 0);
        }
        catch (Exception error) { Finish("FAIL: " + error, 1); }

        T View<T>(string name) where T : Control => view.FindControl<T>(name) ?? throw new InvalidDataException(name);
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
