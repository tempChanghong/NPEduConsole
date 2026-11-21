using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private sealed record OrganizationImportAccess(string Origin, string SchoolId, string SchoolCode, string Password, string DraftId,
        string DraftName, string ActiveId, string DraftFile, string WrongGradeFile, bool AllowOrganizationTestWrites);

    private static int StartOrganizationImport(string fixture, string output)
    {
        var access = JsonSerializer.Deserialize<OrganizationImportAccess>(File.ReadAllText(fixture), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Expected isolated school organization fixture.");
        Check(access.AllowOrganizationTestWrites && IsLoopback(access.Origin), "Organization writes require the isolated loopback fixture.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyOrganizationImportAsync(access, output); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyOrganizationImportAsync(OrganizationImportAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        using var control = new HttpClient { BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(95) };
        try
        {
            main.Width = 1160;
            main.Height = 780;
            Find<TextBox>(main, "ServerAddress").Text = access.Origin;
            Find<TextBox>(main, "SchoolCode").Text = access.SchoolCode;
            Find<TextBox>(main, "Username").Text = "admin";
            Find<TextBox>(main, "Password").Text = access.Password;
            Click(Find<Button>(main, "LoginButton"));
            var school = Find<ComboBox>(main, "SchoolPicker");
            await Until(() => school.Items.OfType<School>().Any(row => row.Id == access.SchoolId), "real administrator has school selection");
            school.SelectedItem = school.Items.OfType<School>().Single(row => row.Id == access.SchoolId);
            var classes = Find<Button>(main, "OpenClassWorkspaceButton");
            await Until(() => classes.IsEffectivelyVisible && classes.IsEffectivelyEnabled, "class workspace open");
            Click(classes);

            Click(Find<Button>(main, "OpenTermPreparationButton"));
            var termManager = await WindowOf<TermPreparationWindow>();
            var terms = Find<ListBox>(termManager, "PreparedTerms");
            await Until(() => terms.Items.OfType<ManagedAcademicTerm>().Any(row => row.Id == access.DraftId), "existing draft shown in term manager");
            terms.SelectedItem = terms.Items.OfType<ManagedAcademicTerm>().Single(row => row.Id == access.DraftId);
            var entry = Find<Button>(termManager, "TermOrganizationImport");
            await Until(() => entry.IsEffectivelyVisible && entry.IsEffectivelyEnabled, "draft term carries its ID into organization editor");
            Click(entry);

            var window = await WindowOf<OrganizationImportWindow>();
            await Until(() => Find<ComboBox>(window, "OrgTerm").SelectedItem is OrganizationTerm { Status: "DRAFT" } term &&
                term.Id == access.DraftId, "organization editor scopes the existing draft");
            Click(Find<Button>(window, "OrgExample"));
            Check(Find<ListBox>(window, "OrgClasses").ItemCount == 7 && Status(window).Contains("示例"), "real action loads seven-class example");
            var gradeChoice = Find<ComboBox>(window, "OrgGrade");
            gradeChoice.SelectedItem = gradeChoice.Items.OfType<object>().Single(row => row.ToString()?.Contains("高二（G2）") == true);
            window.Width = 840;
            window.Height = 480;
            await Until(() => window.ClientSize is { Width: <= 845, Height: <= 485 }, "compact editor geometry");
            var loadDocument = typeof(OrganizationImportWindow).GetMethod("LoadOrganizationText", BindingFlags.Instance | BindingFlags.NonPublic)!;
            loadDocument.Invoke(window, [await File.ReadAllTextAsync(access.WrongGradeFile)]);
            Check(Find<TextBox>(window, "OrgGradeCode").Text == "G2" && Status(window).Contains("高一（G1）"),
                "loading a G1 document preserves the manually selected G2 target and calls out G1 locally");
            Click(Find<Button>(window, "OrgPreview"));
            var differences = Find<ListBox>(window, "OrgDifferences");
            await Until(() => differences.Items.OfType<OrganizationDifference>().Any(row => row.Action == "BLOCK" &&
                row.Path == "declaration.gradeCode" && row.Current?.Contains("G2") == true && row.After?.Contains("G1") == true),
                "the server blocks the grade mismatch and returns both target and file values");
            Check(!Find<Button>(window, "OrgSave").IsEnabled, "mismatched grade cannot be confirmed");
            await Signal("wrong-grade-zero-write");
            await Capture(window, output, "00-compact-grade-declaration-blocked");
            differences.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F8 });
            await Until(() => gradeChoice.IsFocused, "F8 on a grade declaration conflict returns focus to the selected target");
            loadDocument.Invoke(window, [await File.ReadAllTextAsync(access.DraftFile)]);
            Check(Find<ListBox>(window, "OrgClasses").ItemCount == 7 && Find<ListBox>(window, "OrgGroups").ItemCount == 1,
                "legacy file populates seven classes and one teaching group");
            Click(Find<Button>(window, "OrgPreview"));
            await Until(() => differences.Items.OfType<OrganizationDifference>().Any(row => row.Action == "BLOCK" && row.Object.Contains("09")),
                "nine follows administrative class and cannot be selected for A1");
            Check(!Find<Button>(window, "OrgSave").IsEnabled, "conflict blocks committing the batch");
            await Capture(window, output, "01-compact-source-blocked");
            differences.SelectedItem = differences.Items.OfType<OrganizationDifference>().Single(row => row.Action == "BLOCK");
            differences.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            var source = Find<TextBox>(window, "OrgSourceCodes");
            await Until(() => source.IsFocused && source.IsEffectivelyVisible && source.Text?.Contains("G2-C09") == true,
                "Enter from blocked source opens the correct compact editor and focuses that cell");
            await Capture(window, output, "02-compact-focused-source");
            var rear = Find<StackPanel>(window, "OrgSources").Children.OfType<CheckBox>().Single(check =>
                check.Content?.ToString()?.Contains("G2-C09") == true);
            rear.IsChecked = false;
            Check(source.Text == "G2-C07，G2-C08", "removing ninth class retains seventh and eighth");
            Click(Find<Button>(window, "OrgBackGroupTable"));
            await Until(() => Find<ListBox>(window, "OrgGroups").IsEffectivelyVisible, "compact return restores table");
            window.Width = 1160;
            window.Height = 780;
            Click(Find<Button>(window, "OrgPreview"));
            await Until(() => Find<CheckBox>(window, "OrgApprove").IsEffectivelyEnabled && Status(window).Contains("请核对差异"),
                "corrected draft computes a fresh server preview");
            await Capture(window, output, "03-wide-corrected-impact");
            await Signal("legacy-change");
            Find<CheckBox>(window, "OrgApprove").IsChecked = true;
            Click(Find<Button>(window, "OrgSave"));
            await Until(() => differences.Items.OfType<OrganizationDifference>().Any(row => row.Key == "class:G2-C08" && row.NeedsReview) &&
                Status(window).Contains("零写入"), "old web change invalidates the entire preview and marks eight");
            await Signal("stale-zero-write");
            await Capture(window, output, "04-wide-stale-eighth");

            Click(Find<Button>(window, "OrgPreview"));
            await Until(() => Find<CheckBox>(window, "OrgApprove").IsEffectivelyEnabled, "fresh reviewed draft");
            Find<CheckBox>(window, "OrgApprove").IsChecked = true;
            Click(Find<Button>(window, "OrgSave"));
            await Until(() => Status(window).Contains("找回本次") && Find<ListBox>(window, "OrgReceiptClasses").ItemCount == 8,
                "dropped success response is recovered by the original read-only receipt");
            await Signal("draft-applied");
            await Capture(window, output, "05-draft-receipt");

            Click(Find<Button>(window, "OrgPreview"));
            await Until(() => Status(window).Contains("已与服务器一致") && differences.Items.OfType<OrganizationDifference>().Any() &&
                !Find<Button>(window, "OrgSave").IsEnabled,
                "same table can be compared again");
            Check(differences.Items.OfType<OrganizationDifference>().All(row => row.Action == "KEEP"),
                "same table preview is entirely consistent");
            await Signal("repeat-kept");
            Find<TabControl>(window, "OrgSections").SelectedIndex = 4;
            Click(Find<Button>(window, "OrgContinue"));
            var heading = Find<TextBlock>(termManager, "TermHeading");
            await Until(() => heading.Text?.Contains(access.DraftName) == true &&
                heading.Text.Contains("启用前检查") && Find<TextBlock>(termManager, "TermCheckStatus").Text?.Contains("正在读取") == false,
                "receipt opens the existing exact draft term readiness view");
            await Capture(termManager, output, "06-draft-term-readiness");
            termManager.Close();
            await Until(() => !termManager.IsVisible && main.IsEnabled, "main workspace restored");

            Click(Find<Button>(main, "OpenOrganizationImportButton"));
            window = await WindowOf<OrganizationImportWindow>();
            await Until(() => Find<ComboBox>(window, "OrgTerm").SelectedItem is OrganizationTerm { Status: "ACTIVE" } term &&
                term.Id == access.ActiveId, "normal workspace entry defaults to active term");
            var grade = Find<ComboBox>(window, "OrgGrade");
            grade.SelectedItem = grade.Items.OfType<object>().Single(row => row.ToString()?.Contains("高二（G2）") == true);
            Click(Find<Button>(window, "OrgExcel"));
            await Until(() => desktop.Windows.Any(row => row.IsVisible && row.Title == "从 Excel 粘贴"), "Excel column dialog opened");
            var paste = desktop.Windows.Single(row => row.IsVisible && row.Title == "从 Excel 粘贴");
            var pasteCell = paste.GetVisualDescendants().OfType<TextBox>().Single(row => AutomationProperties.GetName(row) == "粘贴 Excel 列");
            pasteCell.Text = "班级代码\t班名\t学科代码\t学科名称\t授课方式\t必修\t学生自选\nG2-C15\t高二（15）班\tPHY\t物理\t随班\t是\t是";
            Click(paste.GetVisualDescendants().OfType<Button>().Single(row => Equals(row.Content, "替换本表草稿")));
            await Until(() => !paste.IsVisible && Find<ListBox>(window, "OrgClasses").ItemCount == 1, "Excel rows loaded through the actual editor");
            Click(Find<Button>(window, "OrgPreview"));
            await Until(() => Find<CheckBox>(window, "OrgApprove").IsEffectivelyEnabled, "active term preview can be confirmed");
            Find<CheckBox>(window, "OrgApprove").IsChecked = true;
            Click(Find<Button>(window, "OrgSave"));
            await Until(() => Status(window).Contains("由服务器确认") && Find<ListBox>(window, "OrgReceiptClasses").ItemCount == 1, "active import result");
            await Signal("current-applied");
            await Capture(window, output, "07-current-receipt");
            Click(Find<Button>(window, "OrgContinue"));
            var teaching = await WindowOf<TeachingWindow>();
            var subjects = Find<ComboBox>(teaching, "TeachingSubjectPicker");
            await Until(() => subjects.Items.OfType<TeachingSubject>().Any(row => row.Name == "物理"), "teaching dialog resolves imported class and physics");
            Check(teaching.GetVisualDescendants().OfType<TextBlock>().Any(row => row.Text?.Contains("高二（15）班") == true),
                "receipt takes the administrator to the exact imported class");
            await Capture(teaching, output, "08-current-teaching");
            Console.WriteLine("PASS: Windows compact conflict correction, actual draft and active workflows, receipts and destination.");
            desktop.Shutdown(0);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            var visible = desktop.Windows.LastOrDefault(row => row.IsVisible && row.Title != "从 Excel 粘贴");
            if (visible is not null) try { await Capture(visible, output, "desktop-organization-failure"); } catch { }
            desktop.Shutdown(1);
        }

        async Task<T> WindowOf<T>() where T : Window
        {
            await Until(() => desktop.Windows.OfType<T>().Any(row => row.IsVisible), typeof(T).Name + " is visible");
            return desktop.Windows.OfType<T>().Single(row => row.IsVisible);
        }
        async Task Signal(string mark)
        {
            using var response = await control.PostAsync("/__org/" + mark, null);
            Check(response.IsSuccessStatusCode, "isolated PostgreSQL checkpoint: " + mark);
        }
        static string Status(Window window) => Find<TextBlock>(window, "OrgStatus").Text ?? "";
    }
}
