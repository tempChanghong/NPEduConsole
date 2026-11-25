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
        int? WindowWidth = null, int? WindowHeight = null);

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
            await Until(() => schools.Items.OfType<School>().Any(row => row.Id == access.SchoolId), "authenticated school");
            schools.SelectedItem = schools.Items.OfType<School>().Single(row => row.Id == access.SchoolId);
            var directoryButton = Find<Button>(main, "OpenDeviceDirectoryButton");
            await Until(() => directoryButton.IsEffectivelyVisible && directoryButton.IsEnabled, "device directory accessible");
            Click(directoryButton);
            var directory = Find<SchoolDeviceDirectoryView>(main, "DeviceDirectory");
            var entry = directory.FindControl<Button>("DeviceWebsitePolicyButton")!;
            await Until(() => entry.IsEffectivelyVisible && entry.IsEffectivelyEnabled, "website policy entry visible");
            Click(entry);
            WebsitePolicyWindow? opened = null;
            await Until(() => (opened = desktop.Windows.OfType<WebsitePolicyWindow>().SingleOrDefault())?.IsVisible == true, "website rule window open");
            var window = opened!;
            window.Width = access.WindowWidth ?? (access.ReadReceipt ? 840 : 1160);
            window.Height = access.WindowHeight ?? (access.ReadReceipt ? 480 : 780);
            var choices = Find<ComboBox>(window, "ScopeChoice");
            if (access.ReadReceipt || access.AllowClass) Find<ComboBox>(window, "ScopeKind").SelectedIndex = 1;
            string code = access.ReadReceipt || access.AllowClass ? access.ClassCode : access.GradeCode;
            await Until(() => choices.Items.Cast<object>().Any(row => row.ToString()!.EndsWith(" · " + code, StringComparison.Ordinal)), "original target ready");
            choices.SelectedItem = choices.Items.Cast<object>().Single(row => row.ToString()!.EndsWith(" · " + code, StringComparison.Ordinal));
            Click(Find<Button>(window, "ReadScopeButton"));
            var hosts = Find<TextBox>(window, "Hosts");
            if (access.ExpectedScreen is { } expected && access.ExpectedStatus is { } status)
            {
                var screens = Find<ItemsControl>(window, "ScreenRows");
                await Until(() => screens.Items.OfType<string>().Any(row => row.Contains(expected) && row.Contains(status)), "current device evidence visible");
                var scroller = Find<ScrollViewer>(window, "WebsiteDetailsScroll");
                await Until(() => screens.GetVisualDescendants().OfType<TextBlock>().Any(row =>
                    row.Text?.Contains(expected) == true && row.Text.Contains(status) &&
                    row.TranslatePoint(new(0, row.Bounds.Height), scroller)?.Y <= scroller.Bounds.Height - 2),
                    "current device evidence fits the window");
                await CaptureWithFrame(window, output, access.ImageName ?? "current-device-evidence");
            }
            else if (access.ReadReceipt)
            {
                var screens = Find<ItemsControl>(window, "ScreenRows");
                await Until(() => screens.Items.OfType<string>().Any(row => row.Contains("设备已收到")), "actual bound Host acknowledgement visible");
                Check(screens.Items.OfType<string>().Any(row => row.Contains("验收教室前屏") && row.Contains("Edge 生效未确认")), "receipt distinguished from application");
                Check(screens.Items.OfType<string>().Any(row => row.Contains("同班待接入后屏") && row.Contains("未配对原生设备")), "neighboring binding independent");
                var scroller = Find<ScrollViewer>(window, "WebsiteDetailsScroll");
                await Until(() =>
                {
                    var targetRows = screens.GetVisualDescendants().OfType<TextBlock>().Where(row =>
                        row.Text?.Contains("验收教室前屏") == true || row.Text?.Contains("同班待接入后屏") == true).ToArray();
                    return targetRows.Length == 2 && targetRows.All(row =>
                        row.TranslatePoint(new(0, row.Bounds.Height), scroller)?.Y <= scroller.Bounds.Height - 2);
                }, "both target screen states fit the narrow inherited view");
                await CaptureWithFrame(window, output, access.ImageName ?? "02-received-not-applied-840");
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
                await Until(() => preview.IsEffectivelyEnabled, "changed draft ready");
                Click(preview);
                var publish = Find<Button>(window, "PublishButton");
                await Until(() => publish.IsEffectivelyVisible && publish.IsEffectivelyEnabled, "server compared real classes");
                var rooms = Find<ItemsControl>(window, "RoomRows");
                if (access.ExpectedBefore is { } expectedBefore && access.ExpectedAfter is { } expectedAfter)
                {
                    Check(rooms.Items.OfType<string>().Any(row => row.Contains(expectedBefore) && row.Contains(expectedAfter)),
                        "clear timed class impact before publish");
                }
                else if (access.Timed || access.HostPeriods is { Count: > 0 })
                {
                    var expectedPeriod = access.HostPeriods?.Values.FirstOrDefault(p => p != WebsitePolicyTiming.PeriodAlways) ?? "LESSON|AFTER_SCHOOL";
                    string expectedLabel = WebsitePolicyTiming.PeriodLabel(expectedPeriod);
                    Check(rooms.Items.OfType<string>().Any(row => row.Contains("现在：") && row.Contains($"发布后：") && row.Contains($"（{expectedLabel}）")),
                        "clear timed combination before/after in preview");
                }
                else
                {
                    Check(rooms.Items.OfType<string>().Any(row => access.AllowClass
                        ? row.Contains("现在：年级默认 · douyin.com") && row.Contains("发布后：本班自定 · 无下发域名")
                        : row.Contains("现在：未设置") && row.Contains("发布后：年级默认 · douyin.com")),
                        "clear class impact before publish");
                }
                if (window.Width < 940 && access.Timed)
                {
                    var scroller = Find<ScrollViewer>(window, "WebsiteDetailsScroll");
                    await Until(() => rooms.GetVisualDescendants().OfType<TextBlock>().Any(row =>
                        row.Text?.Contains("发布后：") == true && row.TranslatePoint(default, scroller) is { Y: >= 0 } &&
                        row.TranslatePoint(new(0, row.Bounds.Height), scroller)?.Y <= scroller.Bounds.Height),
                        "narrow preview displays the actual comparison before publication");
                }
                await CaptureWithFrame(window, output, access.ImageName ?? (access.AllowClass ? "03-class-allow-review-1160" : "01-grade-review-1160"));
                Click(publish);
                await Until(() => Find<TextBlock>(window, "Feedback").Text?.Contains("学校配置已保存") == true, "operator's server receipt present");
            }
            window.Close();
            desktop.Shutdown(0);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            var current = desktop.Windows.OfType<WebsitePolicyWindow>().FirstOrDefault();
            if (current is not null) await CaptureWithFrame(current, output, "failure");
            desktop.Shutdown(1);
        }
    }
}
