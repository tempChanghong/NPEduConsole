using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private sealed record ExamAccess(string Origin, string SchoolCode, string SchoolId, string Pin, string RoomId,
        string FrontId, string RearId, string FrontDeviceId, string RearDeviceId, string ModeOnlyId, string ModeOnlyDeviceId,
        bool AllowWrites);

    private static int StartExamWorkbench(string fixture, string output)
    {
        var access = JsonSerializer.Deserialize<ExamAccess>(File.ReadAllText(fixture), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Expected isolated exam-workbench fixture");
        Check(access.AllowWrites && IsLoopback(access.Origin), "Exam-workbench writes require a disposable loopback fixture");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyExamWorkbenchAsync(access, output); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyExamWorkbenchAsync(ExamAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        var mainHandle = main.TryGetPlatformHandle()?.Handle;
        using var checkpoints = new HttpClient { BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(75) };
        try
        {
            main.Width = 1160;
            main.Height = 780;
            Find<TextBox>(main, "ServerAddress").Text = access.Origin;
            Find<TextBox>(main, "SchoolCode").Text = access.SchoolCode;
            Find<TextBox>(main, "Username").Text = "admin";
            Find<TextBox>(main, "Password").Text = access.Pin;
            Click(Find<Button>(main, "LoginButton"));
            var schools = Find<ComboBox>(main, "SchoolPicker");
            await Until(() => schools.Items.OfType<School>().Any(item => item.Id == access.SchoolId), "authenticated school available");
            schools.SelectedItem = schools.Items.OfType<School>().Single(item => item.Id == access.SchoolId);
            var classNav = Find<Button>(main, "OpenClassWorkspaceButton");
            await Until(() => classNav.IsEffectivelyVisible && classNav.IsEnabled, "normal class entrance");
            Click(classNav);
            var classList = Find<ListBox>(main, "ClassPicker");
            await Until(() => classList.Items.OfType<Classroom>().Any(room => room.Id == access.RoomId), "target classroom loaded");
            classList.SelectedItem = classList.Items.OfType<Classroom>().Single(room => room.Id == access.RoomId);
            Button? classEntry = null;
            await Until(() => (classEntry = main.GetVisualDescendants().OfType<Button>().FirstOrDefault(button =>
                button.IsEffectivelyVisible && AutomationProperties.GetAutomationId(button) == "ClassScreenExam-" + access.FrontId)) is not null,
                "class card exposes the front screen's workbench");
            Click(classEntry!);
            var front = await Open();
            await Until(() => Label(front, "ExamPhase") == "当前处于日常模式" && Target(front, access.FrontId, access.FrontDeviceId),
                "front screen is isolated and remains daily");
            Check(!Find<Button>(front, "ExamChooseFile").IsEnabled, "no plan launch without a current authorized plan observation");
            Click(Find<Button>(front, "ExamBack"));
            await Until(() => !front.IsVisible && classEntry!.IsFocused, "class card focus restored");

            Click(Find<Button>(main, "OpenDeviceDirectoryButton"));
            var devices = Find<SchoolDeviceDirectoryView>(main, "DeviceDirectory");
            var search = devices.FindControl<TextBox>("DeviceSearch")!;
            await Until(() => search.IsEffectivelyVisible, "regular device directory available");
            search.Text = "七班后屏";
            var rows = devices.FindControl<ListBox>("DeviceDirectoryRows")!;
            await Until(() => rows.Items.OfType<OverviewScreenItem>().Any(item => item.ScreenId == access.RearId), "rear available by binding");
            var rearRow = rows.Items.OfType<OverviewScreenItem>().Single(row => row.ScreenId == access.RearId);
            Button? detailButton = null;
            await Until(() => (detailButton = rows.GetVisualDescendants().OfType<Button>().FirstOrDefault(button =>
                button.IsEffectivelyVisible && ReferenceEquals(button.Tag, rearRow))) is not null, "exact rear row's detail button");
            Click(detailButton!);
            var entry = Find<Button>(main, "OpenScreenExamButton");
            await Until(() => entry.IsEffectivelyVisible, "device detail exposes the exam entrance");
            Click(entry);
            var window = await Open();
            await Until(() => Label(window, "ExamPhase") == "当前处于日常模式" && Target(window, access.RearId, access.RearDeviceId)
                && Find<Button>(window, "ExamPrimary").IsEnabled, "fresh rear mode observation and primary operation");
            Check(Grid.GetColumn(Find<StackPanel>(window, "ExamRightSection")) == 1 &&
                !Find<StackPanel>(window, "ExamPlanSection").IsEffectivelyVisible, "wide mode and checks sit side by side, with optional plans collapsed");
            await CaptureWithFrame(main, output, "01-wide-exam-preflight");
            await Signal("rear-selected");

            Click(Find<Button>(window, "ExamPrimary"));
            await Until(() => Label(window, "ExamPhase") == "考试环境已就绪" &&
                Label(window, "ExamOperationResult").Contains("设备已回报切换完成") && Find<Button>(window, "ExamChooseFile").IsEnabled,
                "wait for actual exam environment report before selecting plan");
            await Signal("exam-environment");
            Check(!Find<Button>(window, "ExamChooseFile").IsEffectivelyVisible, "the mode operation does not require a plan or expose its tools by default");
            Click(Find<Button>(window, "ExamWidePlanButton"));
            await Until(() => Find<Button>(window, "ExamChooseFile").IsEffectivelyVisible, "optional plan tools are revealed explicitly");

            var date = DateTime.Now.Date.AddDays(1).ToString("yyyy-MM-dd");
            var contents = JsonSerializer.SerializeToUtf8Bytes(new
            {
                examName = "隔离考试方案",
                message = "",
                examInfos = new[]
                {
                    new {name = "隔离语文考试", start = date + "T09:00:00", end = date + "T11:00:00", alertTime = 15, materials = Array.Empty<string>()},
                    new {name = "隔离数学考试", start = date + "T14:00:00", end = date + "T16:00:00", alertTime = 10, materials = Array.Empty<string>()}
                }
            });
            await window.LoadExamFileAsync("七班验收.ea2", new MemoryStream(contents));
            await Until(() => Label(window, "ExamPrimaryLabel") == "投递给设备校验" && Find<Button>(window, "ExamPrimary").IsEnabled,
                "file accepted by the desktop selection path");
            Click(Find<Button>(window, "ExamPrimary"));
            await Until(() => Label(window, "ExamPhase").Contains("设备校验通过") && Find<Border>(window, "ExamSchedulePanel").IsVisible &&
                Find<Button>(window, "ExamPrimary").IsEnabled, "actual device returned schedule before explicit start");
            await Signal("prepared");
            main.Width = 840;
            main.Height = 480;
            var schedule = Find<ExamScheduleView>(window, "ExamSchedule");
            await Until(() => main.ClientSize.Width <= 845 && main.ClientSize.Height <= 485 &&
                schedule.GetVisualDescendants().OfType<TextBlock>().Any(item => item.Text == "隔离数学考试"), "second exam retained in the narrow layout");
            Check(Find<Border>(window, "ExamStepBar").IsEffectivelyVisible &&
                !Find<Border>(window, "ExamStatusCard").IsEffectivelyVisible &&
                !Find<Border>(window, "ExamPlanSetupCard").IsEffectivelyVisible, "narrow prepared plan step opens directly to its schedule");
            var secondExam = schedule.GetVisualDescendants().OfType<Border>().Single(item =>
                item.IsEffectivelyVisible && AutomationProperties.GetName(item)?.StartsWith("隔离数学考试，") == true);
            Check(secondExam.Focus(NavigationMethod.Tab), "second narrow exam is keyboard focusable");
            secondExam.BringIntoView();
            await Until(() => secondExam.IsFocused, "focus stays on the second narrow exam");
            Check(secondExam.GetVisualDescendants().OfType<TextBlock>().Any(item => item.Text?.EndsWith("14:00–16:00") == true),
                "narrow schedule displays a single local date and the original exam times");
            var primary = Find<Button>(window, "ExamPrimary");
            Check(primary.TranslatePoint(new Point(primary.Bounds.Width, primary.Bounds.Height), window) is { } corner &&
                corner.X <= window.Bounds.Width && corner.Y <= window.Bounds.Height &&
                Label(window, "ExamPrimaryLabel").Contains("七班后屏"), "narrow window retains the targeted confirmation action");
            await CaptureWithFrame(main, output, "02-narrow-device-schedule");
            Click(primary);
            await Until(() => Label(window, "ExamPrimaryLabel") == "核对原请求" && primary.IsEffectivelyEnabled,
                "a dropped start response and unavailable receipt leave a readable original request");
            Check(Find<Border>(window, "ExamStatusCard").IsEffectivelyVisible &&
                !Find<Button>(window, "ExamRefresh").IsEffectivelyVisible, "start restores the status step with one receipt check action");
            await CaptureWithFrame(main, output, "02b-narrow-start-receipt-pending");
            Click(Find<Button>(window, "ExamBack"));
            await Until(() => !window.IsVisible && entry.IsFocused, "pending receipt permits return to the original screen entry");
            await Signal("start-receipt-pending");
            Click(entry);
            window = await Open();
            primary = Find<Button>(window, "ExamPrimary");
            await Until(() => Label(window, "ExamPhase") == "本次放映已就绪" &&
                Label(window, "ExamNotice").Contains("已核对原请求"), "reopen recovers the original start and playback evidence separately");
            Check(!Find<Button>(window, "ExamPrimary").IsVisible, "live playback cannot be replaced or switched remotely");
            await CaptureWithFrame(main, output, "03-narrow-real-playback");
            await Signal("playing");
            await Signal("hide-playing-observation");
            await Until(() => primary.IsVisible && !primary.IsEnabled && Label(window, "ExamPrimaryLabel") == "暂不能返回日常" &&
                Label(window, "ExamNextStep").Contains("还未收到本次会话结束"), "known start with missing playback observation blocks returning");
            await Until(() => WithinViewport(window, "ExamPhase") && WithinViewport(window, "ExamNextStep"),
                "the whole status headline and reason why daily mode is blocked fit on the narrow first screen");
            await CaptureWithFrame(main, output, "03b-narrow-playback-unconfirmed");
            await Signal("allow-local-close");
            await Until(() => Label(window, "ExamPhase") == "本次放映已结束" && Label(window, "ExamPrimaryLabel") == "返回日常" && primary.IsEnabled,
                "same session closed before enabling return to daily");
            await Signal("closed");
            await CaptureWithFrame(main, output, "04-narrow-matching-session-closed");
            Click(primary);
            await Until(() => Label(window, "ExamPhase") == "当前处于日常模式" &&
                Label(window, "ExamOperationResult").Contains("返回日常") && Label(window, "ExamOperationResult").Contains("设备已回报切换完成"),
                "device reports daily restored");
            await Signal("returned");
            Click(Find<Button>(window, "ExamBack"));
            await Until(() => !window.IsVisible && entry.IsFocused, "screen detail focus restored");
            Click(entry);
            window = await Open();
            await Until(() => Label(window, "ExamPhase") == "当前处于日常模式" &&
                Label(window, "ExamOperationResult").Contains("返回日常"), "reopened workbench reads the original result");
            await CaptureWithFrame(main, output, "05-reopened-daily-result");
            await Until(() => Label(window, "ExamPrimaryLabel").Contains("进入考试") && Primary().IsEnabled,
                "another exam cycle can be started from daily");
            Click(Primary());
            await Until(() => Label(window, "ExamPhase") == "考试环境已就绪" && Label(window, "ExamPrimaryLabel") == "返回日常" &&
                Primary().IsEnabled, "previous playback is history when a subsequent exam environment opens");
            await Signal("new-cycle");
            await CaptureWithFrame(main, output, "06-second-cycle-without-old-playback");
            await Until(() => Primary().IsEnabled, "subsequent exam can return without a new playback");
            Click(Primary());
            await Until(() => Label(window, "ExamPhase") == "当前处于日常模式" && Label(window, "ExamOperationResult").Contains("返回日常"),
                "second cycle restores daily without waiting for old playback proof");
            await Signal("returned-again");
            Click(Find<Button>(window, "ExamBack"));
            await Until(() => !window.IsVisible, "rear workbench closed");

            Click(Find<Button>(main, "OpenDeviceDirectoryButton"));
            await Until(() => search.IsEffectivelyVisible, "device directory restored");
            search.Text = "环境切换验收屏";
            Button? modeOnlyRow = null;
            await Until(() => (modeOnlyRow = rows.GetVisualDescendants().OfType<Button>().FirstOrDefault(button =>
                button.IsEffectivelyVisible && button.Tag is OverviewScreenItem item && item.ScreenId == access.ModeOnlyId)) is not null,
                "environment-only screen available through exact binding");
            Click(modeOnlyRow!);
            await Until(() => entry.IsEffectivelyVisible && entry.IsEnabled, "environment-only exam entrance");
            Click(entry);
            window = await Open();
            await Until(() => Target(window, access.ModeOnlyId, access.ModeOnlyDeviceId) &&
                Label(window, "ExamPrimaryLabel").Contains("进入考试") && Primary().IsEnabled, "independent mode authorization");
            Click(Primary());
            await Until(() => Label(window, "ExamPhase") == "考试环境已就绪" && Label(window, "ExamPrimaryLabel") == "返回日常" && Primary().IsEnabled,
                "confirmed empty plan list permits returning without a plan observation");
            await Signal("fail-mode-only-plan-read");
            await Until(() => Label(window, "ExamPhase") == "设备状态待核对" && !Primary().IsVisible,
                "failed plan read cannot be interpreted as an empty list");
            await CaptureWithFrame(main, output, "07-mode-only-plan-read-failure");
            await Signal("restore-mode-only-plan-read");
            await Until(() => Primary().IsEnabled && Label(window, "ExamPrimaryLabel") == "返回日常",
                "return enabled again when an empty plan list was confirmed");
            await CaptureWithFrame(main, output, "08-mode-only-return");
            await Until(() => Primary().IsEnabled, "mode-only return still actionable");
            Click(Primary());
            await Until(() => Label(window, "ExamPhase") == "当前处于日常模式" && Label(window, "ExamOperationResult").Contains("返回日常"),
                "environment-only device returns to daily");
            await Signal("mode-only-returned");
            desktop.Shutdown(0);

            Button Primary() => Find<Button>(window, "ExamPrimary");
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (desktop.Windows.LastOrDefault(item => item.IsVisible) is { } current)
                try { await CaptureWithFrame(current, output, "desktop-failure"); } catch { }
            desktop.Shutdown(1);
        }

        async Task<ExamWorkbenchView> Open()
        {
            var view = await AcademicPageAsync<ExamWorkbenchView>(main);
            Check(mainHandle is not null && main.TryGetPlatformHandle()?.Handle == mainHandle &&
                desktop.Windows.Count(item => item.IsVisible) == 1, "exam workbench preserves the main HWND and its three window controls");
            return view;
        }
        async Task Signal(string name)
        {
            using var response = await checkpoints.PostAsync("/__exam/" + name, null);
            Check(response.IsSuccessStatusCode, "exam checkpoint " + name);
        }
        static bool Target(ExamWorkbenchView window, string screen, string device) =>
            Label(window, "ExamFullIdentityText").Contains("绑定 " + screen) && Label(window, "ExamFullIdentityText").Contains("设备 " + device);
        static string Label(ExamWorkbenchView window, string name) => Find<TextBlock>(window, name).Text ?? "";
        static bool WithinViewport(ExamWorkbenchView view, string name)
        {
            var text = Find<TextBlock>(view, name);
            var scroll = Find<ScrollViewer>(view, "ExamScroll");
            return text.IsEffectivelyVisible && text.TranslatePoint(default, scroll) is { } point && point.Y >= 0 &&
                point.Y + text.Bounds.Height <= scroll.Bounds.Height;
        }
    }
}
