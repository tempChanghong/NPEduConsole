using System.Security.Cryptography;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NPEduConsole.Core;

public sealed record DesktopOAuthProvider(string Id, string Name);
public sealed record DesktopOAuthSignIn(string AccountName, IReadOnlyList<School> Schools);

public sealed class DesktopOAuthRequest
{
    internal DesktopOAuthRequest(ConsoleClient client, string id, string verifier, Uri browserUri, TimeSpan lifetime)
    {
        Client = client;
        Id = id;
        Verifier = verifier;
        BrowserUri = browserUri;
        Lifetime = lifetime;
    }

    public Uri BrowserUri { get; }
    public string ConfirmationCode => Id[..8];
    internal ConsoleClient Client { get; }
    internal string Id { get; }
    internal string Verifier { get; }
    internal TimeSpan Lifetime { get; }
}

public sealed partial class ConsoleClient
{
    private const string DesktopOAuthPath = "accounts/oauth/desktop/";
    private static readonly Regex OAuthKey = new(@"\A[A-Za-z0-9_-]{43}\z", RegexOptions.NonBacktracking);
    private static readonly Regex OAuthProviderId = new(@"\A[a-z0-9_-]{1,32}\z", RegexOptions.NonBacktracking);
    private static readonly Regex OAuthAccountToken = new(@"\A[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\z", RegexOptions.NonBacktracking);
    private sealed record DesktopOAuthBegin(string RequestId, int ExpiresInSeconds);
    private sealed record DesktopOAuthClaim(string State, [property: JsonPropertyName("access_token")] string? AccessToken = null);
    private sealed record OAuthAccountIdentity(string? Name, string? Email);

    public async Task<IReadOnlyList<DesktopOAuthProvider>> GetDesktopOAuthProvidersAsync(CancellationToken ct)
    {
        var providers = await RequestAsync<DesktopOAuthProvider[]>(HttpMethod.Get, DesktopOAuthPath + "providers", null, ct);
        if (providers.Length > 32 || providers.Any(provider => provider is null || !OAuthProviderId.IsMatch(provider.Id) ||
            string.IsNullOrWhiteSpace(provider.Name) || provider.Name.Length > 128 || provider.Name.Any(char.IsControl)) ||
            providers.DistinctBy(provider => provider.Id).Count() != providers.Length)
            throw InvalidResponse();
        return providers;
    }

    public async Task<DesktopOAuthRequest> BeginDesktopOAuthAsync(DesktopOAuthProvider provider, CancellationToken ct)
    {
        if (_session is not null) throw new ConsoleApiException(0, "请先退出当前账号。");
        if (!OAuthProviderId.IsMatch(provider.Id)) throw new ArgumentException("请选择服务器提供的登录方式。", nameof(provider));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var begin = await RequestAsync<DesktopOAuthBegin>(HttpMethod.Post, DesktopOAuthPath + "begin", null, ct,
            body: new { provider = provider.Id, challenge });
        if (!OAuthKey.IsMatch(begin.RequestId) || begin.ExpiresInSeconds is < 1 or > 600) throw InvalidResponse();
        var uri = new Uri(_http.BaseAddress!, DesktopOAuthPath + begin.RequestId + "/browser");
        return new(this, begin.RequestId, verifier, uri, TimeSpan.FromSeconds(begin.ExpiresInSeconds));
    }

    public async Task<DesktopOAuthSignIn> WaitForDesktopOAuthAsync(DesktopOAuthRequest request, Action browserVerified, CancellationToken ct)
    {
        VerifyDesktopOAuthClient(request);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (elapsed.Elapsed < request.Lifetime)
        {
            ct.ThrowIfCancellationRequested();
            // A claim already sent to KV can create a session. Read its bounded response even after local cancellation
            // so any delivered session can be explicitly signed out without attaching it to this client.
            var result = await RequestAsync<DesktopOAuthClaim>(HttpMethod.Post, DesktopOAuthPath + request.Id + "/claim", null,
                CancellationToken.None, body: new { verifier = request.Verifier });
            if (result.State == "COMPLETE" && result.AccessToken is {Length: <= 8192} token && OAuthAccountToken.IsMatch(token))
            {
                ct.ThrowIfCancellationRequested();
                browserVerified();
                try
                {
                    await SendAsync(HttpMethod.Post, DesktopOAuthPath + request.Id + "/acknowledge", token, ct,
                        body: new { verifier = request.Verifier });
                }
                catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or >= 500)
                {
                    // Re-read once with the received credential. An unconfirmed session cannot read any schools.
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    await AbandonLoginSessionAsync(token);
                    throw;
                }
                return await AttachSchoolSessionAsync(token, includeIdentity: true, ct);
            }
            if (result.State != "PENDING" || result.AccessToken is not null) throw InvalidResponse();
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        throw DesktopOAuthError(410, "OAUTH_DESKTOP_UNAVAILABLE");
    }

    public async Task CancelDesktopOAuthAsync(DesktopOAuthRequest request, CancellationToken ct)
    {
        VerifyDesktopOAuthClient(request);
        using var response = await _http.PostAsJsonAsync(DesktopOAuthPath + request.Id + "/cancel",
            new { verifier = request.Verifier }, ct);
        if ((int)response.StatusCode is not (204 or 410)) throw DesktopOAuthError((int)response.StatusCode, null);
    }

    private void VerifyDesktopOAuthClient(DesktopOAuthRequest request)
    {
        if (!ReferenceEquals(this, request.Client)) throw new ArgumentException("登录请求属于另一个 KV 客户端。", nameof(request));
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static ConsoleApiException DesktopOAuthError(int status, string? code) => new(status, (status, code) switch
    {
        (404 or 426, _) => "此 KV 版本没有桌面浏览器登录入口。仍可使用本地账号登录。",
        (410, _) => "这次浏览器登录已结束或过期，请重新发起。",
        (409, "OAUTH_DESKTOP_DECLINED") => "浏览器认证未完成，请重新发起。",
        (429, _) => "服务器收到的登录请求过多，请稍后再试。",
        (400, "OAUTH_DESKTOP_INVALID") => "服务器提供的登录方式已变化，请刷新后重试。",
        _ => HttpError(status, false).Message,
    }, code);
}
