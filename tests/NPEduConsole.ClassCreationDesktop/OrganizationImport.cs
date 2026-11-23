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
        string DraftName, string ActiveId, string OtherSchoolId, string DraftFile, string WrongGradeFile, bool AllowOrganizationTestWrites);

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
            var schools = Find<ComboBox>(main, "SchoolPicker");
            await Until(() => schools.Items.OfType<School>().Count() == 2, "both separately manageable schools are available");
            SelectSchool(access.SchoolId);
            await PressScoped(main, "OpenClassWorkspaceButton");
            await PressScoped(main, "OpenTermPreparationButton");
            var termManager = await AcademicPageAsync<TermPreparationView>(main);
            var terms = Find<ListBox>(termManager, "PreparedTerms");
            await Until(() => terms.Items.OfType<ManagedAcademicTerm>().Any(row => row.Id == access.DraftId), "existing draft shown in term manager");
            terms.SelectedItem = terms.Items.OfType<ManagedAcademicTerm>().Single(row => row.Id == access.DraftId);
            await PressScoped(termManager, "TermOrganizationImport");

            var draft = await AcademicPageAsync<OrganizationImportView>(main);
            var draftTerm = Find<ComboBox>(draft, "OrgTerm");
            await Until(() => draftTerm.SelectedItem is OrganizationTerm { Status: "DRAFT" } term &&
                term.Id == access.DraftId, "organization editor scopes the existing draft");
            Check(!draftTerm.IsEnabled && TopLevel.GetTopLevel(draft) == main, "the draft entry is locked to the original term in the same HWND");
            SelectGrade(draft);
            await PressScoped(draft, "OrgExample");
            var rows = Find<ListBox>(draft, "OrgClasses");
            Check(rows.ItemCount == 7 && Status(draft).Contains("示例"), "real action loads seven-class example");
            var groupTitle = Find<TextBlock>(draft, "OrgGroupTitle");
            string priorTitle = groupTitle.Text ?? "";

            main.Width = 840;
            main.Height = 480;
            await Until(() => main.ClientSize is { Width: <= 845, Height: <= 485 }, "compact main window geometry");
            await CancelOrganizationPickerAsync(main, Find<Button>(draft, "OrgOpenFile"));
            Check(rows.ItemCount == 7 && groupTitle.Text == priorTitle && Find<TextBox>(draft, "OrgGradeCode").Text == "G2",
                "cancelling the system picker preserves the edited table and its selected target");
            draft.LoadOrganizationText(await File.ReadAllTextAsync(access.WrongGradeFile));
            var wrongTarget = Find<TextBlock>(draft, "OrgFileMismatchText");
            Check(Find<TextBox>(draft, "OrgGradeCode").Text == "G2" && rows.ItemCount == 7 &&
                wrongTarget.Text?.Contains("高一（G1）") == true && wrongTarget.Text.Contains("高二（G2）") &&
                !Find<Button>(draft, "OrgPreview").IsEnabled, "wrong file leaves the original seven rows and exact target untouched");
            await Signal("wrong-grade-zero-write");
            await CaptureWithFrame(main, output, "00-compact-grade-declaration-blocked");

            var file = await File.ReadAllTextAsync(access.DraftFile);
            draft.LoadOrganizationText(file);
            await Until(() => Find<Button>(draft, "OrgCancelReplacement").IsFocused, "matching file asks before replacing the unsaved table");
            Check(rows.ItemCount == 7 && groupTitle.Text == priorTitle, "on-screen original remains untouched during confirmation");
            await CaptureWithFrame(main, output, "00a-compact-confirm-replacement");
            await PressScoped(draft, "OrgCancelReplacement");
            Check(rows.ItemCount == 7 && groupTitle.Text == priorTitle, "cancel restores the original worksheet");
            draft.LoadOrganizationText(file);
            await PressScoped(draft, "OrgConfirmReplacement");
            Check(rows.ItemCount == 7 && Find<ListBox>(draft, "OrgGroups").ItemCount == 1, "confirmed file supplies its seven classes and course group");
            await PressScoped(draft, "OrgPreview");
            var differences = Find<ListBox>(draft, "OrgDifferences");
            await Until(() => differences.Items.OfType<OrganizationDifference>().Any(row => row.Action == "BLOCK" && row.Object.Contains("09")),
                "nine follows administrative class and cannot be selected for A1");
            Check(!Find<Button>(draft, "OrgSave").IsEffectivelyVisible, "the conflict has no active commit button");
            await CaptureWithFrame(main, output, "01-compact-source-blocked");
            main.Width = 1160;
            main.Height = 780;
            await Until(() => main.ClientSize.Width >= 1150, "wide main window geometry");
            var blockedRow = differences.Items.OfType<OrganizationDifference>().Single(row => row.Action == "BLOCK" && row.Object.Contains("09"));
            differences.SelectedItem = blockedRow;
            await VisibleIssue(blockedRow);
            await CaptureWithFrame(main, output, "01a-wide-source-blocked");
            main.Width = 840;
            main.Height = 480;
            await Until(() => main.ClientSize.Width <= 845, "same issue at compact width");
            await VisibleIssue(blockedRow);
            await CaptureWithFrame(main, output, "01b-compact-source-blocked-selected");
            main.Width = 1160;
            main.Height = 780;
            await Until(() => main.ClientSize.Width >= 1150, "same issue after widening again");
            await VisibleIssue(blockedRow);

            async Task VisibleIssue(OrganizationDifference issue)
            {
                differences.ScrollIntoView(issue);
                await Until(() => ReferenceEquals(differences.SelectedItem, issue) &&
                    differences.GetVisualDescendants().OfType<TextBlock>().Any(block =>
                        block.IsEffectivelyVisible && block.Text?.Contains(issue.Object) == true &&
                        block.Bounds.Width > 0 && block.Bounds.Height > 0),
                    "selected issue remains readable across the width change");
            }

            var source = Find<TextBox>(draft, "OrgSourceCodes");
            differences.Focus();
            KeyPress(differences, Key.F8);
            await Until(() => source.IsFocused && source.IsEffectivelyVisible && source.Text?.Contains("G2-C09") == true,
                "F8 opens the exact wide source cell");
            await PressScoped(draft, "OrgBackToReview");
            Check(differences.SelectedItem is OrganizationDifference { Action: "BLOCK" } &&
                Find<ComboBox>(draft, "OrgDifferenceFilter").SelectedIndex == 4, "returns to the same issue and filter");
            main.Width = 840;
            main.Height = 480;
            await Until(() => main.ClientSize.Width <= 845 && differences.IsKeyboardFocusWithin, "narrow review restores the keyboard focus");
            KeyPress((Control)TopLevel.GetTopLevel(differences)!.FocusManager!.GetFocusedElement()!, Key.Enter);
            await Until(() => source.IsFocused && source.IsEffectivelyVisible, "Enter opens the exact narrow source cell");
            await CaptureWithFrame(main, output, "02-compact-focused-source");
            var rear = Find<StackPanel>(draft, "OrgSources").Children.OfType<CheckBox>().Single(check =>
                check.Content?.ToString()?.Contains("G2-C09") == true);
            rear.IsChecked = false;
            Check(source.Text == "G2-C07，G2-C08", "removing ninth class retains seventh and eighth");
            await PressScoped(draft, "OrgBackGroupTable");
            await Until(() => Find<ListBox>(draft, "OrgGroups").IsEffectivelyVisible, "compact return restores the teaching group table");
            await PressScoped(draft, "OrgPreview");
            await Until(() => Find<CheckBox>(draft, "OrgApprove").IsEffectivelyEnabled && Status(draft).Contains("请核对差异"),
                "corrected draft computes a fresh server preview");
            await Signal("legacy-change");
            await Commit(draft);
            await Until(() => differences.Items.OfType<OrganizationDifference>().Any(row => row.Key == "class:G2-C08" && row.NeedsReview) &&
                Status(draft).Contains("本次没有保存任何更改"), "old web change invalidates the entire preview and marks eight");
            await Signal("stale-zero-write");
            var staleRow = differences.Items.OfType<OrganizationDifference>().First(row =>
                row.Key == "class:G2-C08" && row.NeedsReview && !string.IsNullOrWhiteSpace(row.Current) && !string.IsNullOrWhiteSpace(row.After));
            differences.SelectedItem = staleRow;
            await VisibleIssue(staleRow);
            await CaptureWithFrame(main, output, "03-compact-stale-eighth");
            main.Width = 1160;
            main.Height = 780;
            await Until(() => main.ClientSize.Width >= 1150, "same stale difference at full width");
            await VisibleIssue(staleRow);
            await CaptureWithFrame(main, output, "04-wide-stale-eighth");

            await PressScoped(draft, "OrgPreview");
            await Commit(draft);
            await Until(() => Find<Button>(draft, "OrgVerify").IsEffectivelyEnabled && Status(draft).Contains("保留原请求"),
                "dropped write reply and dropped first read leave the original receipt pending");
            await Signal("draft-awaiting");
            await PressScoped(draft, "OrgClose");
            await Until(() => Find<Button>(termManager, "TermOrganizationImport").IsFocused, "draft entry regains focus");
            await PressScoped(termManager, "TermBack");
            await Until(() => !termManager.IsEffectivelyVisible, "returned to the normal class workspace");

            var active = await OpenNormal(access.ActiveId);
            SelectGrade(active);
            var activeClasses = Find<ListBox>(active, "OrgClasses");
            var resume = Find<WrapPanel>(active, "OrgPendingActions").Children.OfType<Button>()
                .Single(button => AutomationProperties.GetAutomationId(button)?.StartsWith("OrgResumePending-") == true);
            Check(resume.Content?.ToString()?.Contains(access.DraftName) == true && activeClasses.ItemCount == 0,
                "the current entry offers the pending spring request without switching the active workspace");
            await PressScoped(active, "OrgExcel");
            var excel = Find<TextBox>(active, "OrgExcelText");
            excel.Text = "G2-C99\t不应写入";
            Check(TopLevel.GetTopLevel(excel) == main, "Excel entry is inline within the main window");
            await PressScoped(active, "OrgCancelExcel");
            await Until(() => activeClasses.ItemCount == 0 && Find<Button>(active, "OrgExcel").IsFocused, "cancelling paste keeps the table empty");
            await PressScoped(active, "OrgExcel");
            excel.Text = "班级代码\t班名\t学科代码\t学科名称\t授课方式\t必修\t学生自选\nG2-C15\t高二（15）班\tPHY\t物理\t随班\t是\t是";
            await PressScoped(active, "OrgApplyExcel");
            await Until(() => activeClasses.ItemCount == 1, "inline Excel keeps the selected active term and the intended class");
            await Signal("active-draft-before-recovery");

            Click(resume);
            var recovery = await AcademicPageAsync<OrganizationImportView>(main);
            Check(!ReferenceEquals(active, recovery), "explicit recovery opens a separate main-window page");
            var result = Find<ListBox>(recovery, "OrgReceiptClasses");
            await Until(() => Status(recovery).Contains("找回本次") && result.ItemCount == 8,
                "recovery reads the exact original receipt by request ID");
            await Signal("draft-applied");
            await CaptureWithFrame(main, output, "05-draft-receipt");

            await PressScoped(recovery, "OrgContinue");
            var check = await AcademicPageAsync<TermPreparationView>(main);
            var heading = Find<TextBlock>(check, "TermHeading");
            await Until(() => heading.Text?.Contains(access.DraftName) == true && heading.Text.Contains("启用前检查") &&
                Find<TextBlock>(check, "TermCheckStatus").Text?.Contains("正在读取") == false, "receipt opens exact draft readiness within the main HWND");
            await CaptureWithFrame(main, output, "06-draft-term-readiness");
            await PressScoped(check, "TermBack");
            await Until(() => Find<Button>(recovery, "OrgContinue").IsFocused && result.ItemCount == 8,
                "return restores the same receipt and action focus");

            await PressScoped(recovery, "OrgBackToTable");
            await PressScoped(recovery, "OrgPreview");
            var repeated = Find<ListBox>(recovery, "OrgDifferences");
            await Until(() => Status(recovery).Contains("已与服务器一致") && repeated.ItemCount > 0,
                "the same committed rows preview as already consistent");
            Check(repeated.Items.OfType<OrganizationDifference>().All(row => row.Action == "KEEP"), "repeat preview has no writes");
            await Signal("repeat-kept");
            await PressScoped(recovery, "OrgClose");
            await Until(() => active.IsEffectivelyVisible && ((OrganizationTerm)Find<ComboBox>(active, "OrgTerm").SelectedItem!).Id == access.ActiveId,
                "return restores the original active workspace");
            Check(activeClasses.ItemCount == 1 && activeClasses.GetVisualDescendants().OfType<TextBox>().Any(box => box.Text == "G2-C15"),
                "recovery leaves the active worksheet unchanged");

            await PressScoped(active, "OrgPreview");
            await Commit(active);
            var activeReceipt = Find<ListBox>(active, "OrgReceiptClasses");
            await Until(() => Status(active).Contains("由服务器确认") && activeReceipt.ItemCount == 1, "active term import confirmed");
            await Signal("current-applied");
            await CaptureWithFrame(main, output, "07-current-receipt");
            await PressScoped(active, "OrgTeacherAllocation");
            var allocation = await TaskWindow<TeacherAllocationWindow>();
            await Until(() => Find<TextBlock>(allocation, "AllocationScope").Text?.Contains("秋季学期") == true,
                "organization receipt opens teacher allocation in the exact active term");
            Check(Find<ComboBox>(allocation, "AllocationGrade").SelectedItem?.ToString()?.Contains("G2") == true,
                "teacher allocation receives the original grade ID");
            await PressScoped(allocation, "AllocationClose");
            await Until(() => Find<Button>(active, "OrgTeacherAllocation").IsFocused && activeReceipt.SelectedItem is not null,
                "closing teacher allocation returns to the same selected receipt");
            await PressScoped(active, "OrgContinue");
            var teaching = await AcademicPageAsync<TeachingView>(main);
            var subjects = Find<ComboBox>(teaching, "TeachingSubjectPicker");
            await Until(() => subjects.Items.OfType<TeachingSubject>().Any(row => row.Name == "物理") &&
                Find<TextBlock>(teaching, "TeachingContext").Text?.Contains("高二（15）班") == true,
                "teaching resolves the new class and real physics subject in the main window");
            await CaptureWithFrame(main, output, "08-current-teaching");
            await PressScoped(teaching, "CloseTeachingButton");
            await Until(() => Find<Button>(active, "OrgContinue").IsFocused, "teaching returns to the original class receipt");
            await PressScoped(active, "OrgClose");
            await Until(() => Find<Button>(main, "OpenOrganizationImportButton").IsFocused, "return restores the regular route anchor");

            await Signal("arm-delayed-catalog");
            await PressScoped(main, "OpenOrganizationImportButton");
            var delayed = await AcademicPageAsync<OrganizationImportView>(main);
            await Signal("await-delayed-catalog");
            SelectSchool(access.OtherSchoolId);
            await Until(() => !delayed.IsEffectivelyVisible && (schools.SelectedItem as School)?.Id == access.OtherSchoolId, "switch cancels the old work page");
            await Signal("release-delayed-catalog");
            await Signal("late-catalog-returned");
            Check(!delayed.IsEffectivelyVisible && Find<ListBox>(delayed, "OrgClasses").ItemCount == 0 &&
                !main.GetVisualDescendants().OfType<OrganizationImportView>().Any(view => view.IsEffectivelyVisible),
                "late original-school success cannot repopulate the document");

            SelectSchool(access.SchoolId);
            await PressScoped(main, "OpenClassWorkspaceButton");
            var revoked = await OpenNormal(access.ActiveId);
            SelectGrade(revoked);
            await PressScoped(revoked, "OrgExample");
            await Signal("revoke-active-admin");
            await PressScoped(revoked, "OrgPreview");
            await Until(() => !revoked.IsEffectivelyVisible && schools.Items.OfType<School>().All(row => row.Id != access.SchoolId),
                "an actual server 403 clears the old school and closes import");
            Check(Find<ListBox>(revoked, "OrgClasses").ItemCount == 0 && Find<ListBox>(revoked, "OrgReceiptClasses").ItemCount == 0 &&
                schools.Items.OfType<School>().Any(row => row.Id == access.OtherSchoolId), "revocation clears draft and receipt and retains independent school access");
            await Signal("revoked-zero-write");
            Console.WriteLine("PASS: one Windows HWND, all organization stages, exact same-school receipt, stale and late reads, revoked school, precise term/teacher destinations.");
            desktop.Shutdown(0);

            void SelectSchool(string id) => schools.SelectedItem = schools.Items.OfType<School>().Single(row => row.Id == id);
            async Task<OrganizationImportView> OpenNormal(string expectedTerm)
            {
                await PressScoped(main, "OpenOrganizationImportButton");
                var view = await AcademicPageAsync<OrganizationImportView>(main);
                await Until(() => Find<ComboBox>(view, "OrgTerm").SelectedItem is OrganizationTerm term && term.Id == expectedTerm &&
                    Find<Button>(view, "OrgOpenFile").IsEffectivelyEnabled, "normal entry has the actual active term");
                return view;
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            try { await CaptureWithFrame(main, output, "desktop-organization-failure"); } catch { }
            desktop.Shutdown(1);
        }

        async Task<T> TaskWindow<T>() where T : Window
        {
            await Until(() => desktop.Windows.OfType<T>().Any(row => row.IsVisible), typeof(T).Name + " is visible");
            return desktop.Windows.OfType<T>().Single(row => row.IsVisible);
        }

        async Task Signal(string mark)
        {
            using var response = await control.PostAsync("/__org/" + mark, null);
            Check(response.IsSuccessStatusCode, "isolated PostgreSQL checkpoint: " + mark);
        }

        static async Task Commit(OrganizationImportView view)
        {
            var approve = Find<CheckBox>(view, "OrgApprove");
            await Until(() => approve.IsEffectivelyVisible && approve.IsEffectivelyEnabled, "review is ready to be confirmed");
            approve.IsChecked = true;
            await PressScoped(view, "OrgSave");
        }

        static void SelectGrade(OrganizationImportView view)
        {
            var choice = Find<ComboBox>(view, "OrgGrade");
            choice.SelectedItem = choice.Items.OfType<object>().Single(row => row.ToString()?.Contains("高二（G2）") == true);
        }

        static string Status(Control view) => Find<TextBlock>(view, "OrgStatus").Text ?? "";
        static void KeyPress(Control target, Key key) =>
            target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
    }
}
