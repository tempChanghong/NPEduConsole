using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private sealed class BrowserLoginOperation(ConsoleClient client, string address, DesktopOAuthProvider provider)
    {
        internal ConsoleClient Client { get; } = client;
        internal string Address { get; } = address;
        internal DesktopOAuthProvider Provider { get; } = provider;
        internal CancellationTokenSource Lifetime { get; } = new();
        internal DesktopOAuthRequest? Request { get; set; }
        internal Task? ServerCancellation { get; set; }
        internal bool Claiming { get; set; }
    }

    private BrowserLoginOperation? _desktopLogin;
    private CancellationTokenSource? _browserDiscovery;
    private IReadOnlyList<DesktopOAuthProvider> _browserProviders = [];
    private string? _browserProvidersAddress;
    private bool _closingBrowserLogin;
    internal Func<Uri, bool>? DesktopBrowserLauncher { private get; set; }

    private void ConnectBrowserLogin()
    {
        ServerAddress.TextChanged += (_, _) =>
        {
            if (_desktopLogin is not null) _ = CancelBrowserLoginAsync(restoreFocus: false);
            if (LoginView.IsVisible) _ = DiscoverBrowserLoginAsync(debounce: true);
        };
        BrowserLogin.LoginRequested += provider => _ = StartBrowserLoginAsync(provider);
        BrowserLogin.CancelRequested += () => _ = CancelBrowserLoginAsync(restoreFocus: true);
        BrowserLogin.ReopenRequested += ReopenLoginBrowser;
        BrowserLogin.RefreshRequested += () => _ = DiscoverBrowserLoginAsync();
        if (ServerAddress.Text?.Trim().Length > 0) _ = DiscoverBrowserLoginAsync();
    }

    private void StopBrowserDiscovery()
    {
        var inFlight = _browserDiscovery;
        _browserDiscovery = null;
        inFlight?.Cancel();
    }

    private async Task DiscoverBrowserLoginAsync(bool debounce = false)
    {
        StopBrowserDiscovery();
        _browserProviders = [];
        _browserProvidersAddress = null;
        var address = ServerAddress.Text?.Trim() ?? "";
        if (_client is not null || _desktopLogin is not null || !LoginView.IsVisible) return;
        BrowserLogin.IsVisible = address.Length != 0;
        if (!BrowserLogin.IsVisible) return;

        using var read = new CancellationTokenSource();
        _browserDiscovery = read;
        BrowserLogin.SetAvailability([], "正在读取此服务器的浏览器登录方式…");
        try
        {
            if (debounce) await Task.Delay(400, read.Token);
            using var client = new ConsoleClient(address);
            var providers = await client.GetDesktopOAuthProvidersAsync(read.Token);
            if (!ReferenceEquals(_browserDiscovery, read)) return;
            _browserProviders = providers;
            _browserProvidersAddress = address;
            BrowserLogin.SetAvailability(providers, providers.Count == 0 ? "此服务器未配置浏览器登录。" : null);
        }
        catch (ConsoleApiException error) when (ReferenceEquals(_browserDiscovery, read))
        {
            var message = error.StatusCode is 404 or 426 ? error.Message : "暂时无法读取浏览器登录方式。可重试，或使用本地账号。";
            BrowserLogin.SetAvailability([], message, canRefresh: error.StatusCode is not (404 or 426));
        }
        catch (Exception error) when (IsServiceError(error) && !ReferenceEquals(_browserDiscovery, read)) { }
        finally { if (ReferenceEquals(_browserDiscovery, read)) _browserDiscovery = null; }
    }

    private async Task StartBrowserLoginAsync(string providerId)
    {
        var address = ServerAddress.Text?.Trim();
        var provider = _browserProviders.FirstOrDefault(item => item.Id == providerId);
        if (_client is not null || _request is not null || _signingOut || _serverStatusOpen || _desktopLogin is not null ||
            address is null || address != _browserProvidersAddress || provider is null) return;
        var attempt = new BrowserLoginOperation(new ConsoleClient(address), address, provider);
        _desktopLogin = attempt;
        SetLoginMessage("");
        Password.Clear();
        ShowBrowserLoginStage(waiting: true);
        BrowserLogin.SetPending(address, provider.Name, "", canReopen: false);

        var signedIn = false;
        try
        {
            attempt.Request = await attempt.Client.BeginDesktopOAuthAsync(provider, attempt.Lifetime.Token);
            if (!IsCurrentBrowserLogin(attempt)) return;
            ReopenLoginBrowser();
            var result = await attempt.Client.WaitForDesktopOAuthAsync(attempt.Request, () =>
            {
                if (!IsCurrentBrowserLogin(attempt)) return;
                attempt.Claiming = true;
                BrowserLogin.SetClaiming();
                SetLoginMessage("");
            }, attempt.Lifetime.Token);
            if (!IsCurrentBrowserLogin(attempt)) return;
            ShowSignedIn(attempt.Client, result.Schools, result.AccountName);
            signedIn = true;
        }
        catch (Exception error) when (IsServiceError(error))
        {
            if (IsCurrentBrowserLogin(attempt)) SetLoginMessage(ServiceMessage(error));
        }
        finally
        {
            var current = ReferenceEquals(_desktopLogin, attempt);
            if (current)
            {
                _desktopLogin = null;
                ShowBrowserLoginStage(waiting: false);
                if (signedIn) { Activate(); FocusSchoolSelection(); }
                else { BrowserLogin.SetAvailability(_browserProviders); BrowserLogin.FocusRestore(); }
            }
            if (!signedIn)
            {
                await EndBrowserRequestAsync(attempt);
                await ReleaseLoginClientAsync(attempt.Client);
            }
            attempt.Lifetime.Dispose();
        }
    }

    private bool IsCurrentBrowserLogin(BrowserLoginOperation attempt) => ReferenceEquals(_desktopLogin, attempt) &&
        !attempt.Lifetime.IsCancellationRequested && (ServerAddress.Text?.Trim() ?? "") == attempt.Address;

    private void ReopenLoginBrowser()
    {
        if (_desktopLogin is not {Request: { } request, Claiming: false} attempt || !IsCurrentBrowserLogin(attempt)) return;
        var open = DesktopBrowserLauncher ?? SchoolAdminBrowser.Open;
        BrowserLogin.SetPending(attempt.Address, attempt.Provider.Name, request.ConfirmationCode, canReopen: true);
        SetLoginMessage(open(request.BrowserUri) ? "" : "无法打开系统浏览器。请检查默认浏览器，再重新打开。");
    }

    private async Task CancelBrowserLoginAsync(bool restoreFocus)
    {
        if (_desktopLogin is not { } attempt) return;
        _desktopLogin = null;
        attempt.Lifetime.Cancel();
        ShowBrowserLoginStage(waiting: false);
        BrowserLogin.SetAvailability(_browserProviders);
        SetLoginMessage(restoreFocus ? "已取消此窗口的浏览器登录。" : "");
        if (restoreFocus) BrowserLogin.FocusRestore();
        await EndBrowserRequestAsync(attempt);
    }

    private void ShowBrowserLoginStage(bool waiting)
    {
        SetLoginBusy(waiting);
        ServerAddress.IsEnabled = true;
        LocalSchoolLoginFields.IsVisible = LocalPasswordLoginFields.IsVisible = LoginButton.IsVisible = !waiting;
    }

    private static Task EndBrowserRequestAsync(BrowserLoginOperation attempt)
    {
        return attempt.Request is { } request
            ? attempt.ServerCancellation ??= SendCancelAsync(attempt.Client, request)
            : Task.CompletedTask;

        static async Task SendCancelAsync(ConsoleClient client, DesktopOAuthRequest request)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await client.CancelDesktopOAuthAsync(request, timeout.Token); }
            catch (Exception error) when (IsServiceError(error) || error is ObjectDisposedException) { }
        }
    }

    private static async Task ReleaseLoginClientAsync(ConsoleClient client)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await client.LogoutAsync(timeout.Token); }
        catch (Exception error) when (IsServiceError(error)) { }
        finally { client.Dispose(); }
    }
}
