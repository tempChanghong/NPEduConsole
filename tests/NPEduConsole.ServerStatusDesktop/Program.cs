using System.Diagnostics;
using System.Net;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NPEduConsole.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string output = args.Single();
        Directory.CreateDirectory(output);
        using var fixture = new StatusFixture();
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyAsync(fixture, output); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyAsync(StatusFixture fixture, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        try
        {
            var main = (MainWindow)desktop.MainWindow!;
            T MainControl<T>(string name) where T : Control => main.FindControl<T>(name) ?? throw new InvalidDataException(name);
            var entry = MainControl<Button>("OpenSchoolServerStatusButton");
            var legacyEntry = MainControl<Button>("OpenServerStatusButton");
            var summary = MainControl<TextBlock>("SchoolServerStatusSummary");
            var picker = MainControl<ComboBox>("SchoolPicker");
            var login = MainControl<Button>("LoginButton");
            var workspace = MainControl<Grid>("Workspace");
            MainControl<TextBox>("ServerAddress").Text = fixture.Origin;
            Check(legacyEntry.IsEffectivelyVisible && !entry.IsEffectivelyVisible, "Only the utility diagnostic is offered before login.");
            Click(legacyEntry);
            var ops = desktop.Windows.OfType<ServerStatusWindow>().Single();
            var opsToken = Find<TextBox>(ops, "OpsMonitoringToken");
            Check(Find<Border>(ops, "OpsCredentialsCard").IsEffectivelyVisible && opsToken.PasswordChar != '\0', "The independent masked credential is still offered.");
            opsToken.Text = "fixture-monitor-token";
            Click(Find<Button>(ops, "CheckServerStatusButton"));
            var monitor = await fixture.NextAsync();
            Check(monitor.Route == "ops" && monitor.Bearer == "Bearer fixture-monitor-token", "Old operation credentials go only to the existing operations route.");
            monitor.Reply(Snapshot("operations"));
            await Until(() => Find<StackPanel>(ops, "OpsSnapshot").IsVisible, "operations result");
            ops.Close();
            await Until(() => !ops.IsVisible && login.IsEffectivelyEnabled, "login form restored");
            Check(string.IsNullOrEmpty(opsToken.Text), "Closing operations diagnostics clears its credential.");

            await SignIn("first");
            var first = await fixture.NextAsync();
            Check(first.Route == "school" && first.Bearer == "Bearer session-first", "Login automatically checks with the current account's Bearer.");
            Check(picker.SelectedItem is null && entry.IsEffectivelyVisible && summary.Text == "检查中…", "The workspace is available before the first check and before selecting a school.");
            Click(entry);
            var detail = desktop.Windows.OfType<ServerStatusWindow>().Single();
            var refresh = Find<Button>(detail, "RefreshSchoolStatusButton");
            var result = Find<StackPanel>(detail, "OpsSnapshot");
            var error = Find<TextBlock>(detail, "OpsErrorMessage");
            Check(!Find<Border>(detail, "OpsCredentialsCard").IsEffectivelyVisible && Find<TextBlock>(detail, "OpsActivity").IsVisible,
                "The school's detail shows the ongoing check without a credential form.");
            first.Reply(Snapshot("first-check"));
            await Until(() => refresh.IsEnabled && result.IsVisible && summary.Text == "数据库可用", "first school snapshot");
            Check(Find<TextBlock>(detail, "OpsBackupLastResult").Text == "成功", "Account details reuse automatic backup evidence.");
            await Capture(detail, output, "02-account-details");
            detail.Close();
            await Capture(main, output, "01-workspace-status");
            Click(entry);
            detail = desktop.Windows.OfType<ServerStatusWindow>().Single();
            refresh = Find<Button>(detail, "RefreshSchoolStatusButton");
            result = Find<StackPanel>(detail, "OpsSnapshot");
            error = Find<TextBlock>(detail, "OpsErrorMessage");

            await ExpectFailure(404, "当前 KV 不支持", "未提供登录后检查");
            await ExpectFailure(403, "无状态查看权限", "无权查看");
            await ExpectFailure(503, "检查失败", "暂不可用");
            Click(refresh);
            (await fixture.NextAsync()).Reply(Snapshot("outage", database: "UNAVAILABLE"));
            await Until(() => result.IsVisible && summary.Text == "数据库不可用", "database outage from a valid returned snapshot");
            await Capture(detail, output, "04-database-unavailable");
            var cadence = Stopwatch.StartNew();
            await ExpectFailure(401, "暂无法核实登录", "自动重试");
            await Capture(detail, output, "03-cannot-verify-session");
            detail.Close();

            var automatic = await fixture.NextAsync(TimeSpan.FromSeconds(72));
            Check(cadence.Elapsed >= TimeSpan.FromSeconds(55) && automatic.Bearer == "Bearer session-first",
                "The account polls again at the real cadence after 401, even with details closed.");
            automatic.Reply(Snapshot("recovered"));
            await Until(() => summary.Text == "数据库可用", "the same session automatically observes recovery");
            Check(workspace.IsEffectivelyVisible && picker.SelectedItem is null, "A diagnostic result never changes workspace or school selection.");
            Click(entry);
            detail = desktop.Windows.OfType<ServerStatusWindow>().Single();
            refresh = Find<Button>(detail, "RefreshSchoolStatusButton");
            result = Find<StackPanel>(detail, "OpsSnapshot");
            Check(Find<TextBlock>(detail, "OpsVersion").Text == "recovered", "Reopening details shows the recovered snapshot.");
            Click(refresh);
            var previousAccount = await fixture.NextAsync();
            Check(summary.Text == "正在刷新…" && result.IsVisible && Find<TextBlock>(detail, "OpsActivity").Text!.Contains("上次检查"),
                "An in-flight refresh identifies prior information as an older snapshot.");

            var logout = main.GetVisualDescendants().OfType<Button>().Single(button => button.Content is "退出登录");
            Click(logout);
            previousAccount.Reply(Snapshot("obsolete-account"));
            await Until(() => login.IsEffectivelyVisible && login.IsEffectivelyEnabled, "sign out while a diagnostic is in flight");
            Check(!entry.IsEffectivelyVisible && !detail.IsVisible && Find<TextBlock>(detail, "OpsVersion").Text is null,
                "Logout closes diagnostics and removes details from the previous account.");

            await SignIn("second");
            var replacement = await fixture.NextAsync();
            Check(replacement.Bearer == "Bearer session-second" && summary.Text == "检查中…", "The new login begins with no previous account result.");
            replacement.Reply(Snapshot("second-account"));
            await Until(() => summary.Text == "数据库可用", "replacement account completes its own check");
            Click(entry);
            detail = desktop.Windows.OfType<ServerStatusWindow>().Single();
            Check(Find<TextBlock>(detail, "OpsVersion").Text == "second-account", "The new account only sees its own result.");
            Click(logout);
            await Until(() => login.IsEffectivelyVisible && login.IsEffectivelyEnabled, "final sign out");
            End("PASS: Windows workspace automatic status without school selection, shared details, distinct 404/403/503/401, 60-second retry, login isolation, logout and legacy operations credentials.", 0);

            async Task SignIn(string account)
            {
                MainControl<TextBox>("SchoolCode").Text = "FIXTURE";
                MainControl<TextBox>("Username").Text = account;
                MainControl<TextBox>("Password").Text = "fixture-only-password";
                Click(login);
                await Until(() => workspace.IsEffectivelyVisible && picker.ItemCount == 2, "logged in with two schools");
            }

            async Task ExpectFailure(int code, string title, string message)
            {
                Click(refresh);
                var failure = await fixture.NextAsync();
                failure.Fail(code);
                await Until(() => refresh.IsEnabled && summary.Text == title, $"status failure {code}");
                Check(workspace.IsEffectivelyVisible && !result.IsVisible && error.Text!.Contains(message),
                    $"diagnostic {code} hides old data without signing out or exposing raw errors");
            }
        }
        catch (Exception error) { End("FAIL: " + error, 1); }

        void End(string message, int code)
        {
            File.WriteAllText(Path.Combine(output, "result.txt"), message);
            Console.WriteLine(message);
            desktop.Shutdown(code);
        }
    }

    private static object Snapshot(string version, string database = "AVAILABLE") => new
    {
        version, database, checkedAt = "2026-11-20T12:30:00.123Z",
        updateCatalog = new { status = "AVAILABLE", releaseCount = 3 },
        backup = new { status = "AVAILABLE", timeliness = "CURRENT", maxAgeHours = 26, lastSuccessAt = "2026-11-20T11:00:00Z",
            lastAttempt = new { result = "SUCCEEDED", finishedAt = "2026-11-20T11:00:00Z" } }
    };

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static T Find<T>(Window window, string name) where T : Control => window.FindControl<T>(name) ?? throw new InvalidDataException(name);

    private static async Task Capture(Window target, string output, string filename)
    {
        await Task.Delay(130);
        var root = (Control)target.Content!;
        using var image = new RenderTargetBitmap(new PixelSize((int)root.Bounds.Width, (int)root.Bounds.Height));
        image.Render(root);
        image.Save(Path.Combine(output, filename + ".png"), new PngBitmapEncoderOptions());
    }

    private static async Task Until(Func<bool> condition, string step)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try { while (!condition()) await Task.Delay(25, deadline.Token); }
        catch (OperationCanceledException) { throw new TimeoutException(step); }
    }

    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidDataException(message);
    }

    private sealed class StatusFixture : IDisposable
    {
        private readonly WebApplication _app;
        private readonly Channel<StatusCall> _calls = Channel.CreateUnbounded<StatusCall>();
        public string Origin { get; }

        public StatusFixture()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            _app = builder.Build();
            _app.MapPost("/proxy/accounts/local/login", (Login login) => Envelope(new { access_token = "session-" + login.Username }));
            _app.MapPost("/proxy/accounts/logout", () => Envelope(new { }));
            _app.MapGet("/proxy/api/v2/me/schools", () => Envelope(new[]
            {
                new { role = "ADMIN", school = new { id = "school-a", name = "测试学校甲", terms = Array.Empty<object>() } },
                new { role = "OWNER", school = new { id = "school-b", name = "测试学校乙", terms = Array.Empty<object>() } }
            }));
            _app.MapGet("/proxy/api/v2/me/server-status", (Delegate)((HttpContext context) => WaitForReply("school", context)));
            _app.MapGet("/proxy/api/v2/ops/status", (Delegate)((HttpContext context) => WaitForReply("ops", context)));
            _app.StartAsync().GetAwaiter().GetResult();
            Origin = _app.Urls.Single() + "/proxy";
        }

        private async Task<IResult> WaitForReply(string route, HttpContext context)
        {
            var call = new StatusCall(route, context.Request.Headers.Authorization.ToString());
            await _calls.Writer.WriteAsync(call, context.RequestAborted);
            return await call.Response.Task.WaitAsync(context.RequestAborted);
        }

        public async Task<StatusCall> NextAsync(TimeSpan? timeout = null)
        {
            using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(8));
            return await _calls.Reader.ReadAsync(deadline.Token);
        }

        public void Dispose() => Task.Run(async () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _app.StopAsync(deadline.Token);
            await _app.DisposeAsync();
        }).GetAwaiter().GetResult();

        private sealed record Login(string Username);
    }

    private static IResult Envelope(object data) => Results.Json(new { success = true, data });

    private sealed record StatusCall(string Route, string Bearer)
    {
        public TaskCompletionSource<IResult> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Reply(object data) => Response.SetResult(Route == "school" ? Envelope(data) : Results.Json(data));
        public void Fail(int status) => Response.SetResult(Results.StatusCode(status));
    }
}
