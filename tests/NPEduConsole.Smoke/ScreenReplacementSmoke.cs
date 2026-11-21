using System.Text.Json;
using NPEduConsole.Core;

internal static class ScreenReplacementSmoke
{
    internal static async Task RunAsync(string path)
    {
        var fixture = JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(path), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Missing screen replacement fixture.");
        if (!new Uri(fixture.Origin).IsLoopback) throw new InvalidDataException("Screen replacement smoke requires a loopback service.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = timeout.Token;
        using var client = new ConsoleClient(fixture.Origin);
        var schools = await client.LoginAsync(fixture.SchoolCode, fixture.Username, fixture.Password, ct);
        try
        {
            if (!schools.Any(school => school.Id == fixture.SchoolId)) throw new InvalidDataException("The fixture school is not authorized.");
            var list = await client.GetScreenReplacementsAsync(fixture.SchoolId, fixture.ScreenId, ct);
            if (fixture.Decision is { } decision)
            {
                if (!fixture.AllowScreenReplacementTestWrites || fixture.PairingId is not { } id || fixture.RequestId is not { } requestId ||
                    !Enum.TryParse<ScreenReplacementDecision>(decision, ignoreCase: true, out var action) || !Enum.IsDefined(action))
                    throw new InvalidDataException("Review requires an explicit disposable fixture, pairing ID, request ID and decision.");
                var snapshot = list.Items.Single(item => item.PairingId == id && item.State == "PENDING");
                await client.ReviewScreenReplacementAsync(snapshot, action, requestId, ct);
                list = await client.GetScreenReplacementsAsync(fixture.SchoolId, fixture.ScreenId, ct);
            }
            var detail = fixture.PairingId is { } detailId
                ? await client.GetScreenReplacementAsync(fixture.SchoolId, fixture.ScreenId, detailId, ct) : null;
            if (fixture.ExpectedProgress is { } expected && detail?.Progress != expected)
                throw new InvalidDataException($"Expected progress {expected}; got {detail?.Progress ?? "no selected request"}.");
            Console.WriteLine(JsonSerializer.Serialize(new { list.Items, list.LatestHandover, Detail = detail }, JsonSerializerOptions.Web));
        }
        finally { await client.LogoutAsync(ct); }
    }

    private sealed record Fixture(string Origin, string SchoolCode, string Username, string Password, string SchoolId, string ScreenId,
        Guid? PairingId = null, string? ExpectedProgress = null, string? Decision = null, Guid? RequestId = null,
        bool AllowScreenReplacementTestWrites = false);
}
