using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class DeviceConnectionsWindow
{
    private sealed record ApprovalChoice(ConnectionApproval Value)
    {
        public override string ToString() => $"{Value.Screen.ClassName} · {Value.Screen.Name} · {Value.PairingId:D}";
    }

    private ConnectionCandidate? _candidate;
    private ConnectionApprovalState? _approval;
    private bool _approvalChecked;
    private ConnectionApproval? CurrentApproval => (ConnectionApprovalHistory.SelectedItem as ApprovalChoice)?.Value;

    private void SelectApproval(ConnectionApproval? approval)
    {
        var choices = _pending.Approvals.Values.Reverse().Select(item => new ApprovalChoice(item)).ToArray();
        ConnectionApprovalHistory.ItemsSource = choices;
        ConnectionApprovalHistory.SelectedItem = choices.FirstOrDefault(choice => choice.Value == approval);
        _approval = null;
        _approvalChecked = false;
    }

    private void ApprovalHistoryChanged(object? sender, SelectionChangedEventArgs args)
    {
        _approval = null;
        _approvalChecked = false;
        Render();
    }

    private void SetApprovalScreens()
    {
        var screens = _catalog?.Screens ?? [];
        if (_candidate?.ScreenBindingId is { } bound)
            screens = screens.Where(row => row.Id == bound).ToArray();
        else
            screens = screens.Where(row => row.Device is null).ToArray();
        ConnectionApprovalScreens.ItemsSource = screens;
        ConnectionApprovalScreens.SelectedItem = _candidate?.ScreenBindingId is not null ? screens.SingleOrDefault() : null;
    }

    private void ApprovalScreenChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!_ready) return;
        ConnectionApprovalConfirmed.IsChecked = false;
        Render();
    }

    private void CodeChanged(object? sender, TextChangedEventArgs args)
    {
        if (!_ready || string.IsNullOrEmpty(ConnectionCode.Text) || _candidate is null) return;
        _candidate = null;
        ConnectionCandidateInfo.Text = "";
        SetApprovalScreens();
        Render();
    }

    private async void ResolveClicked(object? sender, RoutedEventArgs args)
    {
        if (_catalog is not { Term: not null } catalog) return;
        string code = ConnectionCode.Text ?? "";
        await RunAsync(async ct =>
        {
            _candidate = null;
            ConnectionCandidateInfo.Text = "";
            SetApprovalScreens();
            var candidate = await _client.ResolveConnectionCodeAsync(catalog, code, ct);
            ct.ThrowIfCancellationRequested();
            ConnectionCode.Clear();
            if (_pending.Approvals.TryGetValue(candidate.PairingId, out var existing))
            {
                SelectApproval(existing);
                await ReadApprovalAsync(ct);
                return;
            }
            _candidate = candidate;
            SetApprovalScreens();
            var source = candidate.Source == "CLASS_APPLY" ? "按班级发起的接入申请" : "管理员批准配对";
            ConnectionCandidateInfo.Text = $"{source} · {candidate.DeviceName} · NPEduTools {candidate.AppVersion}\n"
                + $"有效至 {candidate.ExpiresAt.LocalDateTime:g}"
                + (candidate.State == "APPROVED" ? " · 学校已有批准，请现场核对并确认。" : "");
            if (candidate is { State: "PENDING", ScreenBindingId: not null } &&
                ConnectionApprovalScreens.SelectedItem is not ConnectionScreen)
                SetMessage("申请指向的大屏不在当前可办理目录。请现场核对原学期与大屏。");
            else if (ConnectionApprovalScreens.SelectedItem is ConnectionScreen { Device: not null })
                SetMessage("原大屏已有有效配对。如需接替，请用现有换机流程。");
            else if (candidate.State == "PENDING")
                (candidate.Source == "CLASS_APPLY" ? (Control)ConnectionApprovalConfirmed : ConnectionApprovalScreens).Focus();
        });
    }

    private bool CanApprove() =>
        _candidate is { State: "PENDING" } candidate && !_pending.Approvals.ContainsKey(candidate.PairingId) &&
        ConnectionApprovalScreens.SelectedItem is ConnectionScreen { Device: null } && ConnectionApprovalConfirmed.IsChecked == true;

    private async void ApproveClicked(object? sender, RoutedEventArgs args)
    {
        if (!CanApprove() || _catalog is not { } catalog || _candidate is not { } candidate ||
            ConnectionApprovalScreens.SelectedItem is not ConnectionScreen screen) return;
        await RunAsync(async ct =>
        {
            var pending = new ConnectionApproval(candidate.PairingId, Guid.NewGuid(), _school.Id, screen);
            _pending.Approvals.Add(pending.PairingId, pending);
            SelectApproval(pending);
            try
            {
                await _client.ApproveConnectionAsync(catalog, candidate, screen, pending, ct);
                Changed = true;
                SetMessage("学校已批准。请现场在 NPEduTools 确认，随后核对上报。");
            }
            catch (ConsoleApiException error) when (error.Code != "CONNECTION_UNCONFIRMED")
            {
                _pending.Approvals.Remove(pending.PairingId);
                SelectApproval(null);
                ConnectionApprovalConfirmed.IsChecked = false;
                if (error.Code == "MANAGEMENT_NOT_CONFIGURED")
                    SetMessage("本班尚无管理口令。请在「配对预授权」按申请所示原大屏设置，再回来批准。");
                else throw;
                return;
            }
            finally
            {
                if (_pending.Approvals.ContainsKey(pending.PairingId)) ClearCandidate();
            }
            EnableReadWhileChecking();
            await ReadApprovalAsync(ct);
        }, writing: true);
    }

    private void ClearCandidate()
    {
        _candidate = null;
        ConnectionCode.Clear();
        ConnectionCandidateInfo.Text = "";
        SetApprovalScreens();
    }

    private async void ReadApprovalClicked(object? sender, RoutedEventArgs args) => await RunAsync(ReadApprovalAsync);

    private async Task ReadApprovalAsync(CancellationToken ct)
    {
        if (CurrentApproval is not { } pending) return;
        _approval = null;
        _approvalChecked = false;
        var result = await _client.ReadConnectionApprovalAsync(pending, ct);
        ct.ThrowIfCancellationRequested();
        _approval = result;
        _approvalChecked = true;
        if (result is not null) Changed = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_closed && ReferenceEquals(CurrentApproval, pending) && ConnectionTabs.SelectedIndex == 1)
                ConnectionApprovalCard.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    private async void CancelApprovalClicked(object? sender, RoutedEventArgs args)
    {
        if (CurrentApproval is not { } pending || _approval is not { State: "APPROVED", BindingCurrent: true }) return;
        await RunAsync(async ct =>
        {
            _approval = null;
            _approvalChecked = false;
            try
            {
                await _client.CancelConnectionAsync(pending, ct);
            }
            catch (ConsoleApiException error) when (error.Code == "PAIRING_STATE_CONFLICT")
            {
                SetMessage("现场申请状态已变化，正在核对原申请。");
            }
            EnableReadWhileChecking();
            await ReadApprovalAsync(ct);
        }, writing: true);
    }

    private void FinishApprovalClicked(object? sender, RoutedEventArgs args)
    {
        SelectApproval(null);
        Render();
        ConnectionCode.Focus();
    }

    private void RenderApproval(int tab)
    {
        var pending = CurrentApproval;
        ConnectionResolve.IsEnabled = _catalog?.Term is not null;
        ConnectionCode.IsEnabled = ConnectionResolve.IsEnabled;
        ConnectionApprovalScreens.IsEnabled = _candidate is { State: "PENDING", Source: "ADMIN" };
        ConnectionApprovalConfirmed.IsEnabled = _candidate?.State == "PENDING" &&
            ConnectionApprovalScreens.SelectedItem is ConnectionScreen { Device: null };
        ConnectionApprovalTarget.Text = ConnectionApprovalScreens.SelectedItem is ConnectionScreen target
            ? $"批准目标 {target}" : _candidate?.ScreenBindingId is { } id ? $"原大屏绑定 {id}" : "";
        ConnectionApprove.IsVisible = tab == 1 && _candidate?.State == "PENDING";
        ConnectionApprove.IsEnabled = !_busy && CanApprove();
        ConnectionApprovalCard.IsVisible = pending is not null;
        ConnectionCancelApproval.IsVisible = _approval is { State: "APPROVED", BindingCurrent: true };
        ConnectionApprovalHistory.IsVisible = _pending.Approvals.Count != 0;
        if (pending is null) return;
        var status = _approval switch
        {
            { State: "CANCELLED" } => "本申请已取消。",
            { State: "EXPIRED" } => "本申请已过期。",
            { BindingCurrent: false } => "原大屏归属已变化，此结果仅供核对原申请。",
            { State: "APPROVED" } => "学校已批准，等待现场在 NPEduTools 确认。",
            { State: "ACTIVATED", DeviceCurrent: false } => "现场已确认过，原设备授权已变化。",
            { State: "ACTIVATED", ReportedAt: { } at } => $"现场已确认。已收到原设备有效上报（最近 {at.LocalDateTime:g}）。",
            { State: "ACTIVATED" } => "现场已确认，等待该设备首次有效上报。",
            _ => _approvalChecked ? "尚未查到本人的批准结果。可以继续只读核对。" : "请读取本次批准及上报状态。",
        };
        ConnectionApprovalResult.Text = $"{pending.Screen}\n申请 {pending.PairingId:D}\n{status}";
    }
}
