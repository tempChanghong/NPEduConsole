using System.Net;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private const string SyntheticRecoveryKey = "isolated-window-fixture";
    private const string SyntheticRecoveryPin = "314258";

    private static int StartOwnerRecovery(string output)
    {
        Directory.CreateDirectory(output);
        using var slow = new OwnerRecoveryFixture(slowDirectory: true);
        using var current = new OwnerRecoveryFixture(slowDirectory: false);
        var preference = Path.Combine(output, Guid.NewGuid().ToString("N"), "last-server.txt");
        return AppBuilder.Configure(() => new ConsoleApplication { LastServerFile = preference }).UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyOwnerRecoveryAsync(slow, current, output, preference); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyOwnerRecoveryAsync(OwnerRecoveryFixture slow, OwnerRecoveryFixture current, string output, string preference)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        try
        {
            main.Width = 1160;
            main.Height = 780;
            var address = Find<TextBox>(main, "ServerAddress");
            var loginSchool = Find<TextBox>(main, "SchoolCode");
            var loginName = Find<TextBox>(main, "Username");
            var loginPin = Find<TextBox>(main, "Password");
            var entry = Find<Button>(main, "OpenOwnerRecoveryButton");
            address.Text = slow.Origin;
            loginSchool.Text = "NO-MATCH";
            loginName.Text = "old-scope";
            loginPin.Text = "unused-login-field";
            Click(entry);
            var view = Find<OwnerRecoveryView>(main, "OwnerRecovery");
            var recoveryAddress = Find<TextBox>(view, "OwnerRecoveryAddress");
            var read = Find<Button>(view, "OwnerRecoveryCheckServer");
            var picker = Find<ComboBox>(view, "OwnerRecoverySchool");
            var confirm = Find<Button>(view, "OwnerRecoveryConfirmTarget");
            var username = Find<TextBox>(view, "OwnerRecoveryUsername");
            var credentials = Find<StackPanel>(view, "OwnerRecoveryCredentials");
            var key = Find<TextBox>(view, "OwnerRecoverySetupKey");
            var pin = Find<TextBox>(view, "OwnerRecoveryNewPin");
            var repeat = Find<TextBox>(view, "OwnerRecoveryRepeatPin");
            var save = Find<Button>(view, "OwnerRecoverySave");
            var back = Find<Button>(view, "OwnerRecoveryBack");
            var editTarget = Find<Button>(view, "OwnerRecoveryChangeTarget");
            var login = Find<Button>(view, "OwnerRecoveryReturnToLogin");
            var notice = Find<TextBlock>(view, "OwnerRecoveryNotice");
            Check(TopLevel.GetTopLevel(view) == main && Blank(loginPin), "the normal login opens a same-window recovery and clears its password");
            await Until(() => recoveryAddress.IsFocused, "recovery focuses the address");
            Click(read);
            await slow.DirectoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Check(back.IsEffectivelyEnabled && !read.IsEffectivelyEnabled && recoveryAddress.IsEffectivelyEnabled,
                "a pending directory read permits changing the address or leaving");
            recoveryAddress.Text = current.Origin;
            await Until(() => read.IsEffectivelyEnabled, "a target edit cancels the original directory request");
            Click(read);
            await Until(() => picker.IsEffectivelyVisible && picker.ItemCount == 2, "the later server's two schools are loaded");
            await slow.DirectoryCancelled.Task.WaitAsync(TimeSpan.FromSeconds(8));
            slow.ReleaseDirectory.TrySetResult();
            await slow.DirectoryFinished.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Check(picker.Items.Cast<OwnerRecoverySchool>().All(school => school.Id is "one" or "two") &&
                Find<TextBlock>(view, "OwnerRecoveryVerifiedAddress").Text?.Contains(current.Origin) == true,
                "late results cannot change the selected server or available schools");
            Check(picker.SelectedItem is null && !confirm.IsEffectivelyEnabled && !credentials.IsEffectivelyVisible && !save.IsEffectivelyVisible,
                "no exact match means no automatic first school or credential controls");
            await CaptureWithFrame(main, output, "01-wide-choose-school");
            picker.SelectedItem = picker.Items.Cast<OwnerRecoverySchool>().Single(school => school.Id == "two");
            Check(!credentials.IsEffectivelyVisible && confirm.IsEffectivelyEnabled, "selecting a school still requires explicit target confirmation");
            Click(confirm);
            await Until(() => username.IsFocused, "selecting a different school does not carry the old username");
            Check(Blank(username) && Find<TextBlock>(view, "OwnerRecoveryVerifiedSchool").Text?.Contains("SCHOOL2") == true,
                "the server-verified school is visible above the credentials");
            main.Width = 840;
            main.Height = 480;
            await Until(() => main.ClientSize is { Width: <= 845, Height: <= 485 }, "the real narrow window is in use");
            Check(FitsBatchWindow(main, save) && FitsBatchWindow(main, back) && FitsBatchWindow(main, repeat), "narrow credential fields and fixed actions fit");
            Check(new[] { key, pin, repeat }.All(field => field.PasswordChar != '\0' && !field.IsUndoEnabled), "all secret inputs are masked and have no undo history");
            await CaptureWithFrame(main, output, "02-narrow-confirmed-target");

            SetFields("owner-short", "685291");
            Click(save);
            Check(Find<FormField>(view, "OwnerRecoveryRepeatPinField").ErrorMessage?.Contains("不一致") == true && current.WriteCount == 0,
                "a mismatched PIN stays on the form without sending a write");
            Click(editTarget);
            Check(Blank(key) && Blank(pin) && Blank(repeat) && !credentials.IsEffectivelyVisible, "changing the verified target clears every secret before showing school selection");
            Click(confirm);
            SetFields("owner-short");
            Click(back);
            await Until(() => entry.IsFocused && !view.IsEffectivelyVisible, "cancel returns focus to the original login entry");
            Check(address.Text == slow.Origin && loginSchool.Text == "NO-MATCH" && loginName.Text == "old-scope" &&
                Blank(loginPin) && Blank(key) && Blank(pin) && Blank(repeat),
                "cancel leaves the original non-secret login fields in place");

            address.Text = current.Origin;
            loginSchool.Text = "school2";
            loginName.Text = "owner-short";
            Click(entry);
            Click(read);
            await Until(() => picker.SelectedItem is OwnerRecoverySchool { Id: "two" } && confirm.IsEffectivelyEnabled,
                "only an exact matching school code is preselected");
            Check(!credentials.IsEffectivelyVisible, "preselection cannot unlock credential inputs");
            Click(confirm);
            await Until(() => key.IsFocused && username.Text == "owner-short", "the matching known short account is prefilled and focus goes to the key");
            await Reject(401, "INVALID_RECOVERY_KEY", "恢复密钥不正确");
            await Reject(404, "LOCAL_OWNER_NOT_FOUND", "未匹配到本地学校所有者");
            await Reject(429, "LOCAL_AUTH_NETWORK_RATE_LIMITED", "请求过于频繁");
            await Reject(404, null, "未提供恢复接口");

            SetFields("owner-short");
            Click(save);
            var accepted = await current.NextWrite();
            VerifyWrite(accepted);
            Check(Blank(key) && Blank(pin) && Blank(repeat) && !save.IsEffectivelyEnabled && !back.IsEffectivelyEnabled,
                "the actual POST protects navigation and immediately clears the masked controls");
            main.Close();
            Check(main.IsVisible, "the close button cannot abandon the active write");
            Click(save);
            Check(current.WriteCount == 5 && slow.WriteCount == 0, "repeat clicks and earlier targets do not send extra recovery writes");
            accepted.Reply.SetResult(Results.Json(new { success = true, message = "recovered" }));
            await Until(() => login.IsEffectivelyVisible && login.IsFocused && notice.Text?.Contains("服务器已确认恢复") == true,
                "success is shown from the confirmed response and focuses return to login");
            Check(!save.IsEffectivelyVisible && !credentials.IsEffectivelyVisible && FitsBatchWindow(main, login), "the result offers return only and the fixed action fits at 840 by 480");
            await Until(ResultStaysTogether, "the narrow result and return action sit immediately below the verified target");
            await CaptureWithFrame(main, output, "03-narrow-recovery-confirmed");
            Click(login);
            await Until(() => loginPin.IsFocused && !view.IsEffectivelyVisible, "return focuses the normal PIN field");
            Check(address.Text == current.Origin && loginSchool.Text == "SCHOOL2" && loginName.Text == "owner-short" && Blank(loginPin),
                "normal login receives only the checked server, school code and account without auto-login or a carried PIN");

            Click(entry);
            Click(read);
            await Until(() => confirm.IsEffectivelyEnabled, "the same target can be checked in a fresh explicit session");
            Click(confirm);
            SetFields("owner-short");
            Click(save);
            var uncertain = await current.NextWrite();
            VerifyWrite(uncertain);
            uncertain.Reply.SetResult(Results.Text("<html>gateway reply</html>", "text/html"));
            await Until(() => login.IsEffectivelyVisible && notice.Text?.Contains("恢复结果暂未确认") == true,
                "an unrecognized response offers return for a real login check");
            Check(current.WriteCount == 6 && !save.IsEffectivelyVisible, "uncertain recovery keeps the original target and never resends");
            await Until(ResultStaysTogether, "the narrow unconfirmed result stays with the original target");
            await CaptureWithFrame(main, output, "04-narrow-recovery-unconfirmed");
            main.Width = 1160;
            main.Height = 780;
            await Until(() => main.ClientSize is { Width: >= 1155, Height: >= 775 } && ResultStaysTogether(),
                "wide unconfirmed result stays below the target and both remain above the fold");
            await CaptureWithFrame(main, output, "05-wide-recovery-unconfirmed");
            Click(login);
            await Until(() => loginPin.IsFocused, "uncertain return restores focus for manual login");
            Check(Blank(loginPin) && loginSchool.Text == "SCHOOL2" && loginName.Text == "owner-short", "uncertain return carries the same non-secret target only");
            Check(!File.Exists(preference) && current.LoginCount == 0 && slow.LoginCount == 0,
                "recovery alone stores no connection preference and never attempts login");
            desktop.Shutdown(0);
            Console.WriteLine("PASS: same-window OWNER recovery, two servers and stale directory, explicit school confirmation, narrow geometry, clear secrets, controlled rejections, protected single POST, confirmed and uncertain returns.");

            static bool Blank(TextBox field) => string.IsNullOrEmpty(field.Text);
            bool ResultStaysTogether()
            {
                var account = Find<TextBlock>(view, "OwnerRecoveryVerifiedAccount");
                return account.TranslatePoint(new Point(0, account.Bounds.Height), main) is { } from &&
                    notice.TranslatePoint(default, main) is { } currentNotice && login.TranslatePoint(default, main) is { } action &&
                    currentNotice.Y - from.Y is > 0 and < 48 && action.Y - currentNotice.Y is > 0 and < 48 && FitsBatchWindow(main, login);
            }
            void SetFields(string shortName, string repeated = SyntheticRecoveryPin)
            {
                username.Text = shortName;
                key.Text = SyntheticRecoveryKey;
                pin.Text = SyntheticRecoveryPin;
                repeat.Text = repeated;
            }
            static void VerifyWrite(RecoveryCall call) => Check(call is { SchoolCode: "SCHOOL2", Username: "owner-short", ScopedFixtureSecret: true, Authorized: false },
                "the request uses the original selected school and username and has no account authorization header");
            async Task Reject(int status, string? code, string message)
            {
                SetFields("owner-short");
                Click(save);
                var call = await current.NextWrite();
                VerifyWrite(call);
                call.Reply.SetResult(code is null ? Results.StatusCode(status) : Results.Json(new { success = false, code }, statusCode: status));
                await Until(() => notice.Text?.Contains(message) == true && save.IsEffectivelyEnabled, "controlled recovery response " + status);
                Check(!login.IsEffectivelyVisible && Blank(key) && Blank(pin) && Blank(repeat) && username.Text == "owner-short", "definite errors keep the account but clear secret inputs");
            }
        }
        catch (Exception error)
        {
            await CaptureWithFrame(main, output, "failure");
            Console.Error.WriteLine(error);
            desktop.Shutdown(1);
        }
    }

    private sealed record RecoveryBody(string SchoolCode, string Username, string SetupKey, string NewPin);

    private sealed record RecoveryCall(string SchoolCode, string Username, bool ScopedFixtureSecret, bool Authorized)
    {
        public TaskCompletionSource<IResult> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class OwnerRecoveryFixture : IDisposable
    {
        private readonly WebApplication _app;
        private readonly Channel<RecoveryCall> _writes = Channel.CreateUnbounded<RecoveryCall>();
        private int _writeCount, _loginCount;
        public string Origin { get; }
        public int WriteCount => Volatile.Read(ref _writeCount);
        public int LoginCount => Volatile.Read(ref _loginCount);
        public TaskCompletionSource DirectoryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DirectoryCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DirectoryFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDirectory { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public OwnerRecoveryFixture(bool slowDirectory)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            _app = builder.Build();
            _app.MapGet("/accounts/oauth/desktop/providers", () => Envelope(Array.Empty<object>()));
            _app.MapPost("/accounts/local/login", () => { Interlocked.Increment(ref _loginCount); return Results.Unauthorized(); });
            _app.MapGet("/api/v2/catalog/schools", (Func<HttpContext, Task<IResult>>)(async context =>
            {
                if (!slowDirectory) return Envelope(new[] { new OwnerRecoverySchool("one", "SCHOOL1", "甲校"), new("two", "SCHOOL2", "乙校") });
                using var cancelled = context.RequestAborted.Register(() => DirectoryCancelled.TrySetResult());
                DirectoryStarted.TrySetResult();
                await ReleaseDirectory.Task;
                DirectoryFinished.TrySetResult();
                return Envelope(new[] { new OwnerRecoverySchool("stale", "STALE", "原服务器学校") });
            }));
            _app.MapPost("/accounts/local/recover-owner", async (RecoveryBody body, HttpContext context) =>
            {
                Interlocked.Increment(ref _writeCount);
                var call = new RecoveryCall(body.SchoolCode, body.Username, body.SetupKey == SyntheticRecoveryKey && body.NewPin == SyntheticRecoveryPin,
                    context.Request.Headers.ContainsKey("Authorization"));
                await _writes.Writer.WriteAsync(call, context.RequestAborted);
                return await call.Reply.Task.WaitAsync(context.RequestAborted);
            });
            _app.StartAsync().GetAwaiter().GetResult();
            Origin = _app.Urls.Single();
        }

        public async Task<RecoveryCall> NextWrite()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            return await _writes.Reader.ReadAsync(deadline.Token);
        }

        public void Dispose()
        {
            ReleaseDirectory.TrySetResult();
            Task.Run(async () =>
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _app.StopAsync(deadline.Token);
                await _app.DisposeAsync();
            }).GetAwaiter().GetResult();
        }
    }
}
