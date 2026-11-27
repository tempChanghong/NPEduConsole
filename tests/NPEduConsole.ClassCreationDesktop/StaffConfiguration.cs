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
    private sealed record StaffPlanFixture(string Origin, string SchoolId, string SchoolCode, string OtherId, string Pin,
        string TermName, string CurrentId, string SavedInput, string WrongInput, string InterruptedInput, string ChangedInput,
        string LostInput, string Handoff, string LocalSettings, bool Isolated);

    private static int StartStaffConfiguration(string path, string output, bool reopen)
    {
        var fixture = JsonSerializer.Deserialize<StaffPlanFixture>(File.ReadAllText(path), JsonSerializerOptions.Web)!;
        Check(fixture.Isolated && IsLoopback(fixture.Origin), "Use the disposable full-staff fixture.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure(() => new ConsoleApplication { LastServerFile = fixture.LocalSettings }).UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyStaffConfigurationAsync(fixture, output, reopen); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyStaffConfigurationAsync(StaffPlanFixture fixture, string output, bool reopen)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        var handle = main.TryGetPlatformHandle()?.Handle;
        using var relay = new HttpClient { BaseAddress = new Uri(fixture.Origin) };
        try
        {
            main.Width = 1160;
            main.Height = 780;
            if (reopen)
            {
                await LoginAsync("admin2");
                var outsider = await OpenAsync();
                Check(Ready(outsider, "StaffPlanOpen") && !Find<WrapPanel>(outsider, "StaffPlanPendingBar").IsVisible &&
                    !Find<Button>(outsider, "StaffPlanCheck").IsVisible, "另一位本校管理员重开不能看到原办理人编号");
                await SelectFileAsync(outsider, fixture.SavedInput);
                await Signal("revoke");
                Click(Find<Button>(outsider, "StaffPlanPreview"));
                await Until(() => !outsider.IsEffectivelyVisible && Find<ComboBox>(main, "SchoolPicker").Items.OfType<School>().All(row => row.Id != fixture.SchoolId),
                    "撤权清除原专页和学校选择");
                Check(string.IsNullOrEmpty(Text(outsider, "StaffPlanFile")) && PlanRows(outsider).Length == 0, "撤权关闭原文件和内容");
                Click(main.GetVisualDescendants().OfType<Button>().Single(row => row.IsEffectivelyVisible && row.Content is "退出登录"));
                await Until(() => Ready(main, "LoginButton"), "logout between independently authorized accounts");
                await LoginAsync("admin");
                var recovered = await OpenAsync(awaitReceipt: true);
                Check(Ready(recovered, "StaffPlanCheck") && PlanRows(recovered).Length == 0 &&
                    !Find<Button>(recovered, "StaffPlanSave").IsVisible, "网关 200 缺回执行时保留只读核对，不能误认保存或崩溃");
                Click(Find<Button>(recovered, "StaffPlanCheck"));
                await Until(() => Ready(recovered, "StaffPlanNew"), "本人原编号取回一次真实回执");
                Check(PlanRows(recovered).Single().Saved?.Username == "staff-lost" && !Find<WrapPanel>(recovered, "StaffPlanPendingBar").IsVisible &&
                    PlanAction(recovered, "StaffPlanSavePin") is null && !BatchPinReady(recovered, "Open"),
                    "进程重开后原办理人只读取同一回执而不能再交付 PIN");
                var changes = PlanRows(recovered).Single().Saved!.Changes ?? [];
                Check(changes.Any(row => row.Kind == "TEACHING" && row.TargetId == fixture.CurrentId) &&
                    changes.Any(row => row.Kind == "TEACHING" && row.Label.Contains("走班 A1")) &&
                    changes.Any(row => row.Kind == "ACCESS") && changes.Any(row => row.Kind == "CLASS_DUTY" && row.TargetId == fixture.CurrentId),
                    "只有只读回执时仍可按原对象看到任课、直接访问和班级岗位");
                Check(recovered.GetVisualDescendants().OfType<TextBlock>().Any(row => row.Text == "本次回执记录的变化"), "逐项结果清楚标明保存时点");
                await CaptureWithFrame(main, output, "06-wide-original-receipt-reopen");
                Console.WriteLine("PASS: independent Windows restart and actor-scoped pending recovery.");
                desktop.Shutdown(0);
                return;
            }

            await LoginAsync("admin");
            var opening = await OpenChecklistAsync();
            Click(Find<Button>(opening, "OpeningSecondary"));
            var ordinary = await AcademicPageAsync<TeacherAllocationView>(main);
            await Until(() => Ready(ordinary, "AllocationJson"), "普通分配可读取原文件");
            ordinary.LoadAllocationText("""{"assignments":[{"username":"staff-incumbent","name":"现任老师","workspaceCodes":["STAFF-C07"]}]}""", legacy: true);
            Check(Ready(ordinary, "AllocationPreviewButton"), "普通 assignments 文件仍可办理");
            ordinary.LoadAllocationText(await File.ReadAllTextAsync(fixture.WrongInput), legacy: true);
            Check(!Find<Button>(ordinary, "AllocationOpenStaffConfiguration").IsVisible && !Ready(ordinary, "AllocationPreviewButton") &&
                Text(ordinary, "AllocationFooter").Contains("1 位教师"), "历史范围的 FULL 文件不能替换原分配表或偷偷跳转");
            ordinary.LoadAllocationText(await File.ReadAllTextAsync(fixture.SavedInput), legacy: true);
            Check(Ready(ordinary, "AllocationOpenStaffConfiguration") && ordinary.IsEffectivelyVisible &&
                Text(ordinary, "AllocationFooter").Contains("1 位教师"), "匹配的 FULL 须等明确选择，普通表及范围仍在");
            Click(Find<Button>(ordinary, "AllocationOpenStaffConfiguration"));
            var plan = await AcademicPageAsync<StaffConfigurationView>(main);
            await Until(() => Ready(plan, "StaffPlanPreview"), "完整文件进入专用预览");
            Check(TopLevel.GetTopLevel(plan) is Window window && window.TryGetPlatformHandle()?.Handle == handle, "清单和导入走同一主 HWND");
            Click(Find<Button>(plan, "StaffPlanPreview"));
            await Until(() => PlanAction(plan, "StaffPlanResolve-TEACHING") is { IsEffectivelyEnabled: true }, "服务端返回具体任课冲突");
            Check(PlanRows(plan).Count(row => row.Title == "周老师") == 2 && PlanRows(plan).Any(row => row.Teacher?.Username == "staff-same-b"),
                "同名教师由真实短账号区分");
            await CaptureWithFrame(main, output, "01-wide-teaching-and-duty-conflicts");

            main.Width = 840;
            main.Height = 480;
            await Until(() => main.ClientSize is { Width: <= 845, Height: <= 485 } && Ready(plan, "StaffPlanDetails"), "真实窄窗教师列表");
            Click(Find<Button>(plan, "StaffPlanDetails"));
            await Until(() => Ready(plan, "StaffPlanBackList"), "窄窗到当前教师详情");
            await CaptureWithFrame(main, output, "02-narrow-teaching-and-duty-conflicts");
            Click(PlanAction(plan, "StaffPlanResolve-TEACHING")!);
            var teaching = await AcademicPageAsync<TeachingView>(main);
            await Until(() => Text(teaching, "TeachingPinnedSubject") == "师资物理" &&
                Text(teaching, "TeachingContext").Contains("师资核对"), "任课冲突直达启用学期原班科");
            await Signal("teaching");
            Click(Find<Button>(teaching, "CloseTeachingButton"));
            if (Ready(teaching, "TeachingDiscard")) Click(Find<Button>(teaching, "TeachingDiscard"));
            await Until(() => PlanAction(plan, "StaffPlanResolve-CLASS_DUTY") is { IsEffectivelyEnabled: true } &&
                PlanAction(plan, "StaffPlanResolve-TEACHING") is null, "任课返回后重读，仍在原教师，只剩岗位冲突");
            Click(PlanAction(plan, "StaffPlanResolve-CLASS_DUTY")!);
            var duty = await WindowOf<StaffResponsibilitiesWindow>();
            await Until(() => Text(duty, "StaffHeading").Contains("高二（7）班") && Text(duty, "StaffScope").Contains(fixture.TermName), "岗位冲突直达该学期原班");
            await Signal("duty");
            Click(Find<Button>(duty, "StaffClose"));
            await Until(() => Find<CheckBox>(plan, "StaffPlanApprove").IsEffectivelyEnabled && Find<CheckBox>(plan, "StaffPlanApprove").IsVisible &&
                Text(plan, "StaffPlanSummary").Contains("0 个阻断"), "岗位返回后自动重预览并保持选中教师");
            Check(Find<ListBox>(plan, "StaffPlanRows").SelectedItem is StaffPlanRow { Teacher.Username: "staff-same-a" }, "冲突处理返回同一教师");
            await CaptureWithFrame(main, output, "03-narrow-verified-teacher");
            await Signal("changed");
            Submit(plan);
            await Until(() => Text(plan, "StaffPlanStatus").Contains("本次没有保存") && Ready(plan, "StaffPlanPreview"), "确认过期，保留原文件");
            await Signal("stale");
            Click(Find<Button>(plan, "StaffPlanPreview"));
            await WaitForApproval(plan);
            Submit(plan);
            await Until(() => PlanRows(plan).Any(row => row.Saved?.Username == "staff-same-b"), "实际导入完成");
            main.Width = 1160;
            main.Height = 780;
            Find<ListBox>(plan, "StaffPlanRows").SelectedItem = PlanRows(plan).Single(row => row.Saved?.Username == "staff-same-b");
            await Until(() => PlanAction(plan, "StaffPlanSavePin") is { IsEffectivelyVisible: true }, "仅新账号能交付本次实际 PIN");
            await CaptureWithFrame(main, output, "04-wide-applied-and-initial-credential-action");
            var savePins = await ReviewBatchPinAsync(plan, 2);
            var rowsToDeliver = BatchPinElement<ListBox>(plan, "Rows").Items.OfType<TeacherPinBatchDisplay>().ToArray();
            Check(rowsToDeliver.Any(row => row.Identity.Contains("staff-same-a") && row.State == "沿用原凭据") &&
                rowsToDeliver.Any(row => row.Identity.Contains("staff-same-b") && row.State == "可交付") &&
                rowsToDeliver.Any(row => row.Identity.Contains("staff-generated") && row.State == "可交付"),
                "完整方案同时核对原短账号、同名新账号与服务器生成的初始 PIN");
            await CaptureWithFrame(main, output, "07-wide-staff-batch-review");
            main.Width = 840;
            main.Height = 480;
            await Until(() => main.ClientSize is { Width: <= 845, Height: <= 485 } && FitsBatchWindow(main, savePins), "完整师资窄窗主操作可达");
            await CaptureWithFrame(main, output, "08-narrow-staff-batch-review");
            plan.SelectBatchPinFile = _ => main.StorageProvider.TryGetFileFromPathAsync(new Uri(fixture.Handoff));
            Click(savePins);
            await Until(() => !BatchPinElement<Border>(plan, "Review").IsEffectivelyVisible && !BatchPinReady(plan, "Open") &&
                PlanAction(plan, "StaffPlanSavePin") is null, "一次交付后批量及个人动作均结束");
            Click(Find<Button>(plan, "StaffPlanClose"));
            await Until(() => ordinary.IsEffectivelyVisible && Ready(ordinary, "AllocationPreviewButton"), "返回同一普通分配原表");
            Check(Text(ordinary, "AllocationFooter").Contains("1 位教师"), "原教师表和目标继续保留");
            Check(Find<ComboBox>(ordinary, "AllocationExisting").Items.OfType<AllocationAccount>().Any(row => row.Username == "staff-same-b"),
                "返回普通表后同学期的教师目录已刷新");
            Check(!ordinary.CanClose(), "原普通分配表仍作为未保存草稿处理");
            Click(Find<Button>(ordinary, "AllocationDiscard"));

            opening = await OpenChecklistAsync();
            await Until(() => Ready(opening, "OpeningPrimary"), "开学配置原教师账号入口可继续使用");
            Click(Find<Button>(opening, "OpeningPrimary"));
            var teacherAccounts = await AcademicPageAsync<TeacherAccountsView>(main);
            await Until(() => Ready(teacherAccounts, "TeacherAccountBulk"), "教师账号的原批量分配入口可达");
            Click(Find<Button>(teacherAccounts, "TeacherAccountBulk"));
            var teacherBulk = await WindowOf<TeacherAllocationWindow>();
            await Until(() => Ready(teacherBulk.View, "AllocationJson"), "批量分配旧窗口已读取作用域");
            teacherBulk.View.LoadAllocationText(await File.ReadAllTextAsync(fixture.SavedInput), legacy: true);
            Click(Find<Button>(teacherBulk.View, "AllocationOpenStaffConfiguration"));
            await Until(() => teacherBulk.GetVisualDescendants().OfType<StaffConfigurationView>().Any(view => Ready(view, "StaffPlanPreview")),
                "从账户批量入口切专页，沿用原窗口并等明确确认");
            var embedded = teacherBulk.GetVisualDescendants().OfType<StaffConfigurationView>().Single();
            Check(TopLevel.GetTopLevel(embedded) == teacherBulk && !teacherBulk.View.IsEffectivelyVisible, "账户批量入口沿用原宿主");
            Click(Find<Button>(embedded, "StaffPlanClose"));
            await Until(() => teacherBulk.View.IsEffectivelyVisible && Ready(teacherBulk.View, "AllocationJson"), "旧批量窗口可返回普通教师表");
            Click(Find<Button>(teacherBulk.View, "AllocationClose"));
            Click(Find<Button>(teacherAccounts, "CloseTeacherAccountsButton"));

            plan = await OpenAsync();
            await CancelOrganizationPickerAsync(main, Find<Button>(plan, "StaffPlanOpen"), "选择完整师资 JSON");
            await LoadAsync(plan, fixture.InterruptedInput);
            await Signal("miss");
            Submit(plan);
            await Until(() => Ready(plan, "StaffPlanOpen") && Text(plan, "StaffPlanStatus").Contains("未找到本人的回执"), "未到达先只读查无");
            await LoadAsync(plan, fixture.InterruptedInput);
            Check(Find<Button>(plan, "StaffPlanSave").Content is "确认按原编号重试", "重新选同一证明后仍用原编号");
            Submit(plan);
            await Until(() => PlanRows(plan).Any(row => row.Saved?.Username == "staff-not-sent"), "原编号重试实际成功一次");

            Click(Find<Button>(plan, "StaffPlanNew"));
            await LoadAsync(plan, fixture.ChangedInput);
            await Signal("miss");
            Submit(plan);
            await Until(() => Ready(plan, "StaffPlanCancelRequest"), "有本人 404 才开放结束原申请");
            await Signal("alterdate");
            await SelectFileAsync(plan, fixture.ChangedInput);
            Click(Find<Button>(plan, "StaffPlanPreview"));
            await Until(() => Text(plan, "StaffPlanStatus").Contains("不能按原编号提交"), "重预览版本与原申请不同");
            Click(Find<Button>(plan, "StaffPlanCancelRequest"));
            await Until(() => Find<Button>(plan, "StaffPlanKeepRequest").IsFocused, "结束旧编号默认焦点保留原申请");
            await CaptureWithFrame(main, output, "05-wide-cancel-original-confirmation");
            Click(Find<Button>(plan, "StaffPlanConfirmCancel"));
            await Until(() => Ready(plan, "StaffPlanPreview") && Text(plan, "StaffPlanStatus").Contains("原申请已结束") &&
                Text(plan, "StaffPlanFile").Contains("1 位教师"), "明确结束原编号，保留当前文件");
            Click(Find<Button>(plan, "StaffPlanPreview"));
            await WaitForApproval(plan);
            Submit(plan);
            await Until(() => PlanRows(plan).Any(row => row.Saved?.Username == "staff-cancelled"), "用新编号完成经重新确认的方案");
            Click(Find<Button>(plan, "StaffPlanNew"));
            await LoadAsync(plan, fixture.LostInput);
            await Signal("lost");
            Submit(plan);
            await Signal("held");
            Check(Ready(plan, "StaffPlanClose") && !Ready(plan, "StaffPlanCheck") && Text(plan, "StaffPlanStatus").Contains("只读核对"),
                "挂起的只读回执查询允许返回，仍禁止并行办理");
            Click(Find<Button>(plan, "StaffPlanClose"));
            await Until(() => !plan.IsEffectivelyVisible, "只读查询未完成也可退出专页");
            opening = await AcademicPageAsync<SchoolOpeningView>(main);
            await Until(() => Ready(opening, "OpeningRefresh"), "开学清单返回后重读");
            Click(Find<Button>(opening, "OpeningBack"));
            var schools = Find<ComboBox>(main, "SchoolPicker");
            schools.SelectedItem = schools.Items.OfType<School>().Single(row => row.Id == fixture.OtherId);
            opening = await OpenChecklistAsync();
            await Signal("release");
            var other = await OpenAsync(noTerm: true);
            Check(Text(other, "StaffPlanScope").Contains("另一所学校 · 暂无启用学期") && PlanRows(other).Length == 0 &&
                !Find<WrapPanel>(other, "StaffPlanPendingBar").IsVisible, "旧学校迟到回执没有污染另一校，且没有学期不开放导入");
            Console.WriteLine($"PASS: full staff editor on Windows wide and compact, {main.RenderScaling:P0}, normal allocation preserved.");
            desktop.Shutdown(0);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            await CaptureWithFrame(main, output, "failure");
            desktop.Shutdown(1);
        }

        async Task LoginAsync(string account)
        {
            Find<TextBox>(main, "ServerAddress").Text = fixture.Origin;
            Find<TextBox>(main, "SchoolCode").Text = fixture.SchoolCode;
            Find<TextBox>(main, "Username").Text = account;
            Find<TextBox>(main, "Password").Text = fixture.Pin;
            Click(Find<Button>(main, "LoginButton"));
            var schools = Find<ComboBox>(main, "SchoolPicker");
            await Until(() => schools.IsEffectivelyVisible && schools.Items.OfType<School>().Any(row => row.Id == fixture.SchoolId), "真实学校管理员登录");
            schools.SelectedItem = schools.Items.OfType<School>().Single(row => row.Id == fixture.SchoolId);
        }

        async Task<SchoolOpeningView> OpenChecklistAsync()
        {
            var existing = main.GetVisualDescendants().OfType<SchoolOpeningView>().FirstOrDefault(row => row.IsEffectivelyVisible);
            if (existing is not null) return existing;
            var overview = Find<SchoolOverviewView>(main, "SchoolOverviewWorkspace");
            await Until(() => Ready(overview, "OpenSchoolOpeningButton"), "学校总览开学配置实际入口");
            Click(Find<Button>(overview, "OpenSchoolOpeningButton"));
            var checklist = await AcademicPageAsync<SchoolOpeningView>(main);
            await Until(() => Ready(checklist, "OpeningTeachers") && Ready(checklist, "OpeningRefresh"), "开学配置已就绪");
            Click(Find<Button>(checklist, "OpeningTeachers"));
            return checklist;
        }

        async Task<StaffConfigurationView> OpenAsync(bool awaitReceipt = false, bool noTerm = false)
        {
            var checklist = await OpenChecklistAsync();
            await Until(() => Ready(checklist, "OpeningStaffConfiguration"), "教师与岗位的完整师资入口");
            Click(Find<Button>(checklist, "OpeningStaffConfiguration"));
            var view = await AcademicPageAsync<StaffConfigurationView>(main);
            await Until(() => awaitReceipt ? Ready(view, "StaffPlanCheck") || Ready(view, "StaffPlanNew") :
                noTerm ? Text(view, "StaffPlanScope").Contains("暂无启用学期") : Ready(view, "StaffPlanOpen"), "专页服务端范围或本人回执就绪");
            return view;
        }

        async Task<T> WindowOf<T>() where T : Window
        {
            await Until(() => desktop.Windows.OfType<T>().Any(window => window.IsVisible), typeof(T).Name);
            return desktop.Windows.OfType<T>().Single(window => window.IsVisible);
        }

        async Task Signal(string action)
        {
            using var response = await relay.PostAsync("/__staff-plan/" + action, null);
            Check(response.IsSuccessStatusCode, "database or transport checkpoint: " + action);
        }

        static async Task SelectFileAsync(StaffConfigurationView view, string file)
        {
            view.SelectDocumentText = async ct => await File.ReadAllTextAsync(file, ct);
            Click(Find<Button>(view, "StaffPlanOpen"));
            await Until(() => Ready(view, "StaffPlanPreview"), "已从所选文件载入当期方案");
        }

        static async Task LoadAsync(StaffConfigurationView view, string file)
        {
            await SelectFileAsync(view, file);
            Click(Find<Button>(view, "StaffPlanPreview"));
            await WaitForApproval(view);
        }

        static Task WaitForApproval(StaffConfigurationView view) => Until(() => Find<CheckBox>(view, "StaffPlanApprove") is
            { IsEffectivelyVisible: true, IsEffectivelyEnabled: true }, "等待服务器给出可确认的计划");

        static void Submit(StaffConfigurationView view)
        {
            Find<CheckBox>(view, "StaffPlanApprove").IsChecked = true;
            Check(Ready(view, "StaffPlanSave"), "明确核对后可提交计划");
            Click(Find<Button>(view, "StaffPlanSave"));
        }
    }

    private static StaffPlanRow[] PlanRows(StaffConfigurationView view) => Find<ListBox>(view, "StaffPlanRows").Items.OfType<StaffPlanRow>().ToArray();
    private static Button? PlanAction(StaffConfigurationView view, string id) => view.GetVisualDescendants().OfType<Button>()
        .FirstOrDefault(row => AutomationProperties.GetAutomationId(row) == id);
}
