using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NPEduConsole.Core;
using ClassroomScreen = NPEduConsole.Core.Screen;

namespace NPEduConsole.App;

public sealed partial class ExamWorkbenchWindow : Window
{
    private enum ActionKind { Enter, Return, Deliver, Start, CancelPlan, CancelMode }
    private sealed record DeviceChoice(Device Device)
    {
        public override string ToString() => $"{Device.Name}（{Device.Id}）";
    }
    private sealed record Intent(ActionKind Action, Guid RequestId, ExamRuntimeState? Runtime = null,
        ExamPlanState? Plans = null, ExamPlanOperation? Plan = null, ClassroomExamFile? File = null, Guid? OperationId = null);
    private static readonly ConditionalWeakTable<ConsoleClient, Dictionary<ExamScope, Intent>> PendingWrites = new();

    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly Classroom _room = null!;
    private readonly ClassroomScreen _screen = null!;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _target;
    private ExamScope? _scope;
    private ClassroomExamState? _state;
    private ClassroomExamFile? _file;
    private string? _readError, _notice;
    private bool _opened, _closed, _reading, _working, _picking, _allowRetry, _noticeError;
    private long _polled;
    private Guid? _visibleSchedule;
    private ActionKind _primary, _cancel;
    private Intent? Pending => _scope is { } scope ? PendingWrites.GetOrCreateValue(_client).GetValueOrDefault(scope) : null;
    private ClassroomExamState? Live => _readError is null ? _state : null;
    private ExamPlanOperation? Plan => _state is { } state ? CurrentPlans(state).OrderBy(item => item.IsTerminal).FirstOrDefault() : null;

    private static IEnumerable<ExamPlanOperation> CurrentPlans(ClassroomExamState state)
    {
        // Returning to daily mode completes the previous exam cycle. Older plans remain in history.
        var restoredAt = state.RuntimeHistory.FirstOrDefault(item => item is { Target: "DAILY", State: "SUCCEEDED" })?.ResolvedAt;
        return state.Plans.Items.Where(item => !item.IsTerminal || restoredAt is null || item.CreatedAt >= restoredAt.Value);
    }

    public ExamWorkbenchWindow() => InitializeComponent();

    internal ExamWorkbenchWindow(ConsoleClient client, School school, Classroom room, ClassroomScreen screen, string? deviceId) : this()
    {
        _client = client;
        _school = school;
        _room = room;
        _screen = screen;
        ExamTargetText.Text = $"{school.Name} · {room.Name} · {screen.Name} · 绑定编号 {screen.Id}";
        var choices = screen.Devices.Select(device => new DeviceChoice(device)).ToArray();
        ExamDevicePicker.ItemsSource = choices;
        ExamDevicePicker.SelectedItem = choices.FirstOrDefault(item => item.Device.Id == deviceId) ?? choices.FirstOrDefault();
        Opened += async (_, _) =>
        {
            BatchWorkspaceWindow.FitSchoolWindow(this);
            _opened = true;
            await ReadDeviceAsync();
            if (!_closed) _timer.Start();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            ClearTargetRead();
            _file = null;
        };
        _timer.Tick += async (_, _) =>
        {
            Render();
            if (_scope is not null && !_reading && !_working && Stopwatch.GetElapsedTime(_polled) >= TimeSpan.FromSeconds(5))
                await RefreshAsync();
        };
    }

    private async void DeviceChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_opened && !_closed) await ReadDeviceAsync();
    }

    private async Task ReadDeviceAsync()
    {
        ClearTargetRead();
        _state = null;
        _file = null;
        _readError = _notice = null;
        _visibleSchedule = null;
        ExamSchedule.SetSchedule(null);
        _allowRetry = _reading = _working = _picking = false;
        _scope = ExamDevicePicker.SelectedItem is DeviceChoice choice
            ? new(_school.Id, _room.Id, _screen.Id, choice.Device.Id) : null;
        _target = new CancellationTokenSource();
        if (_scope is null) { Render(); return; }
        await RefreshAsync(checkPending: true);
    }

    private void ClearTargetRead()
    {
        _target?.Cancel();
        _target?.Dispose();
        _target = null;
    }

    private bool Current(ExamScope scope, CancellationToken ct) => !_closed && _scope == scope && !ct.IsCancellationRequested;

    private async Task RefreshAsync(bool checkPending = false)
    {
        if (_scope is not { } scope || _target is null || _working || _reading) return;
        var ct = _target.Token;
        _reading = true;
        _polled = Stopwatch.GetTimestamp();
        Render();
        try
        {
            await ReadStateAsync(scope, ct);
            if (Current(scope, ct) && checkPending && Pending is { } intent) await ReconcileAsync(scope, intent, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (ConsoleApiException error)
        {
            if (Current(scope, ct)) _readError = error.Message;
        }
        finally
        {
            if (Current(scope, ct)) { _reading = false; Render(); }
        }
    }

    private async Task ReadStateAsync(ExamScope scope, CancellationToken ct)
    {
        var previous = Plan?.Summary?.PreparationId;
        var state = await _client.GetClassroomExamAsync(scope, ct);
        if (!Current(scope, ct)) return;
        _state = state;
        _readError = null;
        if (Plan is { State: "PREPARED", Summary: { } summary } && summary.PreparationId != previous)
        {
            Render();
            Dispatcher.UIThread.Post(() =>
            {
                if (Current(scope, ct) && Plan?.Summary?.PreparationId == summary.PreparationId && ExamSchedulePanel.IsVisible)
                    ExamSchedule.FocusFirstEntry();
            }, DispatcherPriority.Loaded);
        }
    }

    private async void PickClicked(object? sender, RoutedEventArgs args)
    {
        if (_picking || _working || _scope is not { } scope || _target is null || Pending is not null) return;
        var ct = _target.Token;
        _picking = true;
        Render();
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择 ExamAware 考试方案",
                FileTypeFilter = [new FilePickerFileType("考试方案") { Patterns = ["*.ea2", "*.json"] }]
            });
            if (!Current(scope, ct) || files.Count == 0) return;
            await using var stream = await files[0].OpenReadAsync();
            await LoadExamFileAsync(files[0].Name, stream);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (Current(scope, ct)) Notice("无法读取此文件，请重新选择。", true);
        }
        finally { if (Current(scope, ct)) { _picking = false; Render(); } }
    }

    public async Task LoadExamFileAsync(string name, Stream source)
    {
        if (_scope is not { } scope || _target is null || Pending is not null) return;
        var ct = _target.Token;
        _file = null;
        try
        {
            var bytes = new byte[ClassroomExamFile.MaxBytes + 1];
            int length = await source.ReadAtLeastAsync(bytes, bytes.Length, false, ct);
            if (!Current(scope, ct)) return;
            _file = ClassroomExamFile.Read(name, bytes[..length]);
            Notice("");
        }
        catch (ArgumentException error) { if (Current(scope, ct)) Notice(error.Message, true); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { if (Current(scope, ct)) Render(); }
    }

    private async void PrimaryClicked(object? sender, RoutedEventArgs args)
    {
        if (_working || _reading || _scope is not { } scope || _target is null) return;
        if (Pending is { } pending)
        {
            await HandlePendingAsync(scope, pending, resend: false);
            return;
        }
        if (!ExamPrimary.IsEnabled || Live is not { } state) return;
        var intent = new Intent(_primary, Guid.NewGuid(), state.Runtime, state.Plans, Plan, _file);
        await HandlePendingAsync(scope, intent, resend: true);
    }

    private async void CancelClicked(object? sender, RoutedEventArgs args)
    {
        if (_working || _reading || Pending is not null || _scope is not { } scope || !ExamCancel.IsEnabled) return;
        var operationId = _cancel == ActionKind.CancelPlan ? Plan?.OperationId : Live?.Runtime.UnresolvedOperationId;
        if (operationId is not { } id) return;
        await HandlePendingAsync(scope, new(_cancel, Guid.NewGuid(), OperationId: id), resend: true);
    }

    private async void RetryClicked(object? sender, RoutedEventArgs args)
    {
        if (_working || _reading || !_allowRetry || _scope is not { } scope || Pending is not { } intent) return;
        await HandlePendingAsync(scope, intent, resend: true);
    }

    private async Task HandlePendingAsync(ExamScope scope, Intent intent, bool resend)
    {
        if (_target is null) return;
        var ct = _target.Token;
        _working = true;
        _allowRetry = false;
        Render();
        try
        {
            if (resend)
            {
                PendingWrites.GetOrCreateValue(_client)[scope] = intent;
                try
                {
                    switch (intent.Action)
                    {
                        case ActionKind.Enter: case ActionKind.Return:
                            await _client.RequestExamModeAsync(scope, intent.Runtime!, intent.Action == ActionKind.Enter ? "EXAM" : "DAILY", intent.RequestId, ct);
                            break;
                        case ActionKind.Deliver:
                            await _client.DeliverExamPlanAsync(scope, intent.Plans!, intent.File!, intent.RequestId, ct);
                            break;
                        case ActionKind.Start:
                            await _client.StartExamPlanAsync(scope, intent.Plan!, intent.RequestId, ct);
                            break;
                        case ActionKind.CancelPlan:
                            await _client.CancelExamPlanAsync(scope, intent.OperationId!.Value, intent.RequestId, ct);
                            break;
                        default:
                            await _client.CancelExamModeAsync(scope, intent.OperationId!.Value, intent.RequestId, ct);
                            break;
                    }
                    if (Current(scope, ct)) Accepted(scope, intent, recovered: false);
                }
                catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or 429 or >= 500)
                {
                    if (Current(scope, ct)) await ReconcileAsync(scope, intent, ct);
                }
                catch (ConsoleApiException error)
                {
                    if (Current(scope, ct))
                    {
                        PendingWrites.GetOrCreateValue(_client).Remove(scope);
                        Notice(error.Message, true);
                    }
                }
            }
            else await ReconcileAsync(scope, intent, ct);
            if (Current(scope, ct)) await ReadStateAsync(scope, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (ConsoleApiException error)
        {
            if (Current(scope, ct)) _readError = error.Message;
        }
        finally
        {
            if (Current(scope, ct)) { _working = false; Render(); }
        }
    }

    private async Task ReconcileAsync(ExamScope scope, Intent intent, CancellationToken ct)
    {
        bool found;
        switch (intent.Action)
        {
            case ActionKind.Enter: case ActionKind.Return:
                var mode = await _client.GetExamModeReceiptAsync(scope, intent.RequestId, ct);
                found = mode is not null && mode.Target == (intent.Action == ActionKind.Enter ? "EXAM" : "DAILY");
                break;
            case ActionKind.Deliver: case ActionKind.Start:
                found = await _client.GetExamPlanReceiptAsync(scope, intent.RequestId, intent.Plan?.OperationId is { } operation
                    && intent.Action == ActionKind.Start ? operation : null, ct) is not null;
                break;
            case ActionKind.CancelPlan:
                var plan = await _client.GetExamPlanAsync(scope, intent.OperationId!.Value, ct);
                found = plan.IsTerminal || plan.Grant is not null;
                break;
            default:
                var state = await _client.GetClassroomExamAsync(scope, ct);
                found = state.RuntimeHistory.Any(op => op.OperationId == intent.OperationId && (op.ResolvedAt is not null || op.Grant is not null));
                break;
        }
        if (!Current(scope, ct)) return;
        if (found) Accepted(scope, intent, recovered: true);
        else
        {
            _allowRetry = true;
            Notice($"尚未找到原请求的结果。请继续核对，或用同一请求编号重试。编号 {intent.RequestId:D}。", true);
        }
    }

    private void Accepted(ExamScope scope, Intent intent, bool recovered)
    {
        PendingWrites.GetOrCreateValue(_client).Remove(scope);
        if (intent.Action == ActionKind.Deliver) _file = null;
        Notice(recovered ? "已核对原请求。请以当前设备回执和观测为准。" :
            intent.Action is ActionKind.CancelMode or ActionKind.CancelPlan ? "已收到取消办理结果，请核对任务状态。" :
            "服务器已登记本次请求。");
    }

    private async void RefreshClicked(object? sender, RoutedEventArgs args) => await RefreshAsync(checkPending: true);
    private void BackClicked(object? sender, RoutedEventArgs args) => Close();

    private void Notice(string text, bool error = false)
    {
        _notice = text;
        _noticeError = error;
        if (!_closed) Render();
    }

    private static string RuntimeBlocked(ClassroomExamState? state, bool returning)
    {
        if (state?.Runtime.Policy is not { Supported: true, PairedExamControl: true }) return ExamReason.Describe("CLIENT_UNSUPPORTED");
        if (returning && !state.Runtime.Policy.RemoteDailyControl) return "这台设备尚未报告支持远程返回日常。请更新后核对或在教室办理。";
        if (!state.Runtime.Policy.Enabled) return ExamReason.Describe("CONTROL_DISABLED");
        if (!state.RuntimeFresh) return ExamReason.Describe("DEVICE_OFFLINE");
        if (state.Runtime is not { ControlEpoch: not null, Status: { ModeRevision: not null } last }) return "设备环境信息不完整，请刷新。";
        if (state.Runtime.UnresolvedOperationId is not null) return ExamReason.Describe("OPERATION_BUSY");
        if (last.Desktop != "INTERACTIVE") return ExamReason.Describe("DESKTOP_UNAVAILABLE");
        if (last.NoticeOpen) return ExamReason.Describe("NOTICE_OPEN");
        return "";
    }

    private static string PlanBlocked(ClassroomExamState? state)
    {
        if (state?.PlanFresh != true) return ExamReason.Describe("DEVICE_OFFLINE");
        if (state.Plans.Status is not { Enabled: true } plan) return "本机没有开放方案放映，或学校连接已暂停。请在教室核对方案授权。";
        if (plan.Player is { Known: true, Sessions.Length: > 0 }) return ExamReason.Describe("PLAYER_BUSY");
        if (!plan.Available) return ExamReason.Describe(plan.BlockReason ?? "OPERATION_BUSY");
        if (plan.Player is not { Known: true }) return ExamReason.Describe("PLAYER_UNKNOWN");
        return "";
    }

    private static string ReturnBlocked(ClassroomExamState? state)
    {
        var gate = RuntimeBlocked(state, true);
        if (gate.Length > 0 || state is null) return gate;
        if (state.PlanFresh && state.Plans.Status?.Player is { Known: true, Sessions.Length: > 0 })
            return ExamReason.Describe("PLAYER_BUSY");
        var plans = CurrentPlans(state);
        if (plans.Any(item => !item.IsTerminal)) return "请先结束或取消尚未完成的方案。";
        // The caller has successfully read both services. An independent environment-only
        // device can have no plan observation. The Host still enforces its local return checks.
        var presentation = plans.FirstOrDefault(item => item.State == "STARTED" || item.Grant is not null);
        if (presentation is not null && ExamReason.PlayerObservation(state, presentation)?.State is not ("closed" or "failed"))
            return "还未收到本次会话结束的观测。请在教室核对放映状态，必要时从教室端恢复日常。";
        return "";
    }

    private void Render()
    {
        var state = Live;
        var observed = state?.RuntimeFresh == true ? state.Runtime.Status : null;
        var plan = Plan;
        var active = plan is { IsTerminal: false };
        var playback = ExamReason.PlayerObservation(state, plan);
        var envReady = observed is { RuntimeMode: "EXAM", RuntimePhase: "EXAM_READY", RemoteExamPause: true };
        var restored = observed is { RuntimeMode: "DAILY", RemoteExamPause: false };
        var runtimeGate = RuntimeBlocked(state, false);
        var planGate = PlanBlocked(state);
        var returnGate = ReturnBlocked(state);
        var canUse = !_working && !_reading && _scope is not null;
        var pending = Pending;
        var modeOperation = _state?.RuntimeHistory.FirstOrDefault();
        var dailyConfirmed = restored && modeOperation is { Target: "DAILY", State: "SUCCEEDED" };
        var pendingRuntime = state?.RuntimeHistory.FirstOrDefault(item => item.OperationId == state.Runtime.UnresolvedOperationId);
        ExamDevicePicker.IsEnabled = !_working && !_picking && ExamDevicePicker.ItemCount > 0;
        ExamRefresh.IsEnabled = canUse;
        ExamNotice.Text = _readError ?? _notice;
        ExamNotice.IsVisible = !string.IsNullOrEmpty(ExamNotice.Text);
        ExamNotice.Classes.Set("error", _readError is not null || _noticeError);
        ExamPhase.Text = _scope is null ? "这台大屏暂无有效绑定的 NPEduTools 设备" : _state is null
            ? "等待设备状态" : _readError is not null ? "设备状态待核对" : dailyConfirmed ? "当前处于日常模式" : playback is not null
                ? PlaybackName(playback.State) : active ? PlanName(plan!.State) : plan?.State is "STARTED" or "UNKNOWN"
                    ? PlanName(plan.State) : envReady ? "考试环境已就绪" : restored ? "当前处于日常模式" : "考前核对";
        ExamPlayerEvidence.Text = state?.PlanFresh == true
            ? playback is { } exact ? $"本次播放器会话 {exact.Id} · 设备观测 {Time(state.Plans.ReceivedAt)}"
                : state.Plans.Status?.Player is { Known: true } player
                    ? $"{(player.Sessions.Length == 0 ? "播放器当前空闲" : "设备报告已有放映")} · 设备观测 {Time(state.Plans.ReceivedAt)}"
                    : "播放器状态待核对"
            : "暂无近期播放器观测";
        if (plan?.State == "STARTED" && playback is null)
            ExamPlayerEvidence.Text += " · 尚未读到本次放映会话的当前状态";
        ExamOperationResult.Text = modeOperation is null ? "" :
            $"最近切换 · {(modeOperation.Target == "DAILY" ? "返回日常" : "进入考试")} · {ModeName(modeOperation.State)} · {Time(modeOperation.ProgressReceivedAt)}";
        ExamOperationResult.IsVisible = modeOperation is not null;
        ExamRuntimeCheck.Text = $"环境切换授权 · {Authorization(state?.Runtime.Policy is { Supported: true, PairedExamControl: true } ? state.Runtime.Policy.Enabled : null)}" +
            $"　录制 · {RecordingName(observed?.Recording)}　环境观测 · {Time(state?.Runtime.SampleAsOf)}";
        ExamPlanCheck.Text = $"方案放映授权 · {Authorization(state?.PlanFresh == true ? state.Plans.Status?.Enabled : null)}　" +
            $"桥接 · {BridgeName(state)}";
        ExamFileNote.Text = _file is null ? "设备可校验 UTF-8 .ea2 或 .json，最大 24 KiB。" : $"待投递文件 · {_file.Name}";
        var previousPresentationResolved = plan is not { State: "STARTED" or "UNKNOWN" } || playback?.State is "closed" or "failed" || dailyConfirmed;
        ExamChooseFile.IsEnabled = canUse && !_picking && !active && pending is null && planGate.Length == 0 && previousPresentationResolved;
        ExamEnvironmentImpact.Text = envReady
            ? "返回日常会正常退出 ExamAware、启动 ClassIsland，并解除远程录课暂停。若有未保存内容，请在教室先保存并关闭。"
            : "进入考试会先正常保存现有录制，暂停后续自动录课并保留计划，同时联动 ClassIsland 和 ExamAware 的运行及登录自启动。";
        var summary = plan?.Summary;
        ExamSchedulePanel.IsVisible = summary is not null && (active || playback?.State is "preparing" or "opening");
        if (summary?.PreparationId != _visibleSchedule)
        {
            _visibleSchedule = summary?.PreparationId;
            ExamSchedule.SetSchedule(summary?.Exams);
            ExamScheduleTitle.Text = summary is null ? "" : $"设备校验的考程 · {summary.ExamName}";
        }
        ExamDiagnostics.IsVisible = _state is not null;
        ExamDiagnosticText.Text = string.Join("\n", new[] {
            observed?.ReasonCode is { } runtimeReason ? ExamReason.Describe(runtimeReason) : null,
            plan?.ReasonCode is { } planReason ? ExamReason.Describe(planReason) : null,
            modeOperation?.ReasonCode is { } resultReason ? ExamReason.Describe(resultReason) : null,
            modeOperation?.Evidence is { } proof ? $"设备切换回执 · ExamAware {proof.ExamAware} · ClassIsland {proof.ClassIsland} · 录课暂停 {(proof.RemoteExamPause ? "已建立" : "已解除")}" : null,
            plan is null ? null : $"方案任务 {plan.OperationId:D}"
        }.Where(item => !string.IsNullOrEmpty(item)));
        ExamHistoryText.Text = string.Join("\n", (_state?.Plans.Items ?? []).Take(5)
            .Select(item => $"{Time(item.CreatedAt)} · {item.FileName} · {PlanName(item.State)}")
            .Concat((_state?.RuntimeHistory ?? []).Take(5).Select(item =>
                $"{Time(item.ProgressReceivedAt)} · {(item.Target == "EXAM" ? "进入考试" : "返回日常")} · {ModeName(item.State)}")));
        ExamCancel.IsVisible = pending is null && (active && plan!.Grant is null ||
            pendingRuntime is { Grant: null, ResolvedAt: null });
        _cancel = active && plan!.Grant is null ? ActionKind.CancelPlan : ActionKind.CancelMode;
        ExamCancel.Content = _cancel == ActionKind.CancelPlan ? "取消未开播方案" : "取消未开始的切换";
        ExamCancel.IsEnabled = canUse && state is not null;
        ExamRetry.IsVisible = pending is not null && _allowRetry;
        ExamRetry.IsEnabled = canUse;
        ExamPreflight.IsVisible = _scope is not null;
        ExamNextStep.Text = dailyConfirmed ? "已核对本次返回日常及当前运行环境。" :
            pendingRuntime is not null ? ModeName(pendingRuntime.State) + "，请以设备回执为准。" :
            ModeAdvice(modeOperation, playback, plan, envReady, runtimeGate, planGate, returnGate);
        ExamPrimary.IsVisible = true;
        ExamPrimary.IsEnabled = canUse && pending is not null;
        if (pending is not null) { ExamPrimaryLabel.Text = "核对原请求"; return; }
        if (_scope is null || state is null) { ExamPrimary.IsVisible = false; return; }
        if (plan is { State: "PREPARED", Summary: { } details })
        {
            SelectPrimary(ActionKind.Start, $"在{_screen.Name}开始放映", canUse && planGate.Length == 0 && state is not null &&
                state.Plans.Status?.PreparedId == details.PreparationId && state.Plans.Context == plan.Context &&
                state.Plans.Status.Revision == plan.Revision && plan.ExpiresAt > state.PlanNow);
        }
        else if (active || pendingRuntime is not null || playback?.State is "ready" or "opening" or "preparing" or "closing")
            ExamPrimary.IsVisible = false;
        else if (_file is not null)
            SelectPrimary(ActionKind.Deliver, "投递给设备校验", canUse && planGate.Length == 0 && ExamChooseFile.IsEnabled);
        else if (envReady || observed is { RuntimeMode: "EXAM" } || plan is { State: "STARTED" or "UNKNOWN" } && !restored)
            SelectPrimary(ActionKind.Return, "返回日常", canUse && returnGate.Length == 0);
        else
            SelectPrimary(ActionKind.Enter, $"让{_screen.Name}进入考试", canUse && runtimeGate.Length == 0);
    }

    private void SelectPrimary(ActionKind action, string label, bool enabled)
    {
        _primary = action;
        ExamPrimaryLabel.Text = label;
        ExamPrimary.IsEnabled = enabled;
    }

    private string ModeAdvice(ExamRuntimeOperation? mode, ClassroomPlayerSession? playback, ExamPlanOperation? plan, bool exam,
        string runtimeGate, string planGate, string returnGate)
    {
        if (_scope is null) return "请从大屏接入记录完成现场配对，再到这里核对。";
        if (_readError is not null) return "请刷新或回到设备目录核对原生设备。";
        if (_state is null) return "正在读取学校授权、录制状态和播放器观测。";
        if (playback?.State is "ready" or "opening" or "preparing" or "closing")
            return "放映需在教室的 ExamAware 结束。设备回报本次会话结束后，可办理返回日常。";
        if (plan is { State: "PREPARED" })
            return planGate.Length > 0 ? planGate : "请核对下方各场起止和提醒时间，再明确确认开播。";
        if (plan is { State: "QUEUED" }) return "请等待目标设备校验文件。校验通过后可核对考程。";
        if (plan is { State: "START_REQUESTED" or "START_AUTHORIZED" }) return "等待目标设备回报。播放器就绪另以本次会话观测为准。";
        if (plan is { State: "STARTED" or "UNKNOWN" }) return returnGate.Length > 0 ? returnGate : "设备已回报本次放映终态，可办理返回日常。";
        if (mode?.ReasonCode is { } reason && mode.State is "WAITING_LOCAL" or "PARTIAL" or "REJECTED" or "FAILED")
            return ExamReason.Describe(reason);
        if (exam)
        {
            if (planGate.Length == 0) return "可选择一份考试方案交给这台设备校验。";
            return returnGate.Length == 0 ? "当前无可用的放映观测。可申请返回日常，设备仍会核对现场状态。" : planGate;
        }
        return runtimeGate.Length > 0 ? runtimeGate : "核对现场影响后，可进入考试模式。";
    }

    private static string Authorization(bool? enabled) => enabled switch { true => "已取得", false => "当前未开放", _ => "待核对" };
    private static string Time(DateTimeOffset? value) => value?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "暂无";
    private static string RecordingName(string? value) => value switch
    {
        "IDLE" => "空闲", "STARTING" => "正在启动", "RECORDING" => "录制中", "PAUSED" => "已暂停",
        "FINALIZING" => "正在保存", _ => "待核对"
    };
    private static string BridgeName(ClassroomExamState? state) => state?.PlanFresh != true ? "待核对" :
        state.Plans.Status?.BlockReason switch
        {
            "BRIDGE_DISCONNECTED" => "未连接", "BRIDGE_UPGRADE_REQUIRED" or "PLAYER_PERMISSION_DENIED" => "版本或权限待处理",
            _ when state.Plans.Status?.Player is { Known: true } => "已收到播放器观测", _ => "待核对"
        };
    private static string PlaybackName(string? state) => state switch
    {
        "ready" => "本次放映已就绪", "opening" => "本次放映正在打开窗口", "preparing" => "本次放映正在准备",
        "closing" => "本次放映正在关闭", "closed" => "本次放映已结束", "failed" => "设备报告本次放映失败", _ => "播放器状态待核对"
    };
    private static string PlanName(string state) => state switch
    {
        "QUEUED" => "方案已提交，等待设备校验", "PREPARED" => "设备校验通过，待确认放映",
        "START_REQUESTED" => "开播已提交，等待设备接收", "START_AUTHORIZED" => "设备取得开始许可，等待启动回执",
        "STARTED" => "启动已受理", "FAILED" => "方案办理失败", "UNKNOWN" => "方案结果待核对",
        "EXPIRED" => "方案已失效", "CANCELLED" => "方案已取消", _ => "方案状态待核对"
    };
    private static string ModeName(string state) => state switch
    {
        "QUEUED" => "已登记，等待大屏领取", "RECEIVED" or "CHECKING" => "设备正在核对", "START_AUTHORIZED" => "设备已获开始许可",
        "RUNNING" => "设备正在切换", "WAITING_LOCAL" => "等待现场处理", "SUCCEEDED" => "设备已回报切换完成",
        "REJECTED" => "设备未执行", "FAILED" => "设备报告切换失败", "PARTIAL" => "切换部分完成",
        "UNKNOWN" => "结果待核对", "CANCELLED" => "已取消", "EXPIRED" => "已过开始期限", _ => "状态待核对"
    };
}
