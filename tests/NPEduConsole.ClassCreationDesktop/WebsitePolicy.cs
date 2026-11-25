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
    private sealed record WebsiteAccess(string Origin, string SchoolId, string SchoolCode, string Pin,
        string GradeCode, string ClassCode, bool ReadReceipt, bool AllowClass = false,
        string? ExpectedScreen = null, string? ExpectedStatus = null, string? ImageName = null);

    private static int StartWebsitePolicy(string path, string output)
    {
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
            window.Width = access.ReadReceipt ? 840 : 1160;
            window.Height = access.ReadReceipt ? 480 : 780;
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
                await CaptureWithFrame(window, output, "02-received-not-applied-840");
            }
            else
            {
                await Until(() => Find<CheckBox>(window, "Inherit").IsEffectivelyVisible &&
                    Find<CheckBox>(window, "Inherit").IsEffectivelyEnabled, "loaded actual grade");
                Find<CheckBox>(window, "Inherit").IsChecked = false;
                hosts.Text = access.AllowClass ? "" : "douyin.com";
                var preview = Find<Button>(window, "PreviewButton");
                await Until(() => preview.IsEffectivelyEnabled, "changed draft ready");
                Click(preview);
                var publish = Find<Button>(window, "PublishButton");
                await Until(() => publish.IsEffectivelyVisible && publish.IsEffectivelyEnabled, "server compared real classes");
                var rooms = Find<ItemsControl>(window, "RoomRows");
                Check(rooms.Items.OfType<string>().Any(row => access.AllowClass
                    ? row.Contains("现在：年级默认 · douyin.com") && row.Contains("发布后：本班自定 · 无下发域名")
                    : row.Contains("现在：未设置") && row.Contains("发布后：年级默认 · douyin.com")),
                    "clear class impact before publish");
                await CaptureWithFrame(window, output, access.AllowClass ? "03-class-allow-review-1160" : "01-grade-review-1160");
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
