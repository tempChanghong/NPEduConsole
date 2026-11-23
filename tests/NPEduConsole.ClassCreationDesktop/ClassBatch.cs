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
    private sealed record ClassBatchAccess(string Origin, string SchoolId, string SchoolCode, string Username, string Password,
        string BatchGradeId, bool AllowClassBatchTestWrites, string? SelectedClassId, string[]? CreatedClassIds);

    private static int StartClassBatch(string fixturePath, string output, bool reopen)
    {
        var access = JsonSerializer.Deserialize<ClassBatchAccess>(File.ReadAllText(fixturePath), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Missing batch fixture.");
        Check(access.AllowClassBatchTestWrites && IsLoopback(access.Origin), "Use an isolated loopback batch fixture.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyClassBatchAsync(access, output, reopen); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyClassBatchAsync(ClassBatchAccess access, string output, bool reopen)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        try
        {
            var main = (MainWindow)desktop.MainWindow!;
            var schoolPicker = Find<ComboBox>(main, "SchoolPicker");
            var classPicker = Find<ListBox>(main, "ClassPicker");
            Find<TextBox>(main, "ServerAddress").Text = access.Origin;
            Find<TextBox>(main, "SchoolCode").Text = access.SchoolCode;
            Find<TextBox>(main, "Username").Text = access.Username;
            Find<TextBox>(main, "Password").Text = access.Password;
            Click(Find<Button>(main, "LoginButton"));
            await Until(() => schoolPicker.Items.Cast<School>().Any(item => item.Id == access.SchoolId), "manager login");
            schoolPicker.SelectedItem = schoolPicker.Items.Cast<School>().Single(item => item.Id == access.SchoolId);
            Click(Find<Button>(main, "OpenClassWorkspaceButton"));
            await Until(() => Find<Button>(main, "OpenClassCreationButton").IsEffectivelyVisible && classPicker.ItemCount > 0, "school class list");

            if (reopen)
            {
                var ids = access.CreatedClassIds ?? throw new InvalidDataException("Missing committed class IDs.");
                Check(ids.Length == 12 && ids.All(id => classPicker.Items.Cast<Classroom>().Any(room => room.Id == id)),
                    "A fresh Console process finds all 12 receipt IDs in the regular class list.");
                classPicker.SelectedItem = classPicker.Items.Cast<Classroom>().Single(room => room.Id == access.SelectedClassId);
                await Until(() => Find<Button>(main, "OpenClassRosterButton").IsEnabled, "reopened saved class");
                Click(Find<Button>(main, "OpenClassRosterButton"));
                var reopenedRoster = await WaitForRoster();
                await Until(() => reopenedRoster.GetVisualDescendants().OfType<TextBox>().Any(field => field.Text == "批量首份名单"),
                    "saved first roster in the new process");
                await Capture(main, output, "01-class-list");
                await Capture(reopenedRoster, output, "02-saved-roster");
                Click(Find<Button>(reopenedRoster, "CloseRosterButton"));
                await SignOut(main);
                Finish("PASS: restarted Windows Console found all 12 original IDs and reopened the saved roster by class ID.", 0);
                return;
            }

            Click(Find<Button>(main, "OpenClassCreationButton"));
            await Until(() => desktop.Windows.OfType<ClassCreationWindow>().Any(window => window.IsVisible), "class creation dialog");
            var dialog = desktop.Windows.OfType<ClassCreationWindow>().Single(window => window.IsVisible);
            var gradePicker = Find<ComboBox>(dialog, "CreationGrade");
            await Until(() => gradePicker.Items.Cast<ClassCreationGrade>().Any(grade => grade.Id == access.BatchGradeId), "high-school grade directory");
            gradePicker.SelectedItem = gradePicker.Items.Cast<ClassCreationGrade>().Single(grade => grade.Id == access.BatchGradeId);
            Click(Find<Button>(dialog, "OpenClassBatchButton"));
            var batch = Find<Grid>(dialog, "BatchFields");
            await Until(() => batch.IsVisible, "batch editing");
            Check(Find<TextBox>(dialog, "BatchNamePrefix").Text == "高一" &&
                Find<TextBox>(dialog, "BatchCodePrefix").Text == "G1-C", "The selected grade supplies both default prefixes.");
            Check(dialog.Width >= 1100 && dialog.Height >= 730, "batch workspace uses the school window size");
            Find<TextBox>(dialog, "CreationName").Text = "单班原草稿";
            dialog.Width = 840;
            dialog.Height = 480;
            Find<TextBox>(dialog, "BatchEndNumber").Text = "100";
            Click(Find<Button>(dialog, "BatchPreviewButton"));
            var longBatch = Find<ItemsControl>(dialog, "BatchRows");
            await Until(() => longBatch.ItemCount == 100 && HasText(longBatch, "高一100班"), "100-row editable preview");
            var last = CodeAt(longBatch, 100);
            last.Focus();
            last.BringIntoView();
            await Until(() => Find<ScrollViewer>(dialog, "BatchRowScroll").Offset.Y > 0 && last.IsFocused, "last editable row is focusable in the short window");
            await Capture(dialog, output, "00-narrow-100-rows");
            Check(FitsBatchWindow(dialog, Find<Grid>(dialog, "BatchColumnLabels")) &&
                FitsBatchWindow(dialog, Find<Border>(dialog, "BatchWorkspaceActions")), "headers and commands stay inside the short window after scrolling");
            Click(Find<Button>(dialog, "BatchCancelButton"));
            await AssertBatchFocusReturnAsync(dialog, "OpenClassBatchButton");
            Check(Find<TextBox>(dialog, "CreationName").Text == "单班原草稿", "return restores the original draft");
            Click(Find<Button>(dialog, "OpenClassBatchButton"));
            Find<TextBox>(dialog, "BatchEndNumber").Text = "12";
            Click(Find<Button>(dialog, "BatchPreviewButton"));
            var batchRows = Find<ItemsControl>(dialog, "BatchRows");
            await Until(() => batchRows.ItemCount == 12 && HasText(batchRows, "高一12班"), "12-row preview");
            await Capture(dialog, output, "01-preview");

            TextBox RowCode(int row) => CodeAt(batchRows, row);
            var seventh = RowCode(7);
            var eighth = RowCode(8);
            Check(seventh.Text == "G1-C07" && eighth.Text == "G1-C08", "Preview fields belong to rows 7 and 8.");
            var secondRow = RowCode(2).GetVisualAncestors().OfType<Grid>().First();
            secondRow.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked = false;
            eighth.Text = "g1-c07";
            var validate = Find<Button>(dialog, "BatchValidateButton");
            await Until(() => validate.IsEffectivelyEnabled, "batch verification action enabled for the preview");
            Click(validate);
            await Until(() => HasWord(batchRows, "重复"), "in-batch duplicate shown before any write");
            Check(!Find<Button>(dialog, "BatchSubmitButton").IsEnabled, "known local row errors disable saving");
            eighth.Text = "G1-C08";
            Click(validate);
            await Until(() => Feedback(dialog).Contains("核对通过"), "server revalidation after duplicate correction");
            var submit = Find<Button>(dialog, "BatchSubmitButton");
            await Until(() => submit.IsEnabled, "checked batch can be submitted");
            Click(submit);
            await Until(() => HasWord(batchRows, "本学期已有") && batchRows.ItemCount == 12, "server conflict on one preview row");
            Check(Feedback(dialog).Contains("第 7 行") && Feedback(dialog).Contains("本批未建班"),
                "The visible summary identifies row 7 and confirms no class was written.");
            var seventhRow = seventh.GetVisualAncestors().OfType<Border>().First(border => border.Classes.Contains("batch-row"));
            Check(HasWord(seventhRow, "G1-C07") && HasWord(seventhRow, "本学期已有"),
                "The server conflict appears on row 7.");
            Check(RowCode(1).Text == "G1-C01" && RowCode(12).Text == "G1-C12", "The first and last draft rows are retained.");
            await AssertBatchConflictFocusAsync(dialog, 7, "G1-C07");
            Check(!submit.IsEnabled && validate.IsEnabled, "a server row conflict disables saving and leaves revalidation available");
            await Capture(dialog, output, "02-row-07-conflict");
            dialog.Width = 840;
            dialog.Height = 480;
            Click(Find<Button>(dialog, "BatchLocateIssueButton"));
            await Until(() => FitsBatchWindow(dialog, seventh), "row 7 remains reachable at 840 by 480");
            Check(FitsBatchWindow(dialog, Find<Grid>(dialog, "BatchColumnLabels")) &&
                FitsBatchWindow(dialog, submit), "headers and commit remain visible while fixing row 7");
            await Capture(dialog, output, "02-row-07-conflict-compact");
            seventh.Text = "G1-C07-FIX";
            Check(!submit.IsEnabled, "row edits require a fresh successful check after a confirmed conflict");
            Click(validate);
            await Until(() => Feedback(dialog).Contains("核对通过"), "corrected row is now available");
            await Until(() => submit.IsEnabled, "corrected batch can be submitted");
            Click(submit);

            var checkReceipt = Find<Button>(dialog, "BatchVerifyReceiptButton");
            await Until(() => checkReceipt.IsEnabled && checkReceipt.IsVisible, "lost successful reply prompts receipt lookup");
            Check(!submit.IsEnabled && !batchRows.IsEffectivelyEnabled && batchRows.ItemCount == 12,
                "A lost acknowledgement locks the original payload and prevents direct re-submission.");
            await Capture(dialog, output, "03-awaiting-receipt");
            dialog.Close();
            await Until(() => Notice(main).Contains("上次批量开班"), "main window remembers the unconfirmed batch");
            Click(Find<Button>(main, "OpenClassCreationButton"));
            await Until(() => desktop.Windows.OfType<ClassCreationWindow>().Any(window => window.IsVisible), "reopen the batch dialog");
            dialog = desktop.Windows.OfType<ClassCreationWindow>().Single(window => window.IsVisible);
            batchRows = Find<ItemsControl>(dialog, "BatchRows");
            checkReceipt = Find<Button>(dialog, "BatchVerifyReceiptButton");
            await Until(() => checkReceipt.IsEnabled && checkReceipt.IsVisible && batchRows.ItemCount == 12,
                "the original draft and request are restored");
            Check(HasText(batchRows, "G1-C07-FIX"), "The corrected row is still present in the restored payload.");
            Click(checkReceipt);
            var results = Find<ListBox>(dialog, "BatchResults");
            await Until(() => results.ItemCount == 12 && Find<Grid>(dialog, "BatchResultPane").IsVisible, "12 committed rows recovered by receipt");
            results.SelectedIndex = 2;
            var openRoster = Find<Button>(dialog, "BatchOpenRosterButton");
            await Until(() => openRoster.IsEnabled, "class selected from the receipt");
            openRoster.BringIntoView();
            await Capture(dialog, output, "04-committed-receipt");
            Click(openRoster);
            var roster = await WaitForRoster();
            Check(classPicker.SelectedItem is Classroom { Name: "高一3班" }, "The chosen third result opens the expected class.");
            await PressScoped(roster, "OpenRosterImport");
            Find<TextBox>(roster, "RosterPasteText").Text = "001\t批量首份名单";
            Click(Find<Button>(roster, "ApplyRosterImport"));
            Click(Find<Button>(roster, "PreviewRosterButton"));
            await Until(() => Find<ListBox>(roster, "RosterChangesList").ItemCount == 1, "first student preview");
            Click(Find<Button>(roster, "ConfirmRosterSave"));
            await Until(() => Find<TextBlock>(roster, "RosterFeedback").Text?.Contains("已保存") == true, "first roster saved");
            await Capture(roster, output, "05-roster-saved");
            Click(Find<Button>(roster, "CloseRosterButton"));
            await SignOut(main);
            Finish("PASS: Windows Console preview, row 7 conflict, correction, lost acknowledgement, 12-row receipt, and first roster.", 0);

            async Task<ClassRosterView> WaitForRoster()
            {
                var roster = await AcademicPageAsync<ClassRosterView>(main);
                await Until(() => Find<Grid>(roster, "RosterEditorPane").IsEffectivelyVisible, "roster editor loaded");
                return roster;
            }
        }
        catch (Exception error)
        {
            Finish("FAIL: " + error, 1);
        }

        static TextBox CodeAt(ItemsControl rows, int row) => rows.GetVisualDescendants().OfType<TextBox>()
            .Single(field => AutomationProperties.GetAutomationId(field) == $"BatchClassCode-{row}");
        static bool HasText(Control control, string expected) =>
            control.GetVisualDescendants().OfType<TextBox>().Any(field => field.Text == expected);
        static bool HasWord(Control control, string expected) =>
            control.GetVisualDescendants().OfType<TextBlock>().Any(field => field.Text?.Contains(expected) == true);

        void Finish(string result, int status)
        {
            File.WriteAllText(Path.Combine(output, "result.txt"), result);
            Console.WriteLine(result);
            desktop.Shutdown(status);
        }
    }

    private static async Task SignOut(MainWindow main)
    {
        Click(main.GetVisualDescendants().OfType<Button>().Single(button => button.Content is "退出登录"));
        await Until(() => Find<Button>(main, "LoginButton").IsEffectivelyVisible, "logout");
    }
}
