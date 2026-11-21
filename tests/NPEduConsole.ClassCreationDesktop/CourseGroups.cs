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
    private sealed record CourseAccess(string Origin, string SchoolId, string SchoolCode, string Password, string AlternateSchoolId,
        string SevenId, string EightId, string NineId, string TenId, string SubjectId, string TeacherId, string A2Id, bool AllowCourseTestWrites);

    private static int StartCourseGroups(string path, string output)
    {
        var access = JsonSerializer.Deserialize<CourseAccess>(File.ReadAllText(path), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Expected an isolated current course-group fixture.");
        Check(access.AllowCourseTestWrites && IsLoopback(access.Origin), "Walking test writes require an isolated loopback fixture.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyCourseGroupsAsync(access, output); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyCourseGroupsAsync(CourseAccess access, string output)
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
            await Until(() => schools.Items.Cast<School>().Any(row => row.Id == access.SchoolId), "authorized school loaded");
            schools.SelectedItem = schools.Items.Cast<School>().Single(row => row.Id == access.SchoolId);
            var overview = Find<SchoolOverviewView>(main, "SchoolOverviewWorkspace");
            await ExpandTaskQueue(overview);
            var filter = overview.FindControl<ComboBox>("OverviewTaskFilter")!;
            filter.SelectedIndex = 1;
            var tasks = overview.FindControl<ListBox>("OverviewTasksList")!;
            var scope = await OpenTask("WALKING_SUBJECT_WITHOUT_GROUP", access.EightId, false);
            var editor = Find<ScrollViewer>(scope, "CourseEditorScroll");
            var primary = Find<Button>(scope, "CoursePrimary");
            await Until(() => primary.IsEffectivelyEnabled && editor.IsEffectivelyVisible && Source(scope, access.EightId).IsChecked == true,
                "8th class task opens exact source and subject, preselected");
            Check(scope.ClientSize.Width >= 1100 && ((CourseGroupLabel)Find<ComboBox>(scope, "CourseSubject").SelectedItem!).Id == access.SubjectId,
                "wide creation shows the exact subject");
            Find<TextBox>(scope, "CourseName").Text = "物理 A1";
            Find<TextBox>(scope, "CourseCode").Text = "PHY-A1";
            Source(scope, access.NineId).IsChecked = true;
            Click(primary);
            await Until(() => CourseText(scope, "CourseNotice").Contains("高二（9）班") &&
                CourseText(scope, "CourseNotice").Contains("已有行政班任课"), "blocked class is named visibly");
            var conflict = Find<StackPanel>(scope, "CoursePreviewRows").GetVisualDescendants().OfType<Button>()
                .Single(button => AutomationProperties.GetAutomationId(button) == "CourseConflictTeaching-" + access.NineId);
            Check(primary.Content?.ToString() == "重新预览", "blocked preview offers no confirm");
            await Signal("blocked");
            await Capture(scope, output, "01-wide-fixed-teacher-conflict");
            Click(conflict);
            var teaching = await WindowOf<TeachingWindow>();
            await Until(() => CourseText(teaching, "TeachingContext").Contains("高二（9）班") &&
                Find<ComboBox>(teaching, "TeachingExistingPicker").Items.Cast<TeachingAssignment>()
                    .Any(row => row.TeacherName == "原任课教师"), "exact scheduled 9th class opened separately");
            Click(Find<Button>(teaching, "CloseTeachingButton"));
            await Until(() => !teaching.IsVisible && primary.IsEffectivelyEnabled, "returns to same walking draft");
            Check(Find<TextBox>(scope, "CourseName").Text == "物理 A1" && Find<TextBox>(scope, "CourseCode").Text == "PHY-A1" &&
                Source(scope, access.EightId).IsChecked == true && Source(scope, access.NineId).IsChecked == true, "all fields and picks survived");
            Source(scope, access.NineId).IsChecked = false;
            Source(scope, access.SevenId).IsChecked = true;
            Click(primary);
            await Until(() => primary.Content?.ToString() == "确认保存" && primary.IsEffectivelyEnabled, "7/8 preview ready");
            Check(Find<StackPanel>(scope, "CoursePreviewRows").GetVisualDescendants().OfType<TextBlock>().Any(row => row.Text?.Contains("物理 A2") == true),
                "first source explicitly keeps its other teaching group");
            await Capture(scope, output, "02-wide-source-in-two-teaching-groups");
            Click(primary);
            var list = Find<ListBox>(scope, "CourseGroupRows");
            await Until(() => list.SelectedItem is CourseGroupDirectoryRow item && item.Group.Code == "PHY-A1" &&
                CourseText(scope, "CourseNotice").Contains("已确认"), "lost success response verified by original receipt");
            string groupId = ((CourseGroupDirectoryRow)list.SelectedItem!).Group.Id;
            Check(groupId != access.A2Id && list.Items.Cast<CourseGroupDirectoryRow>().Count(row => row.Group.Code == "PHY-A1") == 1,
                "server-confirmed group selected and no clone made");
            await Signal("created");
            await Capture(scope, output, "03-wide-saved-and-ready-to-assign");
            Click(Find<Button>(scope, "CourseBack"));
            await Until(() => !scope.IsVisible && FindTask("COURSE_GROUP_WITHOUT_TEACHER", groupId, true) is not null &&
                FindTask("WALKING_SUBJECT_WITHOUT_GROUP", access.EightId, false) is null, "only the A1 teacher task remains relevant");

            scope = await OpenTask("COURSE_GROUP_WITHOUT_TEACHER", groupId, true);
            teaching = await WindowOf<TeachingWindow>();
            var teacherPicker = Find<ComboBox>(teaching, "TeachingTeacherPicker");
            await Until(() => CourseText(teaching, "TeachingContext").Contains("物理 A1") &&
                teacherPicker.IsEffectivelyEnabled && teacherPicker.Items.Cast<TeachingTeacher>().Any(teacher => teacher.Id == access.TeacherId),
                "group task opens exact A1 with valid candidates");
            Check(!Find<StackPanel>(teaching, "TeachingPeers").IsEffectivelyVisible &&
                !Find<ComboBox>(teaching, "TeachingSubjectPicker").IsEnabled, "assignment is fixed to one teaching group and subject");
            teacherPicker.SelectedItem = teacherPicker.Items.Cast<TeachingTeacher>().Single(row => row.Id == access.TeacherId);
            Click(Find<Button>(teaching, "TeachingPreviewButton"));
            var apply = Find<Button>(teaching, "TeachingApplyButton");
            await Until(() => apply.IsEffectivelyEnabled, "single group teacher assignment preview");
            Click(apply);
            await Until(() => CourseText(teaching, "TeachingFeedback").Contains("已保存"), "group assignment separately saved");
            await Signal("assigned");
            Click(Find<Button>(teaching, "CloseTeachingButton"));
            await Until(() => !teaching.IsVisible && Find<Button>(scope, "CourseBack").IsEffectivelyEnabled, "return to group directory");
            Click(Find<Button>(scope, "CourseBack"));
            await Until(() => !scope.IsVisible && FindTask("COURSE_GROUP_WITHOUT_TEACHER", groupId, true) is null, "the exact group task clears");
            Check(filter.SelectedIndex == 1, "return keeps the teaching task filter");

            main.Width = 840;
            main.Height = 480;
            await Until(() => main.ClientSize is {Width: <= 845, Height: <= 485}, "compact parent window measured");
            Click(Find<Button>(main, "OpenClassWorkspaceButton"));
            var entry = Find<Button>(main, "OpenCourseGroupsButton");
            await Until(() => entry.IsEffectivelyVisible && entry.IsEffectivelyEnabled, "existing class workspace exposes walking directory");
            Click(entry);
            scope = await WindowOf<CourseGroupsWindow>();
            list = Find<ListBox>(scope, "CourseGroupRows");
            await Until(() => list.Items.Cast<CourseGroupDirectoryRow>().Any(row => row.Group.Id == groupId), "reopened directory contains original A1 ID");
            var directorySubject = Find<ComboBox>(scope, "CourseSubjectFilter");
            directorySubject.SelectedItem = directorySubject.Items.Cast<CourseGroupLabel>().Single(row => row.Id == access.SubjectId);
            list.SelectedItem = list.Items.Cast<CourseGroupDirectoryRow>().Single(row => row.Group.Id == groupId);
            Click(Find<Button>(scope, "CourseEdit"));
            primary = Find<Button>(scope, "CoursePrimary");
            await Until(() => Find<ScrollViewer>(scope, "CourseEditorScroll").IsEffectivelyVisible && primary.IsEffectivelyEnabled,
                "existing group opens in compact full-page editor");
            Check(scope.ClientSize.Width <= 845 && scope.ClientSize.Height <= 485 &&
                Grid.GetRow(Find<Border>(scope, "CourseReviewCard")) == 1, "compact editor stacks sources and review");
            Check(Source(scope, access.SevenId).IsChecked == true && Source(scope, access.EightId).IsChecked == true,
                "both original sources are retained");
            Source(scope, access.NineId).IsChecked = true;
            Click(primary);
            var narrowEditor = Find<ScrollViewer>(scope, "CourseEditorScroll");
            Button? narrowConflict = null;
            await Until(() =>
            {
                narrowConflict = Find<StackPanel>(scope, "CoursePreviewRows").GetVisualDescendants().OfType<Button>()
                    .SingleOrDefault(button => AutomationProperties.GetAutomationId(button) == "CourseConflictTeaching-" + access.NineId);
                return CourseText(scope, "CourseNotice").Contains("高二（9）班") && narrowConflict is {IsFocused: true} &&
                    narrowConflict.TranslatePoint(new Point(0, 0), narrowEditor)?.Y >= 0 &&
                    narrowConflict.TranslatePoint(new Point(0, narrowConflict.Bounds.Height), narrowEditor)?.Y <= narrowEditor.Bounds.Height;
            }, "compact conflict action is fully in view and keyboard focused");
            await Capture(scope, output, "04-narrow-existing-group-conflict");
            narrowConflict!.RaiseEvent(new KeyEventArgs {RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = narrowConflict});
            narrowConflict.RaiseEvent(new KeyEventArgs {RoutedEvent = InputElement.KeyUpEvent, Key = Key.Enter, Source = narrowConflict});
            teaching = await WindowOf<TeachingWindow>();
            await Until(() => CourseText(teaching, "TeachingContext").Contains("高二（9）班"), "keyboard opens the same precise admin class assignment");
            Click(Find<Button>(teaching, "CloseTeachingButton"));
            await Until(() => !teaching.IsVisible && primary.IsEffectivelyEnabled, "compact conflict return preserves the original draft");
            Check(Source(scope, access.NineId).IsChecked == true && Find<TextBox>(scope, "CourseCode").Text == "PHY-A1",
                "compact conflict handling keeps fields and original source selection");
            Source(scope, access.NineId).IsChecked = false;
            Source(scope, access.TenId).IsChecked = true;
            Find<TextBox>(scope, "CourseCode").Text = "PHY-A1-EDIT";
            Click(primary);
            await Until(() => primary.Content?.ToString() == "确认保存" && primary.IsEffectivelyEnabled, "supplement preview ready");
            Check(CourseText(scope, "CoursePreviewIdentity").Contains("当前：物理 A1（PHY-A1）") &&
                CourseText(scope, "CoursePreviewIdentity").Contains("保存后：物理 A1（PHY-A1-EDIT）"), "group edit compares old and new codes");
            Check(primary.TranslatePoint(new Point(0, primary.Bounds.Height), scope)?.Y <= scope.ClientSize.Height,
                "confirm action remains inside the visible compact viewport");
            Find<Border>(scope, "CourseReviewCard").BringIntoView();
            await Capture(scope, output, "05-narrow-source-supplement");
            Click(primary);
            await Until(() => list.SelectedItem is CourseGroupDirectoryRow row && row.Group.Id == groupId &&
                row.Group.Code == "PHY-A1-EDIT" && row.Group.SourceClassIds.Contains(access.TenId), "the same group now includes class 10 under the updated code");
            Check(directorySubject.SelectedItem is CourseGroupLabel kept && kept.Id == access.SubjectId, "compatible directory filters survive saving");
            await Signal("supplemented");
            Click(Find<Button>(scope, "CourseBack"));
            await Until(() => !scope.IsVisible, "group workflow closed");
            schools.SelectedItem = schools.Items.Cast<School>().Single(row => row.Id == access.AlternateSchoolId);
            Click(Find<Button>(main, "OpenClassWorkspaceButton"));
            await Until(() => entry.IsEffectivelyVisible && entry.IsEffectivelyEnabled, "other school is selected");
            Click(entry);
            scope = await WindowOf<CourseGroupsWindow>();
            await Until(() => CourseText(scope, "CourseDirectoryEmpty").Contains("没有启用学期"), "no class or group leaks into the alternate school");
            Check(Find<ListBox>(scope, "CourseGroupRows").ItemCount == 0, "alternate school remains empty");
            Click(Find<Button>(scope, "CourseBack"));
            Finish("PASS: Windows 1160×780 / 840×480, exact source and A1 tasks, teacher conflict blocks write, recover real receipt, separate assignment, supplement original sources, school switch.", 0);

            OverviewTaskItem? FindTask(string code, string target, bool group) => tasks.Items.Cast<OverviewTaskItem>()
                .FirstOrDefault(row => row.Task.Code == code && (group ? row.Task.CourseGroupId : row.Task.ClassId) == target && row.Task.SubjectId == access.SubjectId);

            async Task<CourseGroupsWindow> OpenTask(string code, string target, bool group)
            {
                Button? action = null;
                await Until(() =>
                {
                    if (FindTask(code, target, group) is not { } item) return false;
                    Check(item.Task.Action == (group ? "OPEN_COURSE_GROUP_TEACHING" : "OPEN_COURSE_GROUP_SOURCE"), "opt-in action with exact scope");
                    action = tasks.GetVisualDescendants().OfType<Button>().FirstOrDefault(row => ReferenceEquals(row.Tag, item) && row.IsEffectivelyVisible);
                    if (action is null) tasks.ScrollIntoView(tasks.Items.IndexOf(item));
                    return action is not null;
                }, "task has a real action: " + code);
                Click(action!);
                return await WindowOf<CourseGroupsWindow>();
            }
        }
        catch (Exception error)
        {
            try { if (desktop.Windows.LastOrDefault(window => window.IsVisible) is { } window) await Capture(window, output, "failure"); }
            finally { Finish(error.ToString(), 1); }
        }

        async Task<T> WindowOf<T>() where T : Window
        {
            await Until(() => desktop.Windows.OfType<T>().Any(window => window.IsVisible), typeof(T).Name + " opens");
            return desktop.Windows.OfType<T>().Single(window => window.IsVisible);
        }

        async Task Signal(string step)
        {
            using var response = await control.PostAsync("/__course/" + step, null);
            Check(response.IsSuccessStatusCode, "isolated server check: " + step);
        }

        void Finish(string result, int code)
        {
            try { File.WriteAllText(Path.Combine(output, "result.txt"), result); }
            finally { System.Console.WriteLine(result); desktop.Shutdown(code); }
        }
    }

    private static string CourseText(Window window, string name) => Find<TextBlock>(window, name).Text ?? "";
    private static CheckBox Source(Window window, string id) => Find<StackPanel>(window, "CourseSourceRows")
        .GetVisualDescendants().OfType<CheckBox>().Single(row => AutomationProperties.GetAutomationId(row) == "CourseSource-" + id);
}
