using System.Net;
using System.Text.Json;
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
using NPEduConsole.Core;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var directoryRecovery = args is ["--directory-recovery", _];
        if (!directoryRecovery && args is not ([_] or ["--live" or "--live-lost", _, _])) throw new ArgumentException("Specify an output directory, --directory-recovery <directory> or --live/--live-lost <fixture.json> <directory>.");
        using var fixture = args.Length == 1 || directoryRecovery ? new Fixture() : null;
        var access = fixture?.Access ?? JsonSerializer.Deserialize<Access>(File.ReadAllText(args[1]), JsonSerializerOptions.Web)!;
        bool lost = args[0] == "--live-lost";
        string origin = lost ? access.LostReplyOrigin : access.Origin;
        Check(access.AllowFirstScreenTestWrites && new Uri(origin).IsLoopback, "Use an isolated loopback server explicitly authorized for test writes.");
        string output = args[^1];
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyAsync(access, origin, fixture, lost, output, directoryRecovery); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyAsync(Access access, string origin, Fixture? fixture, bool lost, string output, bool directoryRecovery)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        try
        {
            var main = (MainWindow)desktop.MainWindow!;
            T Find<T>(string name) where T : Control => main.FindControl<T>(name) ?? throw new InvalidDataException(name);
            var schools = Find<ComboBox>("SchoolPicker");
            var classes = Find<ListBox>("ClassPicker");
            var prepare = Find<Button>("PrepareFirstScreenButton");
            var refresh = Find<Button>("RefreshFirstScreenButton");
            var admission = Find<Border>("ClassAdmissionCard");
            var login = Find<Button>("LoginButton");
            string Feedback() => Find<TextBlock>("FirstScreenFeedback").Text ?? "";
            string AdmissionStatus() => Find<TextBlock>("AdmissionStatus").Text ?? "";
            Find<TextBox>("ServerAddress").Text = origin;
            Find<TextBox>("SchoolCode").Text = access.SchoolCode;
            Find<TextBox>("Username").Text = access.Username;
            if (directoryRecovery)
            {
                var server = fixture!;
                var message = Find<TextBlock>("LoginMessage");
                foreach (var fault in new[] { "charset", "membership" })
                {
                    server.DirectoryFault = fault;
                    Find<TextBox>("Password").Text = access.Password;
                    Click(login);
                    await Until(() => login.IsEffectivelyEnabled && !string.IsNullOrWhiteSpace(message.Text),
                        $"invalid {fault} is recoverable at the login page");
                    Check(schools.ItemCount == 0, "incomplete memberships cannot grant a school");
                }
                server.DirectoryFault = "utf8";
                Find<TextBox>("Password").Text = access.Password;
                Click(login);
                await Until(() => schools.ItemCount == 2, "retry with a UTF-8 alias signs in");
                SelectSchool(access.SchoolId);
                await Until(() => classes.ItemCount > 0, "school directory initially loads");
                Click(Find<Button>("OpenClassWorkspaceButton"));
                var read = Find<Button>("RefreshClassDirectoryButton");
                await Until(() => read.IsEffectivelyVisible && read.IsEffectivelyEnabled, "directory refresh reachable");
                foreach (var fault in new[] { "screens", "classes", "devices", "terms" })
                {
                    server.DirectoryFault = fault;
                    Click(read);
                    await Until(() => Find<TextBlock>("ClassListMessage").Text?.Contains("读取失败") == true, $"invalid {fault} gives a directory error");
                    Check(classes.ItemCount == 0 && schools.SelectedItem is School { Id: "school" }, "retain authorized school, clear incomplete list");
                    server.DirectoryFault = "";
                    Click(read);
                    await Until(() => classes.ItemCount > 0, "refresh recovers in the same session");
                }
                await SignOut();
                End("PASS: login and class directory recover from incompatible responses, UTF-8 alias, same-session retry and logout.", 0);
                return;
            }
            Find<TextBox>("Password").Text = access.Password;
            Click(login);
            if (fixture is not null)
            {
                await Until(() => schools.ItemCount == 2, "two managed fixture schools");
                SelectSchool(access.SchoolId);
            }
            await Until(() => classes.Items.Cast<Classroom>().Any(room => room.Id == access.InactiveClassId), "school loaded and selected");
            Check(!prepare.IsEffectivelyVisible, "Before class selection there is no first-screen action.");
            Select(access.InactiveClassId);
            Check(!prepare.IsEffectivelyVisible && Find<TextBlock>("WorkspaceMessage").Text!.Contains("停用"), "Inactive records block the first-screen entry.");
            Select(access.EmptyClassId);
            Check(prepare.IsEffectivelyVisible && prepare.IsEffectivelyEnabled, "A genuinely empty classroom offers the action.");
            await Capture(main, output, "01-empty-class");
            Click(prepare);

            if (fixture is null)
            {
                if (lost)
                {
                    await Until(() => Feedback().Contains("未确认") && refresh.IsEffectivelyEnabled, "lost receipt has an explicit recovery state");
                    await Capture(main, output, "02-unconfirmed");
                    Click(refresh);
                    await Until(() => Feedback().Contains("核对本班现有大屏") && VisibleAdmissionOpeners().Length == 1, "refresh reads the registered screen without opening it");
                    Check(!admission.IsEffectivelyVisible, "A read after a lost receipt must not open a screen of uncertain identity.");
                    await Capture(main, output, "03-verified-list-manual-selection");
                    Click(VisibleAdmissionOpeners().Single());
                }

                await Until(() => admission.IsEffectivelyVisible && AdmissionStatus().Contains("尚未设置"), "existing admission editor reads the new screen's disabled settings");
                Check(Find<TextBlock>("AdmissionScope").Text!.Contains("教室大屏") && !Find<TextBlock>("AdmissionStatus").Text!.Contains("已开放") &&
                    string.IsNullOrEmpty(Find<TextBox>("AdmissionSecret").Text), "Registration alone has no admission grant or preset secret.");
                await Capture(main, output, "04-admission-settings");
                await SignOut();
                End(lost ? "PASS: Windows and real KV/PostgreSQL, swallowed reply, read-only refresh, manual screen selection, and closed admission."
                    : "PASS: Windows and real KV/PostgreSQL, empty class, inactive records, exact confirmed screen handoff, and closed admission.", 0);
                return;
            }

            var first = await fixture.NextWrite();
            Check(first.ClassId == access.EmptyClassId && first.TermId == "term" && first.RequestId != Guid.Empty && first.HeaderId == first.RequestId.ToString(), "Correct NPEP operation scope and idempotency key.");
            Click(prepare);
            Check(fixture.WriteCount == 1, "A pending write cannot be submitted twice.");
            fixture.SetScreens(first.ClassId, "decoy-screen", "confirmed-screen");
            fixture.FailNextList = true;
            first.Confirm("confirmed-screen");
            await Until(() => Feedback().Contains("列表刷新失败") && refresh.IsEffectivelyEnabled, "confirmed registration with a failed class reload");
            Check(fixture.LastAdmissionScreen is null && !admission.IsEffectivelyVisible, "A failed read does not start admission.");
            await Capture(main, output, "02-confirmed-refresh-failed");
            Click(refresh);
            await Until(() => fixture.LastAdmissionScreen == "confirmed-screen" && AdmissionStatus().Contains("尚未设置"), "recovery opens the exact confirmed screen despite a same-name decoy");
            Check(fixture.AdmissionWrites == 0 && fixture.WriteCount == 1, "Opening admission settings never grants access or repeats registration.");
            await Capture(main, output, "03-confirmed-admission");

            Select("uncertain");
            Click(prepare);
            var uncertain = await fixture.NextWrite();
            fixture.SetScreens("uncertain", "unknown-screen");
            uncertain.Fail(408);
            await Until(() => Feedback().Contains("未确认") && refresh.IsEffectivelyEnabled, "HTTP 408 is treated as unconfirmed");
            Click(refresh);
            await Until(() => Feedback().Contains("核对本班现有大屏"), "unconfirmed refresh offers manual review");
            Check(fixture.LastAdmissionScreen == "confirmed-screen" && !admission.IsEffectivelyVisible && VisibleAdmissionOpeners().Length == 1,
                "Refreshing an unconfirmed write leaves admission closed until the operator selects a screen.");

            Select("review");
            Click(prepare);
            var conflict = await fixture.NextWrite();
            fixture.SetScreens("review", "existing-screen");
            conflict.Fail(409, "FIRST_SCREEN_EXISTS");
            await Until(() => Feedback().Contains("已有大屏记录") && refresh.IsEffectivelyEnabled, "occupancy conflict prompts a recheck");
            Click(refresh);
            await Until(() => Feedback().Contains("核对本班现有大屏"), "conflict refresh reads the current screen");
            Check(!admission.IsEffectivelyVisible, "Conflict never opens the occupying screen.");

            Select("retry");
            Click(prepare);
            var timedOut = await fixture.NextWrite();
            timedOut.Fail(503);
            await Until(() => Feedback().Contains("未确认") && refresh.IsEffectivelyEnabled, "503 is unconfirmed");
            Click(refresh);
            await Until(() => prepare.IsEffectivelyVisible && prepare.Content is "重试准备", "only a checked empty class offers retry");
            Click(prepare);
            var retry = await fixture.NextWrite();
            Check(retry.ClassId == timedOut.ClassId && retry.RequestId == timedOut.RequestId, "Retry preserves the original request ID.");
            Select(access.InactiveClassId);
            retry.Confirm("late-screen");
            await Task.Delay(120);
            Check(!Find<Border>("FirstScreenCard").IsEffectivelyVisible && !admission.IsEffectivelyVisible, "Changing classes cancels scope and ignores the late receipt.");

            Select("retry");
            Click(prepare);
            var oldSchool = await fixture.NextWrite();
            SelectSchool("other");
            oldSchool.Confirm("late-school-screen");
            await Until(() => Find<TextBlock>("ClassListMessage").Text == "该学校暂无可查看班级。", "other school finishes loading");
            Check(!Find<Border>("FirstScreenCard").IsEffectivelyVisible && !admission.IsEffectivelyVisible, "A different school does not inherit a pending registration.");
            SelectSchool(access.SchoolId);
            await Until(() => classes.ItemCount > 0, "return to the original school");

            Select("retry");
            Click(prepare);
            var unsupported = await fixture.NextWrite();
            unsupported.Fail(426);
            await Until(() => Feedback().Contains("不支持") && !prepare.IsEffectivelyVisible, "old KV does not offer an immediate repeat");
            fixture.OverviewUnavailable = true;
            Click(Find<Button>("RefreshButton"));
            await Until(() => Find<TextBlock>("WorkspaceMessage").Text!.Contains("暂无可用大屏"), "device overview unavailable");
            Check(!Find<Border>("FirstScreenCard").IsEffectivelyVisible, "An unavailable overview is not evidence of eligibility.");
            fixture.OverviewUnavailable = false;
            Click(Find<Button>("RefreshButton"));
            await Until(() => prepare.IsEffectivelyVisible, "eligibility verified again");

            Click(prepare);
            var oldAccount = await fixture.NextWrite();
            await SignOut();
            oldAccount.Confirm("old-account-screen");
            await Task.Delay(120);
            Check(!Find<Border>("FirstScreenCard").IsEffectivelyVisible && fixture.LastAdmissionScreen == "confirmed-screen", "A late result after sign-out cannot start admission.");
            End("PASS: Windows desktop, eligible/disabled/unsupported entry, exact confirmed ID, failed-refresh recovery, unconfirmed/409 manual review, fixed retry ID, class/school switches and logout.", 0);

            void Select(string id) => classes.SelectedItem = classes.Items.Cast<Classroom>().Single(room => room.Id == id);
            void SelectSchool(string id) => schools.SelectedItem = schools.Items.Cast<School>().Single(school => school.Id == id);

            Button[] VisibleAdmissionOpeners() => Find<StackPanel>("ScreenCards").GetVisualDescendants().OfType<Button>()
                .Where(button => button.Content is "配置口令接入" && button.IsEffectivelyVisible).ToArray();

            async Task SignOut()
            {
                Click(main.GetVisualDescendants().OfType<Button>().Single(button => button.Content is "退出登录"));
                await Until(() => login.IsEffectivelyVisible && login.IsEffectivelyEnabled, "sign-out");
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

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task Capture(Window target, string output, string name)
    {
        await Task.Delay(120);
        var root = (Control)target.Content!;
        using var image = new RenderTargetBitmap(new PixelSize((int)root.Bounds.Width, (int)root.Bounds.Height));
        image.Render(root);
        image.Save(Path.Combine(output, name + ".png"), new PngBitmapEncoderOptions());
    }

    private static async Task Until(Func<bool> condition, string step)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try { while (!condition()) await Task.Delay(25, deadline.Token); }
        catch (OperationCanceledException) { throw new TimeoutException(step); }
    }

    private static void Check(bool success, string description)
    {
        if (!success) throw new InvalidDataException(description);
    }

    private sealed record Access(string Origin, string LostReplyOrigin, string SchoolCode, string Username, string Password,
        string SchoolId, string EmptyClassId, string InactiveClassId, bool AllowFirstScreenTestWrites);
    private sealed record WriteBody(Guid RequestId, string TermId);
    private sealed record ScreenRow(string Id, string AdministrativeClassId, bool IsActive, string Name = "教室大屏",
        string SchoolId = "school", long NpepBindingRevision = 1);

    private sealed record Write(string ClassId, string TermId, Guid RequestId, string HeaderId)
    {
        public TaskCompletionSource<IResult> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Confirm(string id) => Response.SetResult(Protocol("0.16", HeaderId, new { schoolId = "school", TermId, ClassId, screenBindingId = id }));
        public void Fail(int status, string? code = null) => Response.SetResult(Protocol("0.16", HeaderId, null, status, code));
    }

    private static IResult Academic(object data) => Results.Json(new { success = true, data });

    private static IResult Protocol(string protocolVersion, string requestId, object? data, int status = 200, string? code = null) =>
        Results.Json(new { protocolVersion, requestId, serverTime = DateTimeOffset.UtcNow, error = code is null ? null : new { code }, data }, statusCode: status);

    private sealed class Fixture : IDisposable
    {
        private readonly WebApplication _app;
        private readonly Channel<Write> _writes = Channel.CreateUnbounded<Write>();
        private readonly object _screenLock = new();
        private readonly List<ScreenRow> _screens = [new("inactive-screen", "inactive", false)];
        public Access Access { get; }
        public bool FailNextList { get; set; }
        public volatile string DirectoryFault = "";
        public bool OverviewUnavailable { get; set; }
        public int WriteCount { get; private set; }
        public string? LastAdmissionScreen { get; private set; }
        public int AdmissionWrites { get; private set; }

        public Fixture()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            _app = builder.Build();
            _app.Use(async (context, next) =>
            {
                var path = context.Request.Path.Value!;
                var fault = DirectoryFault;
                if (path == "/accounts/local/login" && fault is "charset" or "utf8")
                {
                    var charset = fault == "utf8" ? "utf8" : "unrecognized-charset";
                    context.Response.ContentType = "application/json; charset=" + charset;
                    await context.Response.Body.WriteAsync("""{"success":true,"data":{"access_token":"fixture-school-token"}}"""u8.ToArray());
                }
                else if ((path == "/api/v2/me/schools" && fault == "membership") || (path.EndsWith("/classroom-screens") && fault == "screens"))
                    await Academic(new object?[] { null }).ExecuteAsync(context);
                else if (path == "/api/v2/me/schools" && fault == "terms")
                    await Academic(new[] { new { role = "ADMIN", school = new { id = "school", name = "首屏演练学校", terms = new object?[] { null } } } }).ExecuteAsync(context);
                else if (path.EndsWith("/academic-structure") && fault == "classes")
                    await Academic(new { term = new { id = "term", status = "ACTIVE" }, administrativeClasses = new object?[] { null } }).ExecuteAsync(context);
                else if (path.EndsWith("/device-overview") && fault == "devices")
                    await Protocol("0.1", context.Request.Headers["X-Request-Id"].ToString(), new { items = new object?[] { null }, nextCursor = (string?)null }).ExecuteAsync(context);
                else await next(context);
            });
            _app.MapPost("/accounts/local/login", () => Academic(new { access_token = "fixture-school-token" }));
            _app.MapPost("/accounts/logout", () => Academic(new { }));
            var term = new { id = "term", status = "ACTIVE" };
            _app.MapGet("/api/v2/me/schools", () => Academic(new[]
            {
                new { role = "ADMIN", school = new { id = "school", name = "首屏演练学校", terms = new[] { term } } },
                new { role = "ADMIN", school = new { id = "other", name = "另一所学校", terms = new[] { term } } }
            }));
            _app.MapGet("/api/v2/admin/schools/other/classroom-screens", () => Academic(Array.Empty<object>()));
            _app.MapGet("/api/v2/admin/schools/other/academic-structure", () => Academic(new { term, administrativeClasses = Array.Empty<object>() }));
            _app.MapGet("/api/v2/npep/schools/other/device-overview", () => Results.StatusCode(503));
            _app.MapGet("/api/v2/admin/schools/school/classroom-screens", () =>
            {
                if (FailNextList) { FailNextList = false; return Results.StatusCode(503); }
                lock (_screenLock) return Academic(_screens.ToArray());
            });
            _app.MapGet("/api/v2/admin/schools/school/academic-structure", () => Academic(new
            {
                term, administrativeClasses = new[] { "empty", "inactive", "uncertain", "review", "retry" }.Select((id, index) => new
                { id, name = $"高二（{index + 1}）班", isActive = true, grade = new { name = "高二" } })
            }));
            const string npep = "/api/v2/npep/schools/school";
            _app.MapGet(npep + "/device-overview", (Delegate)((HttpContext context) =>
                OverviewUnavailable ? Results.StatusCode(503) : Protocol("0.1", context.Request.Headers["X-Request-Id"].ToString(),
                    new { items = Array.Empty<object>(), nextCursor = (string?)null })));
            _app.MapPost(npep + "/classes/{classId}/first-screen", async (string classId, WriteBody body, HttpContext context) =>
            {
                Check(context.Request.Headers["X-NPEP-Version"] == "0.16" && context.Request.Headers.Authorization == "Bearer fixture-school-token",
                    "The endpoint expects a scoped school account and NPEP 0.16.");
                var write = new Write(classId, body.TermId, body.RequestId, context.Request.Headers["X-Request-Id"].ToString());
                WriteCount++;
                await _writes.Writer.WriteAsync(write, context.RequestAborted);
                return await write.Response.Task.WaitAsync(context.RequestAborted);
            });
            _app.MapGet(npep + "/class-management/{screenId}", (string screenId, HttpContext context) =>
            {
                LastAdmissionScreen = screenId;
                string classId;
                lock (_screenLock) classId = _screens.Single(screen => screen.Id == screenId).AdministrativeClassId;
                return Protocol("0.16", context.Request.Headers["X-Request-Id"].ToString(), new
                { configured = false, enabled = false, revision = 0, screenBindingId = screenId, classId, bindingRevision = 1, pairingRevision = 0 });
            });
            _app.MapPost(npep + "/class-management/{screenId}", () => { AdmissionWrites++; return Results.StatusCode(500); });
            _app.StartAsync().GetAwaiter().GetResult();
            string origin = _app.Urls.Single();
            Access = new(origin, origin, "FIXTURE", "admin", "fixture-only", "school", "empty", "inactive", true);
        }

        public void SetScreens(string classId, params string[] ids)
        {
            lock (_screenLock)
            {
                _screens.RemoveAll(screen => screen.AdministrativeClassId == classId);
                _screens.AddRange(ids.Select(id => new ScreenRow(id, classId, true)));
            }
        }

        public async Task<Write> NextWrite()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            return await _writes.Reader.ReadAsync(deadline.Token);
        }

        public void Dispose() => Task.Run(async () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _app.StopAsync(deadline.Token);
            await _app.DisposeAsync();
        }).GetAwaiter().GetResult();
    }
}
