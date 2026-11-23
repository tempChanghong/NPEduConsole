using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private sealed record ActivationAccess(string Origin, string SchoolId, string SchoolCode, string Password,
        string CurrentName, string DraftName, string DraftId, string FirstId, string SecondId, string UnmatchedId, bool AllowActivation);

    private static int StartTermActivation(string path, string output)
    {
        var access = JsonSerializer.Deserialize<ActivationAccess>(File.ReadAllText(path), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Expected an isolated term activation fixture.");
        Check(access.AllowActivation && IsLoopback(access.Origin), "Activation writes require the isolated fixture.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyTermActivationAsync(access, output); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyTermActivationAsync(ActivationAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        using var control = new HttpClient {BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(30)};
        try
        {
            main.Width = 1160;
            main.Height = 780;
            Find<TextBox>(main, "ServerAddress").Text = access.Origin;
            Find<TextBox>(main, "SchoolCode").Text = access.SchoolCode;
            Find<TextBox>(main, "Username").Text = "admin";
            Find<TextBox>(main, "Password").Text = access.Password;
            Click(Find<Button>(main, "LoginButton"));
            var schools = Find<ComboBox>(main, "SchoolPicker");
            await Until(() => schools.Items.Cast<School>().Any(row => row.Id == access.SchoolId), "authorized school");
            schools.SelectedItem = schools.Items.Cast<School>().Single(row => row.Id == access.SchoolId);
            var entry = Find<Button>(main, "OpenTermPreparationButton");
            Click(Find<Button>(main, "OpenClassWorkspaceButton"));
            await Until(() => entry.IsEffectivelyVisible && entry.IsEffectivelyEnabled, "visible term management entry");
            Click(entry);
            var page = await WindowAsync();
            var drafts = Find<ListBox>(page, "PreparedTerms");
            await Until(() => drafts.Items.Cast<ManagedAcademicTerm>().Any(row => row.Id == access.DraftId), "prepare existing term");
            drafts.SelectedItem = drafts.Items.Cast<ManagedAcademicTerm>().Single(row => row.Id == access.DraftId);
            Click(Find<Button>(page, "TermExistingButton"));
            var primary = Find<Button>(page, "TermPrimary");
            var refresh = Find<Button>(page, "TermRefresh");
            var checkRows = Find<ListBox>(page, "TermScreenRows");
            await Until(() => primary.IsEffectivelyVisible && primary.IsEffectivelyEnabled && checkRows.ItemCount == 3, "ready check with three screens");
            Check(checkRows.Items.Cast<TermDraftScreenImpact>().Count(row => row.NativeImpact == "REPAIR_REQUIRED") == 2 &&
                checkRows.Items.Cast<TermDraftScreenImpact>().Single(row => row.BindingId == access.UnmatchedId).ArchiveInvalidatesNative,
                "two exact re-pair rows and archival invalidation on the retained screen");
            string Heading() => TermText(page, "TermActivationHeadline");
            Check(Heading().Contains(access.CurrentName) && Heading().Contains(access.DraftName), "both names visible");
            await Capture(page, output, "01-wide-activation-check");
            Click(primary);
            var cancel = Find<Button>(page, "TermConfirmCancel");
            await Until(() => cancel.IsEffectivelyVisible && cancel.IsFocused, "confirmation initially focuses cancel");
            Check(primary.Content?.ToString() == "确认启用" && Heading().Contains(access.DraftName), "one explicit confirmation");
            await Capture(page, output, "02-wide-confirmation");
            await Signal("change-binding");
            Click(primary);
            await Until(() => TermText(page, "TermNotice").Contains("本次未启用"), "stale binding refused");
            var details = Find<ItemsControl>(page, "TermActivationChanges");
            Check(details.Items.Cast<string>().Any(line => line.Contains(access.SecondId) && line.Contains("已改显示名")),
                "only the changed binding is named");
            await Signal("confirm-no-write");
            await Capture(page, output, "03-stale-exact-binding");

            main.Width = 840;
            main.Height = 480;
            await Until(() => main.ClientSize is {Width: <= 845, Height: <= 485}, "compact live Windows window");
            Click(primary);
            await Until(() => cancel.IsFocused, "compact default focus stays on cancel");
            Check(primary.TranslatePoint(new Point(0, primary.Bounds.Height), page) is { } bottom &&
                bottom.Y <= page.Bounds.Height && Heading().Contains("3 台"), "compact confirmation retains main action and count");
            await Capture(page, output, "04-compact-confirmation");
            Click(primary);
            await Until(() => TermText(page, "TermNotice").Contains("启用回执待核对") && refresh.IsEffectivelyEnabled, "lost POST and failing first GET preserve pending request");
            Check(!primary.IsEffectivelyVisible, "no retry after a failed receipt read");
            Click(refresh);
            var outcomes = Find<ListBox>(page, "TermActivatedScreens");
            await Until(() => outcomes.ItemCount == 3 && outcomes.IsEffectivelyVisible, "read original receipt");
            Check(TermText(page, "TermActivatedName").Contains(access.DraftName) &&
                TermText(page, "TermArchivedName").Contains(access.CurrentName) &&
                TermText(page, "TermActivatedSummary").Contains("待处理 3 台"), "confirmed draft, archive, and pending count");
            AssertStatus(access.FirstId, "REPAIR_REQUIRED");
            AssertStatus(access.SecondId, "REPAIR_REQUIRED");
            AssertStatus(access.UnmatchedId, "RETAINED_ORIGINAL");
            await Capture(page, output, "05-compact-result");
            main.Width = 1160;
            main.Height = 780;
            await Until(() => main.ClientSize.Width >= 1100, "wide result");
            await Capture(page, output, "06-wide-result");

            await Signal("before-repair");
            await Signal("paired");
            Click(refresh);
            await Until(() => Row(access.FirstId).Status == "AWAITING_REPORT", "first screen still needs a valid report");
            AssertStatus(access.SecondId, "REPAIR_REQUIRED");
            await Capture(page, output, "07-paired-waiting-report");
            await Signal("reported");
            Click(refresh);
            await Until(() => Row(access.FirstId).Status == "REPORTED" && TermText(page, "TermActivatedSummary").Contains("待处理 2 台"),
                "only the first screen is resolved by its own report");
            AssertStatus(access.SecondId, "REPAIR_REQUIRED");
            AssertStatus(access.UnmatchedId, "RETAINED_ORIGINAL");
            await Capture(page, output, "08-first-screen-reported");

            outcomes.SelectedItem = Row(access.SecondId);
            Check(primary.Content?.ToString() == "打开大屏接入", "retained second screen opens existing workflow");
            Click(primary);
            await Until(() => !page.IsVisible && Find<Border>(main, "ScreenEvidenceCard").IsEffectivelyVisible, "return to exact device detail");
            await Until(() => Find<Border>(main, "ClassAdmissionCard").IsEffectivelyVisible &&
                TermText(main, "AdmissionScope").Contains("七班后屏（已改显示名）"), "result opens admission for the exact affected rear screen");
            await Capture(main, output, "09-exact-rear-screen");

            Click(Find<Button>(main, "OpenSchoolOverviewButton"));
            var overview = Find<SchoolOverviewView>(main, "SchoolOverviewWorkspace");
            await ExpandTaskQueue(overview);
            var tasks = overview.FindControl<ListBox>("OverviewTasksList")!;
            await Until(() => tasks.Items.Cast<OverviewTaskItem>().Any(row => row.Task.ScreenId == access.SecondId &&
                row.Task.Code == "SCREEN_REPAIR_REQUIRED"), "new-term dashboard reloaded");
            Check(tasks.Items.Cast<OverviewTaskItem>().Any(row => row.Task.ScreenId == access.UnmatchedId &&
                row.Task.Code == "TERM_SCREEN_RETAINED_ORIGINAL") &&
                tasks.Items.Cast<OverviewTaskItem>().Where(row => row.Task.ScreenId == access.FirstId)
                    .All(row => row.Task.Code == "SCREEN_WEB_ACCOUNT_REQUIRED"),
                "the confirmed native screen has no native task; its unrelated missing web account may still be reported");
            await Capture(main, output, "10-school-overview");
            Click(Find<Button>(main, "OpenClassWorkspaceButton"));
            await Until(() => entry.IsEffectivelyVisible && entry.IsEffectivelyEnabled, "return to term management");
            Click(entry);
            page = await WindowAsync();
            var latest = Find<Button>(page, "TermLatestActivation");
            await Until(() => latest.IsEffectivelyVisible && latest.IsEffectivelyEnabled, "latest own receipt available on reopen");
            Click(latest);
            await Until(() => TermText(page, "TermActivatedName").Contains(access.DraftName), "historical result read as receipt");
            Finish("PASS: actual Windows 1160×780 and 840×480, explicit confirmation and safe focus, stale binding, lost result, per-screen updates, guided navigation and overview refresh.", 0);

            TermActivatedScreen Row(string id) => outcomes.Items.Cast<TermActivatedScreen>().Single(row => row.BindingId == id);
            void AssertStatus(string id, string status) => Check(Row(id).Status == status, "screen " + id + " should be " + status);
        }
        catch (Exception error)
        {
            try { if (desktop.Windows.LastOrDefault(window => window.IsVisible) is { } window) await Capture(window, output, "failure"); }
            finally { Finish(error.ToString(), 1); }
        }

        async Task<TermPreparationView> WindowAsync()
        {
            return await AcademicPageAsync<TermPreparationView>(main);
        }

        async Task Signal(string step)
        {
            using var response = await control.PostAsync("/__activation/" + step, null);
            Check(response.IsSuccessStatusCode, "isolated checkpoint: " + step);
        }

        void Finish(string result, int code)
        {
            try { File.WriteAllText(Path.Combine(output, "desktop-result.txt"), result); }
            finally { System.Console.WriteLine(result); desktop.Shutdown(code); }
        }
    }
}
