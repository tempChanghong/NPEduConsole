using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
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
using Calendar = Avalonia.Controls.Calendar;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is not ([_] or ["--live", _, _])) throw new ArgumentException("Specify a screenshot directory or --live <fixture.json> <directory>.");
        using var fixture = args.Length == 1 ? new AuditFixture() : null;
        var credentials = fixture?.Credentials ?? JsonSerializer.Deserialize<Credentials>(File.ReadAllText(args[1]), JsonSerializerOptions.Web)!;
        Check(Uri.TryCreate(credentials.Origin, UriKind.Absolute, out var url) && url.IsLoopback, "Use an isolated loopback test server.");
        string output = args[^1];
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyAsync(credentials, fixture, output); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyAsync(Credentials credentials, AuditFixture? fixture, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        try
        {
            var main = (MainWindow)desktop.MainWindow!;
            T MainControl<T>(string name) where T : Control => main.FindControl<T>(name) ?? throw new InvalidDataException(name);
            var audit = MainControl<SchoolAuditView>("SchoolAuditWorkspace");
            T Find<T>(string name) where T : Control => audit.FindControl<T>(name) ?? throw new InvalidDataException(name);
            string Status() => Find<TextBlock>("AuditStatus").Text ?? "";
            string Detail(string name) => Find<SelectableTextBlock>(name).Text ?? "";
            static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var schools = MainControl<ComboBox>("SchoolPicker");
            var open = MainControl<Button>("OpenSchoolAuditButton");
            var list = Find<ListBox>("AuditList");
            var query = Find<Button>("QueryAuditButton");
            var more = Find<Button>("MoreAuditButton");
            var details = Find<Border>("AuditDetails");
            var from = Find<TextBox>("AuditFromDate");
            var to = Find<TextBox>("AuditToDate");
            Check(!open.IsEffectivelyVisible, "Audit navigation is unavailable before login.");
            await SignIn();
            Check(!open.IsEnabled, "Audit navigation waits for school selection.");
            SelectSchool(credentials.SchoolId);
            Click(open);
            Check(audit.IsEffectivelyVisible && !MainControl<Grid>("ClassManagementWorkspace").IsEffectivelyVisible,
                "School navigation works without choosing a term, class or device.");

            if (fixture is null)
            {
                await Until(() => query.IsEnabled && list.ItemCount == 50, "first live page");
                var actions = Find<ComboBox>("AuditActionFilter");
                var results = Find<ComboBox>("AuditResultFilter");
                actions.SelectedItem = actions.Items.Single(option => option?.ToString() == "预览学校账号操作");
                Click(query);
                await Until(() => query.IsEnabled && list.ItemCount == 1, "single account preview");
                await ShowDetails();
                Check(Detail("AuditDetailRequest").EndsWith("/management-accounts/preview"), "Account previews have their own filter.");
                Click(Find<Button>("CloseAuditDetailsButton"));
                foreach (var (label, count) in new[] { ("修改任课关系", 2), ("保存学期资料", 1) })
                {
                    actions.SelectedItem = actions.Items.Single(option => option?.ToString() == label);
                    results.SelectedItem = results.Items.Single(option => option?.ToString() == "失败");
                    Click(query);
                    await Until(() => query.IsEnabled && list.ItemCount == count, label + "未保存的记录");
                    await ShowDetails();
                    Check(Detail("AuditDetailAction") == label && Detail("AuditDetailResult") == "HTTP 200 · 失败", label);
                    await Capture(main, output, label == "修改任课关系" ? "teaching-refused" : "term-refused");
                    Click(Find<Button>("CloseAuditDetailsButton"));
                }
                actions.SelectedIndex = results.SelectedIndex = 0;
                from.Text = to.Text = credentials.AuditDate;
                Click(query);
                await Until(() => query.IsEnabled && list.ItemCount == 50 && more.IsVisible, "local single-day live filter");
                await ShowDetails();
                await Capture(main, output, "live-detail");
                CheckRosterDetails();
                Click(Find<Button>("CloseAuditDetailsButton"));
                Click(more);
                await Until(() => list.ItemCount == 53 && !more.IsVisible, "live cursor ends on the selected date");
                SelectSchool(credentials.SecondarySchoolId);
                Check(list.ItemCount == 0 && !details.IsVisible && Detail("AuditDetailMetadata") == "", "Switching clears details immediately.");
                OpenAuditFromOverview();
                await Until(() => query.IsEnabled && list.ItemCount == 1 && MainControl<TextBlock>("ClassListMessage").Text == "该学校暂无可查看班级。",
                    "live audit logs for a school without an active term");
                await Capture(main, output, "live-school-without-classes");
                await ShowDetails();
                Check(Detail("AuditDetailAction") == "FUTURE_ACTION", "Unknown action can be read in the live view.");
                await SignOut();
                End("PASS: Windows desktop with real KV/PostgreSQL, local whole-day filtering, 53 records over two pages, redacted historical details and a school with no classes.", 0);
                return;
            }

            var original = await fixture.NextAsync();
            Check(original.SchoolId == credentials.SchoolId && original.Query["limit"] == "50" && original.Query.Count == 1, "Opening fetches an unfiltered first page.");
            Check(Find<ComboBox>("AuditActorFilter").Items.Cast<object>().Any(option => option.ToString() == "系统初始化") &&
                Find<ComboBox>("AuditActionFilter").Items.Cast<object>().Any(option => option.ToString() == "导入人员配置"),
                "Initialization and staff configuration imports are available as filters.");
            from.Text = "2026-04-16";
            var calendar = await OpenCalendar("OpenAuditToCalendar");
            ((StackPanel)calendar.Content!).Children.OfType<Calendar>().Single().SelectedDate = new DateTime(2026, 4, 15);
            await Until(() => !calendar.IsOpen && to.Text == credentials.AuditDate, "choosing a day populates the end date and closes the calendar");
            Click(query);
            Check(Status().Contains("有效的开始和结束日期"), "An inverted range is caught locally.");
            from.Text = "2026-02-30";
            Click(query);
            Check(Status().Contains("有效的开始和结束日期"), "An impossible typed date is caught locally.");
            var startCalendar = await OpenCalendar("OpenAuditFromCalendar");
            Click(((StackPanel)startCalendar.Content!).Children.OfType<Button>().Single());
            await Until(() => !startCalendar.IsOpen && string.IsNullOrEmpty(from.Text), "clear date in the open calendar");
            from.Text = credentials.AuditDate;
            Find<ComboBox>("AuditActorFilter").SelectedIndex = 2;
            Find<ComboBox>("AuditActionFilter").SelectedItem = Find<ComboBox>("AuditActionFilter").Items.Cast<object>()
                .Single(option => option.ToString() == "名单变更记录");
            Find<ComboBox>("AuditResultFilter").SelectedIndex = 2;
            Check(list.ItemCount == 0 && !more.IsVisible && Status().Contains("请应用筛选"), "Editing filters clears obsolete results and pagination.");
            Click(query);
            var filtered = await fixture.NextAsync();
            var local = DateOnly.ParseExact(credentials.AuditDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var midnight = new DateTimeOffset(local.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local));
            var tomorrow = new DateTimeOffset(local.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local));
            Check(filtered.Query["actorType"] == "CLASSROOM_SCREEN" && filtered.Query["action"] == "CLASS_ROSTER_SAVED" && filtered.Query["success"] == "false" &&
                Parse("from") == midnight && Parse("before") == tomorrow && Parse("to") == tomorrow.AddMilliseconds(-1) &&
                !filtered.Query.ContainsKey("cursor"), "The desktop sends scope, all filters, and both UTC bounds for the entire local day.");
            original.Reply(AuditFixture.Entry(credentials.SchoolId, "obsolete-filter"));
            await Task.Delay(150);
            Check(list.ItemCount == 0 && !query.IsEnabled, "An obsolete first page cannot replace the pending filtered request.");
            const string cursor = "id+?next=1&exact=yes";
            object roster = AuditFixture.Roster(credentials.SchoolId);
            filtered.Reply(cursor, roster);
            await Until(() => list.ItemCount == 1 && more.IsEnabled && more.IsVisible, "filtered result with next cursor");
            await Capture(main, output, "filtered-results");

            Click(more);
            var failedPage = await fixture.NextAsync();
            Check(failedPage.Query["cursor"] == cursor && failedPage.Query["before"] == filtered.Query["before"], "The cursor and original filters survive URI encoding.");
            failedPage.Fail(503);
            await Until(() => more.IsEnabled && more.Content is "重试加载更多", "recoverable next page error");
            Check(list.ItemCount == 1, "A failed continuation keeps the loaded rows.");
            Click(more);
            var retried = await fixture.NextAsync();
            Check(retried.Query["cursor"] == cursor, "Retry uses the same cursor.");
            retried.Reply(roster, AuditFixture.Entry(credentials.SchoolId, "older"));
            await Until(() => list.ItemCount == 2 && !more.IsVisible, "continuation deduplicates rows");
            await ShowDetails();
            CheckRosterDetails();
            await Capture(main, output, "historical-details");

            SelectSchool(credentials.SecondarySchoolId);
            Check(!details.IsVisible && Detail("AuditDetailMetadata") == "" && list.ItemCount == 0 && string.IsNullOrEmpty(from.Text),
                "A school switch clears details, records, and the previous filters synchronously.");
            OpenAuditFromOverview();
            var other = await fixture.NextAsync();
            Check(other.SchoolId == credentials.SecondarySchoolId && other.Query.Count == 1, "A new school starts at its own first page.");
            SelectSchool(credentials.SchoolId);
            OpenAuditFromOverview();
            var current = await fixture.NextAsync();
            other.Reply(AuditFixture.Entry(credentials.SecondarySchoolId, "obsolete-school"));
            await Task.Delay(150);
            Check(list.ItemCount == 0 && !query.IsEnabled, "A late page cannot cross schools.");
            current.Reply(AuditFixture.Entry(credentials.SecondarySchoolId, "incorrectly-scoped"));
            await Until(() => query.IsEnabled && Status().Contains("读取审计记录失败") && list.ItemCount == 0,
                "a response containing another school's records is rejected");
            Click(query);
            (await fixture.NextAsync()).Reply("pending-cursor", AuditFixture.Entry(credentials.SchoolId, "current"));
            await Until(() => list.ItemCount == 1 && more.IsEnabled, "current school result");
            Click(more);
            var denied = await fixture.NextAsync();
            await ShowDetails();
            denied.Fail(403);
            await Until(() => Status().Contains("无权") && query.IsEnabled, "revoked school permission feedback");
            Check(list.ItemCount == 0 && !details.IsVisible && Detail("AuditDetailAction") == "" && !more.IsVisible,
                "Revocation removes everything loaded for that school.");

            Click(query);
            var beforeLogout = await fixture.NextAsync();
            var signingOut = SignOut();
            beforeLogout.Reply(AuditFixture.Entry(credentials.SchoolId, "obsolete-session"));
            await signingOut;
            Check(!audit.IsEffectivelyVisible && list.ItemCount == 0 && Detail("AuditDetailMetadata") == "", "Sign-out clears audit state and ignores in-flight responses.");

            await SignIn();
            SelectSchool(credentials.SchoolId);
            Click(open);
            var beforeClose = await fixture.NextAsync();
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            bool closed = false;
            main.Closed += (_, _) => closed = true;
            main.Close();
            beforeClose.Reply(AuditFixture.Entry(credentials.SchoolId, "obsolete-window"));
            await Until(() => closed, "close the main window with audit in flight");
            Check(!audit.IsEffectivelyVisible && list.ItemCount == 0, "Window close cancels the read and clears scoped data.");
            End("PASS: Windows audit navigation, filters and calendar, obsolete requests, continuation retry, redaction and full roster, school switching, permission denial, logout and window close.", 0);

            DateTimeOffset Parse(string name) => DateTimeOffset.Parse(filtered.Query[name]!, CultureInfo.InvariantCulture);

            void SelectSchool(string id) => schools.SelectedItem = schools.Items.Cast<School>().Single(school => school.Id == id);

            void OpenAuditFromOverview()
            {
                Check(MainControl<SchoolOverviewView>("SchoolOverviewWorkspace").IsEffectivelyVisible,
                    "Switching schools opens the new school's overview.");
                Click(open);
            }

            async Task<Flyout> OpenCalendar(string name)
            {
                var opener = Find<Button>(name);
                var flyout = (Flyout)FlyoutBase.GetAttachedFlyout(opener)!;
                Click(opener);
                await Until(() => flyout.IsOpen, "date calendar opens from its button");
                return flyout;
            }

            async Task SignOut()
            {
                var logout = main.GetVisualDescendants().OfType<Button>().Single(button => button.Content is "退出登录");
                Click(logout);
                await Until(() => MainControl<Button>("LoginButton").IsEffectivelyVisible && MainControl<Button>("LoginButton").IsEffectivelyEnabled, "logged out");
            }

            async Task SignIn()
            {
                MainControl<TextBox>("ServerAddress").Text = credentials.Origin;
                MainControl<TextBox>("SchoolCode").Text = credentials.SchoolCode;
                MainControl<TextBox>("Username").Text = credentials.Username;
                MainControl<TextBox>("Password").Text = credentials.Password;
                Click(MainControl<Button>("LoginButton"));
                await Until(() => schools.ItemCount == 2, "two authorized schools after login");
            }

            async Task ShowDetails()
            {
                Button? button = null;
                await Until(() => (button = list.GetVisualDescendants().OfType<Button>()
                    .FirstOrDefault(candidate => candidate.DataContext == list.Items[0] && candidate.Content is "查看详情")) is not null,
                    "details button for first row");
                Click(button!);
                Check(details.IsEffectivelyVisible, "Details are displayed for the selected entry.");
            }

            void CheckRosterDetails()
            {
                var metadata = Detail("AuditDetailMetadata");
                Check(Detail("AuditDetailAction") == "名单变更记录", "The first whole-day row is the historical roster.");
                Check(Detail("AuditDetailRequest") == "PUT /historic/students", "Only the path is shown for the request.");
                Check(Detail("AuditDetailResult") == "HTTP 409 · 失败", "The HTTP status and result are shown together.");
                Check(Detail("AuditDetailIp") == "127.0.0.1" && Detail("AuditDetailActor").Contains("班级大屏"), "The source IP and actor are shown.");
                Check(metadata.Contains("学生64") && metadata.Contains("[REDACTED]") && !metadata.Contains("fixture-only"),
                    "Complete historical roster and redacted sensitive values are both present.");
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

    private static async Task Capture(Window target, string output, string name)
    {
        await Task.Delay(140);
        var root = (Control)target.Content!;
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

    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidDataException(message);
    }

    private sealed record Credentials(string Origin, string SchoolCode, string Username, string Password, string SchoolId,
        string SecondarySchoolId, string AuditDate);

    private sealed class AuditFixture : IDisposable
    {
        private const string Token = "synthetic-school-audit-token";
        private readonly WebApplication _app;
        private readonly Channel<AuditRequest> _calls = Channel.CreateUnbounded<AuditRequest>();

        public Credentials Credentials { get; }

        public AuditFixture()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            _app = builder.Build();
            _app.MapPost("/accounts/local/login", () => Ok(new { access_token = Token }));
            _app.MapPost("/accounts/logout", () => Ok(new { }));
            _app.MapGet("/api/v2/me/schools", () => Ok(new[]
            {
                new { role = "ADMIN", school = new { id = "a", name = "审计测试学校", terms = Array.Empty<object>() } },
                new { role = "OWNER", school = new { id = "b", name = "暂无班级的学校", terms = Array.Empty<object>() } },
            }));
            _app.MapGet("/api/v2/admin/schools/{schoolId}/classroom-screens", () => Ok(Array.Empty<object>()));
            _app.MapGet("/api/v2/admin/schools/{schoolId}/audit-logs", async (string schoolId, HttpContext context) =>
            {
                if (context.Request.Headers.Authorization != "Bearer " + Token) return Results.Unauthorized();
                var call = new AuditRequest(schoolId, context.Request.Query);
                await _calls.Writer.WriteAsync(call, context.RequestAborted);
                return await call.Response.Task.WaitAsync(context.RequestAborted);
            });
            _app.StartAsync().GetAwaiter().GetResult();
            Credentials = new(_app.Urls.Single(), "FIXTURE", "test-admin", "fixture-only", "a", "b", "2026-04-15");
        }

        public async Task<AuditRequest> NextAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            return await _calls.Reader.ReadAsync(deadline.Token);
        }

        public static object Entry(string schoolId, string id) => new
        {
            id, schoolId, actorType = "ACCOUNT", action = "FUTURE_ACTION", success = true, createdAt = "2026-04-15T10:00:00Z"
        };

        public static object Roster(string schoolId) => new
        {
            id = "legacy-roster", schoolId, actorType = "CLASSROOM_SCREEN", action = "CLASS_ROSTER_SAVED", success = false,
            createdAt = "2026-04-15T10:00:00Z", summary = "测试名单更新", actorScreen = new { name = "高二（1）班大屏" },
            entityType = "WORKSPACE", entityId = "room-with-long-id", requestMethod = "PUT", statusCode = 409, clientIp = "127.0.0.1",
            requestPath = "/historic/students?pin=fixture-only",
            metadata = new { nested = new { pin = "fixture-only" }, after = Enumerable.Range(0, 65).Select(i => new { name = "学生" + i }) }
        };

        internal static IResult Ok(object data) => Results.Json(new { success = true, data });

        public void Dispose() => Task.Run(async () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _app.StopAsync(deadline.Token);
            await _app.DisposeAsync();
        }).GetAwaiter().GetResult();
    }

    private sealed record AuditRequest(string SchoolId, IQueryCollection Query)
    {
        public TaskCompletionSource<IResult> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Reply(params object[] items) => Reply(null, items);
        public void Reply(string? nextCursor, params object[] items) => Response.SetResult(AuditFixture.Ok(new { items, nextCursor }));
        public void Fail(int code) => Response.SetResult(Results.StatusCode(code));
    }
}
