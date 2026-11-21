using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.App;
using NPEduConsole.Core;
internal static partial class Program
{
    private sealed record TeacherAllocationAccess(string Origin, string SchoolId, string SchoolCode, string Password,
        string DraftId, string OauthSchoolId, string NewPin, string IgnoredPin,
        string InviteEmail, string RevokeEmail, string WrongTermFile, string WrongSchoolFile, string MatchingFile, bool AllowAllocationWrites);

    private static int StartTeacherAllocation(string fixture, string output)
    {
        var access = JsonSerializer.Deserialize<TeacherAllocationAccess>(File.ReadAllText(fixture), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Expected isolated teacher allocation fixture.");
        Check(access.AllowAllocationWrites && IsLoopback(access.Origin), "Teacher allocation requires an isolated loopback fixture.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyTeacherAllocationAsync(access, output); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyTeacherAllocationAsync(TeacherAllocationAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        using var checkpoints = new HttpClient {BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(90)};
        try
        {
            main.Width = 1160;
            main.Height = 780;
            Find<TextBox>(main, "ServerAddress").Text = access.Origin;
            Find<TextBox>(main, "SchoolCode").Text = access.SchoolCode;
            Find<TextBox>(main, "Username").Text = "admin";
            Find<TextBox>(main, "Password").Text = access.Password;
            Click(Find<Button>(main, "LoginButton"));
            await SelectSchool(access.SchoolId);

            Click(Find<Button>(main, "OpenOrganizationImportButton"));
            var organization = await WindowOf<OrganizationImportWindow>();
            var organizationTerms = Find<ComboBox>(organization, "OrgTerm");
            await Until(() => organizationTerms.Items.OfType<OrganizationTerm>().Any(row => row.Id == access.DraftId),
                "both terms visible in organization import");
            organizationTerms.SelectedItem = organizationTerms.Items.OfType<OrganizationTerm>().Single(row => row.Id == access.DraftId);
            await Until(() => Find<Button>(organization, "OrgOpenFile").IsEnabled && Find<TextBlock>(organization, "OrgScope").Text!.Contains("开学准备草稿"),
                "draft term selected in organization import");
            typeof(OrganizationImportWindow).GetMethod("LoadOrganizationText", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(organization, ["""{"grade":{"code":"G2","name":"高二"},"subjects":[],"administrativeClasses":[{"code":"G2-C10","name":"高二（10）班草稿","subjectRules":{}}]}"""]);
            Click(Find<Button>(organization, "OrgPreview"));
            await Until(() => Find<CheckBox>(organization, "OrgApprove").IsEnabled, "a real organization write is ready");
            Find<CheckBox>(organization, "OrgApprove").IsChecked = true;
            Click(Find<Button>(organization, "OrgSave"));
            var handoff = Find<Button>(organization, "OrgTeacherAllocation");
            await Until(() => handoff.IsEffectivelyVisible && handoff.IsEnabled, "server organization receipt offers teacher allocation");
            Click(handoff);
            var window = await WindowOf<TeacherAllocationWindow>();
            var allocationScope = Find<TextBlock>(window, "AllocationScope");
            await Until(() => allocationScope.Text?.Contains("开学准备草稿") == true && allocationScope.Text.Contains("高二"),
                "organization receipt supplies draft term and grade");
            Check(Find<ComboBox>(window, "AllocationGrade").SelectedItem?.ToString()?.Contains("G2") == true,
                "selected server grade displays high school code");
            Load(window, """{"assignments":[{"username":"viewer","name":"viewer","role":"ASSISTANT","workspaceCodes":["G2-C08"]}]}""");
            await Preview(window, () => Differences(window).Any(row => row.Action == "ADD" && row.Workspace?.Contains("草稿") == true));
            await Save(window, () => Status(window).Contains("分配属于草稿学期"));
            await Signal("draft-access");
            await Capture(window, output, "01-draft-handoff-and-access");
            Click(Find<Button>(window, "AllocationClose"));
            await Until(() => !window.IsVisible && main.IsEnabled, "returned from draft allocation");

            var (accountWindow, active) = await OpenTeacherBulk();
            window = active;
            await Until(() => Find<Button>(window, "AllocationJson").IsEnabled && Find<TextBlock>(window, "AllocationScope").Text!.Contains("秋季学期"),
                "teacher account bulk entry defaults to active term");
            window.Width = 840;
            window.Height = 480;
            await Until(() => window.ClientSize is {Width: <= 845, Height: <= 485}, "narrow allocation workbench");
            string ActiveTable(string pin) => "短账号\t姓名\t初始 PIN\t班级代码\t角色\n" +
                $"new-physics\t陈老师\t{pin}\tG2-C07,G2-PHY-A1\t教师\nviewer\tviewer\t{access.IgnoredPin}\tG2-C08\t助教";
            await Paste(window, ActiveTable("abc"));
            var term = Find<ComboBox>(window, "AllocationTerm").SelectedItem;
            var target = Find<TextBlock>(window, "AllocationTargetDetails");
            await OpenLegacyFile(window, access.WrongTermFile);
            Check(target.IsEffectivelyVisible && target.Text!.Contains("所选 秋季学期／文件声明 开学准备草稿") &&
                ReferenceEquals(term, Find<ComboBox>(window, "AllocationTerm").SelectedItem) &&
                Find<TextBlock>(window, "AllocationFooter").Text!.Contains("2 位教师") &&
                !Find<Button>(window, "AllocationPreviewButton").IsEnabled && !Find<Button>(window, "AllocationSave").IsEnabled,
                "an old term file keeps the active term and the unchanged original table and blocks writes");
            await Capture(window, output, "09-native-file-old-term-rejected");
            await OpenLegacyFile(window, access.WrongSchoolFile);
            Check(target.Text!.Contains("所选 示范中学／文件声明编号 " + access.OauthSchoolId) &&
                !Find<Button>(window, "AllocationPreviewButton").IsEnabled, "another school's declaration blocks before preview");
            await Signal("document-scope-zero");
            await OpenLegacyFile(window, access.MatchingFile);
            Check(!target.IsEffectivelyVisible, "a matching file can proceed from the same native entry");
            await Preview(window, () => Differences(window).Any(row => row.Action == "BLOCK" && row.Path.EndsWith(".pin", StringComparison.Ordinal)));
            var list = Find<ListBox>(window, "AllocationDifferences");
            list.RaiseEvent(new KeyEventArgs {RoutedEvent = InputElement.KeyDownEvent, Key = Key.F8});
            await Until(() => window.GetVisualDescendants().OfType<TextBox>().Any(row =>
                row.IsFocused && AutomationProperties.GetName(row) == "初始 PIN" && row.PasswordChar != '\0'), "F8 focuses the masked PIN cell");
            var pinCell = window.GetVisualDescendants().OfType<TextBox>().Single(row => row.IsFocused);
            pinCell.Text = access.NewPin;
            await Preview(window, () => Differences(window).Any(row => row.Message?.Contains("旧 PIN 已忽略") == true) &&
                Differences(window).Any(row => row.Workspace?.Contains("08") == true && row.Current == "只读" && row.After == "助教"));
            await Capture(window, output, "02-narrow-pin-ignored-and-role-preview");
            await Signal("legacy-remove");
            await Save(window, () => Status(window).Contains("零写入") && Differences(window).Any(row => row.NeedsReview &&
                row.Workspace?.Contains("08") == true && row.Message?.Contains("预览时 只读，现为 无记录") == true));
            await Signal("stale-zero");
            await Capture(window, output, "03-narrow-eighth-stale");

            window.Width = 1160;
            window.Height = 780;
            await Preview(window, () => Find<CheckBox>(window, "AllocationApprove").IsEnabled);
            await Save(window, () => Status(window).Contains("找回原回执"));
            await Signal("primary-applied");
            await Capture(window, output, "04-wide-teacher-results");

            await Paste(window, ActiveTable(access.NewPin));
            await Preview(window, () => Status(window).Contains("无需重复保存") &&
                Differences(window).Any(row => row.Message?.Contains("旧 PIN 已忽略") == true));
            Check(!Find<Button>(window, "AllocationSave").IsEnabled, "consistent legacy table cannot be submitted repeatedly");
            Find<TabControl>(window, "AllocationTabs").SelectedIndex = 3;
            var spaceList = Find<ListBox>(window, "AllocationDirectorySpaces");
            spaceList.SelectedItem = spaceList.Items.OfType<AllocationWorkspace>().Single(row => row.Code == "G2-C07");
            var spaceDetails = Find<StackPanel>(window, "AllocationSpaceDetails");
            var focused = spaceDetails.Children.OfType<Border>().Single(card =>
                card.GetVisualDescendants().OfType<TextBlock>().Any(row => row.Text?.Contains("viewer（viewer）") == true));
            Click(focused.GetVisualDescendants().OfType<Button>().Single(row => Equals(row.Content, "把移除加入教师表")));
            await Preview(window, () => Differences(window).Any(row => row.Action == "REMOVE" && row.Message?.Contains("班主任") == true));
            await Capture(window, output, "05-removing-direct-keeps-head-teacher");
            await Save(window, () => Status(window).Contains("服务器已确认"));
            await Signal("duty-only");
            var teacherDirectory = Find<ListBox>(window, "AllocationDirectoryTeachers");
            teacherDirectory.SelectedItem = teacherDirectory.Items.Cast<object>().Single(row => row.ToString()!.Contains("new-physics"));
            var a1 = Find<StackPanel>(window, "AllocationTeacherDetails").Children.OfType<Border>().Single(card =>
                card.GetVisualDescendants().OfType<TextBlock>().Any(row => row.Text?.Contains("物理 A1") == true));
            Click(a1.GetVisualDescendants().OfType<Button>().Single(row => Equals(row.Content, "继续分配任课")));
            var teaching = await WindowOf<TeachingWindow>();
            await Until(() => Find<TextBlock>(teaching, "TeachingContext").Text?.Contains("物理 A1") == true &&
                Find<ComboBox>(teaching, "TeachingTeacherPicker").SelectedItem is TeachingTeacher {Username: "new-physics"},
                "continued to the confirmed teacher and course group without writing a teaching assignment");
            await Capture(teaching, output, "08-continue-to-course-group-teaching");
            Click(Find<Button>(teaching, "CloseTeachingButton"));
            await Until(() => !window.IsVisible && accountWindow.IsEnabled, "teacher accounts refreshed after allocation");
            accountWindow.Close();
            await Until(() => !accountWindow.IsVisible && main.IsEnabled, "close local teacher list");

            await SelectSchool(access.OauthSchoolId);
            var (oauthAccounts, oauth) = await OpenTeacherBulk();
            window = oauth;
            await Until(() => Find<Button>(window, "AllocationJson").IsEnabled && Find<TextBlock>(window, "AllocationScope").Text!.Contains("OAuth 邮箱"),
                "email school does not ask for a personal PIN");
            Load(window, JsonSerializer.Serialize(new {assignments = new[]
            {
                new {email = access.InviteEmail, role = "TEACHER", workspaceCodes = new[] {"G2-C07"}},
                new {email = access.RevokeEmail, role = "VIEWER", workspaceCodes = new[] {"G2-C08"}}
            }}));
            await Preview(window, () => Differences(window).Count(row => row.Action == "ADD" && row.After?.Contains("待认领") == true) == 2);
            await Save(window, () => Status(window).Contains("服务器已确认"));
            await Signal("invitation-pending");
            Find<TabControl>(window, "AllocationTabs").SelectedIndex = 3;
            spaceList = Find<ListBox>(window, "AllocationDirectorySpaces");
            spaceList.SelectedItem = spaceList.Items.OfType<AllocationWorkspace>().Single(row => row.Code == "G2-C08");
            var revoke = Find<StackPanel>(window, "AllocationSpaceDetails").GetVisualDescendants().OfType<Button>()
                .Single(row => Equals(row.Content, "把撤回加入教师表"));
            Click(revoke);
            await Preview(window, () => Differences(window).Any(row => row.Action == "REMOVE" && row.After?.Contains("撤回") == true));
            await Capture(window, output, "06-revoke-one-email-invitation");
            await Save(window, () => Status(window).Contains("服务器已确认"));
            await Signal("oauth-complete");
            Click(Find<Button>(window, "AllocationRefresh"));
            await Until(() => Status(window).Contains("重新读取"), "after real OAuth callback and exchange, re-read actual invitation state");
            Find<TabControl>(window, "AllocationTabs").SelectedIndex = 3;
            spaceList.SelectedItem = spaceList.Items.OfType<AllocationWorkspace>().Single(row => row.Code == "G2-C07");
            await Until(() => Find<StackPanel>(window, "AllocationSpaceDetails").GetVisualDescendants().OfType<TextBlock>()
                .Any(row => row.Text == "邮箱已认领"), "only the verified, redeemed invitation is marked claimed");
            await Capture(window, output, "07-email-claimed-live");
            Console.WriteLine("PASS: Windows teacher allocation from both entry points, masked input, stale and lost replies, duties and invitations.");
            desktop.Shutdown(0);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            var visible = desktop.Windows.LastOrDefault(row => row.IsVisible);
            if (visible is not null && visible.Title != "从 Excel 粘贴教师")
                try { await Capture(visible, output, "teacher-allocation-desktop-failure"); } catch { }
            desktop.Shutdown(1);
        }

        async Task SelectSchool(string id)
        {
            var picker = Find<ComboBox>(main, "SchoolPicker");
            await Until(() => picker.Items.OfType<School>().Any(row => row.Id == id), "authorized school available");
            picker.SelectedItem = picker.Items.OfType<School>().Single(row => row.Id == id);
            var classes = Find<Button>(main, "OpenClassWorkspaceButton");
            await Until(() => classes.IsEffectivelyVisible && classes.IsEnabled, "school class hub available");
            Click(classes);
        }

        async Task<(TeacherAccountsWindow Owner, TeacherAllocationWindow Window)> OpenTeacherBulk()
        {
            var entry = Find<Button>(main, "OpenTeacherAccountsButton");
            await Until(() => entry.IsEffectivelyVisible && entry.IsEnabled, "normal teacher account entry");
            Click(entry);
            var owner = await WindowOf<TeacherAccountsWindow>();
            var bulk = Find<Button>(owner, "TeacherAccountBulk");
            await Until(() => bulk.IsEffectivelyVisible && bulk.IsEnabled, "teacher bulk action available");
            Click(bulk);
            return (owner, await WindowOf<TeacherAllocationWindow>());
        }

        async Task Paste(TeacherAllocationWindow window, string tabSeparated)
        {
            var clipboard = window.Clipboard ?? throw new InvalidOperationException("Windows clipboard unavailable.");
            // This fixture supplies disposable credentials through the actual Excel paste action.
            await clipboard.SetTextAsync(tabSeparated);
            try
            {
                Click(Find<Button>(window, "AllocationPaste"));
                await Until(() => desktop.Windows.Any(row => row.IsVisible && row.Title == "从 Excel 粘贴教师"), "Excel clipboard dialog open");
                var dialog = desktop.Windows.Single(row => row.IsVisible && row.Title == "从 Excel 粘贴教师");
                Click(dialog.GetVisualDescendants().OfType<Button>().Single(row => Equals(row.Content, "读取 Excel 剪贴板并载入")));
                await Until(() => !dialog.IsVisible && Status(window).Contains("已载入教师表"), "actual clipboard columns read");
            }
            finally
            {
                if (await clipboard.TryGetTextAsync() == tabSeparated) await clipboard.ClearAsync();
            }
        }

        async Task OpenLegacyFile(TeacherAllocationWindow window, string file)
        {
            Console.WriteLine($"Select fixture in Windows picker: {file}");
            var button = Find<Button>(window, "AllocationJson");
            Click(button);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            while (!button.IsEnabled) await Task.Delay(50, timeout.Token);
        }

        async Task Preview(TeacherAllocationWindow window, Func<bool> ready)
        {
            Click(Find<Button>(window, "AllocationPreviewButton"));
            await Until(() => Find<TabControl>(window, "AllocationTabs").SelectedIndex == 1 &&
                Find<Button>(window, "AllocationPreviewButton").IsEnabled && ready(), "server preview is ready");
        }

        async Task Save(TeacherAllocationWindow window, Func<bool> ready)
        {
            Find<CheckBox>(window, "AllocationApprove").IsChecked = true;
            Click(Find<Button>(window, "AllocationSave"));
            await Until(() => Find<Button>(window, "AllocationClose").IsEnabled && ready(), "server result is visible");
        }

        async Task<T> WindowOf<T>() where T : Window
        {
            await Until(() => desktop.Windows.OfType<T>().Any(row => row.IsVisible), typeof(T).Name + " is visible");
            return desktop.Windows.OfType<T>().Single(row => row.IsVisible);
        }

        async Task Signal(string mark)
        {
            using var response = await checkpoints.PostAsync("/__allocation/" + mark, null);
            Check(response.IsSuccessStatusCode, "isolated checkpoint: " + mark);
        }

        static string Status(TeacherAllocationWindow window) => Find<TextBlock>(window, "AllocationStatus").Text ?? "";
        static IEnumerable<AllocationDifference> Differences(TeacherAllocationWindow window) =>
            Find<ListBox>(window, "AllocationDifferences").Items.OfType<AllocationDifference>();
        static void Load(TeacherAllocationWindow window, string document) =>
            typeof(TeacherAllocationWindow).GetMethod("LoadAllocationText", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [document, true]);
    }

}
