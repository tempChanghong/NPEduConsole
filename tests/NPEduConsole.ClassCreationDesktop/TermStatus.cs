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
    private sealed record TermStatusAccess(string Origin, string SchoolId, string SchoolCode, string Username, string Password,
        string TermId, string TermName, string OtherSchoolId, string SameNameId, string ForceTermId, string FrontId, string RearId, string UnmatchedId,
        bool AllowTermStatusWrites);

    private static int StartTermStatus(string path, string output)
    {
        var access = JsonSerializer.Deserialize<TermStatusAccess>(File.ReadAllText(path), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Expected an isolated term status fixture.");
        Check(access.AllowTermStatusWrites && IsLoopback(access.Origin), "Term status writes require the isolated fixture.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyTermStatusAsync(access, output); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyTermStatusAsync(TermStatusAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        using var control = new HttpClient {BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(55)};
        try
        {
            main.Width = 1160;
            main.Height = 780;
            Find<TextBox>(main, "ServerAddress").Text = access.Origin;
            Find<TextBox>(main, "SchoolCode").Text = access.SchoolCode;
            Find<TextBox>(main, "Username").Text = access.Username;
            Find<TextBox>(main, "Password").Text = access.Password;
            Click(Find<Button>(main, "LoginButton"));
            var schools = Find<ComboBox>(main, "SchoolPicker");
            await Until(() => schools.Items.Cast<School>().Any(row => row.Id == access.SchoolId), "本校作为可管理学校");
            SelectSchool(access.SchoolId);
            Click(Find<Button>(main, "OpenClassWorkspaceButton"));
            var directory = await OpenDirectory();
            var list = Find<ListBox>(directory, "PreparedTerms");
            await Until(() => list.Items.Cast<ManagedAcademicTerm>().Any(row => row.Id == access.SameNameId), "同名历史学期单独列出");
            var sameName = list.Items.Cast<ManagedAcademicTerm>().Where(row => row.Name == access.TermName).ToArray();
            Check(sameName.Length == 2 && sameName.Select(row => row.Id).Distinct().Count() == 2, "同名学期按 ID 及学年区分");
            Find<ComboBox>(directory, "TermFilter").SelectedIndex = 3;
            await Until(() => list.ItemCount == 1 && ((ManagedAcademicTerm)list.SelectedItem!).Id == access.SameNameId, "归档筛选");
            Find<ComboBox>(directory, "TermFilter").SelectedIndex = 0;
            SelectTerm(access.TermId);
            await Capture(directory, output, "01-wide-term-directory");

            var state = await OpenMore("TermStatusArchive");
            await Review(state);
            Check(Text(state, "StatusAfter").Contains("暂无启用学期") && Text(state, "StatusAfter").Contains("整校迁出暂不可办"),
                "确认页明确学生暂无学期和整校迁出条件");
            await Capture(state, output, "02-wide-archive-impact");
            await Step("rename-screen");
            Click(Find<Button>(state, "StatusCommit"));
            await Until(() => Text(state, "StatusMessage").Contains("本次没有写入") && Text(state, "StatusMessage").Contains(access.RearId),
                "后屏变更按绑定 ID 说明，旧预览零写入");
            await Step("confirmed-stale");
            await Capture(state, output, "03-stale-exact-rear-screen");
            Click(Find<Button>(state, "StatusCommit"));
            await Result(state, "已归档");
            await Step("after-archive");
            await Capture(state, output, "04-recovered-archive-receipt");
            await CloseStatus(state, "ARCHIVED");
            Check(Text(directory, "TermScope").Contains("暂无"), "全校顶栏没有启用学期");

            state = await OpenStatus("TermStatusActivate");
            await Review(state);
            Click(Find<Button>(state, "StatusCommit"));
            await Result(state, "启用中");
            await Step("old-id-enabled");
            await CloseStatus(state, "ACTIVE");
            state = await OpenMore("TermStatusDraft");
            await Review(state);
            Click(Find<Button>(state, "StatusCommit"));
            await Result(state, "草稿");
            await Step("old-id-draft");
            await CloseStatus(state, "DRAFT");
            state = await OpenMore("TermStatusArchive");
            await Review(state);
            Click(Find<Button>(state, "StatusCommit"));
            await Result(state, "已归档");
            await CloseStatus(state, "ARCHIVED");

            directory.Width = 840;
            directory.Height = 480;
            Click(Find<Button>(directory, "TermPrimary"));
            await Until(() => Text(directory, "TermDraftSource").Contains("已归档") && Text(directory, "TermDraftSource").Contains(access.TermName),
                "无启用学期，原 ID 归档历史作为草稿来源");
            Find<TextBox>(directory, "TermDraftYear").Text = "2027";
            Find<ComboBox>(directory, "TermDraftSemester").SelectedIndex = 0;
            Find<TextBox>(directory, "TermDraftName").Text = "从归档准备的新学期";
            Click(Find<Button>(directory, "TermPreviewButton"));
            await Until(() => Text(directory, "TermPreviewSummary").Contains("2 个走班教学班") && Find<Button>(directory, "TermPrimary").IsEnabled,
                "从历史学期读取现有继承对象");
            await Capture(directory, output, "05-compact-archived-source-preview");
            Click(Find<Button>(directory, "TermPrimary"));
            await Until(() => Find<Button>(directory, "TermActivationOptions").IsEffectivelyVisible &&
                Find<Button>(directory, "TermActivationOptions").IsEnabled, "草稿写入后带确切 ID 到启用前检查");
            await Step("archived-source-draft-created");
            state = await OpenStatus("TermActivationOptions");
            state.Width = 840;
            state.Height = 480;
            await Review(state);
            var screens = Find<TermStatusScreensView>(state, "StatusScreens");
            var movements = Find<CheckBox>(state, "StatusMoveScreens");
            movements.IsChecked = false;
            Click(movements);
            await Until(() => Find<Button>(state, "StatusCommit").IsEnabled && Text(state, "StatusScreenSummary").Contains("拟改绑 0 台"),
                "改选保留原班会重新读取预览");
            movements.IsChecked = true;
            Click(movements);
            await Until(() => Find<Button>(state, "StatusCommit").IsEnabled && Text(state, "StatusScreenSummary").Contains("拟改绑 2 台"),
                "同班前后屏分别拟迁");
            screens.BringIntoView();
            screens.SelectBinding(access.RearId);
            await Until(() => screens.FocusSelected() && screens.SelectedBindingId == access.RearId, "窄窗按后屏 ID 键盘定位到行");
            await Capture(state, output, "06-compact-dual-screen-preview");
            Click(Find<Button>(state, "StatusCommit"));
            await Result(state, "启用中");
            await Step("front-reported");
            Click(Find<Button>(state, "StatusRefresh"));
            var screenList = Find<ListBox>(screens, "TermStatusScreensList");
            await Until(() => screenList.Items.Cast<TermStatusScreenLine>().Any(row => row.BindingId == access.FrontId && row.Outcome.Contains("首次有效上报")) &&
                screenList.Items.Cast<TermStatusScreenLine>().Any(row => row.BindingId == access.RearId && row.Outcome.Contains("仍需现场重配")),
                "回执不变，前后屏按真实首报分别更新");
            screens.SelectBinding(access.FrontId);
            screens.FocusSelected();
            await Capture(state, output, "07-compact-independent-screen-results");
            screens.SelectBinding(access.UnmatchedId);
            var firstPlacementButton = Find<Button>(state, "StatusCommit");
            await Until(() => firstPlacementButton.IsEffectivelyVisible && firstPlacementButton.IsEnabled, "第一轮无匹配屏可原位办理");
            Click(firstPlacementButton);
            await CompletePlacement("first-round-placement");
            await Until(() => Find<Button>(state, "StatusBack").IsEffectivelyEnabled && state.IsEffectivelyEnabled, "第一轮安排返回历史结果");
            Click(Find<Button>(state, "StatusBack"));
            await Until(() => !state.IsVisible && Text(directory, "TermScope").Contains("从归档准备的新学期"), "回目录后全校启用事实更新");
            string newTermId = list.Items.Cast<ManagedAcademicTerm>().Single(row => row.Name == "从归档准备的新学期").Id;
            SelectTerm(access.TermId);
            state = await OpenStatus("TermStatusActivate");
            await Review(state);
            var preserve = Find<CheckBox>(state, "StatusMoveScreens");
            preserve.IsChecked = false;
            Click(preserve);
            await Until(() => Find<Button>(state, "StatusCommit").IsEnabled && Text(state, "StatusScreenSummary").Contains("拟改绑 0 台"),
                "回启来源学期保留原绑定");
            Click(Find<Button>(state, "StatusCommit"));
            await Result(state, "启用中");
            await Step("return-unmatched-to-old-class");
            await CloseStatus(state, "ACTIVE");
            SelectTerm(newTermId);
            state = await OpenStatus("TermStatusActivate");
            await Review(state);
            Click(Find<Button>(state, "StatusCommit"));
            await Result(state, "启用中");
            await Step("second-round-pending");
            Click(Find<Button>(state, "StatusBack"));
            await Until(() => !state.IsVisible && list.SelectedItem is ManagedAcademicTerm selected && selected.Id == newTermId &&
                selected.Status == "ACTIVE", "同一学期第二轮启用保留同一 ID");
            Find<Expander>(directory, "TermMoreActions").IsExpanded = true;
            var history = Find<ComboBox>(directory, "TermStatusHistoryPicker");
            var activations = history.Items.Cast<TermStatusHistoryEntry>().Where(row => row.Operation == "ACTIVATE").ToArray();
            Check(activations.Length == 2 && activations.Select(row => row.RequestId).Distinct().Count() == 2, "目录区分同一学期两轮回执");
            history.SelectedItem = activations[^1];
            state = await OpenStatus("TermStatusHistoryOpen");
            await Result(state, "启用中");
            screens = Find<TermStatusScreensView>(state, "StatusScreens");
            screenList = Find<ListBox>(screens, "TermStatusScreensList");
            await Until(() => screenList.Items.Cast<TermStatusScreenLine>().Any(row => row.BindingId == access.UnmatchedId &&
                row.Outcome.Contains("本轮后续安排：高二（7）班") && row.Outcome.Contains("高二（9）班")), "第一轮办理记录有据，另列现在的原班");
            screens.SelectBinding(access.UnmatchedId);
            screens.FocusSelected();
            screens.BringIntoView();
            Check(!Find<Button>(state, "StatusCommit").IsEffectivelyVisible, "历史回执不能用来替当前轮办理");
            await Capture(state, output, "10-first-round-history-current-location");
            Click(Find<Button>(state, "StatusBack"));
            await Until(() => !state.IsVisible && Find<Button>(directory, "TermBack").IsEnabled, "回学期目录");
            directory.Close();
            await Until(() => !directory.IsVisible, "返回工作区后从总览办第二轮");
            Click(Find<Button>(main, "OpenSchoolOverviewButton"));
            var overview = Find<SchoolOverviewView>(main, "SchoolOverviewWorkspace");
            await ExpandTaskQueue(overview);
            var taskList = overview.FindControl<ListBox>("OverviewTasksList")!;
            Button? openPlacement = null;
            await Until(() =>
            {
                var task = taskList.Items.Cast<OverviewTaskItem>().FirstOrDefault(row => row.Task.Code == "TERM_SCREEN_RETAINED_ORIGINAL" &&
                    row.Task.ScreenId == access.UnmatchedId);
                if (task is null) return false;
                openPlacement = taskList.GetVisualDescendants().OfType<Button>().FirstOrDefault(row =>
                    row.IsEffectivelyVisible && ReferenceEquals(row.Tag, task));
                if (openPlacement is null) taskList.ScrollIntoView(task);
                return openPlacement is not null;
            }, "学校总览第二轮只认当前待安排屏");
            Click(openPlacement!);
            await CompletePlacement("second-round-placement", "11-second-round-placement-from-overview");

            SelectSchool(access.OtherSchoolId);
            Click(Find<Button>(main, "OpenClassWorkspaceButton"));
            directory = await OpenDirectory();
            list = Find<ListBox>(directory, "PreparedTerms");
            await Until(() => list.Items.Cast<ManagedAcademicTerm>().Any(row => row.Id == access.ForceTermId), "换校后只列另一校目标");
            SelectTerm(access.ForceTermId);
            state = await OpenStatus("TermStatusActivate");
            var force = Find<CheckBox>(state, "StatusForce");
            await Until(() => force.IsEffectivelyVisible && !Find<Button>(state, "StatusCommit").IsEnabled, "普通启用被具体教务事项阻断");
            await Capture(state, output, "08-force-review-before-confirming");
            force.IsChecked = true;
            await Until(() => Find<Button>(state, "StatusCommit").IsEnabled, "勾选强制后才可办理；无额外确认窗");
            Click(Find<Button>(state, "StatusCommit"));
            await Result(state, "启用中");
            await Step("force-applied");
            await Capture(state, output, "09-force-confirmed");
            await File.WriteAllTextAsync(Path.Combine(output, "windows-term-status.json"), "{\"passed\":true}");
            desktop.Shutdown(0);

            void SelectSchool(string id) => schools.SelectedItem = schools.Items.Cast<School>().Single(row => row.Id == id);
            void SelectTerm(string id) => list.SelectedItem = list.Items.Cast<ManagedAcademicTerm>().Single(row => row.Id == id);
            async Task<TermPreparationWindow> OpenDirectory()
            {
                var entry = Find<Button>(main, "OpenTermPreparationButton");
                await Until(() => entry.IsEffectivelyVisible && entry.IsEffectivelyEnabled, "现有学期管理入口");
                Click(entry);
                await Until(() => desktop.Windows.OfType<TermPreparationWindow>().Any(window => window.IsVisible), "打开实际学期窗口");
                return desktop.Windows.OfType<TermPreparationWindow>().Single(window => window.IsVisible);
            }
            async Task<TermStatusWindow> OpenMore(string name)
            {
                Find<Expander>(directory, "TermMoreActions").IsExpanded = true;
                return await OpenStatus(name);
            }
            async Task<TermStatusWindow> OpenStatus(string name)
            {
                var button = Find<Button>(directory, name);
                await Until(() => button.IsEffectivelyVisible && button.IsEffectivelyEnabled, name + " visible");
                Click(button);
                await Until(() => desktop.Windows.OfType<TermStatusWindow>().Any(window => window.IsVisible), name + " dialog");
                return desktop.Windows.OfType<TermStatusWindow>().Single(window => window.IsVisible);
            }
            async Task CloseStatus(TermStatusWindow window, string expected)
            {
                Click(Find<Button>(window, "StatusBack"));
                await Until(() => !window.IsVisible && list.SelectedItem is ManagedAcademicTerm row && row.Id == access.TermId && row.Status == expected,
                    "目录原 ID 选择保留，状态已重读 " + expected);
            }
            Task Review(TermStatusWindow window) => Until(() => Find<Button>(window, "StatusCommit").IsEnabled &&
                Find<Button>(window, "StatusCommit").IsEffectivelyVisible, "可确认的服务器预览");
            Task Result(TermStatusWindow window, string label) => Until(() => Text(window, "StatusAfterLabel") == "服务器确认的结果" &&
                Text(window, "StatusAfter").Contains(label), "服务端确认 " + label);
            async Task CompletePlacement(string signal, string? screenshot = null)
            {
                await Until(() => desktop.Windows.OfType<TermScreenPlacementWindow>().Any(window => window.IsVisible), "打开已复用的定屏安排窗");
                var placement = desktop.Windows.OfType<TermScreenPlacementWindow>().Single(window => window.IsVisible);
                placement.Width = 840;
                placement.Height = 480;
                var choices = Find<ListBox>(placement, "PlacementCandidates");
                await Until(() => Text(placement, "PlacementBindingId").Contains(access.UnmatchedId) &&
                    choices.Items.Cast<TermScreenCandidate>().Any(row => row.Code == "STATE-7"), "安排页定位本轮原屏及确切目标班");
                choices.SelectedItem = choices.Items.Cast<TermScreenCandidate>().Single(row => row.Code == "STATE-7");
                var commit = Find<Button>(placement, "PlacementPrimary");
                await Until(() => commit.IsEffectivelyEnabled && commit.IsEffectivelyVisible, "本轮安排可确认");
                if (screenshot is not null) await Capture(placement, output, screenshot);
                Click(commit);
                await Until(() => Text(placement, "PlacementNotice").Contains("已核对本次安排回执"), "原请求的本轮安排已确认");
                await Step(signal);
                Click(Find<Button>(placement, "PlacementBack"));
                await Until(() => !placement.IsVisible, "关闭已确认安排窗");
            }
            async Task Step(string name)
            {
                using var response = await control.PostAsync("/__term-status/" + name, null);
                Check(response.IsSuccessStatusCode, "跨端检查点失败 " + name);
            }
        }
        catch (Exception error)
        {
            try
            {
                if (desktop.Windows.LastOrDefault(window => window.IsVisible) is { } window) await Capture(window, output, "term-status-failure");
                await File.WriteAllTextAsync(Path.Combine(output, "windows-term-status-failure.txt"), error.ToString());
            }
            finally { Console.Error.WriteLine(error); desktop.Shutdown(1); }
        }

        static string Text(Window window, string name) => Find<TextBlock>(window, name).Text ?? "";
    }
}
