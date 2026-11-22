using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class DeviceFieldActionWindow : Window
{
    private sealed class Operation(DeviceFieldRequest request)
    {
        internal DeviceFieldRequest Request { get; } = request;
        internal DeviceFieldResult? Receipt { get; set; }
        internal bool CheckedMissing { get; set; }
        internal long CheckedAt { get; set; }
    }

    private static readonly ConditionalWeakTable<ConsoleClient, Dictionary<(DeviceFieldScope, string), Operation>> Requests = new();
    private readonly ConsoleClient _client = null!;
    private readonly DeviceFieldScope _scope = null!;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private DeviceFieldState? _state;
    private DeviceFieldRequest? _confirm;
    private string _targetLabel = "";
    private string _originalDevice = "";
    private long _checkedAt;
    private bool _closed, _busy, _forbidden;

    public DeviceFieldActionWindow() => InitializeComponent();

    internal DeviceFieldActionWindow(ConsoleClient client, DeviceFieldScope scope, string school, string classroom, string screen, string device) : this()
    {
        _client = client;
        _scope = scope;
        _originalDevice = device;
        FieldTargetName.Text = _targetLabel = $"{school} · {classroom} · {screen}";
        FieldConfirmation.Approved += async (_, _) =>
        {
            if (_confirm is not { } request) return;
            _confirm = null;
            await BeginAsync(request);
            if (!_closed) FieldRead.Focus();
        };
        FieldConfirmation.Dismissed += (_, _) =>
        {
            _confirm = null;
            Render();
            FieldRevoke.Focus();
        };
        Opened += async (_, _) =>
        {
            BatchWorkspaceWindow.FitSchoolWindow(this);
            Arrange();
            await RunAsync(ReadAsync);
            if (!_closed) _timer.Start();
        };
        SizeChanged += (_, _) => Arrange();
        Closed += (_, _) => { _closed = true; _timer.Stop(); _lifetime.Cancel(); _lifetime.Dispose(); };
        _timer.Tick += async (_, _) =>
        {
            if (_closed || _busy || _confirm is not null || _forbidden) return;
            if (FindOperation("RESUME")?.Receipt is { ExpiresAt: { } expiry } current &&
                (current.Outcome == "QUEUED" || current is { Outcome: "ACCEPTED", SampleAt: null } && DateTimeOffset.UtcNow < expiry.AddSeconds(20)))
                await RunAsync(ReadAsync);
            else Render();
        };
        Render();
    }

    internal Action<ConsoleApiException>? SessionExpired { get; init; }
    internal bool Revoked { get; private set; }
    private Dictionary<(DeviceFieldScope, string), Operation> Operations => Requests.GetOrCreateValue(_client);
    private Operation? FindOperation(string action) => Operations.GetValueOrDefault((_scope, action));
    private Operation? Pending => FindOperation("RESUME") is { Receipt: null } resume ? resume :
        FindOperation("REVOKE") is { Receipt: null } revoke ? revoke : null;

    private async void ReadClicked(object? sender, RoutedEventArgs args) => await RunAsync(ReadAsync);
    private async void VerifyClicked(object? sender, RoutedEventArgs args)
    {
        if (Pending is { } pending) await RunAsync(() => VerifyAsync(pending));
    }
    private async void RetryClicked(object? sender, RoutedEventArgs args)
    {
        if (Pending is { CheckedMissing: true } pending) await RunAsync(() => SubmitAsync(pending));
    }
    private async void ResumeClicked(object? sender, RoutedEventArgs args)
    {
        if (FieldResume.IsEnabled && _state is { Resume.ReviewToken: { } proof } state)
            await BeginAsync(new(_scope, state.Target.BindingRevision, "RESUME", proof, Guid.NewGuid()));
    }
    private void RevokeClicked(object? sender, RoutedEventArgs args)
    {
        if (!FieldRevoke.IsEnabled || _state is not { Revoke.ReviewToken: { } proof } state) return;
        _confirm = new(_scope, state.Target.BindingRevision, "REVOKE", proof, Guid.NewGuid());
        FieldConfirmation.Present($"{_targetLabel}\n原生设备 {state.Target.DeviceName}（{_scope.DeviceId}）",
            "旧凭据下次连接会被拒绝。现场按原流程重新配对。离线设备是否还在本地运行需到教室核对。");
        Render();
        Dispatcher.UIThread.Post(() => { if (!_closed && _confirm is not null) FieldConfirmation.BringIntoView(); }, DispatcherPriority.Loaded);
    }
    private void ReturnClicked(object? sender, RoutedEventArgs args)
    {
        if (_confirm is not null) FieldConfirmation.Dismiss();
        else Close();
    }

    private async Task BeginAsync(DeviceFieldRequest request)
    {
        if (_busy || _closed || _forbidden || Pending is not null) return;
        var operation = new Operation(request);
        Operations[(_scope, request.Action)] = operation;
        await RunAsync(() => SubmitAsync(operation));
    }

    private async Task SubmitAsync(Operation operation)
    {
        operation.CheckedMissing = false;
        try
        {
            var receipt = await _client.SubmitDeviceFieldActionAsync(operation.Request, _lifetime.Token);
            if (!_closed) Record(operation, receipt);
        }
        catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or >= 500)
        {
            if (_closed) return;
            FieldNotice.Text = "成功响应未收到，正在只读核对本人请求。";
            await VerifyAsync(operation);
            return;
        }
        catch (ConsoleApiException error)
        {
            Operations.Remove((_scope, operation.Request.Action));
            if (error.StatusCode != 409 || _closed) throw;
            FieldNotice.Text = error.Message;
            await ReadStateAsync();
            return;
        }
        if (!_closed) await ReadStateAsync();
    }

    private async Task VerifyAsync(Operation operation)
    {
        var result = await _client.ReadDeviceFieldResultAsync(operation.Request, _lifetime.Token);
        if (_closed) return;
        if (result is null)
        {
            if (operation.Receipt is null)
            {
                operation.CheckedMissing = true;
                FieldNotice.Text = "暂未找到本人回执。可继续核对，或沿用原编号和原目标重试。";
            }
            return;
        }
        Record(operation, result);
        await ReadStateAsync();
    }

    private void Record(Operation operation, DeviceFieldResult result)
    {
        operation.Receipt = result;
        operation.CheckedMissing = false;
        operation.CheckedAt = Stopwatch.GetTimestamp();
        Revoked |= result.Outcome == "REVOKED";
        FieldNotice.Text = "已核对本账号的办理回执。";
    }

    private async Task ReadAsync()
    {
        FieldNotice.Text = "";
        if (Pending is { } pending) { await VerifyAsync(pending); return; }
        await ReadStateAsync();
        foreach (string action in new[] { "RESUME", "REVOKE" })
        {
            if (_closed) return;
            if (FindOperation(action) is not { Receipt: not null } operation) continue;
            if (await _client.ReadDeviceFieldResultAsync(operation.Request, _lifetime.Token) is { } result && !_closed)
                Record(operation, result);
        }
    }

    private async Task ReadStateAsync()
    {
        _checkedAt = Stopwatch.GetTimestamp();
        _state = null;
        try
        {
            var state = await _client.ReadDeviceFieldStateAsync(_scope, _lifetime.Token);
            if (!_closed) _state = state;
        }
        catch (ConsoleApiException error) when (error.Code == "BINDING_CHANGED" || error.StatusCode == 404)
        { if (!_closed) FieldNotice.Text = "原设备或大屏归属已变化，请返回设备目录重新定位。"; }
    }

    private async Task RunAsync(Func<Task> work)
    {
        if (_busy || _closed || _forbidden) return;
        _busy = true;
        Render();
        try { await work(); }
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error)
        {
            if (_closed) return;
            if (error.StatusCode is 401 or 403)
            {
                _forbidden = true;
                _state = null;
                _confirm = null;
                FieldConfirmation.Clear();
                SessionExpired?.Invoke(error);
            }
            if (!_closed) FieldNotice.Text = error.Message;
        }
        finally { _busy = false; if (!_closed) Render(); }
    }

    private void Render()
    {
        var target = _state?.Target;
        var resume = _state?.Resume;
        var pending = Pending;
        bool available = !_busy && !_forbidden && _confirm is null;
        bool fresh = _checkedAt != 0 && Stopwatch.GetElapsedTime(_checkedAt) < TimeSpan.FromSeconds(12);
        FieldDeviceName.Text = _forbidden ? "原生设备信息暂不可读" :
            $"原生设备 {target?.DeviceName ?? _originalDevice} · 最近联系 {Stamp(target?.LastSeenAt)}";
        FieldIdentifiers.Text = _forbidden ? "" : $"大屏绑定 {_scope.BindingId} · 原生设备 {_scope.DeviceId}" +
            (target is null ? "" : $" · 绑定版本 {target.BindingRevision}");
        FieldWindow.Text = resume?.Window is { } window
            ? $"{(resume.Mode == "Exam" ? "考试" : "日常")} · {window.Label}（学校时间） · 证据 {Stamp(resume.ObservedAt)}"
            : "暂无可核对的当前场次";
        FieldResumeReason.Text = _state is null ? "读取当前设备状态后可核对恢复条件。" : !fresh && resume?.ReviewToken is not null
            ? "恢复条件已过期，请刷新。" : ResumeReason(resume!);
        if (resume?.CurrentSamplingAt is { } active)
            FieldResumeReason.Text += fresh ? $"\n核对时有本屏定时采样 · {Stamp(active)}" : $"\n上次采样证据 · {Stamp(active)}；当前待核对。";
        FieldAuthorizationState.Text = _state?.CurrentDevice is { } occupant && occupant.DeviceId != _scope.DeviceId
            ? $"原设备 {target!.DeviceName} 已不是当前配对。现为 {occupant.DeviceName}（{occupant.DeviceId}），请返回并明确选中新设备。"
            : target?.State switch { "ACTIVE" => "原生授权有效，设备离线时仍可撤销。", "REVOKED" => "原配对的授权已撤销。",
                "EXPIRED" => "原配对的凭据已到期。", "INVALIDATED" => "原配对已失效。", _ => "授权状态待核对。" };
        FieldResumeReceipt.Text = _forbidden ? "" : Describe(FindOperation("RESUME"));
        FieldRevokeReceipt.Text = _forbidden ? "" : Describe(FindOperation("REVOKE"));
        FieldResume.IsEnabled = available && pending is null && fresh && resume?.ReviewToken is not null;
        FieldRevoke.IsEnabled = available && pending is null && _state?.Revoke.ReviewToken is not null;
        FieldRevoke.IsVisible = FieldRead.IsVisible = _confirm is null;
        FieldRead.IsEnabled = available;
        FieldVerify.IsVisible = !_forbidden && pending is not null;
        FieldRetry.IsVisible = !_forbidden && pending?.CheckedMissing == true;
        FieldVerify.IsEnabled = FieldRetry.IsEnabled = available;
    }

    private static string Stamp(DateTimeOffset? value) => value?.ToOffset(TimeSpan.FromHours(8)).ToString("M/d HH:mm:ss") ?? "待核对";
    private static string ResumeReason(DeviceFieldResume resume) => resume.Reason switch
    {
        "READY" => resume.ExecutionReason == "WINDOW_FAILED" ? "本场定时监测曾启动失败，可请求恢复。" : "本场定时监测已停止，可请求恢复。",
        "MONITORING_DISABLED" => "学校当前策略未启用定时监测。",
        "OBSERVATION_STALE" => "尚未读取到在线设备的新鲜策略回执。",
        "POLICY_NOT_APPLIED" => "等待设备应用当前策略。",
        "STUDY_POLICY_UNAVAILABLE" => "自习策略暂不可读取，无法核对恢复条件。设备授权可单独办理。",
        "STUDY_RESUME_UNAVAILABLE" => "设备尚未确认支持恢复。请刷新或核对教室端版本。",
        "EXECUTION_STALE" => "当前场次缺少新鲜的执行回报。",
        "MANUAL_OCCUPIED" => "现场正在手动监测，请由教室处理。",
        "CLOCK_UNAVAILABLE" => "教室尚未取得可靠时钟，无法核对场次。",
        "WINDOW_NOT_ACTIVE" => "目前不在本场可恢复的时间窗内。",
        "WINDOW_NOT_STOPPED" => "当前没有可恢复的本场停止或失败记录。",
        "COMMAND_PENDING" => "已有恢复请求在等待设备处理。",
        "DEVICE_UNAVAILABLE" => "原设备配对已失效或已更换。",
        _ => "暂无法确认本场可恢复，请刷新或到现场核对。"
    };

    private static string Describe(Operation? operation)
    {
        if (operation is null) return "";
        string number = $"请求编号 {operation.Request.Id:D}";
        if (operation.Receipt is not { } result) return "办理结果待核对。\n" + number;
        if (result.Outcome == "REVOKED") return $"学校已撤销原生授权 · {Stamp(result.AcceptedAt)}。\n旧凭据下次连接将被拒绝；现场按原流程重新配对。\n{number}";
        string detail = result.Outcome switch
        {
            "QUEUED" => "等待设备处理，采样待核对。",
            "ACCEPTED" when result.SampleAt is { } sample => $"设备已处理 · {Stamp(result.DeviceProcessedAt)}\n本次恢复首次采样 · {Stamp(sample)}\n" +
                (result.SampleFresh && Stopwatch.GetElapsedTime(operation.CheckedAt) < TimeSpan.FromSeconds(12)
                    ? "核对时仍有本次恢复的采样信号。" : "本次采样为历史证据，当前状态见上方。"),
            "ACCEPTED" => $"设备已处理 · {Stamp(result.DeviceProcessedAt)}\n本场新鲜定时采样仍待核对。",
            "REJECTED" => "设备拒绝恢复，请查看当前场次或到教室核对。",
            _ => "指令已过期或原办理条件已变化，设备处理结果待核对。"
        };
        return $"学校已受理 · {Stamp(result.AcceptedAt)}\n{detail}\n{number}";
    }

    private void Arrange()
    {
        bool compact = ClientSize.Width is > 0 and < 900;
        FieldColumns.ColumnDefinitions[1].Width = compact ? new(0) : new(1, GridUnitType.Star);
        FieldColumns.ColumnSpacing = compact ? 0 : 8;
        Grid.SetColumn(FieldAuthorization, compact ? 0 : 1);
        Grid.SetRow(FieldAuthorization, compact ? 1 : 0);
    }
}
