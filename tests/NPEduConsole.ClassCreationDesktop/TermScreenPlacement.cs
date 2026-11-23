using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private sealed record ScreenPlacementAccess(string Origin, string SchoolId, string SchoolCode, string Password,
        string TermId, string TargetId, string FrontId, string RearId, string BindingId, bool AllowPlacement);

    private static int StartTermScreenPlacement(string path, string output, bool reopen)
    {
        var access = JsonSerializer.Deserialize<ScreenPlacementAccess>(File.ReadAllText(path), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Expected isolated screen placement fixture.");
        Check(access.AllowPlacement && IsLoopback(access.Origin), "Screen placement requires the disposable fixture.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyTermScreenPlacementAsync(access, output, reopen); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyTermScreenPlacementAsync(ScreenPlacementAccess access, string output, bool reopen)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        main.Width = 1160;
        main.Height = 780;
        using var control = new HttpClient {BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(30)};
        try
        {
            await Login("admin");
            var history = await OpenHistoryAsync();
            var original = Find<ListBox>(history, "TermActivatedScreens");
            await Until(() => original.IsEffectivelyVisible && original.Items.Cast<TermActivatedScreen>().Any(row => row.BindingId == access.BindingId),
                "personal activation history contains the retained screen");
            var historic = original.Items.Cast<TermActivatedScreen>().Single(row => row.BindingId == access.BindingId);
            original.SelectedItem = historic;
            Check(historic.TargetClass is null && historic.FromClass?.Name.Contains("8") == true,
                "activation history still records its original unmatched class");
            var historyAction = Find<Button>(history, "TermPrimary");
            await Until(() => historyAction.IsEffectivelyEnabled, "historic row action is available");
            if (reopen)
            {
                Check(historic.Placement is {Status: "REPORTED"} placed && placed.TargetClass.Id == access.TargetId,
                    "the separate Console process reads the later arrangement and the current accepted report");
                await Capture(history, output, "placement-09-reopened-history");
                Click(Find<Button>(history, "TermBack"));
                await Until(() => Find<Button>(history, "TermLatestActivation").IsEffectivelyVisible &&
                    Find<Button>(history, "TermBack").IsEffectivelyEnabled, "return to the term directory");
                Click(Find<Button>(history, "TermBack"));
                await Until(() => !history.IsVisible, "term window closed");
                await ReadOverview(hasRetained: false);
                await Capture(main, output, "placement-10-reopened-overview");
                Finish("PASS: fresh Windows process shows immutable activation history with later placement and no stale retained task.", 0);
                return;
            }

            Check(historyAction.Content?.ToString() == "为这台屏安排新班", "history opens the desktop arrangement action");
            Click(historyAction);
            var placement = await PlacementWindowAsync();
            await CheckOriginal(placement);
            await PickClass(placement, "G2-7");
            Check(placement.ClientSize.Width >= 1100 && Grid.GetColumn(Find<Border>(placement, "PlacementTargetCard")) == 1,
                "wide target and selected class summary sit side by side");
            await Capture(placement, output, "placement-01-wide-from-history");
            Click(Find<Button>(placement, "PlacementBack"));
            await Until(() => !placement.IsVisible && historyAction.IsEffectivelyEnabled, "return from history placement without writing");
            Click(Find<Button>(history, "TermBack"));
            await Until(() => Find<Button>(history, "TermLatestActivation").IsEffectivelyVisible &&
                Find<Button>(history, "TermBack").IsEffectivelyEnabled, "return to term directory");
            Click(Find<Button>(history, "TermBack"));
            await Until(() => !history.IsVisible, "history window closed");
            Logout();
            await Login("placement-admin");

            var tasks = await ReadOverview(hasRetained: true);
            Button? open = null;
            await Until(() =>
            {
                var task = tasks.Items.Cast<OverviewTaskItem>().FirstOrDefault(row =>
                    row.Task.Code == "TERM_SCREEN_RETAINED_ORIGINAL" && row.Task.ScreenId == access.BindingId);
                if (task is null) return false;
                open = tasks.GetVisualDescendants().OfType<Button>().FirstOrDefault(row => row.IsEffectivelyVisible && ReferenceEquals(row.Tag, task));
                if (open is null) tasks.ScrollIntoView(task);
                return open is not null;
            }, "the non-activating administrator sees the exact dashboard action");
            Click(open!);
            placement = await PlacementWindowAsync();
            placement.Width = 840;
            placement.Height = 480;
            await CheckOriginal(placement);
            await Until(() => placement.ClientSize is {Width: <= 845, Height: <= 485} &&
                Grid.GetRow(Find<Border>(placement, "PlacementTargetCard")) == 1, "compact dialog uses stacked comparison");
            await PickClass(placement, "G2-7");
            var primary = Find<Button>(placement, "PlacementPrimary");
            Check(primary.TranslatePoint(new Point(0, primary.Bounds.Height), placement) is { } edge && edge.Y <= placement.ClientSize.Height,
                "compact confirmation button is reachable");
            await Capture(placement, output, "placement-02-compact-selection");
            Find<Border>(placement, "PlacementTargetCard").BringIntoView();
            await Capture(placement, output, "placement-03-compact-existing-screens");
            Click(primary);
            var refresh = Find<Button>(placement, "PlacementRefresh");
            await Until(() => TermText(placement, "PlacementNotice").Contains("安排回执待核对") && refresh.IsEffectivelyEnabled,
                "post committed but both success response and first receipt read were unavailable");
            Check(!primary.IsEffectivelyVisible, "no write retry after failed readback");
            Click(refresh);
            await Until(() => TermText(placement, "PlacementResultTitle").Contains("等待现场配对") &&
                TermText(placement, "PlacementNotice").Contains("已核对本次安排回执"), "recover own request and new binding");
            await Capture(placement, output, "placement-04-recovered-awaiting-pairing");
            await Signal("binding-arranged");
            Click(primary);
            await Until(() => !placement.IsVisible && Find<Border>(main, "ClassAdmissionCard").IsEffectivelyVisible &&
                TermText(main, "AdmissionScope").Contains("八班无匹配屏") &&
                TermText(main, "AdmissionScope").Contains("高二（7）班"),
                "result opens on-site admission for the migrated screen, not the existing front or rear screen");
            await Capture(main, output, "placement-05-moved-screen-exact-admission");
            Logout();
            await Login("admin");
            history = await OpenHistoryAsync();
            original = Find<ListBox>(history, "TermActivatedScreens");
            await Until(() => PlacementRow()?.Placement?.Status == "AWAITING_PAIRING", "activation history reads latest placement");
            original.SelectedItem = PlacementRow();
            var statusRefresh = Find<Button>(history, "TermRefresh");
            await Signal("paired");
            Click(statusRefresh);
            await Until(() => PlacementRow()?.Placement?.Status == "AWAITING_REPORT", "paired only, awaiting report");
            await Capture(history, output, "placement-06-awaiting-report");
            await Signal("reported");
            Click(statusRefresh);
            await Until(() => PlacementRow()?.Placement?.Status == "REPORTED", "new device has its own report");
            await Capture(history, output, "placement-07-first-report");
            Click(Find<Button>(history, "TermBack"));
            await Until(() => Find<Button>(history, "TermLatestActivation").IsEffectivelyVisible &&
                Find<Button>(history, "TermBack").IsEffectivelyEnabled, "back to the directory");
            Click(Find<Button>(history, "TermBack"));
            await Until(() => !history.IsVisible, "closing term directory returns to the workspace");
            await ReadOverview(hasRetained: false);
            await Capture(main, output, "placement-08-overview");
            Finish("PASS: both placement entry points, two administrators, wide and compact search, correct target, own readback, per-screen states and exact navigation.", 0);

            TermActivatedScreen? PlacementRow() => original.Items.Cast<TermActivatedScreen>()
                .SingleOrDefault(row => row.BindingId == access.BindingId);
        }
        catch (Exception error)
        {
            try { if (desktop.Windows.LastOrDefault(window => window.IsVisible) is { } window) await Capture(window, output, "placement-failure"); }
            finally { Finish(error.ToString(), 1); }
        }

        void Logout() => Click(main.GetVisualDescendants().OfType<Button>()
            .Single(button => button.IsEffectivelyVisible && button.Content?.ToString() == "退出登录"));

        async Task Login(string username)
        {
            var server = Find<TextBox>(main, "ServerAddress");
            var submit = Find<Button>(main, "LoginButton");
            await Until(() => server.IsEffectivelyVisible && submit.IsEffectivelyEnabled, "login form ready after sign-out");
            server.Text = access.Origin;
            Find<TextBox>(main, "SchoolCode").Text = access.SchoolCode;
            Find<TextBox>(main, "Username").Text = username;
            Find<TextBox>(main, "Password").Text = access.Password;
            Click(submit);
            var schools = Find<ComboBox>(main, "SchoolPicker");
            await Until(() => schools.IsEffectivelyVisible && schools.Items.Cast<School>().Any(row => row.Id == access.SchoolId),
                "login and authorized school");
            schools.SelectedItem = schools.Items.Cast<School>().Single(row => row.Id == access.SchoolId);
        }

        async Task<TermPreparationView> OpenHistoryAsync()
        {
            Click(Find<Button>(main, "OpenClassWorkspaceButton"));
            var entry = Find<Button>(main, "OpenTermPreparationButton");
            await Until(() => entry.IsEffectivelyVisible && entry.IsEffectivelyEnabled, "term directory action");
            Click(entry);
            var page = await AcademicPageAsync<TermPreparationView>(main);
            var latest = Find<Button>(page, "TermLatestActivation");
            await Until(() => latest.IsEffectivelyVisible && latest.IsEffectivelyEnabled, "server supplies own historical receipt");
            Click(latest);
            return page;
        }

        async Task<ListBox> ReadOverview(bool hasRetained)
        {
            Click(Find<Button>(main, "OpenSchoolOverviewButton"));
            var overview = Find<SchoolOverviewView>(main, "SchoolOverviewWorkspace");
            await ExpandTaskQueue(overview);
            var tasks = overview.FindControl<ListBox>("OverviewTasksList")!;
            await Until(() => hasRetained ? tasks.Items.Cast<OverviewTaskItem>().Any(row =>
                row.Task.ScreenId == access.BindingId && row.Task.Action == "OPEN_TERM_SCREEN_PLACEMENT") :
                tasks.Items.Cast<OverviewTaskItem>().Any(row => row.Task.ScreenId == access.RearId),
                "school overview reads fresh tasks");
            if (!hasRetained) Check(tasks.Items.Cast<OverviewTaskItem>().All(row =>
                row.Task.ScreenId != access.BindingId && row.Task.ScreenId != access.FrontId ||
                row.Task.Code is not ("TERM_SCREEN_RETAINED_ORIGINAL" or "SCREEN_AWAITING_PAIRING" or "SCREEN_REPAIR_REQUIRED" or
                    "SCREEN_AWAITING_REPORT" or "SCREEN_NEEDS_VERIFICATION")),
                "only the unrelated rear screen retains a native pairing task");
            return tasks;
        }

        async Task<TermScreenPlacementWindow> PlacementWindowAsync()
        {
            await Until(() => desktop.Windows.OfType<TermScreenPlacementWindow>().Any(row => row.IsVisible), "shared placement form");
            return desktop.Windows.OfType<TermScreenPlacementWindow>().Single(row => row.IsVisible);
        }

        async Task CheckOriginal(TermScreenPlacementWindow window) =>
            await Until(() => TermText(window, "PlacementBindingId").Contains(access.BindingId) &&
                TermText(window, "PlacementScope").Contains("八班无匹配屏") &&
                TermText(window, "PlacementOriginal").Contains("秋季学期"), "fixed original and exact binding evidence");

        async Task PickClass(TermScreenPlacementWindow window, string query)
        {
            var search = Find<TextBox>(window, "PlacementSearch");
            var rows = Find<ListBox>(window, "PlacementCandidates");
            await Until(() => rows.IsEffectivelyVisible && rows.ItemCount >= 2, "all actual eligible classes");
            search.Focus();
            search.Text = query;
            await Until(() => rows.Items.Cast<TermScreenCandidate>().Count(row => row.Code.StartsWith(query)) == 2, "similar class names kept distinct");
            search.RaiseEvent(new KeyEventArgs {RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = search});
            await Until(() => rows.IsKeyboardFocusWithin && rows.SelectedItem is TermScreenCandidate candidate &&
                candidate.Id == access.TargetId, "Enter moves to the exact first matching code");
            var selected = (TermScreenCandidate)rows.SelectedItem!;
            Check(selected.Screens.Select(row => row.Id).ToHashSet().SetEquals([access.FrontId, access.RearId]) &&
                TermText(window, "PlacementOtherScreens").Contains("七班前屏") &&
                TermText(window, "PlacementOtherScreens").Contains("七班后屏") &&
                TermText(window, "PlacementChosen").Contains("G2-7"), "both existing screens and exact target are visible");
        }

        async Task Signal(string step)
        {
            using var response = await control.PostAsync("/__placement/" + step, null);
            Check(response.IsSuccessStatusCode, "isolated checkpoint: " + step);
        }

        void Finish(string message, int code)
        {
            try { File.WriteAllText(Path.Combine(output, reopen ? "placement-reopen-result.txt" : "placement-desktop-result.txt"), message); }
            finally { System.Console.WriteLine(message); desktop.Shutdown(code); }
        }
    }
}
