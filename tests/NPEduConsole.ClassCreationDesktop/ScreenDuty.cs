using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private sealed record ScreenDutyAccess(string Origin, string SchoolCode, string SchoolId, string Pin, string ResetPin,
        string FrontId, string RearId, string SevenId, string EightId, string ArchivedId, string OriginalClassId, string HistoricalBindingId,
        bool AllowWrites);

    private static int StartScreenDuty(string fixture, string output)
    {
        var access = JsonSerializer.Deserialize<ScreenDutyAccess>(File.ReadAllText(fixture), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Expected the isolated screen-duty fixture.");
        Check(access.AllowWrites && IsLoopback(access.Origin), "Screen-duty writes require a disposable loopback fixture.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyScreenDutyAsync(access, output); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyScreenDutyAsync(ScreenDutyAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        var mainHandle = main.TryGetPlatformHandle()?.Handle;
        using var checkpoints = new HttpClient {BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(100)};
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
            await Until(() => schools.Items.OfType<School>().Any(row => row.Id == access.SchoolId), "authorized school available");
            schools.SelectedItem = schools.Items.OfType<School>().Single(row => row.Id == access.SchoolId);
            var overview = Find<SchoolOverviewView>(main, "SchoolOverviewWorkspace");
            var filter = overview.FindControl<ComboBox>("OverviewTaskFilter")!;
            await Until(() => filter.IsEffectivelyVisible, "school overview queue rendered");
            filter.SelectedIndex = 2;
            var tasks = overview.FindControl<ListBox>("OverviewTasksList")!;
            await Until(() => tasks.Items.OfType<OverviewTaskItem>().Any(row => row.Task.Code == "SCREEN_WEB_ACCOUNT_REQUIRED" &&
                row.Task.ScreenId == access.RearId), "only the rear screen is missing the webpage account");
            Check(tasks.Items.OfType<OverviewTaskItem>().All(row => row.Task.ScreenId != access.FrontId ||
                row.Task.Code != "SCREEN_WEB_ACCOUNT_REQUIRED"), "the paired front screen's existing account is not missing");
            var task = tasks.Items.OfType<OverviewTaskItem>().Single(row => row.Task.Code == "SCREEN_WEB_ACCOUNT_REQUIRED" && row.Task.ScreenId == access.RearId);
            await Until(() => tasks.ContainerFromIndex(0) is ListBoxItem, "filtered device tasks arranged");
            tasks.ScrollIntoView(task);
            await Until(() => tasks.GetVisualDescendants().OfType<Button>().Any(row => ReferenceEquals(row.Tag, task)), "rear screen task action available");
            Click(tasks.GetVisualDescendants().OfType<Button>().Single(row => ReferenceEquals(row.Tag, task)));
            var window = await Workbench();
            await Until(() => window.Detail?.Id == access.RearId && Label(window, "NativeDutyState").Contains("已有有效上报"),
                "overview follows the rear binding without confusing the paired front");
            await CaptureWithFrame(main, output, "01-wide-paired-no-web-account");
            Click(Find<Button>(window, "DutyPrimary"));
            Find<TextBox>(window, "DutyCode").Text = "duty-rear";
            var pin = Find<TextBox>(window, "DutyPin");
            pin.Text = access.Pin;
            Check(pin.PasswordChar != '\0', "screen PIN is masked");
            await Review(window);
            Check(Label(window, "DutyReviewNative").Contains("可继续使用"), "in-place setup preserves native pairing");
            await Submit(window);
            var handoffFile = Path.Combine(output, "temporary-screen-handoff.txt");
            await File.WriteAllTextAsync(handoffFile, "");
            try
            {
                window.SelectPinFile = _ => main.StorageProvider.TryGetFileFromPathAsync(new Uri(handoffFile));
                await Until(() => Find<Button>(window, "DutySavePin").IsEffectivelyEnabled && Find<Button>(window, "DutySavePin").IsVisible,
                    "fresh screen configuration offers only its own web PIN");
                Click(Find<Button>(window, "DutySavePin"));
                await Until(() => Label(window, "DutyNotice").Contains("已另存"), "screen handoff completed");
                var text = await File.ReadAllTextAsync(handoffFile);
                Check(text.Contains(access.SchoolCode) && text.Contains("duty-rear", StringComparison.OrdinalIgnoreCase) &&
                    text.EndsWith("PIN " + access.Pin) && !Find<Button>(window, "DutyCopyPin").IsVisible,
                    "saved the intended binding's web account and original PIN exactly once");
            }
            finally { File.Delete(handoffFile); }
            await Signal("configured");
            await Refresh(window);
            Check(Label(window, "BrowserDutyEvidence").Contains("DUTY-REAR") &&
                !Label(window, "BrowserDutyEvidence").Contains("当前凭据心跳 暂无"), "the browser's first heartbeat and the paired native report coexist");
            Check(Grid.GetColumn(Find<Border>(window, "NativeDutyCard")) == 1 &&
                Find<Border>(window, "BrowserDutyCard").IsEffectivelyVisible &&
                Find<Border>(window, "NativeDutyCard").IsEffectivelyVisible, "wide view shows both current states alongside");
            await CaptureWithFrame(main, output, "02-wide-real-browser-heartbeat");

            Click(Find<Button>(window, "DutyPrimary"));
            await Apply(window, "DutyRefreshData");
            await Signal("refresh");
            await Refresh(window, () => Label(window, "DutyCommandPicked").Contains("已确认刷新数据"));
            await Apply(window, "DutyRefreshData");
            await Signal("expired");
            await Refresh(window, () => Label(window, "DutyCommandPicked").Contains("已过期"));
            await Signal("edit-begin");
            await Apply(window, "DutyReloadWeb");
            await Signal("editing-held");
            await Refresh(window, () => Label(window, "DutyCommandPicked").Contains("已送达，等待回执"));
            await CaptureWithFrame(main, output, "03-editing-saved-from-reload");
            await Signal("reload-accepted");
            await Refresh(window, () => Label(window, "DutyCommandPicked").Contains("已接收，等待空闲重载"));
            await CaptureWithFrame(main, output, "04-reload-ack-is-not-page-navigation");
            await Signal("reload-completed");

            main.Width = 840;
            main.Height = 480;
            await Until(() => main.ClientSize.Width <= 845 && main.ClientSize.Height <= 485 &&
                Grid.GetRow(Find<Border>(window, "NativeDutyCard")) == 1, "compact layout stacks browser and native evidence");
            await ShowManagement(window);
            Click(Find<Button>(window, "DutyMove"));
            SelectEight(window);
            await Review(window);
            Check(Label(window, "DutyReviewObject").Contains(access.RearId) && Label(window, "DutyReviewObject").Contains("高二（8）班"),
                "moves must review the precise binding and destination");
            await Signal("legacy-move");
            Click(Find<Button>(window, "DutyConfirm"));
            await Until(() => Label(window, "DutyNotice").Contains("未写入") && !Find<Border>(window, "DutyReviewPanel").IsVisible,
                "old page changed the destination and now no write is made");
            await Signal("stale-no-write");
            Check((Find<ComboBox>(window, "DutyTargetClass").SelectedItem as ScreenDutyRoom)?.Id == access.EightId,
                "stale preview keeps the destination draft");
            await Review(window);
            Check(Label(window, "DutyReviewPeers").Contains("机房备用屏"), "reconciliation names the actual peer in the destination");
            var confirm = Find<Button>(window, "DutyConfirm");
            Check(confirm.TranslatePoint(new Point(confirm.Bounds.Width, confirm.Bounds.Height), window) is { } corner &&
                corner.X <= window.Bounds.Width && corner.Y <= window.Bounds.Height, "compact confirmation remains reachable");
            await CaptureWithFrame(main, output, "05-narrow-reviewed-move-destination");
            await Submit(window, "已找回服务器确认的原回执");
            await Signal("moved");
            await Refresh(window);
            Check(window.Detail?.Id == access.RearId && Label(window, "DutyIdentity").Contains("高二（8）班") &&
                Label(window, "NativeDutyState").Contains("现场重新配对"), "moved rear retains id and reports re-pair");
            Check(Label(window, "NativeDutyEvidence").Contains("原配对最后上报") &&
                !Label(window, "NativeDutyEvidence").Contains("当前配对有效上报"), "a historical native report is not credited to the current binding");
            var nativeEntry = Find<Button>(window, "DutyNative");
            var viewport = Find<ScrollViewer>(window, "DutyScroll");
            await Until(() => nativeEntry.TranslatePoint(new Point(0, nativeEntry.Bounds.Height), viewport) is { } entryBottom &&
                entryBottom.Y <= viewport.Bounds.Height + 1, "native admission action fits above the footer in the narrow post-move view");
            await CaptureWithFrame(main, output, "06-narrow-moved-rear-repair");

            await Apply(window, "DutyRename", () => Find<TextBox>(window, "DutyName").Text = "八班后屏");
            await ShowManagement(window);
            Click(Find<Button>(window, "DutyCodeChange"));
            Find<TextBox>(window, "DutyCode").Text = " duty-rear ";
            await Until(() => Label(window, "DutyNotice").Contains("短账号未变，无需保存") && !Find<Button>(window, "DutyConfirm").IsEnabled,
                "the same short account with different casing and whitespace cannot be submitted");
            await Signal("unchanged-login");
            Click(Find<Button>(window, "DutyBack"));
            await Apply(window, "DutyCodeChange", () => Find<TextBox>(window, "DutyCode").Text = "duty-rear-renamed");
            await Signal("changed-login");
            await ShowManagement(window);
            Click(Find<Button>(window, "DutyResetPin"));
            Find<TextBox>(window, "DutyPin").Text = access.ResetPin;
            await Review(window);
            Check(Label(window, "DutyReviewNative").Contains("可继续使用") && Label(window, "DutyReviewNative").Contains("重新发起"),
                "reset impact distinguishes active devices from unfinished operations");
            await Submit(window);
            await Signal("updated-pin");
            await CaptureWithFrame(main, output, "08-narrow-pin-rotation");
            await Apply(window, "DutyToggle");
            await Signal("inactive");
            Click(Find<Button>(window, "DutyBack"));
            await Until(() => !window.IsVisible && main.IsEnabled && filter.SelectedIndex == 2 && filter.IsFocused &&
                tasks.Items.OfType<OverviewTaskItem>().All(row => row.Task.ScreenId != access.RearId ||
                    row.Task.Code != "SCREEN_WEB_ACCOUNT_REQUIRED"),
                "return re-reads the school and restores the filter when the original task has cleared");
            Click(Find<Button>(main, "OpenDeviceDirectoryButton"));
            var directory = Find<SchoolDeviceDirectoryView>(main, "DeviceDirectory");
            var dutyDirectory = directory.FindControl<Button>("DeviceDutyDirectory")!;
            await Until(() => dutyDirectory.IsEffectivelyVisible, "normal device directory visible");
            Click(dutyDirectory);
            window = await Workbench();
            var stopped = Find<CheckBox>(window, "DutyDisabledOnly");
            await Until(() => stopped.IsEffectivelyVisible, "device management catalog includes inactive bindings");
            stopped.IsChecked = true;
            var items = Find<ListBox>(window, "DutyRows");
            await Until(() => items.Items.Cast<object>().Any(row => row.ToString()!.Contains(access.RearId)), "same stopped binding found under inactive filter");
            items.SelectedItem = items.Items.Cast<object>().Single(row => row.ToString()!.Contains(access.RearId));
            Click(Find<Button>(window, "DutyOpen"));
            await Until(() => window.Detail?.Id == access.RearId && Label(window, "BrowserDutyState").Contains("已停用"),
                "reopen stopped screen by id from the device directory");
            await Apply(window, "DutyPrimary");
            await Signal("restored");
            await Apply(window, "DutyUnbindBrowser");
            await Signal("unbound");
            await Refresh(window);
            Check(Label(window, "NativeDutyState").Contains("已有有效上报") && Label(window, "BrowserDutyState").Contains("等待使用网页账号登录"),
                "browser unlink retains the restored native pairing");
            Check(Label(window, "BrowserDutyEvidence").Contains("当前凭据心跳 暂无") &&
                !Label(window, "BrowserDutyEvidence").Contains("变更前最后心跳") &&
                Label(window, "DutyBrowserHistoryText").Contains("变更前最后心跳"), "old browser evidence appears only in history");
            await CaptureWithFrame(main, output, "08a-narrow-browser-unbound-native-still-reported");
            await ShowManagement(window);
            await CaptureWithFrame(main, output, "08b-narrow-web-and-screen-management");
            Click(Find<Button>(window, "DutyAnother"));
            SelectEight(window);
            Find<TextBox>(window, "DutyName").Text = "八班新增屏";
            Find<TextBox>(window, "DutyCode").Text = "duty-extra";
            Find<TextBox>(window, "DutyPin").Text = access.Pin;
            await Review(window);
            Click(Find<Button>(window, "DutyConfirm"));
            var retry = Find<Button>(window, "DutyRetryButton");
            await Until(() => retry.IsEffectivelyVisible && retry.IsEnabled, "only allow same-request retry after looking for a receipt");
            await Signal("create-unsent");
            var retryPin = Find<TextBox>(window, "DutyRetryPin");
            Check(string.IsNullOrEmpty(retryPin.Text) && retryPin.PasswordChar != '\0', "retry asks for the original PIN without retaining it");
            retryPin.Text = access.Pin;
            Click(retry);
            await Until(() => Label(window, "DutyNotice").Contains("服务器已确认"), "same request creates once after its first attempt never reached KV");
            await Signal("another-created");
            await CaptureWithFrame(main, output, "07-new-separate-screen");
            await File.WriteAllTextAsync(handoffFile, "保留原文件");
            try
            {
                window.SelectPinFile = async _ =>
                {
                    await Signal("rotate-during-handoff");
                    return await main.StorageProvider.TryGetFileFromPathAsync(new Uri(handoffFile));
                };
                Click(Find<Button>(window, "DutySavePin"));
                await Until(() => Label(window, "DutyNotice").Contains("已经变化") && !Find<Button>(window, "DutySavePin").IsVisible,
                    "changing the credential during file selection withdraws the delivery");
                Check(await File.ReadAllTextAsync(handoffFile) == "保留原文件", "a stale PIN never replaces the destination file");
            }
            finally { File.Delete(handoffFile); }
            await CloseDutyAndWaitForDirectory(window);

            Click(dutyDirectory);
            window = await Workbench();
            var historyFilter = Find<CheckBox>(window, "DutyHistory");
            var rows = Find<ListBox>(window, "DutyRows");
            await Until(() => historyFilter.IsEffectivelyVisible && rows.Items.Cast<object>().Any(row => row.ToString()!.Contains(access.FrontId)),
                "the default catalogue lists operational screens");
            Check(rows.Items.Cast<object>().All(row => !row.ToString()!.Contains(access.HistoricalBindingId)), "historical bindings are opt-in");
            historyFilter.IsChecked = true;
            await Until(() => rows.Items.Cast<object>().Any(row => row.ToString()!.Contains(access.HistoricalBindingId)), "archived room appears by its binding ID");
            rows.SelectedItem = rows.Items.Cast<object>().Single(row => row.ToString()!.Contains(access.HistoricalBindingId));
            Click(Find<Button>(window, "DutyOpen"));
            await Until(() => window.Detail?.Room.Id == access.OriginalClassId && window.Detail.Room.TermId == access.ArchivedId,
                "the historical screen opens under the original same-code class and term");
            Check(!Find<Button>(window, "DutyRefreshData").IsEffectivelyEnabled && !Find<Button>(window, "DutyResetPin").IsVisible,
                "historical controls offer neither current web commands nor historical PIN editing");
            main.Width = 1160;
            main.Height = 780;
            await CaptureWithFrame(main, output, "09-original-archived-binding-wide");
            Click(Find<Button>(window, "DutyUnbindBrowser"));
            await Review(window);
            Check(Label(window, "DutyReviewWeb").Contains("归档期间无法网页登录") &&
                !Label(window, "DutyReviewNative").Contains("可继续使用"), "unlink preview describes archival limits");
            main.Width = 840;
            main.Height = 480;
            await CaptureWithFrame(main, output, "10-original-archived-unlink-narrow");
            await Submit(window);
            await Signal("archived-unbind");
            await Apply(window, "DutyToggle");
            Click(Find<Button>(window, "DutyPrimary"));
            await Review(window);
            Check(Label(window, "DutyReviewWeb").Contains("仅恢复原绑定") && Label(window, "DutyReviewWeb").Contains("归档期间无法网页登录"),
                "restoring a historical binding does not imply login is available");
            await Submit(window);
            await Signal("archived-restored");

            await ShowManagement(window);
            Click(Find<Button>(window, "DutyMove"));
            SelectEight(window);
            await Review(window);
            Check(Label(window, "DutyReviewObject").Contains("归档同码学期") && Label(window, "DutyReviewObject").Contains("目标班"),
                "the original and destination are displayed together before moving");
            Click(Find<Button>(window, "DutyConfirm"));
            await Until(() => Find<Button>(window, "DutyBack").IsEnabled && Find<Button>(window, "DutyConfirm") is
                {IsEnabled: true, Content: "重试核对原回执"} && Find<TextBlock>(window, "DutyNotice").Classes.Contains("error"),
                "a failed receipt read releases the close protection while retaining the operation");
            await Signal("archived-moved");
            await CloseDutyAndWaitForDirectory(window);

            var newBinding = () => directory.GetVisualDescendants().OfType<Button>().FirstOrDefault(button =>
                button.Classes.Contains("screen-duty-entry") && button.DataContext is OverviewScreenItem item &&
                item.ScreenId == access.HistoricalBindingId && item.ClassId == access.EightId && button.IsEffectivelyVisible);
            var directorySearch = directory.FindControl<TextBox>("DeviceSearch")!;
            directorySearch.Text = access.HistoricalBindingId;
            await Until(() => newBinding() is {IsEffectivelyEnabled: true}, "the moved screen is visible in the new classroom");
            Click(newBinding()!);
            window = await Workbench();
            await Until(() => window.Detail?.Room.Id == access.EightId && Label(window, "DutyNotice").Contains("其他大屏操作待核对"),
                "a different class cannot open the historical request of the same binding");
            Check(!Find<Button>(window, "DutyConfirm").IsVisible && !Find<StackPanel>(window, "DutyRetryPanel").IsVisible,
                "the new scope does not offer a receipt lookup or resend from the old scope");
            await Signal("other-scope-no-receipt");
            await CloseDutyAndWaitForDirectory(window);
            await Until(() => newBinding() is { IsFocused: true }, "return restores focus to the exact screen duty entry");
            Click(dutyDirectory);
            window = await Workbench();
            await Until(() => window.Detail?.Room.Id == access.EightId && Label(window, "DutyNotice").Contains("找回服务器确认的原回执"),
                "the school-wide catalogue can still look up the original move");
            await Signal("own-history-receipt");
            desktop.Shutdown(0);

            async Task CloseDutyAndWaitForDirectory(ScreenDutyView active)
            {
                var refreshed = false;
                void Refreshed(SchoolOverview? snapshot, string status) => refreshed |= snapshot is not null;
                overview.SnapshotChanged += Refreshed;
                try
                {
                    Click(Find<Button>(active, "DutyBack"));
                    if (active.IsEffectivelyVisible)
                    {
                        await Until(() => !active.IsEffectivelyVisible ||
                            Find<StackPanel>(active, "DutyDirectoryPanel").IsEffectivelyVisible, "return to the original screen catalogue");
                        if (active.IsEffectivelyVisible) Click(Find<Button>(active, "DutyBack"));
                    }
                    await Until(() => refreshed && !active.IsVisible && main.IsEnabled && dutyDirectory.IsEffectivelyEnabled,
                        "workbench returned and the equipment directory consumed its refreshed snapshot");
                }
                finally { overview.SnapshotChanged -= Refreshed; }
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            var current = desktop.Windows.LastOrDefault(row => row.IsVisible);
            if (current is not null) try { await Capture(current, output, "screen-duty-desktop-failure"); } catch { }
            desktop.Shutdown(1);
        }

        async Task<ScreenDutyView> Workbench()
        {
            var view = await AcademicPageAsync<ScreenDutyView>(main);
            Check(TopLevel.GetTopLevel(view) == main && mainHandle is not null && main.TryGetPlatformHandle()?.Handle == mainHandle &&
                desktop.Windows.Count(row => row.IsVisible) == 1, "the original main HWND hosts screen duty");
            return view;
        }

        async Task ShowManagement(ScreenDutyView window)
        {
            var panel = Find<StackPanel>(window, "DutyManagePanel");
            if (!panel.IsEffectivelyVisible) Click(Find<Button>(window, "DutyManageStep"));
            await Until(() => panel.IsEffectivelyVisible, "screen management step is reachable");
        }

        async Task Signal(string checkpoint)
        {
            using var result = await checkpoints.PostAsync("/__duty/" + checkpoint, null);
            Check(result.IsSuccessStatusCode, "isolated screen duty checkpoint: " + checkpoint);
        }

        void SelectEight(ScreenDutyView window)
        {
            var rooms = Find<ComboBox>(window, "DutyTargetClass");
            rooms.SelectedItem = rooms.Items.OfType<ScreenDutyRoom>().Single(row => row.Id == access.EightId);
        }

        async Task Review(ScreenDutyView window)
        {
            Click(Find<Button>(window, "DutyConfirm"));
            await Until(() => Find<Border>(window, "DutyReviewPanel").IsVisible && Find<Button>(window, "DutyConfirm").IsEnabled,
                "action has reviewed consequences");
        }

        async Task Submit(ScreenDutyView window, string expected = "服务器已")
        {
            Click(Find<Button>(window, "DutyConfirm"));
            await Until(() => Label(window, "DutyNotice").Contains(expected) && Find<Button>(window, "DutyBack").IsEnabled,
                "screen operation is confirmed");
        }

        async Task Apply(ScreenDutyView window, string name, Action? fill = null)
        {
            var action = Find<Button>(window, name);
            if (Find<StackPanel>(window, "DutyManagePanel").GetVisualDescendants().Contains(action)) await ShowManagement(window);
            await Until(() => action.IsEffectivelyVisible && action.IsEnabled, name + " available");
            Click(action);
            fill?.Invoke();
            await Review(window);
            await Submit(window);
        }

        async Task Refresh(ScreenDutyView window, Func<bool>? proof = null)
        {
            var refresh = Find<Button>(window, "DutyRefresh");
            await Until(() => refresh.IsEnabled, "screen duty refresh available");
            Click(refresh);
            await Until(() => refresh.IsEnabled && Label(window, "DutyNotice").Contains("服务器最新") && (proof?.Invoke() ?? true),
                "latest evidence is visible");
        }

        static string Label(ScreenDutyView window, string name) => Find<TextBlock>(window, name).Text ?? "";
    }
}
