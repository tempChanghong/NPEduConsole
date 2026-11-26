using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace NPEduConsole.App;

/// <summary>
/// 大屏值守页的窄窗布局：主布局与主流程在 ScreenDutyView.axaml(.cs)，
/// 这里只负责「网页值守 / NPEduTools 接入」两张证据卡的并排与上下切换，
/// 以及预览转入影响确认、取消返回时的焦点落点。
/// </summary>
public sealed partial class ScreenDutyView
{
    /// <summary>低于这个宽度（DIP）就把两张证据卡改成上下排。</summary>
    private const double CompactWidth = 880;

    private bool _evidenceStacked;
    private bool _evidenceArranged;
    private bool _manageStep;

    /// <summary>Loading / SizeChanged 调用；只在真正需要时改布局，避免拖动窗口时反复重排。</summary>
    private void ArrangeDutyLayout()
    {
        var width = Bounds.Width;
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0) return;
        var stacked = width < CompactWidth;
        if (_evidenceArranged && stacked == _evidenceStacked) return;
        _evidenceStacked = stacked;
        _evidenceArranged = true;
        ArrangeEvidence(stacked);
        RenderDutySteps();
    }

    private void ArrangeEvidence(bool stacked)
    {
        var columns = DutyEvidenceGrid.ColumnDefinitions;
        if (columns is {Count: >= 2})
        {
            columns[0].Width = new GridLength(1, GridUnitType.Star);
            columns[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        }
        Grid.SetColumn(DutyNative, stacked ? 1 : 0);
        Grid.SetRow(DutyNative, stacked ? 0 : 3);
        Grid.SetRowSpan(DutyNative, stacked ? 3 : 1);
        Grid.SetColumn(DutyBrowserAside, stacked ? 1 : 0);
        Grid.SetRow(DutyBrowserAside, stacked ? 0 : 4);
        Grid.SetRowSpan(DutyBrowserAside, stacked ? 3 : 1);
        Grid.SetColumn(NativeDutyCard, stacked ? 0 : 1);
        Grid.SetRow(NativeDutyCard, stacked ? 1 : 0);
        DutyEvidenceGrid.ColumnSpacing = stacked ? 0 : 8;
        DutyEvidenceGrid.RowSpacing = stacked ? 5 : 0;
        BrowserDutyCard.Padding = NativeDutyCard.Padding = new Thickness(stacked ? 6 : 11);
        DutyDetailPanel.Classes.Set("compact", stacked);
    }

    private void RenderDutySteps()
    {
        bool manages = DutyMaintenance.IsVisible;
        DutyDetailSteps.IsVisible = _evidenceStacked && manages;
        DutyEvidenceGrid.IsVisible = !_evidenceStacked || !_manageStep || !manages;
        DutyManagePanel.IsVisible = (!_evidenceStacked || _manageStep) && manages;
        DutyStatusStep.Classes.Set("selected", !_manageStep);
        DutyManageStep.Classes.Set("selected", _manageStep);
    }

    private void DutyStatusStepClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => SetDutyStep(manage: false);
    private void DutyManageStepClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => SetDutyStep(manage: true);

    private void SetDutyStep(bool manage)
    {
        _manageStep = manage;
        DutyScroll.Offset = default;
        RenderDutySteps();
    }

    /// <summary>
    /// 预览成功后调用：把焦点交给可读的确认摘要（不是「确认」按钮本身），
    /// 这样键盘回车不会误提交；摘要不可聚焦时只滚到确认按钮，不抢焦点。
    /// </summary>
    private void FocusDutyReview()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed) return;
            AnnounceReview();
            if (DutyReviewPanel.IsEffectivelyVisible && DutyReviewPanel.Focusable)
            {
                DutyReviewPanel.BringIntoView();
                DutyReviewPanel.Focus(NavigationMethod.Tab);
                return;
            }
            if (DutyConfirm.IsEffectivelyVisible) DutyConfirm.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>摘要面板的可读名，让辅助技术一次读出「动作 + 对象 + 影响」。</summary>
    private void AnnounceReview()
    {
        if (!DutyReviewPanel.IsEffectivelyVisible) return;
        var parts = new[]
        {
            DutyReviewTitle.Text,
            DutyReviewObject.Text,
            DutyReviewPeers.Text,
            DutyReviewWeb.Text,
            DutyReviewNative.Text,
        };
        var summary = string.Join("。", parts.Where(part => !string.IsNullOrWhiteSpace(part))).Trim();
        if (summary.Length > 0) AutomationProperties.SetName(DutyReviewPanel, summary);
    }

    /// <summary>
    /// 取消/返回后把焦点交回原处（anchor 被隐藏或禁用时退到目录、主操作、返回）。
    /// </summary>
    private void RestoreDutyFocus(Control? anchor)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed) return;
            var target = Usable(anchor) ? anchor
                : Usable(DutyRows) ? DutyRows
                : Usable(DutyPrimary) ? DutyPrimary
                : (Control?)DutyBack;
            if (target is null) return;
            target.BringIntoView();
            target.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private static bool Usable(Control? control) => control is {IsEffectivelyVisible: true, IsEffectivelyEnabled: true};
}
