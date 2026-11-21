using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private sealed record SubjectAccess(string Origin, string SchoolId, string SchoolCode, string Password, string SecondUsername, string GradeId,
        string SevenId, string EightId, string NineId, string MathId, string PhysicsId, string TeacherId, string EmptySchoolId, string EmptyClassId,
        bool AllowSubjectSetupTestWrites, string[] LongSubjectIds);

    private static int StartSubjectSetup(string path, string output)
    {
        var access = JsonSerializer.Deserialize<SubjectAccess>(File.ReadAllText(path), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Missing subject-setup fixture.");
        Check(access.AllowSubjectSetupTestWrites && IsLoopback(access.Origin), "Use an isolated loopback fixture.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifySubjectSetupAsync(access, output); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifySubjectSetupAsync(SubjectAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        using var fixture = new HttpClient { BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            Find<TextBox>(main, "ServerAddress").Text = access.Origin;
            Find<TextBox>(main, "SchoolCode").Text = access.SchoolCode;
            Find<TextBox>(main, "Username").Text = "admin";
            Find<TextBox>(main, "Password").Text = access.Password;
            Click(Find<Button>(main, "LoginButton"));
            var schools = Find<ComboBox>(main, "SchoolPicker");
            await Until(() => schools.Items.Cast<School>().Any(item => item.Id == access.SchoolId), "the fixture school");
            schools.SelectedItem = schools.Items.Cast<School>().Single(item => item.Id == access.SchoolId);
            var rooms = Find<ListBox>(main, "ClassPicker");
            Click(Find<Button>(main, "OpenClassWorkspaceButton"));
            await Until(() => rooms.IsEffectivelyVisible && rooms.Items.Cast<Classroom>().Any(item => item.Id == access.SevenId), "school classes");
            rooms.SelectedItem = rooms.Items.Cast<Classroom>().Single(item => item.Id == access.SevenId);
            await Until(() => Find<Button>(main, "OpenSubjectSetupButton").IsEffectivelyVisible, "class details setup entry");
            VerifyBatchPlacementMath();
            var initialPosition = main.Position;
            if (main.Screens.ScreenFromWindow(main) is { } currentScreen)
                main.Position = new PixelPoint(currentScreen.WorkingArea.Right - 390, currentScreen.WorkingArea.Bottom - 260);
            main.Width = 840;
            main.Height = 480;
            await Until(() => main.ClientSize.Width <= 850 && main.ClientSize.Height <= 490, "short school window");
            Click(Find<Button>(main, "OpenSubjectSetupButton"));
            var setup = await FindSetup();
            CheckBatchWindowOnScreen(setup);
            Check(Choice(setup, "Class", access.SevenId).IsChecked == true &&
                Choice(setup, "Class", access.EightId).IsChecked == false, "class details defaults to the selected class");
            Choice(setup, "Subject", access.MathId).IsChecked = true;
            foreach (string id in access.LongSubjectIds) Choice(setup, "Subject", id).IsChecked = true;
            Click(Find<Button>(setup, "SubjectSetupPreviewButton"));
            var longList = Find<ListBox>(setup, "SubjectSetupRows");
            await Until(() => longList.ItemCount == 100, "100 pairs in a real KV preview");
            var last = longList.Items.Cast<SubjectSetupCell>().Last();
            longList.SelectedItem = last;
            longList.ScrollIntoView(last);
            await Until(() => longList.GetVisualDescendants().OfType<CheckBox>().Any(control =>
                AutomationProperties.GetAutomationId(control) == last.ToggleId && FitsBatchWindow(setup, control)),
                "the last row is reachable after scrolling");
            Check(FitsBatchWindow(setup, Find<Grid>(setup, "SubjectSetupColumnLabels")) &&
                FitsBatchWindow(setup, Find<Border>(setup, "SubjectSetupActions")), "subject headers and actions stay visible while browsing 100 pairs");
            await Capture(setup, output, "00-narrow-100-pairs");
            setup.Close();
            await Until(() => !setup.IsVisible, "close setup without writing");
            Click(Find<Button>(main, "OpenSubjectSetupButton"));
            setup = await FindSetup();
            Choice(setup, "Class", access.EightId).IsChecked = true;
            Choice(setup, "Class", access.NineId).IsChecked = true;
            Choice(setup, "Subject", access.PhysicsId).IsChecked = true;
            Click(Find<Button>(setup, "SubjectSetupPreviewButton"));
            var raceList = Find<ListBox>(setup, "SubjectSetupRows");
            await Until(() => raceList.ItemCount == 3, "all three physics rules were absent at preview");
            var excluded = await FindPair(raceList, access.SevenId, access.PhysicsId);
            excluded.IsChecked = false;
            using (var race = await fixture.PostAsync("/__subject-setup/add-physics-concurrently", null))
                Check(race.IsSuccessStatusCode, "the second administrator adds only class 8 physics");
            Click(Find<Button>(setup, "SubjectSetupApplyButton"));
            await Until(() => raceList.Items.Cast<SubjectSetupCell>().Any(cell => cell.Row.ClassId == access.EightId &&
                cell.Row.State == "PRESENT" && cell.Detail == "预览：将新增 · 当前：已随班开设"),
                "stale result shows the previous and current rule on the affected class");
            await AssertSubjectConflictFocusAsync(setup, access.EightId, access.PhysicsId);
            Check(raceList.Items.Cast<SubjectSetupCell>().Single(cell => cell.Row.ClassId == access.SevenId).IsExcluded &&
                !Find<Button>(setup, "SubjectSetupApplyButton").IsEnabled, "the stale attempt keeps explicit exclusion and requires a new preview");
            await Capture(setup, output, "00-stale-now-present");
            setup.Close();
            await Until(() => !setup.IsVisible && main.IsEnabled, "close the stale review");
            main.Position = initialPosition;
            main.Width = 1160;
            main.Height = 780;

            Click(Find<Button>(main, "OpenTeachingButton"));
            var empty = await FindTeaching();
            var emptyEntry = Find<Button>(empty, "TeachingSubjectSetupButton");
            await Until(() => emptyEntry.IsEffectivelyVisible, "empty teaching form offers subject setup");
            Click(emptyEntry);
            setup = await FindSetup();
            Choice(setup, "Class", access.EightId).IsChecked = true;
            Choice(setup, "Class", access.NineId).IsChecked = true;
            Choice(setup, "Subject", access.MathId).IsChecked = true;
            Click(Find<Button>(setup, "SubjectSetupPreviewButton"));
            var list = Find<ListBox>(setup, "SubjectSetupRows");
            await Until(() => list.ItemCount == 3, "one actual preview row per requested pair");
            var cells = list.Items.Cast<SubjectSetupCell>().ToArray();
            Check(cells.Single(cell => cell.Row.ClassId == access.SevenId).Row.State == "ADD" &&
                cells.Single(cell => cell.Row.ClassId == access.EightId).Row.State == "PRESENT" &&
                cells.Single(cell => cell.Row.ClassId == access.NineId).Row.State == "WALKING_CONFLICT", "three precise preview states");
            var save = Find<Button>(setup, "SubjectSetupApplyButton");
            Check(!save.IsEnabled, "the walking conflict disables save");
            Check(setup.Width >= 1100 && setup.Height >= 730, "setup uses the school window size even from teaching");
            Check(Find<TextBlock>(setup, "SubjectSetupScope").Text?.Contains("当前学期 · 从高一（7）班进入") == true,
                "scope labels class 7 as the entry rather than the batch scope");
            Check(Find<TextBlock>(setup, "SubjectSetupCount").Text?.Contains("纳入 3 个班级（已选 3 个）") == true,
                "the preview lists the actual and originally selected class counts");
            await AssertSubjectConflictFocusAsync(setup, access.NineId, access.MathId);
            await Capture(setup, output, "01-three-preview-states");
            var previousTheme = Application.Current!.RequestedThemeVariant;
            Application.Current.RequestedThemeVariant = ThemeVariant.Light;
            await Capture(setup, output, "01-three-preview-states-light");
            Application.Current.RequestedThemeVariant = previousTheme;
            var filter = Find<CheckBox>(setup, "SubjectSetupConflictFilter");
            filter.IsChecked = true;
            Check(list.ItemCount == 1 && ((SubjectSetupCell)list.Items[0]!).Row.ClassId == access.NineId,
                "conflict filter narrows to the exact class and subject");
            filter.IsChecked = false;
            var conflict = cells.Single(cell => cell.Row.ClassId == access.NineId);
            list.ScrollIntoView(conflict);
            CheckBox? toggle = null;
            await Until(() => (toggle = list.GetVisualDescendants().OfType<CheckBox>()
                .FirstOrDefault(check => AutomationProperties.GetAutomationId(check) == conflict.ToggleId)) is not null,
                "resolve the actual exclusion control");
            toggle!.Focus();
            toggle.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space, Source = toggle });
            toggle.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Space, Source = toggle });
            await Until(() => save.IsEnabled, "unchecking only the conflicted cell enables save");
            Check(Choice(setup, "Class", access.NineId).IsChecked == true && list.ItemCount == 3, "the rest of the draft remains");
            Check(Find<TextBlock>(setup, "SubjectSetupCount").Text?.Contains("纳入 2 个班级（已选 3 个）") == true,
                "excluding the only pair for class 9 updates the included class count without changing selected classes");
            await Capture(setup, output, "01-excluded-two-of-three");
            Click(save);
            var readback = Find<Button>(setup, "SubjectSetupReadButton");
            await Until(() => readback.Content?.ToString() == "核对保存结果" && readback.IsEffectivelyEnabled,
                "lost reply and a failed first readback leave the selection locked");
            Check(!save.IsEffectivelyVisible && list.IsEffectivelyEnabled, "the pending list can be reviewed but cannot be submitted");
            Check(!(await FindPair(list, access.SevenId, access.MathId)).IsEffectivelyEnabled,
                "pending pairs remain readable and cannot be changed");
            await Capture(setup, output, "02-pending-readback");
            setup.Close();
            await Until(() => !setup.IsVisible && empty.IsEnabled, "close the unconfirmed setup without clearing the request");
            empty.Close();
            await Until(() => !empty.IsVisible && main.IsEnabled, "return to the classroom entry");
            await CheckOtherSessionDoesNotSeePending();
            await ChooseClass(main, access.EmptySchoolId, access.EmptyClassId);
            Click(Find<Button>(main, "OpenSubjectSetupButton"));
            var otherSchoolSetup = await FindSetup();
            Check(Find<ListBox>(otherSchoolSetup, "SubjectSetupRows").ItemCount == 0 &&
                Find<Button>(otherSchoolSetup, "SubjectSetupReadButton").Content?.ToString() == "刷新目录",
                "the same signed-in account cannot see another school's draft");
            otherSchoolSetup.Close();
            await Until(() => !otherSchoolSetup.IsVisible && main.IsEnabled, "return from other school in the same session");
            await ChooseClass(main, access.SchoolId, access.SevenId);
            Click(Find<Button>(main, "OpenSubjectSetupButton"));
            setup = await FindSetup();
            list = Find<ListBox>(setup, "SubjectSetupRows");
            readback = Find<Button>(setup, "SubjectSetupReadButton");
            await Until(() => list.ItemCount == 3, "reopening from the class workspace restores all three rows");
            await AssertSubjectFocusReturnAsync(setup, "SubjectSetupReadButton");
            Check(list.Items.Cast<SubjectSetupCell>().Single(cell => cell.Row.ClassId == access.NineId).IsExcluded,
                "the walking conflict remains excluded in the reopened selection");
            Click(readback);
            await Until(() => Find<TextBlock>(setup, "SubjectSetupFeedback").Text?.Contains("服务器当前规则与本次选择一致") == true,
                "lost save reply is verified from KV");
            await Capture(setup, output, "02-saved-and-verified");
            using (var receipt = await fixture.PostAsync("/__subject-setup/check-pending", null))
                Check(receipt.IsSuccessStatusCode, "only class 7 is missing a math teacher after adding rules");
            Click(Find<Button>(setup, "SubjectSetupTeachingButton"));
            var teaching = await FindTeaching(empty);
            var subject = Find<ComboBox>(teaching, "TeachingSubjectPicker");
            await Until(() => subject.SelectedItem is TeachingSubject item && item.Id == access.MathId, "continue directly to exact class and subject");
            Check(Find<TextBlock>(teaching, "TeachingContext").Text?.Contains("（7）") == true, "the teaching anchor remains class 7");
            var teachers = Find<ComboBox>(teaching, "TeachingTeacherPicker");
            teachers.SelectedItem = teachers.Items.Cast<TeachingTeacher>().Single(item => item.Id == access.TeacherId);
            Click(Find<Button>(teaching, "TeachingPreviewButton"));
            var apply = Find<Button>(teaching, "TeachingApplyButton");
            await Until(() => apply.IsEnabled, "real teaching preview is ready");
            Click(apply);
            await Until(() => Find<TextBlock>(teaching, "TeachingFeedback").Text?.Contains("已保存 1 个班") == true, "teacher is assigned");
            teaching.Close();
            Click(Find<Button>(main, "OpenSchoolOverviewButton"));
            var overview = Find<SchoolOverviewView>(main, "SchoolOverviewWorkspace");
            await ExpandTaskQueue(overview);
            var tasks = overview.FindControl<ListBox>("OverviewTasksList")!;
            await Until(() => overview.IsEffectivelyVisible && tasks.ItemCount > 0 &&
                !tasks.Items.Cast<OverviewTaskItem>().Any(item => item.Task.Code == "ADMIN_CLASS_SUBJECT_WITHOUT_TEACHER" &&
                    item.Task.SubjectId == access.MathId), "fresh overview no longer has the math assignment task");
            await Capture(main, output, "03-overview-after-teaching");

            Click(Find<Button>(main, "OpenClassWorkspaceButton"));
            Click(Find<Button>(main, "OpenClassCreationButton"));
            await Until(() => desktop.Windows.OfType<ClassCreationWindow>().Any(window => window.IsVisible), "class creation form");
            var creation = desktop.Windows.OfType<ClassCreationWindow>().Single(window => window.IsVisible);
            var grades = Find<ComboBox>(creation, "CreationGrade");
            await Until(() => grades.Items.Cast<ClassCreationGrade>().Any(item => item.Id == access.GradeId), "current grade directory");
            grades.SelectedItem = grades.Items.Cast<ClassCreationGrade>().Single(item => item.Id == access.GradeId);
            Click(Find<Button>(creation, "OpenClassBatchButton"));
            Find<TextBox>(creation, "BatchStartNumber").Text = "10";
            Find<TextBox>(creation, "BatchEndNumber").Text = "11";
            Click(Find<Button>(creation, "BatchPreviewButton"));
            Click(Find<Button>(creation, "BatchSubmitButton"));
            var results = Find<ListBox>(creation, "BatchResults");
            await Until(() => results.ItemCount == 2, "two real class-creation receipt rows");
            results.SelectedIndex = 0;
            var batchSetup = Find<Button>(creation, "BatchOpenSubjectSetupButton");
            await Until(() => batchSetup.IsEnabled, "batch result setup entry");
            Click(batchSetup);
            setup = await FindSetup();
            var checks = Find<StackPanel>(setup, "SubjectSetupClasses").Children.OfType<CheckBox>().Where(item => item.IsChecked == true).ToArray();
            Check(checks.Length == 2 && checks.All(check => check.Content?.ToString()?.Contains("10班") == true ||
                check.Content?.ToString()?.Contains("11班") == true), "batch defaults to only the two newly created classes");
            await Capture(setup, output, "04-batch-preselection");
            setup.Close();
            creation.Close();
            schools.SelectedItem = schools.Items.Cast<School>().Single(item => item.Id == access.EmptySchoolId);
            Click(Find<Button>(main, "OpenClassWorkspaceButton"));
            await Until(() => rooms.IsEffectivelyVisible && rooms.Items.Cast<Classroom>().Any(item => item.Id == access.EmptyClassId), "school with no existing subjects");
            rooms.SelectedItem = rooms.Items.Cast<Classroom>().Single(item => item.Id == access.EmptyClassId);
            await Until(() => Find<Button>(main, "OpenSubjectSetupButton").IsEffectivelyVisible, "open setup with no subjects");
            Click(Find<Button>(main, "OpenSubjectSetupButton"));
            setup = await FindSetup();
            Check(Find<TextBlock>(setup, "SubjectSetupFeedback").Text?.Contains("学校还没有学科") == true &&
                Find<Button>(setup, "SubjectSetupNewSubjectButton").IsEffectivelyEnabled &&
                Find<Button>(setup, "SubjectSetupWebButton").IsEnabled &&
                !Find<Button>(setup, "SubjectSetupPreviewButton").IsEnabled, "missing subjects point to existing maintenance");
            await Capture(setup, output, "05-school-without-subjects");
            setup.Close();
            Finish("PASS: Windows class detail, empty teaching and catalog, exact batch preview, per-cell exclusion, lost reply, teaching, overview, and batch result preselection.", 0);
        }
        catch (Exception error)
        {
            System.Console.Error.WriteLine(error);
            try
            {
                var visible = desktop.Windows.LastOrDefault(window => window.IsVisible);
                if (visible is not null) await Capture(visible, output, "failure");
            }
            catch (Exception captureError) { System.Console.Error.WriteLine(captureError.Message); }
            Finish(error.ToString(), 1);
        }

        async Task ChooseClass(MainWindow window, string schoolId, string classId)
        {
            var schoolPicker = Find<ComboBox>(window, "SchoolPicker");
            await Until(() => schoolPicker.Items.Cast<School>().Any(school => school.Id == schoolId), "authorized school");
            schoolPicker.SelectedItem = schoolPicker.Items.Cast<School>().Single(school => school.Id == schoolId);
            Click(Find<Button>(window, "OpenClassWorkspaceButton"));
            var classPicker = Find<ListBox>(window, "ClassPicker");
            await Until(() => classPicker.IsEffectivelyVisible && classPicker.Items.Cast<Classroom>().Any(room => room.Id == classId),
                "class in the selected school");
            classPicker.SelectedItem = classPicker.Items.Cast<Classroom>().Single(room => room.Id == classId);
            await Until(() => Find<Button>(window, "OpenSubjectSetupButton").IsEffectivelyVisible, "setup entry for the current class");
        }

        async Task CheckOtherSessionDoesNotSeePending()
        {
            var otherMain = new MainWindow { Width = 840, Height = 480 };
            otherMain.Show();
            try
            {
                Find<TextBox>(otherMain, "ServerAddress").Text = access.Origin;
                Find<TextBox>(otherMain, "SchoolCode").Text = access.SchoolCode;
                Find<TextBox>(otherMain, "Username").Text = access.SecondUsername;
                Find<TextBox>(otherMain, "Password").Text = access.Password;
                Click(Find<Button>(otherMain, "LoginButton"));
                await ChooseClass(otherMain, access.SchoolId, access.SevenId);
                Click(Find<Button>(otherMain, "OpenSubjectSetupButton"));
                var otherSetup = await FindSetup();
                Check(Find<ListBox>(otherSetup, "SubjectSetupRows").ItemCount == 0 &&
                    Find<Button>(otherSetup, "SubjectSetupReadButton").Content?.ToString() == "刷新目录",
                    "an independent authorized account cannot see the previous account's draft");
                otherSetup.Close();
                await Until(() => !otherSetup.IsVisible && otherMain.IsEnabled, "close the second account's editor");
                await SignOut(otherMain);
            }
            finally { otherMain.Close(); }
            main.Activate();
        }

        async Task<CheckBox> FindPair(ListBox list, string roomId, string subjectId)
        {
            CheckBox? result = null;
            await Until(() => (result = list.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault(control =>
                AutomationProperties.GetAutomationId(control) == $"SubjectSetupPair-{roomId}-{subjectId}" && control.IsEffectivelyVisible))
                is not null, "the pair editor is realized");
            return result!;
        }

        async Task<SubjectSetupWindow> FindSetup()
        {
            await Until(() => desktop.Windows.OfType<SubjectSetupWindow>().Any(window => window.IsVisible &&
                Find<StackPanel>(window, "SubjectSetupClasses").Children.Count > 0), "valid subject setup directory");
            return desktop.Windows.OfType<SubjectSetupWindow>().Single(window => window.IsVisible);
        }
        async Task<TeachingWindow> FindTeaching(TeachingWindow? excluding = null)
        {
            await Until(() => desktop.Windows.OfType<TeachingWindow>().Any(window => window != excluding && window.IsVisible), "teaching window");
            return desktop.Windows.OfType<TeachingWindow>().Single(window => window != excluding && window.IsVisible);
        }
        void Finish(string message, int code)
        {
            try { File.WriteAllText(Path.Combine(output, "result.txt"), message); }
            finally { System.Console.WriteLine(message); desktop.Shutdown(code); }
        }
    }

    private static CheckBox Choice(SubjectSetupWindow window, string kind, string id) =>
        Find<StackPanel>(window, "SubjectSetup" + (kind == "Class" ? "Classes" : "Subjects")).Children.OfType<CheckBox>()
            .Single(check => AutomationProperties.GetAutomationId(check) == "SubjectSetup" + kind + "-" + id);
}
