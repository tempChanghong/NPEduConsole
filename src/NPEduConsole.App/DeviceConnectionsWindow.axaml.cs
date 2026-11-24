using Avalonia.Controls;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class DeviceConnectionsWindow : Window
{
    public sealed class Pending
    {
        public ConnectionBatch? Batch { get; set; }
        public Dictionary<Guid, ConnectionApproval> Approvals { get; } = [];
    }

    private sealed record BatchRange(string? Id, string Name)
    {
        public override string ToString() => Name;
    }

    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    public sealed record InitialTarget(string TermId, string ClassId, string ScreenId);
    private readonly InitialTarget? _initialTarget;
    private readonly Pending _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private ConnectionCatalog? _catalog;
    private ClassAdmission? _admission;
    private ConnectionBatch? _batch;
    private bool _busy, _writing, _filling, _closed, _ready;

    public Action<ConsoleApiException>? AccessLost { get; init; }
    public bool Changed { get; private set; }

    public DeviceConnectionsWindow() => InitializeComponent();

    public DeviceConnectionsWindow(ConsoleClient client, School school, InitialTarget? initialTarget, Pending pending) : this()
    {
        _client = client;
        _school = school;
        _initialTarget = initialTarget;
        _pending = pending;
        _ready = true;
        ConnectionTabs.SelectedIndex = pending.Approvals.Count > 0 && initialTarget is null ? 1 : 0;
        SelectApproval(null);
        ConnectionTabs.SelectionChanged += (_, args) =>
        {
            if (args.Source != ConnectionTabs) return;
            ClearSecret();
            ConnectionCode.Clear();
            Render();
        };
        foreach (var box in new[] { ConnectionPairingEnabled, ConnectionBatchConfirmed, ConnectionApprovalConfirmed, ConnectionEquipmentConfirmed })
            box.IsCheckedChanged += (_, _) => Render();
        ConnectionSecret.TextChanged += (_, _) => Render();
        ConnectionSecretAgain.TextChanged += (_, _) => Render();
        Opened += async (_, _) => await RunAsync(async ct =>
        {
            await ReadCatalogAsync(ct);
            if (SelectedScreen() is not null) await ReadAdmissionAsync(ct);
            if (_pending.Batch is { } waiting)
                ConnectionBatchState.Text = $"批量结果待核对 · {waiting.TermName} · {waiting.TargetName}";
            (ConnectionScreens.SelectedItem is not null ? ConnectionReadAdmission : ConnectionRefresh).Focus();
        });
        Closing += (_, args) => { if (_writing) args.Cancel = true; };
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
            ClearSecret();
            ConnectionCode.Clear();
            _candidate = null;
            _equipmentFreshnessTimer.Stop();
        };
        _equipmentFreshnessTimer.Tick += (_, _) => UpdateExpiredEquipment();
        Render();
    }

    internal void ClearAndClose()
    {
        _writing = false;
        _pending.Approvals.Clear();
        _pending.Batch = null;
        Close();
    }

    private void ExitClicked(object? sender, RoutedEventArgs args) => Close();
    private async void RefreshClicked(object? sender, RoutedEventArgs args) => await RunAsync(async ct =>
    {
        await ReadCatalogAsync(ct);
        if (ConnectionTabs.SelectedIndex == 1) await ReadApprovalAsync(ct, revealResult: false);
    });

    private async Task ReadCatalogAsync(CancellationToken ct)
    {
        var initial = _catalog is null ? _initialTarget : null;
        var selected = SelectedScreen();
        var equipment = ConnectionEquipmentScreens.SelectedItem as ConnectionScreen;
        string? previousScreen = selected?.Id ?? initial?.ScreenId;
        string? previousEquipment = equipment?.Id ?? previousScreen;
        var previousTerm = _catalog?.Term?.Id;
        _catalog = null;
        _approval = null;
        _approvalChecked = false;
        ResetAdmission();
        ResetBatch();
        ResetEquipment();
        _candidate = null;
        ConnectionCode.Clear();
        ConnectionCandidateInfo.Text = "";
        SetScreenOptions(null, null);
        _catalog = await _client.GetDeviceConnectionsAsync(_school.Id, ct);
        ct.ThrowIfCancellationRequested();
        var term = _catalog.Term;
        ConnectionScope.Text = term is null ? $"{_school.Name} · 当前没有启用学期"
            : $"{_school.Name} · 在用学期 {term.Name}（{term.Id}）";
        bool moved = previousScreen is not null && !_catalog.Screens.Any(row => row.Id == previousScreen && row.ClassId ==
            (selected?.ClassId ?? initial?.ClassId));
        if (moved || initial is not null && initial.TermId != term?.Id)
        {
            previousScreen = previousEquipment = null;
            SetMessage("原班级和大屏绑定已不在当前目录。请核对后明确选择要办理的大屏。");
        }
        if (previousTerm is not null && previousTerm != term?.Id) previousScreen = previousEquipment = null;
        if (equipment is not null && !_catalog.Screens.Any(row => row.Id == equipment.Id && row.ClassId == equipment.ClassId &&
            row.Device?.Id == equipment.Device?.Id)) previousEquipment = null;
        SetScreenOptions(previousScreen, previousEquipment);
        _filling = true;
        ConnectionBatchRange.ItemsSource = new[] { new BatchRange(null, "全校") }
            .Concat(_catalog.Grades.Select(g => new BatchRange(g.Id, g.Name))).ToArray();
        ConnectionBatchRange.SelectedIndex = term is null ? -1 : 0;
        ConnectionBatchChange.SelectedIndex = term is null ? -1 : 0;
        _filling = false;
        if (previousScreen is not null && SelectedScreen() is null)
            SetMessage("原大屏不在当前可办理目录。请核对学期、班级和绑定编号。");
        else if (term is null)
            SetMessage("先在「学期管理」启用学期。已批准申请仍可按原编号核对。");
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, bool writing = false)
    {
        if (_busy || _closed) return;
        _busy = true;
        _writing = writing;
        var ct = _lifetime.Token;
        SetMessage(null);
        Render();
        try { await action(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (ConsoleApiException error) when (error.StatusCode is 401 or 403)
        {
            if (!_closed)
            {
                ClearAndClose();
                AccessLost?.Invoke(error);
            }
        }
        catch (Exception error) when (error is ConsoleApiException or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            if (!_closed)
                SetMessage(error is ConsoleApiException ? error.Message : "暂时无法核对学校服务，请刷新后重试。");
        }
        finally
        {
            _busy = _writing = false;
            if (!_closed) Render();
        }
    }

    private void SetMessage(string? value)
    {
        ConnectionMessage.Text = value;
        ConnectionMessage.IsVisible = value is not null;
    }

    private ConnectionScreen? SelectedScreen() => ConnectionScreens.SelectedItem as ConnectionScreen;

    private void SetScreenOptions(string? selected, string? equipment)
    {
        var search = ConnectionScreenSearch.Text?.Trim();
        var screens = _catalog?.Screens ?? [];
        _filling = true;
        ConnectionScreens.ItemsSource = screens.Where(row => string.IsNullOrEmpty(search) ||
            row.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        ConnectionScreens.SelectedItem = screens.FirstOrDefault(row => row.Id == selected);
        if (ConnectionScreens.SelectedItem is null) ResetAdmission();
        ConnectionEquipmentScreens.ItemsSource = screens.Where(row => row.Device is not null).ToArray();
        ConnectionEquipmentScreens.SelectedItem = screens.FirstOrDefault(row => row.Id == equipment && row.Device is not null);
        _filling = false;
        SetApprovalScreens();
    }

    private void ScreenSearchChanged(object? sender, TextChangedEventArgs args)
    {
        if (_filling || _catalog is null) return;
        var selection = SelectedScreen()?.Id;
        var search = ConnectionScreenSearch.Text?.Trim();
        _filling = true;
        var rows = _catalog.Screens.Where(row => string.IsNullOrEmpty(search) ||
            row.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        ConnectionScreens.ItemsSource = rows;
        ConnectionScreens.SelectedItem = rows.FirstOrDefault(row => row.Id == selection);
        _filling = false;
        if (SelectedScreen() is null) ResetAdmission();
        Render();
    }

    private async void ScreenChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_filling || _catalog is null) return;
        ResetAdmission();
        await RunAsync(ReadAdmissionAsync);
    }

    private async void ReadAdmissionClicked(object? sender, RoutedEventArgs args) => await RunAsync(ReadAdmissionAsync);

    private async Task ReadAdmissionAsync(CancellationToken ct)
    {
        ResetAdmission();
        if (SelectedScreen() is not { } screen) return;
        var snapshot = await _client.GetConnectionAdmissionAsync(_school.Id, screen, ct);
        ct.ThrowIfCancellationRequested();
        _admission = snapshot;
        ConnectionPairingEnabled.IsChecked = snapshot.Enabled;
        ConnectionAdmissionState.Text = $"{screen.Name} · {(snapshot.Enabled ? "网页配对已开放" : "网页配对未开放")} · "
            + (snapshot.Configured ? $"本班已有管理口令（版本 {snapshot.Revision}）" : "本班尚无管理口令");
        if (!snapshot.SupportsGuardedSave) SetMessage("旧学校服务仅可查看，请按本站兼容入口办理。");
    }

    private void ResetAdmission()
    {
        _admission = null;
        ConnectionAdmissionState.Text = "";
        ClearSecret();
    }

    private void ClearSecret()
    {
        ConnectionRotate.IsChecked = false;
        ConnectionSecret.Clear();
        ConnectionSecretAgain.Clear();
    }

    private void RotateChanged(object? sender, RoutedEventArgs args)
    {
        if (!_ready) return;
        if (ConnectionRotate.IsChecked != true)
        {
            ConnectionSecret.Clear();
            ConnectionSecretAgain.Clear();
        }
        Render();
    }

    private async void SaveAdmissionClicked(object? sender, RoutedEventArgs args)
    {
        if (!CanSaveAdmission() || _admission is not { } snapshot) return;
        var enabled = ConnectionPairingEnabled.IsChecked == true;
        var secret = ConnectionRotate.IsChecked == true ? ConnectionSecret.Text : null;
        if (secret is not null && !ClassAdmission.IsValidSecret(secret))
        {
            SetMessage("请输入 8 至 128 位管理口令，避免换行或控制字符。");
            ConnectionSecret.Focus();
            return;
        }
        await RunAsync(async ct =>
        {
            ClearSecret();
            _admission = null;
            ConnectionAdmissionState.Text = "正在保存，结果待核对。";
            await _client.UpdateConnectionAdmissionAsync(snapshot, enabled, secret, Guid.NewGuid(), ct);
            Changed = true;
            SetMessage("学校已保存逐屏设置。");
            EnableReadWhileChecking();
            await ReadAdmissionAsync(ct);
        }, writing: true);
    }

    private bool CanSaveAdmission()
    {
        if (_admission is not { SupportsGuardedSave: true } snapshot) return false;
        if (ConnectionRotate.IsChecked == true)
            return !string.IsNullOrEmpty(ConnectionSecret.Text) && ConnectionSecret.Text == ConnectionSecretAgain.Text;
        var enabled = ConnectionPairingEnabled.IsChecked == true;
        return enabled != snapshot.Enabled && (!enabled || snapshot.Configured);
    }

    private void BatchOptionsChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!_ready || _filling) return;
        ResetBatch();
        Render();
    }

    private void ResetBatch()
    {
        _batch = null;
        ConnectionBatchRows.ItemsSource = null;
        ConnectionBatchConfirmed.IsChecked = false;
        ConnectionBatchState.Text = _pending.Batch is { } pending
            ? $"原批量保存结果待核对 · {pending.TermName} · {pending.TargetName}" : "";
    }

    private async void BatchPreviewClicked(object? sender, RoutedEventArgs args)
    {
        if (_pending.Batch is not null || _catalog is not { Term: not null } catalog ||
            ConnectionBatchRange.SelectedItem is not BatchRange range || ConnectionBatchChange.SelectedIndex < 0) return;
        var enable = ConnectionBatchChange.SelectedIndex == 0;
        await RunAsync(async ct =>
        {
            ResetBatch();
            _batch = await _client.PreviewConnectionBatchAsync(catalog, range.Id, enable, ct);
            ct.ThrowIfCancellationRequested();
            ConnectionBatchRows.ItemsSource = _batch.Items;
            ConnectionBatchState.Text = BatchText(_batch);
        });
    }

    private static string BatchText(ConnectionBatch batch)
    {
        string text = $"{batch.TermName} · {batch.TargetName} · 共 {batch.TotalScreens} 台可办理，变更 {batch.ChangedScreens} 台 / 保留 {batch.UnchangedScreens} 台 / 不适用 {batch.ExcludedScreens} 台";
        if (batch.Truncated) text += "。下方列出前 50 台，确认适用于上方完整范围。";
        if (batch.MissingCredentials.Length != 0)
            text += "\n以下班级尚无管理口令，请先逐班设置：" + string.Join("、", batch.MissingCredentials.Select(row => row.ClassName));
        return text;
    }

    private async void BatchApplyClicked(object? sender, RoutedEventArgs args)
    {
        if (_batch is not { ChangedScreens: > 0, MissingCredentials.Length: 0 } preview || ConnectionBatchConfirmed.IsChecked != true) return;
        await RunAsync(async ct =>
        {
            _pending.Batch = preview;
            ResetBatch();
            try
            {
                var applied = await _client.ApplyConnectionBatchAsync(preview, ct);
                _pending.Batch = null;
                Changed = true;
                ConnectionBatchState.Text = "学校已保存本次批量设置。\n" + BatchText(applied);
            }
            catch (ConsoleApiException error) when (error.Code != "CONNECTION_UNCONFIRMED")
            {
                _pending.Batch = null;
                ResetBatch();
                throw;
            }
        }, writing: true);
    }

    private async void BatchCheckClicked(object? sender, RoutedEventArgs args)
    {
        if (_pending.Batch is not { } pending) return;
        await RunAsync(async ct =>
        {
            var catalog = await _client.GetDeviceConnectionsAsync(_school.Id, ct);
            if (catalog.Term?.Id != pending.TermId || pending.TargetId is { } grade && !catalog.Grades.Any(item => item.Id == grade))
            {
                _pending.Batch = null;
                ConnectionBatchState.Text = $"{pending.TermName} · {pending.TargetName} 已无法按原范围核对。本次保存结果未知。";
                return;
            }
            var current = await _client.PreviewConnectionBatchAsync(catalog, pending.TargetId, pending.Enabled, ct);
            _pending.Batch = null;
            var rosterChanged = !pending.Items.Select(item => item.ScreenBindingId).ToHashSet(StringComparer.Ordinal)
                .SetEquals(current.Items.Select(item => item.ScreenBindingId));
            ConnectionBatchState.Text = $"当前范围内的 {current.TotalScreens} 台可办理大屏中，"
                + (current.ChangedScreens == 0 ? "均已达到所选状态。" : $"仍有 {current.ChangedScreens} 台未达到所选状态。")
                + (rosterChanged ? " 可见的绑定名单与原预览不同。" : " 请以当前名单为准，原名单可能已变化。")
                + " 无法认定本人这次是否保存成功。未重发原请求。\n" + BatchText(current);
            Changed = true;
        });
    }

    private void EnableReadWhileChecking()
    {
        _writing = false;
        Render();
    }

    private void Render()
    {
        if (_closed || !_ready) return;
        ConnectionTabs.IsEnabled = !_busy;
        ConnectionExit.IsEnabled = !_writing;
        ConnectionRefresh.IsEnabled = !_busy;
        var tab = ConnectionTabs.SelectedIndex;
        ConnectionReadAdmission.IsEnabled = SelectedScreen() is not null;
        ConnectionAdmissionForm.IsVisible = _admission is not null;
        ConnectionAdmissionForm.IsEnabled = _admission?.SupportsGuardedSave == true;
        ConnectionSecretFields.IsVisible = ConnectionRotate.IsChecked == true;
        ConnectionAdmissionSave.IsVisible = tab == 0 && _admission is not null && (_batch is null || CanSaveAdmission());
        ConnectionAdmissionSave.IsEnabled = !_busy && CanSaveAdmission();
        ConnectionBatchPreview.IsEnabled = _catalog?.Term is not null && _pending.Batch is null;
        ConnectionBatchCheck.IsVisible = _pending.Batch is not null;
        ConnectionBatchRows.IsVisible = _batch is not null;
        var canApply = tab == 0 && _batch is { ChangedScreens: > 0, MissingCredentials.Length: 0 };
        ConnectionBatchConfirmed.IsVisible = canApply;
        ConnectionBatchConfirmed.IsEnabled = !_busy;
        ConnectionBatchApply.IsVisible = canApply;
        ConnectionBatchApply.IsEnabled = !_busy && ConnectionBatchConfirmed.IsChecked == true;
        RenderApproval(tab);
        RenderEquipment(tab);
    }
}
