using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private sealed record WebsiteAccess(string Origin, string SchoolId, string SchoolCode, string Pin,
        string GradeCode, string ClassCode, bool ReadReceipt, bool AllowClass = false,
        string? ExpectedScreen = null, string? ExpectedStatus = null, string? ImageName = null,
        bool Timed = false, string[]? DraftHosts = null, Dictionary<string, string>? HostPeriods = null,
        string? UnavailableBehavior = null, bool ExpectLegacyOnly = false,
        string? ExpectedBefore = null, string? ExpectedAfter = null,
        int? WindowWidth = null, int? WindowHeight = null, bool VerifyWorkspace = false, bool DropPublishReply = false);

    private static int StartWebsitePolicy(string path, string output)
    {
        if (path == "--timing-checks")
        {
            WebsitePolicyTimingChecks.RunAll();
            Console.WriteLine("PASS WebsitePolicyTimingChecks");
            return 0;
        }
        var access = JsonSerializer.Deserialize<WebsiteAccess>(File.ReadAllText(path), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Expected an isolated website policy fixture.");
        Check(IsLoopback(access.Origin), "Website policy verification requires the disposable server.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyWebsitePolicyAsync(access, output); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyWebsitePolicyAsync(WebsiteAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        var hwnd = main.TryGetPlatformHandle()?.Handle;
        try
        {
            var width = access.WindowWidth ?? (access.ReadReceipt ? 840 : 1160);
            var height = access.WindowHeight ?? (access.ReadReceipt ? 480 : 780);
            main.Width = width;
            main.Height = height;
            Find<TextBox>(main, "ServerAddress").Text = access.Origin;
            Find<TextBox>(main, "SchoolCode").Text = access.SchoolCode;
            Find<TextBox>(main, "Username").Text = "admin";
            Find<TextBox>(main, "Password").Text = access.Pin;
            Click(Find<Button>(main, "LoginButton"));
            var schools = Find<ComboBox>(main, "SchoolPicker");
            await Until(() => schools.Items.OfType<School>().Any(row => row.Id == access.SchoolId), "authenticated school");
            schools.SelectedItem = schools.Items.OfType<School>().Single(row => row.Id == access.SchoolId);
            var directoryButton = Find<Button>(main, "OpenDeviceDirectoryButton");
            await Until(() => directoryButton.IsEffectivelyVisible && directoryButton.IsEnabled, "device directory accessible");
            Click(directoryButton);
            var directory = Find<SchoolDeviceDirectoryView>(main, "DeviceDirectory");
            var entry = directory.FindControl<Button>("DeviceWebsitePolicyButton")!;
            await Until(() => entry.IsEffectivelyVisible && entry.IsEffectivelyEnabled, "website policy entry visible");
            async Task<WebsitePolicyView> OpenAsync()
            {
                Click(entry);
                WebsitePolicyView? opened = null;
                await Until(() => (opened = main.GetVisualDescendants().OfType<WebsitePolicyView>().SingleOrDefault())
                    ?.IsEffectivelyVisible == true, "website rule page in original main window");
                Check(TopLevel.GetTopLevel(opened!) == main && hwnd is not null && main.TryGetPlatformHandle()?.Handle == hwnd &&
                    desktop.Windows.Count(item => item.IsVisible) == 1,
                    "website rules use the original window and its title bar");
                return opened!;
            }
            var window = await OpenAsync();
            var editorScroll = Find<ScrollViewer>(window, "WebsiteDetailsScroll");
            var reviewScroll = Find<ScrollViewer>(window, "VerifyDetailsScroll");
            var narrowTabs = Find<Border>(window, "NarrowTabs");
            var verifyButton = Find<Button>(window, "VerifyTabButton");
            var editButton = Find<Button>(window, "EditTabButton");
            await Until(() => narrowTabs.IsVisible == (width < 1000), "responsive website layout");
            var choices = Find<ComboBox>(window, "ScopeChoice");
            if (access.ReadReceipt || access.AllowClass) Find<ComboBox>(window, "ScopeKind").SelectedIndex = 1;
            string code = access.ReadReceipt || access.AllowClass ? access.ClassCode : access.GradeCode;
            await Until(() => choices.Items.Cast<object>().Any(row => row.ToString()!.EndsWith(" · " + code, StringComparison.Ordinal)), "original target ready");
            choices.SelectedItem = choices.Items.Cast<object>().Single(row => row.ToString()!.EndsWith(" · " + code, StringComparison.Ordinal));
            Click(Find<Button>(window, "ReadScopeButton"));
            var hosts = Find<TextBox>(window, "Hosts");
            if (access.ReadReceipt || access.ExpectedScreen is not null)
            {
                await Until(() => Find<ItemsControl>(window, "ScreenRows").Items.OfType<WebsitePolicyScreenRow>().Any(),
                    "read current scope before opening screen evidence");
                if (narrowTabs.IsVisible) Click(verifyButton);
                await Until(() => reviewScroll.IsEffectivelyVisible, "current evidence on review step");
            }
            if (access.ExpectedScreen is { } expected && access.ExpectedStatus is { } status)
            {
                var screens = Find<ItemsControl>(window, "ScreenRows");
                await Until(() => screens.Items.OfType<WebsitePolicyScreenRow>().Any(row => row.Title.Contains(expected) && row.Evidence.Contains(status)),
                    "current device evidence visible");
                await Until(() => screens.GetVisualDescendants().OfType<TextBlock>().Any(row =>
                    row.Text?.Contains(status) == true && Fits(row, reviewScroll)),
                    "current device evidence fits the window");
                await CaptureWithFrame(main, output, access.ImageName ?? "current-device-evidence");
            }
            else if (access.ReadReceipt)
            {
                var screens = Find<ItemsControl>(window, "ScreenRows");
                await Until(() => screens.Items.OfType<WebsitePolicyScreenRow>().Any(row => row.Evidence.Contains("设备已收到")),
                    "actual bound Host acknowledgement visible");
                Check(screens.Items.OfType<WebsitePolicyScreenRow>().Any(row => row.Title.Contains("验收教室前屏") &&
                    row.Evidence.Contains("Edge 生效未确认")), "receipt distinguished from application");
                Check(screens.Items.OfType<WebsitePolicyScreenRow>().Any(row => row.Title.Contains("同班待接入后屏") &&
                    row.Evidence.Contains("未配对原生设备")), "neighboring binding independent");
                await Until(() =>
                {
                    var targetRows = screens.GetVisualDescendants().OfType<TextBlock>().Where(row =>
                        row.Text?.Contains("Edge 生效未确认") == true || row.Text?.Contains("未配对原生设备") == true).ToArray();
                    return targetRows.Length == 2 && targetRows.All(row => Fits(row, reviewScroll));
                }, "both target screen evidence lines fit the narrow review step");
                await CaptureWithFrame(main, output, access.ImageName ?? "02-received-not-applied-840");
            }
            else
            {
                await Until(() => Find<CheckBox>(window, "Inherit").IsEffectivelyVisible &&
                    Find<CheckBox>(window, "Inherit").IsEffectivelyEnabled, "loaded actual scope");
                Find<CheckBox>(window, "Inherit").IsChecked = false;
                string[] targetHosts = access.DraftHosts ?? (access.AllowClass ? [] : ["douyin.com"]);
                hosts.Text = string.Join(Environment.NewLine, targetHosts);
                if (access.ExpectLegacyOnly)
                {
                    Check(!Find<StackPanel>(window, "TimingSection").IsVisible, "legacy KV hides timing controls");
                    Check(!Find<Grid>(window, "UnavailablePanel").IsVisible, "legacy KV hides unavailable behavior");
                }
                else if ((access.Timed || access.HostPeriods is { Count: > 0 }) && targetHosts.Length > 0)
                {
                    var hostPeriods = access.HostPeriods ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [targetHosts[0]] = "LESSON|AFTER_SCHOOL"
                    };
                    var timingRows = Find<ItemsControl>(window, "TimingRows");
                    await Until(() => timingRows.GetVisualDescendants().OfType<CheckBox>().Count() == targetHosts.Length * 4,
                        "per-host timing checkboxes rendered");
                    var boxes = timingRows.GetVisualDescendants().OfType<CheckBox>().ToArray();
                    CheckBox Box(string host, string label) =>
                        boxes.Single(item => AutomationProperties.GetName(item) == $"{host} {label}");

                    foreach (var host in targetHosts)
                    {
                        Check(Box(host, "全天").IsChecked == true, "all hosts default to all-day");
                        Check(Box(host, "上课").IsChecked == false && Box(host, "课间").IsChecked == false && Box(host, "放学后").IsChecked == false,
                            "timed checkboxes start unchecked");
                    }
                    Check(!Find<Grid>(window, "UnavailablePanel").IsVisible, "unavailable behavior hidden while all-day");

                    var previewCheck = Find<Button>(window, "PreviewButton");
                    var firstTimed = hostPeriods.FirstOrDefault(pair => pair.Value != WebsitePolicyTiming.PeriodAlways);
                    if (firstTimed.Key is not null)
                    {
                        Box(firstTimed.Key, "全天").IsChecked = false;
                        await Until(() => !previewCheck.IsEnabled, "empty period combination blocks preview");
                    }

                    foreach (var (host, period) in hostPeriods)
                    {
                        var (isAllDay, isLesson, isBreak, isAfterSchool) = WebsitePolicyTiming.DecodeSelection(period);
                        if (isAllDay)
                        {
                            Box(host, "全天").IsChecked = true;
                        }
                        else
                        {
                            Box(host, "上课").IsChecked = isLesson;
                            Box(host, "课间").IsChecked = isBreak;
                            Box(host, "放学后").IsChecked = isAfterSchool;
                            Check(Box(host, "全天").IsChecked == false, "timed selection clears all-day");
                        }
                    }

                    bool expectsTimed = hostPeriods.Values.Any(period => period != WebsitePolicyTiming.PeriodAlways);
                    var unavailablePanel = Find<Grid>(window, "UnavailablePanel");
                    await Until(() => unavailablePanel.IsVisible == expectsTimed, "unavailable behavior panel matches timed state");
                    if (expectsTimed)
                    {
                        var unavailableBox = Find<ComboBox>(window, "UnavailableBehavior");
                        Check(unavailableBox.SelectedIndex == 0, "default unavailable behavior is block");
                        if (string.Equals(access.UnavailableBehavior, WebsitePolicyTiming.BehaviorAllow, StringComparison.Ordinal))
                            unavailableBox.SelectedIndex = 1;
                    }
                    Check(!Find<ComboBox>(window, "ScopeKind").IsEnabled && !Find<ComboBox>(window, "ScopeChoice").IsEnabled,
                        "scope switching locked while dirty");
                }
                var preview = Find<Button>(window, "PreviewButton");
                if (access.VerifyWorkspace)
                {
                    var draft = hosts.Text;
                    Click(Find<Button>(window, "ReturnButton"));
                    Check(window.IsEffectivelyVisible && hosts.Text == draft &&
                        Find<TextBlock>(window, "Feedback").Text?.Contains("草稿已保留") == true, "dirty return keeps draft and target");
                    main.Width = 840;
                    main.Height = 480;
                    await Until(() => narrowTabs.IsVisible && editorScroll.IsEffectivelyVisible && !reviewScroll.IsEffectivelyVisible,
                        "narrow editor is the only visible pane");
                    Click(verifyButton);
                    await Until(() => !editorScroll.IsEffectivelyVisible && reviewScroll.IsEffectivelyVisible &&
                        reviewScroll.Bounds.Width >= window.Bounds.Width - 36 &&
                        reviewScroll.TranslatePoint(default, window) is { X: < 20 }, "narrow review replaces editor at full width");
                    Click(editButton);
                    await Until(() => editorScroll.IsEffectivelyVisible && !reviewScroll.IsEffectivelyVisible &&
                        hosts.Text == draft && Find<CheckBox>(window, "Inherit").IsChecked == false, "return to same editor without losing draft");
                    await CaptureWithFrame(main, output, access.AllowClass ? "03-class-editor-840" : "01a-grade-draft-840");
                    main.Width = width;
                    main.Height = height;
                    if (width >= 1000)
                        await Until(() => editorScroll.IsEffectivelyVisible && reviewScroll.IsEffectivelyVisible &&
                            reviewScroll.TranslatePoint(default, window) is { X: > 390 }, "wide editor and review visibly sit alongside");
                }
                await Until(() => preview.IsEffectivelyEnabled, "changed draft ready");
                await ClickRuleControlAsync(main, preview);
                var publish = Find<Button>(window, "PublishButton");
                await Until(() => publish.IsEffectivelyVisible && publish.IsEffectivelyEnabled, "server compared real classes");
                var rooms = Find<ItemsControl>(window, "RoomRows");
                if (access.ExpectedBefore is { } expectedBefore && access.ExpectedAfter is { } expectedAfter)
                {
                    Check(rooms.Items.OfType<WebsitePolicyRoomRow>().Any(row => row.PublishedSummary.Contains(expectedBefore) &&
                        row.PendingSummary.Contains(expectedAfter)),
                        "clear timed class impact before publish");
                }
                else if (access.Timed || access.HostPeriods is { Count: > 0 })
                {
                    var expectedPeriod = access.HostPeriods?.Values.FirstOrDefault(p => p != WebsitePolicyTiming.PeriodAlways) ?? "LESSON|AFTER_SCHOOL";
                    string expectedLabel = WebsitePolicyTiming.PeriodLabel(expectedPeriod);
                    Check(rooms.Items.OfType<WebsitePolicyRoomRow>().Any(row => row.HasPending &&
                        row.PendingSummary.Contains($"（{expectedLabel}）")),
                        "clear timed combination before/after in preview");
                }
                else
                {
                    Check(rooms.Items.OfType<WebsitePolicyRoomRow>().Any(row => access.AllowClass
                        ? row.PublishedSummary.Contains("现在：年级默认 · douyin.com") && row.PendingSummary.Contains("发布后：本班自定 · 无下发域名")
                        : row.PublishedSummary.Contains("现在：未设置") && row.PendingSummary.Contains("发布后：年级默认 · douyin.com")),
                        "clear class impact before publish");
                }
                if (narrowTabs.IsVisible)
                {
                    await Until(() => rooms.GetVisualDescendants().OfType<TextBlock>().Any(row =>
                        row.Text?.Contains("发布后：") == true && Fits(row, reviewScroll)) && Fits(publish, window),
                        "narrow preview displays the actual comparison before publication");
                }
                await CaptureWithFrame(main, output, access.ImageName ?? (access.AllowClass ? "03-class-allow-review-840" : "01-grade-review-1160"));
                await ClickRuleControlAsync(main, publish);
                if (access.DropPublishReply)
                {
                    var original = Find<Button>(window, "ReceiptButton");
                    var pendingFeedback = Find<TextBlock>(window, "Feedback");
                    await Until(() => original.IsEffectivelyVisible && original.IsEffectivelyEnabled, "lost reply offers original receipt");
                    var requestId = System.Text.RegularExpressions.Regex.Match(pendingFeedback.Text ?? "", @"[0-9a-f-]{36}").Value;
                    Check(Guid.TryParse(requestId, out _) && !publish.IsVisible, "valid original operation; no repeat write");
                    Click(Find<Button>(window, "ReturnButton"));
                    await Until(() => entry.IsEffectivelyVisible && main.FocusManager?.GetFocusedElement() == entry, "return restores directory entry focus");
                    window = await OpenAsync();
                    original = Find<Button>(window, "ReceiptButton");
                    await Until(() => original.IsEffectivelyVisible && original.IsEffectivelyEnabled, "same operation is available after reopening");
                    Check(Find<TextBlock>(window, "Feedback").Text?.Contains(requestId) == true &&
                        Find<TextBlock>(window, "WorkingScope").Text?.Contains("班级") == true, "original receipt and scope remain explicit");
                    await CaptureWithFrame(main, output, "04-original-receipt-pending-840");
                    await ClickRuleControlAsync(main, original);
                    await Until(() => Find<TextBlock>(window, "Feedback").Text is { } result &&
                        result.Contains("学校配置已保存") && result.Contains(requestId) &&
                        Find<ItemsControl>(window, "RoomRows").Items.OfType<WebsitePolicyRoomRow>().Any(row =>
                            row.PublishedSummary.Contains("本班自定 · 无下发域名")), "same original receipt and saved class exception");
                    await CaptureWithFrame(main, output, "05-original-receipt-confirmed-840");
                }
                else
                    await Until(() => Find<TextBlock>(window, "Feedback").Text?.Contains("学校配置已保存") == true,
                        "operator's server receipt present");
            }
            Click(Find<Button>(window, "ReturnButton"));
            await Until(() => entry.IsEffectivelyVisible, "returned to device directory in the same window");
            desktop.Shutdown(0);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (main.IsVisible) await CaptureWithFrame(main, output, "failure");
            desktop.Shutdown(1);
        }

        static bool Fits(Visual child, Visual viewport) =>
            child.TranslatePoint(default, viewport) is { Y: >= 0 } &&
            child.TranslatePoint(new(0, child.Bounds.Height), viewport)?.Y <= viewport.Bounds.Height;
    }
}
