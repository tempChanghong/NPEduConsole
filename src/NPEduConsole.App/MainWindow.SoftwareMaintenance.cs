using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer _softwarePoll = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly Dictionary<(string SchoolId, string DeviceId), SoftwareFeedback> _softwareFeedback = new();
    private readonly Dictionary<(string SchoolId, string DeviceId), SoftwareReceiptProjection> _softwareHistory = new();
    private ConsoleClient? _softwareHistoryClient;
    private CancellationTokenSource? _softwareRead;
    private CancellationTokenSource? _softwareSession;
    private SoftwareUpdate? _software;
    private string? _softwareError;
    private bool _softwareErrorAllowsHistory;
    private bool _softwareReadRetryable;
    private bool _softwareReceiptExpanded;
    private const string LocalRecoveryHelp = "请在这台大屏打开已安装的 NPDownloader。若本机显示「恢复上次升级」，按本机提示处理。该入口仅在本机有可恢复记录时出现。";

    private sealed record SoftwareAttempt(SoftwareUpdate Snapshot, SoftwareUpdateAction Action, string? TargetVersion, Guid Id);

    private sealed record SoftwareFeedback(string Message, bool Writing = false, SoftwareAttempt? Pending = null,
        Guid? ConflictingInvitation = null, WithdrawalState? WithdrawalResult = null, bool ReviewInvitation = false,
        bool RetryVerified = false);

    private sealed record SoftwareReceiptProjection(
        string? PlanId,
        string? State,
        string? Reason,
        string? PreviousVersion,
        string? TargetVersion,
        DateTimeOffset? PlanUpdatedAt,
        SoftwareRollbackConfirmation? RollbackConfirmation,
        string? ObservedVersion,
        DateTimeOffset? LastSeenAt,
        DateTimeOffset CapturedAt);

    private readonly record struct WithdrawalState(long Revision, string Enrollment, bool Managed, Guid? EnrollmentId)
    {
        public WithdrawalState(SoftwareUpdate snapshot) : this(snapshot.Revision, snapshot.Enrollment, snapshot.Managed, snapshot.EnrollmentId) { }
    }

    private SoftwareFeedback? CurrentSoftwareFeedback => EquipmentKey is { } key ? _softwareFeedback.GetValueOrDefault(key) : null;

    private async void OpenSoftwareMaintenanceClicked(object? sender, RoutedEventArgs args)
    {
        if (EquipmentKey is not { } key || SoftwareMaintenanceCard.IsVisible) return;
        CloseScreenReplacement();
        SoftwareMaintenanceScope.Text = EquipmentScopeText;
        SoftwareMaintenanceCard.IsVisible = true;
        RenderSoftwareMaintenance();
        ShowSoftwareMaintenance(key);
        await ReadSoftwareAsync();
        if (SoftwareMaintenanceCard.IsVisible && EquipmentKey == key)
        {
            RefreshSoftwareButton.Focus();
            ShowSoftwareMaintenance(key);
        }
    }

    private void ShowSoftwareMaintenance((string SchoolId, string DeviceId) key) => Dispatcher.UIThread.Post(() =>
    {
        if (SoftwareMaintenanceCard.IsVisible && EquipmentKey == key) SoftwareMaintenanceCard.BringIntoView();
    }, DispatcherPriority.Loaded);

    private void CloseSoftwareMaintenanceClicked(object? sender, RoutedEventArgs args)
    {
        CloseSoftwareMaintenance();
        OpenSoftwareMaintenanceButton.Focus();
    }

    private void CloseSoftwareMaintenance()
    {
        var read = _softwareRead;
        _softwareRead = null;
        read?.Cancel();
        _softwarePoll.Stop();
        StopSoftwarePreflightClock();
        _software = null;
        _softwareError = null;
        _softwareErrorAllowsHistory = false;
        _softwareReadRetryable = false;
        _softwareReceiptExpanded = false;
        SoftwareReleasePicker.ItemsSource = null;
        SoftwarePilotPreflightSection.IsVisible = false;
        SoftwareReceiptSection.IsVisible = false;
        SoftwareReceiptDetails.IsVisible = false;
        SoftwareMaintenanceCard.IsVisible = false;
    }

    private void CancelSoftwareSession()
    {
        _softwareSession?.Cancel();
        _softwareSession?.Dispose();
        _softwareSession = null;
        StopSoftwarePreflightClock();
        _softwareFeedback.Clear();
        _softwareHistory.Clear();
        _softwareHistoryClient = null;
        _softwareErrorAllowsHistory = false;
        _softwareReadRetryable = false;
        _softwareReceiptExpanded = false;
    }

    private void ToggleSoftwareReceiptClicked(object? sender, RoutedEventArgs args)
    {
        _softwareReceiptExpanded = !_softwareReceiptExpanded;
        RenderSoftwareMaintenance();
        if (_softwareReceiptExpanded && EquipmentKey is { } key) ShowSoftwareMaintenance(key);
    }

    private static bool ShouldClearSoftwareHistory(Exception error) => error is ConsoleApiException
    {
        StatusCode: 401 or 403 or 404 or 409 or 426
    };

    private static bool CanUseSoftwareHistoryOnError(Exception error) => error switch
    {
        ConsoleApiException { StatusCode: 0 or 408 or 429 or >= 500 } => true,
        HttpRequestException or OperationCanceledException => true,
        _ => false
    };

    private void SaveSoftwareHistory(ConsoleClient client, (string SchoolId, string DeviceId) key, SoftwareUpdate snapshot)
    {
        if (_softwareHistoryClient is not null && !ReferenceEquals(_softwareHistoryClient, client))
            _softwareHistory.Clear();
        _softwareHistoryClient = client;
        if (snapshot.Plan is null && snapshot.ObservedVersion is null)
        {
            _softwareHistory.Remove(key);
            return;
        }
        _softwareHistory[key] = CreateSoftwareReceiptProjection(snapshot, DateTimeOffset.Now);
    }

    private SoftwareReceiptProjection? GetSoftwareHistory(ConsoleClient? client, (string SchoolId, string DeviceId) key) =>
        client is not null && ReferenceEquals(_softwareHistoryClient, client)
            ? _softwareHistory.GetValueOrDefault(key)
            : null;

    private void ClearSoftwareHistory(ConsoleClient? client, (string SchoolId, string DeviceId) key)
    {
        if (client is not null && ReferenceEquals(_softwareHistoryClient, client))
            _softwareHistory.Remove(key);
    }

    private static SoftwareReceiptProjection CreateSoftwareReceiptProjection(SoftwareUpdate snapshot, DateTimeOffset capturedAt)
    {
        var plan = snapshot.Plan;
        var confirmation = snapshot.IsEnrolled ? plan?.RollbackConfirmation : null;
        return new SoftwareReceiptProjection(
            plan?.Id,
            plan?.State,
            plan?.Reason,
            plan?.ValidPreviousVersion,
            plan?.TargetVersion,
            plan?.UpdatedAt,
            confirmation,
            snapshot.ObservedVersion,
            snapshot.LastSeenAt,
            capturedAt);
    }

    private async void RefreshSoftwareClicked(object? sender, RoutedEventArgs args) => await ReadSoftwareAsync(userRefresh: true);
    private async void PollSoftware(object? sender, EventArgs args) => await ReadSoftwareAsync();
    private async void InviteSoftwareClicked(object? sender, RoutedEventArgs args) => await SaveSoftwareAsync(SoftwareUpdateAction.Enroll);
    private async void WithdrawInvitationClicked(object? sender, RoutedEventArgs args) =>
        await SaveSoftwareAsync(SoftwareUpdateAction.WithdrawInvitation);
    private async void ApproveSoftwareClicked(object? sender, RoutedEventArgs args) =>
        await SaveSoftwareAsync(SoftwareUpdateAction.Approve, SoftwareReleasePicker.SelectedItem as string);
    private async void CancelSoftwareClicked(object? sender, RoutedEventArgs args) => await SaveSoftwareAsync(SoftwareUpdateAction.Cancel);
    private async void RetrySoftwareClicked(object? sender, RoutedEventArgs args)
    {
        if (CurrentSoftwareFeedback?.Pending is { } pending)
            await SaveSoftwareAsync(pending.Action, pending.TargetVersion, pending);
    }
    private void SoftwareReleaseChanged(object? sender, SelectionChangedEventArgs args) =>
        ApproveSoftwareButton.IsEnabled = CanEditSoftware && SoftwareReleasePicker.SelectedItem is string;

    private async Task ReadSoftwareAsync(bool userRefresh = false)
    {
        if (!SoftwareMaintenanceCard.IsVisible || EquipmentKey is not { } key || _client is not { } client ||
            _softwareRead is not null || CurrentSoftwareFeedback?.Writing == true) return;
        using var request = new CancellationTokenSource();
        _softwareRead = request;
        RenderSoftwareMaintenance();
        try
        {
            var snapshot = await client.GetSoftwareUpdateAsync(key.SchoolId, key.DeviceId, request.Token);
            if (_softwareRead != request || _client != client || EquipmentKey != key) return;
            _software = snapshot;
            _softwareError = null;
            _softwareErrorAllowsHistory = false;
            _softwareReadRetryable = false;
            SaveSoftwareHistory(client, key, snapshot);
            var feedback = CurrentSoftwareFeedback;
            if (feedback?.ConflictingInvitation is { } invitation)
                _softwareFeedback[key] = ReviewWithdrawalConflict(snapshot, invitation);
            else if (feedback?.WithdrawalResult is { } result)
            {
                var current = new WithdrawalState(snapshot);
                if (feedback.ReviewInvitation && !userRefresh && snapshot.CanWithdraw)
                {
                    if (current != result)
                        _softwareFeedback[key] = ReplacementInvitationFeedback(current);
                }
                else if (current != result || feedback.ReviewInvitation)
                    _softwareFeedback.Remove(key);
            }
            else if (feedback?.Pending is { } pending)
            {
                if (snapshot.Revision > pending.Snapshot.Revision)
                    _softwareFeedback[key] = new("学校状态已更新。请核对当前状态，单凭此次刷新无法确认原请求。");
                else
                {
                    bool retryVerified = (userRefresh || feedback.RetryVerified) && CanRetrySoftwareAttempt(pending, snapshot);
                    _softwareFeedback[key] = feedback with
                    {
                        RetryVerified = retryVerified,
                        Message = retryVerified ? "上次操作结果尚未确认。可按原编号重试同一操作。" : PendingSoftwareMessage
                    };
                }
            }
        }
        catch (Exception error) when (IsServiceError(error))
        {
            if (_softwareRead != request || _client != client || EquipmentKey != key || ReturnToLoginIfExpired(error)) return;
            _software = null;
            if (ShouldClearSoftwareHistory(error))
            {
                ClearSoftwareHistory(client, key);
                _softwareFeedback.Remove(key);
            }
            _softwareErrorAllowsHistory = CanUseSoftwareHistoryOnError(error);
            _softwareReadRetryable = error is ConsoleApiException { StatusCode: 0 or 408 or 500 or 502 or 503 or 504 }
                or HttpRequestException or OperationCanceledException;
            _softwareError = error is ConsoleApiException { StatusCode: 404 or 426 } ?
                "当前服务未提供此设备的软件维护接口，请刷新班级列表或核对服务版本。" : ServiceMessage(error);
        }
        finally
        {
            if (_softwareRead == request)
            {
                _softwareRead = null;
                RenderSoftwareMaintenance();
                if (userRefresh) ShowSoftwareMaintenance(key);
            }
        }
    }

    private const string PendingSoftwareMessage = "学校是否接受本次操作尚未确认。请先刷新维护状态。";

    private async Task SaveSoftwareAsync(SoftwareUpdateAction action, string? targetVersion = null, SoftwareAttempt? retry = null)
    {
        if ((retry is null ? !CanEditSoftware : !CanRetrySoftware || CurrentSoftwareFeedback?.Pending != retry) ||
            _client is not { } client || EquipmentKey is not { } key || _software is not { } snapshot) return;
        var attempt = retry ?? new(snapshot, action, targetVersion, Guid.NewGuid());
        bool withdrawing = attempt.Action == SoftwareUpdateAction.WithdrawInvitation;
        if (withdrawing && (!snapshot.CanWithdraw || CurrentSoftwareFeedback?.ReviewInvitation == true)) return;
        bool wasOffline = _device?.Device.Connectivity == "OFFLINE";
        var session = _softwareSession ??= new CancellationTokenSource();
        _softwareFeedback[key] = new("正在提交给学校…", Writing: true);
        RenderSoftwareMaintenance();
        try
        {
            var saved = await client.SetSoftwareUpdateAsync(attempt.Snapshot, attempt.Action, attempt.TargetVersion, attempt.Id, session.Token);
            if (_client != client) return;
            if (retry is not null)
            {
                // A replay can return the original accepted snapshot. Read the current state before offering actions.
                _softwareFeedback[key] = new("学校已回应原请求。请以当前维护状态为准。");
                if (EquipmentKey == key && SoftwareMaintenanceCard.IsVisible)
                {
                    _software = null;
                    await ReadSoftwareAsync();
                }
                return;
            }
            SaveSoftwareHistory(client, key, saved);
            if (withdrawing)
                _softwareFeedback[key] = new(wasOffline
                    ? "学校端已撤回邀请。终端最近观测离线，需重新连接学校服务才能获取最新状态。"
                    : "学校端已撤回邀请。终端需联系学校服务才能获取最新状态。",
                    WithdrawalResult: new WithdrawalState(saved));
            else
                _softwareFeedback.Remove(key);
            if (EquipmentKey == key && SoftwareMaintenanceCard.IsVisible)
            {
                _software = saved;
                _softwareError = null;
                _softwareErrorAllowsHistory = false;
            }
        }
        catch (Exception error) when (IsServiceError(error))
        {
            if (_client != client || ReturnToLoginIfExpired(error)) return;
            if (ShouldClearSoftwareHistory(error))
            {
                ClearSoftwareHistory(client, key);
                if (EquipmentKey == key && SoftwareMaintenanceCard.IsVisible)
                {
                    _software = null;
                    _softwareErrorAllowsHistory = false;
                }
            }
            bool unknown = error is ConsoleApiException { Code: "UPDATE_UNCONFIRMED" } or HttpRequestException or OperationCanceledException;
            _softwareFeedback[key] = withdrawing && error is ConsoleApiException
                { StatusCode: 409, Code: "REVISION_CONFLICT" or "UPDATE_ENROLLMENT_CHANGED" }
                ? new("邀请状态已变化，正在核对最新状态。", ConflictingInvitation: snapshot.EnrollmentId)
                : new(unknown ? PendingSoftwareMessage : ServiceMessage(error), Pending: unknown ? attempt : null);
            if (EquipmentKey == key && SoftwareMaintenanceCard.IsVisible)
                await ReadSoftwareAsync();
        }
        if (_client == client && EquipmentKey == key && SoftwareMaintenanceCard.IsVisible)
        {
            RenderSoftwareMaintenance();
            if (withdrawing) ShowSoftwareMaintenance(key);
        }
    }

    private bool CanEditSoftware => SoftwareMaintenanceCard.IsVisible && _softwareError is null && _softwareRead is null &&
        CurrentSoftwareFeedback is not ({ Writing: true } or { Pending: not null } or { ConflictingInvitation: not null });

    private bool CanRetrySoftware => SoftwareMaintenanceCard.IsVisible && _softwareError is null && _softwareRead is null &&
        CurrentSoftwareFeedback is { Writing: false, RetryVerified: true, Pending: { } pending } &&
        _software is { } snapshot && CanRetrySoftwareAttempt(pending, snapshot);

    private static bool CanRetrySoftwareAttempt(SoftwareAttempt attempt, SoftwareUpdate current)
    {
        var original = attempt.Snapshot;
        if (original.Revision != current.Revision || original.Enrollment != current.Enrollment ||
            original.EnrollmentId != current.EnrollmentId || original.Managed != current.Managed ||
            original.Plan?.Id != current.Plan?.Id || original.Plan?.State != current.Plan?.State) return false;
        return attempt.Action switch
        {
            SoftwareUpdateAction.Enroll => current.CanInvite,
            SoftwareUpdateAction.WithdrawInvitation => current.CanWithdraw,
            SoftwareUpdateAction.Cancel => current.CanCancel,
            SoftwareUpdateAction.Approve => current.EligibleReleases.Any(release => release.Version == attempt.TargetVersion),
            _ => false
        };
    }

    private static SoftwareFeedback ReviewWithdrawalConflict(SoftwareUpdate snapshot, Guid invitation)
    {
        var current = new WithdrawalState(snapshot);
        if (snapshot.IsEnrolled)
            return new("教室端已完成纳管，本次撤回未执行，现有纳管保留。", WithdrawalResult: current);
        if (snapshot.CanWithdraw && snapshot.EnrollmentId != invitation)
            return ReplacementInvitationFeedback(current);
        if (snapshot.CanInvite)
            return new("原邀请已失效，当前没有待确认的邀请。", WithdrawalResult: current);
        return new("邀请状态已变化。已重新读取，请核对当前邀请。", WithdrawalResult: current);
    }

    private static SoftwareFeedback ReplacementInvitationFeedback(WithdrawalState current) =>
        new("原邀请已变更。当前的新邀请仍保留。如需撤回新邀请，请刷新核对后操作。",
            WithdrawalResult: current, ReviewInvitation: true);

    private void RenderSoftwareMaintenance()
    {
        if (!SoftwareMaintenanceCard.IsVisible) return;
        var snapshot = _software;
        var feedback = CurrentSoftwareFeedback;
        var plan = snapshot?.Plan;
        var confirmation = _softwareError is null && snapshot?.IsEnrolled == true ? plan?.RollbackConfirmation : null;
        var releases = snapshot?.EligibleReleases.Select(entry => entry.Version).ToArray() ?? [];
        if (!SoftwareReleasePicker.Items.OfType<string>().SequenceEqual(releases))
        {
            var selected = SoftwareReleasePicker.SelectedItem as string;
            SoftwareReleasePicker.ItemsSource = releases;
            if (releases.Contains(selected)) SoftwareReleasePicker.SelectedItem = selected;
        }

        SoftwareMaintenanceStatus.Text = _softwareError ?? (snapshot is null ? "正在读取软件维护状态…" : SoftwareStatusText(snapshot, confirmation));
        RenderSoftwarePilotPreflight(snapshot);
        SoftwareVersions.IsVisible = snapshot is not null;
        SoftwareObservedVersion.Text = snapshot?.ObservedVersion ?? "尚无版本回执";
        SoftwareObservationTime.Text = snapshot?.LastSeenAt is { } seen ? $"上报于 {FormatTime(seen)}" : "";
        SoftwareTargetVersion.Text = plan?.TargetVersion ?? "暂无任务";
        SoftwarePlanUpdatedAt.Text = plan is not null ? $"更新于 {FormatTime(plan.UpdatedAt)}" : "";
        SoftwareRollbackReceipt.IsVisible = confirmation is not null;
        SoftwareRollbackReceipt.Text = confirmation is { } receipt
            ? $"回退确认 · 原版本 {receipt.Version} · 确认于 {FormatTime(receipt.ConfirmedAt)}" : null;
        string details = snapshot is null || _softwareError is not null ? "" : SoftwareDetails(snapshot, _device?.Device.Connectivity, confirmation);
        SoftwareMaintenanceDetails.Text = details;
        SoftwareMaintenanceDetails.IsVisible = details.Length > 0;
        RenderSoftwareReceipt(snapshot);
        SoftwareApprovalForm.IsVisible = releases.Length > 0 && _softwareError is null;
        InviteSoftwareButton.IsVisible = snapshot?.CanInvite == true && _softwareError is null;
        WithdrawInvitationButton.IsVisible = snapshot?.CanWithdraw == true && _softwareError is null;
        ApproveSoftwareButton.IsVisible = SoftwareApprovalForm.IsVisible;
        CancelSoftwareButton.IsVisible = snapshot?.CanCancel == true && _softwareError is null;
        InviteSoftwareButton.IsEnabled = CancelSoftwareButton.IsEnabled = SoftwareReleasePicker.IsEnabled = CanEditSoftware;
        WithdrawInvitationButton.IsEnabled = CanEditSoftware && feedback?.ReviewInvitation != true;
        ApproveSoftwareButton.IsEnabled = CanEditSoftware && SoftwareReleasePicker.SelectedItem is string;
        RetrySoftwareButton.IsVisible = feedback is { RetryVerified: true, Pending: not null } && _softwareError is null;
        RetrySoftwareButton.IsEnabled = CanRetrySoftware;
        RefreshSoftwareButton.IsEnabled = _softwareRead is null && feedback?.Writing != true;
        bool showFeedback = feedback is not null && (feedback.WithdrawalResult is not { } result ||
            snapshot is not null && _softwareError is null && new WithdrawalState(snapshot) == result);
        SoftwareMaintenanceFeedback.Text = showFeedback ? feedback?.Message : null;
        SoftwareMaintenanceFeedback.IsVisible = showFeedback;
        if (snapshot is { Enrollment: "INVITED" } || plan?.IsActive == true ||
            snapshot?.IsEnrolled == true && plan is { HasRollbackReport: true, ValidPreviousVersion: not null } && confirmation is null)
            _softwarePoll.Start();
        // A transient read failure may keep an existing poll alive, but must not start a new one.
        else if (snapshot is not null || !_softwareReadRetryable)
            _softwarePoll.Stop();
    }

    private void RenderSoftwareReceipt(SoftwareUpdate? snapshot)
    {
        SoftwareReceiptProjection? projection = null;
        bool isHistorical = false;
        if (_softwareError is null && snapshot is not null && (snapshot.Plan is not null || snapshot.ObservedVersion is not null))
        {
            projection = CreateSoftwareReceiptProjection(snapshot, DateTimeOffset.Now);
        }
        else if (((_softwareError is null && snapshot is null) || (_softwareError is not null && _softwareErrorAllowsHistory)) &&
                 EquipmentKey is { } key && GetSoftwareHistory(_client, key) is { } cached)
        {
            projection = cached;
            isHistorical = true;
        }

        if (projection is null)
        {
            SoftwareReceiptSection.IsVisible = false;
            SoftwareReceiptDetails.IsVisible = false;
            return;
        }

        SoftwareReceiptSection.IsVisible = true;
        SoftwareReceiptHeading.Text = isHistorical ? "上次已确认维护详情" : "本次维护详情";
        string? hint = isHistorical
            ? $"上次已确认（只读回看，未启用写入） · 记录于 {FormatTime(projection.CapturedAt)}"
            : projection.PlanUpdatedAt is { } updated
                ? $"任务更新于 {FormatTime(updated)}"
                : projection.LastSeenAt is { } seen
                    ? $"最近上报于 {FormatTime(seen)}"
                    : null;
        SoftwareReceiptHint.Text = hint;
        SoftwareReceiptHint.IsVisible = !string.IsNullOrEmpty(hint);
        ToggleSoftwareReceiptButton.Content = _softwareReceiptExpanded ? "收起详情" : "展开详情";
        SoftwareReceiptDetails.IsVisible = _softwareReceiptExpanded;
        SoftwareReceiptDetails.Text = _softwareReceiptExpanded ? FormatSoftwareReceipt(projection, EquipmentKey?.DeviceId, isHistorical) : null;
    }

    private static string FormatSoftwareReceipt(SoftwareReceiptProjection receipt, string? deviceId, bool isHistorical)
    {
        string stage = FormatSoftwareReceiptStage(receipt.State, receipt.Reason, receipt.RollbackConfirmation is not null);
        string rollback = receipt.RollbackConfirmation is { } confirmed
            ? $"已确认（原版本 {confirmed.Version} · 确认于 {FormatTime(confirmed.ConfirmedAt)}）"
            : "未确认";
        return string.Join("\n",
        [
            $"记录性质：{(isHistorical ? $"上次已确认（只读回看，记录于 {FormatTime(receipt.CapturedAt)}）" : "本次维护")}",
            $"任务编号：{receipt.PlanId ?? "暂无"}",
            $"设备编号：{deviceId ?? "未提供"}",
            $"阶段与代码：{stage}",
            $"原版本：{receipt.PreviousVersion ?? "未提供"}",
            $"目标版本：{receipt.TargetVersion ?? "暂无"}",
            $"最近上报版本：{receipt.ObservedVersion ?? "尚无版本回执"}",
            $"任务更新时间：{FormatTime(receipt.PlanUpdatedAt)}",
            $"最近上报时间：{FormatTime(receipt.LastSeenAt)}",
            $"回退确认：{rollback}"
        ]);
    }

    private static string FormatSoftwareReceiptStage(string? state, string? reason, bool rollbackConfirmed)
    {
        if (string.IsNullOrWhiteSpace(state)) return "暂无任务";
        string label = state switch
        {
            "APPROVED" => "更新已批准",
            "DEFERRED" => "等待安全更新时机",
            "INSTALLING" => "等待安装器报告结果",
            "AWAITING_RESTART" => "等待新版教室后台上报",
            "SUCCEEDED" => "升级成功",
            "FAILED" when reason is "ROLLED_BACK" or "ROLLBACK_SUCCEEDED" && rollbackConfirmed => "升级失败（已确认原版本重新运行）",
            "FAILED" when reason is "ROLLED_BACK" or "ROLLBACK_SUCCEEDED" => "升级失败（安装器已报告回退）",
            "FAILED" => "升级失败",
            "CANCELLED" => "更新任务已取消",
            _ => "维护状态待核对"
        };
        string code = string.IsNullOrWhiteSpace(reason) ? state : $"{state} / {reason}";
        return $"{label}（{code}）";
    }

    private static string SoftwareStatusText(SoftwareUpdate snapshot, SoftwareRollbackConfirmation? confirmation) => snapshot.Enrollment switch
    {
        "UNMANAGED" => "设备尚未纳管",
        "INVITED" => "等待教室端确认纳管",
        "MANAGED" when snapshot.IsEnrolled => snapshot.Plan?.State switch
        {
            null => "设备已纳管",
            "APPROVED" => "更新已批准",
            "DEFERRED" when snapshot.Plan.Reason is "BUSY" or "INSTALLER_BUSY" => "等待安装器空闲",
            "DEFERRED" when snapshot.Plan.Reason is "DOWNLOADER_UNAVAILABLE" or "WORKER_UNAVAILABLE" => "需要现场核对维护工具",
            "DEFERRED" => "等待安全更新时机",
            "INSTALLING" => "等待安装器报告结果",
            "AWAITING_RESTART" when snapshot.Plan.Reason is "BUSY" or "INSTALLER_BUSY" => "等待现场安装器退出",
            "AWAITING_RESTART" when snapshot.Plan.Reason is null => "等待新版教室后台上报",
            "AWAITING_RESTART" => "安装结果待核对",
            "SUCCEEDED" when snapshot.ObservedVersion == snapshot.Plan.TargetVersion => "升级成功，已收到新版上报",
            "SUCCEEDED" => "任务已完成，当前版本待核对",
            "FAILED" when confirmation is not null => "升级失败，已确认原版本重新运行",
            "FAILED" when snapshot.Plan.HasRollbackReport => "升级失败，安装器已报告回退",
            "FAILED" when snapshot.Plan.Reason is "ROLLBACK_FAILED" or "RECOVERY_REQUIRED" or "HANDOFF_UNCONFIRMED" => "升级失败，需现场核对恢复",
            "FAILED" when snapshot.Plan.Reason == "APP_START_FAILED" => "教室后台启动失败",
            "FAILED" when snapshot.Plan.Reason is "VERSION_MISMATCH" or "BASELINE_MISMATCH" => "更新前校验失败",
            "FAILED" when snapshot.Plan.Reason is "HANDOFF_NOT_STARTED" or "CLASSROOM_STILL_RUNNING" => "安装未开始",
            "FAILED" => "升级失败",
            "CANCELLED" => "更新任务已取消",
            _ => "更新状态暂不可识别"
        },
        _ => "设备纳管状态暂不可识别"
    };

    private static string SoftwareDetails(SoftwareUpdate snapshot, string? connectivity, SoftwareRollbackConfirmation? confirmation)
    {
        if (snapshot.CanInvite)
            return "邀请仅适用于这台大屏。需在教室端打开 NPDownloader 并确认，学校才可批准指定版本。";
        if (snapshot.Enrollment == "INVITED")
        {
            string connection = connectivity == "OFFLINE" ? "终端最近观测离线。" : "";
            string receipt = snapshot.InvitationLastExchangeAt is { } seen
                ? $"本次邀请期间，学校于 {FormatTime(seen)} 收到最近一次终端维护上报。"
                : snapshot.CanWithdrawInvitation.HasValue ? "尚未收到本次邀请期间的终端维护上报。"
                    : "当前服务未提供本次邀请期间的终端维护上报信息。";
            return connection + receipt + "\n请在此大屏打开 NPDownloader，核对学校、班级和设备后同意纳管。";
        }
        if (!snapshot.IsEnrolled) return "";
        if (snapshot.Plan is { State: "FAILED" } failed)
            return FailedUpdateDetails(snapshot, failed, confirmation);
        if (snapshot.Plan is { IsActive: true } active)
            return (active.State, active.Reason) switch
            {
                ("DEFERRED", "RECORDING_ACTIVE" or "RECORDING_BUSY") => "正在录课或保存录像。终端会等待录课安全结束。",
                ("DEFERRED", "STUDY_ACTIVE" or "STUDY_BUSY") => "正在自习。终端会等待本场自习结束。",
                ("DEFERRED", "CLASS_IN_SESSION") => "当天课程尚未结束。终端会等到课后再次检查。",
                ("DEFERRED", "STATE_UNKNOWN" or "STATUS_UNKNOWN" or "CLASSROOM_STATE_UNKNOWN") => "终端尚不能确认课堂状态，会继续等待。",
                ("DEFERRED", "DOWNLOADER_UNAVAILABLE" or "WORKER_UNAVAILABLE") => "终端无法启动维护工具。请现场确认 NPDownloader 已安装且能够打开，再刷新维护状态。",
                ("DEFERRED", "DOWNLOAD_FAILED") => "更新包暂未下载完成。请现场核对大屏网络，再刷新维护状态。",
                ("DEFERRED" or "AWAITING_RESTART", "BUSY" or "INSTALLER_BUSY") => "终端上次报告维护流程被占用。请等现场安装程序结束后刷新维护状态。若状态一直没有变化，请现场打开已安装的 NPDownloader 核对。",
                ("INSTALLING", _) => "学校正在等待本任务的安装器回执。请先等待。若状态一直没有变化，请现场打开已安装的 NPDownloader 核对。",
                ("AWAITING_RESTART", null) => "安装器已报告安装完成。请等待新版 NPEduTools 启动并上报。若现场确认后台尚未启动，请在本机打开 NPEduTools 后刷新。",
                ("AWAITING_RESTART", _) => "学校尚未收到明确的安装完成回执。请现场打开已安装的 NPDownloader 核对，再刷新维护状态。",
                _ => "终端会在确认课堂空闲后执行批准的更新。"
            };
        if (snapshot.ObservedVersion is null) return "等待终端上报当前版本。";
        if (snapshot.CatalogStatus is "NOT_CONFIGURED" or "UNAVAILABLE") return "学校更新目录暂不可用。";
        if (!snapshot.Catalog.Any(entry => entry.Version == snapshot.ObservedVersion)) return "学校更新目录中没有当前版本的恢复包。";
        return snapshot.EligibleReleases.Any() ? "批准只针对这台设备与所选版本。终端会等待课堂空闲。" : "学校更新目录暂无可批准的更高版本。";
    }

    private static string FailedUpdateDetails(SoftwareUpdate snapshot, SoftwareUpdatePlan plan, SoftwareRollbackConfirmation? confirmation)
    {
        if (confirmation is { } receipt)
        {
            if (snapshot.ObservedVersion is { } current && current != receipt.Version)
                return snapshot.LastSeenAt > receipt.ConfirmedAt
                    ? $"此后最近一次上报的运行版本为 {current}。请按最新上报核对设备。"
                    : $"最近上报的运行版本为 {current}，请刷新核对设备当前状态。";
            return "";
        }

        if (plan.HasRollbackReport)
        {
            string baseline = plan.ValidPreviousVersion is { } previous ? $"本次任务的原版本为 {previous}。" : "";
            return baseline + "学校尚无法确认原版本已重新运行。请现场核对 NPEduTools 是否已启动并连接学校，再刷新维护状态。";
        }

        return plan.Reason switch
        {
            "ROLLBACK_FAILED" => "终端报告回退失败。" + LocalRecoveryHelp,
            "RECOVERY_REQUIRED" => "终端报告需要现场检查恢复记录。" + LocalRecoveryHelp,
            "HANDOFF_UNCONFIRMED" => "学校无法确认本次安装是否已启动。" + LocalRecoveryHelp,
            "APP_START_FAILED" => "安装流程报告无法启动 NPEduTools。请先在本机核对后台状态。" + LocalRecoveryHelp,
            "VERSION_MISMATCH" or "BASELINE_MISMATCH" => "现场版本或已安装文件与任务基线不符，未开始本次安装。请现场核对 NPEduTools 的安装版本与实际运行版本。",
            "HANDOFF_NOT_STARTED" => "本次未启动安装程序。请现场核对 NPEduTools 与已安装的 NPDownloader 状态。",
            "CLASSROOM_STILL_RUNNING" => "教室软件仍在运行，安装器未开始替换文件。请在录课和自习结束后现场核对状态。",
            _ => "学校已收到失败回执。请现场核对终端实际运行版本，必要时打开已安装的 NPDownloader 查看维护状态。"
        };
    }
}
