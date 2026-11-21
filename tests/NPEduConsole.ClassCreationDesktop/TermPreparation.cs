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
    private sealed record TermAccess(string Origin, string SchoolId, string OtherId, string SchoolCode, string Password,
        string TermName, string FirstId, string SecondId, string UnmatchedId, bool AllowTermWrites);

    private static int StartTermPreparation(string path, string output)
    {
        var access = JsonSerializer.Deserialize<TermAccess>(File.ReadAllText(path), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Expected a disposable term preparation fixture.");
        Check(access.AllowTermWrites && IsLoopback(access.Origin), "Term preparation writes require the isolated test fixture.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyTermPreparationAsync(access, output); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyTermPreparationAsync(TermAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        using var control = new HttpClient {BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(12)};
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
            await Until(() => schools.Items.Cast<School>().Any(row => row.Id == access.OtherId), "both authorized schools loaded");
            SelectSchool(access.OtherId);
            var overview = Find<SchoolOverviewView>(main, "SchoolOverviewWorkspace");
            await ExpandTaskQueue(overview);
            var tasks = overview.FindControl<ListBox>("OverviewTasksList")!;
            Button? taskButton = null;
            await Until(() =>
            {
                var item = tasks.Items.Cast<OverviewTaskItem>().SingleOrDefault(row => row.Task.Code == "NO_ACTIVE_TERM");
                if (item is null) return false;
                Check(item.Task.Action == "OPEN_TERM_PREPARATION", "no-term task routes to desktop management");
                taskButton = tasks.GetVisualDescendants().OfType<Button>().FirstOrDefault(row => row.IsEffectivelyVisible && ReferenceEquals(row.Tag, item));
                if (taskButton is null) tasks.ScrollIntoView(item);
                return taskButton is not null;
            }, "the actual no-term overview action");
            Click(taskButton!);
            var page = await WindowAsync();
            await Until(() => TermText(page, "ActiveTermName").Contains("暂无") && Find<Button>(page, "TermWeb").IsEffectivelyVisible &&
                Find<Button>(page, "TermWeb").Content?.ToString() == "打开初始化入口", "empty school explains source and links initialization");
            Check(!Find<Button>(page, "TermPrimary").IsEffectivelyVisible, "no draft-from-source action without an active term");
            await Capture(page, output, "01-no-source");
            Click(Find<Button>(page, "TermBack"));
            await Until(() => !page.IsVisible, "empty school management closes");

            SelectSchool(access.SchoolId);
            Click(Find<Button>(main, "OpenClassWorkspaceButton"));
            var entry = Find<Button>(main, "OpenTermPreparationButton");
            await Until(() => entry.IsEffectivelyVisible && entry.IsEffectivelyEnabled, "term management visible in the class workspace");
            Click(entry);
            page = await WindowAsync();
            var primary = Find<Button>(page, "TermPrimary");
            await Until(() => primary.IsEffectivelyVisible && primary.IsEffectivelyEnabled &&
                TermText(page, "TermScope").Contains(access.TermName), "reads the server actual active term");
            Check(page.ClientSize.Width >= 1100 && Grid.GetColumn(Find<Border>(page, "DraftTermsCard")) == 1, "wide directory is parallel");
            await Capture(page, output, "02-wide-active-and-drafts");
            Click(primary);
            Find<TextBox>(page, "TermDraftYear").Text = "2027";
            Find<TextBox>(page, "TermDraftName").Text = "2027 春季准备";
            Find<TextBox>(page, "TermStartDate").Text = "2027-02-15";
            Find<TextBox>(page, "TermEndDate").Text = "2027-07-15";
            Find<ComboBox>(page, "TermDraftSemester").SelectedIndex = 0;
            var preview = Find<Button>(page, "TermPreviewButton");
            Check(!primary.IsEffectivelyEnabled && primary.Content?.ToString() == "建立草稿" &&
                Find<CheckBox>(page, "CarryTermInvites").IsChecked != true, "create requires explicit preview and excludes pending OAuth invites");
            Click(preview);
            await Until(() => TermText(page, "TermPreviewSummary").Contains("2 条任课") && primary.IsEffectivelyEnabled, "default preview includes active teaching");
            Check(TermText(page, "TermPreviewSummary").Contains("2 个走班教学班") &&
                TermText(page, "TermPreviewSummary").Contains("2 条走班来源"), "inheritance shows both independent walking sources");
            var details = Find<ComboBox>(page, "TermPreviewItems");
            foreach (string group in new[] {"物理 A1", "物理 A2"})
            {
                details.SelectedItem = details.Items.Cast<object>().Single(row => row.ToString()!.StartsWith(group));
                Check(TermText(page, "TermPreviewDetails").Contains("高二（7）班"), group + " points to the exact existing source in preview");
            }
            Find<CheckBox>(page, "CarryTermTeaching").IsChecked = false;
            Check(TermText(page, "TermAccessNote").Contains("仍可能赋予教师访问"), "independent membership inheritance is explained");
            Click(preview);
            await Until(() => TermText(page, "TermPreviewSummary").Contains("0 条任课") &&
                TermText(page, "TermPreviewSummary").Contains("2 条成员权限") && primary.IsEffectivelyEnabled, "opt-out preview matches effective copied memberships and assignments");
            await Capture(page, output, "03-wide-members-without-teaching");
            Find<CheckBox>(page, "CarryTermTeaching").IsChecked = true;
            Click(preview);
            await Until(() => TermText(page, "TermPreviewSummary").Contains("2 条任课") && primary.IsEffectivelyEnabled, "reselect active teaching and preview");
            await Signal("change-source");
            Click(primary);
            await Until(() => TermText(page, "TermNotice").Contains("没有建立草稿") &&
                TermText(page, "TermPreviewSummary").Contains("3 条任课") && preview.IsEffectivelyEnabled, "legacy racing change affects exact preview and blocks commit");
            Check(!primary.IsEffectivelyEnabled && Find<TextBox>(page, "TermDraftName").Text == "2027 春季准备" &&
                Find<TextBox>(page, "TermStartDate").Text == "2027-02-15", "stale preview keeps original input and requires another review");
            await Signal("no-draft");
            await Capture(page, output, "04-stale-preview-no-write");
            Click(preview);
            await Until(() => primary.IsEffectivelyEnabled, "updated preview explicitly read");
            Click(primary);
            await Until(() => TermText(page, "TermNotice").Contains("核对没有完成") && Find<Button>(page, "TermRefresh").IsEffectivelyEnabled,
                "a failed read keeps the original receipt pending");
            Check(!primary.IsEffectivelyVisible && !Find<Button>(page, "TermDiscard").IsEffectivelyVisible,
                "unknown receipt does not unlock an unverified replay or discard");
            Click(Find<Button>(page, "TermRefresh"));
            var screenRows = Find<ListBox>(page, "TermScreenRows");
            await Until(() => screenRows.ItemCount == 3 && TermText(page, "TermCheckTarget").Contains("2027 春季准备") &&
                TermText(page, "TermNotice").Contains("按本次回执"), "lost success response recovers the actual new draft and shows its check");
            Check(TermText(page, "TermScope").Contains(access.TermName) && !TermText(page, "TermScope").Contains("2027 春季准备") &&
                TermText(page, "TermCheckReceiptCounts").Contains("3 条任课"), "active term stays visible separately from the draft and its receipt");
            var rows = screenRows.Items.Cast<TermDraftScreenImpact>().ToArray();
            Check(rows.Count(row => row.NativeImpact == "REPAIR_REQUIRED" && row.TargetClass?.Code == "G2-7") == 2 &&
                rows.Where(row => row.BindingId == access.FirstId || row.BindingId == access.SecondId).Select(row => row.BindingId).Distinct().Count() == 2,
                "both bindings remain separate with explicit re-pairing impact");
            await Signal("verify-draft");
            var search = Find<TextBox>(page, "TermScreenSearch");
            search.BringIntoView();
            await Capture(page, output, "05-wide-two-bindings-and-receipt");

            page.Width = 840;
            page.Height = 480;
            await Until(() => page.ClientSize is {Width: <= 845, Height: <= 485}, "actual narrow dialog measured");
            await Signal("change-draft");
            var refresh = Find<Button>(page, "TermRefresh");
            Click(refresh);
            await Until(() => screenRows.Items.Cast<TermDraftScreenImpact>().Any(row =>
                row.BindingId == access.UnmatchedId && row.NativeImpact == "NO_REBIND" && row.TargetClass is null),
                "refresh reflects the old web edit and the unmatched target");
            Check(Find<StackPanel>(page, "TermBlockerItems").GetVisualDescendants().OfType<TextBlock>()
                .Any(label => label.Text?.Contains("没有走班教学班覆盖") == true), "a new real teaching issue appears as an activation blocker");
            search.Text = access.SecondId;
            await Until(() => screenRows.ItemCount == 1 && screenRows.Items.Cast<TermDraftScreenImpact>().Single().BindingId == access.SecondId,
                "the exact second binding is searchable, even with duplicate screen names");
            search.Focus();
            search.RaiseEvent(new KeyEventArgs {RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = search});
            await Until(() => screenRows.SelectedItem is TermDraftScreenImpact selected && selected.BindingId == access.SecondId,
                "Enter selects the exact binding");
            screenRows.BringIntoView();
            await Capture(page, output, "06-compact-exact-binding");
            search.Text = access.UnmatchedId;
            await Until(() => screenRows.ItemCount == 1 && screenRows.Items.Cast<TermDraftScreenImpact>().Single().NativeImpact == "NO_REBIND",
                "unmatched row filtered by ID");
            screenRows.BringIntoView();
            await Capture(page, output, "07-compact-unmatched-binding");
            Click(Find<Button>(page, "TermBack"));
            var drafts = Find<ListBox>(page, "PreparedTerms");
            await Until(() => drafts.IsEffectivelyVisible && drafts.Items.Cast<ManagedAcademicTerm>().Any(row => row.Name == "2027 春季准备") &&
                Grid.GetRow(Find<Border>(page, "DraftTermsCard")) == 1, "narrow directory stacks the current term and actual draft");
            await Capture(page, output, "08-compact-current-and-draft");
            var draft = drafts.Items.Cast<ManagedAcademicTerm>().Single(row => row.Name == "2027 春季准备");
            Click(Find<Button>(page, "TermBack"));
            await Until(() => !page.IsVisible, "term management closes");

            main.Width = 840;
            main.Height = 480;
            await Until(() => entry.IsEffectivelyVisible && entry.IsEffectivelyEnabled, "term management entry returns after narrow resize");
            Click(entry);
            page = await WindowAsync();
            drafts = Find<ListBox>(page, "PreparedTerms");
            await Until(() => drafts.Items.Cast<ManagedAcademicTerm>().Any(row => row.Id == draft.Id), "reopened term management shows original draft ID");
            drafts.SelectedItem = drafts.Items.Cast<ManagedAcademicTerm>().Single(row => row.Id == draft.Id);
            Click(Find<Button>(page, "TermExistingButton"));
            await Until(() => TermText(page, "TermCheckTarget").Contains(draft.Name) &&
                TermText(page, "TermScope").Contains(access.TermName), "an existing draft reopens independently while actual active term stays visible");
            await Capture(page, output, "09-real-activation-blocker");
            Finish("PASS: Windows 1160×780 / 840×480, no-source task, option-aware preview, stale zero-write, lost-reply receipt, original active term, exact double binding, refreshed missing target and original draft reopen.", 0);

            void SelectSchool(string id) => schools.SelectedItem = schools.Items.Cast<School>().Single(row => row.Id == id);
        }
        catch (Exception error)
        {
            try { if (desktop.Windows.LastOrDefault(window => window.IsVisible) is { } window) await Capture(window, output, "failure"); }
            finally { Finish(error.ToString(), 1); }
        }

        async Task<TermPreparationWindow> WindowAsync()
        {
            await Until(() => desktop.Windows.OfType<TermPreparationWindow>().Any(row => row.IsVisible), "term management opens");
            return desktop.Windows.OfType<TermPreparationWindow>().Single(row => row.IsVisible);
        }

        async Task Signal(string step)
        {
            using var response = await control.PostAsync("/__term/" + step, null);
            Check(response.IsSuccessStatusCode, "isolated checkpoint: " + step);
        }

        void Finish(string result, int code)
        {
            try { File.WriteAllText(Path.Combine(output, "result.txt"), result); }
            finally { System.Console.WriteLine(result); desktop.Shutdown(code); }
        }
    }

    private static string TermText(Window window, string control) => Find<TextBlock>(window, control).Text ?? "";
}
