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
    private sealed record TeachingAccess(string Origin, string SchoolId, string SchoolCode, string Password,
        string SevenId, string EightId, string NineId, string SubjectId, string TeacherId, bool AllowTeachingTestWrites,
        string HistorySubjectId, string[] HistoryPeerIds, string OtherManageableSchoolId);

    private static int StartTeaching(string path, string output)
    {
        var access = JsonSerializer.Deserialize<TeachingAccess>(File.ReadAllText(path), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Missing teaching fixture.");
        Check(access.AllowTeachingTestWrites && IsLoopback(access.Origin), "Use the isolated teaching fixture.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyTeachingAsync(access, output); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task ExpandTaskQueue(SchoolOverviewView overview)
    {
        var tasks = overview.FindControl<ListBox>("OverviewTasksList")!;
        await Until(() => tasks.IsEffectivelyVisible, "all tasks are visible");
    }

    private static string ChosenAccount(TeachingView view) => Find<TextBlock>(view, "TeachingChosenTeacherAccount").Text ?? "";

    private static async Task VerifyTeachingAsync(TeachingAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        var overview = Find<SchoolOverviewView>(main, "SchoolOverviewWorkspace");
        var schools = Find<ComboBox>(main, "SchoolPicker");
        var tasks = overview.FindControl<ListBox>("OverviewTasksList")!;
        var rooms = Find<ListBox>(main, "ClassPicker");
        try
        {
            VerifyMixedTeachingResults();
            main.Width = 1160;
            main.Height = 780;
            Find<TextBox>(main, "ServerAddress").Text = access.Origin;
            Find<TextBox>(main, "SchoolCode").Text = access.SchoolCode;
            Find<TextBox>(main, "Username").Text = "admin";
            Find<TextBox>(main, "Password").Text = access.Password;
            Click(Find<Button>(main, "LoginButton"));
            await Until(() => schools.Items.Cast<School>().Any(school => school.Id == access.SchoolId), "current school");
            schools.SelectedItem = schools.Items.Cast<School>().Single(school => school.Id == access.SchoolId);
            await ExpandTaskQueue(overview);
            await Until(() => Missing(access.SevenId) && Missing(access.EightId), "7 and 8 both have a math task");
            Check(!Missing(access.NineId), "the existing primary leaves 9 without a missing-assignment task");
            nint hwnd = main.TryGetPlatformHandle()!.Handle;
            var filter = Find<ComboBox>(overview, "OverviewTaskFilter");
            filter.SelectedIndex = 1;
            System.Console.WriteLine("Windows: two missing math tasks resolved by ID.");
            await CaptureWithFrame(main, output, "01-missing-sections");

            var button = await TaskAction(access.SevenId, access.SubjectId);
            string taskId = AutomationProperties.GetAutomationId(button)!;
            Click(button);
            var dialog = await Editor();
            var subject = Find<ComboBox>(dialog, "TeachingSubjectPicker");
            Check(subject.SelectedItem is TeachingSubject item && item.Id == access.SubjectId &&
                !subject.IsEnabled && Find<TextBlock>(dialog, "TeachingContext").Text?.Contains("（7）") == true,
                "overview locks the real classroom and subject");
            await PickTeacher(dialog, "陈老师", checkDuplicate: true);
            await CaptureWithFrame(main, output, "01b-wide-overview-teaching");
            Click(Find<Button>(dialog, "CloseTeachingButton"));
            Click(Find<Button>(dialog, "TeachingKeepDraft"));
            await Until(() => filter.SelectedIndex == 1 && tasks.GetVisualDescendants().OfType<Button>().Any(row =>
                row.IsFocused && AutomationProperties.GetAutomationId(row) == taskId), "the same overview task and filter regain focus");

            main.Width = 840;
            main.Height = 480;
            await Until(() => main.ClientSize is { Width: <= 845, Height: <= 485 }, "actual 840 by 480 window");
            await OpenClass(access.SevenId);
            dialog = await Editor();
            subject = Find<ComboBox>(dialog, "TeachingSubjectPicker");
            Check(subject.IsEnabled && subject.SelectedItem is null, "class detail leaves the choice of subjects to the administrator");
            subject.SelectedItem = subject.Items.Cast<TeachingSubject>().Single(item => item.Id == access.SubjectId);
            Check(ChosenAccount(dialog) == "@mathteacher", "overview draft is visible from the same classroom and subject");
            await CaptureWithFrame(main, output, "01c-narrow-class-restored-draft");
            using var fixture = new HttpClient { BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(10) };
            subject.SelectedItem = subject.Items.Cast<TeachingSubject>().Single(item => item.Id == access.HistorySubjectId);
            Check(ChosenAccount(dialog) == "", "the math teacher selection does not leak into history");
            await PickTeacher(dialog, "陈老师", checkDuplicate: true);
            foreach (string id in access.HistoryPeerIds) Peer(dialog, id).IsChecked = true;
            Click(Find<Button>(dialog, "TeachingPreviewButton"));
            var list = Find<ListBox>(dialog, "TeachingPreviewList");
            await Until(() => list.ItemCount == 24 && Find<Button>(dialog, "TeachingApplyButton").IsEnabled,
                "24 actual history classes in the narrow preview");
            await AssertTeachingPreviewAsync(dialog, [list.Items[0]!, list.Items[^1]!]);
            await Capture(dialog, output, "00-long-preview-compact");
            using (var race = await fixture.PostAsync("/__teaching/change-history", null))
                Check(race.IsSuccessStatusCode, "the second administrator changes the last selected history class");
            Click(Find<Button>(dialog, "TeachingApplyButton"));
            await Until(() => list.Items.Cast<TeachingPreviewRow>().Any(row => row.Row.ClassId == access.HistoryPeerIds[^1] &&
                row.Before == "暂无任课" && row.After.Contains("王老师 · 协同")), "stale preview compares the exact original and latest assignments");
            await AssertTeachingClassFocusAsync(dialog, list.Items.Cast<TeachingPreviewRow>().Single(), access.HistoryPeerIds[^1]);
            Check(!Find<Button>(dialog, "TeachingApplyButton").IsEnabled &&
                Peer(dialog, access.HistoryPeerIds[0]).IsChecked == true, "stale submission leaves the draft intact and requires a new preview");
            await CaptureWithFrame(main, output, "00-concurrent-change-compact");
            var conflictLink = list.GetVisualDescendants().OfType<Button>().Single(action => action.IsEffectivelyVisible &&
                AutomationProperties.GetAutomationId(action) == "LocateTeachingPeer-" + access.HistoryPeerIds[^1]);
            Click(conflictLink);
            await Until(() => Peer(dialog, access.HistoryPeerIds[^1]).IsFocused, "conflict action returns to the exact classroom checkbox");
            Check(Peer(dialog, access.HistoryPeerIds[^1]).IsChecked == true, "locating the classroom does not edit the draft");
            await CaptureWithFrame(main, output, "00b-conflict-exact-class-checkbox");

            main.Width = 1160;
            main.Height = 780;
            subject.SelectedItem = subject.Items.Cast<TeachingSubject>().Single(item => item.Id == access.SubjectId);
            Check(ChosenAccount(dialog) == "@mathteacher", "the original math draft survives switching subjects");
            Click(Find<Button>(dialog, "CloseTeachingButton"));
            Click(Find<Button>(dialog, "TeachingKeepDraft"));
            await Until(() => !dialog.IsVisible && Find<Button>(main, "OpenTeachingButton").IsFocused, "class detail restores the assignment button");
            Click(Find<Button>(main, "OpenSchoolOverviewButton"));
            await Until(() => Missing(access.SevenId) && tasks.IsEffectivelyVisible, "math task returns with a fresh snapshot");
            Click(await TaskAction(access.SevenId, access.SubjectId));
            dialog = await Editor();
            await Until(() => ChosenAccount(dialog) == "@mathteacher", "the same classroom draft is restored from the exact overview task");
            list = Find<ListBox>(dialog, "TeachingPreviewList");
            Peer(dialog, access.EightId).IsChecked = true;
            Peer(dialog, access.NineId).IsChecked = true;
            Click(Find<Button>(dialog, "TeachingPreviewButton"));
            await Until(() => Find<Button>(dialog, "TeachingPreviewButton").IsEnabled, "finished first preview");
            System.Console.WriteLine(Feedback(dialog) + " " + string.Join(" / ", RowText(dialog)));
            await Until(() => RowText(dialog).Any(row => row.Contains("（9）") && row.Contains("已有其他主讲")), "preview identifies the exact primary conflict");
            Check(!Find<Button>(dialog, "TeachingApplyButton").IsEnabled && Peer(dialog, access.EightId).IsChecked == true, "batch stays uncommittable and retains selections");
            await AssertTeachingClassFocusAsync(dialog,
                list.Items.Cast<TeachingPreviewRow>().Single(row => row.Row.ClassId == access.NineId), access.NineId);
            await Capture(dialog, output, "02-primary-conflict");

            var role = Find<ComboBox>(dialog, "TeachingPositionPicker");
            role.SelectedIndex = 1;
            Click(Find<Button>(dialog, "TeachingPreviewButton"));
            await Until(() => list.Items.Cast<TeachingPreviewRow>().Any(row => row.Row.ClassId == access.NineId &&
                row.After.Contains("王老师 · 主讲") && row.After.Contains("陈老师（mathteacher） · 协同")),
                "co-teaching preview retains the existing primary and shows the new co-teacher");
            await Capture(dialog, output, "02-co-teacher-preview");
            role.SelectedIndex = 0;

            Peer(dialog, access.NineId).IsChecked = false;
            Click(Find<Button>(dialog, "TeachingPreviewButton"));
            await Until(() => Find<Button>(dialog, "TeachingApplyButton").IsEnabled, "7 and 8 can be submitted atomically");
            Check(list.Items.Cast<TeachingPreviewRow>().Count(row => row.Row.State == "ADD" && row.After.Contains("主讲")) == 2, "preview contains both proposed changes");
            await Capture(dialog, output, "03-batch-preview");
            Click(Find<Button>(dialog, "TeachingApplyButton"));
            await Until(() => Feedback(dialog).Contains("提交回执仍未确认") && Find<Button>(dialog, "TeachingReadButton").IsEnabled,
                "save reply and first read fail without resubmitting");
            Check(!Find<Button>(dialog, "TeachingApplyButton").IsEffectivelyVisible, "only readback is available");
            await Capture(dialog, output, "04-waiting-readback");
            Click(Find<Button>(dialog, "CloseTeachingButton"));
            await Until(() => !dialog.IsVisible && filter.IsFocused && !Missing(access.SevenId) && !Missing(access.EightId) &&
                tasks.Items.OfType<OverviewTaskItem>().Any(item => item.Task.ClassId == access.SevenId && item.Task.SubjectId == access.HistorySubjectId),
                "return rereads resolved math tasks and restores filter focus while the history task remains");
            Click(await TaskAction(access.SevenId, access.HistorySubjectId));
            dialog = await Editor();
            subject = Find<ComboBox>(dialog, "TeachingSubjectPicker");
            Check((subject.SelectedItem as TeachingSubject)?.Id == access.HistorySubjectId && !subject.IsEnabled &&
                Equals(Find<Button>(dialog, "TeachingReadButton").Content, "刷新安排"), "pending math recovery cannot take over the exact history task");
            Check(ChosenAccount(dialog) == "@mathteacher" && Peer(dialog, access.HistoryPeerIds[^1]).IsChecked == true,
                "history restores its own teacher and classrooms across entrances");
            Click(Find<Button>(dialog, "CloseTeachingButton"));
            Click(Find<Button>(dialog, "TeachingKeepDraft"));
            await Until(() => !dialog.IsVisible && overview.IsEffectivelyVisible, "history draft can return independently");

            await OpenClass(access.SevenId);
            dialog = await Editor();
            var readback = Find<Button>(dialog, "TeachingReadButton");
            await Until(() => readback.IsFocused && Equals(readback.Content, "核对提交结果") &&
                Find<ListBox>(dialog, "TeachingPreviewList").ItemCount == 2 &&
                (Find<ComboBox>(dialog, "TeachingSubjectPicker").SelectedItem as TeachingSubject)?.Id == access.SubjectId,
                "same scope restores the original math pending rows and readback focus");
            Click(readback);
            await Until(() => Feedback(dialog).Contains("已核对，服务器当前安排与本次提交一致"), "read-only comparison reconciles the committed assignment");
            await Capture(dialog, output, "04-receipt-reconciled");
            Click(Find<Button>(dialog, "CloseTeachingButton"));
            Click(Find<Button>(main, "OpenSchoolOverviewButton"));
            await Until(() => overview.IsEffectivelyVisible && tasks.ItemCount > 0 && !Missing(access.SevenId) && !Missing(access.EightId), "return reloads both resolved tasks");
            await Capture(main, output, "05-overview-after-save");

            await OpenClass(access.EightId);
            dialog = await Editor();
            var existing = Find<ComboBox>(dialog, "TeachingExistingPicker");
            existing.SelectedItem = existing.Items.Cast<TeachingAssignment>().Single(row => row.AccountId == access.TeacherId);
            Click(Find<Button>(dialog, "TeachingRemoveButton"));
            Check(Find<TextBlock>(dialog, "TeachingAccessNote").Text?.Contains("班级访问权限会保留") == true, "removal preserves class access in the UI");
            Click(Find<Button>(dialog, "TeachingPreviewButton"));
            await Until(() => Find<Button>(dialog, "TeachingApplyButton").IsEnabled, "preview the single removal");
            Check(RowText(dialog).Single().Contains("（8）"), "removal is scoped to the original 8th class");
            await Capture(dialog, output, "06-remove-only-eight");
            Click(Find<Button>(dialog, "TeachingApplyButton"));
            await Until(() => Feedback(dialog).Contains("已保存 1 个班"), "8th class removal saved");
            Click(Find<Button>(dialog, "CloseTeachingButton"));
            Click(Find<Button>(main, "OpenSchoolOverviewButton"));
            await Until(() => Missing(access.EightId), "8th class missing-math task returns");
            Check(!Missing(access.SevenId) && !Missing(access.NineId), "the other two classes stay resolved");
            await Capture(main, output, "07-only-eight-needs-teacher");
            main.Width = 840;
            main.Height = 480;
            await Until(() => main.ClientSize is { Width: <= 845, Height: <= 485 }, "narrow overview geometry");
            await CaptureWithFrame(main, output, "07a-narrow-overview-only-eight");
            Click(await TaskAction(access.EightId, access.SubjectId));
            dialog = await Editor();
            var narrowSubject = Find<ComboBox>(dialog, "TeachingSubjectPicker");
            Check((narrowSubject.SelectedItem as TeachingSubject)?.Id == access.SubjectId && !narrowSubject.IsEnabled &&
                Find<TextBlock>(dialog, "TeachingContext").Text?.Contains("（8）") == true, "narrow overview opens the original eighth classroom and math");
            await CaptureWithFrame(main, output, "07b-narrow-overview-teaching");
            Click(Find<Button>(dialog, "CloseTeachingButton"));
            await Until(() => overview.IsEffectivelyVisible && tasks.GetVisualDescendants().OfType<Button>().Any(action =>
                action.IsFocused && action.Tag is OverviewTaskItem row && IsMathTask(row, access.EightId)), "narrow return focuses the original task");
            using (var hold = await fixture.PostAsync("/__teaching/hold-class-read", null)) Check(hold.IsSuccessStatusCode, "arm delayed original-school read");
            await OpenClass(access.SevenId);
            using (var waiting = await fixture.PostAsync("/__teaching/await-class-read", null)) Check(waiting.IsSuccessStatusCode, "real old-school catalog is held");
            schools.SelectedItem = schools.Items.Cast<School>().Single(row => row.Id == access.OtherManageableSchoolId);
            using (var release = await fixture.PostAsync("/__teaching/release-class-read", null)) Check(release.IsSuccessStatusCode, "release delayed old-school catalog");
            await Until(() => overview.IsEffectivelyVisible && !main.GetVisualDescendants().OfType<TeachingView>().Any(view => view.IsEffectivelyVisible) &&
                (schools.SelectedItem as School)?.Id == access.OtherManageableSchoolId, "late class catalog cannot return to a former school");
            Finish($"PASS: Windows HWND 0x{hwnd:X}, both teaching entrances, width modes, same-name choice, scope and focus, stale conflict, lost reply, and school isolation.", 0);

            async Task OpenClass(string classId)
            {
                Click(Find<Button>(main, "OpenClassWorkspaceButton"));
                await Until(() => rooms.IsEffectivelyVisible && rooms.Items.Cast<Classroom>().Any(row => row.Id == classId), "class directory has the requested classroom");
                rooms.SelectedItem = rooms.Items.Cast<Classroom>().Single(row => row.Id == classId);
                var action = Find<Button>(main, "OpenTeachingButton");
                await Until(() => action.IsEffectivelyVisible && action.IsEffectivelyEnabled, "class detail exposes teaching");
                Click(action);
            }

            async Task<TeachingView> Editor()
            {
                var view = await AcademicPageAsync<TeachingView>(main);
                await Until(() => Find<ComboBox>(view, "TeachingSubjectPicker").ItemCount > 0, "teaching catalog loaded");
                Check(ReferenceEquals(TopLevel.GetTopLevel(view), main) && main.TryGetPlatformHandle()?.Handle == hwnd &&
                    desktop.Windows.Count(window => window.IsVisible) == 1, "both routes stay on the original HWND");
                return view;
            }
        }
        catch (Exception error)
        {
            System.Console.Error.WriteLine(error);
            try
            {
                var visible = desktop.Windows.OfType<TeachingWindow>().FirstOrDefault(window => window.IsVisible);
                if (visible is not null) await Capture(visible, output, "failure-dialog");
                else if (main.IsVisible) await Capture(main, output, "failure");
            }
            catch (Exception captureError) { System.Console.Error.WriteLine(captureError.Message); }
            Finish(error.ToString(), 1);
        }

        bool IsMathTask(OverviewTaskItem item, string id) => item.Task.Action == "OPEN_TEACHING" &&
            item.Task.ClassId == id && item.Task.SubjectId == access.SubjectId;
        bool Missing(string id) => tasks.Items.Cast<OverviewTaskItem>().Any(item => IsMathTask(item, id));
        static CheckBox Peer(TeachingView dialog, string id) => Find<StackPanel>(dialog, "TeachingClassChoices").Children.OfType<CheckBox>()
            .Single(row => AutomationProperties.GetAutomationId(row) == "TeachingClass-" + id);
        static string Feedback(TeachingView dialog) => Find<TextBlock>(dialog, "TeachingFeedback").Text ?? "";
        static IEnumerable<string> RowText(TeachingView dialog) => Find<ListBox>(dialog, "TeachingPreviewList").Items.Cast<TeachingPreviewRow>()
            .Select(row => $"{row.ClassName} · {row.Status} · {row.Before} → {row.After}");
        async Task<Button> TaskAction(string classId, string subjectId)
        {
            var task = tasks.Items.OfType<OverviewTaskItem>().Single(item => item.Task.Action == "OPEN_TEACHING" &&
                item.Task.ClassId == classId && item.Task.SubjectId == subjectId);
            tasks.ScrollIntoView(task);
            Button? action = null;
            await Until(() => (action = tasks.GetVisualDescendants().OfType<Button>().FirstOrDefault(row =>
                row.IsEffectivelyVisible && ReferenceEquals(row.Tag, task))) is not null, "the exact class and subject task is realized");
            return action!;
        }
        async Task PickTeacher(TeachingView view, string query, bool checkDuplicate)
        {
            var entry = Find<Button>(view, "OpenTeachingTeacherChoice");
            Click(entry);
            var search = Find<TextBox>(view, "TeachingTeacherSearch");
            await Until(() => search.IsFocused, "teacher search gets entry focus");
            search.Text = query;
            var candidates = Find<ListBox>(view, "TeachingTeacherCandidates");
            await Until(() => candidates.Items.OfType<TeachingTeacherCandidate>().Any(row => row.Teacher.Id == access.TeacherId) &&
                (!checkDuplicate || candidates.Items.OfType<TeachingTeacherCandidate>()
                    .Where(row => row.Teacher.Name == query).Select(row => row.Detail).Order()
                    .SequenceEqual(["@duplicateteacher", "@mathteacher"]) && candidates.ItemCount == 2),
                "the search displays both same-name teachers with their distinct short accounts");
            candidates.SelectedItem = candidates.Items.OfType<TeachingTeacherCandidate>().Single(row => row.Teacher.Id == access.TeacherId);
            await CaptureWithFrame(main, output, main.Width < 900 ? "01d-narrow-same-name-teachers" : "01a-wide-same-name-teachers");
            candidates.Focus();
            candidates.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await Until(() => entry.IsFocused && ChosenAccount(view) == "@mathteacher", "keyboard selects and displays the exact identity");
        }
        void Finish(string message, int code)
        {
            try { File.WriteAllText(Path.Combine(output, "result.txt"), message); }
            finally
            {
                System.Console.WriteLine(message);
                desktop.Shutdown(code);
            }
        }
    }

    private static void VerifyMixedTeachingResults()
    {
        var draft = new TeachingDraft("term", "subject", "ASSIGN", ["seven", "eight"], "account", "PRIMARY", null);
        var original = new TeachingPending(draft, new TeachingPreview([
            new("seven", "7 班", "ADD", "", "", []),
            new("eight", "8 班", "ADD", "", "", [])
        ]));
        var matched = new TeachingRow("seven", "7 班", "UNCHANGED", "相同安排", "",
            [new("relation", "account", "陈老师", "PRIMARY")]);
        var blocked = new TeachingRow("eight", "8 班", "OTHER_PRIMARY", "已有主讲", "",
            [new("other-relation", "other", "王老师", "PRIMARY")]);
        foreach (var mode in new[] { TeachingObservation.Blocked, TeachingObservation.Readback })
        {
            var rows = TeachingPreviewRow.Compare([matched, blocked], original, mode);
            Check(rows is [{ NeedsAttention: false }, { NeedsAttention: true }] &&
                rows.First(row => row.NeedsAttention).Row.ClassId == "eight",
                $"mixed {mode} highlights and targets only the affected class");
        }
        var stale = TeachingPreviewRow.Compare([matched], original, TeachingObservation.Stale);
        Check(stale is [{ NeedsAttention: true }], "a returned stale row needs review even when the new arrangement matches");
    }
}
