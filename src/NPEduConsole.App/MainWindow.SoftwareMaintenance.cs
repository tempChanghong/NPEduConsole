using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer _softwarePoll = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly Dictionary<(string SchoolId, string DeviceId), SoftwareFeedback> _softwareFeedback = new();
    private CancellationTokenSource? _softwareRead;
    private CancellationTokenSource? _softwareSession;
    private SoftwareUpdate? _software;
    private string? _softwareError;

    private sealed record SoftwareFeedback(string Message, bool Writing = false, bool Unconfirmed = false,
        Guid? ConflictingInvitation = null, WithdrawalState? WithdrawalResult = null, bool ReviewInvitation = false);

    private readonly record struct WithdrawalState(long Revision, string Enrollment, bool Managed, Guid? EnrollmentId)
    {
        public WithdrawalState(SoftwareUpdate snapshot) : this(snapshot.Revision, snapshot.Enrollment, snapshot.Managed, snapshot.EnrollmentId) { }
    }

    private SoftwareFeedback? CurrentSoftwareFeedback => EquipmentKey is { } key ? _softwareFeedback.GetValueOrDefault(key) : null;

    private async void OpenSoftwareMaintenanceClicked(object? sender, RoutedEventArgs args)
    {
        if (EquipmentKey is not { } key || SoftwareMaintenanceCard.IsVisible) return;
        SoftwareMaintenanceScope.Text = EquipmentScopeText;
        SoftwareMaintenanceCard.IsVisible = true;
        RenderSoftwareMaintenance();
        Dispatcher.UIThread.Post(() =>
        {
            if (SoftwareMaintenanceCard.IsVisible && EquipmentKey == key) SoftwareMaintenanceCard.BringIntoView();
        }, DispatcherPriority.Loaded);
        await ReadSoftwareAsync();
        if (SoftwareMaintenanceCard.IsVisible && EquipmentKey == key)
        {
            RefreshSoftwareButton.Focus();
            Dispatcher.UIThread.Post(() =>
            {
                if (SoftwareMaintenanceCard.IsVisible && EquipmentKey == key) SoftwareMaintenanceCard.BringIntoView();
            }, DispatcherPriority.Loaded);
        }
    }

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
        _software = null;
        _softwareError = null;
        SoftwareReleasePicker.ItemsSource = null;
        SoftwareMaintenanceCard.IsVisible = false;
    }

    private void CancelSoftwareSession()
    {
        _softwareSession?.Cancel();
        _softwareSession?.Dispose();
        _softwareSession = null;
        _softwareFeedback.Clear();
    }

    private async void RefreshSoftwareClicked(object? sender, RoutedEventArgs args) => await ReadSoftwareAsync(userRefresh: true);
    private async void PollSoftware(object? sender, EventArgs args) => await ReadSoftwareAsync();
    private async void InviteSoftwareClicked(object? sender, RoutedEventArgs args) => await SaveSoftwareAsync(SoftwareUpdateAction.Enroll);
    private async void WithdrawInvitationClicked(object? sender, RoutedEventArgs args) =>
        await SaveSoftwareAsync(SoftwareUpdateAction.WithdrawInvitation);
    private async void ApproveSoftwareClicked(object? sender, RoutedEventArgs args) =>
        await SaveSoftwareAsync(SoftwareUpdateAction.Approve, SoftwareReleasePicker.SelectedItem as string);
    private async void CancelSoftwareClicked(object? sender, RoutedEventArgs args) => await SaveSoftwareAsync(SoftwareUpdateAction.Cancel);
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
            else if (userRefresh && feedback?.Unconfirmed == true)
                _softwareFeedback[key] = new("已重新读取学校状态。请核对纳管或任务结果，再决定下一步。");
        }
        catch (Exception error) when (IsServiceError(error))
        {
            if (_softwareRead != request || _client != client || ReturnToLoginIfExpired(error)) return;
            _softwareError = error is ConsoleApiException { StatusCode: 404 or 426 } ?
                "当前服务未提供此设备的软件维护接口，请刷新班级列表或核对服务版本。" : ServiceMessage(error);
        }
        finally
        {
            if (_softwareRead == request)
            {
                _softwareRead = null;
                RenderSoftwareMaintenance();
            }
        }
    }

    private async Task SaveSoftwareAsync(SoftwareUpdateAction action, string? targetVersion = null)
    {
        if (!CanEditSoftware || _client is not { } client || EquipmentKey is not { } key || _software is not { } snapshot) return;
        bool withdrawing = action == SoftwareUpdateAction.WithdrawInvitation;
        if (withdrawing && (!snapshot.CanWithdraw || CurrentSoftwareFeedback?.ReviewInvitation == true)) return;
        bool wasOffline = _device?.Device.Connectivity == "OFFLINE";
        var session = _softwareSession ??= new CancellationTokenSource();
        _softwareFeedback[key] = new("正在提交给学校…", Writing: true);
        RenderSoftwareMaintenance();
        try
        {
            var saved = await client.SetSoftwareUpdateAsync(snapshot, action, targetVersion, Guid.NewGuid(), session.Token);
            if (_client != client) return;
            if (withdrawing)
                _softwareFeedback[key] = new(wasOffline
                    ? "学校端已撤回邀请。终端最近观测离线，需重新连接学校服务才能获取最新状态。"
                    : "学校端已撤回邀请。终端需联系学校服务才能获取最新状态。",
                    WithdrawalResult: new WithdrawalState(saved));
            else
                _softwareFeedback.Remove(key);
            if (EquipmentKey == key && SoftwareMaintenanceCard.IsVisible) _software = saved;
        }
        catch (Exception error) when (IsServiceError(error))
        {
            if (_client != client || ReturnToLoginIfExpired(error)) return;
            bool unknown = error is ConsoleApiException { Code: "UPDATE_UNCONFIRMED" } or HttpRequestException or OperationCanceledException;
            _softwareFeedback[key] = withdrawing && error is ConsoleApiException
                { StatusCode: 409, Code: "REVISION_CONFLICT" or "UPDATE_ENROLLMENT_CHANGED" }
                ? new("邀请状态已变化，正在核对最新状态。", ConflictingInvitation: snapshot.EnrollmentId)
                : new(unknown ? "学校是否接受本次操作尚未确认。请先刷新维护状态。" : ServiceMessage(error),
                    Unconfirmed: unknown);
            if (EquipmentKey == key && SoftwareMaintenanceCard.IsVisible)
                await ReadSoftwareAsync();
        }
        if (_client == client && EquipmentKey == key && SoftwareMaintenanceCard.IsVisible)
        {
            RenderSoftwareMaintenance();
            if (withdrawing)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_client == client && EquipmentKey == key && SoftwareMaintenanceCard.IsVisible)
                        SoftwareMaintenanceCard.BringIntoView();
                }, DispatcherPriority.Loaded);
            }
        }
    }

    private bool CanEditSoftware => SoftwareMaintenanceCard.IsVisible && _softwareError is null && _softwareRead is null &&
        CurrentSoftwareFeedback is not ({ Writing: true } or { Unconfirmed: true } or { ConflictingInvitation: not null });

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
        var releases = snapshot?.EligibleReleases.Select(entry => entry.Version).ToArray() ?? [];
        if (!SoftwareReleasePicker.Items.OfType<string>().SequenceEqual(releases))
        {
            var selected = SoftwareReleasePicker.SelectedItem as string;
            SoftwareReleasePicker.ItemsSource = releases;
            if (releases.Contains(selected)) SoftwareReleasePicker.SelectedItem = selected;
        }

        SoftwareMaintenanceStatus.Text = _softwareError ?? (snapshot is null ? "正在读取软件维护状态…" : SoftwareStatusText(snapshot));
        SoftwareVersions.IsVisible = snapshot is not null;
        SoftwareObservedVersion.Text = snapshot?.ObservedVersion ?? "尚无版本回执";
        SoftwareObservationTime.Text = snapshot?.LastSeenAt is { } seen ? $"上报于 {FormatTime(seen)}" : "";
        SoftwareTargetVersion.Text = snapshot?.Plan?.TargetVersion ?? "暂无任务";
        SoftwarePlanUpdatedAt.Text = snapshot?.Plan is { } plan ? $"更新于 {FormatTime(plan.UpdatedAt)}" : "";
        SoftwareMaintenanceDetails.Text = snapshot is null || _softwareError is not null ? "" : SoftwareDetails(snapshot, _device?.Device.Connectivity);
        SoftwareApprovalForm.IsVisible = releases.Length > 0 && _softwareError is null;
        InviteSoftwareButton.IsVisible = snapshot?.CanInvite == true && _softwareError is null;
        WithdrawInvitationButton.IsVisible = snapshot?.CanWithdraw == true && _softwareError is null;
        ApproveSoftwareButton.IsVisible = SoftwareApprovalForm.IsVisible;
        CancelSoftwareButton.IsVisible = snapshot?.CanCancel == true && _softwareError is null;
        InviteSoftwareButton.IsEnabled = CancelSoftwareButton.IsEnabled = SoftwareReleasePicker.IsEnabled = CanEditSoftware;
        WithdrawInvitationButton.IsEnabled = CanEditSoftware && feedback?.ReviewInvitation != true;
        ApproveSoftwareButton.IsEnabled = CanEditSoftware && SoftwareReleasePicker.SelectedItem is string;
        RefreshSoftwareButton.IsEnabled = _softwareRead is null && feedback?.Writing != true;
        bool showFeedback = feedback is not null && (feedback.WithdrawalResult is not { } result ||
            snapshot is not null && _softwareError is null && new WithdrawalState(snapshot) == result);
        SoftwareMaintenanceFeedback.Text = showFeedback ? feedback?.Message : null;
        SoftwareMaintenanceFeedback.IsVisible = showFeedback;
        if (snapshot is { Enrollment: "INVITED" } || snapshot?.Plan?.IsActive == true) _softwarePoll.Start();
        else _softwarePoll.Stop();
    }

    private static string SoftwareStatusText(SoftwareUpdate snapshot) => snapshot.Enrollment switch
    {
        "UNMANAGED" => "设备尚未纳管",
        "INVITED" => "等待教室端确认纳管",
        "MANAGED" when snapshot.IsEnrolled => snapshot.Plan?.State switch
        {
            null => "设备已纳管",
            "APPROVED" => "更新已批准",
            "DEFERRED" => "等待安全更新时机",
            "INSTALLING" => "正在安装更新",
            "AWAITING_RESTART" => "等待新版教室后台上报",
            "SUCCEEDED" when snapshot.ObservedVersion == snapshot.Plan.TargetVersion => "升级成功，已收到新版上报",
            "SUCCEEDED" => "任务已完成，当前版本待核对",
            "FAILED" => "升级失败",
            "CANCELLED" => "更新任务已取消",
            _ => "更新状态暂不可识别"
        },
        _ => "设备纳管状态暂不可识别"
    };

    private static string SoftwareDetails(SoftwareUpdate snapshot, string? connectivity)
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
        if (snapshot.Plan is { IsActive: true } or { State: "FAILED" })
            return snapshot.Plan.Reason switch
            {
                "RECORDING_ACTIVE" or "RECORDING_BUSY" => "正在录课或保存录像。终端会等待录课安全结束。",
                "STUDY_ACTIVE" or "STUDY_BUSY" => "正在自习。终端会等待本场自习结束。",
                "CLASS_IN_SESSION" => "当天课程尚未结束。终端会等到课后再次检查。",
                "STATE_UNKNOWN" or "STATUS_UNKNOWN" or "CLASSROOM_STATE_UNKNOWN" => "终端尚不能确认课堂状态，会继续等待。",
                "ROLLED_BACK" or "ROLLBACK_SUCCEEDED" => "终端已报告恢复到升级前的版本。",
                "ROLLBACK_FAILED" or "RECOVERY_REQUIRED" => "终端未能确认恢复旧版本，请现场检查。",
                _ when snapshot.Plan.State == "AWAITING_RESTART" => "学校正等待这台设备实际运行并上报目标版本。",
                _ when snapshot.Plan.State == "FAILED" => "学校已收到失败回执，请核对终端实际运行版本与现场状态。",
                _ => "终端会在确认课堂空闲后执行批准的更新。"
            };
        if (snapshot.ObservedVersion is null) return "等待终端上报当前版本。";
        if (snapshot.CatalogStatus is "NOT_CONFIGURED" or "UNAVAILABLE") return "学校更新目录暂不可用。";
        if (!snapshot.Catalog.Any(entry => entry.Version == snapshot.ObservedVersion)) return "学校更新目录中没有当前版本的恢复包。";
        return snapshot.EligibleReleases.Any() ? "批准只针对这台设备与所选版本。终端会等待课堂空闲。" : "学校更新目录暂无可批准的更高版本。";
    }
}
