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
    private sealed record ClassroomActionAccess(string Origin, string SchoolCode, string SchoolId, string Pin, string GradeCode,
        string ClassCode, string NeighborCode, string FrontId, string RearId, string HistoricalKeyId);

    private static int StartClassroomActionPolicy(string fixture, string output)
    {
        var data = JsonSerializer.Deserialize<ClassroomActionAccess>(File.ReadAllText(fixture), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Expected isolated classroom action policy fixture");
        Check(IsLoopback(data.Origin), "Classroom action policy requires a disposable loopback fixture");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyClassroomActionPolicyAsync(data, output); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyClassroomActionPolicyAsync(ClassroomActionAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        var mainHandle = main.TryGetPlatformHandle()?.Handle;
        try
        {
            main.Width = 1160;
            main.Height = 780;
            Find<TextBox>(main, "ServerAddress").Text = access.Origin;
            Find<TextBox>(main, "SchoolCode").Text = access.SchoolCode;
            Find<TextBox>(main, "Username").Text = "admin";
            Find<TextBox>(main, "Password").Text = access.Pin;
            Click(Find<Button>(main, "LoginButton"));
            var schools = Find<ComboBox>(main, "SchoolPicker");
            await Until(() => schools.Items.OfType<School>().Any(item => item.Id == access.SchoolId), "authenticated school available");
            schools.SelectedItem = schools.Items.OfType<School>().Single(item => item.Id == access.SchoolId);
            var nav = Find<Button>(main, "OpenDeviceDirectoryButton");
            await Until(() => nav.IsEffectivelyVisible && nav.IsEnabled, "normal device directory visible");
            Click(nav);
            var directory = Find<SchoolDeviceDirectoryView>(main, "DeviceDirectory");
            var entry = directory.FindControl<Button>("DeviceActionPolicyButton")!;
            await Until(() => entry.IsEffectivelyVisible && entry.IsEffectivelyEnabled, "policy is accessible from the normal device directory");
            async Task<ClassroomActionPolicyView> OpenAsync()
            {
                Click(entry);
                ClassroomActionPolicyView? view = null;
                await Until(() => (view = main.GetVisualDescendants().OfType<ClassroomActionPolicyView>().SingleOrDefault())
                    ?.IsEffectivelyVisible == true, "policy page opened");
                Check(TopLevel.GetTopLevel(view!) == main && mainHandle is not null &&
                    main.TryGetPlatformHandle()?.Handle == mainHandle && desktop.Windows.Count(item => item.IsVisible) == 1,
                    "policy page shares the original main window and caption controls");
                return view!;
            }
            var window = await OpenAsync();
            Button Action(string name) => Find<Button>(window, "ActionPolicy" + name);
            ComboBox Option(string name) => Find<ComboBox>(window, "ActionPolicy" + name);
            string Content(string name) => Find<TextBlock>(window, "ActionPolicy" + name).Text ?? "";
            void Pick(string option, string label)
            {
                var control = Option(option);
                control.SelectedItem = control.Items.Cast<object>().Single(item => item.ToString() == label);
            }
            async Task EditStep()
            {
                var editor = Find<Grid>(window, "EditorPane");
                if (!editor.IsEffectivelyVisible) Click(Find<Button>(window, "EditStepButton"));
                await Until(() => editor.IsEffectivelyVisible, "original editor is visible");
            }
            async Task VerifyStep()
            {
                var verification = Find<Grid>(window, "VerifyPane");
                if (!verification.IsEffectivelyVisible) Click(Find<Button>(window, "VerifyStepButton"));
                await Until(() => verification.IsEffectivelyVisible, "current verification is visible");
            }
            async Task ChooseTarget(string code)
            {
                await EditStep();
                var targets = Option("Target");
                await Until(() => targets.Items.Cast<object>().Any(item => item.ToString()!.EndsWith(" · " + code, StringComparison.Ordinal)), "exact target visible " + code);
                targets.SelectedItem = targets.Items.Cast<object>().Single(item => item.ToString()!.EndsWith(" · " + code, StringComparison.Ordinal));
                Click(Action("Load"));
                await Until(() => Action("SavePreview").IsEffectivelyVisible && Action("SavePreview").IsEffectivelyEnabled, "selected policy target loaded");
            }
            async Task Publish(bool lostReply = false)
            {
                var agree = Find<CheckBox>(window, "ActionPolicyConfirmed");
                await Until(() => agree.IsEffectivelyVisible && Action("Apply").IsEffectivelyVisible, "explicit review available");
                Check(!Action("Apply").IsEffectivelyEnabled, "unconfirmed publication was enabled");
                agree.IsChecked = true;
                await ClickRuleControlAsync(main, Action("Apply"));
                if (lostReply)
                {
                    await Until(() => Content("Status").Contains("原请求待核对") && Action("Close").IsEffectivelyEnabled,
                        "successful write with lost response is awaiting the original receipt");
                    string id = System.Text.RegularExpressions.Regex.Match(Content("Status"), @"[0-9a-f-]{36}").Value;
                    Check(Guid.TryParse(id, out _), "original request is explicit");
                    using var relay = new HttpClient { BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(5) };
                    bool automaticReadArrived = false;
                    for (int attempt = 0; attempt < 30 && !automaticReadArrived; attempt++)
                    {
                        using var result = await relay.GetAsync("/__classroom-policy-fixture/original-read-started");
                        automaticReadArrived = result.IsSuccessStatusCode;
                        if (!automaticReadArrived) await Task.Delay(50);
                    }
                    Check(automaticReadArrived, "first receipt read is held by the fixture before leaving");
                    Click(Action("Close"));
                    await Until(() => entry.IsEffectivelyVisible && entry.IsFocused, "return while looking up the original receipt");
                    main.Width = 840;
                    main.Height = 480;
                    window = await OpenAsync();
                    await Until(() => Action("ReadReceipt").IsEffectivelyEnabled && Content("Status").Contains(id), "reopen exposes the same pending request");
                    Check(Find<TextBlock>(window, "WorkingScope").Text?.Contains("秋季学期 · 年级默认 高二") == true &&
                        !Action("Retry").IsEffectivelyVisible, "correct original term and grade, no resubmit");
                    var read = Action("ReadReceipt");
                    Check(read.TranslatePoint(new(0, read.Bounds.Height), window)?.Y <= window.Bounds.Height, "receipt action fits on narrow screen");
                    await CaptureWithFrame(main, output, "01a-original-receipt-pending-840");
                    await ClickRuleControlAsync(main, read);
                    await Until(() => Content("Status").Contains("本人已发布") && Content("Status").Contains(id), "verified original receipt");
                    await CaptureWithFrame(main, output, "01b-original-receipt-found-840");
                    return;
                }
                await Until(() => Content("Status").Contains("本人已发布", StringComparison.Ordinal), "own policy receipt visible");
            }

            await ChooseTarget(access.GradeCode);
            Check(Content("Context").Contains("秋季学期") && Content("Source").Contains("沿用原教室端规则"), "original grade/term was not clear");
            Option("StopMode").SelectedIndex = 1;
            Option("StopFactor").SelectedIndex = 3;
            Option("ExitMode").SelectedIndex = 2;
            Pick("ManualStudyMode", "允许现场操作");
            Pick("ManualRecordingMode", "允许现场操作");
            Click(Action("SavePreview"));
            var review = Find<ListBox>(window, "ActionPolicyReviewRows");
            await Until(() => review.IsEffectivelyVisible && review.Items.Count == 3, "grade preview lists the three real classes");
            Check(review.Items.OfType<string>().Any(row => row.Contains("全部") && row.Contains("退出 禁止现场操作") &&
                row.Contains("手动录课 允许现场操作（年级默认）") && row.Contains("同班后屏")), "preview omitted the grade rule or second screen");
            var wideReview = Find<Grid>(window, "VerifyPane");
            Check(Find<Grid>(window, "EditorPane").IsEffectivelyVisible &&
                wideReview.TranslatePoint(default, window) is { X: > 420 }, "wide editor and grade review sit alongside");
            await CaptureWithFrame(main, output, "01-grade-preview-wide");
            await Publish(lostReply: true);
            review = Find<ListBox>(window, "ActionPolicyReviewRows");
            var tabs = Find<TabControl>(window, "ActionPolicyTabs");
            await VerifyStep();
            var observations = Find<ListBox>(window, "ActionPolicyScreenEvidence");
            string ScreenRow(string bindingId) => observations.Items.OfType<string>().Single(row => row.Contains(bindingId));
            bool FrontNeedsRecordingOnly()
            {
                var row = ScreenRow(access.FrontId);
                return row.Contains("旧版大屏回执 v4") && row.Contains("此屏需升级才能应用手动录课规则") &&
                    !row.Contains("手动自习") && !row.Contains("设备已应用");
            }
            bool RearNeedsBoth()
            {
                var row = ScreenRow(access.RearId);
                return row.Contains("旧版大屏回执 v3") && row.Contains("此屏需升级才能应用手动自习、手动录课规则") &&
                    !row.Contains("设备已应用");
            }
            async Task Refresh()
            {
                await Until(() => Action("Refresh").IsEffectivelyEnabled, "receipt refresh is available");
                Click(Action("Refresh"));
                await Until(() => Action("Refresh").IsEffectivelyEnabled, "latest per-screen read finished");
            }
            for (int refresh = 0; refresh < 8; refresh++)
            {
                if (FrontNeedsRecordingOnly() && RearNeedsBoth()) break;
                await Task.Delay(300);
                await Refresh();
            }
            Check(FrontNeedsRecordingOnly() && RearNeedsBoth(), "v4 needs only recording while v3 needs both configured actions");
            await CaptureWithFrame(main, output, "02-version-specific-upgrade-narrow");

            await EditStep();
            tabs.SelectedIndex = 0;
            Option("Kind").SelectedIndex = 1;
            await ChooseTarget(access.ClassCode);
            Check(Content("Source").Contains("继承年级默认"), "class did not inherit grade policy");
            var recording = Option("ManualRecordingMode");
            Check(!recording.IsEffectivelyEnabled && recording.SelectedItem?.ToString() == "允许现场操作" &&
                Option("ManualRecordingSource").SelectedItem?.ToString() == "跟随年级", "recording did not start by following the grade");
            Pick("ManualRecordingSource", "本班自定");
            Check(recording.IsEffectivelyEnabled && Option("ExitSource").SelectedItem?.ToString() == "跟随年级" &&
                !Option("ExitMode").IsEffectivelyEnabled && Option("ExitMode").SelectedItem?.ToString() == "禁止现场操作",
                "customizing only manual recording should keep the inherited exit denial");
            Pick("ManualRecordingMode", "验证后允许");
            Pick("ManualRecordingFactor", "本班口令");
            Check(Find<TextBlock>(window, "WorkingScope").Text?.Contains("草稿未发布") == true && !Option("ExitFactor").IsEffectivelyVisible &&
                Content("ExitModeRead") == "禁止现场操作" && Content("ExitFactorRead") == "不适用",
                "the draft and inherited exit denial must be visible without an unrelated credential");
            Click(Action("Close"));
            Check(window.IsEffectivelyVisible && Content("Status").Contains("已保留当前草稿"), "dirty return preserves exact draft");
            await VerifyStep();
            var narrowReview = Find<Grid>(window, "VerifyPane");
            await Until(() => !Find<Grid>(window, "EditorPane").IsEffectivelyVisible &&
                narrowReview.Bounds.Width >= window.Bounds.Width - 32 && narrowReview.TranslatePoint(default, window) is { X: <= 16 },
                "narrow verification takes the full width");
            await EditStep();
            tabs.SelectedIndex = 1;
            Check(!Action("Enroll").IsEffectivelyEnabled, "a separate USB write cannot replace the current rule draft");
            tabs.SelectedIndex = 0;
            Check(recording.SelectedItem?.ToString() == "验证后允许" && Option("ManualRecordingFactor").SelectedItem?.ToString() == "本班口令",
                "step and USB-tab switches preserve the exact recording draft");
            var editorScroll = Find<ScrollViewer>(window, "PolicyRulesScroll");
            await Until(() => recording.TranslatePoint(new(0, recording.Bounds.Height), editorScroll)?.Y <= editorScroll.Bounds.Height,
                "the narrow edited recording row and its controls fit above the footer");
            await CaptureWithFrame(main, output, "03-manual-recording-narrow");
            main.Width = 1160;
            main.Height = 780;
            await Until(() => recording.Bounds.Width >= 109, "wide action selector has room for its entire mode label");
            await CaptureWithFrame(main, output, "04-manual-recording-wide");
            main.Width = 840;
            main.Height = 480;
            Click(Action("SavePreview"));
            await Until(() => review.IsEffectivelyVisible && review.Items.Count == 1 && Content("ReviewSummary").Contains("2 台大屏"), "override only that exact class and its two screens");
            Check(Content("ReviewSummary").StartsWith("开始手动录课 · 允许现场操作（年级默认） → 本班口令（本班自定）"),
                "the single changed action and its effective before/after are visible above the detailed scroll");
            var classReview = review.Items.OfType<string>().Single();
            Check(classReview.Contains("手动录课 本班口令（本班自定）") &&
                classReview.Contains("手动自习 允许现场操作（年级默认）") &&
                classReview.Contains("课堂模式 沿用原行为") && classReview.Contains("退出 禁止现场操作（年级默认）"),
                "preview omitted the selected rule or its independent inheritance");
            Check(classReview.Contains("回执 v3") && classReview.Contains("回执 v4") &&
                classReview.Contains("v1–v3 大屏需升级才能应用手动自习、手动录课规则。") &&
                classReview.Contains("v4 大屏需升级才能应用手动录课规则。"), "preview did not distinguish the version ranges and the observed screen schemas");
            Check(classReview.Split('\n').Count(line => line == "本班适用 U 盘 1 支") == 2,
                "the shared class USB pool should be separate from action rules in both previews");
            await CaptureWithFrame(main, output, "05-preview-narrow");
            await Publish();
            Check(Content("Source").Contains("已发布：逐项设置") && !Find<TextBlock>(window, "ActionPolicyDraft").IsEffectivelyVisible,
                "class override was not activated in KV");
            await VerifyStep();
            bool HasClassReceipts() => FrontNeedsRecordingOnly() && RearNeedsBoth() &&
                ScreenRow(access.FrontId).Contains("上次回报版本 3") && ScreenRow(access.RearId).Contains("上次回报版本 3");
            for (int refresh = 0; refresh < 8; refresh++)
            {
                if (HasClassReceipts()) break;
                await Task.Delay(300);
                await Refresh();
            }
            Check(HasClassReceipts(), "both current receipts should reflect the specific missing actions after publication");
            using var checkpoint = new HttpClient { BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(15) };
            using (var injected = await checkpoint.PostAsync("/__classroom-policy-fixture/fault-rear", null))
                Check(injected.IsSuccessStatusCode, "rear storage fault accepted on the current device session");
            await Refresh();
            var rearFault = ScreenRow(access.RearId);
            Check(FrontNeedsRecordingOnly() && rearFault.Contains("设备策略存储异常") && !rearFault.Contains("需升级") && !rearFault.Contains("设备已应用"),
                "the current storage fault must take precedence over the receipt schema on the exact rear screen");
            await CaptureWithFrame(main, output, "06-independent-upgrade-and-storage-fault");
            using (var restarted = await checkpoint.PostAsync("/__classroom-policy-fixture/restart-rear", null))
                Check(restarted.IsSuccessStatusCode, "rear device opens a new session without sending a new policy receipt");
            await Refresh();
            var stale = ScreenRow(access.RearId);
            Check(stale.Contains("等待同步") && !stale.Contains("存储异常") && !stale.Contains("需升级") && FrontNeedsRecordingOnly(),
                "the old-session storage fault must not be shown as current or alter the front receipt");
            await CaptureWithFrame(main, output, "07-restarted-screen-waits-for-receipt");
            using (var acknowledged = await checkpoint.PostAsync("/__classroom-policy-fixture/confirm-front", null))
                Check(acknowledged.IsSuccessStatusCode, "front sent the supported receipt for the exact current policy");
            await Refresh();
            Check(ScreenRow(access.FrontId).Contains("设备已应用策略") && ScreenRow(access.RearId).Contains("等待同步") &&
                !ScreenRow(access.RearId).Contains("设备已应用"), "the front receipt never credits the rear screen");
            await CaptureWithFrame(main, output, "08-two-screen-delivery-narrow");
            tabs.SelectedIndex = 0;
            await ChooseTarget(access.NeighborCode);
            Check(Content("Source").Contains("继承年级默认") &&
                Option("ExitMode").SelectedItem?.ToString() == "禁止现场操作" &&
                Option("ManualRecordingSource").SelectedItem?.ToString() == "跟随年级" &&
                recording.SelectedItem?.ToString() == "允许现场操作", "neighbor should still follow the grade for all settings");
            await ChooseTarget(access.ClassCode);
            Click(Action("Inherit"));
            await Until(() => review.IsEffectivelyVisible && Content("ReviewSummary").Contains("恢复继承"), "restoration has its own review");
            await Publish();
            Check(Content("Source").Contains("继承年级默认"), "restored class did not inherit grade policy");
            await VerifyStep();
            Check(observations.Items.OfType<string>().Any(row => row.Contains(access.FrontId) && !row.Contains("设备已应用") && row.Contains("上次回报版本")),
                "restored content was incorrectly credited with the old identical digest receipt");

            tabs.SelectedIndex = 0;
            Option("Kind").SelectedIndex = 0;
            await ChooseTarget(access.GradeCode);
            tabs.SelectedIndex = 1;
            var registry = Find<ListBox>(window, "ActionPolicyUsbRegistry");
            Check(registry.Items.Count == 1 && registry.Items[0]!.ToString() == "验收值班 U 盘", "only the exact grade key is listed");
            registry.SelectedIndex = 0;
            Click(Action("Revoke"));
            await Until(() => Content("ReviewSummary").Contains("补齐验证方式前") && review.Items.OfType<string>().Any(row => row.Contains("停止本场缺少验证条件")),
                "lost key may be revoked after previewing the actual credential consequence");
            await CaptureWithFrame(main, output, "04-final-key-revoke-warning-narrow");
            await Publish();
            Check(registry.Items.Count == 0, "revoked key remained in the current range");
            Click(Action("Close"));
            await Until(() => !window.IsVisible && entry.IsFocused, "return restores focus to original policy entry");

            using (var archived = await checkpoint.PostAsync("/__classroom-policy-fixture/archive", null))
                Check(archived.IsSuccessStatusCode, "isolated original term was archived after a new USB registration");
            window = await OpenAsync();
            await Until(() => Content("Context").Contains("当前没有启用学期") && Action("OpenHistory").IsEffectivelyEnabled, "historical entry remains available with no active term");
            Check(!Action("Load").IsEffectivelyEnabled, "current term editing remained enabled without an active term");
            Click(Action("OpenHistory"));
            var historical = Find<ListBox>(window, "ActionPolicyHistoryList");
            await Until(() => historical.IsEffectivelyVisible && historical.Items.Count == 1 && Action("HistoryBack").IsEffectivelyEnabled, "original USB registration listed");
            Check(historical.Items[0]?.ToString() is { } row && row.Contains("秋季学期（已归档）") && row.Contains("年级 高二") &&
                row.Contains(access.HistoricalKeyId), "historical key lost its original term, grade or key ID");
            historical.SelectedIndex = 0;
            await CaptureWithFrame(main, output, "05-no-active-term-historical-usb-narrow");
            Click(Action("HistoryRevoke"));
            await Until(() => Action("Apply").IsEffectivelyVisible && Content("ReviewSummary").Contains("有效绑定同步"), "outstanding offline effect is shown before the historical write");
            Check(Content("ReviewSummary").Contains(access.HistoricalKeyId), "confirmation substituted the original key");
            Check(Equals(Action("Apply").Content, "确认撤销原登记"), "historical confirmation clearly names revocation");
            await CaptureWithFrame(main, output, "06-historical-usb-revocation-confirmation-narrow");
            var historicalConfirm = Find<CheckBox>(window, "ActionPolicyConfirmed");
            Check(!Action("Apply").IsEffectivelyEnabled, "historical revocation did not require explicit confirmation");
            historicalConfirm.IsChecked = true;
            Click(Action("Apply"));
            await Until(() => Content("Status").Contains("已撤销原登记") && historical.Items.Count == 0, "exact historical revocation removes only the original row");
            Check(Content("Context").Contains("当前没有启用学期") && !Action("Load").IsEffectivelyEnabled, "historical revocation enabled the original term");
            Click(Action("Close"));
            await Until(() => !window.IsVisible && entry.IsFocused, "historical window returns to the same device entry");
            File.WriteAllText(Path.Combine(output, "desktop-result.json"), JsonSerializer.Serialize(new { passed = true, realWindows = true,
                physicalUsbRegistered = false, verified = new[] { "normal-entry", "wide-grade-review", "lost-response-own-read", "narrow-independent-screens",
                    "class-override", "manual-recording-preview", "neighbor-follows-grade", "version-specific-missing-actions", "standalone-class-usb-count", "current-storage-fault",
                    "superseded-storage-fault", "applied-front-pending-rear", "restore-inheritance",
                    "last-key-revoke-warning", "entry-focus", "historical-no-active-term", "original-key-revocation" } }));
            desktop.Shutdown(0);
        }
        catch (Exception error)
        {
            if (main.IsVisible) await CaptureWithFrame(main, output, "failure");
            File.WriteAllText(Path.Combine(output, "desktop-failure.txt"), error.ToString());
            Console.Error.WriteLine(error);
            desktop.Shutdown(1);
        }
    }
}
