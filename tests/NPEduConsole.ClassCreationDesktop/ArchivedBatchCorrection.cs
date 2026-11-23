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
    private sealed record ArchivedCorrectionAccess(string Origin, string SchoolId, string SchoolCode, string Password,
        string ArchivedTermId, string ActiveName, string ArchiveName, string File, string ClassId, string GroupId, string TeacherId);

    private static int StartArchivedBatchCorrection(string fixture, string output)
    {
        var access = JsonSerializer.Deserialize<ArchivedCorrectionAccess>(File.ReadAllText(fixture), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Expected isolated archive correction fixture.");
        Check(IsLoopback(access.Origin), "Archive writes require a loopback fixture.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyArchivedBatchCorrectionAsync(access, output); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyArchivedBatchCorrectionAsync(ArchivedCorrectionAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        using var control = new HttpClient {BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(95)};
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
            await Until(() => school.Items.OfType<School>().Any(row => row.Id == access.SchoolId), "admin school appears");
            school.SelectedItem = school.Items.OfType<School>().Single(row => row.Id == access.SchoolId);
            var classes = Find<Button>(main, "OpenClassWorkspaceButton");
            await Until(() => classes.IsEffectivelyEnabled && classes.IsEffectivelyVisible, "class workspace available");
            Click(classes);
            Click(Find<Button>(main, "OpenTermPreparationButton"));
            var terms = await AcademicPageAsync<TermPreparationView>(main);
            var list = Find<ListBox>(terms, "PreparedTerms");
            await Until(() => list.Items.OfType<ManagedAcademicTerm>().Any(row => row.Id == access.ArchivedTermId), "archived term listed");
            list.SelectedItem = list.Items.OfType<ManagedAcademicTerm>().Single(row => row.Id == access.ArchivedTermId);
            var entry = Find<Button>(terms, "TermOrganizationImport");
            await Until(() => entry.IsEffectivelyEnabled && Equals(entry.Content, "批量更正"), "explicit archive entry");
            Click(entry);
            var org = await AcademicPageAsync<OrganizationImportView>(main);
            var grade = Find<ComboBox>(org, "OrgGrade");
            await Until(() => grade.Items.Count > 1, "archive catalog");
            Check(!Find<ComboBox>(org, "OrgTerm").IsEnabled &&
                Find<TextBlock>(org, "OrgScope").Text!.Contains(access.ActiveName), "archive and active terms are distinct");
            grade.SelectedItem = grade.Items.Cast<object>().Single(item => item.ToString()!.Contains("高二（G2）"));
            var original = await File.ReadAllTextAsync(access.File);
            org.LoadOrganizationText(original);
            org.LoadOrganizationText(original.Replace("\"code\":\"G2\",\"name\":\"高二\"", "\"code\":\"G1\",\"name\":\"高一\"", StringComparison.Ordinal));
            var misfile = Find<TextBlock>(org, "OrgFileMismatchText");
            Check(misfile.IsEffectivelyVisible && misfile.Text!.Contains("高一（G1）") && misfile.Text.Contains("高二（G2）") &&
                !Find<Button>(org, "OrgPreview").IsEnabled && Find<ListBox>(org, "OrgGroups").ItemCount == 1,
                "wrong file target provides both sides and keeps original draft");
            main.Width = 840;
            main.Height = 480;
            await CaptureWithFrame(main, output, "00-narrow-wrong-file-keeps-draft");
            await PressScoped(org, "OrgKeepOriginal");
            await PressScoped(org, "OrgExcel");
            var cells = Find<TextBox>(org, "OrgExcelText");
            cells.Text = "班级代码\t班名\t学科代码\t学科名称\t授课方式\t必修\nG2-C08\t高二（8）班\tPHY\t物理\t走班\t是";
            await PressScoped(org, "OrgApplyExcel");
            await Until(() => !cells.IsEffectivelyVisible && Find<ListBox>(org, "OrgClasses").ItemCount == 1, "Excel updates only the target archive table");
            var order = Find<NumericUpDown>(org, "OrgGradeOrder");
            order.Value = 2;
            var sections = Find<TabControl>(org, "OrgSections");
            sections.SelectedIndex = 1;
            var groups = Find<ListBox>(org, "OrgGroups");
            groups.SelectedIndex = 0;
            Find<CheckBox>(org, "OrgReplaceSources").IsChecked = true;
            var approval = Find<CheckBox>(org, "OrgApprove");
            var differences = Find<ListBox>(org, "OrgDifferences");
            Click(Find<Button>(org, "OrgPreview"));
            await Until(() => approval.IsEffectivelyEnabled &&
                differences.Items.OfType<OrganizationDifference>().Any(row => row.Action == "REMOVE"), "review explicit sources");
            var gradeDifference = differences.Items.OfType<OrganizationDifference>().Single(row => row.Key == "grade");
            differences.SelectedItem = gradeDifference;
            differences.ScrollIntoView(gradeDifference);
            await Until(() => differences.ContainerFromItem(gradeDifference) is Control, "grade difference row is realized");
            var gradeRow = (Control)differences.ContainerFromItem(gradeDifference)!;
            gradeRow.Focus();
            gradeRow.RaiseEvent(new KeyEventArgs {RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter});
            await Until(() => order.IsKeyboardFocusWithin, "narrow grade difference focuses the actual order input");
            await PressScoped(org, "OrgBackToReview");
            main.Width = 1160;
            main.Height = 780;
            await CaptureWithFrame(main, output, "01-wide-archive-removals");
            await Signal("web-change");

            main.Width = 840;
            main.Height = 480;
            approval.IsChecked = true;
            Click(Find<Button>(org, "OrgSave"));
            await Until(() => Find<TextBlock>(org, "OrgStatus").Text!.Contains("本次没有保存任何更改") &&
                differences.Items.OfType<OrganizationDifference>().Any(row => row.NeedsReview), "stale organization draft retains choices");
            await Signal("zero");
            await CaptureWithFrame(main, output, "02-narrow-source-conflict");
            differences.SelectedItem = differences.Items.OfType<OrganizationDifference>().First(row => row.NeedsReview);
            differences.RaiseEvent(new KeyEventArgs {RoutedEvent = InputElement.KeyDownEvent, Key = Key.F8});
            await Until(() => Find<TextBox>(org, "OrgSourceCodes").IsFocused, "F8 focuses the narrow source editor");

            Click(Find<Button>(org, "OrgPreview"));
            await Until(() => approval.IsEffectivelyEnabled && differences.Items.OfType<OrganizationDifference>()
                .Count(row => row.Action == "REMOVE") == 2, "updated preview lists both removals");
            approval.IsChecked = true;
            Click(Find<Button>(org, "OrgSave"));
            await Until(() => Find<TextBlock>(org, "OrgStatus").Text!.Contains("找回本次"), "lost response resolves exact organization receipt");
            await Signal("organization-only");
            await CaptureWithFrame(main, output, "03-narrow-organization-receipt");
            var results = Find<ListBox>(org, "OrgReceiptClasses");
            results.SelectedItem = results.Items.Cast<object>().Single(item => item.ToString()!.Contains("G2-C08"));
            await PressScoped(org, "OrgHistoryClass");
            var history = await AcademicPageAsync<TermClassManagementView>(main);
            await Until(() => Find<TermClassDirectoryView>(history, "StructureDirectory").Selected?.Id == access.ClassId &&
                Find<TextBlock>(history, "StructureTarget").Text?.Contains(access.ArchiveName) == true,
                "receipt takes the administrator to the historical class ID in the same HWND");
            await PressScoped(history, "StructureClose");
            await Until(() => Find<Button>(org, "OrgHistoryClass").IsFocused &&
                results.SelectedItem?.ToString()?.Contains("G2-C08") == true, "return restores the exact historical class receipt");
            results.SelectedItem = results.Items.Cast<object>().Single(item => item.ToString()!.Contains("A1"));
            Click(Find<Button>(org, "OrgTeacherAllocation"));

            var teacher = await WindowOf<TeacherAllocationWindow>();
            teacher.Width = 840;
            teacher.Height = 480;
            var existing = Find<ComboBox>(teacher, "AllocationExisting");
            await Until(() => existing.Items.OfType<AllocationAccount>().Any(row => row.Id == access.TeacherId), "existing teacher by account ID");
            Check(!Find<Button>(teacher, "AllocationJson").IsVisible && !Find<Button>(teacher, "AllocationAddTeacher").IsVisible &&
                Find<TextBlock>(teacher, "AllocationScope").Text!.Contains(access.ArchiveName), "archive access has no account creation and shows target term");
            existing.SelectedItem = existing.Items.OfType<AllocationAccount>().Single(row => row.Id == access.TeacherId);
            var editor = Find<Button>(teacher, "AllocationOpenEditor");
            if (editor.IsEffectivelyVisible) Click(editor);
            Click(Find<Button>(teacher, "AllocationAddAccess"));
            var target = teacher.GetVisualDescendants().OfType<ComboBox>().Single(item => AutomationProperties.GetName(item) == "目标教学空间");
            Check(target.SelectedItem?.ToString()?.Contains("A1") == true, "historical group is carried from the receipt");
            await Signal("disable-teacher-for-review");
            Click(Find<Button>(teacher, "AllocationPreviewButton"));
            var teacherReview = Find<ListBox>(teacher, "AllocationDifferences");
            await Until(() => teacherReview.Items.OfType<AllocationDifference>().Any(row => row.Path.EndsWith(".accountId") && row.Action == "BLOCK"),
                "stopped ordinary account is a scoped identity conflict");
            teacherReview.RaiseEvent(new KeyEventArgs {RoutedEvent = InputElement.KeyDownEvent, Key = Key.F8});
            await Until(() => Find<ListBox>(teacher, "AllocationTeachers").IsEffectivelyVisible &&
                teacher.GetVisualDescendants().OfType<TextBox>().Any(input => input.IsFocused && input.IsReadOnly),
                "teacher identity conflict restores the narrow table and focuses the read-only account");
            await Capture(teacher, output, "04a-narrow-teacher-identity-focus");
            await Signal("enable-teacher-for-review");
            Click(Find<Button>(teacher, "AllocationPreviewButton"));
            var approvedAccess = Find<CheckBox>(teacher, "AllocationApprove");
            await Until(() => approvedAccess.IsEffectivelyEnabled && Find<ListBox>(teacher, "AllocationDifferences").Items.OfType<AllocationDifference>()
                .Any(row => row.WorkspaceId == access.GroupId && row.After == "只读"), "only historic A1 has a direct read-only grant");
            await Capture(teacher, output, "04-narrow-teacher-preview");
            approvedAccess.IsChecked = true;
            Click(Find<Button>(teacher, "AllocationSave"));
            await Until(() => Equals(Find<Button>(teacher, "AllocationSave").Content, "按原编号重试") &&
                Find<Button>(teacher, "AllocationSave").IsEnabled, "missing original request can be retried only after read-only lookup");
            Click(Find<Button>(teacher, "AllocationSave"));
            await Until(() => Find<TextBlock>(teacher, "AllocationStatus").Text!.Contains("找回原回执"), "lost response resolves independent access receipt");
            await Signal("both-complete");
            teacher.Width = 1160;
            teacher.Height = 780;
            await Capture(teacher, output, "05-wide-teacher-receipt");
            await PressScoped(teacher, "AllocationClose");
            await Until(() => Find<Button>(org, "OrgTeacherAllocation").IsFocused &&
                results.SelectedItem?.ToString()?.Contains("A1") == true, "return to the exact historical receipt and row");
            Console.WriteLine("PASS: Windows archive scope, stale source changes, F8 return, two independent recovered receipts.");
            desktop.Shutdown(0);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (desktop.Windows.LastOrDefault(window => window.IsVisible) is { } failure)
                try { await Capture(failure, output, "desktop-failure"); } catch { }
            desktop.Shutdown(1);
        }

        async Task<T> WindowOf<T>() where T : Window
        {
            await Until(() => desktop.Windows.OfType<T>().Any(row => row.IsVisible), typeof(T).Name + " visible");
            return desktop.Windows.OfType<T>().Single(row => row.IsVisible);
        }
        async Task Signal(string mark)
        {
            using var response = await control.PostAsync("/__archive/" + mark, null);
            Check(response.IsSuccessStatusCode, "PostgreSQL and Edge checkpoint: " + mark);
        }
    }
}
