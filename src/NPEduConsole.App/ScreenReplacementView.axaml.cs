using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class ScreenReplacementView : UserControl
{
    private sealed record Scope(ConsoleClient Client, string SchoolId, string ScreenId);
    private sealed record Review(ScreenReplacement Request, ScreenReplacementDecision Decision, Guid RequestId);

    private Scope? _scope;
    private CancellationTokenSource? _operation;
    private ScreenReplacementList? _list;
    private Review? _draft, _unconfirmed;
    private Button? _draftOpener;
    private bool _canRetry;
    private string? _readError, _feedback;

    public ScreenReplacementView() => InitializeComponent();

    public event Action? CloseRequested;
    public event Action<ConsoleApiException>? SessionExpired;
    public event Action<ScreenReplacementDetail>? HandoverObserved;

    public async Task OpenAsync(ConsoleClient client, string schoolId, string screenId, string label)
    {
        Reset();
        var scope = _scope = new(client, schoolId, screenId);
        ReplacementScope.Text = label;
        IsVisible = true;
        await RefreshAsync();
        if (ReferenceEquals(_scope, scope)) RefreshReplacementButton.Focus();
    }

    public void Reset()
    {
        var operation = _operation;
        _operation = null;
        operation?.Cancel();
        _scope = null;
        _list = null;
        _draft = _unconfirmed = null;
        _draftOpener = null;
        _canRetry = false;
        _readError = _feedback = null;
        IsVisible = false;
        Render();
    }

    private void CloseClicked(object? sender, RoutedEventArgs args) => CloseRequested?.Invoke();
    private async void RefreshClicked(object? sender, RoutedEventArgs args) => await RefreshAsync();
    private async void ConfirmClicked(object? sender, RoutedEventArgs args)
    {
        if (_draft is { } review) await SubmitAsync(review);
    }
    private async void RetryClicked(object? sender, RoutedEventArgs args)
    {
        if (_canRetry && _unconfirmed is { } review) await SubmitAsync(review);
    }

    private void CancelReviewClicked(object? sender, RoutedEventArgs args)
    {
        var opener = _draftOpener;
        _draft = null;
        _draftOpener = null;
        RenderReview();
        opener?.Focus();
    }

    private async Task RefreshAsync()
    {
        if (_scope is not { } scope || _operation is not null || _draft is not null) return;
        if (_unconfirmed is null) _feedback = null;
        using var operation = StartOperation();
        try { await LoadAsync(scope, operation); }
        finally { Finish(operation); }
    }

    private CancellationTokenSource StartOperation()
    {
        _operation = new();
        _list = null;
        _readError = null;
        _canRetry = false;
        Render();
        return _operation;
    }

    private void Finish(CancellationTokenSource operation)
    {
        if (_operation != operation) return;
        _operation = null;
        Render();
    }

    private async Task LoadAsync(Scope scope, CancellationTokenSource operation)
    {
        try
        {
            ScreenReplacementDetail? checkedReview = null;
            if (_unconfirmed is { } review)
            {
                try
                {
                    checkedReview = await scope.Client.GetScreenReplacementAsync(scope.SchoolId, scope.ScreenId,
                        review.Request.PairingId, operation.Token);
                }
                catch (ConsoleApiException error) when (error.StatusCode is 404 or 410)
                {
                    if (_operation != operation) return;
                    _unconfirmed = null;
                    _feedback = "这份申请已无法查询。请核对当前申请和最近一次交接。";
                }
            }

            var list = await scope.Client.GetScreenReplacementsAsync(scope.SchoolId, scope.ScreenId, operation.Token);
            if (_operation != operation) return;
            _list = list;
            if (list.LatestHandover is { } handover) HandoverObserved?.Invoke(handover);
            if (_unconfirmed is { } unresolved && checkedReview is { } detail)
                ObserveUnconfirmedReview(unresolved, detail, list);
        }
        catch (ConsoleApiException error)
        {
            if (_operation != operation || HandleExpired(error)) return;
            _readError = error.Message;
            _list = null;
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
    }

    private void ObserveUnconfirmedReview(Review review, ScreenReplacementDetail detail, ScreenReplacementList list)
    {
        // A concurrent administrator or candidate can advance the same request between the two reads.
        if (list.LatestHandover is { } latest && latest.Request.PairingId == review.Request.PairingId) detail = latest;
        if (detail.Request.ExpectedDeviceId != review.Request.ExpectedDeviceId || detail.Request.ExpectedBindingRevision != review.Request.ExpectedBindingRevision)
            _feedback = "核对到的原设备或绑定版本已变化，请查看这台大屏的最新记录。";
        else if (detail.Progress == "AWAITING_REVIEW")
        {
            _canRetry = list.Items.Any(item => item.State == "PENDING" && item.PairingId == review.Request.PairingId &&
                item.ExpectedDeviceId == review.Request.ExpectedDeviceId && item.ExpectedBindingRevision == review.Request.ExpectedBindingRevision);
            if (_canRetry)
            {
                _feedback = "已核对，同一份申请仍待审核。需要时可重试原审核。";
                return;
            }
            if (list.Items.Any(item => item.PairingId == review.Request.PairingId && item.State == "APPROVED"))
                _feedback = "已核对，该申请正在等待新机现场确认。";
            else _feedback = "核对期间申请状态已变化。请查看这台大屏的最新记录。";
        }
        else _feedback = "已核对 · " + ProgressText(detail);
        _unconfirmed = null;
    }

    private async Task SubmitAsync(Review review)
    {
        if (_scope is not { } scope || _operation is not null) return;
        _draft = _unconfirmed = null;
        _draftOpener = null;
        _feedback = "正在提交审核…";
        using var operation = StartOperation();
        try
        {
            try
            {
                var receipt = await scope.Client.ReviewScreenReplacementAsync(review.Request, review.Decision, review.RequestId, operation.Token);
                if (_operation != operation) return;
                _feedback = receipt.State switch
                {
                    "APPROVED" => "学校已批准。新机现场确认成功后才会接替旧机。",
                    "ACTIVATED" => "该申请已交接，请核对最近一次交接的首报进度。",
                    "CANCELLED" when review.Decision == ScreenReplacementDecision.Reject => "已拒绝本次换机申请。",
                    _ => "该申请已取消，请核对当前大屏。"
                };
            }
            catch (ConsoleApiException error)
            {
                if (_operation != operation || HandleExpired(error)) return;
                _feedback = error.Message;
                if (error.StatusCode == 403)
                {
                    _readError = "当前账号暂无法审核这台大屏。";
                    return;
                }
                if (error.Code == "REPLACEMENT_REVIEW_UNCONFIRMED") _unconfirmed = review;
            }
            await LoadAsync(scope, operation);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        finally { Finish(operation); }
        if (ReferenceEquals(_scope, scope)) RefreshReplacementButton.Focus();
    }

    private bool HandleExpired(ConsoleApiException error)
    {
        if (error.StatusCode != 401) return false;
        SessionExpired?.Invoke(error);
        Reset();
        return true;
    }

    private void Render()
    {
        ReplacementStatus.Text = _readError ?? (_operation is not null ? "正在核对学校换机记录…" :
            _list?.Items.Count > 0 ? "当前换机申请" : "当前没有待处理的换机申请。");
        ReplacementFeedback.Text = _feedback;
        ReplacementFeedback.IsVisible = _feedback is not null;
        ReplacementItems.Children.Clear();
        if (_list is { } list)
            foreach (var row in list.Items) ReplacementItems.Children.Add(BuildRequest(row));
        ReplacementLatest.IsVisible = _list?.LatestHandover is not null;
        ReplacementLatestText.Text = _list?.LatestHandover is { } latest
            ? $"{latest.Request.CurrentDeviceName} → {latest.Request.DeviceName}\n{ProgressText(latest)}\n新机编号 {latest.DeviceId:D}"
            : null;
        RenderReview();
    }

    private void RenderReview()
    {
        bool busy = _operation is not null;
        ReplacementItems.IsEnabled = !busy && _draft is null && _unconfirmed is null;
        RefreshReplacementButton.IsEnabled = !busy && _draft is null;
        RetryReplacementButton.IsVisible = !busy && _canRetry;
        ReplacementConfirmation.IsVisible = _draft is not null;
        if (_draft is not { } draft) return;
        bool approve = draft.Decision == ScreenReplacementDecision.Approve;
        ConfirmReplacementButton.Content = approve ? "确认批准" : "确认拒绝";
        string question = approve
            ? $"批准「{draft.Request.DeviceName}」接替「{draft.Request.CurrentDeviceName}」？新机须在现场确认成功，旧机届时才会停用。"
            : $"拒绝「{draft.Request.DeviceName}」接替「{draft.Request.CurrentDeviceName}」的申请？本次决定不会改变旧机绑定。";
        ReplacementReviewText.Text = $"{question}\n旧机编号 {draft.Request.ExpectedDeviceId:D}\n申请编号 {draft.Request.PairingId:D}";
    }

    private Control BuildRequest(ScreenReplacement item)
    {
        var row = new StackPanel { Spacing = 5 };
        row.Children.Add(new TextBlock { Text = $"{item.DeviceName} · {item.AppVersion}", Classes = { "label" }, TextWrapping = TextWrapping.Wrap });
        row.Children.Add(new TextBlock { Text = $"申请中的旧机 {item.CurrentDeviceName}（{item.ExpectedDeviceId:D}）\n申请编号 {item.PairingId:D}",
            Classes = { "muted" }, TextWrapping = TextWrapping.Wrap });
        string status = item.State == "PENDING" ? "待学校审核" : "待新机现场确认";
        row.Children.Add(new TextBlock { Text = $"{status} · 有效至 {LocalTime(item.ExpiresAt)}", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap });
        if (item.State == "PENDING")
        {
            var actions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
            actions.Children.Add(ReviewButton(item, ScreenReplacementDecision.Approve, "批准"));
            actions.Children.Add(ReviewButton(item, ScreenReplacementDecision.Reject, "拒绝"));
            row.Children.Add(actions);
        }
        return new Border { Child = row, Classes = { "card" }, Padding = new Thickness(12) };
    }

    private Button ReviewButton(ScreenReplacement item, ScreenReplacementDecision decision, string label)
    {
        var button = new Button { Content = label, Classes = { "action" } };
        AutomationProperties.SetName(button, $"{label}换机申请 {item.DeviceName}");
        AutomationProperties.SetAutomationId(button, $"ScreenReplacement{decision}-{item.PairingId:D}");
        button.Click += (_, _) =>
        {
            if (_operation is not null || _unconfirmed is not null || _draft is not null) return;
            var draft = _draft = new(item, decision, Guid.NewGuid());
            _draftOpener = button;
            RenderReview();
            Dispatcher.UIThread.Post(() =>
            {
                if (_draft != draft) return;
                ReplacementConfirmation.BringIntoView();
                ConfirmReplacementButton.Focus();
            }, DispatcherPriority.Loaded);
        };
        return button;
    }

    private static string ProgressText(ScreenReplacementDetail detail) => detail.Progress switch
    {
        "AWAITING_CONFIRMATION" => "待新机现场确认。",
        "AWAITING_REPORT" => "已交接，待新机首报。",
        "REPORTED" when detail.FirstReportedAt is { } first => $"新机已上报。首报 {LocalTime(first)}。",
        "EXPIRED" => "这份申请已过期。",
        "CANCELLED" => "这份申请已取消。",
        _ => "这份申请待学校审核。"
    };

    private static string LocalTime(DateTimeOffset value) => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
