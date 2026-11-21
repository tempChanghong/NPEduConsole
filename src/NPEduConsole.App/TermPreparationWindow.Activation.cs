using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class TermPreparationWindow
{
    private sealed record ActivationRequest(TermPreparationCheck Check, Guid RequestId);
    internal sealed record ScreenDestination(string ClassId, string ScreenId, string TermId, bool NeedsPairing);
    private static readonly ConditionalWeakTable<ConsoleClient, Dictionary<string, ActivationRequest>> ActivationsByClient = new();

    private Dictionary<string, ActivationRequest> Activations => ActivationsByClient.GetOrCreateValue(_client);
    private ActivationRequest? PendingActivation => Activations.GetValueOrDefault(_school.Id);
    private TermPreparationCheck? _confirmation;
    private TermActivationResult? _activation;
    private Guid? _latestActivationId;
    private bool _canRepeatActivation, _activationReadFailed;

    internal bool ActivationObserved { get; private set; }
    internal ScreenDestination? ActivationDestination { get; private set; }
    internal Func<Window, string, string, Task<ScreenDestination?>>? OpenPlacement { get; init; }

    private bool CanConfirmActivation => _check is {ActivationProof: not null, BlockingDiagnostics.Length: 0};

    private async Task OpenActivationActionAsync()
    {
        if (PendingActivation is { } pending)
        {
            if (_canRepeatActivation) await RunAsync(() => SubmitActivationAsync(pending));
            return;
        }
        if (!CanConfirmActivation || _check is not { } check) return;
        if (_confirmation != check)
        {
            _confirmation = check;
            Render();
            Focus(TermConfirmCancel);
            return;
        }
        _confirmation = null;
        var operation = new ActivationRequest(check, Guid.NewGuid());
        Activations[_school.Id] = operation;
        await RunAsync(() => SubmitActivationAsync(operation));
    }

    private void CancelActivationConfirmation(object? sender, RoutedEventArgs args)
    {
        _confirmation = null;
        Render();
        Focus(TermPrimary);
    }

    private async Task SubmitActivationAsync(ActivationRequest pending)
    {
        _canRepeatActivation = false;
        _writing = true;
        ActivationObserved = true;
        Notice("正在启用学期并核对回执…");
        Render();
        TermActivationResult result;
        try { result = await _client.ActivateTermAsync(_school.Id, pending.Check, pending.RequestId, _lifetime.Token); }
        catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or >= 500 || error.Code == "TERM_ACTIVATION_REQUEST_CONFLICT")
        {
            if (!_closed) await VerifyActivationAsync(pending);
            return;
        }
        catch (ConsoleApiException error) when (error.Code is "TERM_ACTIVATION_CHANGED" or "TERM_NOT_READY")
        {
            if (_closed) return;
            Activations.Remove(_school.Id);
            ActivationObserved = true;
            await ReadCheckAsync(pending.Check.Term.Id);
            if (!_closed) Notice(error.Message, true);
            return;
        }
        catch (ConsoleApiException)
        {
            if (!_closed) Activations.Remove(_school.Id);
            throw;
        }
        if (_closed) return;
        Activations.Remove(_school.Id);
        if (result.State == "APPLIED")
        {
            ShowActivationResult(result);
            return;
        }

        _check = result.Check;
        _activeTerm = _check?.ActiveTerm;
        ShowCheck();
        var changes = result.Changes!;
        Notice("本次未启用。请核对已变化的项目，再确认。", true);
        TermActivationChanges.ItemsSource = changes;
        TermActivationChanges.IsVisible = true;
        ActivationObserved = true;
        TermScroll.Offset = default;
        Focus(TermRefresh);
    }

    private async Task VerifyActivationAsync(ActivationRequest pending)
    {
        _canRepeatActivation = false;
        Render();
        var result = await _client.GetTermActivationReceiptAsync(_school.Id, pending.RequestId, pending.Check.Term.Id, _lifetime.Token);
        if (_closed) return;
        if (result is not null)
        {
            Activations.Remove(_school.Id);
            ShowActivationResult(result);
            return;
        }

        _canRepeatActivation = true;
        Notice("服务器暂未找到本次回执。可继续只读核对，或以同一编号重试原先确认的操作。", true);
        Focus(TermRefresh);
    }

    private async Task RefreshActivationAsync(Guid requestId, string? termId = null)
    {
        var prior = TermActivatedScreens.SelectedItem as TermActivatedScreen;
        try
        {
            var result = await _client.GetTermActivationReceiptAsync(_school.Id, requestId, termId, _lifetime.Token);
            if (_closed) return;
            if (result is null)
            {
                InvalidateActivationScreenStatus();
                Notice("服务器暂未找到该回执。请重新读取学期列表。", true);
                return;
            }
            ShowActivationResult(result, prior?.BindingId);
        }
        catch (ConsoleApiException error) when (error.StatusCode is not (401 or 403))
        {
            if (_closed) return;
            InvalidateActivationScreenStatus();
            Notice("历史启用回执仍可见。当前大屏状态读取失败，请刷新后办理。", true);
        }
    }

    private void InvalidateActivationScreenStatus()
    {
        _activationReadFailed = true;
        if (_activation?.Receipt is not { } history) return;
        TermActivatedSummary.Text = $"本次改绑 {history.ReboundScreens} 台 · 当前待处理数量待核对";
        TermActivatedScreens.ItemsSource = history.Screens.Select(row => row with {Status = "VERIFY"}).ToArray();
    }

    private async void OpenLatestActivation(object? sender, RoutedEventArgs args)
    {
        if (_latestActivationId is { } requestId) await RunAsync(() => RefreshActivationAsync(requestId));
    }

    private void ShowActivationResult(TermActivationResult result, string? selectedBinding = null)
    {
        var receipt = result.Receipt!;
        var rows = result.Screens!;
        _activation = result;
        _activationReadFailed = false;
        _latestActivationId = receipt.RequestId;
        _activeTerm = result.ActiveTerm;
        _dirty = false;
        ActivationObserved = true;
        TermActivatedName.Text = $"已启用 · {receipt.Term.Name}";
        TermArchivedName.Text = receipt.ArchivedTerm is { } archived ? $"已归档 · {archived.Name}" : "本次没有原启用学期待归档。";
        TermActivatedSummary.Text = $"本次改绑 {receipt.ReboundScreens} 台 · 当前待处理 {rows.Count(row => row.NeedsAttention)} 台";
        TermActivatedScreens.ItemsSource = rows;
        TermActivatedScreens.SelectedItem = rows.FirstOrDefault(row => row.BindingId == selectedBinding) ??
            rows.FirstOrDefault(row => row.NeedsAttention);
        SetPage(Page.ActivationResult);
        Notice(result.ActiveTerm?.Id == receipt.Term.Id ? null : "这是历史启用回执。全校实际启用学期见上方。");
        Focus(TermRefresh);
    }

    private void ActivationSelectionChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs args) => Render();

    private async Task OpenActivationScreenAsync()
    {
        if (_activationReadFailed || TermActivatedScreens.SelectedItem is not TermActivatedScreen row) return;
        if (row.HasPlacement && row.Placement is {BindingConfirmed: true} placed && _activeTerm is { } activePlacement)
        {
            ActivationDestination = new(placed.TargetClass.Id, row.BindingId, activePlacement.Id, placed.Status == "AWAITING_PAIRING");
            Close();
            return;
        }
        if (row.Status == "RETAINED_ORIGINAL" && row.FromClass is { } original)
        {
            if (_activation is {PlacementSupported: true, Receipt: { } activation} && OpenPlacement is { } open)
            {
                await RunAsync(async () =>
                {
                    var target = await open(this, activation.Term.Id, row.BindingId);
                    if (_closed) return;
                    if (target is not null)
                    {
                        ActivationDestination = target;
                        Close();
                        return;
                    }
                    await RefreshActivationAsync(activation.RequestId, activation.Term.Id);
                });
                return;
            }
            var link = SchoolAdminBrowser.Link(_catalog?.AdminUrl, _school.Id, original.TermId, "screens", row.BindingId, original.Id);
            if (link is not null && SchoolAdminBrowser.Open(link)) return;
            Notice($"请到网页学校管理的大屏页核对 {row.Name}（{row.BindingId}）。", true);
            return;
        }
        if (!row.Rebound || row.TargetClass is not { } target || _activation is not {Receipt: { } receipt, ActiveTerm: { } active} ||
            receipt.Term.Id != active.Id) return;
        ActivationDestination = new(target.Id, row.BindingId, active.Id, row.Status is "REPAIR_REQUIRED" or "AWAITING_PAIRING");
        Close();
    }

    private void RenderActivation()
    {
        bool pending = PendingActivation is not null;
        TermActivationResultView.IsVisible = _page == Page.ActivationResult;
        TermLatestActivation.IsVisible = _page == Page.Directory && _latestActivationId is not null && !pending;
        TermLatestActivation.IsEnabled = !_busy;

        TermConfirmCancel.IsVisible = _confirmation is not null;
        TermConfirmCancel.IsEnabled = !_busy;
        TermActivationHeadline.IsVisible = _page == Page.Check && _check is not null;
        if (_page == Page.Check && _check is { } check)
        {
            var count = check.Screens.Count(row => row.NativeImpact != "NO_REBIND" || row.TargetClass is null);
            TermActivationHeadline.Text = $"{(_confirmation is not null ? "确认启用 " : "将启用 ")}{check.Term.Name}" +
                (check.ActiveTerm is { } current ? $"，并归档 {current.Name}" : "（当前无启用学期）") +
                $"。启用后有 {count} 台大屏需处理或核对。";
        }
        if (_page == Page.Check && _check?.ActivationProof is not null && !pending)
        {
            TermPrimary.IsVisible = true;
            TermPrimary.IsEnabled = !_busy && CanConfirmActivation;
            TermPrimary.Content = _confirmation is null ? "启用此学期" : "确认启用";
        }
        if (_confirmation is not null)
        {
            TermWeb.IsVisible = false;
            TermBack.IsEnabled = false;
        }
        if (pending)
        {
            TermPrimary.IsVisible = _canRepeatActivation;
            TermPrimary.IsEnabled = !_busy && _canRepeatActivation;
            TermPrimary.Content = "按原编号重试启用";
            TermBack.Content = "关闭，稍后核对";
            TermRefresh.Content = "核对启用回执";
            TermWeb.IsVisible = false;
        }
        if (_page != Page.ActivationResult) return;

        var row = TermActivatedScreens.SelectedItem as TermActivatedScreen;
        TermPrimary.IsVisible = row is not null;
        TermPrimary.IsEnabled = !_busy && !_activationReadFailed && row is not null &&
            (row.Status == "RETAINED_ORIGINAL" || (row.Rebound || row.Placement?.BindingConfirmed == true) &&
                _activation?.Receipt?.Term.Id == _activeTerm?.Id);
        TermPrimary.Content = row?.Status == "RETAINED_ORIGINAL" ?
            _activation?.PlacementSupported == true ? "为这台屏安排新班" : "网页安排这台屏" :
            (row?.Placement?.Status ?? row?.Status) is "REPAIR_REQUIRED" or "AWAITING_PAIRING" ? "打开大屏接入" : "查看选中大屏";
        TermRefresh.Content = "刷新办理状态";
        TermBack.Content = "返回学期管理";
    }
}
