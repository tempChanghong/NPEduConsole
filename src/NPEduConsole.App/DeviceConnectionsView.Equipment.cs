using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class DeviceConnectionsView
{
    private sealed record EquipmentChoice(string? Id, string Name)
    {
        public override string ToString() => Name;
    }

    private readonly Dictionary<string, long> _savedEquipment = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _equipmentFreshnessTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Equipment? _equipment;

    private void ResetEquipment()
    {
        _equipmentFreshnessTimer.Stop();
        _equipment = null;
        ConnectionEquipmentConfirmed.IsChecked = false;
        ConnectionEquipmentState.Text = ConnectionEquipmentScreens.SelectedItem is ConnectionScreen { Device: { } device }
            ? $"{device.Name}（原生设备 {device.Id}）" : "";
    }

    private async void EquipmentScreenChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!_ready || _filling || _catalog is null) return;
        ResetEquipment();
        await RunAsync(ReadEquipmentAsync);
    }

    private async void ReadEquipmentClicked(object? sender, RoutedEventArgs args) => await RunAsync(ReadEquipmentAsync);

    private async Task ReadEquipmentAsync(CancellationToken ct)
    {
        ResetEquipment();
        if (ConnectionEquipmentScreens.SelectedItem is not ConnectionScreen { Device: { } device } screen) return;
        var saved = await _client.GetEquipmentAsync(_school.Id, device.Id, ct);
        ct.ThrowIfCancellationRequested();
        _filling = true;
        SetEquipmentChoices(ConnectionMicrophone, saved.Microphones.Select(item => new EquipmentChoice(item.Id, item.Name)), saved.MicrophoneId);
        SetEquipmentChoices(ConnectionCamera, saved.Cameras.Select(item => new EquipmentChoice(item.Id, item.Name)), saved.CameraId);
        ConnectionEquipmentLocked.IsChecked = saved.Locked;
        _filling = false;
        _equipment = saved;
        ShowEquipmentObservation(screen, device, saved);
        if (saved.IsInventoryFresh) _equipmentFreshnessTimer.Start();
    }

    private void UpdateExpiredEquipment()
    {
        if (_closed || _equipment is not { IsInventoryFresh: false } saved ||
            ConnectionEquipmentScreens.SelectedItem is not ConnectionScreen { Device: { } device } screen) return;
        _equipmentFreshnessTimer.Stop();
        ShowEquipmentObservation(screen, device, saved);
        ConnectionEquipmentConfirmed.IsChecked = false;
        Render();
    }

    private void ShowEquipmentObservation(ConnectionScreen screen, ConnectionDevice device, Equipment saved)
    {
        var receipt = saved.ReceivedAt is { } at ? $"Host 回执版本 {saved.AppliedRevision} · {at.LocalDateTime:g}" : "暂无 Host 回执";
        var accepted = _savedEquipment.TryGetValue(device.Id, out var revision)
            ? $"本次保存版本 {revision}。 " : "";
        ConnectionEquipmentState.Text = $"{screen.ClassName} · {screen.Name} · {device.Name}（{device.Id}）\n"
            + $"{accepted}学校当前版本 {saved.PublishedRevision} · {receipt}。{DescribeEquipmentObservation(saved)}";
    }

    private static string DescribeEquipmentObservation(Equipment saved)
    {
        if (saved.ReceivedAt is not null && !saved.IsInventoryFresh)
            return saved.HasCurrentReceipt ? "曾收到此版本回执，当前清单已过期。" : "当前清单已过期，等待设备重新上报。";
        if (saved.ErrorCode is not null || saved.AppliedRevision > saved.PublishedRevision) return saved.StatusText;
        if (!saved.HasCurrentReceipt) return "当前版本待设备应用或核对。";

        var observations = new List<string>();
        if (saved.HasCurrentLocalMicrophoneOverride || saved.MicrophoneId is not null && !saved.MatchesPublishedMicrophone)
            observations.Add(saved.StatusText);
        if (saved.CameraId is { } camera && (saved.ReportedCameraId != camera || !saved.Cameras.Any(item => item.Id == camera)))
            observations.Add("尚未确认使用学校指定的摄像头");
        if (saved.Locked && saved.LocallyUnlocked != false) observations.Add(saved.EditingStatusText);
        return observations.Count == 0 ? "Host 已报告应用当前版本。" : string.Join("。", observations) + "。";
    }

    private static void SetEquipmentChoices(ComboBox control, IEnumerable<EquipmentChoice> source, string? chosen)
    {
        var choices = new List<EquipmentChoice> { new(null, "清除学校指定") };
        choices.AddRange(source.Where(choice => choice.Id is not null).DistinctBy(choice => choice.Id));
        if (!choices.Any(item => item.Id == chosen)) choices.Add(new(chosen, "原指定未在当前设备清单中"));
        control.ItemsSource = choices;
        control.SelectedItem = choices.Single(item => item.Id == chosen);
    }

    private void EquipmentOptionsChanged(object? sender, RoutedEventArgs args)
    {
        if (!_ready || _filling) return;
        ConnectionEquipmentConfirmed.IsChecked = false;
        Render();
    }

    private bool CanSaveEquipment() => _equipment is { IsInventoryFresh: true } snapshot &&
        ConnectionMicrophone.SelectedItem is EquipmentChoice mic && ConnectionCamera.SelectedItem is EquipmentChoice camera &&
        (mic.Id, camera.Id, ConnectionEquipmentLocked.IsChecked == true) != (snapshot.MicrophoneId, snapshot.CameraId, snapshot.Locked);

    private async void SaveEquipmentClicked(object? sender, RoutedEventArgs args)
    {
        if (_equipment is { IsInventoryFresh: false })
        {
            UpdateExpiredEquipment();
            SetMessage("设备清单已过期，请等设备重新上报后再读取。");
            return;
        }
        if (!CanSaveEquipment() || ConnectionEquipmentConfirmed.IsChecked != true || _equipment is not { } snapshot ||
            ConnectionMicrophone.SelectedItem is not EquipmentChoice microphone || ConnectionCamera.SelectedItem is not EquipmentChoice camera ||
            ConnectionEquipmentScreens.SelectedItem is not ConnectionScreen { Device: { } device }) return;
        bool locked = ConnectionEquipmentLocked.IsChecked == true;
        await RunAsync(async ct =>
        {
            ResetEquipment();
            var version = await _client.SetConnectionEquipmentAsync(snapshot, microphone.Id, camera.Id, locked, Guid.NewGuid(), ct);
            _savedEquipment[device.Id] = version;
            Changed = true;
            SetMessage($"学校已保存版本 {version}，请核对 Host 应用回执。");
            EnableReadWhileChecking();
            await ReadEquipmentAsync(ct);
        }, writing: true);
    }

    private void RenderEquipment(int tab)
    {
        ConnectionReadEquipment.IsEnabled = ConnectionEquipmentScreens.SelectedItem is not null;
        ConnectionEquipmentForm.IsVisible = _equipment is not null;
        ConnectionEquipmentForm.IsEnabled = _equipment?.IsInventoryFresh == true;
        ConnectionEquipmentConfirmed.IsVisible = tab == 2 && CanSaveEquipment();
        ConnectionEquipmentConfirmed.IsEnabled = !_busy;
        ConnectionEquipmentSave.IsVisible = tab == 2 && _equipment is not null;
        ConnectionEquipmentSave.IsEnabled = !_busy && CanSaveEquipment() && ConnectionEquipmentConfirmed.IsChecked == true;
    }
}
