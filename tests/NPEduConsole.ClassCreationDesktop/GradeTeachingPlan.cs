using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private sealed record GradePlanAccess(string Origin, string SchoolId, string SchoolCode, string Password,
        string GradeId, string SevenId, string EightId, string NineId, string SubjectId, string TeacherId, string A1Id,
        string ConsoleHistoryGroupId, bool AllowGradePlanTestWrites);

    private static int StartGradeTeachingPlan(string path, string output)
    {
        var access = JsonSerializer.Deserialize<GradePlanAccess>(File.ReadAllText(path), JsonSerializerOptions.Web) ??
            throw new InvalidDataException("Expected an isolated grade-plan fixture.");
        Check(access.AllowGradePlanTestWrites && IsLoopback(access.Origin), "Teaching plan test writes require an isolated loopback fixture.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyGradeTeachingPlanAsync(access, output); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyGradeTeachingPlanAsync(GradePlanAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        using var checkpoint = new HttpClient { BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(12) };
        GradeTeachingPlanWindow? window = null;
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
            await Until(() => schools.Items.OfType<School>().Any(row => row.Id == access.SchoolId), "authorized school loaded");
            schools.SelectedItem = schools.Items.OfType<School>().Single(row => row.Id == access.SchoolId);
            var classes = Find<Button>(main, "OpenClassWorkspaceButton");
            await Until(() => classes.IsEffectivelyVisible && classes.IsEffectivelyEnabled, "class workspace accessible");
            Click(classes);
            var entry = Find<Button>(main, "OpenGradeTeachingPlanButton");
            await Until(() => entry.IsEffectivelyVisible && entry.IsEffectivelyEnabled, "existing class workspace exposes grade plan");
            Click(entry);
            window = await WindowOf<GradeTeachingPlanWindow>();
            var rows = Find<ListBox>(window, "PlanRows");
            var grade = Find<ComboBox>(window, "PlanGrade");
            var subject = Find<ComboBox>(window, "PlanSubject");
            await Until(() => rows.Items.OfType<GradeTeachingPlanRowView>().Any(row => row.ClassId == access.EightId), "grade comparison loaded");
            Check((grade.SelectedItem as GradePlanLabel)?.Id == access.GradeId, "agreement on exact grade ID");
            subject.SelectedItem = subject.Items.OfType<GradePlanLabel>().Single(row => row.Id == access.SubjectId);
            await Until(() => rows.Items.OfType<GradeTeachingPlanRowView>().Any(row => row.Key == access.EightId + ":" + access.SubjectId),
                "physics loaded and scoped by ID");
            Check(window.ClientSize.Width >= 1100 && window.ClientSize.Height >= 710, "wide desktop window measured");
            rows.SelectedItem = Room(access.EightId);
            Click(Find<Button>(window, "PlanStageRules"));
            var tabs = Find<TabControl>(window, "PlanTabs");
            tabs.SelectedIndex = 1;
            var courses = Find<ComboBox>(window, "PlanExistingGroup");
            courses.SelectedItem = courses.Items.OfType<GradePlanCourse>().Single(row => row.Id == access.A1Id);
            var operation = Find<ComboBox>(window, "PlanGroupOperation");
            operation.SelectedIndex = 1;
            Click(Find<Button>(window, "PlanStageGroup"));
            tabs.SelectedIndex = 2;
            var teacher = Find<ComboBox>(window, "PlanTeacher");
            teacher.SelectedItem = teacher.Items.OfType<GradePlanTeacher>().Single(row => row.Id == access.TeacherId);
            Click(Find<Button>(window, "PlanStageTeacher"));
            tabs.SelectedIndex = 0;
            var preview = Find<Button>(window, "PlanPreview");
            var save = Find<Button>(window, "PlanSave");
            Click(preview);
            await Until(() => save.IsEffectivelyEnabled || Status().Contains("阻断") || Status().Contains("冲突"), "first draft evaluated");
            Check(save.IsEffectivelyEnabled, "three changes may be submitted: " + Status());
            Check(Room(access.SevenId).After.Contains("物理 A2") && !Room(access.SevenId).After.Contains("物理 A1") &&
                  Room(access.EightId).After.Contains("随行政班 · 必修") && Room(access.EightId).After.Contains("八班物理老师") &&
                  Room(access.NineId).After.Contains("沿用当前安排"), "exact three classroom outcomes before save");
            await Capture(window, output, "01-wide-78-9-preview");

            window.Width = 840;
            window.Height = 480;
            await Until(() => window.ClientSize is { Width: <= 845, Height: <= 485 }, "compact window measured");
            await Signal("web-change");
            Click(save);
            await Until(() => Status().Contains("整份零写入") && !save.IsEffectivelyEnabled, "concurrent legacy write blocked entire plan");
            await Until(() => rows.SelectedItem is GradeTeachingPlanRowView {Conflict: true} conflict &&
                rows.ContainerFromItem(conflict) is ListBoxItem {IsFocused: true}, "keyboard focus lands on a changed row");
            await Signal("stale");
            Check(preview.IsEffectivelyEnabled &&
                  Find<ListBox>(window, "PlanChangeRows").Items.OfType<GradeTeachingPlanChangeView>().Any(row => row.Key == access.A1Id) &&
                  Find<TextBlock>(window, "PlanChangeCount").Text?.Contains("任课 1 项") == true &&
                  save.TranslatePoint(new Point(0, save.Bounds.Height), window)?.Y <= window.ClientSize.Height,
                  "compact window preserves the draft and bottom action");
            await Capture(window, output, "02-compact-legacy-concurrency");
            Click(preview);
            await Until(() => save.IsEffectivelyEnabled, "rechecked plan can be saved");
            Click(save);
            await Until(() => Status().Contains("整份方案已保存"), "lost successful response confirmed using the original receipt");
            await Until(() => !Find<Button>(window, "PlanReload").Content!.Equals("核对回执") && Find<Button>(window, "PlanBack").IsEffectivelyEnabled,
                "receipt confirmed and current data rested");
            await Signal("applied");
            // Reopen through the same product entry and observe persisted state by identity.
            Click(Find<Button>(window, "PlanBack"));
            await Until(() => !window.IsVisible && entry.IsEffectivelyEnabled, "normal class workspace regained");
            Click(entry);
            window = await WindowOf<GradeTeachingPlanWindow>();
            var reopenedSubject = Find<ComboBox>(window, "PlanSubject");
            await Until(() => reopenedSubject.Items.OfType<GradePlanLabel>().Any(row => row.Id == access.SubjectId), "reopened catalog loaded");
            reopenedSubject.SelectedItem = reopenedSubject.Items.OfType<GradePlanLabel>().Single(row => row.Id == access.SubjectId);
            rows = Find<ListBox>(window, "PlanRows");
            await Until(() => rows.Items.OfType<GradeTeachingPlanRowView>().Any(row => row.ClassId == access.EightId &&
                row.Current.Contains("八班物理老师")), "reopened exact class has confirmed teaching");
            await Capture(window, output, "03-wide-persisted-arrangement");
            Click(Find<Button>(window, "PlanBack"));
            await Until(() => !window.IsVisible && entry.IsEffectivelyEnabled, "ready to open the exact course group");
            Click(Find<Button>(main, "OpenCourseGroupsButton"));
            var directory = await AcademicPageAsync<CourseGroupsView>(main);
            var courseRows = Find<ListBox>(directory, "CourseGroupRows");
            await Until(() => courseRows.Items.OfType<CourseGroupDirectoryRow>().Any(row => row.Group.Name == "物理 A2"), "walking directory loaded");
            var a2 = courseRows.Items.OfType<CourseGroupDirectoryRow>().Single(row => row.Group.Name == "物理 A2");
            courseRows.SelectedItem = a2;
            Click(Find<Button>(directory, "CourseGradePlan"));
            await Until(() => desktop.Windows.OfType<GradeTeachingPlanWindow>().Any(candidate => candidate.IsVisible), "walking group opens grade plan");
            window = desktop.Windows.OfType<GradeTeachingPlanWindow>().Single(candidate => candidate.IsVisible);
            await Until(() => Find<ComboBox>(window, "PlanExistingGroup").SelectedItem is GradePlanCourse picked && picked.Id == a2.Group.Id,
                "opens exact group ID and scope");
            Check(Find<TabControl>(window, "PlanTabs").SelectedIndex == 1, "group resolves to source editor");
            Click(Find<Button>(window, "PlanBack"));
            var teachingEntry = Find<Button>(directory, "CourseTeaching");
            await Until(() => !window.IsVisible && courseRows.Items.OfType<CourseGroupDirectoryRow>()
                .Any(row => row.Group.Id == access.ConsoleHistoryGroupId), "course directory shows group used by existing teaching flow");
            courseRows.SelectedItem = courseRows.Items.OfType<CourseGroupDirectoryRow>().Single(row => row.Group.Id == access.ConsoleHistoryGroupId);
            Check(teachingEntry.IsEffectivelyEnabled, "existing course assignment editor available");
            Click(teachingEntry);
            var teaching = await AcademicPageAsync<TeachingView>(main);
            var assignments = Find<ComboBox>(teaching, "TeachingExistingPicker");
            await Until(() => assignments.Items.OfType<TeachingAssignment>().Count() == 1, "one current teacher in existing editor");
            assignments.SelectedItem = assignments.Items.OfType<TeachingAssignment>().Single();
            Click(Find<Button>(teaching, "TeachingRemoveButton"));
            Click(Find<Button>(teaching, "TeachingPreviewButton"));
            var applyRemoval = Find<Button>(teaching, "TeachingApplyButton");
            await Until(() => applyRemoval.IsEffectivelyEnabled, "existing teaching removal is confirmed");
            Click(applyRemoval);
            await Until(() => Find<TextBlock>(teaching, "TeachingFeedback").Text?.Contains("已保存") == true &&
                !assignments.Items.OfType<TeachingAssignment>().Any(), "existing editor now lists no teacher");
            await Signal("desktop-assignment-removed");
            await Capture(teaching, output, "04-existing-console-removal-applied");
            Console.WriteLine("PASS: Windows grade plan preview, stale draft and zero writes, receipt readback, persisted arrangement and group route.");
            desktop.Shutdown(0);
            return;

            async Task<T> WindowOf<T>() where T : Window
            {
                await Until(() => desktop.Windows.OfType<T>().Any(candidate => candidate.IsVisible), typeof(T).Name + " opened");
                return desktop.Windows.OfType<T>().Single(candidate => candidate.IsVisible);
            }

            GradeTeachingPlanRowView Room(string id) => rows.Items.OfType<GradeTeachingPlanRowView>().Single(row => row.ClassId == id);
            string Status() => Find<TextBlock>(window!, "PlanNotice").Text ?? "";
        }
        catch (Exception error)
        {
            if (window is { IsVisible: true })
            {
                try { await Capture(window, output, "desktop-failure"); }
                catch (Exception captureError) { Console.Error.WriteLine(captureError.Message); }
            }
            await File.WriteAllTextAsync(Path.Combine(output, "desktop-failure.txt"), error.ToString());
            Console.Error.WriteLine(error);
            desktop.Shutdown(1);
        }

        async Task Signal(string step)
        {
            using var result = await checkpoint.PostAsync("/__plan/" + step, null);
            Check(result.IsSuccessStatusCode, "PostgreSQL and KV checkpoint: " + step);
        }
    }
}
