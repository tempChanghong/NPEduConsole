using System.Text.Json;
using System.Runtime.InteropServices;
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
    private sealed record ClassRulesAccess(string Origin, string SchoolId, string SchoolCode, string Password,
        string SpringId, string ArchiveId, string PhysicsId, string ChineseId, string ChemistryId, string HistoryId, string BiologyId, string GeographyId,
        string TeacherId, string EightId, string ArchiveEightId, string FallEightId, string A1Id, string A2Id, string InactivePhysicsId,
        string ChemistryGroupId, string InactiveChemistryId, bool AllowClassRuleWrites);

    private static int StartClassRules(string path, string output)
    {
        var access = JsonSerializer.Deserialize<ClassRulesAccess>(File.ReadAllText(path), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Missing isolated class rule fixture.");
        Check(access.AllowClassRuleWrites && IsLoopback(access.Origin), "Class rule writes require an isolated fixture.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => _ = VerifyClassRulesAsync(access, output)))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyClassRulesAsync(ClassRulesAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        using var control = new HttpClient { BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(100) };
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
            await Until(() => schools.Items.OfType<School>().Any(row => row.Id == access.SchoolId), "已读取测试学校");
            schools.SelectedItem = schools.Items.OfType<School>().Single(row => row.Id == access.SchoolId);
            await PressScoped(main, "OpenClassWorkspaceButton");
            await Signal("start");

            await PressScoped(main, "OpenTermPreparationButton");
            var termWindow = await AcademicPageAsync<TermPreparationView>(main);
            var termList = Find<ListBox>(termWindow, "PreparedTerms");
            await Until(() => termList.Items.OfType<ManagedAcademicTerm>().Any(row => row.Id == access.SpringId), "加载草稿学期");
            SelectTerm(access.SpringId);
            var (plan, rules) = await OpenRules(termWindow);
            await Until(() => Text(rules, "RulesScope").Contains("春季授课草稿") && Text(rules, "RulesScope").Contains("草稿"),
                "可辨别的目标学期和班级");
            Check((Find<ListBox>(rules, "RulesRows").SelectedItem as ClassRuleEditorLine)?.Id == access.PhysicsId,
                "班级教学计划的原科目随主窗办理入口传入");
            await SelectRule(rules, access.PhysicsId);
            Check(Text(rules, "RulesRelations").Contains("物理 A1"), "原物理 A1 可核对");
            await CaptureWithFrame(main, output, "01-wide-spring-rules");
            await ApplyPreset(0, ["RULE-PHY"], switchFromSingle: true);
            await CheckDraft(access.PhysicsId, "走班教学 · 必修");
            await CheckDraft(access.ChineseId, "随行政班 · 选修");
            await CheckDraft(access.ChemistryId, "走班教学 · 选修");
            await CheckDraft(access.BiologyId, "走班教学 · 必修");
            await CheckDraft(access.GeographyId, "走班教学 · 选修");
            await CheckDraft(access.HistoryId, "随行政班 · 必修");
            await SelectRule(rules, access.PhysicsId);
            await Capture(main, output, "01a-preset-after-changing-kind");
            await ApplyPreset(1, ["RULE-PHY"]);
            await CheckDraft(access.PhysicsId, "随行政班 · 必修");
            await CheckDraft(access.ChemistryId, "走班教学 · 选修");
            await ApplyPreset(2, ["RULE-CHEM", "RULE-BIO", "RULE-GEO"]);
            await CheckDraft(access.PhysicsId, "本班不开设");
            await CheckDraft(access.ChemistryId, "随行政班 · 选修");
            await CheckDraft(access.BiologyId, "随行政班 · 必修");
            await CheckDraft(access.GeographyId, "随行政班 · 选修");
            await CheckDraft(access.ChineseId, "随行政班 · 选修");
            await Signal("presets");
            await PressScoped(main, "OpenSchoolOverviewButton");
            await Until(() => ButtonText(rules, "RulesClose") == "放弃草稿并返回", "预设只改草稿，离开前提示");
            await PressScoped(main, "OpenSchoolOverviewButton");
            Check(rules.IsEffectivelyVisible, "重复点击主窗导航不会丢掉规则草稿");
            await CheckDraft(access.PhysicsId, "本班不开设");
            await PressScoped(rules, "RulesClose");
            await CheckPlanReturn(plan, access.PhysicsId);
            await PressScoped(plan, "PlanEditClassRules");
            rules = await RulesWorkspace();
            await SelectRule(rules, access.PhysicsId);
            SetMode(rules, 2);
            await SelectRule(rules, access.ChemistryId);
            SetMode(rules, 0);
            await SelectRule(rules, access.HistoryId);
            SetMode(rules, 2);
            await PressScoped(rules, "RulesPreview");
            await Until(() => ButtonReady(rules, "RulesOpenConflict"), "有效随班任课阻止保存");
            Check(!ButtonReady(rules, "RulesSave") && Text(rules, "RulesReviewTitle").Contains("历史"), "定位历史任课冲突");
            var reviewList = Find<ListBox>(rules, "RulesReviewRows");
            await Until(() => (reviewList.SelectedItem as ClassRuleReviewLine)?.SubjectId == access.HistoryId && reviewList.IsKeyboardFocusWithin,
                "历史冲突行取得真实 Avalonia 键盘焦点");
            reviewList.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            var resolver = await AcademicPageAsync<GradeTeachingPlanView>(main);
            Check(ReferenceEquals(TopLevel.GetTopLevel(resolver), main) && !rules.IsEffectivelyVisible &&
                desktop.Windows.Count(window => window.IsVisible) == 1, "任课冲突在原主窗工作区办理");
            var existing = Find<ComboBox>(resolver, "PlanTeachingExisting");
            await Until(() => existing.Items.OfType<GradePlanAssignment>().Any(item => item.Id == access.TeacherId), "按原班与历史科定位现有任课");
            existing.SelectedItem = existing.Items.OfType<GradePlanAssignment>().Single(item => item.Id == access.TeacherId);
            await PressScoped(resolver, "PlanStageRemoval");
            await PressScoped(resolver, "PlanPreview");
            await PressScoped(resolver, "PlanSave");
            await Until(() => Text(resolver, "PlanNotice").Contains("整份方案已保存"), "任课入口确认移除");
            await PressScoped(resolver, "PlanBack");
            await Until(() => ButtonReady(rules, "RulesPreview") && ButtonText(rules, "RulesPreview").Contains("3 科"), "重读后保留三科草稿");

            var filter = Find<TextBox>(rules, "RulesFilter");
            filter.Text = "RULE-PHY";
            await SelectRule(rules, access.PhysicsId);
            await PressScoped(rules, "RulesPreview");
            var confirmations = Find<StackPanel>(rules, "RulesSourceChecks");
            await CheckSources(access.A1Id, access.InactivePhysicsId, "物理 A1", "物理旧组");
            Check(!ButtonReady(rules, "RulesSave"), "逐组确认前不可保存");
            var oldPhysics = SourceBox(access.A1Id);
            await ClickForegroundRuleControlAsync(main, oldPhysics);
            await Until(() => oldPhysics.IsChecked == true, "实际点击第一条物理确认");
            Check((reviewList.SelectedItem as ClassRuleReviewLine)?.SubjectId == access.PhysicsId && confirmations.IsEffectivelyVisible,
                "点击确认没有触发科目导航");
            SourceBox(access.InactivePhysicsId).IsChecked = true;
            Check(!ButtonReady(rules, "RulesSave") && Text(rules, "RulesReviewSummary").Contains("化学 2 条来源待核对"),
                "只核物理仍不可保存，底栏点出未核的化学");
            Click(Find<Button>(rules, "RulesSave"));
            await Signal("partially-reviewed");
            await CaptureWithFrame(main, output, "02a-wide-physics-chemistry-pending");

            main.Width = 840;
            main.Height = 480;
            await Until(() => main.ClientSize is { Width: <= 845, Height: <= 485 } && ButtonReady(rules, "RulesReturnReviewList"),
                "840×480 缩窗保留正在核对的物理");
            Check(!reviewList.IsEffectivelyVisible && SourceBox(access.A1Id).IsChecked == true &&
                SourceBox(access.InactivePhysicsId).IsChecked == true, "缩窗仍在同一预览而且物理确认保留");
            Console.WriteLine($"Desktop rule review: {main.ClientSize.Width} × {main.ClientSize.Height} client DIP, {main.RenderScaling:P0} scaling.");
            RaiseKey(Find<Button>(rules, "RulesReturnReviewList"), Key.Escape);
            await Until(() => ReviewRowFocused(access.PhysicsId), "Escape 返回原物理行和键盘焦点");
            var chemistryReview = reviewList.Items.OfType<ClassRuleReviewLine>().Single(row => row.SubjectId == access.ChemistryId);
            reviewList.SelectedItem = chemistryReview;
            Check(reviewList.IsEffectivelyVisible, "仅改变选中行不会自动钻入详情");
            reviewList.ScrollIntoView(chemistryReview);
            await Until(() => reviewList.ContainerFromItem(chemistryReview) is ListBoxItem, "化学行已实际呈现");
            var chemistryRow = (ListBoxItem)reviewList.ContainerFromItem(chemistryReview)!;
            chemistryRow.BringIntoView();
            await Dispatcher.UIThread.InvokeAsync(() => AssertInsideViewport(main, reviewList, chemistryRow, "化学核对行"),
                DispatcherPriority.Background);
            await ClickForegroundRuleControlAsync(main, chemistryRow);
            await Until(() => ButtonReady(rules, "RulesReturnReviewList") && !reviewList.IsEffectivelyVisible, "鼠标单击已选化学即进入详情");
            await CheckSources(access.ChemistryGroupId, access.InactiveChemistryId, "化学 C1", "化学旧组");
            Check(!ButtonReady(rules, "RulesSave") && ConfirmedCount() == 0, "化学两条尚未确认");
            RaiseKey(Find<Button>(rules, "RulesReturnReviewList"), Key.Escape);
            await Until(() => ReviewRowFocused(access.ChemistryId), "化学返回原行");
            reviewList.SelectedItem = reviewList.Items.OfType<ClassRuleReviewLine>().Single(row => row.SubjectId == access.PhysicsId);
            RaiseKey(reviewList, Key.Enter);
            await CheckSources(access.A1Id, access.InactivePhysicsId, "物理 A1", "物理旧组");
            Check(ConfirmedCount() == 2, "去化学再返回物理，原两条仍已确认");
            main.Width = 1160;
            main.Height = 780;
            await Until(() => main.ClientSize.Width >= 1100 && reviewList.IsEffectivelyVisible && confirmations.IsEffectivelyVisible,
                "放宽窗口后，科目列表和原物理详情同时可见");
            Check((reviewList.SelectedItem as ClassRuleReviewLine)?.SubjectId == access.PhysicsId && ConfirmedCount() == 2,
                "窄窗回到宽窗仍在同一预览，物理确认保留");
            main.Width = 840;
            main.Height = 480;
            await Until(() => ButtonReady(rules, "RulesReturnReviewList") && !reviewList.IsEffectivelyVisible,
                "再缩窗仍在原物理详情");
            RaiseKey(Find<Button>(rules, "RulesReturnReviewList"), Key.F8);
            await Until(() => (reviewList.SelectedItem as ClassRuleReviewLine)?.SubjectId == access.ChemistryId &&
                confirmations.Children.OfType<CheckBox>().FirstOrDefault()?.IsFocused == true, "F8 跨科定位并聚焦第一条未确认来源");
            foreach (var box in confirmations.Children.OfType<CheckBox>())
            {
                var lastLine = ((StackPanel)box.Content!).Children.OfType<TextBlock>().Last();
                AssertInsideViewport(main, Find<ScrollViewer>(rules, "RulesReviewScroll"), lastLine, "两条化学关系的保留来源班");
            }
            await CaptureWithFrame(main, output, "02b-narrow-chemistry-only-pending");
            foreach (var box in confirmations.Children.OfType<CheckBox>()) box.IsChecked = true;
            await Signal("swap");
            await PressScoped(rules, "RulesSave");
            await Until(() => Text(rules, "RulesNotice").Contains("零写入") && Text(rules, "RulesRelations").Contains("物理 A2"),
                "等量换来源后定位旧 A1 和新 A2");
            await Until(() => Find<ComboBox>(rules, "RulesMode").IsFocused && ButtonReady(rules, "RulesReturnSubjects"),
                "窄窗陈旧确认后直接聚焦物理编辑");
            Check(Text(rules, "RulesRelations").Contains("物理 A1") && ButtonText(rules, "RulesPreview").Contains("3 科") && filter.Text == "RULE-PHY",
                "差异指向原来源与新来源，保留三科草稿和原筛选");
            await Signal("stale");
            await CaptureWithFrame(main, output, "02-narrow-stale-specific-source");

            await PressScoped(rules, "RulesPreview");
            await PressScoped(rules, "RulesNextUnconfirmed");
            await CheckSources(access.A2Id, access.InactivePhysicsId, "物理 A2", "物理旧组");
            oldPhysics.IsChecked = false;
            oldPhysics.IsChecked = true;
            Check(ConfirmedCount() == 0 && !ButtonReady(rules, "RulesSave"), "新预览清空旧确认，旧控件迟到事件也不能确认");
            foreach (var box in confirmations.Children.OfType<CheckBox>()) box.IsChecked = true;
            await CaptureWithFrame(main, output, "03-narrow-confirm-exact-links");
            await PressScoped(rules, "RulesNextUnconfirmed");
            await CheckSources(access.ChemistryGroupId, access.InactiveChemistryId, "化学 C1", "化学旧组");
            Check(ConfirmedCount() == 0, "未改版的化学也必须重新核对新预览");
            foreach (var box in confirmations.Children.OfType<CheckBox>()) box.IsChecked = true;
            await PressScoped(rules, "RulesSave");
            await Until(() => ButtonReady(rules, "RulesClose") && ButtonText(rules, "RulesClose") == "返回，稍后核对",
                "写入已结束，原回执的只读请求等待时可以返回");
            await Signal("receipt-reading");
            await CaptureWithFrame(main, output, "04a-narrow-while-reading-original");
            await PressScoped(rules, "RulesClose");
            await CheckPlanReturn(plan, access.PhysicsId);
            await Signal("receipt-left");
            await PressScoped(plan, "PlanEditClassRules");
            rules = await RulesWorkspace();
            await Until(() => Text(rules, "RulesNotice").Contains("本账号的保存回执") && ButtonText(rules, "RulesPreview").Contains("0 科"),
                "丢成功回包后只读找回本人结果");
            await Signal("saved");
            await CaptureWithFrame(main, output, "04-narrow-own-receipt");
            await PressScoped(rules, "RulesClose");
            await CheckPlanReturn(plan, access.PhysicsId);
            await PressScoped(plan, "PlanBack");

            SelectTerm(access.ArchiveId);
            var (archivePlan, archiveRules) = await OpenRules(termWindow);
            await Until(() => Text(archiveRules, "RulesScope").Contains("归档授课学期") && ButtonReady(archiveRules, "RulesEditArchive"),
                "归档学期先查看");
            main.Width = 840;
            main.Height = 480;
            await Until(() => ButtonReady(archiveRules, "RulesOpenSubject"), "归档窄窗从科目列表进入详情");
            await SelectRule(archiveRules, access.ChineseId);
            Check(!Find<ComboBox>(archiveRules, "RulesRequirement").IsEffectivelyEnabled, "归档默认不得更正");
            await PressScoped(archiveRules, "RulesEditArchive");
            SetMode(archiveRules, 2);
            await PressScoped(archiveRules, "RulesPreview");
            var clear = Find<CheckBox>(archiveRules, "RulesClearCheck");
            var archiveReview = Find<ListBox>(archiveRules, "RulesReviewRows");
            await Until(() => !ButtonReady(archiveRules, "RulesSave") &&
                Text(archiveRules, "RulesReviewSummary") == "1 科调整 · 整班清空待确认" &&
                Text(archiveRules, "RulesNotice").Contains("整班清空需要单独确认"),
                "归档整班清空单独确认，不显示走班来源提示");
            RaiseKey(archiveReview, Key.F8);
            await Until(() => clear.IsEffectivelyVisible && clear.IsFocused && !archiveReview.IsEffectivelyVisible,
                "F8 在窄窗单独聚焦整班清空确认");
            await CaptureWithFrame(main, output, "04b-narrow-clear-confirmation");
            clear.IsChecked = true;
            Check(ButtonReady(archiveRules, "RulesSave"), "单独确认后整班清空才就绪");
            await PressScoped(archiveRules, "RulesBack");
            await Signal("clear-preview-only");
            await SelectRule(archiveRules, access.ChineseId);
            SetMode(archiveRules, 0);
            Find<ComboBox>(archiveRules, "RulesRequirement").SelectedIndex = 0;
            await PressScoped(archiveRules, "RulesPreview");
            await PressScoped(archiveRules, "RulesSave");
            await Until(() => Text(archiveRules, "RulesNotice").Contains("本账号的保存回执"), "原归档班按原 ID 保存");
            await Signal("archive");
            await PressScoped(archiveRules, "RulesClose");
            await PressScoped(archivePlan, "PlanBack");

            SelectTerm(access.SpringId);
            await PressScoped(termWindow, "TermStatusActivate");
            var status = await Opened<TermStatusWindow>();
            await PressScoped(status, "StatusCommit");
            await Until(() => Text(status, "StatusAfterLabel") == "服务器确认的结果", "正式启用春季");
            await Signal("active");
            await Capture(status, output, "05-spring-activated");
            await PressScoped(status, "StatusBack");
            await Until(() => !status.IsVisible &&
                termList.Items.OfType<ManagedAcademicTerm>().Any(row => row.Id == access.SpringId && row.Status == "ACTIVE"),
                "学期管理读回已启用的春季后再返回日常班级");
            termWindow.Close();
            await Until(() => !termWindow.IsVisible, "离开学期管理");
            await PressScoped(main, "OpenClassWorkspaceButton");
            var classPicker = Find<ListBox>(main, "ClassPicker");
            await Until(() => classPicker.Items.OfType<Classroom>().Any(row => row.Id == access.EightId), "日常班级列表恢复春季原班 ID");
            classPicker.SelectedItem = classPicker.Items.OfType<Classroom>().Single(row => row.Id == access.EightId);
            await PressScoped(main, "OpenClassRulesButton");
            var currentEntry = await RulesWorkspace();
            await SelectRule(currentEntry, access.PhysicsId);
            Check(Text(currentEntry, "RulesBefore").Contains("本班不开设") && Text(currentEntry, "RulesScope").Contains("启用中"),
                "日常快捷入口读取原结果");
            await CaptureWithFrame(main, output, "06-daily-current-class");
            await PressScoped(currentEntry, "RulesClose");
            await Until(() => Find<Button>(main, "OpenClassRulesButton").IsFocused &&
                classPicker.SelectedItem is Classroom room && room.Id == access.EightId, "日常入口返回原班和入口焦点");
            Console.WriteLine("PASS: Windows 同主窗宽窄整班规则、原科任课回跳、Edge 等量换来源、返回重查原回执、归档更正与实际学生选课。");
            Environment.ExitCode = 0;

            int ConfirmedCount() => confirmations.Children.OfType<CheckBox>().Count(box => box.IsChecked == true);
            CheckBox SourceBox(string id) => confirmations.Children.OfType<CheckBox>().Single(box => AutomationProperties.GetAutomationId(box) == "RulesSource-" + id);
            bool ReviewRowFocused(string subjectId) => reviewList.IsEffectivelyVisible && reviewList.SelectedItem is ClassRuleReviewLine row &&
                row.SubjectId == subjectId && reviewList.ContainerFromItem(row) is ListBoxItem {IsFocused: true};
            async Task CheckSources(string activeId, string inactiveId, string linked, string inactive)
            {
                await Until(() => confirmations.IsEffectivelyVisible && confirmations.Children.OfType<CheckBox>()
                    .Select(AutomationProperties.GetAutomationId).ToHashSet().SetEquals(new[] {"RulesSource-" + activeId, "RulesSource-" + inactiveId}),
                    "按服务器真实编号仅显示本科两条来源 " + linked);
                string Label(string id) => string.Join("\n", ((StackPanel)SourceBox(id).Content!).Children.OfType<TextBlock>().Select(text => text.Text));
                Check(Label(activeId).Contains(linked) && Label(inactiveId).Contains(inactive) && Label(inactiveId).Contains("已停用") &&
                    new[] {activeId, inactiveId}.All(id => Label(id).Contains("保留来源班：高二（7）班") && Label(id).Contains("解除来源班：高二（8）班")),
                    "区分两条真实来源，七班保留，八班移出");
            }
            async Task CheckDraft(string subjectId, string expected)
            {
                var rows = Find<ListBox>(rules, "RulesRows");
                await Until(() => rows.Items.OfType<ClassRuleEditorLine>().Any(row => row.Id == subjectId &&
                    row.Detail.StartsWith(expected, StringComparison.Ordinal)), "此预设科目安排：" + expected);
            }
            async Task ApplyPreset(int preset, string[] codes, bool switchFromSingle = false)
            {
                await PressScoped(rules, "RulesPreset");
                await Until(() => desktop.Windows.Any(window => window.IsVisible && window.Title == "授课方式预设"), "打开预设弹窗");
                var modal = desktop.Windows.Single(window => window.IsVisible && window.Title == "授课方式预设");
                var kind = modal.GetVisualDescendants().OfType<ComboBox>().First();
                var fixedSubjects = modal.GetVisualDescendants().OfType<ListBox>().Single();
                kind.SelectedIndex = switchFromSingle ? 1 : preset;
                foreach (var code in codes)
                    fixedSubjects.SelectedItems!.Add(fixedSubjects.Items.Cast<object>().Single(item => item.ToString()!.Contains(code)));
                if (switchFromSingle)
                {
                    kind.SelectedIndex = preset;
                    Check(!fixedSubjects.IsEffectivelyVisible && fixedSubjects.SelectedItems?.Count == 1, "切换预设只隐藏此前选择");
                }
                var apply = modal.GetVisualDescendants().OfType<Button>().Single(button => button.Content?.ToString() == "应用到草稿");
                await Until(() => apply.IsEffectivelyEnabled, "有效预设可以应用到草稿");
                Click(apply);
                await Until(() => !modal.IsVisible, "应用预设后回到整班规则");
            }
            async Task<(GradeTeachingPlanView, ClassSubjectRulesView)> OpenRules(TermPreparationView owner)
            {
                await PressScoped(owner, "TermManageTeaching");
                var parent = await AcademicPageAsync<GradeTeachingPlanView>(main);
                var tabs = Find<TabControl>(parent, "PlanTabs");
                var picker = Find<ComboBox>(parent, "PlanSummaryClass");
                await Until(() => picker.Items.Cast<object>().Any(row => row.ToString()!.Contains("RULE-8")), "读取指定学期的八班");
                tabs.SelectedItem = Find<TabItem>(parent, "PlanClassSummaryTab");
                picker.SelectedItem = picker.Items.Cast<object>().Single(row => row.ToString()!.Contains("RULE-8"));
                var subjects = Find<ListBox>(parent, "PlanClassSubjects");
                subjects.SelectedItem = subjects.Items.OfType<TermTeachingSubjectLine>().Single(row => row.Id == access.PhysicsId);
                await PressScoped(parent, "PlanEditClassRules");
                return (parent, await RulesWorkspace());
            }
            void SelectTerm(string id) => termList.SelectedItem = termList.Items.OfType<ManagedAcademicTerm>().Single(row => row.Id == id);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (desktop.Windows.LastOrDefault(window => window.IsVisible) is { } window)
                await Capture(window, output, "class-rules-failure");
            Environment.ExitCode = 1;
        }
        finally { desktop.Shutdown(Environment.ExitCode); }

        async Task Signal(string step)
        {
            using var response = await control.PostAsync("/fixture/class-rules/" + step, null);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(step + " → " + await response.Content.ReadAsStringAsync());
        }
        async Task<T> Opened<T>(T? exclude = null) where T : Window
        {
            await Until(() => desktop.Windows.OfType<T>().Any(window => window.IsVisible && window != exclude), "打开 " + typeof(T).Name);
            return desktop.Windows.OfType<T>().Single(window => window.IsVisible && window != exclude);
        }
        async Task<ClassSubjectRulesView> RulesWorkspace()
        {
            var view = await AcademicPageAsync<ClassSubjectRulesView>(main);
            var windowButtons = main.GetVisualAncestors().Last().GetVisualDescendants().OfType<Button>().Where(button =>
                button.IsEffectivelyVisible && button.Name is "PART_MinimizeButton" or "PART_MaximizeButton" or "PART_CloseButton" or "PART_FullScreenButton");
            Check(ReferenceEquals(TopLevel.GetTopLevel(view), main) && desktop.Windows.Count(window => window.IsVisible) == 1 &&
                main.IsExtendedIntoWindowDecorations &&
                windowButtons.Select(button => button.Name).ToHashSet().SetEquals(
                    ["PART_MinimizeButton", "PART_MaximizeButton", "PART_CloseButton"]), "整班规则沿用原主窗三键");
            return view;
        }
        async Task CheckPlanReturn(GradeTeachingPlanView parent, string subject)
        {
            await Until(() => Find<Button>(parent, "PlanEditClassRules").IsFocused &&
                Find<ComboBox>(parent, "PlanSummaryClass").SelectedItem?.ToString()?.Contains("RULE-8") == true &&
                Find<ListBox>(parent, "PlanClassSubjects").SelectedItem is TermTeachingSubjectLine row && row.Id == subject,
                "从整班规则回到原班、原科和打开入口");
        }
        static async Task SelectRule(Control window, string id)
        {
            var rows = Find<ListBox>(window, "RulesRows");
            if (ButtonReady(window, "RulesReturnSubjects")) await PressScoped(window, "RulesReturnSubjects");
            await Until(() => rows.Items.OfType<ClassRuleEditorLine>().Any(row => row.Id == id), "按学科 ID 定位");
            rows.SelectedItem = rows.Items.OfType<ClassRuleEditorLine>().Single(row => row.Id == id);
            if (ButtonReady(window, "RulesOpenSubject")) await PressScoped(window, "RulesOpenSubject");
        }
        static void RaiseKey(Control source, Key key) => source.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
        static void SetMode(Control window, int index) => Find<ComboBox>(window, "RulesMode").SelectedIndex = index;
        static bool ButtonReady(Control window, string name) => Find<Button>(window, name) is { IsEffectivelyEnabled: true, IsEffectivelyVisible: true };
        static string ButtonText(Control window, string name) => Find<Button>(window, name).Content?.ToString() ?? "";
        static string Text(Control window, string name) => Find<TextBlock>(window, name).Text ?? "";
    }

    private static async Task ClickRuleControlAsync(Window window, Control control)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? throw new InvalidDataException("Expected the real Windows rules window.");
        control.BringIntoView();
        await Until(() => window.IsVisible && control.IsEffectivelyVisible && TopLevel.GetTopLevel(control) == window &&
            control.Bounds.Width > 0 && control.Bounds.Height > 0, "native click targets the visible isolated rules window");
        var screen = control.PointToScreen(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2));
        var position = new RulePointer.Point(screen.X, screen.Y);
        Check(RulePointer.ScreenToClient(handle, ref position) && RulePointer.GetClientRect(handle, out var bounds) &&
            position.X >= 0 && position.Y >= 0 && position.X < bounds.Right && position.Y < bounds.Bottom,
            "native test click is inside the isolated window client area");
        nint point = position.Y << 16 | position.X;
        Check(RulePointer.PostMessage(handle, 0x0200, 0, point) && RulePointer.PostMessage(handle, 0x0201, 1, point) &&
            RulePointer.PostMessage(handle, 0x0202, 0, point), "deliver mouse move, press and release only to the isolated HWND");
    }

    private static async Task ClickForegroundRuleControlAsync(Window window, Control control)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? throw new InvalidDataException("Expected the real Windows main window.");
        window.Activate();
        control.BringIntoView();
        await Until(() => RulePointer.GetForegroundWindow() == handle && control.IsEffectivelyVisible &&
            TopLevel.GetTopLevel(control) == window && control.Bounds.Width > 0 && control.Bounds.Height > 0,
            "the isolated main window and its rule control are in the foreground");
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        var screen = control.PointToScreen(new Point(Math.Min(24, control.Bounds.Width / 2), control.Bounds.Height / 2));
        Check(RulePointer.GetCursorPos(out var before) && RulePointer.GetForegroundWindow() == handle &&
            RulePointer.WindowFromPoint(new(screen.X, screen.Y)) == handle && RulePointer.SetCursorPos(screen.X, screen.Y),
            "target a visible point inside the isolated main window");
        try
        {
            await Task.Delay(70);
            Check(RulePointer.GetForegroundWindow() == handle && RulePointer.GetCursorPos(out var positioned) &&
                positioned.X == screen.X && positioned.Y == screen.Y && RulePointer.WindowFromPoint(positioned) == handle,
                "the foreground mouse still points to this fixture");
            RulePointer.mouse_event(0x0002, 0, 0, 0, 0);
            RulePointer.mouse_event(0x0004, 0, 0, 0, 0);
            await Task.Delay(70);
        }
        finally { RulePointer.SetCursorPos(before.X, before.Y); }
    }

    private static class RulePointer
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Point(int x, int y) { internal int X = x, Y = y; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect { internal int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] internal static extern bool ScreenToClient(nint window, ref Point point);
        [DllImport("user32.dll")] internal static extern bool GetClientRect(nint window, out Rect rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool PostMessage(nint window, uint message, nuint state, nint point);
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
        [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] internal static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);
    }
}
