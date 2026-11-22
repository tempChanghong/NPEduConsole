using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace NPEduConsole.App;

/// <summary>
/// 设备现场处置的一次确认卡片：宿主把目标与影响塞进来，用户按「撤销这台原生设备授权」或「取消」。
/// </summary>
/// <remarks>
/// 破坏性动作不做「回车即执行」：<see cref="Present"/> 之后焦点落在「取消」上，
/// Enter 只有在用户自己移到撤销按钮后才会发生。Esc 与「取消」等价。
/// 卡片显示/隐藏由本控件负责，宿主只处理 <see cref="Approved"/> / <see cref="Dismissed"/> 之后的流程。
/// </remarks>
public sealed partial class DeviceFieldActionConfirmation : UserControl
{
    /// <summary>用户确认撤销这台原生设备的授权。</summary>
    public event EventHandler? Approved;

    /// <summary>用户放弃本次处置（按钮、Esc 或宿主的返回动作）。</summary>
    public event EventHandler? Dismissed;

    public DeviceFieldActionConfirmation()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>卡片是否正在等待用户决定。</summary>
    public bool IsPresenting => ConfirmCard.IsVisible;

    /// <summary>当前卡片上的目标文本（便于宿主在关闭前做日志/证据）。</summary>
    public string Target => TargetText.Text ?? string.Empty;

    /// <summary>当前卡片上的影响文本。</summary>
    public string Impact => ImpactText.Text ?? string.Empty;

    /// <summary>
    /// 显示确认卡片。<paramref name="target"/> 说清是哪台屏/哪台原生设备，
    /// <paramref name="impact"/> 说清会发生什么（例如本班其他设备是否受影响、是否需要重新配对）。
    /// </summary>
    public void Present(string target, string impact)
    {
        TargetText.Text = target ?? string.Empty;
        ImpactText.Text = impact ?? string.Empty;
        ConfirmCard.IsVisible = true;
        // 默认焦点给「取消」：布局完成后再抢，避免此时按钮还没有容器。
        Dispatcher.UIThread.Post(() => DismissButton.Focus(), DispatcherPriority.Loaded);
    }

    /// <summary>收起卡片但不触发 <see cref="Dismissed"/>（宿主自己换页时用）。</summary>
    public void Clear() => ConfirmCard.IsVisible = false;

    /// <summary>把焦点放回「取消」，供宿主在重排版后恢复。</summary>
    public bool FocusCancel() => DismissButton.Focus();

    /// <summary>用户视角的「取消」：收起并通知宿主。</summary>
    public void Dismiss()
    {
        if (!ConfirmCard.IsVisible) return;
        Clear();
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (!ConfirmCard.IsVisible) return;
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Dismiss();
    }

    private void ApproveClicked(object? sender, RoutedEventArgs e)
    {
        if (!ConfirmCard.IsVisible) return;
        Clear();
        Approved?.Invoke(this, EventArgs.Empty);
    }

    private void DismissClicked(object? sender, RoutedEventArgs e) => Dismiss();
}
