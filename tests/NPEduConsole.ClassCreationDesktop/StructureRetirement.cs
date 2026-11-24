using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Presenters;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private sealed record StructureAccess(string Origin, string SchoolId, string SchoolCode, string Password, string TermId,
        string GradeId, string SubjectId, string RoomId, string GroupId, string ConflictId, string FirstId, string SecondId,
        string DisabledId, string MixedId, bool AllowStructureTestWrites);

    private static int StartStructureRetirement(string path, string output)
    {
        var access = JsonSerializer.Deserialize<StructureAccess>(File.ReadAllText(path), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Missing isolated structure fixture.");
        Check(access.AllowStructureTestWrites && IsLoopback(access.Origin), "Structure fixture must use its isolated loopback service.");
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => _ = VerifyStructureRetirementAsync(access, output)))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyStructureRetirementAsync(StructureAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        using var control = new HttpClient { BaseAddress = new Uri(access.Origin) };
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
            await Until(() => schools.Items.OfType<School>().Any(row => row.Id == access.SchoolId), "school authenticated");
            schools.SelectedItem = schools.Items.OfType<School>().Single(row => row.Id == access.SchoolId);
            await PressScoped(main, "OpenClassWorkspaceButton");
            await PressScoped(main, "OpenTermPreparationButton");
            var terms = await AcademicPageAsync<TermPreparationView>(main);
            var termList = Find<ListBox>(terms, "PreparedTerms");
            await Until(() => termList.Items.OfType<ManagedAcademicTerm>().Any(row => row.Id == access.TermId), "actual term loaded");
            termList.SelectedItem = termList.Items.OfType<ManagedAcademicTerm>().Single(row => row.Id == access.TermId);
            await PressScoped(terms, "TermManageTeaching");
            var plan = await Planner();
            await PickSubject(plan, access.SubjectId, access.RoomId);
            Find<TabControl>(plan, "PlanTabs").SelectedIndex = 2;
            var teacher = Find<ComboBox>(plan, "PlanTeacher");
            teacher.SelectedItem = teacher.Items.OfType<GradePlanTeacher>().Single(row => row.Id == access.MixedId);
            await PressScoped(plan, "PlanOpenMixedTeaching");
            var mixed = Find<ListBox>(plan, "PlanMixedTeachingTargets");
            var seven = mixed.Items.Cast<object>().Single(row => row.ToString()!.Contains("G2-7"));
            var a1 = mixed.Items.Cast<object>().Single(row => row.ToString()!.Contains("PHY-MIXED"));
            Check(!mixed.Items.Cast<object>().Any(row => row.ToString()!.Contains("G3-7")), "other grade not offered");
            mixed.SelectedItems!.Add(seven);
            mixed.SelectedItems.Add(a1);
            await PressScoped(plan, "PlanStageMixedTeaching");
            await PressScoped(plan, "PlanPreview");
            await Until(() => Find<Button>(plan, "PlanSave").IsEffectivelyEnabled, "mixed preview can be saved");
            await Fit(plan, 1160, 780);
            await CaptureWithFrame(plan, output, "01-wide-mixed-teaching-preview");
            await Fit(plan, 840, 480);
            var review = Find<ListBox>(Find<TermTeachingReviewView>(plan, "PlanScopedReview"), "TermTeachingReviewList");
            await Until(() => review.ItemCount == 2 && review.ContainerFromIndex(1) is ListBoxItem groupRow &&
                review.GetVisualDescendants().OfType<ScrollContentPresenter>().FirstOrDefault() is { } viewport &&
                groupRow.TranslatePoint(new Point(0, groupRow.Bounds.Height), viewport) is { Y: var bottom } &&
                bottom <= viewport.Bounds.Height, "both direct-access rows are wholly visible at 840 × 480");
            await CaptureWithFrame(plan, output, "02-narrow-mixed-teaching-preview");
            await Signal("other-admin-changes-access");
            await PressScoped(plan, "PlanSave");
            await Until(() => Read(plan, "PlanNotice").Contains("整份零写入"), "membership-only change invalidates whole plan");
            await Signal("stale");
            await CaptureWithFrame(plan, output, "03-narrow-stale-membership");
            await PressScoped(plan, "PlanPreview");
            await PressScoped(plan, "PlanSave");
            await Until(() => Read(plan, "PlanNotice").Contains("整份方案已保存"), "lost application read by original number");
            await Signal("receipt");
            await PressScoped(plan, "PlanBack");
            await PressScoped(terms, "TermBack");
            await PressScoped(main, "OpenSchoolOverviewButton");
            var tasks = Find<ListBox>(Find<SchoolOverviewView>(main, "SchoolOverviewWorkspace"), "OverviewTasksList");
            var duplicate = await TaskItem("MULTIPLE_PRIMARY_TEACHERS");
            Check(duplicate.Task.AccountId is null && duplicate.AutomationId.EndsWith("-" + access.SubjectId), "conflict identifies group and subject");
            await OpenTask(duplicate);
            plan = await Planner();
            var assignments = await OriginalAssignments(plan, access.SecondId);
            Check(assignments.Items.OfType<GradePlanAssignment>().Count(row => row.Position == "PRIMARY") == 2, "the correct same-named group opens");
            assignments.SelectedItem = assignments.Items.OfType<GradePlanAssignment>().Single(row => row.AccountId == access.SecondId);
            await Fit(plan, 840, 480);
            await CaptureWithFrame(plan, output, "04-narrow-original-group-conflict");
            await PressScoped(plan, "PlanStageRemoval");
            await PressScoped(plan, "PlanPreview");
            await PressScoped(plan, "PlanSave");
            await Until(() => Read(plan, "PlanNotice").Contains("整份方案已保存"), "exact second primary removed");
            await Signal("resolved-exact-group");
            await PressScoped(plan, "PlanBack");
            await Until(() => !tasks.Items.OfType<OverviewTaskItem>().Any(row => row.AutomationId == duplicate.AutomationId), "overview conflict gone");

            // Leave a different teacher's REMOVE pending in the same real room and subject.
            await PressScoped(main, "OpenClassWorkspaceButton");
            await PressScoped(main, "OpenTermPreparationButton");
            terms = await AcademicPageAsync<TermPreparationView>(main);
            termList = Find<ListBox>(terms, "PreparedTerms");
            await Until(() => termList.Items.OfType<ManagedAcademicTerm>().Any(row => row.Id == access.TermId), "original term still exists");
            termList.SelectedItem = termList.Items.OfType<ManagedAcademicTerm>().Single(row => row.Id == access.TermId);
            await PressScoped(terms, "TermManageTeaching");
            plan = await Planner();
            await PickSubject(plan, access.SubjectId, access.RoomId);
            Find<TabControl>(plan, "PlanTabs").SelectedIndex = 2;
            var target = Find<ComboBox>(plan, "PlanTeachingTarget");
            target.SelectedItem = target.Items.Cast<object>().Single(row => row.ToString()!.Contains("PHY-CONFLICT"));
            assignments = Find<ComboBox>(plan, "PlanTeachingExisting");
            assignments.SelectedItem = assignments.Items.OfType<GradePlanAssignment>().Single(row => row.AccountId == access.FirstId);
            await PressScoped(plan, "PlanStageRemoval");
            await PressScoped(plan, "PlanPreview");
            await Signal("miss-next-write");
            await PressScoped(plan, "PlanSave");
            await Until(() => Read(plan, "PlanNotice").Contains("暂未找到回执"), "other teacher's request remains unresolved");
            await PressScoped(plan, "PlanBack");
            await PressScoped(terms, "TermBack");
            await PressScoped(main, "OpenSchoolOverviewButton");
            var disabled = await TaskItem("DISABLED_TEACHER_ASSIGNED");
            Check(disabled.Task.AccountId == access.DisabledId, "disabled teacher uses original account ID");
            await OpenTask(disabled);
            plan = await Planner();
            assignments = await OriginalAssignments(plan, access.DisabledId);
            Check((assignments.SelectedItem as GradePlanAssignment)?.AccountId == access.DisabledId &&
                Read(plan, "PlanNotice").Contains("另有一份方案待核对") &&
                !Find<Button>(plan, "PlanSave").IsEffectivelyEnabled &&
                Find<ListBox>(plan, "PlanChangeRows").ItemCount == 0,
                "same group and subject do not switch to a different teacher's pending REMOVE");
            await Signal("separate-pending");
            await CaptureWithFrame(plan, output, "05-original-teacher-pending-separate");
            await PressScoped(plan, "PlanBack");
            await Until(() => !plan.IsVisible && main.IsEffectivelyEnabled, "return to cached diagnosis");
            disabled = await TaskItem("DISABLED_TEACHER_ASSIGNED");
            await Signal("other-admin-restores-teacher");
            await OpenTask(disabled);
            plan = await Planner();
            await Until(() => Read(plan, "PlanNotice").Contains("已变化或不再成立"), "stale diagnostic no longer proposes removal");
            Check(Find<ComboBox>(plan, "PlanTeachingExisting").SelectedItem is null &&
                !Find<Button>(plan, "PlanStageRemoval").IsEffectivelyEnabled && !Find<Button>(plan, "PlanSave").IsEffectivelyEnabled,
                "re-enabled teacher keeps healthy assignment under original ID");
            await Fit(plan, 840, 480);
            await CaptureWithFrame(plan, output, "06-narrow-resolved-task-removal-disabled");
            await PressScoped(plan, "PlanBack");
            await Until(() => tasks.Items.Count > 0 && !tasks.Items.OfType<OverviewTaskItem>()
                .Any(row => row.AutomationId == disabled.AutomationId), "overview refresh removes the resolved task");
            await Signal("healthy-assignment-preserved");
            Console.WriteLine("PASS: Windows structure, same-grade mixed preview, whole-plan stale, original receipt, exact and distinct task.");
            desktop.Shutdown(0);
            return;

            async Task<GradeTeachingPlanWindow> Planner()
            {
                await Until(() => desktop.Windows.OfType<GradeTeachingPlanWindow>().Any(window => window.IsVisible), "normal teaching plan window");
                return desktop.Windows.OfType<GradeTeachingPlanWindow>().Single(window => window.IsVisible);
            }
            async Task<OverviewTaskItem> TaskItem(string code)
            {
                OverviewTaskItem? item = null;
                await Until(() => (item = tasks.Items.OfType<OverviewTaskItem>().FirstOrDefault(row => row.Task.Code == code &&
                    row.Task.CourseGroupId == access.ConflictId && row.Task.SubjectId == access.SubjectId)) is not null, code);
                return item!;
            }
            async Task OpenTask(OverviewTaskItem item)
            {
                Button? action = null;
                await Until(() =>
                {
                    var current = tasks.Items.OfType<OverviewTaskItem>().FirstOrDefault(row => row.AutomationId == item.AutomationId);
                    if (current is null || !tasks.IsEffectivelyEnabled) return false;
                    tasks.ScrollIntoView(current);
                    action = tasks.GetVisualDescendants().OfType<Button>().FirstOrDefault(button =>
                        ReferenceEquals(button.Tag, current) && button.IsEffectivelyVisible && button.IsEffectivelyEnabled);
                    return action is not null;
                }, "overview task available");
                Click(action!);
            }
            async Task<ComboBox> OriginalAssignments(GradeTeachingPlanWindow window, string account)
            {
                var picker = Find<ComboBox>(window, "PlanTeachingExisting");
                await Until(() => picker.Items.OfType<GradePlanAssignment>().Any(row => row.AccountId == account) &&
                    Find<TabControl>(window, "PlanTabs").SelectedIndex == 2 && Read(window, "PlanNotice").Contains("核对"), "original teaching assignment loaded");
                return picker;
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (desktop.Windows.LastOrDefault(window => window.IsVisible) is { } window)
                await CaptureWithFrame(window, output, "structure-failure");
            desktop.Shutdown(1);
        }

        async Task Signal(string step)
        {
            using var result = await control.PostAsync("/__structure/" + step, null);
            Check(result.IsSuccessStatusCode, "real KV/PostgreSQL checkpoint " + step);
        }
        static async Task Fit(Window window, int width, int height)
        {
            window.Width = width;
            window.Height = height;
            await Until(() => Math.Abs(window.ClientSize.Width - width) < 5 && Math.Abs(window.ClientSize.Height - height) < 5,
                $"actual Windows client {width}×{height}");
        }
    }
}
