using System.Net;
using System.Text.Json;

namespace NPEduConsole.Core;

public enum DeploymentDeclarationState { Declared, Missing, Unavailable, Invalid }

public sealed record DeploymentDeclaration(DeploymentDeclarationState State, Uri? ApiBaseUrl = null);

public static class SchoolSetupDeployment
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(8),
        MaxResponseContentBufferSize = 16 * 1024,
    };

    public static Uri NormalizeWebsite(string address)
    {
        var uri = ConsoleClient.NormalizeKvAddress(address);
        if (uri.AbsolutePath != "/")
            throw new ConsoleApiException(0, "请输入师生网页的站点地址，不含页面路径。");
        return uri;
    }

    public static Uri BrowserCheckUrl(Uri website, Uri expectedApi) =>
        new UriBuilder(new Uri(website, "connection-check.html"))
        {
            Fragment = "expected=" + Uri.EscapeDataString(expectedApi.AbsoluteUri),
        }.Uri;
    public static Uri LegacySetupUrl(Uri website) => new(website, "setup");

    public static async Task<DeploymentDeclaration> ReadDeclarationAsync(Uri website, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(website, ".well-known/npclassworks-deployment.json"));
            request.Headers.CacheControl = new() { NoCache = true };
            using var response = await Http.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone ||
                response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "text/html")
                return new(DeploymentDeclarationState.Missing);
            if (!response.IsSuccessStatusCode) return new(DeploymentDeclarationState.Unavailable);

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schema) || schema != 1 ||
                !root.TryGetProperty("apiBaseUrl", out var api))
                return new(DeploymentDeclarationState.Invalid);
            return api.ValueKind switch
            {
                JsonValueKind.Null => new(DeploymentDeclarationState.Declared, website),
                JsonValueKind.String => new(DeploymentDeclarationState.Declared, ConsoleClient.NormalizeKvAddress(api.GetString()!)),
                _ => new(DeploymentDeclarationState.Invalid),
            };
        }
        catch (Exception error) when (error is JsonException or ConsoleApiException)
        {
            return new(DeploymentDeclarationState.Invalid);
        }
        catch (Exception error) when (error is HttpRequestException || error is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new(DeploymentDeclarationState.Unavailable);
        }
    }
}
