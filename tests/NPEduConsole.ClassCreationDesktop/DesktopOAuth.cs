using System.Net.Http.Json;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private sealed record DesktopOAuthAccess(string Origin, string LegacyOrigin, string SchoolId, string OtherId,
        string SchoolCode, string OwnerId, string Pin, bool Isolated);

    private static int StartDesktopOAuth(string config, string output)
    {
        var access = JsonSerializer.Deserialize<DesktopOAuthAccess>(File.ReadAllText(config), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Expected the desktop OAuth fixture configuration.");
        Check(access.Isolated && IsLoopback(access.Origin) && IsLoopback(access.LegacyOrigin), "Only loopback test servers may receive fixtures.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyDesktopOAuthAsync(access, output); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyDesktopOAuthAsync(DesktopOAuthAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var wide = (MainWindow)desktop.MainWindow!;
        var compact = new MainWindow {Width = 840, Height = 480};
        var wideLinks = new List<Uri>();
        var compactLinks = new List<Uri>();
        wide.DesktopBrowserLauncher = uri => {wideLinks.Add(uri); return true;};
        compact.DesktopBrowserLauncher = uri => {compactLinks.Add(uri); return true;};
        using var checkpoints = new HttpClient {BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(35)};
        try
        {
            wide.Width = 1160;
            wide.Height = 780;
            compact.Show();
            Address(wide).Text = access.Origin;
            var notice = Find<TextBlock>(Browser(compact), "DesktopOAuthNotice");
            Address(compact).Text = access.LegacyOrigin + "/no-providers";
            await Until(() => notice.Text?.Contains("未配置")==true, "an empty directory reports unconfigured sign-in");
            Address(compact).Text = access.LegacyOrigin + "/provider-error";
            await Until(() => notice.Text?.Contains("无法读取")==true && Usable(Find<Button>(Browser(compact),"DesktopOAuthRefresh")),
                "discovery failures offer retry instead of reporting no configured provider");
            Address(compact).Text = access.LegacyOrigin + "/many-providers";
            var choices = Find<ComboBox>(Browser(compact), "DesktopOAuthProviders");
            await Until(() => Usable(choices) && choices.ItemCount == 2, "multiple providers are visibly selectable");
            Address(compact).Text = access.Origin;
            await Until(() => Usable(OAuthButton(wide)) && Usable(OAuthButton(compact)) && !choices.IsEffectivelyVisible,
                "each window discovered the same KV provider with a single browser action");
            await Capture(wide, output, "01-wide-login-choice");
            var left = await Start(wide, wideLinks);
            compact.Activate();
            var right = await Start(compact, compactLinks);
            var reopen = Find<Button>(Browser(compact), "DesktopOAuthReopen");
            var cancel = Find<Button>(Browser(compact), "DesktopOAuthCancel");
            await Until(() => Usable(reopen) && Usable(cancel) && compact.ClientSize is {Width: <= 845, Height: <= 485}, "narrow wait actions are available");
            await Capture(compact, output, "02-narrow-pending");
            Click(reopen);
            await Until(() => compactLinks.Count == 2, "reopening invokes the same browser request");
            Check(compactLinks[1] == right.Url && left.Url != right.Url, "browser URLs are stable per window and distinct across windows");
            await Signal("initial-two-windows", new {left = left.Url, leftCode = left.Code, right = right.Url, rightCode = right.Code, rightReopen = compactLinks[1]});
            compact.Activate();
            var esc = new KeyEventArgs {RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape};
            Browser(compact).RaiseEvent(esc);
            await Until(() => Usable(OAuthButton(compact)) && LoginMessage(compact).Contains("已取消"), "Escape cancels only the originating window");
            await Until(() => OAuthButton(compact).IsFocused, "narrow window restores browser-login keyboard focus");
            await OnlySchool(wide, access.OtherId);
            await Signal("cancelled-unclaimed");
            Check(Find<ScrollViewer>(compact,"LoginView").IsEffectivelyVisible, "the second window remains signed out when the first accepts its result");
            await SignOut(wide);

            var fresh = await Start(wide, wideLinks);
            await Signal("fresh-owner", fresh);
            await Until(() => Picker(wide).Items.OfType<School>().Count() == 2, "the newly verified OAuth account can manage two schools");
            Check(Picker(wide).SelectedItem is null && Picker(wide).Items.OfType<School>().Any(s => s.Id == access.SchoolId && s.ManagementRole == "OWNER") &&
                Picker(wide).Items.OfType<School>().Any(s => s.Id == access.OtherId && s.ManagementRole == "ADMIN"), "multi-school picker uses exact roles and requires a choice");
            wide.Activate();
            var schools = Picker(wide);
            schools.IsDropDownOpen = true;
            await Capture(wide, output, "03-wide-multi-school-roles");
            var schoolOptions = schools.Items.OfType<School>().ToArray();
            await Until(() => schoolOptions.All(school => schools.ContainerFromItem(school) is ComboBoxItem control &&
                control.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text ==
                    $"{school.Name} · {(school.ManagementRole == "OWNER" ? "所有者" : "管理员")}")),
                "visible dropdown labels show each school and its role");
            var popup = TopLevel.GetTopLevel(schools.ContainerFromItem(schoolOptions[0])!)!;
            using (var image = new RenderTargetBitmap(new PixelSize((int)popup.Bounds.Width, (int)popup.Bounds.Height)))
            {
                image.Render(popup);
                image.Save(Path.Combine(output, "03b-school-role-picker.png"), new PngBitmapEncoderOptions());
            }
            schools.IsDropDownOpen = false;
            await SelectSchool(wide, access.SchoolId);
            var handoffProof = await OpenAccounts(wide);
            Check(Find<Button>(handoffProof,"ManagementCreate").IsEffectivelyVisible, "OAuth owner can manage school accounts in the first school");
            handoffProof.Close();
            await SelectSchool(wide, access.OtherId);
            var adminDirectory = await OpenAccounts(wide);
            Check(!Find<Button>(adminDirectory,"ManagementCreate").IsEffectivelyVisible, "OAuth administrator does not receive OWNER actions in the second school");
            adminDirectory.Close();

            await LocalSignIn(compact);
            await SelectSchool(compact, access.SchoolId);
            var oldOwner = await OpenAccounts(compact);
            var list = Find<ListBox>(oldOwner,"ManagementRows");
            list.SelectedItem = list.Items.OfType<ManagementAccountRow>().Single(row => row.Account.Id == access.OwnerId);
            if (Usable(Find<Button>(oldOwner,"ManagementOpenDetail"))) Click(Find<Button>(oldOwner,"ManagementOpenDetail"));
            var role = Find<ComboBox>(oldOwner,"ManagementRole");
            role.SelectedItem = role.Items.Cast<object>().Single(row => row.ToString() == "学校管理员");
            Click(Find<Button>(oldOwner,"ManagementRolePreview"));
            var impact = Find<SchoolAccountImpactView>(oldOwner,"ManagementImpact");
            var confirm = Find<Button>(impact,"SchoolAccountImpactConfirm");
            await Until(() => Usable(confirm), "original owner can finish handover after the OAuth login proof");
            await Capture(oldOwner, output, "04-handover-after-oauth-proof");
            Click(confirm);
            await Until(() => Find<TextBlock>(oldOwner,"ManagementScope").Text?.Contains("当前会话为学校管理员")==true, "owner handover saved in actual compact Console");
            await Signal("owner-handoff-saved");
            oldOwner.Close();
            await SignOut(compact);

            var lostClaim = await Start(compact, compactLinks);
            await Signal("claim-reply-lost", lostClaim);
            await Until(() => Usable(OAuthButton(compact)) && LoginMessage(compact).Length != 0,
                "a lost claim response leaves this window signed out");
            var revoked = await Start(compact, compactLinks);
            var pending = Signal("revoke-before-confirmation", revoked);
            await Until(() => Find<TextBlock>(Browser(compact), "DesktopOAuthPendingText").Text?.Contains("正在确认本窗口") == true,
                "Console distinguishes browser completion from active session confirmation");
            await Capture(compact, output, "04b-narrow-session-confirming");
            await Signal("confirming-state-captured", revoked);
            await pending;
            await OnlySchool(compact, access.OtherId);
            await Capture(compact, output, "05-narrow-only-other-school-after-revocation");
            await SignOut(compact);

            var noSchool = await Start(compact, compactLinks);
            await Signal("no-managed-school", noSchool);
            await Until(() => Usable(OAuthButton(compact)) && LoginMessage(compact).Contains("没有可管理的学校"), "unmanaged OAuth identity returns to login and releases its session");
            await Capture(compact, output, "06-no-management-access");

            var stale = await Start(compact, compactLinks);
            Address(compact).Text = access.LegacyOrigin;
            await Until(() => Find<TextBlock>(Browser(compact),"DesktopOAuthNotice").Text?.Contains("没有桌面浏览器登录") == true &&
                Usable(Find<Button>(compact,"LoginButton")), "old KV leaves PIN login usable after switching servers");
            await Signal("changed-server", stale);
            await LocalSignIn(compact);
            await SelectSchool(compact, access.SchoolId);
            await Capture(compact, output, "07-old-kv-local-pin");
            await SignOut(compact);
            Address(compact).Text = access.Origin;
            var closing = await Start(compact, compactLinks);
            compact.Close();
            await Until(() => !compact.IsVisible, "the closing window actually closes after canceling its OAuth request");
            await Signal("closed-window", closing);
            Check(Picker(wide).SelectedItem is School stillSelected && stillSelected.Id == access.OtherId,
                "closing one window does not change the other window's independent session");
            Console.WriteLine("PASS: two Windows Console windows, exact OAuth claims, multi-school roles, owner handoff, and the old-KV local login.");
            desktop.Shutdown(0);
        }
        catch (Exception error)
        {
            foreach (var candidate in new[] {wide, compact}) Find<TextBox>(candidate,"Password").Text = "";
            try { await Capture(desktop.Windows.Last(window => window.IsVisible), output, "failure"); }
            catch (Exception captureError) { Console.Error.WriteLine(captureError.Message); }
            await File.WriteAllTextAsync(Path.Combine(output, "failure.txt"), error.ToString());
            Console.Error.WriteLine(error);
            desktop.Shutdown(1);
        }

        static TextBox Address(Window window) => Find<TextBox>(window, "ServerAddress");
        static ComboBox Picker(Window window) => Find<ComboBox>(window, "SchoolPicker");
        static DesktopOAuthLoginView Browser(Window window) => Find<DesktopOAuthLoginView>(window, "BrowserLogin");
        static Button OAuthButton(Window window) => Find<Button>(Browser(window), "DesktopOAuthLogin");
        static bool Usable(Control control) => control.IsEffectivelyVisible && control.IsEffectivelyEnabled;
        static string LoginMessage(Window window) => Find<TextBlock>(window, "LoginMessage").Text ?? "";

        static async Task<(Uri Url, string Code)> Start(Window window, List<Uri> links)
        {
            await Until(() => Usable(OAuthButton(window)), "browser login button available");
            int previous = links.Count;
            Click(OAuthButton(window));
            await Until(() => links.Count == previous + 1, "Console invokes its browser URL");
            var url = links[^1];
            var code = url.Segments[^2].TrimEnd('/')[..8];
            Check(Find<TextBlock>(Browser(window), "DesktopOAuthPendingText").Text?.Contains(code) == true, "Console shows the corresponding browser code");
            return (url, code);
        }

        async Task Signal(string step, object? data = null)
        {
            if (data is ValueTuple<Uri, string> link) data = new {url = link.Item1, code = link.Item2};
            using var response = await checkpoints.PostAsJsonAsync("/__desktop-oauth/" + step, data ?? new {});
            Check(response.IsSuccessStatusCode, "isolated desktop authentication checkpoint " + step);
        }
        async Task LocalSignIn(Window window)
        {
            var action = Find<Button>(window, "LoginButton");
            await Until(() => Usable(action), "local PIN login ready");
            Find<TextBox>(window,"SchoolCode").Text = access.SchoolCode;
            Find<TextBox>(window,"Username").Text = "admin";
            Find<TextBox>(window,"Password").Text = access.Pin;
            Click(action);
            await Until(() => Picker(window).Items.OfType<School>().Any(s => s.Id == access.SchoolId), "local PIN signs in to the authorized school");
        }
        static async Task SignOut(Window window)
        {
            var action = window.GetVisualDescendants().OfType<Button>().Single(button => Usable(button) && button.Content is "退出登录");
            Click(action);
            await Until(() => Usable(Find<Button>(window,"LoginButton")), "logout returns to a usable login form");
        }
        static async Task OnlySchool(Window window, string id)
        {
            await Until(() => Picker(window).SelectedItem is School school && school.Id == id && Picker(window).ItemCount == 1 &&
                Find<Button>(window,"OpenSchoolOverviewButton").IsEffectivelyEnabled, "one manageable school is selected automatically");
        }
        static async Task SelectSchool(Window window, string id)
        {
            Picker(window).SelectedItem = Picker(window).Items.OfType<School>().Single(school => school.Id == id);
            await Until(() => Usable(Find<Button>(window,"OpenSchoolManagementAccountsButton")), "school workspace is ready");
        }
        async Task<SchoolManagementAccountsWindow> OpenAccounts(Window window)
        {
            Click(Find<Button>(window,"OpenSchoolManagementAccountsButton"));
            await Until(() => desktop.Windows.OfType<SchoolManagementAccountsWindow>().Any(view => view.IsVisible &&
                Find<ListBox>(view,"ManagementRows").ItemCount > 0 && Find<ListBox>(view,"ManagementRows").IsEffectivelyEnabled), "management directory refreshed with the scoped session");
            return desktop.Windows.OfType<SchoolManagementAccountsWindow>().Single(view => view.IsVisible);
        }
    }
}
