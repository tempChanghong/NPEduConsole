using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
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
            Click(entry);
            ClassroomActionPolicyWindow? view = null;
            await Until(() => (view = desktop.Windows.OfType<ClassroomActionPolicyWindow>().SingleOrDefault())?.IsVisible == true, "policy window opened");
            var window = view!;
            Button Action(string name) => Find<Button>(window, "ActionPolicy" + name);
            ComboBox Option(string name) => Find<ComboBox>(window, "ActionPolicy" + name);
            string Content(string name) => Find<TextBlock>(window, "ActionPolicy" + name).Text ?? "";
            void Pick(string option, string label)
            {
                var control = Option(option);
                control.SelectedItem = control.Items.Cast<object>().Single(item => item.ToString() == label);
            }
            var targets = Option("Target");
            async Task ChooseTarget(string code)
            {
                await Until(() => targets.Items.Cast<object>().Any(item => item.ToString()!.EndsWith(" · " + code, StringComparison.Ordinal)), "exact target visible " + code);
                targets.SelectedItem = targets.Items.Cast<object>().Single(item => item.ToString()!.EndsWith(" · " + code, StringComparison.Ordinal));
                Click(Action("Load"));
                await Until(() => Action("SavePreview").IsEffectivelyVisible && Action("SavePreview").IsEffectivelyEnabled, "selected policy target loaded");
            }
            async Task Publish()
            {
                var agree = Find<CheckBox>(window, "ActionPolicyConfirmed");
                await Until(() => agree.IsEffectivelyVisible && Action("Apply").IsEffectivelyVisible, "explicit review available");
                Check(!Action("Apply").IsEffectivelyEnabled, "unconfirmed publication was enabled");
                agree.IsChecked = true;
                Click(Action("Apply"));
                await Until(() => Content("Status").Contains("本人已发布", StringComparison.Ordinal), "own policy receipt visible");
            }

            window.Width = 1160;
            window.Height = 780;
            await ChooseTarget(access.GradeCode);
            Check(Content("Context").Contains("秋季学期") && Content("Source").Contains("沿用原教室端规则"), "original grade/term was not clear");
            Option("StopMode").SelectedIndex = 1;
            Option("StopFactor").SelectedIndex = 3;
            Option("ExitMode").SelectedIndex = 2;
            Pick("ManualRecordingMode", "允许现场操作");
            Click(Action("SavePreview"));
            var review = Find<ListBox>(window, "ActionPolicyReviewRows");
            await Until(() => review.IsEffectivelyVisible && review.Items.Count == 3, "grade preview lists the three real classes");
            Check(review.Items.OfType<string>().Any(row => row.Contains("全部") && row.Contains("退出 禁止现场操作") &&
                row.Contains("手动录课 允许现场操作（年级默认）") && row.Contains("同班后屏")), "preview omitted the grade rule or second screen");
            await CaptureWithFrame(window, output, "01-grade-preview-wide");
            await Publish();
            var tabs = Find<TabControl>(window, "ActionPolicyTabs");
            tabs.SelectedIndex = 2;
            var observations = Find<ListBox>(window, "ActionPolicyScreenEvidence");
            for (int refresh = 0; refresh < 8; refresh++)
            {
                if (observations.Items.OfType<string>().Any(row => row.Contains(access.FrontId) && row.Contains("需升级才能应用手动录课规则"))) break;
                await Task.Delay(300);
                await Until(() => Action("Refresh").IsEffectivelyEnabled, "refresh after policy publication");
                Click(Action("Refresh"));
                await Until(() => Action("Refresh").IsEffectivelyEnabled, "latest per-screen read finished");
            }
            Check(observations.Items.OfType<string>().Any(row => row.Contains(access.FrontId) && row.Contains("需升级才能应用手动录课规则")) &&
                observations.Items.OfType<string>().Any(row => row.Contains(access.RearId) && row.Contains("等待同步")), "front receipt and rear pending status were conflated");
            window.Width = 840;
            window.Height = 480;
            await CaptureWithFrame(window, output, "02-front-upgrade-rear-pending-narrow");

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
            Check(Find<TextBlock>(window, "ActionPolicyDraft").IsEffectivelyVisible && !Option("ExitFactor").IsEffectivelyVisible &&
                Content("ExitModeRead") == "禁止现场操作" && Content("ExitFactorRead") == "不适用",
                "the draft and inherited exit denial must be visible without an unrelated credential");
            await CaptureWithFrame(window, output, "03-manual-recording-narrow");
            window.Width = 1160;
            window.Height = 780;
            await CaptureWithFrame(window, output, "04-manual-recording-wide");
            window.Width = 840;
            window.Height = 480;
            Click(Action("SavePreview"));
            await Until(() => review.IsEffectivelyVisible && review.Items.Count == 1 && Content("ReviewSummary").Contains("2 台大屏"), "override only that exact class and its two screens");
            Check(review.Items.OfType<string>().Single().Contains("手动录课 本班口令（本班自定）") &&
                review.Items.OfType<string>().Single().Contains("手动自习 沿用原行为") &&
                review.Items.OfType<string>().Single().Contains("课堂模式 沿用原行为") &&
                review.Items.OfType<string>().Single().Contains("旧版大屏需升级才能应用手动录课规则") &&
                review.Items.OfType<string>().Single().Contains("退出 禁止现场操作（年级默认）"), "preview omitted the new rule, origins or required upgrade");
            await CaptureWithFrame(window, output, "05-preview-narrow");
            await Publish();
            Check(Content("Source").Contains("已发布：逐项设置") && !Find<TextBlock>(window, "ActionPolicyDraft").IsEffectivelyVisible,
                "class override was not activated in KV");
            tabs.SelectedIndex = 2;
            string ScreenRow(string bindingId) => observations.Items.OfType<string>().Single(row => row.Contains(bindingId));
            bool HasNewLegacyReceipt()
            {
                var row = ScreenRow(access.FrontId);
                return row.Contains("此屏需升级才能应用手动录课规则") && row.Contains("上次回报版本 3") && !row.Contains("设备已应用");
            }
            for (int refresh = 0; refresh < 8; refresh++)
            {
                if (HasNewLegacyReceipt() && !ScreenRow(access.RearId).Contains("等待同步")) break;
                await Task.Delay(300);
                await Until(() => Action("Refresh").IsEffectivelyEnabled, "refresh after classroom mode publication");
                Click(Action("Refresh"));
                await Until(() => Action("Refresh").IsEffectivelyEnabled, "classroom mode evidence finished");
            }
            Check(HasNewLegacyReceipt(), "a v4 receipt for this publication should leave manual recording unconfirmed");
            var rearFault = ScreenRow(access.RearId);
            Check(rearFault.Contains("设备策略存储异常") && !rearFault.Contains("需升级") && !rearFault.Contains("设备已应用"),
                "the current storage fault must take precedence over the receipt schema on the exact rear screen");
            await CaptureWithFrame(window, output, "06-independent-upgrade-and-storage-fault");
            using var checkpoint = new HttpClient { BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(15) };
            using (var restarted = await checkpoint.PostAsync("/__classroom-policy-fixture/restart-rear", null))
                Check(restarted.IsSuccessStatusCode, "rear device opens a new session without sending a new policy receipt");
            Click(Action("Refresh"));
            await Until(() => Action("Refresh").IsEffectivelyEnabled, "screen receipts refreshed after rear device restarted");
            var stale = ScreenRow(access.RearId);
            Check(stale.Contains("等待同步") && !stale.Contains("存储异常") && !stale.Contains("需升级") && HasNewLegacyReceipt(),
                "the old-session storage fault must not be shown as current or alter the front receipt");
            await CaptureWithFrame(window, output, "07-restarted-screen-waits-for-receipt");
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
            tabs.SelectedIndex = 2;
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
            await CaptureWithFrame(window, output, "04-final-key-revoke-warning-narrow");
            await Publish();
            Check(registry.Items.Count == 0, "revoked key remained in the current range");
            Click(Action("Close"));
            await Until(() => !window.IsVisible && entry.IsFocused, "return restores focus to original policy entry");

            using (var archived = await checkpoint.PostAsync("/__classroom-policy-fixture/archive", null))
                Check(archived.IsSuccessStatusCode, "isolated original term was archived after a new USB registration");
            Click(entry);
            await Until(() => (view = desktop.Windows.OfType<ClassroomActionPolicyWindow>().SingleOrDefault(candidate => candidate.IsVisible)) is not null,
                "normal policy entry reopens after the only active term is archived");
            window = view!;
            window.Width = 840;
            window.Height = 480;
            await Until(() => Content("Context").Contains("当前没有启用学期") && Action("OpenHistory").IsEffectivelyEnabled, "historical entry remains available with no active term");
            Check(!Action("Load").IsEffectivelyEnabled, "current term editing remained enabled without an active term");
            Click(Action("OpenHistory"));
            var historical = Find<ListBox>(window, "ActionPolicyHistoryList");
            await Until(() => historical.IsEffectivelyVisible && historical.Items.Count == 1 && Action("HistoryBack").IsEffectivelyEnabled, "original USB registration listed");
            Check(historical.Items[0]?.ToString() is { } row && row.Contains("秋季学期（已归档）") && row.Contains("年级 高二") &&
                row.Contains(access.HistoricalKeyId), "historical key lost its original term, grade or key ID");
            historical.SelectedIndex = 0;
            await CaptureWithFrame(window, output, "05-no-active-term-historical-usb-narrow");
            Click(Action("HistoryRevoke"));
            await Until(() => Action("Apply").IsEffectivelyVisible && Content("ReviewSummary").Contains("有效绑定同步"), "outstanding offline effect is shown before the historical write");
            Check(Content("ReviewSummary").Contains(access.HistoricalKeyId), "confirmation substituted the original key");
            Check(Equals(Action("Apply").Content, "确认撤销原登记"), "historical confirmation clearly names revocation");
            await CaptureWithFrame(window, output, "06-historical-usb-revocation-confirmation-narrow");
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
                    "class-override", "manual-recording-preview", "neighbor-follows-grade", "old-device-receipt-pending", "current-storage-fault",
                    "superseded-storage-fault", "restore-inheritance",
                    "last-key-revoke-warning", "entry-focus", "historical-no-active-term", "original-key-revocation" } }));
            desktop.Shutdown(0);
        }
        catch (Exception error)
        {
            var visible = desktop.Windows.OfType<ClassroomActionPolicyWindow>().FirstOrDefault(window => window.IsVisible);
            if (visible is not null) await CaptureWithFrame(visible, output, "failure");
            File.WriteAllText(Path.Combine(output, "desktop-failure.txt"), error.ToString());
            Console.Error.WriteLine(error);
            desktop.Shutdown(1);
        }
    }
}
