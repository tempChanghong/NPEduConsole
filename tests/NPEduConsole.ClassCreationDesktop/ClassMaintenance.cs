using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private sealed record ClassMaintenanceAccess(string Origin, string SchoolId, string SchoolCode, string Username,
        string Password, string ClassId, string ConcurrentClassId, string ScreenId, bool AllowClassMaintenanceTestWrites);

    private static int StartClassMaintenance(string fixturePath, string output, bool reopen)
    {
        var access = JsonSerializer.Deserialize<ClassMaintenanceAccess>(File.ReadAllText(fixturePath), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Missing maintenance fixture.");
        Check(access.AllowClassMaintenanceTestWrites && IsLoopback(access.Origin), "Use an isolated loopback maintenance fixture.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyMaintenanceAsync(access, output, reopen); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyMaintenanceAsync(ClassMaintenanceAccess access, string output, bool reopen)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        try
        {
            if (!reopen) VerifyClassDeactivationImpactView();
            var main = (MainWindow)desktop.MainWindow!;
            main.Width = reopen ? 840 : 1160;
            main.Height = reopen ? 480 : 780;
            var schools = Find<ComboBox>(main, "SchoolPicker");
            var classes = Find<ListBox>(main, "ClassPicker");
            var inactive = Find<CheckBox>(main, "InactiveClassesFilter");
            var entry = Find<Button>(main, "OpenClassMaintenanceButton");
            Find<TextBox>(main, "ServerAddress").Text = access.Origin;
            Find<TextBox>(main, "SchoolCode").Text = access.SchoolCode;
            Find<TextBox>(main, "Username").Text = access.Username;
            Find<TextBox>(main, "Password").Text = access.Password;
            Click(Find<Button>(main, "LoginButton"));
            await Until(() => schools.Items.Cast<School>().Any(school => school.Id == access.SchoolId), "authorized school");
            schools.SelectedItem = schools.Items.Cast<School>().Single(school => school.Id == access.SchoolId);
            Click(Find<Button>(main, "OpenClassWorkspaceButton"));
            await Until(() => classes.IsEffectivelyVisible && classes.ItemCount >= 2, "active school class list");
            if (reopen)
            {
                Check(!classes.Items.Cast<Classroom>().Any(room => room.Id == access.ClassId), "Inactive class is absent from the normal list.");
                inactive.IsChecked = true;
                await Until(() => classes.Items.Cast<Classroom>().Any(room => room.Id == access.ClassId && !room.IsActive), "the original class appears under 已停用");
                SelectRoom();
                Check(entry.Content is "重新启用" && !Find<Button>(main, "OpenClassRosterButton").IsEnabled, "Inactive row has a restore entry.");
                await Capture(main, output, "01-inactive-filter");
                var restore = await OpenMaintenance();
                await Until(() => Find<Button>(restore, "ReactivateClassButton").IsEffectivelyVisible, "restore confirmation");
                Click(Find<Button>(restore, "ReactivateClassButton"));
                await Until(() => inactive.IsChecked == false && classes.SelectedItem is Classroom room && room.Id == access.ClassId &&
                    room.IsActive && room.Screens.Any(screen => screen.Id == access.ScreenId), "original class is selected in active list");
                await VerifyRoster();
                Finish("PASS: Windows reopened the inactive class by ID, restored it, and returned to its original roster and screen.", 0);
                return;
            }

            SelectRoom();
            await VerifyRoster();
            var window = await OpenMaintenance();
            var name = Find<TextBox>(window, "MaintenanceName");
            var code = Find<TextBox>(window, "MaintenanceCode");
            var save = Find<Button>(window, "SaveMaintenanceButton");
            var feedback = Find<TextBlock>(window, "MaintenanceFeedback");
            await Until(() => save.IsEffectivelyVisible ||
                !Find<ProgressBar>(window, "MaintenanceProgress").IsVisible && !string.IsNullOrWhiteSpace(feedback.Text),
                "class settings loaded");
            Check(save.IsEffectivelyVisible, "Could not load class settings: " + feedback.Text);
            await Until(() => TopLevel.GetTopLevel(window) == main &&
                Find<TextBlock>(window, "MaintenanceSchool").Text?.Contains("启用中") == true,
                "the real main HWND shows the original class and active term");
            await CaptureWithFrame(main, output, "00a-wide-class-edit");
            main.Width = 840;
            main.Height = 480;
            await Until(() => main.ClientSize is { Width: <= 845, Height: <= 485 }, "actual narrow class editing window");
            Find<CheckBox>(window, "MaintenanceStudentSelectable").Focus();
            Check(FitsBatchWindow(main, save), "the fixed save action remains accessible from the last class field");
            await CaptureWithFrame(main, output, "00b-narrow-class-edit");
            var newName = "高二（1）班 · 已修正";
            name.Text = newName;
            await PressScoped(window, "ReadDeactivationImpactButton");
            await Until(() => feedback.IsEffectivelyVisible && feedback.Text?.Contains("未保存的班级修改") == true,
                "a dirty form explains why it cannot start the stop review");
            Click(Find<Button>(window, "CloseMaintenanceButton"));
            await Until(() => Find<Button>(window, "KeepMaintenanceDraft").IsFocused, "return asks about the unsaved class name");
            Click(Find<Button>(window, "KeepMaintenanceDraft"));
            Check(name.Text == newName && name.IsFocused, "canceling return preserves the name draft and restores focus");
            code.Text = "G2-2";
            Find<CheckBox>(window, "MaintenanceStudentSelectable").IsChecked = false;
            Click(save);
            await Until(() => feedback.Text?.Contains("已有教学空间使用此代码") == true && save.IsEnabled, "duplicate code can be corrected locally");
            Check(code.Text == "G2-2" && name.Text == newName && code.IsFocused, "Code conflict preserves the draft and focuses code.");
            await CaptureWithFrame(main, output, "01-code-conflict");

            code.Text = "G2.DESK";
            Click(save);
            var check = Find<Button>(window, "CheckMaintenanceButton");
            await Until(() => feedback.Text?.Contains("另一位管理员") == true && check.IsVisible && !save.IsEnabled,
                "a stale concurrent write requires server review");
            Click(check);
            await Until(() => Find<TextBlock>(window, "MaintenanceServerState").Text?.Contains("另一管理员最新名称") == true && save.IsEnabled,
                "latest server data is shown beside the original draft");
            Check(name.Text == newName && code.Text == "G2.DESK", "Concurrent editor retains both draft fields.");
            await CaptureWithFrame(main, output, "02-concurrent-server-value");
            Click(save);
            await Until(() => classes.SelectedItem is Classroom room && room.Id == access.ClassId && room.Name == newName &&
                room.Screens.Any(screen => screen.Id == access.ScreenId), "rename reselects by original ID and keeps screen");
            await VerifyRoster();

            window = await OpenMaintenance();
            var summary = Find<ClassDeactivationImpactView>(window, "MaintenanceImpactSummary");
            var confirm = Find<Button>(window, "ConfirmDeactivateButton");
            await PressScoped(window, "ReadDeactivationImpactButton");
            await Until(() => Find<StackPanel>(window, "MaintenanceImpactPanel").IsEffectivelyVisible,
                "deactivation opens its own impact review");
            await Until(() => summary.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "无法读取停用影响") &&
                Find<Button>(window, "RefreshImpactButton").IsVisible && !confirm.IsEnabled, "failed impact never looks like no relations or permits deactivation");
            await Capture(window, output, "03-impact-unavailable");
            Click(Find<Button>(window, "RefreshImpactButton"));
            await Until(() => confirm.IsEnabled && SummaryContains("大屏绑定 1 台") && SummaryContains("学生 1 人") && SummaryContains("任课安排 1 条") &&
                SummaryContains("生效中的作业或通知 1 条"),
                "real preview shows bound screen and active notice");
            Check(Find<TextBlock>(window, "MaintenanceScreenEffect") is { IsEffectivelyVisible: true, Text: { } warning } &&
                warning.Contains("重新配对"), "The existing native device authorization impact is disclosed before confirmation.");
            Check(Find<Button>(window, "CancelImpactButton").IsFocused && Find<TextBlock>(window, "ConfirmDeactivateLabel").Text?.Contains(newName) == true,
                "impact focuses the safe return action and names the exact target in the stop button");
            await CaptureWithFrame(main, output, "04-impact-reviewed");
            Click(confirm);
            await Until(() => Find<TextBlock>(window, "MaintenanceFeedback").Text?.Contains("关联已变化") == true &&
                confirm.IsEnabled && SummaryContains("大屏绑定 2 台"), "stale preview is replaced and must be confirmed again");
            Check(window.IsVisible, "A changed impact does not close or deactivate the class.");
            await Capture(window, output, "05-impact-changed");
            Click(confirm);
            await Until(() => inactive.IsChecked == true && classes.SelectedItem is Classroom stopped && stopped.Id == access.ClassId &&
                !stopped.IsActive, "deactivation selects the same ID in inactive list");
            await Capture(main, output, "06-inactive");
            await VerifyDraftAfterConcurrentDeactivation();
            Finish("PASS: Windows impact component, draft retention, concurrent edit, scoped rename, impact failure, fresh confirmation, deactivation, and concurrent-deactivation recovery.", 0);

            bool SummaryContains(string text) => summary.GetVisualDescendants().OfType<TextBlock>()
                .Any(item => item.IsEffectivelyVisible && item.Text?.Contains(text) == true);
            void SelectRoom() => classes.SelectedItem = classes.Items.Cast<Classroom>().Single(room => room.Id == access.ClassId);
            async Task<ClassMaintenanceView> OpenMaintenance()
            {
                Click(entry);
                return await AcademicPageAsync<ClassMaintenanceView>(main);
            }
            async Task VerifyDraftAfterConcurrentDeactivation()
            {
                inactive.IsChecked = false;
                await Until(() => classes.Items.Cast<Classroom>().Any(room => room.Id == access.ConcurrentClassId && room.IsActive),
                    "the separate active class for the deactivation race");
                classes.SelectedItem = classes.Items.Cast<Classroom>().Single(room => room.Id == access.ConcurrentClassId);
                var page = await OpenMaintenance();
                var draftName = Find<TextBox>(page, "MaintenanceName");
                var draftCode = Find<TextBox>(page, "MaintenanceCode");
                var selectable = Find<CheckBox>(page, "MaintenanceStudentSelectable");
                var saveDraft = Find<Button>(page, "SaveMaintenanceButton");
                var checkServer = Find<Button>(page, "CheckMaintenanceButton");
                var reactivate = Find<Button>(page, "ReactivateClassButton");
                await Until(() => saveDraft.IsEffectivelyVisible && saveDraft.IsEffectivelyEnabled && !string.IsNullOrEmpty(draftCode.Text),
                    "active original form is loaded before editing");
                draftName.Text = "桌面待保存名称";
                draftCode.Text = "DESK.RECOVER";
                selectable.IsChecked = false;
                Click(saveDraft);
                await Until(() => checkServer.IsEffectivelyVisible && checkServer.IsEffectivelyEnabled && !saveDraft.IsEnabled,
                    "the other administrator's stop rejects the stale desktop write");
                Click(checkServer);
                await Until(() => Find<TextBlock>(page, "MaintenanceSchool").Text?.Contains("班级停用") == true &&
                    Find<TextBlock>(page, "MaintenanceServerState").Text?.Contains("网页已停用的原班") == true && reactivate.IsEffectivelyEnabled,
                    "server review reports the other administrator's inactive class and actual name");
                Check(draftName.Text == "桌面待保存名称" && draftCode.Text == "DESK.RECOVER" && selectable.IsChecked == false,
                    "Checking the inactive server class erased the original unsaved draft.");
                Check(draftName.IsEffectivelyVisible && draftName.IsReadOnly && !saveDraft.IsEffectivelyVisible,
                    "The draft is visible for reference and cannot be saved while the class is inactive.");
                var draftSelectable = Find<TextBlock>(page, "MaintenanceInactiveSelectable");
                await Until(() => draftSelectable is { IsEffectivelyVisible: true, Text: "学生自选：不允许" } &&
                    draftSelectable.TranslatePoint(new Point(0, draftSelectable.Bounds.Height), page) is { } draftBottom &&
                    Find<TextBlock>(page, "MaintenanceFeedback").TranslatePoint(default, page) is { } footer && draftBottom.Y < footer.Y - 12,
                    "all retained fields fit above the fixed footer at 840 by 480");
                await CaptureWithFrame(main, output, "07-concurrent-stop-draft");

                Click(Find<Button>(page, "CloseMaintenanceButton"));
                var keepDraft = Find<Button>(page, "KeepMaintenanceDraft");
                await Until(() => keepDraft.IsEffectivelyVisible && keepDraft.IsFocused, "return protects the inactive draft");
                Click(keepDraft);
                Check(draftName.IsFocused && draftName.Text == "桌面待保存名称", "Canceling return restores focus to the retained draft.");
                Click(reactivate);
                await Until(() => page.IsEffectivelyVisible && saveDraft.IsEffectivelyVisible && saveDraft.IsEffectivelyEnabled && !draftName.IsReadOnly,
                    "deliberate reactivation returns to the original editable draft without saving it");
                Check(draftName.Text == "桌面待保存名称" && draftCode.Text == "DESK.RECOVER" && selectable.IsChecked == false && draftName.IsFocused,
                    "Reactivation keeps the class draft intact and brings focus back to the editor.");
                await CaptureWithFrame(main, output, "08-reactivated-needs-save");
                Click(saveDraft);
                await Until(() => classes.SelectedItem is Classroom room && room.Id == access.ConcurrentClassId && room.Name == "桌面待保存名称",
                    "explicit save returns to the recovered class by ID");
            }
            async Task VerifyRoster()
            {
                Click(Find<Button>(main, "OpenClassRosterButton"));
                var roster = await AcademicPageAsync<ClassRosterView>(main);
                await Until(() => roster.GetVisualDescendants().OfType<TextBox>().Any(field => field.Text == "原班模拟学生"), "original student roster is visible");
                if (reopen) await CaptureWithFrame(main, output, "02-original-roster");
                Click(Find<Button>(roster, "CloseRosterButton"));
                await Until(() => !roster.IsEffectivelyVisible && Find<Button>(main, "OpenClassRosterButton").IsFocused,
                    "return restores focus on the same class roster button");
            }
        }
        catch (Exception error)
        {
            if (desktop.MainWindow is MainWindow main)
                await CaptureWithFrame(main, output, "failure");
            Finish("FAIL: " + error, 1);
        }

        void Finish(string message, int exitCode)
        {
            File.WriteAllText(Path.Combine(output, "result.txt"), message);
            Console.WriteLine(message);
            desktop.Shutdown(exitCode);
        }
    }
}
