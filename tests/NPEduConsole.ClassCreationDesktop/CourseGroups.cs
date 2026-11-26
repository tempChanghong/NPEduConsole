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
        string SevenId, string EightId, string NineId, string TenId, string SubjectId, string TeacherId, string A2Id, string NextRoomId,
        bool AllowCourseTestWrites);

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
            Check(main.ClientSize.Width >= 1100 && desktop.Windows.Count(window => window.IsVisible) == 1 &&
                ReferenceEquals(TopLevel.GetTopLevel(scope), main) && ((CourseGroupLabel)Find<ComboBox>(scope, "CourseSubject").SelectedItem!).Id == access.SubjectId,
                "wide creation shows the exact subject");
            var caption = main.GetVisualAncestors().Last().GetVisualDescendants().OfType<Button>().Where(button =>
                button.IsEffectivelyVisible && button.Name is "PART_MinimizeButton" or "PART_MaximizeButton" or "PART_CloseButton" or "PART_FullScreenButton");
            Check(main.IsExtendedIntoWindowDecorations && caption.Select(button => button.Name).ToHashSet().SetEquals(
                ["PART_MinimizeButton", "PART_MaximizeButton", "PART_CloseButton"]), "walking uses the three buttons in the existing Windows title bar");
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
            await CaptureWithFrame(main, output, "01-wide-fixed-teacher-conflict");
            Click(conflict);
            var teaching = await AcademicPageAsync<TeachingView>(main);
            await Until(() => CourseText(teaching, "TeachingContext").Contains("高二（9）班") &&
                Find<ComboBox>(teaching, "TeachingExistingPicker").Items.Cast<TeachingAssignment>()
                    .Any(row => row.TeacherName == "原任课教师"), "exact scheduled 9th class opens in the main window");
            Click(Find<Button>(teaching, "CloseTeachingButton"));
            await Until(() => !teaching.IsEffectivelyVisible && primary.IsEffectivelyEnabled, "returns to same walking draft");
            Check(Find<TextBox>(scope, "CourseName").Text == "物理 A1" && Find<TextBox>(scope, "CourseCode").Text == "PHY-A1" &&
                Source(scope, access.EightId).IsChecked == true && Source(scope, access.NineId).IsChecked == true, "all fields and picks survived");
            Source(scope, access.NineId).IsChecked = false;
            Source(scope, access.SevenId).IsChecked = true;
            Click(primary);
            await Until(() => primary.Content?.ToString() == "确认保存" && primary.IsEffectivelyEnabled, "7/8 preview ready");
            Check(Find<StackPanel>(scope, "CoursePreviewRows").GetVisualDescendants().OfType<TextBlock>().Any(row => row.Text?.Contains("物理 A2") == true),
                "first source explicitly keeps its other teaching group");
            Check(Find<Border>(scope, "CourseSourcesCard").IsEffectivelyVisible && Find<Border>(scope, "CourseReviewCard").IsEffectivelyVisible &&
                Grid.GetColumn(Find<Border>(scope, "CourseReviewCard")) == 1, "wide workspace shows source and review side by side");
            await CaptureWithFrame(main, output, "02-wide-source-in-two-teaching-groups");
            Click(primary);
            await Signal("commit-response-held");
            var back = Find<Button>(scope, "CourseBack");
            Check(!back.IsEffectivelyEnabled, "the close button is disabled while the save response is in flight");
            main.Close();
            Check(scope.IsEffectivelyVisible, "closing the actual window is blocked while the save response is in flight");
            await Signal("drop-commit-response");
            await Signal("receipt-response-held");
            Check(CourseText(scope, "CourseNotice").Contains("只读核对") && back.IsEffectivelyEnabled,
                "the close button is available while the original receipt is being read");
            await CaptureWithFrame(main, output, "02a-close-while-reading-original-receipt");
            Click(back);
            await Until(() => !scope.IsEffectivelyVisible, "closing during receipt lookup returns to the main window");
            await Signal("receipt-read-canceled");

            scope = await OpenTask("WALKING_SUBJECT_WITHOUT_GROUP", access.TenId, false);
            primary = Find<Button>(scope, "CoursePrimary");
            await Until(() => primary.IsEffectivelyEnabled, "the second source entry has finished opening");
            Check(Find<ScrollViewer>(scope, "CourseEditorScroll").IsEffectivelyVisible &&
                Source(scope, access.TenId).IsChecked == true && Source(scope, access.EightId).IsChecked == false &&
                string.IsNullOrEmpty(Find<TextBox>(scope, "CourseName").Text) && primary.Content?.ToString() == "预览改动",
                "同科另一来源班打开自己的草稿，未接管先前班级的原请求");
            await Signal("other-source-opened");
            await CaptureWithFrame(main, output, "02b-other-source-keeps-exact-target");
            Find<TextBox>(scope, "CourseName").Text = "物理 B1";
            Find<TextBox>(scope, "CourseCode").Text = "PHY-B1";
            Click(primary);
            await Until(() => primary.Content?.ToString() == "确认保存" && primary.IsEffectivelyEnabled, "the other source has its own preview");
            Click(primary);
            var secondGroups = Find<ListBox>(scope, "CourseGroupRows");
            await Until(() => secondGroups.SelectedItem is CourseGroupDirectoryRow row && row.Group.Code == "PHY-B1" &&
                CourseText(scope, "CourseNotice").Contains("已确认"), "the second source saves a different server group");
            string otherGroupId = ((CourseGroupDirectoryRow)secondGroups.SelectedItem!).Group.Id;
            await Signal("other-source-saved");
            Click(Find<Button>(scope, "CourseBack"));
            await Until(() => !scope.IsEffectivelyVisible, "the second source can close after saving");

            scope = await OpenTask("COURSE_GROUP_WITHOUT_TEACHER", otherGroupId, true);
            teaching = await AcademicPageAsync<TeachingView>(main);
            await Until(() => CourseText(teaching, "TeachingContext").Contains("物理 B1"), "the exact second group reaches its own teaching page");
            await Signal("other-group-opened");
            Click(Find<Button>(teaching, "CloseTeachingButton"));
            await Until(() => !teaching.IsEffectivelyVisible && Find<Button>(scope, "CourseEdit").IsEffectivelyEnabled, "return to the second group");
            Click(Find<Button>(scope, "CourseBack"));
            await Until(() => !scope.IsEffectivelyVisible && FindTask("COURSE_GROUP_WITHOUT_TEACHER", otherGroupId, true) is not null,
                "original-term group action remains in the loaded overview");

            await Signal("switch-term-for-bookmark");
            scope = await OpenTask("COURSE_GROUP_WITHOUT_TEACHER", otherGroupId, true);
            await Until(() => CourseText(scope, "CourseNotice").Contains("启用学期已切换"), "the original group task reports its now inactive term");
            await Signal("original-term-rejected");
            Click(Find<Button>(scope, "CourseBack"));
            await Until(() => !scope.IsEffectivelyVisible, "the stale-term entry closes");

            scope = await OpenTask("WALKING_SUBJECT_WITHOUT_GROUP", access.NextRoomId, false);
            primary = Find<Button>(scope, "CoursePrimary");
            await Until(() => primary.IsEffectivelyEnabled, "the new-term source entry has finished opening");
            Check(CourseText(scope, "CourseScope").Contains("切换后的学期") &&
                Source(scope, access.NextRoomId).IsChecked == true && primary.Content?.ToString() == "预览改动",
                "不同学期同名来源班的待办仍按实际 ID 打开自己的草稿");
            await Signal("different-term-opened");
            await CaptureWithFrame(main, output, "02c-new-term-same-named-source");
            var newTermBack = Find<Button>(scope, "CourseBack");
            Click(newTermBack);
            await Until(() => newTermBack.Content?.ToString() == "返回", "an untouched new-term editor returns to its directory");
            Click(newTermBack);
            await Until(() => !scope.IsEffectivelyVisible, "the new-term source closes without writing");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            await PressScoped(main, "OpenClassWorkspaceButton");
            await Until(() => main.IsActive && !Find<ProgressBar>(main, "WorkspaceProgress").IsEffectivelyVisible &&
                Find<ListBox>(main, "ClassPicker").Items.OfType<Classroom>().Any(row => row.Id == access.EightId),
                "the original school's class directory is ready again");
            await PressScoped(main, "OpenCourseGroupsButton");
            scope = await AcademicPageAsync<CourseGroupsView>(main);
            var list = Find<ListBox>(scope, "CourseGroupRows");
            await Until(() => list.SelectedItem is CourseGroupDirectoryRow item && item.Group.Code == "PHY-A1" &&
                CourseText(scope, "CourseNotice").Contains("已确认"), "lost success response verified by original receipt");
            string groupId = ((CourseGroupDirectoryRow)list.SelectedItem!).Group.Id;
            Check(groupId != access.A2Id && list.Items.Cast<CourseGroupDirectoryRow>().Count(row => row.Group.Code == "PHY-A1") == 1,
                "server-confirmed group selected and no clone made");
            await Signal("created");
            await CaptureWithFrame(main, output, "03-wide-saved-and-ready-to-assign");
            Click(Find<Button>(scope, "CourseBack"));
            await Until(() => !scope.IsEffectivelyVisible, "recovered group workflow closes");
            await PressScoped(main, "OpenSchoolOverviewButton");
            await Until(() => !scope.IsEffectivelyVisible && FindTask("COURSE_GROUP_WITHOUT_TEACHER", groupId, true) is not null &&
                FindTask("WALKING_SUBJECT_WITHOUT_GROUP", access.EightId, false) is null, "only the A1 teacher task remains relevant");

            scope = await OpenTask("COURSE_GROUP_WITHOUT_TEACHER", groupId, true);
            teaching = await AcademicPageAsync<TeachingView>(main);
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
            await Until(() => !teaching.IsEffectivelyVisible && Find<Button>(scope, "CourseBack").IsEffectivelyEnabled, "return to group directory");
            Click(Find<Button>(scope, "CourseBack"));
            await Until(() => !scope.IsEffectivelyVisible && FindTask("COURSE_GROUP_WITHOUT_TEACHER", groupId, true) is null, "the exact group task clears");
            Check(filter.SelectedIndex == 1, "return keeps the teaching task filter");

            main.Width = 840;
            main.Height = 480;
            await Until(() => main.ClientSize is {Width: <= 845, Height: <= 485}, "compact parent window measured");
            Click(Find<Button>(main, "OpenClassWorkspaceButton"));
            var entry = Find<Button>(main, "OpenCourseGroupsButton");
            await Until(() => entry.IsEffectivelyVisible && entry.IsEffectivelyEnabled, "existing class workspace exposes walking directory");
            Click(entry);
            scope = await AcademicPageAsync<CourseGroupsView>(main);
            list = Find<ListBox>(scope, "CourseGroupRows");
            await Until(() => list.Items.Cast<CourseGroupDirectoryRow>().Any(row => row.Group.Id == groupId), "reopened directory contains original A1 ID");
            var directorySubject = Find<ComboBox>(scope, "CourseSubjectFilter");
            directorySubject.SelectedItem = directorySubject.Items.Cast<CourseGroupLabel>().Single(row => row.Id == access.SubjectId);
            list.SelectedItem = list.Items.Cast<CourseGroupDirectoryRow>().Single(row => row.Group.Id == groupId);
            Click(Find<Button>(scope, "CourseEdit"));
            primary = Find<Button>(scope, "CoursePrimary");
            await Until(() => Find<ScrollViewer>(scope, "CourseEditorScroll").IsEffectivelyVisible && primary.IsEffectivelyEnabled,
                "existing group opens in compact full-page editor");
            Check(main.ClientSize.Width <= 845 && main.ClientSize.Height <= 485 &&
                CourseText(scope, "CourseEditorStep").Contains("第 1 步") &&
                Find<Border>(scope, "CourseDetailsCard").IsEffectivelyVisible && Find<Border>(scope, "CourseSourcesCard").IsEffectivelyVisible &&
                !Find<Border>(scope, "CourseReviewCard").IsEffectivelyVisible, "compact editor shows its own editing step");
            Check(Source(scope, access.SevenId).IsChecked == true && Source(scope, access.EightId).IsChecked == true,
                "both original sources are retained");
            var sourceViewport = Find<ScrollViewer>(scope, "CourseEditorScroll");
            var firstSource = Source(scope, access.TenId);
            await Until(() => firstSource.TranslatePoint(new Point(0, firstSource.Bounds.Height), sourceViewport)?.Y <= sourceViewport.Bounds.Height,
                "compact edit step displays an actionable source row above the fold");
            Source(scope, access.NineId).IsChecked = true;
            await CaptureWithFrame(main, output, "04a-narrow-edit-sources");
            Click(primary);
            var narrowEditor = Find<ScrollViewer>(scope, "CourseEditorScroll");
            Button? narrowConflict = null;
            await Until(() =>
            {
                narrowConflict = Find<StackPanel>(scope, "CoursePreviewRows").GetVisualDescendants().OfType<Button>()
                    .SingleOrDefault(button => AutomationProperties.GetAutomationId(button) == "CourseConflictTeaching-" + access.NineId);
                return narrowConflict is {IsFocused: true} &&
                    narrowConflict.TranslatePoint(new Point(0, 0), narrowEditor)?.Y >= 0 &&
                    narrowConflict.TranslatePoint(new Point(0, narrowConflict.Bounds.Height), narrowEditor)?.Y <= narrowEditor.Bounds.Height;
            }, "compact conflict action is fully in view and keyboard focused");
            Check(CourseText(scope, "CourseEditorStep").Contains("第 2 步") &&
                !Find<Border>(scope, "CourseDetailsCard").IsEffectivelyVisible && !Find<Border>(scope, "CourseSourcesCard").IsEffectivelyVisible,
                "compact review has exclusive use of the editor viewport");
            main.Width = 1160;
            await Until(() => Find<Border>(scope, "CourseSourcesCard").IsEffectivelyVisible &&
                Grid.GetColumn(Find<Border>(scope, "CourseReviewCard")) == 1, "expanding keeps the current review in the wide layout");
            main.Width = 840;
            await Until(() => !Find<Border>(scope, "CourseSourcesCard").IsEffectivelyVisible && narrowConflict!.IsEffectivelyVisible,
                "returning to compact preserves the current review");
            await CaptureWithFrame(main, output, "04-narrow-existing-group-conflict");
            narrowConflict!.RaiseEvent(new KeyEventArgs {RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = narrowConflict});
            narrowConflict.RaiseEvent(new KeyEventArgs {RoutedEvent = InputElement.KeyUpEvent, Key = Key.Enter, Source = narrowConflict});
            teaching = await AcademicPageAsync<TeachingView>(main);
            await Until(() => CourseText(teaching, "TeachingContext").Contains("高二（9）班") &&
                Find<ComboBox>(teaching, "TeachingExistingPicker").Items.OfType<TeachingAssignment>().Count() == 1,
                "keyboard opens the exact original class and subject");
            Check(ReferenceEquals(TopLevel.GetTopLevel(teaching), main) && desktop.Windows.Count(window => window.IsVisible) == 1,
                "conflict resolution uses the same shell and window frame");
            var originalTeacher = Find<ComboBox>(teaching, "TeachingExistingPicker");
            originalTeacher.SelectedItem = originalTeacher.Items.OfType<TeachingAssignment>().Single();
            Click(Find<Button>(teaching, "TeachingRemoveButton"));
            Click(Find<Button>(teaching, "TeachingPreviewButton"));
            var confirmRemoval = Find<Button>(teaching, "TeachingApplyButton");
            await Until(() => confirmRemoval.IsEffectivelyEnabled, "the original class assignment removal is ready");
            Click(confirmRemoval);
            await Until(() => CourseText(teaching, "TeachingFeedback").Contains("已保存"), "the exact assignment was removed");
            await Signal("narrow-conflict-resolved");
            Click(Find<Button>(teaching, "CloseTeachingButton"));
            await Until(() => !teaching.IsEffectivelyVisible && primary.IsEffectivelyEnabled, "compact conflict return preserves the original draft");
            Check(Source(scope, access.NineId).IsChecked == true && Find<TextBox>(scope, "CourseCode").Text == "PHY-A1",
                "compact conflict handling keeps fields and original source selection");
            Check(CourseText(scope, "CourseEditorStep").Contains("第 1 步") && Source(scope, access.SevenId).IsChecked == true &&
                Source(scope, access.EightId).IsChecked == true, "the original selections are visible in edit mode again");
            Click(primary);
            await Until(() => primary.Content?.ToString() == "确认保存" && primary.IsEffectivelyEnabled,
                "fresh preview after resolving the original conflict");
            Click(Find<Button>(scope, "CourseBack"));
            await Until(() => Source(scope, access.NineId).IsEffectivelyVisible && Source(scope, access.NineId).IsChecked == true &&
                primary.Content?.ToString() == "预览改动", "back from review retains the unsaved draft");
            Source(scope, access.NineId).IsChecked = false;
            Source(scope, access.TenId).IsChecked = true;
            Find<TextBox>(scope, "CourseCode").Text = "PHY-A1-EDIT";
            Click(primary);
            await Until(() => primary.Content?.ToString() == "确认保存" && primary.IsEffectivelyEnabled, "supplement preview ready");
            Check(CourseText(scope, "CoursePreviewIdentity").Contains("当前：物理 A1（PHY-A1）") &&
                CourseText(scope, "CoursePreviewIdentity").Contains("保存后：物理 A1（PHY-A1-EDIT）"), "group edit compares old and new codes");
            Check(primary.TranslatePoint(new Point(0, primary.Bounds.Height), main)?.Y <= main.ClientSize.Height,
                "confirm action remains inside the visible compact viewport");
            Find<Border>(scope, "CourseReviewCard").BringIntoView();
            await CaptureWithFrame(main, output, "05-narrow-source-supplement");
            Click(primary);
            await Signal("existing-edit-read-held");
            Click(Find<Button>(scope, "CourseBack"));
            await Until(() => !scope.IsEffectivelyVisible, "the pending existing-group edit can close during read");
            await Signal("existing-edit-read-canceled");

            await PressScoped(main, "OpenSchoolOverviewButton");
            scope = await OpenTask("COURSE_GROUP_WITHOUT_TEACHER", otherGroupId, true);
            teaching = await AcademicPageAsync<TeachingView>(main);
            await Until(() => CourseText(teaching, "TeachingContext").Contains("物理 B1"), "the other group still opens while the existing edit is pending");
            Click(Find<Button>(teaching, "CloseTeachingButton"));
            await Until(() => !teaching.IsEffectivelyVisible && Find<Button>(scope, "CourseEdit").IsEffectivelyEnabled, "the other group returns to its directory");
            list = Find<ListBox>(scope, "CourseGroupRows");
            directorySubject = Find<ComboBox>(scope, "CourseSubjectFilter");
            directorySubject.SelectedItem = directorySubject.Items.OfType<CourseGroupLabel>().Single(row => row.Id == access.SubjectId);
            list.SelectedItem = list.Items.OfType<CourseGroupDirectoryRow>().Single(row => row.Group.Id == groupId);
            Click(Find<Button>(scope, "CourseEdit"));
            var name = Find<TextBox>(scope, "CourseName");
            name.Text = "尚未提交的修改";
            primary = Find<Button>(scope, "CoursePrimary");
            Click(primary);
            var verify = Find<Button>(scope, "CourseRefresh");
            await Until(() => verify.Content?.ToString() == "核对原申请" && verify.IsEffectivelyEnabled && !primary.IsEffectivelyEnabled,
                "an existing group cannot be saved with a new number before the original request is checked");
            Check(name.Text == "尚未提交的修改" && CourseText(scope, "CourseNotice").Contains("原编号"),
                "the operator sees the original application and retains input until explicitly opening it");
            await Signal("same-group-blocked");
            await CaptureWithFrame(main, output, "06-narrow-same-group-original-request");
            name.Text = "继续编辑也须先核对";
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Check(verify.Content?.ToString() == "核对原申请" && !primary.IsEffectivelyEnabled &&
                CourseText(scope, "CourseNotice").Contains("原编号"), "editing the same group does not dismiss its original request");
            Click(verify);
            await Until(() => list.IsEffectivelyVisible && CourseText(scope, "CourseNotice").Contains("已确认") &&
                list.SelectedItem is CourseGroupDirectoryRow row && row.Group.Id == groupId &&
                row.Group.Code == "PHY-A1-EDIT" && row.Group.SourceClassIds.Contains(access.TenId),
                "the original receipt confirms the same group now includes class 10 under the updated code");
            Check(directorySubject.SelectedItem is CourseGroupLabel kept && kept.Id == access.SubjectId, "compatible directory filters survive saving");
            await Signal("supplemented");
            Click(Find<Button>(scope, "CourseBack"));
            await Until(() => !scope.IsEffectivelyVisible, "group workflow closed");
            schools.SelectedItem = schools.Items.Cast<School>().Single(row => row.Id == access.AlternateSchoolId);
            Click(Find<Button>(main, "OpenClassWorkspaceButton"));
            await Until(() => entry.IsEffectivelyVisible && entry.IsEffectivelyEnabled, "other school is selected");
            Click(entry);
            scope = await AcademicPageAsync<CourseGroupsView>(main);
            await Until(() => CourseText(scope, "CourseDirectoryEmpty").Contains("没有启用学期"), "no class or group leaks into the alternate school");
            Check(Find<ListBox>(scope, "CourseGroupRows").ItemCount == 0, "alternate school remains empty");
            Click(Find<Button>(scope, "CourseBack"));
            Finish("PASS: Windows 1160×780 / 840×480, save protected and receipt wait cancellable, same-subject second source and group open precisely, stale term rejected, original request recovered, teacher conflict and assignment, original sources, school switch.", 0);

            OverviewTaskItem? FindTask(string code, string target, bool group) => tasks.Items.Cast<OverviewTaskItem>()
                .FirstOrDefault(row => row.Task.Code == code && (group ? row.Task.CourseGroupId : row.Task.ClassId) == target && row.Task.SubjectId == access.SubjectId);

            async Task<CourseGroupsView> OpenTask(string code, string target, bool group)
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
                return await AcademicPageAsync<CourseGroupsView>(main);
            }
        }
        catch (Exception error)
        {
            try { if (desktop.Windows.LastOrDefault(window => window.IsVisible) is { } window) await CaptureWithFrame(window, output, "failure"); }
            finally { Finish(error.ToString(), 1); }
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

    private static string CourseText(Control window, string name) => Find<TextBlock>(window, name).Text ?? "";
    private static CheckBox Source(Control window, string id) => Find<StackPanel>(window, "CourseSourceRows")
        .GetVisualDescendants().OfType<CheckBox>().Single(row => AutomationProperties.GetAutomationId(row) == "CourseSource-" + id);
}
