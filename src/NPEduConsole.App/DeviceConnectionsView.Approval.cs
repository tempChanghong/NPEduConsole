using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class DeviceConnectionsView
{
    internal sealed record TrackedApproval(ConnectionApproval Value, string TermId, string TermName);

    private sealed record ApprovalChoice(TrackedApproval Record)
    {
        public override string ToString() => $"{ScreenLabel(Record.Value.Screen)} · 申请 {ShortId(Record.Value.PairingId.ToString("D"))}";
    }

    private sealed record ApprovalScreenChoice(ConnectionScreen Screen)
    {
        public override string ToString() => ScreenLabel(Screen);
    }

    private ConnectionCandidate? _candidate;
    private ConnectionApprovalState? _approval;
    private bool _approvalChecked;
    private bool _narrowApproval, _showOriginalApproval, _allowOtherApprovalScreens, _selectingApproval, _confirmApprovalExit;
    private bool HasApprovalDraft => _candidate?.State == "PENDING" || !string.IsNullOrWhiteSpace(ConnectionCode.Text);
    private InitialTarget? FixedApprovalTarget => _allowOtherApprovalScreens ? null : _initialTarget;
    private TrackedApproval? SelectedApproval => (ConnectionApprovalHistory.SelectedItem as ApprovalChoice)?.Record;
    private ConnectionApproval? CurrentApproval => SelectedApproval?.Value;
    private ConnectionScreen? SelectedApprovalScreen => (ConnectionApprovalScreens.SelectedItem as ApprovalScreenChoice)?.Screen;
    private ConnectionScreen? FixedApprovalScreen => FixedApprovalTarget is { } target && _catalog?.Term?.Id == target.TermId
        ? _catalog.Screens.SingleOrDefault(screen => screen.Id == target.ScreenId && screen.ClassId == target.ClassId)
        : null;

    private static string ShortId(string value) => value[Math.Max(0, value.Length - 6)..];
    private static string ScreenLabel(ConnectionScreen screen) => $"{screen.ClassName} · {screen.Name} · #{ShortId(screen.Id)}";

    private bool FitsApprovalScope(TrackedApproval record) => FixedApprovalTarget is not { } target ||
        target.TermId == record.TermId && target.ScreenId == record.Value.Screen.Id && target.ClassId == record.Value.Screen.ClassId;

    private void SelectApproval(ConnectionApproval? approval)
    {
        var choices = _pending.Approvals.Values.Reverse().Where(FitsApprovalScope).Select(item => new ApprovalChoice(item)).ToArray();
        _selectingApproval = true;
        ConnectionApprovalHistory.ItemsSource = choices;
        ConnectionApprovalHistory.SelectedItem = choices.FirstOrDefault(choice => choice.Record.Value == approval);
        _selectingApproval = false;
        _approval = null;
        _approvalChecked = false;
        ApprovalResultDetails.IsExpanded = false;
        ApprovalResultScroll.Offset = default;
    }

    private void ApprovalHistoryChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!_ready || _closed || _selectingApproval) return;
        _showOriginalApproval = true;
        _approval = null;
        _approvalChecked = false;
        ApprovalResultDetails.IsExpanded = false;
        ApprovalResultScroll.Offset = default;
        ClearCandidate();
        Render();
    }

    private void SetApprovalScreens()
    {
        var screens = _catalog?.Screens ?? [];
        if (FixedApprovalTarget is not null)
            screens = FixedApprovalScreen is { } fixedScreen ? [fixedScreen] : [];
        if (_candidate?.ScreenBindingId is { } bound)
            screens = screens.Where(row => row.Id == bound).ToArray();
        else
            screens = screens.Where(row => row.Device is null).ToArray();
        var choices = screens.Select(screen => new ApprovalScreenChoice(screen)).ToArray();
        ConnectionApprovalScreens.ItemsSource = choices;
        ConnectionApprovalScreens.SelectedItem = _candidate is not null &&
            (_candidate.ScreenBindingId is not null || FixedApprovalTarget is not null) ? choices.SingleOrDefault() : null;
    }

    private void ApprovalScreenChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!_ready) return;
        ConnectionApprovalConfirmed.IsChecked = false;
        Render();
    }

    private void CodeChanged(object? sender, TextChangedEventArgs args)
    {
        if (!_ready || _closed) return;
        if (string.IsNullOrWhiteSpace(ConnectionCode.Text))
        {
            Render();
            return;
        }
        _showOriginalApproval = false;
        _confirmApprovalExit = false;
        if (_candidate is not null)
        {
            _candidate = null;
            ConnectionCandidateInfo.Text = "";
            SetApprovalScreens();
        }
        Render();
    }

    private void ApprovalNewStepClicked(object? sender, RoutedEventArgs args)
    {
        _showOriginalApproval = false;
        SetMessage(null);
        Render();
        ConnectionCode.Focus();
    }

    private void ApprovalExistingStepClicked(object? sender, RoutedEventArgs args)
    {
        ClearCandidate();
        _showOriginalApproval = true;
        SetMessage(null);
        Render();
        ConnectionApprovalHistory.Focus();
    }

    private void ApprovalChooseOtherClicked(object? sender, RoutedEventArgs args)
    {
        _allowOtherApprovalScreens = true;
        ClearCandidate();
        SelectApproval(null);
        ApprovalNewStepClicked(sender, args);
    }

    private void ApprovalDiscardClicked(object? sender, RoutedEventArgs args)
    {
        ClearCandidate();
        CloseRequested?.Invoke();
    }

    private async void ResolveClicked(object? sender, RoutedEventArgs args)
    {
        if (_catalog is not { Term: not null } catalog || FixedApprovalTarget is not null && FixedApprovalScreen is null) return;
        string code = ConnectionCode.Text ?? "";
        await RunAsync(async ct =>
        {
            ClearCandidate();
            var candidate = await _client.ResolveConnectionCodeAsync(catalog, code, ct);
            ct.ThrowIfCancellationRequested();
            if (FixedApprovalTarget is { } target && candidate.ScreenBindingId is { } bound && bound != target.ScreenId)
            {
                string other = catalog.Screens.SingleOrDefault(screen => screen.Id == bound) is { } screen
                    ? ScreenLabel(screen) : $"绑定 #{ShortId(bound)}";
                SetMessage($"此短码指向 {other}。当前固定为 {target.ClassName} · {target.ScreenName}，无法在这里批准。");
                return;
            }
            if (_pending.Approvals.TryGetValue(candidate.PairingId, out var existing))
            {
                if (!FitsApprovalScope(existing))
                {
                    SetMessage("该短码的原申请属于另一办理范围。请从全校目录按原申请核对。");
                    return;
                }
                SelectApproval(existing.Value);
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
                SelectedApprovalScreen is null)
                SetMessage("申请指向的大屏不在当前可办理目录。请现场核对原学期与大屏。");
            else if (SelectedApprovalScreen is { Device: not null })
                SetMessage("原大屏已有有效配对。如需接替，请用现有换机流程。");
            else if (candidate.State == "PENDING")
                (candidate.Source == "CLASS_APPLY" || FixedApprovalTarget is not null
                    ? (Control)ConnectionApprovalConfirmed : ConnectionApprovalScreens).Focus();
        });
    }

    private bool CanApprove() =>
        _candidate is { State: "PENDING" } candidate && !_pending.Approvals.ContainsKey(candidate.PairingId) &&
        SelectedApprovalScreen is { Device: null } screen && (FixedApprovalTarget is null || screen == FixedApprovalScreen) &&
        ConnectionApprovalConfirmed.IsChecked == true;

    private async void ApproveClicked(object? sender, RoutedEventArgs args)
    {
        if (!CanApprove() || _catalog is not { } catalog || _candidate is not { } candidate ||
            catalog.Term is not { } term || SelectedApprovalScreen is not { } screen) return;
        await RunAsync(async ct =>
        {
            var pending = new ConnectionApproval(candidate.PairingId, Guid.NewGuid(), _school.Id, screen);
            _pending.Approvals.Add(pending.PairingId, new(pending, term.Id, term.Name));
            SelectApproval(pending);
            try
            {
                await _client.ApproveConnectionAsync(catalog, candidate, screen, pending, ct);
                Changed = true;
                SetMessage("学校已批准。请现场在 NPEduTools 确认，随后核对上报。");
            }
            catch (ConsoleApiException error) when (error.Code == "CONNECTION_UNCONFIRMED")
            {
                SetMessage("批准结果待核对。正在按原申请读取。");
            }
            catch (ConsoleApiException error)
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
            SetMessage(null);
        }, writing: true);
    }

    private void ClearCandidate()
    {
        _candidate = null;
        _confirmApprovalExit = false;
        ConnectionCode.Clear();
        ConnectionCandidateInfo.Text = "";
        ConnectionApprovalConfirmed.IsChecked = false;
        SetApprovalScreens();
    }

    private async void ReadApprovalClicked(object? sender, RoutedEventArgs args) =>
        await RunAsync(ct => ReadApprovalAsync(ct));

    private async Task ReadApprovalAsync(CancellationToken ct, bool revealResult = true)
    {
        if (CurrentApproval is not { } pending) return;
        if (revealResult)
        {
            _showOriginalApproval = true;
            Render();
        }
        _approval = null;
        _approvalChecked = false;
        var result = await _client.ReadConnectionApprovalAsync(pending, ct);
        ct.ThrowIfCancellationRequested();
        _approval = result;
        _approvalChecked = true;
        if (result is not null) Changed = true;
        if (!revealResult) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_closed && ReferenceEquals(CurrentApproval, pending) && ConnectionTabs.SelectedIndex == 1)
                ApprovalResultScroll.Offset = default;
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
            catch (ConsoleApiException error) when (error.StatusCode == 410 || error.Code == "PAIRING_STATE_CONFLICT")
            {
                SetMessage("现场申请状态已变化，正在核对原申请。");
            }
            catch (ConsoleApiException error) when (error.Code == "CONNECTION_UNCONFIRMED")
            {
                SetMessage("取消结果待核对。正在按原申请读取。");
            }
            EnableReadWhileChecking();
            await ReadApprovalAsync(ct);
            SetMessage(null);
        }, writing: true);
    }

    private void FinishApprovalClicked(object? sender, RoutedEventArgs args)
    {
        SelectApproval(null);
        ApprovalNewStepClicked(sender, args);
    }

    private void RenderApproval(int tab)
    {
        var pending = CurrentApproval;
        ConnectionTabs.Classes.Set("approval-active", tab == 1);
        var fixedScreen = FixedApprovalScreen;
        var fixedTarget = FixedApprovalTarget;
        bool canResolve = _catalog?.Term is not null && (fixedTarget is null || fixedScreen is not null);
        ConnectionCode.IsEnabled = canResolve;
        ConnectionResolve.IsEnabled = canResolve && !string.IsNullOrWhiteSpace(ConnectionCode.Text);
        ApprovalCandidateDetails.IsVisible = _candidate is not null;
        ConnectionCandidateInfo.IsVisible = _candidate is not null;
        ConnectionApprovalScreens.IsVisible = fixedTarget is null && _candidate is { Source: "ADMIN", State: "PENDING", ScreenBindingId: null };
        ConnectionApprovalScreens.IsEnabled = ConnectionApprovalScreens.IsVisible;
        ConnectionApprovalConfirmed.IsVisible = _candidate?.State == "PENDING";
        ConnectionApprovalConfirmed.IsEnabled = _candidate?.State == "PENDING" &&
            SelectedApprovalScreen is { Device: null };
        ConnectionApprovalTarget.Text = SelectedApprovalScreen is { } screen
            ? $"批准目标 · {ScreenLabel(screen)}" : _candidate?.ScreenBindingId is { } id ? $"原大屏绑定 #{ShortId(id)}" : "";
        if (SelectedApprovalScreen is { } approvalTarget) ToolTip.SetTip(ConnectionApprovalTarget, approvalTarget.Id);
        ConnectionApprove.IsVisible = tab == 1 && (!_narrowApproval || !_showOriginalApproval) && _candidate?.State == "PENDING";
        ConnectionApprove.IsEnabled = !_busy && CanApprove();
        ApprovalDiscardButton.IsVisible = tab == 1 && _confirmApprovalExit && HasApprovalDraft;
        ApprovalDiscardButton.IsEnabled = !_busy;
        ConnectionApprovalCard.IsVisible = pending is not null;
        ConnectionCancelApproval.IsVisible = _approval is { State: "APPROVED", BindingCurrent: true };
        int count = ConnectionApprovalHistory.ItemCount;
        ConnectionApprovalHistory.IsVisible = count != 0;
        ApprovalExistingHeading.Text = count == 0 ? "原申请" : $"原申请 · {count} 条";
        ApprovalExistingStepButton.Content = count == 0 ? "原申请" : $"原申请 · {count}";
        ApprovalEmpty.IsVisible = pending is null;
        ApprovalEmpty.Text = count == 0 ? "这个范围暂没有可核对的原申请。" : "选择一条原申请，再读取批准和上报状态。";
        ApprovalScopeText.Text = fixedScreen is not null ? $"固定大屏 · {ScreenLabel(fixedScreen)}" :
            fixedTarget is null ? "全校目录 · 按短码或原申请办理" :
            $"原大屏不可办理 · {fixedTarget.ClassName} · {fixedTarget.ScreenName} · #{ShortId(fixedTarget.ScreenId)}";
        ApprovalChooseOther.IsVisible = fixedTarget is not null;
        ApprovalStepBar.IsVisible = _narrowApproval;
        ApprovalNewStepButton.Classes.Set("selected", !_showOriginalApproval);
        ApprovalExistingStepButton.Classes.Set("selected", _showOriginalApproval);
        Grid.SetColumn(ApprovalExistingPane, _narrowApproval ? 0 : 1);
        Grid.SetColumnSpan(ApprovalNewPane, _narrowApproval ? 2 : 1);
        Grid.SetColumnSpan(ApprovalExistingPane, _narrowApproval ? 2 : 1);
        ApprovalNewPane.IsVisible = !_narrowApproval || !_showOriginalApproval;
        ApprovalExistingPane.IsVisible = !_narrowApproval || _showOriginalApproval;
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
        ConnectionApprovalResult.Text = $"{ScreenLabel(pending.Screen)}\n{status}";
        ApprovalResultIdentity.Text = $"{SelectedApproval?.TermName}（原学期）\n申请 {pending.PairingId:D}\n批准请求 {pending.RequestId:D}\n大屏绑定 {pending.Screen.Id}";
    }
}
