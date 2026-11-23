using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.VisualTree;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private static async Task VerifyGradeAsync(Access access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        try
        {
            var main = (MainWindow)desktop.MainWindow!;
            var schools = Find<ComboBox>(main, "SchoolPicker");
            var classes = Find<ListBox>(main, "ClassPicker");
            var entry = Find<Button>(main, "OpenClassCreationButton");
            Find<TextBox>(main, "ServerAddress").Text = access.LostReplyOrigin;
            Find<TextBox>(main, "SchoolCode").Text = access.SchoolCode;
            Find<TextBox>(main, "Username").Text = access.Username;
            Find<TextBox>(main, "Password").Text = access.Password;
            Click(Find<Button>(main, "LoginButton"));
            await Until(() => schools.Items.Cast<School>().Any(school => school.Id == access.ZeroGradeSchoolId), "administrator sees the zero-grade school");
            schools.SelectedItem = schools.Items.Cast<School>().Single(school => school.Id == access.ZeroGradeSchoolId);
            await Until(() => Find<TextBlock>(main, "ClassListMessage").Text == "该学校暂无可查看班级。", "zero-class list loaded");
            Check(classes.ItemCount == 0 && entry.IsEnabled, "zero grades and zero classes retain the class-creation entry");
            var dialog = await OpenCreation();
            var gradePicker = Find<ComboBox>(dialog, "CreationGrade");
            Check(gradePicker.ItemCount == 0 && !Find<Button>(dialog, "SubmitClassCreationButton").IsEnabled,
                "first class waits for an actual grade");
            Click(Find<Button>(dialog, "OpenGradeCreationButton"));
            FillGrade(dialog, " g-first ", "首建年级");
            await Capture(dialog, output, "01-zero-grades");
            Click(GradeAction(dialog));
            await Until(() => GradeAction(dialog) is { IsEnabled: true, Content: "核对结果" }, "lost grade receipt offers read-only check");
            Check(!Find<TextBox>(dialog, "NewGradeCode").IsEnabled && gradePicker.SelectedItem is null,
                "receipt must be verified before updating the target list or editing the same request");
            await Capture(dialog, output, "02-check-original-request");
            dialog.Close();
            await Until(() => Notice().Contains("结果待核对"), "pending creation survives closing the dialog");
            dialog = await OpenCreation(pending: true);
            Check(Find<TextBox>(dialog, "NewGradeCode").Text == "G-FIRST" && GradeAction(dialog).Content is "核对结果", "exact normalized request restored");
            Click(GradeAction(dialog));
            gradePicker = Find<ComboBox>(dialog, "CreationGrade");
            await Until(() => gradePicker.SelectedItem is ClassCreationGrade { Code: "G-FIRST", Name: "首建年级" } && gradePicker.IsEffectivelyEnabled,
                "authoritative grade receipt selects the exact directory row");
            string newGradeId = ((ClassCreationGrade)gradePicker.SelectedItem!).Id;
            Check(gradePicker.ItemCount == 1 && Feedback(dialog).Contains("已创建并选中") && !Find<StackPanel>(dialog, "GradeCreationFields").IsVisible,
                "grade creation returns directly to class fields");
            Find<TextBox>(dialog, "CreationName").Text = "首建年级一班";
            Find<TextBox>(dialog, "CreationCode").Text = "g-first-01";
            await Capture(dialog, output, "03-first-class");
            Click(Find<Button>(dialog, "SubmitClassCreationButton"));
            var roster = await AcademicPageAsync<ClassRosterView>(main);
            await Until(() => Find<Grid>(roster, "RosterEditorPane").IsVisible, "class opened its roster");
            Check(classes.SelectedItem is Classroom { Name: "首建年级一班", GradeName: "首建年级" }, "new class and grade visible in the real workspace");
            await PressScoped(roster, "OpenRosterImport");
            Find<TextBox>(roster, "RosterPasteText").Text = "001\t首班学生";
            Click(Find<Button>(roster, "ApplyRosterImport"));
            Click(Find<Button>(roster, "PreviewRosterButton"));
            await Until(() => Find<ListBox>(roster, "RosterChangesList").ItemCount == 1, "first roster preview");
            Click(Find<Button>(roster, "ConfirmRosterSave"));
            await Until(() => Find<TextBlock>(roster, "RosterFeedback").Text?.Contains("已保存") == true, "first roster persisted");
            Click(Find<Button>(roster, "CloseRosterButton"));
            await Until(() => Notice().Contains("本次名单录入结果已确认"), "class and roster confirmed");

            dialog = await OpenCreation();
            Check(Find<ComboBox>(dialog, "CreationGrade").SelectedItem is ClassCreationGrade grade && grade.Id == newGradeId,
                "the existing grade is still selectable");
            Click(Find<Button>(dialog, "OpenGradeCreationButton"));
            FillGrade(dialog, "g-first", "不能误选的重码年级");
            Click(GradeAction(dialog));
            await Until(() => Feedback(dialog).Contains("请换一个年级代码") && Find<TextBox>(dialog, "NewGradeCode").IsEnabled,
                "a duplicate grade code offers a correctable error");
            await Capture(dialog, output, "04-duplicate-code");

            FillGrade(dialog, "STALE-GRADE", "原学期草稿");
            await ActivateNextTermAsync(access);
            Click(GradeAction(dialog));
            await Until(() => Feedback(dialog).Contains("当前学期已变更") && !GradeAction(dialog).IsEnabled,
                "the form scoped to the former term cannot write");
            Click(Find<Button>(dialog, "ReloadCreationContext"));
            await Until(() => Find<TextBlock>(dialog, "CreationTerm").Text == "新启用学期" && GradeAction(dialog).IsEnabled,
                "explicit refresh observes the new term");
            Check(Find<TextBox>(dialog, "NewGradeCode").Text == "STALE-GRADE" && Find<ComboBox>(dialog, "CreationGrade").SelectedItem is null,
                "term refresh preserves input for review and clears the old selected grade");
            await Capture(dialog, output, "05-term-changed");
            dialog.Close();
            Click(main.GetVisualDescendants().OfType<Button>().Single(button => button.Content is "退出登录"));
            await Until(() => Find<Button>(main, "LoginButton").IsEffectivelyVisible, "sign out");
            End("PASS: Windows Console + real KV/PostgreSQL, zero grades → receipt verified by GET → exact grade selected → first class and roster; duplicate code and stale term refused.", 0);

            async Task<ClassCreationWindow> OpenCreation(bool pending = false)
            {
                Click(entry);
                await Until(() => desktop.Windows.OfType<ClassCreationWindow>().Any(window => window.IsVisible &&
                    (pending ? GradeAction(window).IsEnabled && GradeAction(window).Content is "核对结果" : Find<Button>(window, "OpenGradeCreationButton").IsEnabled)),
                    "enter the grade creation flow through New Administrative Class");
                return desktop.Windows.OfType<ClassCreationWindow>().Single(window => window.IsVisible);
            }

            string Notice() => Find<TextBlock>(main, "ClassCreationNoticeText").Text ?? "";
        }
        catch (Exception error) { End("FAIL: " + error, 1); }

        void End(string message, int code)
        {
            File.WriteAllText(Path.Combine(output, "result.txt"), message);
            Console.WriteLine(message);
            desktop.Shutdown(code);
        }
    }

    private static Button GradeAction(ClassCreationWindow dialog) => Find<Button>(dialog, "SubmitGradeCreationButton");
    private static string Feedback(ClassCreationWindow dialog) => Find<TextBlock>(dialog, "CreationFeedback").Text ?? "";
    private static void FillGrade(ClassCreationWindow dialog, string code, string name)
    {
        Find<TextBox>(dialog, "NewGradeCode").Text = code;
        Find<TextBox>(dialog, "NewGradeName").Text = name;
    }

    private static async Task ActivateNextTermAsync(Access access)
    {
        using var http = new HttpClient { BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(10) };
        using var login = await http.PostAsJsonAsync("/accounts/local/login", new { access.SchoolCode, access.Username, access.Password });
        login.EnsureSuccessStatusCode();
        var response = await login.Content.ReadFromJsonAsync<JsonElement>();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", response.GetProperty("data").GetProperty("access_token").GetString());
        using var change = await http.PostAsJsonAsync($"/api/v2/admin/terms/{Uri.EscapeDataString(access.NextTermId!)}/status",
            new { status = "ACTIVE", force = true, rebindScreens = false });
        change.EnsureSuccessStatusCode();
        using var logout = await http.PostAsync("/accounts/logout", null);
        logout.EnsureSuccessStatusCode();
    }
}
