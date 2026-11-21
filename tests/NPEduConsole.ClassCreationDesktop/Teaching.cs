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
    private sealed record TeachingAccess(string Origin, string SchoolId, string SchoolCode, string Password,
        string SevenId, string EightId, string NineId, string SubjectId, string TeacherId, bool AllowTeachingTestWrites);

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
        var all = overview.FindControl<Button>("ShowAllOverviewButton")!;
        var tasks = overview.FindControl<ListBox>("OverviewTasksList")!;
        await Until(() => all.IsEffectivelyVisible, "the school overview has actionable results");
        if (!tasks.IsEffectivelyVisible) Click(all);
        await Until(() => tasks.IsEffectivelyVisible, "all tasks are visible");
    }

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
            System.Console.WriteLine("Windows: two missing math tasks resolved by ID.");
            await Capture(main, output, "01-missing-sections");

            var opener = tasks.Items.Cast<OverviewTaskItem>().Single(item => IsMathTask(item, access.SevenId));
            tasks.ScrollIntoView(opener);
            Button? button = null;
            await Until(() => (button = tasks.GetVisualDescendants().OfType<Button>().SingleOrDefault(item => ReferenceEquals(item.Tag, opener))) is not null,
                "look up the real 7th class task link");
            Click(button!);
            var dialog = await Editor();
            var subject = Find<ComboBox>(dialog, "TeachingSubjectPicker");
            Check(subject.SelectedItem is TeachingSubject item && item.Id == access.SubjectId &&
                Find<TextBlock>(dialog, "TeachingContext").Text?.Contains("（7）") == true, "anchor class and exact math subject are selected");
            var teacher = Find<ComboBox>(dialog, "TeachingTeacherPicker");
            teacher.SelectedItem = teacher.Items.Cast<TeachingTeacher>().Single(row => row.Id == access.TeacherId);
            Peer(dialog, access.EightId).IsChecked = true;
            Peer(dialog, access.NineId).IsChecked = true;
            Click(Find<Button>(dialog, "TeachingPreviewButton"));
            await Until(() => Find<Button>(dialog, "TeachingPreviewButton").IsEnabled, "finished first preview");
            System.Console.WriteLine(Feedback(dialog) + " " + string.Join(" / ", RowText(dialog)));
            await Until(() => RowText(dialog).Any(row => row.Contains("（9）") && row.Contains("已有其他主讲")), "preview identifies the exact primary conflict");
            Check(!Find<Button>(dialog, "TeachingApplyButton").IsEnabled && Peer(dialog, access.EightId).IsChecked == true, "batch stays uncommittable and retains selections");
            await Capture(dialog, output, "02-primary-conflict");

            Peer(dialog, access.NineId).IsChecked = false;
            Click(Find<Button>(dialog, "TeachingPreviewButton"));
            await Until(() => Find<Button>(dialog, "TeachingApplyButton").IsEnabled, "7 and 8 can be submitted atomically");
            Check(RowText(dialog).Count(row => row.Contains("新增此项任课")) == 2, "preview contains both proposed changes");
            await Capture(dialog, output, "03-batch-preview");
            Click(Find<Button>(dialog, "TeachingApplyButton"));
            await Until(() => Feedback(dialog).Contains("已核对，服务器当前安排与本次提交一致"), "lost successful reply triggers a server read");
            await Capture(dialog, output, "04-receipt-reconciled");
            dialog.Close();
            await Until(() => overview.IsEffectivelyVisible && tasks.ItemCount > 0 && !Missing(access.SevenId) && !Missing(access.EightId), "return reloads both resolved tasks");
            await Capture(main, output, "05-overview-after-save");

            Click(Find<Button>(main, "OpenClassWorkspaceButton"));
            await Until(() => rooms.IsEffectivelyVisible && rooms.Items.Cast<Classroom>().Any(row => row.Id == access.EightId), "8 remains selectable in the class directory");
            rooms.SelectedItem = rooms.Items.Cast<Classroom>().Single(row => row.Id == access.EightId);
            var classroomEntry = Find<Button>(main, "OpenTeachingButton");
            await Until(() => classroomEntry.IsEffectivelyVisible, "class detail exposes teaching entry");
            Click(classroomEntry);
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
            dialog.Close();
            Click(Find<Button>(main, "OpenSchoolOverviewButton"));
            await Until(() => Missing(access.EightId), "8th class missing-math task returns");
            Check(!Missing(access.SevenId) && !Missing(access.NineId), "the other two classes stay resolved");
            await Capture(main, output, "07-only-eight-needs-teacher");
            Finish("PASS: Windows Console exact task, primary conflict, atomic batch, lost response reconciliation, and single-class removal.", 0);
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
        static CheckBox Peer(TeachingWindow dialog, string id) => Find<StackPanel>(dialog, "TeachingClassChoices").Children.OfType<CheckBox>()
            .Single(row => AutomationProperties.GetAutomationId(row) == "TeachingClass-" + id);
        static string Feedback(TeachingWindow dialog) => Find<TextBlock>(dialog, "TeachingFeedback").Text ?? "";
        static IEnumerable<string> RowText(TeachingWindow dialog) => Find<StackPanel>(dialog, "TeachingRows").Children.OfType<Border>()
            .Select(row => ((TextBlock)row.Child!).Text ?? "");
        async Task<TeachingWindow> Editor()
        {
            await Until(() => desktop.Windows.OfType<TeachingWindow>().Any(window =>
                window.IsVisible && Find<ComboBox>(window, "TeachingSubjectPicker").ItemCount > 0), "open scoped teaching window");
            return desktop.Windows.OfType<TeachingWindow>().Single(window => window.IsVisible);
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
}
