using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private static int CheckSoftwareMaintenancePolling(bool retry = false)
    {
        AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect().SetupWithoutStarting();
        using var end = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        int result = 1;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await (retry ? ExerciseSoftwareWriteRetryAsync(end.Token) : ExerciseSoftwarePollingAsync(end.Token));
                result = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { await end.CancelAsync(); }
        });
        Dispatcher.UIThread.MainLoop(end.Token);
        return result;
    }

    private static async Task ExerciseSoftwarePollingAsync(CancellationToken token)
    {
        const string school = "software-fixture", device = "front-screen";
        int reads = 0;
        var allowRecoveredRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowForbiddenRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowLateRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var service = builder.Build();
        var enrollment = Guid.NewGuid();
        var plan = Guid.NewGuid().ToString("D");
        service.MapGet($"/api/v2/npep/schools/{school}/devices/{device}/software-update", async (HttpContext ctx) =>
        {
            int read = Interlocked.Increment(ref reads);
            if (read == 2)
            {
                ctx.Response.StatusCode = 503;
                return;
            }
            if (read == 3) await allowRecoveredRead.Task.WaitAsync(ctx.RequestAborted);
            if (read == 4)
            {
                await allowForbiddenRead.Task.WaitAsync(ctx.RequestAborted);
                ctx.Response.StatusCode = 403;
                return;
            }
            if (read == 6) await allowLateRead.Task;
            if (read >= 7)
            {
                ctx.Response.StatusCode = 503;
                return;
            }
            await ctx.Response.WriteAsJsonAsync(new
            {
                protocolVersion = "0.18", requestId = ctx.Request.Headers["X-Request-Id"].ToString(), serverTime = DateTimeOffset.UtcNow,
                data = new SoftwareUpdate("NPEduTools", true, "MANAGED", enrollment, read, [new("1.1.10.0"), new("1.1.11.0")],
                    new SoftwareUpdatePlan(plan, "1.1.11.0", read == 1 ? "APPROVED" : "AWAITING_RESTART", null, DateTimeOffset.UtcNow),
                    "1.1.10.0", DateTimeOffset.UtcNow, "AVAILABLE")
            });
        });
        await service.StartAsync(token);
        using var client = new ConsoleClient(service.Urls.Single());
        const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Instance;
        var clientSession = typeof(ConsoleClient).GetNestedType("Session", BindingFlags.NonPublic)!;
        typeof(ConsoleClient).GetField("_session", hidden)!.SetValue(client,
            Activator.CreateInstance(clientSession, "software.fixture.session"));

        var window = new MainWindow();
        var selection = typeof(MainWindow).GetNestedType("DeviceSelection", BindingFlags.NonPublic)!;
        var screen = Activator.CreateInstance(selection, "binding", "Front", new Device(device, "Front", "ONLINE", null));
        typeof(MainWindow).GetField("_client", hidden)!.SetValue(window, client);
        typeof(MainWindow).GetField("_school", hidden)!.SetValue(window, new School(school, "Fixture school"));
        typeof(MainWindow).GetField("_device", hidden)!.SetValue(window, screen);
        var timer = (DispatcherTimer)typeof(MainWindow).GetField("_softwarePoll", hidden)!.GetValue(window)!;
        var card = Find<Control>(window, "SoftwareMaintenanceCard");
        var revoke = Find<Button>(window, "CancelSoftwareButton");
        var receipt = Find<TextBlock>(window, "SoftwareReceiptHeading");
        var snapshot = typeof(MainWindow).GetField("_software", hidden)!;
        var errorField = typeof(MainWindow).GetField("_softwareError", hidden)!;
        var readMethod = typeof(MainWindow).GetMethod("ReadSoftwareAsync", hidden)!;
        var closeMethod = typeof(MainWindow).GetMethod("CloseSoftwareMaintenance", hidden)!;
        card.IsVisible = true;
        timer.Interval = TimeSpan.FromMilliseconds(120);
        try
        {
            await (Task)readMethod.Invoke(window, [false])!;
            Check(timer.IsEnabled && revoke.IsVisible && revoke.IsEnabled, "active task should start the normal poll");
            await WaitUntil(() => errorField.GetValue(window) is not null, "first background read should return 503");
            Check(timer.IsEnabled, "a transient 503 stopped the active software maintenance poll");
            Check(!revoke.IsVisible && !revoke.IsEnabled && receipt.Text == "上次已确认维护详情",
                "the offline page should show the historical receipt and disable writes");
            await WaitUntil(() => Volatile.Read(ref reads) == 3, "recovery should be read without a manual refresh");
            allowRecoveredRead.TrySetResult();
            await WaitUntil(() => snapshot.GetValue(window) is SoftwareUpdate { Plan.State: "AWAITING_RESTART" },
                "automatic recovery should render the current receipt");
            Check(receipt.Text == "本次维护详情", "the recovered receipt should be labelled current again");
            allowForbiddenRead.TrySetResult();
            await WaitUntil(() => Volatile.Read(ref reads) == 4 && errorField.GetValue(window) is not null, "withdrawn access should be read");
            await Task.Delay(350, token);
            Check(!timer.IsEnabled && Volatile.Read(ref reads) == 4 && !Find<Control>(window, "SoftwareReceiptSection").IsVisible,
                "403 must stop polling and clear the previously visible receipt");

            await (Task)readMethod.Invoke(window, [true])!;
            Check(timer.IsEnabled && Volatile.Read(ref reads) == 5, "a new confirmed active snapshot can resume normal polling");
            await WaitUntil(() => Volatile.Read(ref reads) == 6, "an old poll is in flight");
            closeMethod.Invoke(window, null);
            allowLateRead.TrySetResult();
            await Task.Delay(350, token);
            Check(!card.IsVisible && !timer.IsEnabled && snapshot.GetValue(window) is null && Volatile.Read(ref reads) == 6,
                "a late read from a closed device card must not restore its state or restart the poll");

            card.IsVisible = true;
            await (Task)readMethod.Invoke(window, [false])!;
            await Task.Delay(350, token);
            Check(!timer.IsEnabled && Volatile.Read(ref reads) == 7,
                "a transient failure on the first read must not start a poll");
            Console.WriteLine("PASS transient recovery, 403 stops and clears history, closing rejects late reply, and a failed first read stays manual.");
        }
        finally
        {
            closeMethod.Invoke(window, null);
            allowRecoveredRead.TrySetCanceled();
            allowForbiddenRead.TrySetCanceled();
            allowLateRead.TrySetCanceled();
        }

        async Task WaitUntil(Func<bool> condition, string message)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(3));
            try { while (!condition()) await Task.Delay(20, limit.Token); }
            catch (OperationCanceledException) { throw new TimeoutException(message); }
        }
    }
}
