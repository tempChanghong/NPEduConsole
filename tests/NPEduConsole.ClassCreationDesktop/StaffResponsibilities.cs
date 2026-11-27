using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private sealed record StaffAccess(string Origin, string SchoolId, string SchoolCode, string Password, string EightId, string NineId,
        string GradeId, string FirstId, string SecondId, string ThirdId, string FourthId, string FifthId, bool AllowStaffTestWrites);

    private static int StartStaffResponsibilities(string path, string output)
    {
        var access = JsonSerializer.Deserialize<StaffAccess>(File.ReadAllText(path), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Expected a disposable staff responsibility fixture.");
        Check(access.AllowStaffTestWrites && IsLoopback(access.Origin), "Staff test writes must stay on the isolated loopback fixture.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyStaffResponsibilitiesAsync(access, output); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyStaffResponsibilitiesAsync(StaffAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        using var control = new HttpClient { BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(10) };
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
            await Until(() => schools.Items.Cast<School>().Any(item => item.Id == access.SchoolId), "the authorized fixture school");
            schools.SelectedItem = schools.Items.Cast<School>().Single(item => item.Id == access.SchoolId);
            var overview = Find<SchoolOverviewView>(main, "SchoolOverviewWorkspace");
            var tasks = overview.FindControl<ListBox>("OverviewTasksList")!;
            var filter = overview.FindControl<ComboBox>("OverviewTaskFilter")!;
            await ExpandTaskQueue(overview);
            filter.SelectedIndex = 3;
            await Until(() => FindTask("CLASS_WITHOUT_HOMEROOM", access.EightId) is not null &&
                FindTask("CLASS_WITHOUT_HOMEROOM", access.NineId) is not null, "both exact class duties are missing");

            var review = await OpenReview("CLASS_WITHOUT_HOMEROOM", access.EightId);
            var editor = await OpenRoleEditor(review);
            Check(StaffText(editor, "StaffHeading").Contains("高二（8）班") && editor.ClientSize.Width >= 1100,
                "the task opens class 8 in the wide layout");
            var search = Find<TextBox>(editor, "StaffTeacherSearch");
            search.Text = "张老师";
            var picker = Find<ComboBox>(editor, "StaffTeacherPicker");
            await Until(() => picker.ItemCount == 2, "both same-named teacher accounts search correctly");
            var candidates = picker.Items.Cast<StaffTeacher>().ToArray();
            Check(candidates[0].DisplayName == candidates[1].DisplayName && candidates[0].AccountLabel != candidates[1].AccountLabel,
                "the choices show distinct account identities for the same display name");
            await AddTeacher(editor, access.FirstId);
            await Preview(editor);
            await Save(editor, "职责已保存");
            await Signal("check-first");
            Click(Find<Button>(editor, "StaffClose"));
            await CloseReview(review);
            await Until(() => overview.IsEffectivelyVisible &&
                FindTask("CLASS_WITHOUT_HOMEROOM", access.EightId) is null && FindTask("CLASS_WITHOUT_HOMEROOM", access.NineId) is not null &&
                filter.SelectedIndex == 3 && filter.IsFocused, "returns to the filtered, refreshed task list with focus");

            await SelectClass(access.EightId);
            Click(Find<Button>(main, "OpenClassResponsibilityButton"));
            editor = await Editor();
            await AddTeacher(editor, access.SecondId);
            await ChangeRole(editor, access.SecondId, 0);
            Check(!Find<Button>(editor, "StaffPrimaryAction").IsEnabled, "a second primary does not silently replace the first");
            await ChangeRole(editor, access.FirstId, 2);
            await Capture(editor, output, "01-wide-two-same-named-accounts");
            await Preview(editor);
            Check(Find<ItemsControl>(editor, "StaffAdvisories").Items.Cast<string>().Any(line => line.Contains("尚无本年级任课")),
                "the preview displays the school's existing homeroom/teaching diagnostic");
            await Save(editor, "当前职责与本次提交一致");
            await Signal("check-handoff");
            await Capture(editor, output, "02-class-handoff-readback");
            Click(Find<Button>(editor, "StaffClose"));
            await Until(() => !editor.IsVisible, "close the verified class handoff");
            Click(Find<Button>(main, "OpenSchoolOverviewButton"));
            await ExpandTaskQueue(overview);
            filter.SelectedIndex = 3;
            review = await OpenReview("HOMEROOM_NOT_TEACHING", access.EightId, account: access.SecondId);
            Check(Find<Button>(review, "ReviewResolve").Content?.ToString() == "核对任课",
                "teacher-specific missing teaching diagnostic opens the exact teacher and can choose a teaching destination");
            await CloseReview(review);

            main.Width = 840;
            main.Height = 480;
            await Until(() => main.ClientSize is { Width: <= 845, Height: <= 485 }, "compact shell measured");
            review = await OpenReview("MULTIPLE_PRIMARY_GRADE_LEADERS", grade: access.GradeId);
            editor = await OpenRoleEditor(review);
            Check(editor.ClientSize.Width <= 845 && Find<ListBox>(editor, "StaffCurrentRows").Items.Cast<StaffEditorRow>()
                .Count(row => row.Position == "PRIMARY") == 2, "the narrow editor lists both original primary leaders");
            Check(Grid.GetRow(Find<Border>(editor, "StaffDraftCard")) == 1, "the narrow editor stacks its two sections");
            await Capture(editor, output, "03-narrow-multiple-primaries");
            await AddTeacher(editor, access.SecondId);
            await ChangeRole(editor, access.SecondId, 0);
            await ChangeRole(editor, access.ThirdId, 1);
            Check(!Find<Button>(editor, "StaffPrimaryAction").IsEnabled, "each old primary disposition must be explicit");
            await ChangeRole(editor, access.FourthId, 2);
            await Preview(editor);
            await Signal("race-grade");
            Click(Find<Button>(editor, "StaffPrimaryAction"));
            await Until(() => StaffText(editor, "StaffStatus").Contains("本次未写入") &&
                Find<ListBox>(editor, "StaffCurrentRows").Items.Cast<StaffEditorRow>().Any(row => row.Teacher.Id == access.FifthId) &&
                Find<Button>(editor, "StaffPrimaryAction").IsEffectivelyEnabled, "legacy edit is shown beside the retained draft");
            Check(Draft(editor).Any(row => row.Teacher.Id == access.SecondId && row.Position == "PRIMARY"), "the draft preserves the new grade leader");
            await AssertPinnedStaffStatus(editor);
            await Capture(editor, output, "04-narrow-concurrent-grade-change");
            Click(Find<Button>(editor, "StaffPrimaryAction"));
            Check(Draft(editor).Count(row => row.Position == "DEPUTY") == 2 && Draft(editor).Any(row => row.Teacher.Id == access.FifthId),
                "explicit rebase keeps both deputy leaders, including the intervening legacy change");
            await Preview(editor);
            Find<Border>(editor, "StaffDraftCard").BringIntoView();
            await Capture(editor, output, "05-narrow-multiple-deputies");
            await Save(editor, "职责已保存");
            Click(Find<Button>(editor, "StaffClose"));
            await CloseReview(review);

            await SelectClass(access.NineId);
            var gradeEntry = Find<Button>(main, "OpenGradeResponsibilityButton");
            Check(gradeEntry.IsEffectivelyVisible, "class workspace exposes the current grade action");
            Click(gradeEntry);
            editor = await Editor();
            Check(StaffText(editor, "StaffHeading").Contains("高二") && Find<ListBox>(editor, "StaffCurrentRows").Items.Cast<StaffEditorRow>()
                .Any(row => row.Teacher.Id == access.SecondId && row.Position == "PRIMARY"), "the exact grade reopens from the class workspace");
            Click(Find<Button>(editor, "StaffClose"));
            await Until(() => !editor.IsVisible, "leave the reopened grade workflow");
            Click(Find<Button>(main, "OpenClassResponsibilityButton"));
            editor = await Editor();
            await AddTeacher(editor, access.FirstId);
            await Preview(editor);
            await Signal("switch-term");
            Click(Find<Button>(editor, "StaffPrimaryAction"));
            await Until(() => StaffText(editor, "StaffStatus").Contains("启用学期已切换") && StaffText(editor, "StaffCurrentHeading") == "上次读取的安排" &&
                !Find<Button>(editor, "StaffPrimaryAction").IsEffectivelyVisible,
                "current-term commit rejects the old class scope");
            await Capture(editor, output, "06-term-switch");
            Click(Find<Button>(editor, "StaffClose"));
            Finish("PASS: Windows exact class/account and grade tasks, same-name teachers, class handoff with readback, grade concurrency and explicit rebasing, term switch.", 0);

            OverviewTaskItem? FindTask(string code, string? room = null, string? grade = null, string? account = null) =>
                tasks.Items.Cast<OverviewTaskItem>().FirstOrDefault(item => item.Task.Code == code &&
                    (room is null || item.Task.ClassId == room) && (grade is null || item.Task.GradeId == grade) &&
                    (account is null || item.Task.AccountId == account));

            async Task<StaffReviewWindow> OpenReview(string code, string? room = null, string? grade = null, string? account = null)
            {
                Button? action = null;
                await Until(() =>
                {
                    if (FindTask(code, room, grade, account) is not { } item) return false;
                    Check(item.Task.Action == "OPEN_STAFF_REVIEW", "the current task is actionable in Console");
                    action = tasks.GetVisualDescendants().OfType<Button>().FirstOrDefault(button =>
                        ReferenceEquals(button.Tag, item) && button.IsEffectivelyVisible);
                    if (action is not null) return true;
                    tasks.ScrollIntoView(tasks.Items.IndexOf(item));
                    return false;
                }, "actual task action " + code);
                Click(action!);
                await Until(() => desktop.Windows.OfType<StaffReviewWindow>().Any(window => window.IsVisible), "opens staff review");
                var window = desktop.Windows.OfType<StaffReviewWindow>().Single(window => window.IsVisible);
                var item = FindTask(code, room, grade, account)!.Task;
                string key = StaffReviewIssue.IssueKey(code, item.GradeId, item.ClassId, item.AccountId);
                await Until(() => Find<StaffReviewList>(window, "ReviewIssues").SelectedKey == key &&
                    Find<Button>(window, "ReviewResolve").IsEffectivelyVisible, "staff review selects the exact issue");
                return window;
            }

            async Task<StaffResponsibilitiesWindow> OpenRoleEditor(StaffReviewWindow window)
            {
                await PressScoped(window, "ReviewResolve");
                return await Editor();
            }

            async Task CloseReview(StaffReviewWindow window)
            {
                await Until(() => desktop.Windows.OfType<StaffResponsibilitiesWindow>().All(candidate => !candidate.IsVisible) &&
                    Find<Button>(window, "ReviewRules").IsEffectivelyEnabled, "returns from the editor to staff review");
                await PressScoped(window, "ReviewClose");
                await Until(() => !window.IsVisible, "returns from staff review to school overview");
            }
        }
        catch (Exception error)
        {
            try { if (desktop.Windows.LastOrDefault(window => window.IsVisible) is { } visible) await Capture(visible, output, "failure"); }
            finally { Finish(error.ToString(), 1); }
        }

        async Task SelectClass(string id)
        {
            Click(Find<Button>(main, "OpenClassWorkspaceButton"));
            var list = Find<ListBox>(main, "ClassPicker");
            await Until(() => list.IsEffectivelyVisible && list.Items.Cast<Classroom>().Any(room => room.Id == id), "selected school has the exact class");
            list.SelectedItem = list.Items.Cast<Classroom>().Single(room => room.Id == id);
            await Until(() => Find<Button>(main, "OpenClassResponsibilityButton").IsEffectivelyVisible, "the class detail exposes its duty action");
        }

        async Task<StaffResponsibilitiesWindow> Editor()
        {
            await Until(() => desktop.Windows.OfType<StaffResponsibilitiesWindow>().Any(window => window.IsVisible), "staff editor opens");
            var editor = desktop.Windows.OfType<StaffResponsibilitiesWindow>().Single(window => window.IsVisible);
            await Until(() => Find<TextBox>(editor, "StaffTeacherSearch").IsEffectivelyEnabled, "staff role snapshot loaded");
            return editor;
        }

        async Task Signal(string action)
        {
            using var response = await control.PostAsync("/__staff/" + action, null);
            Check(response.IsSuccessStatusCode, "fixture control " + action);
        }

        void Finish(string message, int code)
        {
            try { File.WriteAllText(Path.Combine(output, "result.txt"), message); }
            finally { System.Console.WriteLine(message); desktop.Shutdown(code); }
        }
    }

    private static string StaffText(Control window, string name) => Find<TextBlock>(window, name).Text ?? "";
    private static IEnumerable<StaffEditorRow> Draft(Window window) => Find<ListBox>(window, "StaffDraftRows").Items.Cast<StaffEditorRow>();

    private static async Task AddTeacher(StaffResponsibilitiesWindow editor, string accountId)
    {
        var picker = Find<ComboBox>(editor, "StaffTeacherPicker");
        Find<TextBox>(editor, "StaffTeacherSearch").Clear();
        await Until(() => picker.Items.Cast<StaffTeacher>().Any(teacher => teacher.Id == accountId), "the exact teacher is a candidate");
        picker.SelectedItem = picker.Items.Cast<StaffTeacher>().Single(teacher => teacher.Id == accountId);
        Click(Find<Button>(editor, "StaffAddTeacher"));
        await Until(() => Draft(editor).Any(row => row.Teacher.Id == accountId), "add the exact teacher to the draft");
    }

    private static async Task ChangeRole(StaffResponsibilitiesWindow editor, string accountId, int selection)
    {
        var list = Find<ListBox>(editor, "StaffDraftRows");
        var row = Draft(editor).Single(item => item.Teacher.Id == accountId);
        list.ScrollIntoView(row);
        ComboBox? picker = null;
        await Until(() => list.ContainerFromItem(row) is Control control &&
            (picker = control.GetVisualDescendants().OfType<ComboBox>().SingleOrDefault()) is { IsEffectivelyVisible: true }, "role selector for " + accountId);
        picker!.SelectedIndex = selection;
        await Until(() => row.Selection == selection, "role change propagates from the selector");
    }

    private static async Task Preview(Window editor)
    {
        var action = Find<Button>(editor, "StaffPrimaryAction");
        Check(action.Content is "预览安排" && action.IsEffectivelyEnabled, "the explicit proposal is valid");
        Click(action);
        await Until(() => action.Content is "确认保存" && action.IsEffectivelyEnabled, "the server preview is ready");
    }

    private static async Task Save(Window editor, string message)
    {
        Click(Find<Button>(editor, "StaffPrimaryAction"));
        await Until(() => StaffText(editor, "StaffStatus").Contains(message) && Find<Button>(editor, "StaffClose").IsEffectivelyEnabled,
            "the server result is visible");
    }

    private static async Task AssertPinnedStaffStatus(Window editor)
    {
        var body = Find<ScrollViewer>(editor, "StaffBody");
        var status = Find<Border>(editor, "StaffStatusBox");
        var footerAction = Find<Button>(editor, "StaffPrimaryAction");
        var top = status.TranslatePoint(default, editor);
        var footer = footerAction.TranslatePoint(default, editor);
        var end = body.Extent.Height - body.Viewport.Height;
        Check(end > 40 && FitsBatchWindow(editor, status) && FitsBatchWindow(editor, footerAction),
            "conflict status and main action are fully visible at the narrow size");
        body.Offset = new Vector(0, body.Offset.Y > end / 2 ? 0 : end);
        await Dispatcher.UIThread.InvokeAsync(editor.UpdateLayout, DispatcherPriority.Loaded);
        Check(status.TranslatePoint(default, editor) == top && footerAction.TranslatePoint(default, editor) == footer,
            "scrolling the comparison keeps the conflict conclusion and main action fixed");
        body.Offset = default;
    }
}
