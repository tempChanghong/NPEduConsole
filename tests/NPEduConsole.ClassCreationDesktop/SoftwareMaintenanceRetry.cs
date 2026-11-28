using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private static async Task ExerciseSoftwareWriteRetryAsync(CancellationToken token)
    {
        const string school = "retry-school", first = "screen-a", second = "screen-b";
        var writes = new ConcurrentQueue<(string Device, string RequestId, string Body)>();
        var releaseFirstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invitation = Guid.NewGuid();
        var secondInvitation = Guid.NewGuid();
        int firstWrites = 0;
        bool secondRevisionMoved = false;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var service = builder.Build();
        string route = $"/api/v2/npep/schools/{school}/devices/{{device}}/software-update";
        service.MapGet(route, (HttpContext ctx) =>
            Respond(ctx, (string)ctx.Request.RouteValues["device"]! == first
                ? Volatile.Read(ref firstWrites) >= 2 ? 3 : 1
                : Volatile.Read(ref secondRevisionMoved) ? 2 : 1));
        service.MapPost(route, async (HttpContext ctx) =>
        {
            using var body = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            string device = (string)ctx.Request.RouteValues["device"]!;
            writes.Enqueue((device, ctx.Request.Headers["X-Request-Id"].ToString(), body.RootElement.GetRawText()));
            if (device == second)
            {
                ctx.Response.StatusCode = 503;
                return;
            }
            if (Interlocked.Increment(ref firstWrites) == 1)
            {
                await releaseFirstWrite.Task.WaitAsync(ctx.RequestAborted);
                ctx.Response.StatusCode = 503;
                return;
            }
            await Respond(ctx, 2);
        });
        Task Respond(HttpContext ctx, int revision) => ctx.Response.WriteAsJsonAsync(new
        {
            protocolVersion = "0.18", requestId = ctx.Request.Headers["X-Request-Id"].ToString(), serverTime = DateTimeOffset.UtcNow,
            data = new SoftwareUpdate("NPEduTools", revision == 3, revision switch { 2 => "INVITED", 3 => "MANAGED", _ => "UNMANAGED" },
                revision > 1 ? (string)ctx.Request.RouteValues["device"]! == first ? invitation : secondInvitation : null,
                revision, [], null, null, null)
        }, token);
        await service.StartAsync(token);
        using var client = new ConsoleClient(service.Urls.Single());
        const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Instance;
        var session = typeof(ConsoleClient).GetNestedType("Session", BindingFlags.NonPublic)!;
        typeof(ConsoleClient).GetField("_session", hidden)!.SetValue(client,
            Activator.CreateInstance(session, "software.retry.session"));
        var window = new MainWindow();
        var selection = typeof(MainWindow).GetNestedType("DeviceSelection", BindingFlags.NonPublic)!;
        var target = typeof(MainWindow).GetField("_device", hidden)!;
        var shownSnapshot = typeof(MainWindow).GetField("_software", hidden)!;
        var read = typeof(MainWindow).GetMethod("ReadSoftwareAsync", hidden)!;
        var readInFlight = typeof(MainWindow).GetField("_softwareRead", hidden)!;
        var close = typeof(MainWindow).GetMethod("CloseSoftwareMaintenance", hidden)!;
        typeof(MainWindow).GetField("_client", hidden)!.SetValue(window, client);
        typeof(MainWindow).GetField("_school", hidden)!.SetValue(window, new School(school, "Fixture school"));
        var card = Find<Control>(window, "SoftwareMaintenanceCard");
        var invite = Find<Button>(window, "InviteSoftwareButton");
        var feedback = Find<TextBlock>(window, "SoftwareMaintenanceFeedback");
        async Task Select(string device)
        {
            close.Invoke(window, null);
            target.SetValue(window, Activator.CreateInstance(selection, device, device, new Device(device, device, "ONLINE", null)));
            card.IsVisible = true;
            await (Task)read.Invoke(window, [false])!;
        }
        try
        {
            await Select(first);
            Check(invite.IsVisible && invite.IsEnabled, "screen A starts with an eligible invitation");
            Click(invite);
            await WaitUntil(() => writes.Count == 1, "screen A's first request is in flight");
            await Select(second);
            releaseFirstWrite.TrySetResult();
            Check(invite.IsEnabled && !feedback.IsVisible && writes.Count == 1,
                "the pending operation must not render on screen B or replay when switching devices");
            await Select(first);
            await WaitUntil(() => feedback.Text?.Contains("尚未确认") == true && readInFlight.GetValue(window) is null,
                "screen A retains its uncertain operation");
            Check(!invite.IsEnabled, "the original operation is unresolved until explicit refresh");
            await (Task)read.Invoke(window, [true])!;
            Check(!invite.IsEnabled && writes.Count == 1,
                "refreshing the unchanged revision must not reopen a new invitation with a new request ID");
            var retry = Find<Button>(window, "RetrySoftwareButton");
            Check(retry.IsVisible && retry.IsEnabled, "explicit refresh permits only retry by original ID");
            Click(retry);
            await WaitUntil(() => writes.Count == 2 && !retry.IsVisible && shownSnapshot.GetValue(window) is SoftwareUpdate { Revision: 3 },
                "explicit retry reads current revision 3 instead of displaying the replayed revision 2");
            var sent = writes.ToArray();
            Check(sent[0] == sent[1] && sent[0].Device == first && Guid.TryParse(sent[0].RequestId, out _),
                "retry must send the exact original body and request ID to screen A only");

            await Select(second);
            Click(invite);
            await WaitUntil(() => writes.Count == 3 && feedback.Text?.Contains("尚未确认") == true && readInFlight.GetValue(window) is null,
                "screen B keeps its own unresolved invitation");
            Volatile.Write(ref secondRevisionMoved, true);
            await (Task)read.Invoke(window, [true])!;
            Check(shownSnapshot.GetValue(window) is SoftwareUpdate { Revision: 2 } latest && latest.EnrollmentId == secondInvitation &&
                !retry.IsVisible && writes.Count == 3 && feedback.Text?.Contains("无法确认原请求") == true,
                "a newer revision shows current state without attributing it to the original request or replaying it");
            Console.WriteLine("PASS A/B/A isolation, exact original retry, fresh status after replay, and no retry or attribution on a newer revision.");
        }
        finally
        {
            releaseFirstWrite.TrySetCanceled();
            close.Invoke(window, null);
        }

        async Task WaitUntil(Func<bool> condition, string message)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(4));
            try { while (!condition()) await Task.Delay(20, limit.Token); }
            catch (OperationCanceledException) { throw new TimeoutException(message); }
        }
    }
}
