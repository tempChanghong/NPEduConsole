using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private sealed record SubjectAccess(string Origin, string SchoolId, string SchoolCode, string Password, string GradeId,
        string SevenId, string EightId, string NineId, string MathId, string TeacherId, string EmptySchoolId, string EmptyClassId,
        bool AllowSubjectSetupTestWrites);

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
            Click(Find<Button>(main, "OpenSubjectSetupButton"));
            var setup = await FindSetup();
            Check(Choice(setup, "Class", access.SevenId).IsChecked == true &&
                Choice(setup, "Class", access.EightId).IsChecked == false, "class details defaults to the selected class");
            setup.Close();
            await Until(() => !setup.IsVisible, "close setup without writing");

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
            await Capture(setup, output, "01-three-preview-states");
            var conflict = cells.Single(cell => cell.Row.ClassId == access.NineId);
            list.ScrollIntoView(conflict);
            CheckBox? toggle = null;
            await Until(() => (toggle = list.GetVisualDescendants().OfType<CheckBox>()
                .FirstOrDefault(check => AutomationProperties.GetAutomationId(check) == conflict.ToggleId)) is not null,
                "resolve the actual exclusion control");
            toggle!.IsChecked = false;
            await Until(() => save.IsEnabled, "unchecking only the conflicted cell enables save");
            Check(Choice(setup, "Class", access.NineId).IsChecked == true && list.ItemCount == 3, "the rest of the draft remains");
            Click(save);
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
            empty.Close();
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
            Check(Find<TextBlock>(setup, "SubjectSetupFeedback").Text?.Contains("学校尚无学科") == true &&
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
