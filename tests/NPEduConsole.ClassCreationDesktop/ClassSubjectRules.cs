using System.Text.Json;
using Avalonia;
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
        string TeacherId, string EightId, string ArchiveEightId, string FallEightId, bool AllowClassRuleWrites);

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
            var termWindow = await Opened<TermPreparationWindow>();
            var termList = Find<ListBox>(termWindow, "PreparedTerms");
            await Until(() => termList.Items.OfType<ManagedAcademicTerm>().Any(row => row.Id == access.SpringId), "加载草稿学期");
            SelectTerm(access.SpringId);
            var (plan, rules) = await OpenRules(termWindow);
            await Until(() => Text(rules, "RulesScope").Contains("春季授课草稿") && Text(rules, "RulesScope").Contains("草稿"),
                "可辨别的目标学期和班级");
            await SelectRule(rules, access.PhysicsId);
            Check(Text(rules, "RulesRelations").Contains("物理 A1"), "原物理 A1 可核对");
            await Capture(rules, output, "01-wide-spring-rules");
            await ApplyPreset(0, ["RULE-PHY"], switchFromSingle: true);
            await CheckDraft(access.PhysicsId, "走班教学 · 必修");
            await CheckDraft(access.ChineseId, "随行政班 · 选修");
            await CheckDraft(access.ChemistryId, "走班教学 · 选修");
            await CheckDraft(access.BiologyId, "走班教学 · 必修");
            await CheckDraft(access.GeographyId, "走班教学 · 选修");
            await CheckDraft(access.HistoryId, "随行政班 · 必修");
            await SelectRule(rules, access.PhysicsId);
            await Capture(rules, output, "01a-preset-after-changing-kind");
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
            await PressScoped(rules, "RulesClose");
            await Until(() => ButtonText(rules, "RulesClose") == "放弃草稿并关闭", "预设只改草稿");
            await PressScoped(rules, "RulesClose");
            await PressScoped(plan, "PlanEditClassRules");
            rules = await Opened<ClassSubjectRulesWindow>();
            await SelectRule(rules, access.PhysicsId);
            SetMode(rules, 2);
            await SelectRule(rules, access.ChemistryId);
            SetMode(rules, 0);
            await SelectRule(rules, access.HistoryId);
            SetMode(rules, 2);
            await PressScoped(rules, "RulesPreview");
            await Until(() => ButtonReady(rules, "RulesOpenConflict"), "有效随班任课阻止保存");
            Check(!ButtonReady(rules, "RulesSave") && Text(rules, "RulesReviewTitle").Contains("历史"), "定位历史任课冲突");
            var review = (TermTeachingReviewView)Find<ContentControl>(rules, "RulesReviewLines").Content!;
            var reviewList = Find<ListBox>(review, "TermTeachingReviewList");
            await Until(() => review.SelectedKey == access.HistoryId && reviewList.IsKeyboardFocusWithin, "历史冲突行取得真实 Avalonia 键盘焦点");
            reviewList.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            var resolver = await Opened<GradeTeachingPlanWindow>(exclude: plan);
            var existing = Find<ComboBox>(resolver, "PlanTeachingExisting");
            await Until(() => existing.Items.OfType<GradePlanAssignment>().Any(item => item.Id == access.TeacherId), "按原班与历史科定位现有任课");
            existing.SelectedItem = existing.Items.OfType<GradePlanAssignment>().Single(item => item.Id == access.TeacherId);
            await PressScoped(resolver, "PlanStageRemoval");
            await PressScoped(resolver, "PlanPreview");
            await PressScoped(resolver, "PlanSave");
            await Until(() => Text(resolver, "PlanNotice").Contains("整份方案已保存"), "任课入口确认移除");
            await PressScoped(resolver, "PlanBack");
            await Until(() => ButtonReady(rules, "RulesPreview") && ButtonText(rules, "RulesPreview").Contains("3 科"), "重读后保留三科草稿");

            rules.Width = 840;
            rules.Height = 480;
            await Until(() => rules.ClientSize is { Width: <= 845, Height: <= 485 }, "840×480 实际窗口");
            await PressScoped(rules, "RulesPreview");
            var confirmations = Find<StackPanel>(rules, "RulesSourceChecks");
            await CheckSources("物理 A1", "物理旧组");
            Check(!ButtonReady(rules, "RulesSave"), "逐组确认前不可保存");
            await Signal("swap");
            foreach (var box in confirmations.Children.OfType<CheckBox>()) box.IsChecked = true;
            await PressScoped(rules, "RulesSave");
            await Until(() => Text(rules, "RulesNotice").Contains("零写入") && Text(rules, "RulesRelations").Contains("物理 A2"),
                "等量换来源后定位旧 A1 和新 A2");
            await Until(() => Find<ListBox>(rules, "RulesRows").IsKeyboardFocusWithin, "窄窗陈旧确认后将焦点交回受影响科目");
            Check(Text(rules, "RulesRelations").Contains("物理 A1") && ButtonText(rules, "RulesPreview").Contains("3 科"),
                "差异指向原来源与新来源，保留所有草稿");
            await Signal("stale");
            await Capture(rules, output, "02-narrow-stale-specific-source");

            await PressScoped(rules, "RulesPreview");
            await CheckSources("物理 A2", "物理旧组");
            foreach (var box in confirmations.Children.OfType<CheckBox>()) box.IsChecked = true;
            await Capture(rules, output, "03-narrow-confirm-exact-links");
            await PressScoped(rules, "RulesSave");
            await Until(() => Text(rules, "RulesNotice").Contains("本账号的保存回执") && ButtonText(rules, "RulesPreview").Contains("0 科"),
                "丢成功回包后只读找回本人结果");
            await Signal("saved");
            await Capture(rules, output, "04-narrow-own-receipt");
            await PressScoped(rules, "RulesClose");
            await Until(() => !rules.IsVisible, "关闭整班规则");
            await PressScoped(plan, "PlanBack");

            SelectTerm(access.ArchiveId);
            var (archivePlan, archiveRules) = await OpenRules(termWindow);
            await Until(() => Text(archiveRules, "RulesScope").Contains("归档授课学期") && ButtonReady(archiveRules, "RulesEditArchive"),
                "归档学期先查看");
            await SelectRule(archiveRules, access.ChineseId);
            Check(!Find<ComboBox>(archiveRules, "RulesRequirement").IsEffectivelyEnabled, "归档默认不得更正");
            await PressScoped(archiveRules, "RulesEditArchive");
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
            var currentEntry = await Opened<ClassSubjectRulesWindow>();
            await SelectRule(currentEntry, access.PhysicsId);
            Check(Text(currentEntry, "RulesBefore").Contains("本班不开设") && Text(currentEntry, "RulesScope").Contains("启用中"),
                "日常快捷入口读取原结果");
            await Capture(currentEntry, output, "06-daily-current-class");
            Console.WriteLine("PASS: Windows 整班规则、任课回跳、Edge 等量换来源、丢回包、归档更正与实际学生选课。");
            Environment.ExitCode = 0;

            async Task CheckSources(string linked, string inactive)
            {
                await Until(() => confirmations.Children.OfType<CheckBox>().Select(box => (box.Content as TextBlock)?.Text).Any(
                    text => text?.Contains(linked) == true), "回读 " + linked);
                var labels = confirmations.Children.OfType<CheckBox>().Select(box => (box.Content as TextBlock)?.Text ?? "").ToArray();
                Check(labels.Length == 2 && labels.Any(text => text.Contains(inactive) && text.Contains("已停用")) &&
                    labels.All(text => text.Contains("高二（7）班")), "仅解除八班两条明确来源，七班原关系可见");
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
            async Task<(GradeTeachingPlanWindow, ClassSubjectRulesWindow)> OpenRules(TermPreparationWindow owner)
            {
                await PressScoped(owner, "TermManageTeaching");
                var parent = await Opened<GradeTeachingPlanWindow>();
                var tabs = Find<TabControl>(parent, "PlanTabs");
                var picker = Find<ComboBox>(parent, "PlanSummaryClass");
                await Until(() => picker.Items.Cast<object>().Any(row => row.ToString()!.Contains("RULE-8")), "读取指定学期的八班");
                tabs.SelectedItem = Find<TabItem>(parent, "PlanClassSummaryTab");
                picker.SelectedItem = picker.Items.Cast<object>().Single(row => row.ToString()!.Contains("RULE-8"));
                await PressScoped(parent, "PlanEditClassRules");
                return (parent, await Opened<ClassSubjectRulesWindow>());
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
        static async Task SelectRule(Window window, string id)
        {
            var rows = Find<ListBox>(window, "RulesRows");
            await Until(() => rows.Items.OfType<ClassRuleEditorLine>().Any(row => row.Id == id), "按学科 ID 定位");
            rows.SelectedItem = rows.Items.OfType<ClassRuleEditorLine>().Single(row => row.Id == id);
        }
        static void SetMode(Window window, int index) => Find<ComboBox>(window, "RulesMode").SelectedIndex = index;
        static bool ButtonReady(Window window, string name) => Find<Button>(window, name) is { IsEffectivelyEnabled: true, IsEffectivelyVisible: true };
        static string ButtonText(Window window, string name) => Find<Button>(window, name).Content?.ToString() ?? "";
        static string Text(Window window, string name) => Find<TextBlock>(window, name).Text ?? "";
    }
}
