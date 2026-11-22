using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

/// <summary>
/// 登录卡片里的 OAuth 区域：只负责显示和转发意图，不自行发请求、不打开浏览器。
/// 由外层页面调用 <see cref="SetAvailability"/> / <see cref="SetPending"/> / <see cref="SetClaiming"/> 驱动状态。
/// </summary>
public sealed partial class DesktopOAuthLoginView : UserControl
{
    private IReadOnlyList<DesktopOAuthProvider> _providers = Array.Empty<DesktopOAuthProvider>();
    private string? _providerId;
    private string? _availabilityMessage;
    private bool _canRefresh;
    private bool _pending;
    private bool _claiming;
    private bool _canReopen;

    public event Action<string>? LoginRequested;
    public event Action? CancelRequested;
    public event Action? ReopenRequested;
    public event Action? RefreshRequested;

    public DesktopOAuthLoginView()
    {
        InitializeComponent();
        AutomationProperties.SetName(this, "学校账号 OAuth 登录");
        DesktopOAuthProviders.SelectionChanged += (_, _) => SyncPrimary();
        DesktopOAuthProviders.KeyDown += ProviderKeyDown;
        Render();
    }

    /// <summary>可用的提供方；message 用于说明「未开启」或「读取失败」，canRefresh 决定是否给重试按钮。</summary>
    public void SetAvailability(IReadOnlyList<DesktopOAuthProvider> providers, string? message = null, bool canRefresh = false)
    {
        _providers = providers ?? Array.Empty<DesktopOAuthProvider>();
        _availabilityMessage = string.IsNullOrWhiteSpace(message) ? null : message!.Trim();
        _canRefresh = canRefresh;
        _pending = false;
        _claiming = false;
        DesktopOAuthProviders.ItemsSource = _providers;
        var keep = _providerId is not null ? _providers.FirstOrDefault(row => row.Id == _providerId) : null;
        DesktopOAuthProviders.SelectedItem = keep ?? (DesktopOAuthProviders.IsVisible ? _providers.FirstOrDefault() : null);
        Render();
    }

    /// <summary>
    /// 浏览器确认页已经打开、等待用户点击继续。核对码是请求编号前 8 位，用户不必输入，只用来对照浏览器页面。
    /// </summary>
    public void SetPending(string server, string providerName, string confirmationCode, bool canReopen)
    {
        _pending = true;
        _claiming = false;
        _canReopen = canReopen;

        var name = string.IsNullOrWhiteSpace(providerName) ? "身份提供方" : providerName.Trim();
        var code = string.IsNullOrWhiteSpace(confirmationCode) ? string.Empty : confirmationCode.Trim();
        var text = code.Length == 0 ? $"正在准备「{name}」登录请求。"
            : $"已在浏览器打开「{name}」。核对码 {code}（应与浏览器页面一致，无需输入）。在浏览器里点击确认后，这里会自动继续。";
        if (!string.IsNullOrWhiteSpace(server)) text += $"服务器：{server.Trim()}";
        DesktopOAuthPendingText.Text = text;
        AutomationProperties.SetName(DesktopOAuthPending, code.Length > 0 ? $"等待浏览器确认，核对码 {code}" : "等待浏览器确认");
        Render();
        FocusFirst();
    }

    /// <summary>
    /// 浏览器已确认、正在换取会话。此时还没收到令牌，所以仍允许取消（放弃本次登录）；
    /// 「重新打开浏览器」只在等待阶段有意义（换一轮 attempt），这里不提供。
    /// </summary>
    public void SetClaiming(string? message = null)
    {
        _pending = true;
        _claiming = true;
        _canReopen = false;
        DesktopOAuthPendingText.Text = string.IsNullOrWhiteSpace(message)
            ? "浏览器认证已完成，正在确认本窗口的登录结果及学校权限…"
            : message!.Trim();
        AutomationProperties.SetName(DesktopOAuthPending, "正在核对学校权限");
        Render();
        FocusFirst();
    }

    /// <summary>
    /// 把焦点放到当前状态下最安全的主操作。
    /// 「等待浏览器确认」与「正在核对学校权限」都还没拿到令牌，所以两者都允许在本窗口取消；
    /// 只有等待阶段才给「重新打开浏览器」（换一轮 attempt，同一个请求）。
    /// </summary>
    public bool FocusFirst()
    {
        Control? target = _claiming && Usable(DesktopOAuthCancel) ? DesktopOAuthCancel
            : _claiming && Usable(DesktopOAuthPending) ? DesktopOAuthPending
            : Usable(DesktopOAuthCancel) && _pending ? DesktopOAuthCancel
            : Usable(DesktopOAuthLogin) && !_pending ? DesktopOAuthLogin
            : Usable(DesktopOAuthProviders) && !_pending ? DesktopOAuthProviders
            : Usable(DesktopOAuthRefresh) ? DesktopOAuthRefresh
            : Usable(DesktopOAuthCancel) ? DesktopOAuthCancel
            : Usable(DesktopOAuthPending) ? DesktopOAuthPending
            : null;
        if (target is null) return false;
        var chosen = target;          // 先固定为非空，避免闭包里的可空推断（CS8602）
        Dispatcher.UIThread.Post(() =>
        {
            if (!Usable(chosen)) return;
            chosen.BringIntoView();
            chosen.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
        return true;
    }

    /// <summary>供外层在取消／返回后恢复焦点，等价于 <see cref="FocusFirst"/>。</summary>
    public bool FocusRestore() => FocusFirst();

    private void Render()
    {
        var single = _providers.Count == 1;
        var multi = _providers.Count > 1;

        DesktopOAuthPending.IsVisible = _pending;
        DesktopOAuthChoice.IsVisible = !_pending;
        DesktopOAuthProviders.IsVisible = !_pending && multi;
        DesktopOAuthLogin.IsVisible = !_pending && _providers.Count > 0;
        DesktopOAuthReopen.IsVisible = _pending && !_claiming && _canReopen;
        DesktopOAuthCancel.IsVisible = _pending;
        DesktopOAuthRefresh.IsVisible = !_pending && _canRefresh;

        DesktopOAuthLogin.Content = single ? $"使用浏览器登录 · {_providers[0].Name}" : "使用浏览器登录";

        var notice = _availabilityMessage;
        if (notice is null)
        {
            if (_providers.Count == 0)
                notice = _canRefresh ? "暂时读不到可用的 OAuth 登录方式，可以重试一次。" : "此服务器未开启桌面 OAuth 登录。";
        }

        DesktopOAuthNotice.Text = notice ?? string.Empty;
        DesktopOAuthNotice.IsVisible = notice is not null;
        DesktopOAuthNotice.Classes.Set("error", _providers.Count == 0 && _availabilityMessage is not null);

        SyncPrimary();
    }

    private void SyncPrimary()
    {
        _providerId = SelectedProviderId();
        DesktopOAuthLogin.IsEnabled = !_pending && _providers.Count > 0 && _providerId is not null;
        if (_providers.Count > 1 && DesktopOAuthProviders.SelectedItem is null && _providers.Count > 0)
            DesktopOAuthProviders.SelectedItem = _providers[0];
    }

    private string? SelectedProviderId()
    {
        if (_providers.Count == 1) return _providers[0].Id;
        return (DesktopOAuthProviders.SelectedItem as DesktopOAuthProvider)?.Id ?? _providerId;
    }

    private static bool Usable(Control? control)
        => control is not null && control.IsEffectivelyVisible && control.IsEffectivelyEnabled;

    private void ProviderKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DesktopOAuthProviders.IsDropDownOpen) return;
        e.Handled = true;
        LoginClicked(sender, e);
    }

    private void ViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_pending) return;
        e.Handled = true;
        CancelRequested?.Invoke();
    }

    private void LoginClicked(object? sender, RoutedEventArgs e)
    {
        var id = SelectedProviderId();
        if (_pending || id is null) return;
        _providerId = id;
        LoginRequested?.Invoke(id);
    }

    private void ReopenClicked(object? sender, RoutedEventArgs e)
    {
        if (!_pending || _claiming) return;
        ReopenRequested?.Invoke();
    }

    private void CancelClicked(object? sender, RoutedEventArgs e)
    {
        if (!_pending) return;
        CancelRequested?.Invoke();
    }

    private void RefreshClicked(object? sender, RoutedEventArgs e)
    {
        if (_pending) return;
        RefreshRequested?.Invoke();
    }
}
