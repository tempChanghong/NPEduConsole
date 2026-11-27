using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using NPEduConsole.App;
using NPEduConsole.Core;
internal static partial class Program
{
    private sealed record TeacherAllocationAccess(string Origin, string SchoolId, string SchoolCode, string Password,
        string DraftId, string ActiveId, string GroupId, string OauthSchoolId, string NewPin, string IgnoredPin,
        string InviteEmail, string RevokeEmail, string WrongTermFile, string WrongSchoolFile, string MatchingFile, bool AllowAllocationWrites,
        string NoTermSchoolId, string BatchFile, string NoTermFile);

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
            var organization = await AcademicPageAsync<OrganizationImportView>(main);
            var organizationTerms = Find<ComboBox>(organization, "OrgTerm");
            await Until(() => organizationTerms.Items.OfType<OrganizationTerm>().Any(row => row.Id == access.DraftId),
                "both terms visible in organization import");
            organizationTerms.SelectedItem = organizationTerms.Items.OfType<OrganizationTerm>().Single(row => row.Id == access.DraftId);
            await Until(() => Find<Button>(organization, "OrgOpenFile").IsEnabled && Find<TextBlock>(organization, "OrgScope").Text!.Contains("开学准备草稿"),
                "draft term selected in organization import");
            organization.LoadOrganizationText("""{"grade":{"code":"G2","name":"高二"},"subjects":[],"administrativeClasses":[{"code":"G2-C10","name":"高二（10）班草稿","subjectRules":{}}]}""");
            Click(Find<Button>(organization, "OrgPreview"));
            await Until(() => Find<CheckBox>(organization, "OrgApprove").IsEnabled, "a real organization write is ready");
            Find<CheckBox>(organization, "OrgApprove").IsChecked = true;
            Click(Find<Button>(organization, "OrgSave"));
            var handoff = Find<Button>(organization, "OrgTeacherAllocation");
            await Until(() => handoff.IsEffectivelyVisible && handoff.IsEnabled, "server organization receipt offers teacher allocation");
            Click(handoff);
            var window = await AcademicPageAsync<TeacherAllocationView>(main);
            var allocationScope = Find<TextBlock>(window, "AllocationScope");
            await Until(() => allocationScope.Text?.Contains("开学准备草稿") == true && allocationScope.Text.Contains("高二"),
                "organization receipt supplies draft term and grade");
            Check(Find<ComboBox>(window, "AllocationGrade").SelectedItem?.ToString()?.Contains("G2") == true,
                "selected server grade displays high school code");
            Load(window, """{"assignments":[{"username":"viewer","name":"viewer","role":"ASSISTANT","workspaceCodes":["G2-C08"]}]}""");
            await Preview(window, () => Differences(window).Any(row => row.Action == "ADD" && row.Workspace?.Contains("草稿") == true));
            await Save(window, () => Find<Button>(window, "AllocationVerify").IsEffectivelyVisible &&
                Status(window).Contains("暂不可读"));
            await Signal("draft-pending");
            await CaptureWithFrame(main, output, "01-draft-handoff-and-pending");
            await PressScoped(window, "AllocationClose");
            await Until(() => !window.IsEffectivelyVisible && Find<Button>(organization, "OrgTeacherAllocation").IsFocused,
                "closing an unresolved draft allocation restores the exact organization receipt");
            await PressScoped(organization, "OrgClose");

            await SelectSchool(access.OauthSchoolId);
            var (otherDirectory, otherWorkbench) = await OpenTeacherBulk();
            await Until(() => Find<TextBlock>(otherWorkbench.View, "AllocationScope").Text?.Contains("OAuth 邮箱") == true,
                "the other school loaded while the original school still has a pending receipt");
            Check(!Find<WrapPanel>(otherWorkbench.View, "AllocationPendingActions").IsVisible,
                "the original pending draft is not exposed at another school");
            await PressScoped(otherWorkbench.View, "AllocationClose");
            await Until(() => otherDirectory.IsEnabled, "other directory restores");
            await PressScoped(otherDirectory, "CloseTeacherAccountsButton");
            await SelectSchool(access.SchoolId);

            await PressScoped(main, "OpenOrganizationImportButton");
            var activeOrganization = await AcademicPageAsync<OrganizationImportView>(main);
            await Until(() => Find<ComboBox>(activeOrganization, "OrgTerm").SelectedItem is OrganizationTerm term && term.Id == access.ActiveId &&
                Find<Button>(activeOrganization, "OrgOpenFile").IsEffectivelyEnabled, "active organization page ready");
            activeOrganization.LoadOrganizationText("""{"grade":{"code":"G2","name":"高二"},"subjects":[],"administrativeClasses":[{"code":"G2-C15","name":"高二（15）班","subjectRules":{}}]}""");
            await PressScoped(activeOrganization, "OrgPreview");
            var activeApproval = Find<CheckBox>(activeOrganization, "OrgApprove");
            await Until(() => activeApproval.IsEffectivelyEnabled, "active organization result preview ready");
            activeApproval.IsChecked = true;
            await PressScoped(activeOrganization, "OrgSave");
            await PressScoped(activeOrganization, "OrgTeacherAllocation");
            window = await AcademicPageAsync<TeacherAllocationView>(main);
            await Until(() => Find<Button>(window, "AllocationJson").IsEffectivelyEnabled && Find<TextBlock>(window, "AllocationScope").Text!.Contains("秋季学期"),
                "active organization receipt opens its own allocation");
            Check(TopLevel.GetTopLevel(window) == main && !Find<ComboBox>(window, "AllocationTerm").IsEnabled &&
                !Find<ComboBox>(window, "AllocationGrade").IsEnabled && !Find<WrapPanel>(window, "AllocationPendingActions").IsVisible,
                "main HWND and fixed current term and grade, with another term's pending receipt isolated");
            await Signal("draft-isolated-from-active");
            await PressScoped(window, "AllocationByTeacher");
            var search = Find<TextBox>(window, "AllocationTeacherSearch");
            search.Text = "同名示例教师";
            var people = Find<ListBox>(window, "AllocationDirectoryTeachers");
            await Until(() => people.ItemCount == 2, "same-name query has filtered the directory");
            var sameName = people.Items.Cast<object>().Select(row => row.ToString()).ToArray();
            Check(sameName.Length == 2 && sameName.Any(row => row!.Contains("peer-a")) && sameName.Any(row => row!.Contains("peer-b")),
                "same-name teachers are distinguished by their actual short accounts");
            await CaptureWithFrame(main, output, "01a-wide-same-name-teachers");
            await PressScoped(window, "AllocationEdit");
            main.Width = 840;
            main.Height = 480;
            await Until(() => main.ClientSize is {Width: <= 845, Height: <= 485}, "narrow main workbench");
            string ActiveTable(string pin) => "短账号\t姓名\t初始 PIN\t班级代码\t角色\n" +
                $"new-physics\t陈老师\t{pin}\tG2-C07,G2-PHY-A1\t教师\nviewer\tviewer\t{access.IgnoredPin}\tG2-C08\t助教";
            await Paste(window, ActiveTable("abc"));
            var term = Find<ComboBox>(window, "AllocationTerm").SelectedItem;
            var target = Find<TextBlock>(window, "AllocationTargetDetails");
            window.LoadAllocationText("""{"teachers":[{"username":"test","teachingAssignments":[],"responsibilities":{}}]}""", legacy: true);
            Check(target.IsEffectivelyVisible && target.Text!.Contains("完整师资配置") &&
                !Find<Button>(window, "AllocationPreviewButton").IsEnabled && Find<TextBlock>(window, "AllocationFooter").Text!.Contains("2 位教师"),
                "the combined staff file cannot replace the original table or drop its duties");
            window.LoadAllocationText("""{"assignments":[{"username":"mixed","teachingAssignments":[]}]}""", legacy: true);
            Check(!Find<Button>(window, "AllocationPreviewButton").IsEnabled, "mixed-format rows cannot silently lose teaching assignments");
            window.LoadAllocationText("""{"assignments":[{"username":"mixed"}],"responsibilities":{"classLeaderships":[]}}""", legacy: true);
            Check(!Find<Button>(window, "AllocationPreviewButton").IsEnabled, "a mixed-format document cannot discard its school duties");
            await CaptureWithFrame(main, output, "12-full-staff-file-keeps-table");
            await OpenLegacyFile(window, access.WrongTermFile);
            Check(target.IsEffectivelyVisible && target.Text!.Contains("所选 秋季学期／文件声明 开学准备草稿") &&
                ReferenceEquals(term, Find<ComboBox>(window, "AllocationTerm").SelectedItem) &&
                Find<TextBlock>(window, "AllocationFooter").Text!.Contains("2 位教师") &&
                !Find<Button>(window, "AllocationPreviewButton").IsEnabled && !Find<Button>(window, "AllocationSave").IsEnabled,
                "an old term file keeps the active term and the unchanged original table and blocks writes");
            await CaptureWithFrame(main, output, "09-native-file-old-term-rejected");
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
            await CaptureWithFrame(main, output, "02-narrow-pin-ignored-and-role-preview");
            await Signal("legacy-remove");
            await Save(window, () => Status(window).Contains("零写入") && Differences(window).Any(row => row.NeedsReview &&
                row.Workspace?.Contains("08") == true && row.Message?.Contains("预览时 只读，现为 无记录") == true));
            await Signal("stale-zero");
            var conflict = Differences(window).Single(row => row.Workspace?.Contains("G2-C08") == true && row.NeedsReview);
            Check(conflict.BeforeText == "无直接访问" && conflict.AfterText == "助教", "the verified lack of a direct role appears as such");
            list.SelectedItem = conflict;
            await VerifyConflictGeometry(conflict, narrow: true);
            await CaptureWithFrame(main, output, "03-narrow-eighth-stale");
            main.Width = 1160;
            main.Height = 780;
            await Until(() => main.ClientSize.Width >= 1150, "return to wide size with the same issue");
            await VerifyConflictGeometry(conflict, narrow: false);
            await CaptureWithFrame(main, output, "03a-wide-eighth-stale");

            async Task VerifyConflictGeometry(AllocationDifference row, bool narrow)
            {
                list.ScrollIntoView(row);
                await Until(() =>
                {
                    if (!ReferenceEquals(list.SelectedItem, row) || list.ContainerFromItem(row) is not ListBoxItem container) return false;
                    var text = container.GetVisualDescendants().OfType<TextBlock>().Where(item => item.IsEffectivelyVisible).ToArray();
                    var current = narrow ? "现在　" + row.BeforeText : row.BeforeText;
                    var after = narrow ? "提交后　" + row.AfterText : row.AfterText;
                    return text.Any(item => item.Text == current && item.Bounds.Height > 0) &&
                        text.Any(item => item.Text == after && item.Bounds.Height > 0) &&
                        list.GetVisualDescendants().OfType<ListBoxItem>().Count(item => item.IsEffectivelyVisible &&
                            item.TranslatePoint(new Point(0, item.Bounds.Height), list) is { } bottom && bottom.Y <= list.Bounds.Height + 1) >= 3 &&
                        !Find<Button>(window, "AllocationJson").IsEffectivelyVisible &&
                        Find<Button>(window, "AllocationPreviewButton").IsEffectivelyVisible;
                }, "selected conflict shows both values on the same row, two neighboring rows and the stage action");
            }

            await Preview(window, () => Find<CheckBox>(window, "AllocationApprove").IsEnabled);
            await Save(window, () => Status(window).Contains("找回原回执"));
            Check(!Find<Button>(window, "AllocationSavePin").IsVisible && !Find<Button>(window, "AllocationCopyPin").IsVisible,
                "a recovered receipt cannot deliver a PIN");
            Check(!BatchPinReady(window, "Open"), "a recovered receipt cannot start batch PIN delivery");
            await Signal("primary-applied");
            await CaptureWithFrame(main, output, "04-wide-teacher-results");
            var receiptRows = Find<ListBox>(window, "AllocationReceiptRows");
            receiptRows.SelectedItem = receiptRows.Items.Cast<object>().Single(row =>
                row.ToString()!.Contains("new-physics") && row.ToString()!.Contains("G2-PHY-A1"));
            Check(receiptRows.Items.Cast<object>().All(row => !row.ToString()!.Contains(access.NewPin) && !row.ToString()!.Contains(access.IgnoredPin)),
                "receipt rows contain no initial or ignored PIN");
            await PressScoped(window, "AllocationBySpace");
            Find<TextBox>(window, "AllocationSpaceSearch").Text = "G2-C07";
            var filteredSpaces = Find<ListBox>(window, "AllocationDirectorySpaces");
            await Until(() => filteredSpaces.ItemCount == 1 &&
                filteredSpaces.Items.OfType<AllocationWorkspace>().First().Code == "G2-C07", "another space is filtered");
            await PressScoped(window, "AllocationBackToReceipt");
            await PressScoped(window, "AllocationReceiptSpace");
            Check((filteredSpaces.SelectedItem as AllocationWorkspace)?.Id == access.GroupId,
                "one click opens the exact receipt space even if a previous query hid it");
            await PressScoped(window, "AllocationBackToReceipt");
            await PressScoped(window, "AllocationReceiptTeaching");
            var teaching = await AcademicPageAsync<TeachingView>(main);
            await Until(() => Find<TextBlock>(teaching, "TeachingContext").Text?.Contains("物理 A1") == true &&
                Find<ComboBox>(teaching, "TeachingTeacherPicker").SelectedItem is TeachingTeacher {Username: "new-physics"},
                "opened the confirmed course group and exact teacher in the main HWND");
            Check(TopLevel.GetTopLevel(teaching) == main, "assignment uses the original main HWND");
            await CaptureWithFrame(main, output, "08-continue-to-course-group-teaching");
            await PressScoped(teaching, "CloseTeachingButton");
            await PressScoped(teaching, "TeachingKeepDraft");
            await Until(() => Find<Button>(window, "AllocationReceiptTeaching").IsFocused &&
                receiptRows.SelectedItem?.ToString()?.Contains("G2-PHY-A1") == true,
                "returned to the original selected receipt row and action");
            await Signal("teaching-return");
            await PressScoped(window, "AllocationEdit");
            await Paste(window, ActiveTable(access.NewPin));
            await Preview(window, () => Status(window).Contains("无需重复保存") &&
                Differences(window).Any(row => row.Message?.Contains("旧 PIN 已忽略") == true));
            Check(!Find<Button>(window, "AllocationSave").IsEnabled, "consistent legacy table cannot be submitted repeatedly");
            await PressScoped(window, "AllocationEdit");
            await PressScoped(window, "AllocationBySpace");
            var spaceList = Find<ListBox>(window, "AllocationDirectorySpaces");
            spaceList.SelectedItem = spaceList.Items.OfType<AllocationWorkspace>().Single(row => row.Code == "G2-C07");
            var spaceDetails = Find<StackPanel>(window, "AllocationSpaceDetails");
            var focused = spaceDetails.Children.OfType<Border>().Single(card =>
                card.GetVisualDescendants().OfType<TextBlock>().Any(row => row.Text?.Contains("viewer（viewer）") == true));
            Click(focused.GetVisualDescendants().OfType<Button>().Single(row => Equals(row.Content, "把移除加入教师表")));
            await Preview(window, () => Differences(window).Any(row => row.Action == "REMOVE" && row.Message?.Contains("班主任") == true));
            await CaptureWithFrame(main, output, "05-removing-direct-keeps-head-teacher");
            await Save(window, () => Status(window).Contains("服务器已确认"));
            await Signal("duty-only");
            await PressScoped(window, "AllocationEdit");
            await Paste(window, "短账号\t姓名\t初始 PIN\t班级代码\t角色\nretry-office\t断线重试教师\t" +
                access.NewPin + "\tG2-C07\t教师");
            await Preview(window, () => Find<CheckBox>(window, "AllocationApprove").IsEffectivelyEnabled);
            await Signal("drop-next-pinned-write");
            await Save(window, () => Status(window).Contains("暂未找到"));
            await Signal("pinned-write-never-arrived");
            await Until(() => Find<Button>(window, "AllocationSave").IsEffectivelyVisible &&
                Equals(Find<Button>(window, "AllocationSave").Content, "按原编号重试"), "missing local-account receipt offers safe original retry");
            await PressScoped(window, "AllocationClose");
            await PressScoped(activeOrganization, "OrgTeacherAllocation");
            window = await AcademicPageAsync<TeacherAllocationView>(main);
            var pendingAfterReopen = Find<WrapPanel>(window, "AllocationPendingActions");
            await Until(() => pendingAfterReopen.Children.OfType<Button>().Any(), "pending local-account request reopens from its own scope");
            Click(pendingAfterReopen.Children.OfType<Button>().Single());
            window = await AcademicPageAsync<TeacherAllocationView>(main);
            await PressScoped(window, "AllocationSave");
            await Until(() => desktop.Windows.Any(dialog => dialog.IsVisible && dialog.Title == "确认原请求的初始 PIN"), "retry requests a fresh masked PIN");
            var retryDialog = desktop.Windows.Single(dialog => dialog.IsVisible && dialog.Title == "确认原请求的初始 PIN");
            Click(retryDialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "取消")));
            await Until(() => !retryDialog.IsVisible && Find<Button>(window, "AllocationSave").IsFocused,
                "cancelling PIN entry keeps the request and restores retry focus");
            await Signal("cancelled-pinned-retry");
            await PressScoped(window, "AllocationSave");
            await Until(() => desktop.Windows.Any(dialog => dialog.IsVisible && dialog.Title == "确认原请求的初始 PIN"),
                "original request is still available for retry");
            retryDialog = desktop.Windows.Single(dialog => dialog.IsVisible && dialog.Title == "确认原请求的初始 PIN");
            var reenterPin = retryDialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.PasswordChar != '\0');
            Check(string.IsNullOrEmpty(reenterPin.Text), "the original PIN was not retained with the request");
            await Capture(retryDialog, output, "10-reenter-pin-for-original-request");
            reenterPin.Text = access.NewPin;
            Click(retryDialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "按原编号重试")));
            await Until(() => window.DisplayedStage == TeacherAllocationView.Stage.Receipt && Status(window).Contains("服务器已确认"),
                "the original local-account request completed");
            var batchRefresh = window.GetVisualDescendants().OfType<Button>()
                .SingleOrDefault(button => AutomationProperties.GetAutomationId(button) == "TeacherBatchPinRefresh");
            Check(batchRefresh is not null && batchRefresh.IsEffectivelyEnabled && batchRefresh.IsEffectivelyVisible,
                "a transient directory failure after an acknowledged write leaves a supported way to recheck the fresh PIN");
            Click(batchRefresh!);
            await Until(() => Find<Button>(window, "AllocationSavePin").IsEffectivelyVisible,
                "a successful directory recheck restores delivery in the original result");
            var handoffFile = Path.Combine(output, "temporary-teacher-handoff.txt");
            await File.WriteAllTextAsync(handoffFile, "");
            try
            {
                window.SelectPinFile = _ => main.StorageProvider.TryGetFileFromPathAsync(new Uri(handoffFile));
                await PressScoped(window, "AllocationSavePin");
                await Until(() => Status(window).Contains("已另存"), "the fresh applied PIN was delivered once");
                var savedHandoff = await File.ReadAllTextAsync(handoffFile);
                Check(savedHandoff.Contains("retry-office") && savedHandoff.Contains(access.SchoolCode) &&
                    savedHandoff.EndsWith("PIN " + access.NewPin) && !Find<Button>(window, "AllocationCopyPin").IsVisible,
                    "saved handoff identifies the correct school, account and exact applied PIN");
            }
            finally { File.Delete(handoffFile); }
            await Signal("pinned-retry-applied");
            await PressScoped(window, "AllocationClose");
            window = await AcademicPageAsync<TeacherAllocationView>(main);
            if (window.DisplayedStage != TeacherAllocationView.Stage.Edit) await PressScoped(window, "AllocationEdit");
            await Paste(window, "race-office\t迟到教师\t" + access.NewPin + "\tG2-C07\t教师");
            await Preview(window, () => Find<CheckBox>(window, "AllocationApprove").IsEffectivelyEnabled);
            await Signal("hold-late-create");
            await Save(window, () => Status(window).Contains("暂未找到"));
            await PressScoped(window, "AllocationSave");
            await Until(() => desktop.Windows.Any(dialog => dialog.IsVisible && dialog.Title == "确认原请求的初始 PIN"), "late request re-entry");
            retryDialog = desktop.Windows.Single(dialog => dialog.IsVisible && dialog.Title == "确认原请求的初始 PIN");
            await Signal("apply-late-original");
            retryDialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.PasswordChar != '\0').Text = access.IgnoredPin;
            Click(retryDialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "按原编号重试")));
            await Until(() => window.DisplayedStage == TeacherAllocationView.Stage.Receipt && Status(window).Contains("服务器已确认"),
                "idempotent replay returns the original teacher");
            Check(!Find<Button>(window, "AllocationCopyPin").IsVisible && !Find<Button>(window, "AllocationSavePin").IsVisible &&
                !BatchPinReady(window, "Open") && (Find<TextBlock>(window, "AllocationPinHint").Text ?? "").Contains("没有可再次交付"),
                "a replay never delivers the retyped PIN");
            await CaptureWithFrame(main, output, "13-late-original-no-pin-delivery");
            await Signal("replay-cannot-deliver");
            await PressScoped(window, "AllocationEdit");
            await Paste(window, "retry-office\t断线重试教师\t\tG2-C08\t助教");
            await Preview(window, () => Find<CheckBox>(window, "AllocationApprove").IsEffectivelyEnabled);
            await Signal("drop-next-role-change");
            await Save(window, () => Status(window).Contains("暂未找到"));
            await PressScoped(window, "AllocationClose");
            await PressScoped(activeOrganization, "OrgTeacherAllocation");
            window = await AcademicPageAsync<TeacherAllocationView>(main);
            var outstandingRoleChange = Find<WrapPanel>(window, "AllocationPendingActions");
            await Until(() => outstandingRoleChange.Children.OfType<Button>().Any(), "role change is available from a freshly opened view");
            Click(outstandingRoleChange.Children.OfType<Button>().Single());
            window = await AcademicPageAsync<TeacherAllocationView>(main);
            await Until(() => Find<Button>(window, "AllocationSave").IsEffectivelyVisible &&
                Find<Button>(window, "AllocationSave").IsEffectivelyEnabled, "reopened request has rechecked its missing receipt");
            await Signal("change-role-scope-before-retry");
            await PressScoped(window, "AllocationSave");
            await Until(() => window.DisplayedStage == TeacherAllocationView.Stage.Review && Status(window).Contains("零写入"),
                "stale original retry comes back as an unapplied review");
            await Signal("stale-retry-left-role-untouched");
            await PressScoped(window, "AllocationClose");
            await Until(() => Find<Button>(window, "AllocationKeepEditing").IsEffectivelyVisible,
                "a reopened draft rejected by the server cannot be silently discarded");
            await CaptureWithFrame(main, output, "11-stale-retry-keeps-original-table");
            await PressScoped(window, "AllocationKeepEditing");
            await PressScoped(window, "AllocationEdit");
            await Until(() => Find<ListBox>(window, "AllocationTeachers").GetVisualDescendants().OfType<TextBox>()
                .Any(box => box.IsEffectivelyVisible && box.Text == "retry-office"),
                "the original unapplied edit is still available");
            await PressScoped(window, "AllocationClose");
            await PressScoped(window, "AllocationDiscard");
            window = await AcademicPageAsync<TeacherAllocationView>(main);
            await VerifyLiveBatchAsync(window);
            await PressScoped(window, "AllocationClose");
            await Until(() => Find<Button>(activeOrganization, "OrgTeacherAllocation").IsFocused &&
                Find<ListBox>(activeOrganization, "OrgReceiptClasses").SelectedItem?.ToString()?.Contains("G2-C15") == true,
                "active organization receipt restores its selected original row and focus");
            await PressScoped(activeOrganization, "OrgClose");

            await SelectSchool(access.NoTermSchoolId);
            var (noTermDirectory, noTermWorkbench) = await OpenTeacherBulk();
            var noTermView = noTermWorkbench.View;
            await Until(() => Find<TextBlock>(noTermView, "AllocationScope").Text?.Contains("无班级分配") == true, "the school with no term loads");
            await Paste(noTermView, "noterm-a\t无学期甲\t048291\t\t\nnoterm-b\t无学期乙\t048292\t\t");
            await Preview(noTermView, () => Find<CheckBox>(noTermView, "AllocationApprove").IsEffectivelyEnabled);
            await Save(noTermView, () => BatchPinReady(noTermView, "Open"));
            var noTermSave = await ReviewBatchPinAsync(noTermView, 2);
            Check(noTermView.GetVisualDescendants().OfType<TextBlock>().Any(row => row.IsEffectivelyVisible && row.Text?.Contains("无指定学期，只建账号") == true),
                "the no-term export names its account-only scope");
            noTermView.SelectBatchPinFile = _ => noTermWorkbench.StorageProvider.TryGetFileFromPathAsync(new Uri(access.NoTermFile));
            Click(noTermSave);
            await Until(() => !BatchPinElement<Border>(noTermView, "Review").IsEffectivelyVisible && !BatchPinReady(noTermView, "Open"),
                "account-only batch PIN export completes");
            await Signal("batch-no-term-file");
            await PressScoped(noTermView, "AllocationEdit");
            await Paste(noTermView, "noterm-leave\t离页核对\t048293\t\t");
            await Preview(noTermView, () => Find<CheckBox>(noTermView, "AllocationApprove").IsEffectivelyEnabled);
            await Save(noTermView, () => BatchPinReady(noTermView, "Open"));
            await PressScoped(noTermView, "AllocationClose");
            await Until(() => !noTermWorkbench.IsVisible && !Find<TeacherBatchPinView>(noTermView, "AllocationPinBatch").IsVisible &&
                !BatchPinReady(noTermView, "Open"), "the result and its PIN actions are cleared when the Windows page closes");
            await Until(() => Find<Button>(noTermDirectory, "TeacherAccountBulk").IsEffectivelyEnabled, "return to the account directory");
            Click(Find<Button>(noTermDirectory, "TeacherAccountBulk"));
            noTermWorkbench = await WindowOf<TeacherAllocationWindow>();
            noTermView = noTermWorkbench.View;
            await Until(() => Find<Button>(noTermView, "AllocationPaste").IsEffectivelyEnabled, "reopened account-only workflow is ready");
            Check(!BatchPinReady(noTermView, "Open"), "reopening the same school cannot reissue an old PIN");
            await Paste(noTermView, "noterm-revoked\t撤权核对\t048294\t\t");
            await Preview(noTermView, () => Find<CheckBox>(noTermView, "AllocationApprove").IsEffectivelyEnabled);
            await Save(noTermView, () => BatchPinReady(noTermView, "Open"));
            var revokedSave = await ReviewBatchPinAsync(noTermView, 1);
            var priorFile = await File.ReadAllBytesAsync(access.NoTermFile);
            noTermView.SelectBatchPinFile = async _ =>
            {
                await Signal("batch-revoke-during-picker");
                return await noTermWorkbench.StorageProvider.TryGetFileFromPathAsync(new Uri(access.NoTermFile));
            };
            Click(revokedSave);
            await Until(() => !BatchPinReady(noTermView, "Open") && !BatchPinElement<Border>(noTermView, "Review").IsEffectivelyVisible &&
                Find<ListBox>(noTermView, "AllocationReceiptRows").Items.Count == 0, "school revocation clears the result and blocks the pending file operation");
            Check((await File.ReadAllBytesAsync(access.NoTermFile)).SequenceEqual(priorFile), "revocation keeps the existing CSV intact");
            if (noTermWorkbench.IsVisible) await PressScoped(noTermView, "AllocationClose");
            if (noTermDirectory.IsEffectivelyVisible) await PressScoped(noTermDirectory, "CloseTeacherAccountsButton");
            await SelectSchool(access.SchoolId);

            var (accounts, regular) = await OpenTeacherBulk();
            var regularView = regular.View;
            await Until(() => Find<TextBlock>(regularView, "AllocationScope").Text?.Contains("秋季学期") == true,
                "regular account entry remains available");
            var pending = Find<WrapPanel>(regularView, "AllocationPendingActions");
            await Until(() => pending.Children.OfType<Button>().Any(button => button.Content?.ToString()?.Contains("开学准备草稿") == true),
                "a distinct button exposes the original other-term pending request");
            Click(pending.Children.OfType<Button>().Single());
            await Until(() => desktop.Windows.OfType<TeacherAllocationWindow>().Any(candidate => candidate != regular &&
                candidate.IsVisible && candidate.View.DisplayedStage == TeacherAllocationView.Stage.Receipt),
                "explicit pending request opens the exact draft receipt");
            var recovered = desktop.Windows.OfType<TeacherAllocationWindow>().Single(candidate => candidate != regular && candidate.IsVisible);
            await Until(() => Status(recovered.View).Contains("找回原回执") && Find<TextBlock>(recovered.View, "AllocationScope").Text!.Contains("开学准备草稿"),
                "server readback retains the original draft term");
            await Signal("draft-access");
            await PressScoped(recovered.View, "AllocationClose");
            await Until(() => !recovered.IsVisible && regular.IsEnabled && Find<TextBlock>(regularView, "AllocationScope").Text!.Contains("秋季学期"),
                "recovery leaves the original regular term unchanged");
            await PressScoped(regularView, "AllocationClose");
            await Until(() => accounts.IsEnabled, "teacher directory restores after workbench");
            Click(Find<Button>(accounts, "CloseTeacherAccountsButton"));
            await Until(() => main.IsEnabled, "regular teacher directory closes");

            await SelectSchool(access.OauthSchoolId);
            var (oauthAccounts, oauth) = await OpenTeacherBulk();
            window = oauth.View;
            await Until(() => Find<Button>(window, "AllocationJson").IsEnabled && Find<TextBlock>(window, "AllocationScope").Text!.Contains("OAuth 邮箱"),
                "email school does not ask for a personal PIN");
            Check(!Find<WrapPanel>(window, "AllocationPendingActions").IsVisible, "a different school has no original school receipts");
            Load(window, JsonSerializer.Serialize(new {assignments = new[]
            {
                new {email = access.InviteEmail, role = "TEACHER", workspaceCodes = new[] {"G2-C07"}},
                new {email = access.RevokeEmail, role = "VIEWER", workspaceCodes = new[] {"G2-C08"}}
            }}));
            await Preview(window, () => Differences(window).Count(row => row.Action == "ADD" && row.After?.Contains("待认领") == true) == 2);
            await Save(window, () => Status(window).Contains("服务器已确认"));
            await Signal("invitation-pending");
            var invitations = Find<ListBox>(window, "AllocationReceiptRows");
            invitations.SelectedItem = invitations.Items.Cast<object>().Single(row => row.ToString()!.Contains(access.InviteEmail));
            await PressScoped(window, "AllocationReceiptPerson");
            await Until(() => window.DisplayedStage == TeacherAllocationView.Stage.BySpace &&
                (Find<ListBox>(window, "AllocationDirectorySpaces").SelectedItem as AllocationWorkspace)?.Code == "G2-C07",
                "an email-only receipt opens its specific invitation and space");
            spaceList = Find<ListBox>(window, "AllocationDirectorySpaces");
            spaceList.SelectedItem = spaceList.Items.OfType<AllocationWorkspace>().Single(row => row.Code == "G2-C08");
            var revoke = Find<StackPanel>(window, "AllocationSpaceDetails").GetVisualDescendants().OfType<Button>()
                .Single(row => Equals(row.Content, "把撤回加入教师表"));
            Click(revoke);
            await Preview(window, () => Differences(window).Any(row => row.Action == "REMOVE" && row.After?.Contains("撤回") == true));
            await Capture(oauth, output, "06-revoke-one-email-invitation");
            await Save(window, () => Status(window).Contains("服务器已确认"));
            await Signal("oauth-complete");
            await PressScoped(window, "AllocationBySpace");
            await PressScoped(window, "AllocationRefresh");
            await Until(() => Status(window).Contains("重新读取"), "after real OAuth callback and exchange, re-read actual invitation state");
            spaceList.SelectedItem = spaceList.Items.OfType<AllocationWorkspace>().Single(row => row.Code == "G2-C07");
            await Until(() => Find<StackPanel>(window, "AllocationSpaceDetails").GetVisualDescendants().OfType<TextBlock>()
                .Any(row => row.Text == "邮箱已认领"), "only the verified, redeemed invitation is marked claimed");
            await Capture(oauth, output, "07-email-claimed-live");
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

        async Task VerifyLiveBatchAsync(TeacherAllocationView page)
        {
            if (page.DisplayedStage != TeacherAllocationView.Stage.Edit) await PressScoped(page, "AllocationEdit");
            await Paste(page, "短账号\t姓名\t初始 PIN\t班级代码\t角色\n" +
                "delivery-a\t=周,老师\t012340\tG2-C07\t教师\n" +
                "delivery-b\t=周,老师\t003482\tG2-C07\t教师\n" +
                "delivery-solo\t另存一位\t018341\tG2-C07\t教师\n" +
                "delivery-changed\t随后重置\t019341\tG2-C07\t教师\n" +
                $"viewer\tviewer\t{access.IgnoredPin}\tG2-C08\t助教");
            await Preview(page, () => Find<CheckBox>(page, "AllocationApprove").IsEffectivelyEnabled);
            await Save(page, () => BatchPinReady(page, "Open"));
            var receiptList = Find<ListBox>(page, "AllocationReceiptRows");
            receiptList.SelectedItem = receiptList.Items.Cast<object>().Single(row => row.ToString()!.Contains("delivery-solo"));
            var soloFile = access.BatchFile + ".single.txt";
            try
            {
                await File.WriteAllTextAsync(soloFile, "");
                page.SelectPinFile = _ => main.StorageProvider.TryGetFileFromPathAsync(new Uri(soloFile));
                await PressScoped(page, "AllocationSavePin");
                await Until(() => Status(page).Contains("已另存"), "the single teacher was delivered before the batch");
                Check((await File.ReadAllTextAsync(soloFile)).EndsWith("PIN 018341", StringComparison.Ordinal), "one-time individual delivery used the applied PIN");
            }
            finally { File.Delete(soloFile); }
            main.Width = 1160;
            main.Height = 780;
            var save = await ReviewBatchPinAsync(page, 3);
            var rows = BatchPinElement<ListBox>(page, "Rows").Items.OfType<TeacherPinBatchDisplay>().ToArray();
            Check(rows.Count(row => row.Identity.StartsWith("=周,老师", StringComparison.Ordinal)) == 2 &&
                rows.Any(row => row.Identity.Contains("delivery-solo") && row.State == "已交付") &&
                rows.Any(row => row.Identity.Contains("viewer") && row.State == "沿用原凭据"),
                "the batch distinguishes identical names and excludes delivered and existing accounts");
            Check(page.GetVisualDescendants().OfType<TextBlock>().Any(row => row.IsEffectivelyVisible && row.Text == "师生网页 https://school.test/campus/"),
                "the school address preserves the configured website path, independent of the KV origin");
            await CaptureWithFrame(main, output, "14-wide-teacher-batch-review");
            main.Width = 840;
            main.Height = 480;
            await Until(() => main.ClientSize is { Width: <= 845, Height: <= 485 } && FitsBatchWindow(main, save) &&
                FitsBatchWindow(main, BatchPinElement<Button>(page, "Cancel")), "narrow batch review keeps both actions in view");
            await CaptureWithFrame(main, output, "15-narrow-teacher-batch-review");
            await CancelOrganizationPickerAsync(main, save, "另存本次教师初始 PIN");
            Check(await File.ReadAllTextAsync(access.BatchFile) == "original", "cancelling the actual Windows save dialog leaves the file intact");
            page.SelectBatchPinFile = async _ =>
            {
                await Signal("batch-reset-during-picker");
                return await main.StorageProvider.TryGetFileFromPathAsync(new Uri(access.BatchFile));
            };
            Click(save);
            await Until(() => BatchPinElement<TextBlock>(page, "Status").Text?.Contains("文件未修改") == true && !save.IsEffectivelyVisible,
                "reset during file selection refuses the entire preview instead of silently exporting a subset");
            Check(await File.ReadAllTextAsync(access.BatchFile) == "original", "a changed account leaves the destination byte-for-byte intact");
            await CaptureWithFrame(main, output, "16-narrow-teacher-batch-reset-conflict");
            Click(BatchPinElement<Button>(page, "Cancel"));
            save = await ReviewBatchPinAsync(page, 2);
            page.SelectBatchPinFile = _ => main.StorageProvider.TryGetFileFromPathAsync(new Uri(access.BatchFile));
            using (var locked = File.Open(access.BatchFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Click(save);
                await Until(() => BatchPinReady(page, "Save") && BatchPinElement<TextBlock>(page, "Status").Text?.Contains("文件未保存") == true,
                    "a locked destination retains the review and permits retry");
            }
            Check(await File.ReadAllTextAsync(access.BatchFile) == "original" &&
                !Directory.EnumerateFiles(Path.GetDirectoryName(access.BatchFile)!, ".np-handoff-*.tmp").Any(),
                "file write failure preserves existing content and cleans temporary output");
            Click(save);
            await Until(() => !BatchPinElement<Border>(page, "Review").IsEffectivelyVisible && !BatchPinReady(page, "Open"),
                "the approved two-row file was saved and consumed");
            await Signal("batch-ordinary-file");
        }

        async Task<(TeacherAccountsView Owner, TeacherAllocationWindow Window)> OpenTeacherBulk()
        {
            var entry = Find<Button>(main, "OpenTeacherAccountsButton");
            await Until(() => entry.IsEffectivelyVisible && entry.IsEnabled, "normal teacher account entry");
            Click(entry);
            var owner = await AcademicPageAsync<TeacherAccountsView>(main);
            var bulk = Find<Button>(owner, "TeacherAccountBulk");
            await Until(() => bulk.IsEffectivelyVisible && bulk.IsEnabled, "teacher bulk action available");
            Click(bulk);
            return (owner, await WindowOf<TeacherAllocationWindow>());
        }

        async Task Paste(TeacherAllocationView window, string tabSeparated)
        {
            var clipboard = TopLevel.GetTopLevel(window)?.Clipboard ?? throw new InvalidOperationException("Windows clipboard unavailable.");
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

        async Task OpenLegacyFile(TeacherAllocationView window, string file)
        {
            Console.WriteLine($"Select fixture in Windows picker: {file}");
            var button = Find<Button>(window, "AllocationJson");
            Click(button);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            while (!button.IsEnabled) await Task.Delay(50, timeout.Token);
        }

        async Task Preview(TeacherAllocationView window, Func<bool> ready)
        {
            await PressScoped(window, "AllocationPreviewButton");
            await Until(() => window.DisplayedStage == TeacherAllocationView.Stage.Review && ready(), "server preview is ready");
        }

        async Task Save(TeacherAllocationView window, Func<bool> ready)
        {
            Find<CheckBox>(window, "AllocationApprove").IsChecked = true;
            await PressScoped(window, "AllocationSave");
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

        static string Status(TeacherAllocationView window) => Find<TextBlock>(window, "AllocationStatus").Text ?? "";
        static IEnumerable<AllocationDifference> Differences(TeacherAllocationView window) =>
            Find<ListBox>(window, "AllocationDifferences").Items.OfType<AllocationDifference>();
        static void Load(TeacherAllocationView window, string document) => window.LoadAllocationText(document, legacy: true);

    }

}
