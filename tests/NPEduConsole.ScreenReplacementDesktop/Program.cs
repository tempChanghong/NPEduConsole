using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Automation;
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
        using var fixture = new Fixture();
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyAsync(fixture, output); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyAsync(Fixture fixture, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var first = new RequestView(Guid.NewGuid(), "候选教室终端", "1.1.10", DateTimeOffset.UtcNow.AddHours(10),
            fixture.OldDeviceId, "当前教室终端", 3, "PENDING");
        var second = first with { PairingId = Guid.NewGuid(), DeviceName = "另一台候选" };
        Guid newDeviceId = Guid.NewGuid();
        var reported = DateTimeOffset.UtcNow;
        try
        {
            var main = (MainWindow)desktop.MainWindow!;
            var school = Control<ComboBox>(main, "SchoolPicker");
            var classroom = Control<ListBox>(main, "ClassPicker");
            var login = Control<Button>(main, "LoginButton");
            var view = Control<ScreenReplacementView>(main, "ScreenReplacementWorkspace");
            var status = Control<TextBlock>(view, "ReplacementStatus");
            var feedback = Control<TextBlock>(view, "ReplacementFeedback");
            var items = Control<StackPanel>(view, "ReplacementItems");
            var latest = Control<TextBlock>(view, "ReplacementLatestText");
            var refresh = Control<Button>(view, "RefreshReplacementButton");
            var retry = Control<Button>(view, "RetryReplacementButton");
            var confirm = Control<Button>(view, "ConfirmReplacementButton");

            Control<TextBox>(main, "ServerAddress").Text = fixture.Origin;
            Control<TextBox>(main, "SchoolCode").Text = "FIXTURE";
            Control<TextBox>(main, "Username").Text = "schooladmin";
            Control<TextBox>(main, "Password").Text = "fixture-only";
            Click(login);
            await Until(() => school.ItemCount == 2, "login with two authorized schools");
            school.SelectedIndex = 0;
            await Until(() => classroom.ItemCount == 1, "school classroom loaded");
            classroom.SelectedIndex = 0;
            await Until(() => Buttons(main).Count(button => AutomationProperties.GetAutomationId(button)?.StartsWith("OpenScreenReplacement-", StringComparison.Ordinal) == true) == 2,
                "two screen action buttons");
            Check(AutomationProperties.GetName(Button(main, "OpenScreenReplacement-screen-a"))!.Contains("screen-a") &&
                AutomationProperties.GetName(Button(main, "OpenScreenReplacement-screen-b"))!.Contains("screen-b"), "same-name screen entries identify the target before opening review");

            Click(Button(main, "OpenScreenReplacement-screen-a"));
            var open = await fixture.NextAsync();
            Check(open is { School: "school-a", Screen: "screen-a", Method: "GET", Pairing: null }, "review request is scoped to the selected screen");
            open.Reply(List([first, second]));
            await Until(() => items.Children.Count == 2 && refresh.IsEnabled, "two review candidates");
            Check(Control<TextBlock>(view, "ReplacementScope").Text!.Contains("screen-a") &&
                items.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains(first.ExpectedDeviceId.ToString()) == true),
                "identical screen names and the old device remain distinguishable");
            Click(Button(view, $"ScreenReplacementApprove-{first.PairingId:D}"));
            Check(Control<Border>(view, "ReplacementConfirmation").IsVisible && fixture.WriteCount == 0 &&
                Control<TextBlock>(view, "ReplacementReviewText").Text!.Contains("现场确认成功"), "approval requires explicit review of the handover point");
            await Capture(main, output, "01-review");
            Click(confirm);
            var approving = await fixture.NextAsync();
            var requestId = CheckWrite(approving, first, "approve");
            approving.Fail(408);
            var checking = await fixture.NextAsync();
            Check(checking.Pairing == first.PairingId && checking.Method == "GET", "ambiguous response is checked through the same pairing, without a repeat write");
            checking.Reply(Detail(first, "AWAITING_REVIEW"));
            (await fixture.NextAsync()).Reply(List([first, second]));
            await Until(() => retry.IsVisible && refresh.IsEnabled, "read-only resolution allows explicit retry");
            Check(feedback.Text!.Contains("仍待审核") && fixture.WriteCount == 1, "results do not misstate a lost response as approval");
            Click(retry);
            var repeated = await fixture.NextAsync();
            Check(CheckWrite(repeated, first, "approve") == requestId, "explicit retry reuses exactly the same request ID and expected target");
            var approved = first with { State = "APPROVED" };
            repeated.Reply(approved);
            (await fixture.NextAsync()).Reply(List([approved, second]));
            await Until(() => refresh.IsEnabled && items.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("待新机现场确认") == true), "approved but not handed over");
            Check(!retry.IsVisible && !ButtonExists(view, $"ScreenReplacementApprove-{first.PairingId:D}"), "approved rows cannot be reviewed again");
            await Capture(main, output, "02-awaiting-confirmation");

            var activated = first with { State = "ACTIVATED" };
            var later = second with { PairingId = Guid.NewGuid(), ExpectedDeviceId = newDeviceId, CurrentDeviceName = first.DeviceName };
            Click(refresh);
            (await fixture.NextAsync()).Reply(List([later], Detail(activated, "AWAITING_REPORT", newDeviceId)));
            await Until(() => refresh.IsEnabled && latest.Text?.Contains("待新机首报") == true, "handover awaiting first report");
            var retired = Buttons(main).Single(button => AutomationProperties.GetName(button)?.Contains("已停用，请刷新班级") == true);
            Check(!retired.IsEnabled && main.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "旧机已停用 · 刷新班级后查看新机"),
                "authoritative handover retires the old device from the cached classroom overview");
            Click(refresh);
            (await fixture.NextAsync()).Reply(List([later], Detail(activated, "REPORTED", newDeviceId, reported)));
            await Until(() => refresh.IsEnabled && latest.Text?.Contains("新机已上报") == true, "accepted first report");
            Check(latest.Text!.Contains("首报") && latest.Text.Contains(newDeviceId.ToString()), "most recent handover shows actual first receipt and new device");
            Control<Border>(view, "ReplacementLatest").BringIntoView();
            await Capture(main, output, "03-reported");

            Click(Button(view, $"ScreenReplacementReject-{later.PairingId:D}"));
            Click(confirm);
            var rejecting = await fixture.NextAsync();
            CheckWrite(rejecting, later, "reject");
            rejecting.Fail(503);
            var checkRejected = await fixture.NextAsync();
            Check(checkRejected.Pairing == later.PairingId && checkRejected.Method == "GET", "lost rejection response reads the original applicant");
            checkRejected.Reply(Detail(later with { State = "CANCELLED" }, "CANCELLED"));
            (await fixture.NextAsync()).Reply(List([], Detail(activated, "REPORTED", newDeviceId, reported)));
            await Until(() => refresh.IsEnabled && feedback.Text?.Contains("已取消") == true && items.Children.Count == 0, "reconciled cancellation removes candidate and retains latest handover");
            Check(!retry.IsVisible && fixture.WriteCount == 3, "terminal status never resends the lost rejection");

            Click(refresh);
            var obsolete = await fixture.NextAsync();
            Click(Button(main, "OpenScreenReplacement-screen-b"));
            var olderKv = await fixture.NextAsync();
            Check(olderKv.Screen == "screen-b", "second screen gets its own request immediately");
            olderKv.Fail(426);
            obsolete.Reply(List([first]));
            await Until(() => refresh.IsEnabled && status.Text?.Contains("KV 版本") == true, "unsupported service on other screen");
            Check(items.Children.Count == 0 && latest.Text is null && Control<TextBlock>(view, "ReplacementScope").Text!.Contains("screen-b"),
                "late screen result cannot restore another screen's rows or handover");

            Click(refresh);
            (await fixture.NextAsync()).Fail(403);
            await Until(() => refresh.IsEnabled && status.Text?.Contains("没有执行此操作的权限") == true, "role revoked without signing out");
            Check(!login.IsEffectivelyVisible && items.Children.Count == 0, "permission error clears rows while retaining the login");
            Click(refresh);
            var lateSchool = await fixture.NextAsync();
            school.SelectedIndex = 1;
            await Until(() => !view.IsVisible, "switching schools closes review");
            lateSchool.Reply(List([second]));
            Check(items.Children.Count == 0, "leaving scope clears the previous school's application");
            End("PASS: Windows Console screen scope, confirm/approve/reject, ambiguous 408, same-request retry, first report, older KV, role denial and late refresh isolation.", 0);
        }
        catch (Exception error) { End("FAIL: " + error, 1); }

        void End(string message, int code)
        {
            File.WriteAllText(Path.Combine(output, "result.txt"), message);
            Console.WriteLine(message);
            desktop.Shutdown(code);
        }
    }

    private static Guid CheckWrite(Call call, RequestView request, string decision)
    {
        Check(call is { School: "school-a", Screen: "screen-a", Method: "POST" } && call.Pairing == request.PairingId && call.Decision == decision,
            "review must address the exact school, screen, application and decision");
        var body = call.Body;
        Check(body.EnumerateObject().Select(field => field.Name).Order().SequenceEqual(new[] { "expectedBindingRevision", "expectedDeviceId", "requestId" }) &&
            body.GetProperty("expectedDeviceId").GetGuid() == request.ExpectedDeviceId &&
            body.GetProperty("expectedBindingRevision").GetInt64() == request.ExpectedBindingRevision, "review payload contains only the exact guarded expectations");
        var requestId = body.GetProperty("requestId").GetGuid();
        Check(call.CorrelationId == requestId.ToString() && requestId != Guid.Empty, "write has matching body and header correlation");
        return requestId;
    }

    private sealed record RequestView(Guid PairingId, string DeviceName, string AppVersion, DateTimeOffset ExpiresAt,
        Guid ExpectedDeviceId, string CurrentDeviceName, long ExpectedBindingRevision, string State);
    private static object List(RequestView[] items, object? latestHandover = null) => new { items, latestHandover };
    private static object Detail(RequestView row, string progress, Guid? deviceId = null, DateTimeOffset? firstReportedAt = null) => new
    {
        row.PairingId, row.DeviceName, row.AppVersion, row.ExpiresAt, row.ExpectedDeviceId, row.CurrentDeviceName, row.ExpectedBindingRevision, row.State,
        progress, deviceId, firstReportedAt
    };

    private static T Control<T>(Control root, string name) where T : Control => root.FindControl<T>(name) ?? throw new InvalidDataException(name);
    private static IEnumerable<Button> Buttons(Control root) => root.GetVisualDescendants().OfType<Button>();
    private static Button Button(Control root, string id) => Buttons(root).Single(button => AutomationProperties.GetAutomationId(button) == id);
    private static bool ButtonExists(Control root, string id) => Buttons(root).Any(button => AutomationProperties.GetAutomationId(button) == id);
    private static void Click(Button button)
    {
        Check(button.IsEffectivelyEnabled && button.IsEffectivelyVisible, $"available control {button.Name ?? AutomationProperties.GetAutomationId(button)}");
        button.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    }

    private static async Task Capture(Window window, string output, string name)
    {
        await Task.Delay(100);
        var root = (Control)window.Content!;
        using var image = new RenderTargetBitmap(new PixelSize((int)root.Bounds.Width, (int)root.Bounds.Height));
        image.Render(root);
        image.Save(Path.Combine(output, name + ".png"), new PngBitmapEncoderOptions());
    }

    private static async Task Until(Func<bool> condition, string step)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try { while (!condition()) await Task.Delay(25, deadline.Token); }
        catch (OperationCanceledException) { throw new TimeoutException(step); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed record Call(string School, string Screen, string Method, Guid? Pairing, string? Decision, string CorrelationId, JsonElement Body)
    {
        public TaskCompletionSource<IResult> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Reply(object data) => Response.TrySetResult(Protocol("0.16", CorrelationId, data));
        public void Fail(int status) => Response.TrySetResult(Results.StatusCode(status));
    }

    private static IResult Academic(object data) => Results.Json(new { success = true, data });
    private static IResult Protocol(string version, string id, object data) => Results.Json(new { protocolVersion = version,
        requestId = id, serverTime = DateTimeOffset.UtcNow, data });

    private sealed class Fixture : IDisposable
    {
        private const string Prefix = "/gateway";
        private readonly WebApplication _app;
        private readonly Channel<Call> _calls = Channel.CreateUnbounded<Call>();
        private int _writeCount;
        public string Origin { get; }
        public Guid OldDeviceId { get; } = Guid.NewGuid();
        public int WriteCount => Volatile.Read(ref _writeCount);

        public Fixture()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            _app = builder.Build();
            var term = new { id = "term", status = "ACTIVE" };
            _app.MapPost(Prefix + "/accounts/local/login", () => Academic(new { access_token = "fixture-school-token" }));
            _app.MapPost(Prefix + "/accounts/logout", () => Academic(new { }));
            _app.MapGet(Prefix + "/api/v2/me/server-status", () => Results.StatusCode(404));
            _app.MapGet(Prefix + "/api/v2/me/schools", () => Academic(new[] { "school-a", "school-b" }.Select((id, index) =>
                new { role = "ADMIN", school = new { id, name = $"验收学校 {index + 1}", terms = new[] { term } } })));
            string schoolPath = Prefix + "/api/v2/admin/schools/{school}";
            _app.MapGet(schoolPath + "/academic-structure", () => Academic(new
            {
                term, administrativeClasses = new[] { new { id = "class", name = "高二（1）班", isActive = true, grade = new { name = "高二" } } }
            }));
            _app.MapGet(schoolPath + "/classroom-screens", (string school) => Academic(new[] { "screen-a", "screen-b" }.Select(id =>
                new { id, schoolId = school, administrativeClassId = "class", name = "教室大屏", isActive = true, npepBindingRevision = 3 })));
            string npep = Prefix + "/api/v2/npep/schools/{school}";
            var screenDevices = new[] { (Screen: "screen-a", Id: OldDeviceId), (Screen: "screen-b", Id: Guid.NewGuid()) };
            _app.MapGet(npep + "/device-overview", (string school, HttpContext context) => Protocol("0.1", context.Request.Headers["X-Request-Id"].ToString(),
                new
                {
                    items = screenDevices.Select(device => new { deviceId = device.Id.ToString(), schoolId = school, administrativeClassId = "class",
                        screenBindingId = device.Screen, bindingRevision = 3, bindingChanged = false, deviceName = "当前教室终端",
                        state = "ACTIVE", connectivity = "ONLINE", lastSeenAt = DateTimeOffset.UtcNow }),
                    nextCursor = (string?)null
                }));
            string replacements = npep + "/screen-bindings/{screen}/replacements";
            _app.MapGet(replacements, (string school, string screen, HttpContext context) => RespondAsync(school, screen, null, null, null, context));
            _app.MapGet(replacements + "/{pairing:guid}", (string school, string screen, Guid pairing, HttpContext context) =>
                RespondAsync(school, screen, pairing, null, null, context));
            _app.MapPost(replacements + "/{pairing:guid}/{decision}", (string school, string screen, Guid pairing, string decision, JsonElement body, HttpContext context) =>
                RespondAsync(school, screen, pairing, decision, body, context));
            _app.StartAsync().GetAwaiter().GetResult();
            Origin = _app.Urls.Single() + Prefix;
        }

        public async Task<Call> NextAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            return await _calls.Reader.ReadAsync(deadline.Token);
        }

        private async Task<IResult> RespondAsync(string school, string screen, Guid? pairing, string? decision, JsonElement? body, HttpContext context)
        {
            if (context.Request.Headers["X-NPEP-Version"] != "0.16" || context.Request.Headers.Authorization != "Bearer fixture-school-token")
                return Results.StatusCode(401);
            if (body is not null) Interlocked.Increment(ref _writeCount);
            var call = new Call(school, screen, context.Request.Method, pairing, decision, context.Request.Headers["X-Request-Id"].ToString(), body?.Clone() ?? default);
            await _calls.Writer.WriteAsync(call, context.RequestAborted);
            return await call.Response.Task.WaitAsync(context.RequestAborted);
        }

        public void Dispose() => Task.Run(async () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _app.StopAsync(deadline.Token);
            await _app.DisposeAsync();
        }).GetAwaiter().GetResult();
    }
}
