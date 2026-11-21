using System.Net.Http;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
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
        var screens = View<ListBox>("OverviewScreensList");
        var refresh = View<Button>("RefreshSchoolOverviewButton");
        var tabs = View<TabControl>("SchoolOverviewTabs");
        var goBack = Find<Button>(main, "ReturnToSchoolOverviewButton");
        using var control = new HttpClient {BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(8)};
        try
        {
            Find<TextBox>(main, "ServerAddress").Text = access.Origin;
            Find<TextBox>(main, "SchoolCode").Text = access.SchoolCode;
            Find<TextBox>(main, "Username").Text = access.Username;
            Find<TextBox>(main, "Password").Text = access.Password;
            Click(Find<Button>(main, "LoginButton"));
            await Until(() => schools.ItemCount == 2, "both authorized fixture schools");
            SelectSchool(access.SchoolId);
            await Until(() => view.IsEffectivelyVisible && Has("ADMIN_CLASS_WITHOUT_SCREEN", access.ClassSevenId) &&
                Has("SCREEN_AWAITING_PAIRING", screen: access.WaitingScreenId), "overview opens by default with two exact targets");
            Check(!tasks.Items.Cast<OverviewTaskItem>().Any(row => row.Task.ScreenId == access.ReadyScreenId) &&
                Has("SCREEN_REPAIR_REQUIRED", screen: access.RestoredScreenId), "reported screen is clear and restored binding needs re-pairing");
            Check(screens.Items.Cast<OverviewScreenItem>().Count(row => row.Title.Contains("（8）")) == 2 &&
                screens.Items.Cast<OverviewScreenItem>().All(row => !row.Title.Contains("往届")), "the archived same-code screen is isolated");
            await Capture(main, output, "01-classroom-tasks");
            tabs.SelectedIndex = 1;
            await Capture(main, output, "02-screen-evidence");
            tabs.SelectedIndex = 0;

            await OpenTask("SCREEN_AWAITING_PAIRING", screen: access.WaitingScreenId);
            await Until(() => goBack.IsEffectivelyVisible &&
                Find<TextBlock>(main, "AdmissionScope").Text?.Contains("后排待接入") == true, "the exact rear screen admission is opened");
            Check(classes.SelectedItem is Classroom {Id: var selected} && selected == access.ClassEightId, "the target class is selected by ID");
            await Capture(main, output, "03-exact-screen");
            Click(goBack);
            await Until(() => Has("ADMIN_CLASS_WITHOUT_SCREEN", access.ClassSevenId), "return re-reads the original school");
            Check(classes.SelectedItem is null, "Returning clears the previous sidebar selection.");
            classes.SelectedItem = classes.Items.Cast<Classroom>().Single(room => room.Id == access.ClassEightId);
            await Until(() => goBack.IsEffectivelyVisible && !view.IsEffectivelyVisible, "same class remains reachable directly from the sidebar");
            Click(goBack);
            await Until(() => Has("ADMIN_CLASS_WITHOUT_SCREEN", access.ClassSevenId), "overview reload after the sidebar route");

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
            tabs.SelectedIndex = 1;
            await Capture(main, output, "06-reported-and-restarted");
            tabs.SelectedIndex = 0;

            await Command("fail");
            Click(refresh);
            await Until(() => View<TextBlock>("OverviewFeedback").Text?.Contains("待核对") == true, "overview read failure");
            Check(tasks.ItemCount == 0 && !View<Grid>("OverviewResults").IsVisible, "failure hides old observations and any completion indicator");
            await Capture(main, output, "07-read-failed");
            await Command("hold");
            Click(refresh);
            await Command("held");
            SelectSchool(access.OtherSchoolId);
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
            Finish("PASS: Windows overview, archive/current isolation, multi-screen targeting, registration → pairing → report, recovery, failed read, late school response, and existing setup entry points.", 0);
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
