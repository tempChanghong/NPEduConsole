using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private sealed record GradeRenameAccess(string Origin, string LostReplyOrigin, string SchoolCode, string Username,
        string Password, string SchoolId, string TermId, string GradeId, string GradeName, string ClassId,
        string SecondUsername, string SecondPassword, bool AllowGradeRenameTestWrites, string? ShiftTermOrigin = null);

    private static int StartGradeRename(string fixturePath, string output)
    {
        var access = JsonSerializer.Deserialize<GradeRenameAccess>(File.ReadAllText(fixturePath), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Could not read the grade-rename fixture.");
        Check(access.AllowGradeRenameTestWrites, "The grade-rename fixture must explicitly allow desktop writes.");
        Check(IsLoopback(access.Origin) && IsLoopback(access.LostReplyOrigin) &&
              (access.ShiftTermOrigin is null || IsLoopback(access.ShiftTermOrigin)),
            "Use isolated loopback origins for the grade-rename desktop test.");
        Check(!string.IsNullOrWhiteSpace(access.SchoolCode) && !string.IsNullOrWhiteSpace(access.Username) &&
              !string.IsNullOrWhiteSpace(access.Password) && !string.IsNullOrWhiteSpace(access.SecondUsername) &&
              !string.IsNullOrWhiteSpace(access.SecondPassword), "The grade-rename fixture needs both manager accounts.");
        Check(!string.IsNullOrWhiteSpace(access.SchoolId) && !string.IsNullOrWhiteSpace(access.TermId) &&
              !string.IsNullOrWhiteSpace(access.GradeId) && !string.IsNullOrWhiteSpace(access.GradeName) &&
              !string.IsNullOrWhiteSpace(access.ClassId), "The grade-rename fixture needs the grade, term and class IDs.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyGradeRenameAsync(access, output); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static bool IsLoopback(string? origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static async Task VerifyGradeRenameAsync(GradeRenameAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        try
        {
            var primary = (MainWindow)desktop.MainWindow!;
            var classes = await LoginAndSelectAsync(primary, access.Origin, access.Username, access.Password);

            var second = await NewMainAsync(access.Origin, access.SecondUsername, access.SecondPassword);
            var staleDialog = await OpenCreationAsync(second, access.GradeId);
            var staleVersion = SelectedGrade(staleDialog)?.UpdatedAt;

            var dialog = await OpenCreationAsync(primary, access.GradeId);
            var original = SelectedGrade(dialog) ?? throw new InvalidOperationException("No grade is selected.");
            Check(original.Name == access.GradeName && original.UpdatedAt is not null,
                "The current grade exposes a version token before renaming.");
            Check(staleVersion == original.UpdatedAt,
                "The second manager opened the same grade version before the first rename.");
            FillDraft(dialog, "改名保留班草稿", "KEEP-DRAFT-01");
            await Capture(dialog, output, "01-before-rename");

            string firstRename = access.GradeName + "-甲";
            await RenameAsync(dialog, firstRename);
            await Until(() => !Find<StackPanel>(dialog, "GradeRenameFields").IsVisible &&
                SelectedGrade(dialog) is { } grade && grade.Id == access.GradeId && grade.Name == firstRename,
                "the first rename is confirmed by ID and the editor closes");
            Check(Feedback(dialog).Contains("已按 ID 恢复选中"), "Successful rename reports the ID-bound selection.");
            Check(DraftName(dialog) == "改名保留班草稿" && DraftCode(dialog) == "KEEP-DRAFT-01",
                "Renaming preserves the class name and code draft.");
            await Capture(dialog, output, "02-renamed");
            dialog.Close();
            await Until(() => Notice(primary).Contains(firstRename) && classes.SelectedItem is Classroom { Id: var id } &&
                id == access.ClassId, "main page refreshes the grade name and preserves the selected class");
            Check(classes.SelectedItem is Classroom { GradeName: var gradeName } && gradeName == firstRename,
                "The selected class shows the new grade name after the main-page refresh.");
            await Capture(primary, output, "03-main-preserved");

            dialog = await OpenCreationAsync(primary, access.GradeId);
            Check(SelectedGrade(dialog)?.Name == firstRename, "Reopening the creation window shows the server grade name.");
            dialog.Close();
            await Until(() => !dialog.IsVisible, "reopened creation window closes");

            Click(Find<Button>(primary, "OpenClassRosterButton"));
            await Until(() => desktop.Windows.OfType<ClassRosterWindow>().Any(window => window.IsVisible &&
                Find<Grid>(window, "RosterEditorPane").IsVisible), "the selected class roster loads");
            var roster = desktop.Windows.OfType<ClassRosterWindow>().Single();
            await Until(() => Find<TextBlock>(roster, "RosterCount").Text?.Contains("1 人") == true,
                "the existing student remains in the roster");
            Check(roster.GetVisualDescendants().OfType<TextBox>().Any(text => text.Text == "模拟学生甲"),
                "Renaming does not remove the seeded student.");
            roster.Close();
            await Until(() => !roster.IsVisible, "roster closes");

            await RenameAsync(staleDialog, firstRename + "-乙");
            await Until(() => Feedback(staleDialog).Contains("服务器当前名称") && Feedback(staleDialog).Contains(firstRename),
                "the stale second-manager window reads the server name after a conflict");
            Check(Feedback(staleDialog).Contains("与本次输入") && Feedback(staleDialog).Contains("不符"),
                "The stale window distinguishes the attempted name from the server name.");
            Check(Find<TextBlock>(staleDialog, "GradeRenameServerName").Text?.Contains(firstRename) == true,
                "The stale window visibly shows the server's current grade name.");
            Check(SelectedGrade(staleDialog)?.Name == firstRename, "The stale window selects the server row by ID.");
            Check(Find<Button>(staleDialog, "SubmitGradeRenameButton") is { IsEnabled: true, Content: "再次确认修改" },
                "A conflict requires an explicit second confirmation with the fresh version.");
            await Capture(staleDialog, output, "04-second-admin-conflict");
            staleDialog.Close();
            await Until(() => Notice(second).Contains("待核对") || Notice(second).Contains("冲突"), "second main reports the conflict");
            await SignOutAndCloseAsync(second);

            var lost = await NewMainAsync(access.LostReplyOrigin, access.Username, access.Password);
            var lostDialog = await OpenCreationAsync(lost, access.GradeId);
            Check(SelectedGrade(lostDialog)?.Name == firstRename, "The lost-reply window starts from the current server name.");
            string lostName = firstRename + "-丢包";
            await RenameAsync(lostDialog, lostName);
            await Until(() => Feedback(lostDialog).Contains("与本次输入一致") &&
                Feedback(lostDialog).Contains("不能证明本次修改已生效"), "a lost reply is read back read-only");
            Check(Find<TextBlock>(lostDialog, "GradeRenameServerName").Text?.Contains("与本次输入一致") == true,
                "The readback explicitly distinguishes a matching server name from proof of this write.");
            Check(SelectedGrade(lostDialog)?.Name == lostName, "The readback refreshes the selected grade by ID.");
            Check(!Find<Button>(lostDialog, "SubmitGradeRenameButton").IsEnabled,
                "A matching readback cannot automatically resend the PATCH.");
            Check(!Find<Button>(lostDialog, "CheckGradeRenameButton").IsVisible,
                "A successful readback closes the blocking check state.");
            await Capture(lostDialog, output, "05-lost-reply-readback");
            lostDialog.Close();
            await Until(() => Notice(lost).Contains("待核对"), "the lost window keeps the uncertain result visible");
            await SignOutAndCloseAsync(lost);

            Check(access.ShiftTermOrigin is not null, "The fixture needs ShiftTermOrigin for the stale-term case.");
            var shifted = await NewMainAsync(access.ShiftTermOrigin!, access.Username, access.Password);
            var shiftedDialog = await OpenCreationAsync(shifted, access.GradeId);
            Check(SelectedGrade(shiftedDialog)?.Name == lostName, "The stale-term window starts in the original active term.");
            FillDraft(shiftedDialog, "学期切换班草稿", "TERM-DRAFT-01");
            await RenameAsync(shiftedDialog, "不应写入的年级名");
            await Until(() => Feedback(shiftedDialog).Contains("当前启用学期已改变") &&
                !Find<Button>(shiftedDialog, "SubmitGradeRenameButton").IsEnabled,
                "a server-side term switch blocks the original rename draft");
            Check(Find<TextBlock>(shiftedDialog, "GradeRenameServerName").Text?.Contains("学期") == true,
                "The term-switch readback explains why the server name could not be verified.");
            await Capture(shiftedDialog, output, "06-term-changed-blocked");

            Click(Find<Button>(shiftedDialog, "ReloadCreationContext"));
            await Until(() => Find<TextBlock>(shiftedDialog, "CreationTerm").Text == "新启用学期" &&
                SelectedGrade(shiftedDialog) is null && !Find<StackPanel>(shiftedDialog, "GradeRenameFields").IsVisible,
                "explicit refresh observes the new term and clears the stale rename draft");
            Check(DraftName(shiftedDialog) == "学期切换班草稿" && DraftCode(shiftedDialog) == "TERM-DRAFT-01",
                "Refreshing the changed term preserves the class draft.");
            var nextGrade = Find<ComboBox>(shiftedDialog, "CreationGrade").Items.Cast<ClassCreationGrade>()
                .FirstOrDefault(grade => grade.Name == "新学期年级");
            Check(nextGrade is not null, "The new active term lists its grade.");
            Find<ComboBox>(shiftedDialog, "CreationGrade").SelectedItem = nextGrade;
            await Until(() => Find<Button>(shiftedDialog, "OpenGradeRenameButton").IsEnabled,
                "reselecting a grade in the new term enables another rename");
            Check(Find<StackPanel>(shiftedDialog, "GradeRenameFields").IsVisible == false,
                "The old rename draft is not silently submitted after refresh.");
            await Capture(shiftedDialog, output, "07-new-term-reselected");
            shiftedDialog.Close();
            await SignOutAndCloseAsync(shifted);

            await SignOutAsync(primary);
            End("PASS: 主入口年级改名+重开、第二位管理员旧窗口冲突读回服务器名称、丢包只读核对、选中班级/学生保留、启用学期改变后刷新重选。", 0);
        }
        catch (Exception error) { End("FAIL: " + error, 1); }

        async Task<ListBox> LoginAndSelectAsync(MainWindow main, string origin, string username, string password)
        {
            Find<TextBox>(main, "ServerAddress").Text = origin;
            Find<TextBox>(main, "SchoolCode").Text = access.SchoolCode;
            Find<TextBox>(main, "Username").Text = username;
            Find<TextBox>(main, "Password").Text = password;
            Click(Find<Button>(main, "LoginButton"));
            var schools = Find<ComboBox>(main, "SchoolPicker");
            await Until(() => schools.Items.Cast<School>().Any(school => school.Id == access.SchoolId), "manager login");
            schools.SelectedItem = schools.Items.Cast<School>().Single(school => school.Id == access.SchoolId);
            var picker = Find<ListBox>(main, "ClassPicker");
            await Until(() => picker.Items.Cast<Classroom>().Any(room => room.Id == access.ClassId), "class list");
            picker.SelectedItem = picker.Items.Cast<Classroom>().Single(room => room.Id == access.ClassId);
            await Until(() => Find<Button>(main, "OpenClassRosterButton").IsEnabled, "class selection");
            return picker;
        }

        async Task<MainWindow> NewMainAsync(string origin, string username, string password)
        {
            var main = new MainWindow();
            main.Show();
            await Until(() => main.IsVisible, "secondary main window");
            await LoginAndSelectAsync(main, origin, username, password);
            return main;
        }

        async Task<ClassCreationWindow> OpenCreationAsync(MainWindow main, string gradeId)
        {
            var before = desktop.Windows.OfType<ClassCreationWindow>().ToHashSet();
            Click(Find<Button>(main, "OpenClassCreationButton"));
            await Until(() => desktop.Windows.OfType<ClassCreationWindow>().Any(window => window.IsVisible &&
                !before.Contains(window)), "class-creation dialog");
            var open = desktop.Windows.OfType<ClassCreationWindow>().Single(window => window.IsVisible && !before.Contains(window));
            await Until(() => Find<ComboBox>(open, "CreationGrade").ItemCount > 0, "grade directory");
            var picker = Find<ComboBox>(open, "CreationGrade");
            var grade = picker.Items.Cast<ClassCreationGrade>().SingleOrDefault(item => item.Id == gradeId);
            Check(grade is not null, "The configured grade is present in the current term.");
            picker.SelectedItem = grade;
            await Until(() => Find<Button>(open, "OpenGradeRenameButton").IsEnabled, "rename action");
            return open;
        }

        async Task RenameAsync(ClassCreationWindow window, string name)
        {
            Click(Find<Button>(window, "OpenGradeRenameButton"));
            await Until(() => Find<StackPanel>(window, "GradeRenameFields").IsVisible, "rename editor");
            Check(!Find<TextBox>(window, "CreationName").IsEffectivelyEnabled,
                "Only the grade name is editable while the class draft is preserved.");
            Find<TextBox>(window, "RenameGradeName").Text = name;
            var submit = Find<Button>(window, "SubmitGradeRenameButton");
            await Until(() => submit.IsEnabled, "rename submission enabled");
            Click(submit);
        }

        async Task SignOutAndCloseAsync(MainWindow main)
        {
            await SignOutAsync(main);
            main.Close();
            await Until(() => !main.IsVisible, "secondary main window closes");
        }

        async Task SignOutAsync(MainWindow main)
        {
            Click(main.GetVisualDescendants().OfType<Button>().Single(button => button.Content is "退出登录"));
            await Until(() => Find<Button>(main, "LoginButton").IsEffectivelyVisible, "sign out");
        }

        void End(string message, int code)
        {
            File.WriteAllText(Path.Combine(output, "result.txt"), message);
            Console.WriteLine(message);
            desktop.Shutdown(code);
        }
    }

    private static ClassCreationGrade? SelectedGrade(ClassCreationWindow window) =>
        Find<ComboBox>(window, "CreationGrade").SelectedItem as ClassCreationGrade;
    private static string Notice(MainWindow window) => Find<TextBlock>(window, "ClassCreationNoticeText").Text ?? "";
    private static string DraftName(ClassCreationWindow window) => Find<TextBox>(window, "CreationName").Text ?? "";
    private static string DraftCode(ClassCreationWindow window) => Find<TextBox>(window, "CreationCode").Text ?? "";
    private static void FillDraft(ClassCreationWindow window, string name, string code)
    {
        Find<TextBox>(window, "CreationName").Text = name;
        Find<TextBox>(window, "CreationCode").Text = code;
    }
}
