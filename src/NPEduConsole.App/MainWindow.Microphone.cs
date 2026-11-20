using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private static readonly TimeSpan FastPollInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SlowPollInterval = TimeSpan.FromSeconds(24);
    private static readonly TimeSpan FastPollDuration = TimeSpan.FromSeconds(75);
    private readonly DispatcherTimer _equipmentPoll = new() { Interval = FastPollInterval };
    private readonly Dictionary<(string SchoolId, string DeviceId), MicrophoneChange> _microphoneChanges = new();
    private CancellationTokenSource? _microphoneSession;
    private Equipment? _equipment;
    private Equipment? _editingEquipment;
    private bool _equipmentReadFailed;
    private long _pollStarted;

    private sealed record MicrophoneChoice(Microphone Microphone, string Label);

    private enum ChangeState { Saving, Accepted, Unconfirmed, Observed, Checked, Rejected }

    private sealed record MicrophoneChange(Microphone? Target, bool? Authorization, long PreviousVersion,
        ChangeState State, long? Version = null, string? Message = null);

    private bool IsEditingEquipment => _editingEquipment is not null || _localAuthorizationSnapshot is not null;
    private string EquipmentScopeText => $"{_school?.Name} / {ClassroomName(_classroom)} / {_device?.ScreenName} / {_device?.Device.Name}";

    private (string SchoolId, string DeviceId)? EquipmentKey =>
        _school is { } school && _device is { } device ? (school.Id, device.Device.Id) : null;

    private MicrophoneChange? CurrentMicrophoneChange => EquipmentKey is { } key
        ? _microphoneChanges.GetValueOrDefault(key) : null;

    private void EditMicrophoneClicked(object? sender, RoutedEventArgs args)
    {
        if (IsEditingEquipment || _equipment is not { } equipment || !CanEditMicrophone(equipment, CurrentMicrophoneChange))
        {
            RenderMicrophoneActions();
            return;
        }

        CancelRequest();
        _editingEquipment = equipment;
        var microphones = equipment.Microphones.DistinctBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var numbers = microphones.GroupBy(item => item.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.OrderBy(item => item.Id, StringComparer.Ordinal)
                .Select((item, index) => (item.Id, Number: index + 1)))
            .ToDictionary(item => item.Id, item => item.Number, StringComparer.Ordinal);
        var options = microphones.Select(item => new MicrophoneChoice(item,
            numbers.TryGetValue(item.Id, out int number) ? $"{item.Name}（{number}）" : item.Name)).ToArray();
        MicrophoneScope.Text = EquipmentScopeText;
        MicrophoneUnlockWarning.IsVisible = equipment.Locked && equipment.ReceivedAt.HasValue &&
            equipment.LocallyUnlocked == true;
        MicrophoneAuthorizationWarning.IsVisible = equipment.AllowsLocalMicrophoneChange;
        MicrophonePicker.ItemsSource = options;
        MicrophonePicker.SelectedItem = equipment.HasLocalMicrophoneOverrideToReplace ? null :
            options.FirstOrDefault(item => item.Microphone.Id == equipment.MicrophoneId);
        MicrophoneEditor.IsVisible = true;
        RenderMicrophoneActions();
        MicrophonePicker.Focus();
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_editingEquipment, equipment)) MicrophoneEditor.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    private void MicrophoneSelectionChanged(object? sender, SelectionChangedEventArgs args) =>
        RenderMicrophoneEditor();

    private void CancelMicrophoneClicked(object? sender, RoutedEventArgs args)
    {
        CloseMicrophoneEditor();
        RenderMicrophoneActions();
        (EditMicrophoneButton.IsVisible ? EditMicrophoneButton : RefreshEquipmentButton).Focus();
    }

    private void CloseMicrophoneEditor()
    {
        _editingEquipment = null;
        MicrophoneEditor.IsVisible = false;
        MicrophonePicker.ItemsSource = null;
        SaveMicrophoneButton.IsEnabled = false;
    }

    private void RenderMicrophoneEditor()
    {
        if (_editingEquipment is not { } equipment) return;
        var target = MicrophonePicker.SelectedItem as MicrophoneChoice;
        var choices = MicrophonePicker.Items.OfType<MicrophoneChoice>();
        string previous = choices.FirstOrDefault(item => item.Microphone.Id == equipment.MicrophoneId)?.Label
            ?? equipment.MicrophoneName ?? "未指定";
        string actual = choices.FirstOrDefault(item => item.Microphone.Id == equipment.ReportedMicrophoneId)?.Label
            ?? equipment.ReportedMicrophoneName ?? "暂无";
        MicrophoneComparison.Text = equipment.HasLocalMicrophoneOverrideToReplace
            ? $"学校原指定：{previous} · 现场上报：{actual}\n新指定：{target?.Label ?? "请选择"}"
            : $"原指定：{previous} → 新指定：{target?.Label ?? "请选择"}";
        MicrophonePicker.IsEnabled = equipment.IsInventoryFresh;
        SaveMicrophoneButton.IsEnabled = MicrophonePicker.IsEnabled && target is not null &&
            equipment.CanSelectSchoolMicrophone(target.Microphone.Id);
    }

    private async void SaveMicrophoneClicked(object? sender, RoutedEventArgs args)
    {
        if (_editingEquipment is not { } snapshot || MicrophonePicker.SelectedItem is not MicrophoneChoice choice ||
            !snapshot.CanSelectSchoolMicrophone(choice.Microphone.Id) ||
            !CanEditMicrophone(snapshot, CurrentMicrophoneChange))
        {
            RenderMicrophoneActions();
            return;
        }

        await SaveEquipmentChangeAsync(snapshot, choice.Microphone, null);
    }

    private async Task SaveEquipmentChangeAsync(Equipment snapshot, Microphone? target, bool? authorization)
    {
        if (_client is not { } client || EquipmentKey is not { } key) return;
        var session = _microphoneSession ??= new CancellationTokenSource();
        var change = new MicrophoneChange(target, authorization, snapshot.PublishedRevision, ChangeState.Saving);
        _microphoneChanges[key] = change;
        CloseMicrophoneEditor();
        CloseLocalAuthorizationEditor();
        RenderMicrophoneActions();
        RefreshEquipmentButton.Focus();
        try
        {
            var requestId = Guid.NewGuid();
            long version = authorization is { } allow
                ? await client.SetLocalMicrophoneChangeAsync(snapshot, allow, requestId, session.Token)
                : await client.ChangeMicrophoneAsync(snapshot, target!.Id, requestId, session.Token);
            change = change with { State = ChangeState.Accepted, Version = version };
        }
        catch (Exception error) when (IsServiceError(error))
        {
            if (_client != client || ReturnToLoginIfExpired(error)) return;
            change = error is ConsoleApiException { Code: "PUBLISH_UNCONFIRMED" } or
                HttpRequestException or OperationCanceledException or System.Text.Json.JsonException
                ? change with { State = ChangeState.Unconfirmed }
                : change with { State = ChangeState.Rejected,
                    Message = $"未保存本次{(authorization is null ? "更换" : "授权设置")}。{ServiceMessage(error)}" };
        }

        if (_client != client) return;
        _microphoneChanges[key] = change;
        if (EquipmentKey != key || _device is not { } selection) return;
        _equipmentReadFailed = true;
        _pollStarted = 0;
        RenderMicrophoneActions();
        await LoadDeviceAsync(selection, background: true);
    }

    private async void RefreshEquipmentClicked(object? sender, RoutedEventArgs args)
    {
        if (_device is { } selection) await LoadDeviceAsync(selection, userRefresh: true);
    }

    private void ObserveMicrophoneChange(Equipment equipment, bool userRefresh)
    {
        _equipmentReadFailed = false;
        if (EquipmentKey is not { } key || CurrentMicrophoneChange is not { State: ChangeState.Unconfirmed } change)
            return;

        if (equipment.PublishedRevision > change.PreviousVersion && MatchesChange(equipment, change))
            _microphoneChanges[key] = change with { State = ChangeState.Observed, Version = equipment.PublishedRevision };
        else if (equipment.PublishedRevision > change.PreviousVersion ||
                 (userRefresh && equipment.PublishedRevision == change.PreviousVersion))
            _microphoneChanges[key] = change with
            {
                State = ChangeState.Checked,
                Message = "已重新读取学校策略。上次发布结果仍未确认，请核对上方的版本和当前设置。"
            };
    }

    private void RenderMicrophoneActions()
    {
        if (EquipmentKey is null)
        {
            MicrophoneActions.IsVisible = false;
            return;
        }

        var change = CurrentMicrophoneChange;
        var equipment = _equipment;
        string? feedback = change is null ? null : ChangeFeedback(change, _equipmentReadFailed ? null : equipment);
        string? availability = equipment is null || _equipmentReadFailed ? null : InventoryFeedback(equipment);
        MicrophoneFeedback.Text = string.Join("\n", new[] { feedback, availability }.OfType<string>());
        MicrophoneFeedback.IsVisible = MicrophoneFeedback.Text.Length > 0;
        MicrophoneActions.IsVisible = !IsEditingEquipment || availability is not null;
        EditMicrophoneButton.IsVisible = !IsEditingEquipment && equipment is not null &&
            CanEditMicrophone(equipment, change);
        RenderMicrophoneEditor();
        RenderLocalAuthorization(equipment, change);
        UpdateEquipmentTimer();
    }

    private bool CanPublishEquipment(Equipment equipment, MicrophoneChange? change) =>
        !_equipmentReadFailed &&
        change?.State is not (ChangeState.Saving or ChangeState.Unconfirmed) &&
        !(change?.Version is { } version && equipment.PublishedRevision < version);

    private bool CanEditMicrophone(Equipment equipment, MicrophoneChange? change) =>
        CanPublishEquipment(equipment, change) && equipment.IsInventoryFresh &&
        equipment.Microphones.Any(item => equipment.CanSelectSchoolMicrophone(item.Id));

    private static string? InventoryFeedback(Equipment equipment)
    {
        if (equipment.ReceivedAt is null) return "尚未收到设备清单。请等待设备上报后刷新。";
        if (!equipment.IsInventoryFresh) return "设备清单已过期。请等待设备重新上报后刷新。";
        if (equipment.Microphones.Count == 0) return "设备最近未上报可选麦克风。请检查现场输入设备后刷新。";
        if (!equipment.HasLocalMicrophoneOverrideToReplace && equipment.Microphones.All(item => item.Id == equipment.MicrophoneId))
            return "设备当前没有其他可选麦克风。接入其他输入设备后可刷新。";
        return null;
    }

    private static string? ChangeFeedback(MicrophoneChange change, Equipment? equipment)
    {
        if (change.State == ChangeState.Saving) return change.Authorization is null ? "正在向学校保存更换…" : "正在保存现场换麦授权…";
        if (change.State == ChangeState.Unconfirmed)
            return "发布结果尚未确认。请刷新设备状态并核对学校当前策略，再决定是否重新选择。";
        if (change.Version is not { } version) return change.Message;
        if (equipment is not null && (equipment.PublishedRevision > version ||
            (equipment.PublishedRevision == version && !MatchesChange(equipment, change))))
            return $"学校现行策略已更新到版本 {equipment.PublishedRevision}，请以当前策略为准。";

        if (change.Authorization is { } allow)
        {
            string action = allow ? "预授权" : "撤销现场授权";
            string authorization = change.State == ChangeState.Accepted ? $"学校已发布{action}" : $"已核对：学校当前已发布{action}";
            return equipment?.HasCurrentReceipt == true && equipment.ErrorCode is null && equipment.PublishedRevision == version
                ? $"{authorization}（版本 {version}）。终端已收到新策略，请核对上方回执。"
                : $"{authorization}（版本 {version}）。等待终端收到新策略。";
        }

        string published = change.State == ChangeState.Accepted
            ? $"学校已接受更换（版本 {version}）。"
            : $"已核对：学校当前已指定「{change.Target!.Name}」（版本 {version}）。";
        if (equipment is null || equipment.PublishedRevision < version)
            return published + "等待核对当前策略和设备回执。";
        if (equipment.ErrorCode is null && equipment.ReceivedAt.HasValue &&
            equipment.AppliedRevision == version && equipment.MatchesPublishedMicrophone)
            return published + "设备已上报使用指定麦克风。";
        return published + "请查看上方的设备回执状态。";
    }

    private static bool MatchesChange(Equipment equipment, MicrophoneChange change) =>
        change.Authorization is { } allow
            ? equipment.ServiceSupportsLocalMicrophoneChange && equipment.AllowsLocalMicrophoneChange == allow
            : equipment.MicrophoneId == change.Target?.Id;

    private static bool AwaitingEquipmentReceipt(Equipment equipment)
    {
        if (equipment.ErrorCode is { } error) return error == "EQUIPMENT_PENDING";
        if (equipment.AppliedRevision is null || equipment.AppliedRevision < equipment.PublishedRevision)
            return true;
        if (!equipment.HasCurrentReceipt) return false;
        return equipment.HasCurrentLocalMicrophoneOverride ? !equipment.MatchesLocalMicrophoneOverride :
            equipment.MicrophoneId is not null && !equipment.MatchesPublishedMicrophone;
    }

    private bool TrackingPublishedChange() =>
        CurrentMicrophoneChange is { State: ChangeState.Accepted or ChangeState.Observed, Version: { } version } change &&
        (_equipment is null || _equipment.PublishedRevision < version ||
            (_equipment.PublishedRevision == version && MatchesChange(_equipment, change) &&
                AwaitingEquipmentReceipt(_equipment)));

    private bool NeedsLongPolling() => TrackingPublishedChange() || _equipment?.AllowsLocalMicrophoneChange == true;

    private void UpdateEquipmentTimer()
    {
        bool tracking = NeedsLongPolling();
        bool waiting = tracking || (_equipment is { } equipment && AwaitingEquipmentReceipt(equipment));
        if (waiting && _pollStarted == 0) _pollStarted = Stopwatch.GetTimestamp();
        if (!waiting) _pollStarted = 0;
        bool fast = waiting && Stopwatch.GetElapsedTime(_pollStarted) < FastPollDuration;
        if (!tracking && !fast && _equipment?.IsInventoryFresh != true)
        {
            _equipmentPoll.Stop();
            return;
        }

        var interval = tracking && !fast && !IsEditingEquipment ? SlowPollInterval : FastPollInterval;
        if (_equipmentPoll.Interval != interval) _equipmentPoll.Interval = interval;
        _equipmentPoll.Start();
    }

    private async void PollEquipment(object? sender, EventArgs args)
    {
        if (_device is not { } selection) return;
        RenderMicrophoneActions();
        if (_request is not null || IsEditingEquipment ||
            CurrentMicrophoneChange?.State is ChangeState.Saving or ChangeState.Unconfirmed ||
            _pollStarted == 0 || (Stopwatch.GetElapsedTime(_pollStarted) >= FastPollDuration && !NeedsLongPolling()))
            return;
        await LoadDeviceAsync(selection, background: true);
    }

    private void ResetEquipmentView()
    {
        CloseMicrophoneEditor();
        CloseLocalAuthorizationEditor();
        _equipment = null;
        _equipmentReadFailed = false;
        _pollStarted = 0;
        _equipmentPoll.Stop();
        MicrophoneActions.IsVisible = false;
    }

    private void CancelMicrophoneChanges()
    {
        _microphoneChanges.Clear();
        _microphoneSession?.Cancel();
        _microphoneSession?.Dispose();
        _microphoneSession = null;
    }
}
