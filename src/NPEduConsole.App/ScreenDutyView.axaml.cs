using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class ScreenDutyView : UserControl
{
    private const string UnchangedLogin = "短账号未变，无需保存。";
    private sealed record PendingOperation(ScreenDutyPlan Plan, ScreenDutyPreview Preview, Guid Id);
    private sealed record Choice(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    private static readonly ConditionalWeakTable<ConsoleClient, Dictionary<string, PendingOperation>> Requests = new();
    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _poll = new() {Interval = TimeSpan.FromSeconds(12)};
    private readonly bool _fromDirectory;
    private readonly string? _inactiveTermId;
    private readonly string? _directoryTerm;
    private IReadOnlyList<InactiveClassScreen>? _legacyScreens;
    internal Func<string, bool>? OpenLegacyBinding { get; init; }
    internal Func<ScreenDutyDetail, Task<bool>>? OpenPlacement { get; init; }
    private ScreenDutyCatalog? _catalog;
    private ScreenDutyDetail? _detail;
    private ScreenDutyPreview? _preview;
    private ScreenDutyPlan? _plan;
    private string? _bindingId, _action, _commandType, _commandId, _defaultClass;
    private string? _expectedTerm, _expectedClass;
    private Guid? _submittedHere;
    private InitialPinDelivery<int>? _delivery;
    private Control? _anchor;
    private bool _closed, _busy, _writing, _unavailable, _commandsExpanded, _directoryView, _applyingScope, _directoryInitialized, _canRetry;
    public bool Changed { get; private set; }
    public ScreenDutyDetail? Detail => _detail;
    public ScreenDutyDetail? NativeTarget { get; private set; }
    internal Action<ConsoleApiException>? SessionExpired { get; init; }
    internal Action? CloseRequested { get; set; }
    private PendingOperation? Pending => Requests.GetOrCreateValue(_client).GetValueOrDefault(_school.Id);
    private bool InScope(ScreenDutyRoom room) => room.Id == _defaultClass && room.TermId == _inactiveTermId && room.Active;
    private bool OtherPending
    {
        get
        {
            if (Pending is not { } pending) return false;
            if (pending.Id == _submittedHere) return false;
            var origin = pending.Preview.Screen?.Room ?? pending.Preview.Target;
            if (_inactiveTermId is not null) return pending.Plan.Action != "ENABLE" || origin is null || !InScope(origin);
            if (!_directoryView && pending.Plan.BindingId != _bindingId) return true;
            var termId = _directoryView ? _directoryTerm : _expectedTerm;
            var classId = _directoryView ? _defaultClass : _expectedClass;
            return termId is not null && origin?.TermId != termId || classId is not null && origin?.Id != classId;
        }
    }
    private bool LoginUnchanged => _action == "CHANGE_LOGIN" &&
        string.Equals(DutyCode.Text?.Trim(), _detail?.LoginCode?.Trim(), StringComparison.OrdinalIgnoreCase);

    public ScreenDutyView() => InitializeComponent();

    internal ScreenDutyView(ConsoleClient client, School school, string? bindingId, string? classId, string? inactiveTermId = null,
        string? termId = null) : this()
    {
        _client = client;
        _school = school;
        _bindingId = bindingId;
        _fromDirectory = bindingId is null;
        _directoryView = bindingId is null;
        _defaultClass = classId;
        _inactiveTermId = inactiveTermId;
        _directoryTerm = termId;
        if (bindingId is not null) { _expectedTerm = termId; _expectedClass = classId; }
        if (inactiveTermId is not null)
        {
            DutyHeading.Text = "本班停用大屏";
            DutySearch.IsVisible = DutyDisabledOnly.IsVisible = DutyNew.IsVisible = false;
        }
        DutyIdentity.Text = school.Name;
        SizeChanged += (_, _) => ArrangeDutyLayout();
        _poll.Tick += async (_, _) =>
        {
            if (_closed || _busy || _action is not null || Pending is not null || _detail is null) return;
            await RunAsync(ReadAsync, quiet: true);
        };
        Render();
    }

    internal async Task StartAsync()
    {
        ArrangeDutyLayout();
        await RunAsync(async () =>
        {
            if (Pending is { } unresolved)
            {
                if (OtherPending)
                {
                    await ReadAsync();
                    if (!_closed) Notice("本校还有其他大屏操作待核对。请返回「教室设备 → 大屏账号与值守」查原回执。");
                    return;
                }
                await VerifyAsync(unresolved);
                if (_closed || Pending is not null) return;
            }
            await ReadAsync();
        });
        if (!_closed) _poll.Start();
    }

    internal bool CanClose()
    {
        if (_closed) return true;
        if (!_writing && (_action is null || Pending is not null)) return true;
        Notice(_writing ? "原申请正在提交，请等结果或进入待核对。" : "请先完成或取消当前操作。");
        return false;
    }

    internal void End()
    {
        if (_closed) return;
        _closed = true;
        _delivery = null;
        _poll.Stop();
        DutyPin.Text = "";
        DutyRetryPin.Text = "";
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async Task ReadAsync()
    {
        ScreenDutyCatalog data;
        try { data = await _client.GetScreenDutyAsync(_school.Id, _directoryView ? null : _bindingId, _lifetime.Token, includeHistorical: true); }
        catch (ConsoleApiException error) when (_inactiveTermId is not null && _directoryView &&
            error is {StatusCode: 404, Code: null or "NOT_FOUND"})
        {
            var screens = await _client.GetLegacyInactiveClassScreensAsync(_school.Id, _inactiveTermId, _defaultClass!, _lifetime.Token);
            if (_closed) return;
            _legacyScreens = screens;
            _catalog = null;
            _detail = null;
            _unavailable = false;
            DutyHeading.Text = "本班停用大屏";
            DutyIdentity.Text = screens.FirstOrDefault() is { } first ? $"{_school.Name} · {first.TermName} · {first.ClassName}" : _school.Name;
            DutyOpen.Content = "在网页版核对所选大屏";
            Notice(screens.Count == 0 ? "原班暂无可核对的停用大屏，请返回刷新待办。" : "此 KV 使用网页版办理，请先选中本班的一台大屏。");
            BindDirectory();
            return;
        }
        if (_closed) return;
        _legacyScreens = null;
        DutyOpen.Content = "打开所选大屏";
        _catalog = data;
        _detail = _directoryView ? null : data.Screens.SingleOrDefault(item => item.Id == _bindingId);
        if (_detail is { } exact && (_expectedClass is not null && exact.Room.Id != _expectedClass ||
            _expectedTerm is not null && exact.Room.TermId != _expectedTerm))
        {
            _detail = null;
            _delivery = null;
            ClearEditor();
            throw new ConsoleApiException(409, "大屏已离开原学期或原班，请返回目录核对原绑定与现归属。");
        }
        if (_inactiveTermId is not null && (data.TermId != _inactiveTermId ||
            !data.Rooms.Any(InScope) || !_directoryView && (_detail is not {Active: false} selected || !InScope(selected.Room))))
        {
            _catalog = null;
            _detail = null;
            _directoryView = true;
            ClearEditor();
            BindDirectory();
            RenderEvidence();
            throw new ConsoleApiException(409, "原学期、班级或大屏状态已变化，请返回刷新待办。");
        }
        if (!CurrentPinMatches(_detail, _delivery)) _delivery = null;
        if (_unavailable) Notice("已重新核对服务器状态。");
        _unavailable = false;
        if (_directoryView && !_directoryInitialized)
        {
            _directoryInitialized = true;
            DutyHistory.IsChecked = data.SupportsHistorical && data.TermId is null;
        }
        BindDirectory();
        RenderEvidence();
    }

    private void BindDirectory()
    {
        if (!_directoryView) return;
        var selected = (DutyRows.SelectedItem as Choice)?.Id ?? _bindingId;
        var query = DutySearch.Text?.Trim() ?? "";
        var rows = _legacyScreens?.Select(row => new Choice(row.Id, $"{row.Name} · {row.Id} · 已停用")).ToArray() ??
            (_catalog?.Screens ?? []).Where(row => (_defaultClass is null || row.Room.Id == _defaultClass) &&
                (_directoryTerm is null || row.Room.TermId == _directoryTerm) &&
                (_inactiveTermId is null ? row.Room.Active || DutyHistory.IsChecked == true : !row.Active && InScope(row.Room)) &&
                (DutyDisabledOnly.IsChecked != true || !row.Active) &&
                $"{row.Display} {row.Room.Code} {row.LoginCode}".Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(row => new Choice(row.Id, row.Display)).ToArray();
        DutyRows.ItemsSource = rows;
        DutyRows.SelectedItem = rows.FirstOrDefault(x => x.Id == selected) ?? (_defaultClass is null ? rows.FirstOrDefault() : null);
        DutyOpen.IsEnabled = !_busy && DutyRows.SelectedItem is Choice;
    }

    private void SearchChanged(object? sender, RoutedEventArgs args)
    {
        if (_client is not null) BindDirectory();
    }
    private void DirectorySelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_client is not null) DutyOpen.IsEnabled = !_busy && DutyRows.SelectedItem is Choice;
    }

    private async void OpenClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy || _unavailable || Pending is not null && !OtherPending || DutyRows.SelectedItem is not Choice item) return;
        if (_legacyScreens is not null)
        {
            await RunAsync(async () =>
            {
                await ReadAsync();
                if (_closed) return;
                if (_legacyScreens?.Any(row => row.Id == item.Id) != true) Notice("原大屏已变化，请重新核对列表。", true);
                else if (OpenLegacyBinding?.Invoke(item.Id) != true) Notice("无法打开原网页，请核对学校网页地址。", true);
            });
            return;
        }
        var room = _catalog?.Screens.FirstOrDefault(row => row.Id == item.Id)?.Room;
        _expectedTerm = room?.TermId;
        _expectedClass = room?.Id;
        _bindingId = item.Id;
        _directoryView = false;
        _unavailable = false;
        SetDutyStep(manage: false);
        Notice("");
        await RunAsync(ReadAsync);
        if (!_closed) RestoreDutyFocus(DutyPrimary);
    }

    private void RenderEvidence()
    {
        if (_detail is not { } row)
        {
            DutyHeading.Text = _inactiveTermId is null ? "大屏账号与值守" : "本班停用大屏";
            var room = _inactiveTermId is not null ? _catalog?.Rooms.FirstOrDefault(InScope) : null;
            DutyIdentity.Text = room is null ? _school.Name : $"{_school.Name} · {room.TermName} · {room} · 停用 {DutyRows.ItemCount} 台";
            return;
        }
        DutyHeading.Text = row.Name;
        if (DutyBindingIdentity.Text != row.Id)
        {
            DutyBindingDetails.IsExpanded = false;
            DutyBrowserHistory.IsExpanded = false;
        }
        DutyBindingIdentity.Text = row.Id;
        DutyIdentity.Text = $"{_school.Name} · {row.Room.TermLabel} · {row.Room}" +
            (row.Active ? "" : " · 绑定停用");
        BrowserDutyState.Text = row.BrowserState switch
        {
            "NEEDS_ACCOUNT" => "网页账号待配置",
            "AWAITING_LOGIN" => "等待使用网页账号登录",
            "AWAITING_HEARTBEAT" => "等待当前网页值守心跳",
            "ONLINE" => "近期收到网页值守心跳",
            "DEGRADED" => "网页值守需关注",
            "OFFLINE" => "近期未收到网页值守心跳",
            "DISABLED" when !row.Room.Active => "原学期或班级未启用，无法网页登录",
            "DISABLED" => "网页值守已停用",
            _ => "网页值守待核对"
        };
        DutyBrowserHistoryText.Text = string.Join("\n", new[]
        {
            row.FirstLoginAt is { } first ? $"本屏历史首次登录 {Time(first)}" : null,
            row.PreviousHeartbeatAt is { } before ? $"变更前最后心跳 {Time(before)}" : null
        }.OfType<string>());
        DutyBrowserHistory.IsVisible = row.FirstLoginAt is not null || row.PreviousHeartbeatAt is not null;
        BrowserDutyEvidence.Text = $"账号 {row.LoginCode ?? "未配置"} · {(row.BrowserBound ? "已绑定浏览器" : "暂无浏览器绑定")}\n" +
            $"当前凭据心跳 {Time(row.HeartbeatAt)}" +
            $"\n待上传 {(row.PendingUploads?.ToString() ?? "待核对")} · " +
            (row.SyncProblem ? "心跳报告同步异常" : row.HeartbeatAt is null || row.BrowserState == "UNKNOWN" ?
                "同步待核对" : "心跳未报告同步异常");
        NativeDutyState.Text = !row.Room.Active ? "原班当前不能接入，请核对学期或安排新班" :
            !row.Active ? "整台大屏已停用" : row.NativeState switch
        {
            "REPORTED" => "已配对，已有有效上报",
            "AWAITING_PAIRING" => "等待现场配对",
            "AWAITING_REPORT" => "已配对，等待首次有效上报",
            "REPAIR_REQUIRED" => "原配对已失效，请现场重新配对",
            "NOT_APPLICABLE" => "暂无原生接入证据",
            _ => "原生配对或上报待核对"
        };
        string reportLabel = row.NativeState switch
        {
            _ when !row.Room.Active || !row.Active => "历史上报",
            "REPAIR_REQUIRED" => "原配对最后上报",
            "UNKNOWN" => "上报记录（当前授权待核）",
            _ => "当前配对有效上报"
        };
        NativeDutyEvidence.Text = $"当前配对记录 {Time(row.NativePairedAt)}\n{reportLabel} {Time(row.NativeReportedAt)}";
        var chosen = row.Commands.FirstOrDefault(item => item.Id == _commandId);
        DutyCommandPicked.IsVisible = _commandId is not null;
        DutyCommandPicked.Text = chosen is null ? "本次指令尚未读到状态，请继续核对。" : $"本次指令 · {CommandLabel(chosen)}";
        DutyCommandHistory.Children.Clear();
        foreach (var command in row.Commands)
            DutyCommandHistory.Children.Add(new TextBlock
            {
                Text = $"{CommandLabel(command)} · 下发 {Time(command.CreatedAt)} · 到期 {Time(command.ExpiresAt)}" +
                    (command.AcknowledgedAt is not null ? $" · 回执 {Time(command.AcknowledgedAt)}" :
                        command.DeliveredAt is not null ? $" · 送达 {Time(command.DeliveredAt)}" : ""),
                Classes = {"muted"}, TextWrapping = TextWrapping.Wrap
            });
    }

    private static string Time(DateTimeOffset? value) => value is { } at ? at.ToLocalTime().ToString("MM-dd HH:mm:ss") : "暂无";
    private static string CommandLabel(ScreenDutyCommand command) =>
        (command.Type == "RELOAD_APP" ? "重载网页" : "刷新网页数据") + " · " + (command.State switch
        {
            "PENDING" => "等待网页接收",
            "DELIVERED" => "已送达，等待回执",
            "REFRESHED" => "已确认刷新数据",
            "REFRESH_UNVERIFIED" => "已收到网页回执，数据刷新待核对",
            "RELOAD_ACCEPTED" => "已接收，等待空闲重载",
            "FAILED" => "网页报告执行失败",
            "EXPIRED" => "已过期",
            "SOURCE_CHANGED" => "原绑定范围已变化",
            _ => "待核对"
        });

    private void PrimaryClicked(object? sender, RoutedEventArgs args)
    {
        if (_detail is not { } row || _busy) return;
        if (!row.Active) { Begin("ENABLE", DutyPrimary); return; }
        if (!row.HasPin || row.LoginCode is null) { Begin("CONFIGURE", DutyPrimary); return; }
        if (row.Room.Active) ToggleCommandsClicked(sender, args);
    }

    private void ToggleCommandsClicked(object? sender, RoutedEventArgs args)
    {
        _commandsExpanded = !_commandsExpanded;
        Render();
        if (_commandsExpanded) DutyCommandsPanel.BringIntoView();
    }

    private void MaintenanceClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is Button {CommandParameter: string action} button) Begin(action, button);
    }
    private void CommandClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is Button {CommandParameter: string command} button) Begin("COMMAND", button, command: command);
    }
    private void ToggleClicked(object? sender, RoutedEventArgs args) => Begin(_detail?.Active == true ? "DISABLE" : "ENABLE", DutyToggle);
    private void NewClicked(object? sender, RoutedEventArgs args) =>
        Begin("CREATE", sender as Control, _detail?.Room.Id ?? _defaultClass);

    private async void PlacementClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy || _detail is not {PlacementTermId: not null, ActivationRequestId: not null} row || OpenPlacement is null) return;
        var arrange = OpenPlacement;
        await RunAsync(async () =>
        {
            if (await arrange(row) && !_closed)
            {
                Changed = true;
                CloseRequested?.Invoke();
            }
            else if (!_closed) await ReadAsync();
        });
    }

    private void Begin(string action, Control? anchor, string? target = null, string? command = null)
    {
        if (_closed || _unavailable || Pending is not null || _inactiveTermId is not null && action != "ENABLE") return;
        _delivery = null;
        _action = action;
        _anchor = anchor;
        _commandType = command;
        _preview = null;
        _plan = null;
        _applyingScope = true;
        DutyTargetSearch.Text = "";
        SetTargetChoices(target);
        _applyingScope = false;
        DutyName.Text = action == "RENAME" ? _detail?.Name : "";
        DutyCode.Text = action == "CHANGE_LOGIN" ? _detail?.LoginCode : "";
        DutyPin.Text = "";
        DutyEditTitle.Text = ActionLabel(action, command);
        Notice(LoginUnchanged ? UnchangedLogin : "");
        Render();
        var focus = DutyRoomFields.IsVisible ? DutyTargetSearch : DutyName.IsVisible ? DutyName :
            DutyCode.IsVisible ? DutyCode : DutyPin.IsVisible ? DutyPin : (Control)DutyConfirm;
        RestoreDutyFocus(focus);
    }

    private void SetTargetChoices(string? id)
    {
        var query = DutyTargetSearch.Text?.Trim() ?? "";
        var choices = (_catalog?.Rooms ?? []).Where(room => (_action != "MOVE" || room.Id != _detail?.Room.Id) &&
            room.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        DutyTargetClass.ItemsSource = choices;
        DutyTargetClass.SelectedItem = choices.FirstOrDefault(room => room.Id == id);
    }

    private void TargetSearchChanged(object? sender, TextChangedEventArgs args)
    {
        if (_client is not null && !_applyingScope) SetTargetChoices((DutyTargetClass.SelectedItem as ScreenDutyRoom)?.Id);
    }

    private void LoginCodeChanged(object? sender, TextChangedEventArgs args)
    {
        if (_client is null || _action != "CHANGE_LOGIN" || _preview is not null) return;
        if (LoginUnchanged) Notice(UnchangedLogin);
        else if (DutyNotice.Text == UnchangedLogin) Notice("");
        Render();
    }

    private static bool NeedsPin(string? action) => action is "CREATE" or "CONFIGURE" or "RESET_PIN";
    private static string ActionLabel(string action, string? command) => action switch
    {
        "CREATE" => "登记另一台大屏",
        "CONFIGURE" => "配置网页账号",
        "RENAME" => "修改大屏名称",
        "CHANGE_LOGIN" => "修改网页短账号",
        "RESET_PIN" => "重置网页 PIN",
        "UNBIND_BROWSER" => "解除网页登录的浏览器绑定",
        "MOVE" => "调整大屏所属班级",
        "DISABLE" => "停用整台大屏",
        "ENABLE" => "恢复大屏",
        _ => command == "RELOAD_APP" ? "重载网页" : "刷新网页数据"
    };

    private ScreenDutyPlan ReadPlan()
    {
        var action = _action ?? throw new InvalidOperationException();
        var source = action == "CREATE" || _catalog?.SupportsHistorical != true ? null : _detail?.Room;
        return new(action, action == "CREATE" ? null : _bindingId,
            action is "CREATE" or "MOVE" ? (DutyTargetClass.SelectedItem as ScreenDutyRoom)?.Id : null,
            action is "CREATE" or "RENAME" ? DutyName.Text?.Trim() : null,
            action is "CREATE" or "CONFIGURE" or "CHANGE_LOGIN" ? DutyCode.Text?.Trim().ToUpperInvariant() : null, _commandType,
            source?.TermId, source?.Id);
    }

    private async void ConfirmClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy || OtherPending) return;
        if (Pending is { } pending) { await RunAsync(() => VerifyAsync(pending)); return; }
        if (_action is null) return;
        if (_preview is null)
        {
            if (LoginUnchanged) { Notice(UnchangedLogin); return; }
            var plan = ReadPlan();
            if (NeedsPin(plan.Action) && (DutyPin.Text is not {Length: >= 4 and <= 8} pin || pin.Any(ch => ch is < '0' or > '9')))
            { Notice("请输入 4 至 8 位数字网页 PIN。", true); DutyPin.Focus(); return; }
            await RunAsync(async () =>
            {
                var preview = await _client.PreviewScreenDutyAsync(_school.Id, plan, _lifetime.Token);
                if (_closed) return;
                if (_inactiveTermId is not null && (preview.Screen is not {Active: false} source || source.Id != _bindingId || !InScope(source.Room)))
                {
                    ClearEditor();
                    await ReadAsync();
                    throw new ConsoleApiException(409, "大屏已变化，请从原班重新选择。");
                }
                _plan = plan;
                _preview = preview;
                ShowReview(preview, plan);
                Notice("请核对大屏、班级和网页登录影响，再确认办理。");
            });
            if (!_closed && _preview is not null) FocusDutyReview();
            return;
        }

        var reviewed = _preview;
        var confirmed = _plan!;
        var intent = new PendingOperation(confirmed, reviewed, Guid.NewGuid());
        _submittedHere = intent.Id;
        Requests.GetOrCreateValue(_client)[_school.Id] = intent;
        var pinToSubmit = NeedsPin(confirmed.Action) ? DutyPin.Text : null;
        DutyPin.Text = "";
        await SubmitAsync(intent, pinToSubmit);
    }

    private async void RetryClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy || OtherPending || !_canRetry || Pending is not { } intent) return;
        var pin = NeedsPin(intent.Plan.Action) ? DutyRetryPin.Text : null;
        if (NeedsPin(intent.Plan.Action) && (pin is not {Length: >= 4 and <= 8} || pin.Any(ch => ch is < '0' or > '9')))
        { Notice("请重新输入原先确认的 4 至 8 位网页 PIN。", true); DutyRetryPin.Focus(); return; }
        DutyRetryPin.Text = "";
        await SubmitAsync(intent, pin);
    }

    private async Task SubmitAsync(PendingOperation intent, string? pin)
    {
        _canRetry = false;
        await RunAsync(async () =>
        {
            ScreenDutyReceipt receipt;
            try
            {
                receipt = await _client.CommitScreenDutyAsync(_school.Id, intent.Plan, intent.Preview,
                    pin, intent.Id, _lifetime.Token);
            }
            catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or >= 500)
            {
                _writing = false;
                if (!_closed) Render();
                if (!_closed) await VerifyAsync(intent);
                return;
            }
            catch (ConsoleApiException error)
            {
                Requests.GetOrCreateValue(_client).Remove(_school.Id);
                _preview = null;
                _plan = null;
                if (error.Code == "SCREEN_DUTY_STALE" && !_closed)
                {
                    await ReadAsync();
                    if (_closed) return;
                    if (_action is "CREATE" or "MOVE")
                        SetTargetChoices((DutyTargetClass.SelectedItem as ScreenDutyRoom)?.Id);
                }
                throw;
            }
            if (_closed) return;
            _writing = false;
            Render();
            var username = intent.Plan.LoginCode ?? intent.Preview.Screen?.LoginCode;
            _delivery = pin is not null && username is not null && receipt.AppliedPinVersion is >= 0 and { } version
                ? new(receipt.BindingId, username, pin, version) : null;
            await AcceptReceiptAsync(receipt, false);
        }, writing: true);
    }

    private void ShowReview(ScreenDutyPreview preview, ScreenDutyPlan plan)
    {
        DutyReviewTitle.Text = ActionLabel(plan.Action, plan.CommandType);
        DutyReviewObject.Text = preview.Screen is { } source ? $"{source.Room.TermLabel} · {source.Room} · {source.Name} · 编号 {source.Id}" : $"新屏 · {plan.Name}";
        if (plan.Name is not null && preview.Screen?.Name != plan.Name && plan.Action == "RENAME") DutyReviewObject.Text += $"\n新名称 {plan.Name}";
        if (plan.LoginCode is not null) DutyReviewObject.Text += $"\n网页短账号 {plan.LoginCode}";
        if (preview.Target is { } target) DutyReviewObject.Text += $"\n目标班 {target.TermLabel} · {target}";
        DutyReviewPeers.IsVisible = preview.Target is not null;
        DutyReviewPeers.Text = preview.TargetScreens.Length == 0 ? "目标班暂无其他大屏记录。" :
            "目标班已有大屏\n" + string.Join("\n", preview.TargetScreens.Select(row => $"{row.Name} · 编号 {row.Id}{(row.Active ? "" : " · 已停用")}"));
        DutyReviewWeb.Text = $"网页登录 · {preview.Impact.Browser}";
        DutyReviewNative.Text = $"NPEduTools · {preview.Impact.Native}";
    }

    private async Task VerifyAsync(PendingOperation intent)
    {
        _canRetry = false;
        Notice("正在按原请求只读核对。");
        var result = await _client.GetScreenDutyReceiptAsync(_school.Id, intent.Plan, intent.Id, _lifetime.Token);
        if (_closed) return;
        if (result is null)
        {
            _canRetry = true;
            Notice($"暂未找到本次回执。可继续核对或用原编号重试 {intent.Id:D}。", true);
            return;
        }
        await AcceptReceiptAsync(result, true);
    }

    private async Task AcceptReceiptAsync(ScreenDutyReceipt receipt, bool recovered)
    {
        var original = Pending?.Preview;
        Requests.GetOrCreateValue(_client).Remove(_school.Id);
        Changed = true;
        ClearEditor();
        _bindingId = receipt.BindingId;
        SetDutyStep(manage: false);
        _directoryView = _inactiveTermId is not null;
        if (original is { } proof)
        {
            var destination = proof.Target ?? proof.Screen?.Room;
            _expectedClass = destination?.Id;
            _expectedTerm = destination?.TermId;
        }
        if (_detail?.Id != receipt.BindingId)
        {
            _detail = null;
            DutyHeading.Text = receipt.Action == "CREATE" ? "已登记大屏" : "大屏账号与值守";
            DutyIdentity.Text = _school.Name;
            DutyBindingIdentity.Text = receipt.BindingId;
        }
        _commandId = receipt.CommandId;
        if (receipt.Action is "CREATE" or "MOVE" or "DISABLE" or "ENABLE") _commandsExpanded = false;
        _commandsExpanded |= _commandId is not null;
        try { await ReadAsync(); }
        catch (ConsoleApiException error)
        {
            _unavailable = true;
            _delivery = null;
            SessionExpired?.Invoke(error);
            if (!_closed) Notice("服务器已确认保存。最新状态暂不可读，请点核对最新状态。", true);
            return;
        }
        if (!_closed && NeedsPin(receipt.Action) && _delivery is null)
        {
            Notice((recovered ? "已找回本人原回执。" : "服务器已确认办理。") + "本页没有可交付的 PIN。如需新的网页 PIN，可为本屏重置。");
            return;
        }
        if (!_closed) Notice(recovered ? "已找回服务器确认的原回执。" :
            receipt.CommandId is null ? "服务器已确认本次办理。" : "服务器已记录指令。以网页回执核对实际执行。");
    }

    private void ClearEditor()
    {
        _preview = null;
        _plan = null;
        _action = null;
        _commandType = null;
        DutyPin.Text = "";
    }

    private async void BackClicked(object? sender, RoutedEventArgs args)
    {
        if (_writing) return;
        if (Pending is null && _preview is not null)
        {
            _preview = null;
            _plan = null;
            Notice("");
            Render();
            RestoreDutyFocus(DutyConfirm);
        }
        else if (Pending is null && _action is not null)
        {
            ClearEditor();
            Notice("");
            Render();
            RestoreDutyFocus(_anchor);
        }
        else if ((Pending is null || OtherPending) && !_directoryView && _fromDirectory)
        {
            _delivery = null;
            _directoryView = true;
            _commandsExpanded = false;
            _commandId = null;
            Notice("");
            await RunAsync(ReadAsync);
            if (!_closed) RestoreDutyFocus(DutyRows);
        }
        else CloseRequested?.Invoke();
    }

    private async void RefreshClicked(object? sender, RoutedEventArgs args)
    {
        await RunAsync(async () =>
        {
            await ReadAsync();
            if (!_closed && _legacyScreens is null) Notice("已读取服务器最新状态。");
        });
    }

    private void NativeClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy || _action is not null || Pending is not null) return;
        NativeTarget = _detail;
        CloseRequested?.Invoke();
    }

    private async Task RunAsync(Func<Task> action, bool writing = false, bool quiet = false)
    {
        if (_busy || _closed) return;
        _busy = true;
        _writing = writing;
        if (!quiet) Render();
        try { await action(); }
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error)
        {
            if (_closed) return;
            _delivery = null;
            if (error.StatusCode is 401 or 403)
            {
                Requests.GetOrCreateValue(_client).Remove(_school.Id);
                _catalog = null;
                _detail = null;
                _legacyScreens = null;
                ClearEditor();
                BindDirectory();
                RenderEvidence();
            }
            SessionExpired?.Invoke(error);
            if (_closed) return;
            if (_action is null || quiet) _unavailable = true;
            Notice(error is {StatusCode: 404, Code: null or "NOT_FOUND"} ? "当前 KV 不支持大屏账号与值守，请更新服务或使用原网页入口。" :
                quiet || _unavailable ? $"读取失败，状态待核对。{error.Message}" : error.Message, true);
        }
        catch (ArgumentException error) when (!_closed) { Notice(error.Message, true); }
        finally
        {
            _busy = _writing = false;
            if (!_closed) Render();
        }
    }

    private void Render()
    {
        var canDeliver = CurrentPinMatches(_detail, _delivery) && !_directoryView && !_unavailable;
        DutyHandoffActions.IsVisible = DutyCopyPin.IsVisible = DutySavePin.IsVisible = canDeliver;
        DutyCopyPin.IsEnabled = DutySavePin.IsEnabled = !_busy;
        var pending = Pending is not null && !OtherPending;
        var editing = _action is not null && _preview is null && !pending;
        var canAct = !_busy && !_unavailable && Pending is null && _detail is { } && (_detail.Room.Active || _catalog?.SupportsHistorical == true);
        var reviewing = _preview is not null && !pending;
        DutyDirectoryPanel.IsVisible = _directoryView && _action is null && !pending;
        DutyDetailPanel.IsVisible = !_directoryView && _detail is not null && _action is null && !pending;
        DutyBindingDetails.IsVisible = !_directoryView && _detail is not null;
        DutyNew.IsEnabled = !_busy && !_unavailable && Pending is null && _catalog?.Rooms.Length > 0;
        DutyHistory.IsVisible = _inactiveTermId is null && _defaultClass is null && _catalog?.SupportsHistorical == true;
        DutyOpen.IsEnabled = !_busy && !_unavailable && DutyRows.SelectedItem is Choice;
        DutyPrimary.IsVisible = _detail is { } primary && (primary.Room.Active || !primary.Active);
        DutyPrimary.IsEnabled = canAct;
        DutyPrimary.Content = _detail is { } row ? !row.Active ? "恢复整台大屏" : row.LoginCode is null || !row.HasPin ?
            "配置网页账号" : _commandsExpanded ? "收起同步情况" : "查看同步情况" : "";
        DutyMaintenance.IsVisible = _inactiveTermId is null;
        DutyMaintenance.IsEnabled = canAct;
        var roomActive = _detail?.Room.Active == true;
        DutyRename.IsVisible = DutyCodeChange.IsVisible = DutyResetPin.IsVisible = DutyAnother.IsVisible = roomActive;
        DutyRename.IsEnabled = canAct && roomActive;
        var active = canAct && _detail?.Active == true;
        var live = active && roomActive;
        var configured = canAct && (active || _catalog?.SupportsHistorical == true) && _detail?.HasPin == true && _detail.LoginCode is not null;
        var legacyBrowser = live && !configured && _detail?.BrowserBound == true;
        DutyLegacySync.IsVisible = legacyBrowser;
        DutyLegacySync.Content = _commandsExpanded ? "收起同步情况" : "查看同步情况";
        DutyCodeChange.IsEnabled = DutyResetPin.IsEnabled = configured && roomActive;
        DutyUnbindBrowser.IsEnabled = (configured || canAct && _catalog?.SupportsHistorical == true) && _detail?.BrowserBound == true;
        DutyMove.IsEnabled = active && _catalog?.Rooms.Any(room => room.Id != _detail!.Room.Id) == true && _detail!.PlacementTermId is null;
        DutyMove.IsVisible = roomActive || _catalog?.Rooms.Length > 0 && _detail?.PlacementTermId is null;
        DutyAnother.IsEnabled = live;
        DutyPlacement.IsVisible = _detail?.PlacementTermId is not null && OpenPlacement is not null;
        DutyPlacement.IsEnabled = canAct;
        DutyToggle.Content = _detail?.Active == true ? "停用整台大屏" : "恢复大屏";
        DutyNative.IsEnabled = live;
        DutyCommandsPanel.IsVisible = _commandsExpanded && _detail?.Room.Active == true;
        DutyRefreshData.IsEnabled = DutyReloadWeb.IsEnabled = live && configured || legacyBrowser;
        DutyEditPanel.IsVisible = editing;
        DutyRoomFields.IsVisible = _action is "CREATE" or "MOVE";
        DutyName.IsVisible = _action is "CREATE" or "RENAME";
        DutyCode.IsVisible = _action is "CREATE" or "CONFIGURE" or "CHANGE_LOGIN";
        DutyPin.IsVisible = NeedsPin(_action);
        DutyReviewPanel.IsVisible = reviewing;
        DutyRetryPanel.IsVisible = pending && _canRetry && !OtherPending;
        DutyRetryButton.IsEnabled = !_busy;
        DutyRetryPin.IsVisible = NeedsPin(Pending?.Plan.Action);
        DutyBack.IsEnabled = !_writing;
        DutyBack.Content = reviewing ? "返回修改" : editing ? "取消操作" : "返回";
        DutyRefresh.IsVisible = !editing && !reviewing && !pending;
        DutyRefresh.IsEnabled = !_busy;
        DutyConfirm.IsVisible = (editing || reviewing || pending) && !OtherPending;
        DutyConfirm.IsEnabled = !_busy && (!editing || !LoginUnchanged);
        DutyConfirm.Content = pending ? "重试核对原回执" : reviewing ? (_action == "MOVE" && _preview?.Target is { } target ?
            $"确认调到{target.Label}" : $"确认{ActionLabel(_action!, _commandType)}") : "核对影响";
        RenderDutySteps();
    }

    private void Notice(string value, bool error = false)
    {
        DutyNotice.Text = value;
        DutyNotice.Classes.Set("error", error);
        DutyNotice.IsVisible = value.Length > 0;
    }
}
