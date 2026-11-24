using Avalonia.Controls;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class ClassroomActionPolicyWindow : Window
{
    internal sealed class Pending
    {
        public PendingWrite? Write { get; set; }
    }

    internal sealed record PendingWrite(Guid RequestId, ClassroomActionChange Change, string Version);
    private sealed record Choice(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record HistoricalKeyRow(ClassroomHistoricalUsbKey Key)
    {
        public override string ToString() => $"{Key.Label}\n{HistoricalTermText(Key.Term)} · {ScopeText(Key.ScopeKind, Key.ScopeName, Key.ScopeId, Key.ScopeState)}\n钥匙编号 {Key.KeyId}";
    }

    private static readonly Choice[] Modes = [new("ALLOW", "允许现场操作"), new("VERIFY", "验证后允许"), new("DENY", "禁止现场操作")];
    private static readonly Choice[] Factors = [new("CLASS_PASSWORD", "本班口令"), new("USB", "登记 U 盘"), new("ANY", "口令或 U 盘（任一）"), new("ALL", "口令与 U 盘（全部）")];
    private static readonly ClassroomActionRules InitialRules = new(new("VERIFY", "CLASS_PASSWORD"), new("VERIFY", "CLASS_PASSWORD"));
    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly Pending _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private ClassroomActionCatalog? _catalog;
    private ClassroomActionScope? _scope;
    private ClassroomActionPreview? _preview;
    private ClassroomActionChange? _change;
    private ClassroomUsbEnrollment? _enrollment;
    private bool _busy, _posting, _closed, _updatingChoices, _missingReceipt, _showingHistory;
    internal event Action<ConsoleApiException>? AccessLost;

    public ClassroomActionPolicyWindow() => InitializeComponent();

    internal ClassroomActionPolicyWindow(ConsoleClient client, School school, Pending pending) : this()
    {
        _client = client;
        _school = school;
        _pending = pending;
        ActionPolicyContext.Text = school.Name;
        ActionPolicyStopMode.ItemsSource = ActionPolicyExitMode.ItemsSource = Modes;
        ActionPolicyStopFactor.ItemsSource = ActionPolicyExitFactor.ItemsSource = Factors;
        ActionPolicyKind.SelectedIndex = 0;
        ActionPolicyUsbLabel.TextChanged += UsbChanged;
        Opened += async (_, _) => await RunAsync(LoadCatalogAsync);
        Closing += (_, args) =>
        {
            if (_posting && !_closed)
            {
                args.Cancel = true;
                ActionPolicyStatus.Text = "正在提交原申请，请等回执或进入待核对。";
            }
        };
        Closed += (_, _) => Clear();
        UpdateActions();
    }

    internal void ClearAndClose()
    {
        Clear();
        Close();
    }

    private void Clear()
    {
        if (_closed) return;
        _closed = true;
        _lifetime.Cancel();
        ClearPreview();
        _scope = null;
        _catalog = null;
    }

    private ClassroomActionTarget? SelectedTarget => _catalog?.Term is { } term && ActionPolicyTarget.SelectedItem is Choice choice
        ? new(term.Id, ActionPolicyKind.SelectedIndex == 0 ? "GRADE" : "CLASS", choice.Id) : null;

    private ClassroomHistoricalUsbKey? SelectedHistoricalKey => (ActionPolicyHistoryList.SelectedItem as HistoricalKeyRow)?.Key;

    private bool MatchesSelection(ClassroomActionChange change) => _showingHistory
        ? SelectedHistoricalKey is { } key && change.Operation == "REVOKE_USB" && change.Target == key.Target && change.KeyId == key.KeyId
        : SelectedTarget == change.Target;

    private async Task LoadCatalogAsync(CancellationToken ct)
    {
        var selected = SelectedTarget;
        var catalog = await _client.GetClassroomActionCatalogAsync(_school.Id, ct);
        if (_closed) return;
        _catalog = catalog;
        ActionPolicyContext.Text = catalog.Term is { } term ? $"{_school.Name} · 正在使用 {term.Name}" : $"{_school.Name} · 当前没有启用学期";
        UpdateTargets(selected?.TermId == catalog.Term?.Id ? selected?.ScopeId : null);
        if (SelectedTarget is { } target) await ReadScopeAsync(target, ct);
        else ActionPolicyStatus.Text = catalog.Term is null ? "启用学期后可发布策略。现在仍可从「历史 U 盘登记」挂失。" : "请选择年级或班级，再读取当前范围。";
        if (_pending.Write is { } pending) ShowPending(pending);
    }

    private void UpdateTargets(string? selected)
    {
        var choices = ActionPolicyKind.SelectedIndex == 0 ? _catalog?.Grades.Select(grade => new Choice(grade.Id, grade.ToString())) :
            _catalog?.Rooms.Select(room =>
            {
                var grade = _catalog.Grades.FirstOrDefault(item => item.Id == room.GradeId);
                return new Choice(room.Id, grade is null ? room.ToString() : $"{grade.Name} · {room}");
            });
        var rows = choices?.ToArray() ?? [];
        _updatingChoices = true;
        ActionPolicyTarget.ItemsSource = rows;
        ActionPolicyTarget.SelectedItem = rows.FirstOrDefault(choice => choice.Id == selected);
        _updatingChoices = false;
        ClearScope();
    }

    private void KindChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!_updatingChoices) UpdateTargets(null);
    }

    private void TargetChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!_updatingChoices) ClearScope();
    }

    private void ClearScope()
    {
        ClearPreview();
        _scope = null;
        ActionPolicyUsbLabel.Clear();
        ActionPolicyUsbVolumes.ItemsSource = null;
        ActionPolicyUsbRegistry.ItemsSource = null;
        ActionPolicyStatus.Text = SelectedTarget is null ? "先选择要办理的范围。" : "读取所选范围后可编辑、登记或核对大屏。";
        if (_pending.Write is { } pending) ShowPending(pending);
        UpdateActions();
    }

    private async Task ReadScopeAsync(ClassroomActionTarget target, CancellationToken ct)
    {
        var scope = await _client.GetClassroomActionScopeAsync(_school.Id, target, ct);
        if (_closed || target != SelectedTarget) return;
        _scope = scope;
        var inherited = target.ScopeKind == "CLASS" ? scope.Rooms.Single().Before : null;
        var rules = scope.SavedActions ?? inherited?.Actions ?? InitialRules;
        ActionPolicyStopMode.SelectedItem = Modes.First(item => item.Id == rules.StopScheduledMonitoring.Mode);
        ActionPolicyExitMode.SelectedItem = Modes.First(item => item.Id == rules.ExitBackground.Mode);
        ActionPolicyStopFactor.SelectedItem = Factors.FirstOrDefault(item => item.Id == rules.StopScheduledMonitoring.Factor) ?? Factors[0];
        ActionPolicyExitFactor.SelectedItem = Factors.FirstOrDefault(item => item.Id == rules.ExitBackground.Factor) ?? Factors[0];
        ActionPolicySource.Text = scope.SavedActions is not null ? $"{scope.ScopeName} · 已发布{(target.ScopeKind == "GRADE" ? "年级默认" : "班级覆盖")}" :
            $"{scope.ScopeName} · {(inherited?.Source == "GRADE" ? "继承年级默认" : "沿用原教室端规则")}";
        ActionPolicyInherit.Content = target.ScopeKind == "GRADE" ? "撤下年级默认" : "恢复继承";
        ActionPolicyUsbRegistry.ItemsSource = scope.UsbKeys.Select(key => new Choice(key.KeyId, key.Label)).ToArray();
        var evidence = Evidence(scope).ToArray();
        ActionPolicyScreenEvidence.ItemsSource = evidence.Length == 0 ? new[] {"所选范围当前没有启用的大屏。"} : evidence;
        ActionPolicyStatus.Text = $"已读取 {scope.Rooms.Length} 个班、{scope.Screens.Length} 台大屏。设备回执可在「逐屏核对」查看。";
        if (_pending.Write is { } pending) ShowPending(pending);
        UpdateActions();
    }

    private async void LoadClicked(object? sender, RoutedEventArgs args)
    {
        if (SelectedTarget is { } target) await RunAsync(ct => ReadScopeAsync(target, ct));
    }

    private async void RefreshClicked(object? sender, RoutedEventArgs args) => await RunAsync(_showingHistory ? ReadHistoryAsync : LoadCatalogAsync);

    private async void OpenHistoryClicked(object? sender, RoutedEventArgs args) => await RunAsync(async ct =>
    {
        _showingHistory = true;
        await ReadHistoryAsync(ct);
        if (!_closed) ActionPolicyHistoryList.Focus();
    });

    private async Task ReadHistoryAsync(CancellationToken ct)
    {
        var original = SelectedHistoricalKey?.KeyId;
        var registry = await _client.GetClassroomHistoricalUsbKeysAsync(_school.Id, ct);
        if (_closed || !_showingHistory) return;
        var rows = registry.Keys.Select(key => new HistoricalKeyRow(key)).ToArray();
        ActionPolicyHistoryList.ItemsSource = rows;
        ActionPolicyHistoryList.SelectedItem = rows.FirstOrDefault(row => row.Key.KeyId == original);
        ActionPolicyStatus.Text = rows.Length == 0 ? "本校没有仍有效的历史登记。" : $"找到 {rows.Length} 支仍有效的历史登记。请选择原登记预览挂失。";
        if (_pending.Write is { } pending) ShowPending(pending);
    }

    private async void HistoryBackClicked(object? sender, RoutedEventArgs args)
    {
        _showingHistory = false;
        ActionPolicyHistoryList.ItemsSource = null;
        await RunAsync(LoadCatalogAsync);
        if (!_closed) ActionPolicyOpenHistory.Focus();
    }

    private async void PreviewHistoricalRevokeClicked(object? sender, RoutedEventArgs args)
    {
        if (_showingHistory && SelectedHistoricalKey is { } key)
            await RunAsync(ct => PreviewAsync(new(key.Term.Id, key.ScopeKind, key.ScopeId, "REVOKE_USB", null, null, key.KeyId), ct));
    }
    private void RuleChanged(object? sender, SelectionChangedEventArgs args) => UpdateActions();
    private void UsbChanged(object? sender, RoutedEventArgs args) => UpdateActions();
    private void ConfirmedChanged(object? sender, RoutedEventArgs args) => UpdateActions();
    private void CloseClicked(object? sender, RoutedEventArgs args) => Close();

    private async void ScanUsbClicked(object? sender, RoutedEventArgs args) => await RunAsync(async ct =>
    {
        var volumes = await Task.Run(ClassroomUsbMedia.List, ct);
        if (_closed) return;
        var current = ActionPolicyUsbVolumes.SelectedItem as ClassroomUsbVolume;
        ActionPolicyUsbVolumes.ItemsSource = volumes;
        ActionPolicyUsbVolumes.SelectedItem = volumes.SingleOrDefault(volume => volume.Root == current?.Root && volume.MediaHash == current.MediaHash);
        ActionPolicyStatus.Text = volumes.Length == 0 ? "未找到可核对硬件序列号的 USB 存储设备。" : "请核对盘符和卷名，再选原盘。";
    });

    private ClassroomActionChange Change(string operation, ClassroomActionRules? rules = null, ClassroomUsbProof? usb = null, string? keyId = null)
    {
        var scope = _scope ?? throw new InvalidOperationException();
        return new(scope.TermId, scope.ScopeKind, scope.ScopeId, operation, rules, usb, keyId);
    }

    private static ClassroomActionRule ReadRule(ComboBox mode, ComboBox factor)
    {
        var choice = (Choice)mode.SelectedItem!;
        return new(choice.Id, choice.Id == "VERIFY" ? ((Choice?)factor.SelectedItem)?.Id : null);
    }

    private async void PreviewRulesClicked(object? sender, RoutedEventArgs args) => await RunAsync(ct => PreviewAsync(
        Change("SET_POLICY", new(ReadRule(ActionPolicyStopMode, ActionPolicyStopFactor), ReadRule(ActionPolicyExitMode, ActionPolicyExitFactor))), ct));

    private async void PreviewInheritClicked(object? sender, RoutedEventArgs args) => await RunAsync(ct => PreviewAsync(Change("SET_POLICY"), ct));

    private async void PreviewRevokeClicked(object? sender, RoutedEventArgs args)
    {
        if (ActionPolicyUsbRegistry.SelectedItem is Choice key)
            await RunAsync(ct => PreviewAsync(Change("REVOKE_USB", keyId: key.Id), ct));
    }

    private async void PreviewEnrollmentClicked(object? sender, RoutedEventArgs args) => await BeginEnrollmentAsync(reissue: false);
    private async void PreviewReissueClicked(object? sender, RoutedEventArgs args) => await BeginEnrollmentAsync(reissue: true);

    private async Task BeginEnrollmentAsync(bool reissue)
    {
        if (ActionPolicyUsbVolumes.SelectedItem is not ClassroomUsbVolume volume || ActionPolicyUsbLabel.Text?.Trim() is not { Length: > 0 } label) return;
        await RunAsync(async ct =>
        {
            ClearPreview();
            var enrollment = await Task.Run(() => ClassroomUsbEnrollment.Prepare(volume, label, reissue), ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                await PreviewAsync(Change("REGISTER_USB", usb: enrollment.Proof), ct, volume.ToString());
                if (_preview is not null && !_closed) { _enrollment = enrollment; enrollment = null; }
            }
            finally { enrollment?.Dispose(); }
        });
    }

    private async Task PreviewAsync(ClassroomActionChange change, CancellationToken ct, string? volume = null)
    {
        var preview = await _client.PreviewClassroomActionAsync(_school.Id, change, ct);
        if (_closed || !MatchesSelection(change)) return;
        if (_showingHistory != (preview.HistoricalTerm is not null))
            throw new ConsoleApiException(409, "原学期状态已改变，请刷新并在对应范围重新核对。", "ACTION_POLICY_TERM_CHANGED");
        var revokedKey = change.Operation == "REVOKE_USB" ? preview.UsbKeys.SingleOrDefault(key => key.KeyId == change.KeyId) : null;
        if (change.Operation == "REVOKE_USB" && revokedKey is null)
            throw new ConsoleApiException(409, "服务器未确认本次预览的原登记，请刷新并重新核对。", "ACTION_POLICY_USB_CHANGED");
        _change = change;
        _preview = preview;
        ActionPolicyConfirmed.IsChecked = false;
        var verb = change.Operation switch
        {
            "REGISTER_USB" => $"{(change.UsbKey?.Replaces is null ? "登记" : "重新登记")} {change.UsbKey?.Label} · {volume}",
            "REVOKE_USB" => $"撤销 {revokedKey!.Label}",
            _ when change.Actions is null => change.ScopeKind == "GRADE" ? "撤下年级默认" : "恢复继承",
            _ => "发布操作权限"
        };
        int changed = preview.Rooms.Count(room => room.Before.PolicyId != room.After.PolicyId);
        int screens = preview.Screens.Count(screen => preview.Rooms.Any(room => room.Id == screen.ClassId && room.Before.PolicyId != room.After.PolicyId));
        var note = change.Operation == "REVOKE_USB" ? "离线大屏同步后才会撤销。" :
            change.UsbKey?.Replaces is not null ? "确认时只替换这支盘上已撤销的本校钥匙。" : "离线大屏沿用上次已应用策略。";
        if (preview.CanApply && preview.Issues.Length > 0) note += " 受影响班级在补齐验证方式前，列出的现场操作将无法通过验证。";
        ActionPolicyReviewSummary.Text = preview.HistoricalTerm is { } history
            ? $"{verb} · {HistoricalTermText(history)} · {ScopeText(preview.ScopeKind, preview.ScopeName, preview.ScopeId, preview.HistoricalScopeState)}\n钥匙编号 {change.KeyId}\n原范围有 {changed} 个班的规则变化，涉及 {screens} 个大屏绑定。原学期与班级状态保持。离线大屏须等有效绑定同步；此处记录学校撤销。"
            : $"{verb} · {preview.ScopeName}\n{changed} 个班、{screens} 台大屏的有效策略将变化。{note}";
        var review = new List<string>();
        foreach (var room in preview.Rooms)
        {
            var problems = preview.Issues.Where(issue => issue.ClassId == room.Id).Select(issue =>
                $"{ActionName(issue.Action)}缺少验证条件（本班口令{(issue.PasswordConfigured ? "已设置" : "未设置")}，适用 U 盘 {issue.UsbCount} 支）");
            var targets = preview.Screens.Where(screen => screen.ClassId == room.Id).Select(screen =>
                $"{screen.BindingName}（{(screen.DeviceName ?? (_showingHistory ? "当前设备待核对" : "未配对"))}）");
            string screenText = string.Join("、", targets);
            var before = _showingHistory ? "原策略" : "当前";
            var after = change.Operation == "REVOKE_USB" ? "撤销后" : "发布后";
            string changeText = room.Before.PolicyId == room.After.PolicyId ? $"保持 {PolicyText(room.Before)}" :
                $"{before} {PolicyText(room.Before)}\n{after} {PolicyText(room.After)}";
            var screenLabel = _showingHistory ? "可核对原大屏" : "大屏";
            review.Add($"{room.Name} · {room.Code}\n{changeText}\n{screenLabel} { (screenText.Length == 0 ? "暂无" : screenText)}" +
                (problems.Any() ? "\n" + string.Join("；", problems) : ""));
        }
        if (_showingHistory && review.Count == 0) review.Add("原范围目前无可核对的班级或大屏。仍可撤销此钥匙的原登记。");
        ActionPolicyReviewRows.ItemsSource = review;
        ActionPolicyApply.Content = _showingHistory ? "确认撤销原登记" : change.Operation == "REVOKE_USB" ? "确认撤销" : "确认发布";
        ActionPolicyStatus.Text = preview.CanApply ? change.Operation == "REVOKE_USB" ? "请核对原登记和影响，再确认撤销。" : "核对后再确认发布。" :
            "请先为列出的班级补齐验证条件，再重新预览。";
        UpdateActions();
        ActionPolicyReviewRows.Focus();
    }

    private void ReviewBackClicked(object? sender, RoutedEventArgs args)
    {
        string? operation = _change?.Operation;
        ClearPreview();
        UpdateActions();
        if (_showingHistory) ActionPolicyHistoryList.Focus();
        else (operation is "REGISTER_USB" ? ActionPolicyEnroll : operation is "REVOKE_USB" ? ActionPolicyRevoke : ActionPolicySavePreview).Focus();
    }

    private void ClearPreview()
    {
        _enrollment?.Dispose();
        _enrollment = null;
        _preview = null;
        _change = null;
    }

    private async void ApplyClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy || _pending.Write is not null || _change is not { } change || _preview is not { CanApply: true } preview || ActionPolicyConfirmed.IsChecked != true) return;
        var pending = new PendingWrite(Guid.NewGuid(), change, preview.Version);
        var enrollment = _enrollment;
        _enrollment = null;
        await RunAsync(async ct =>
        {
            if (enrollment is not null)
            {
                _posting = true;
                try { await Task.Run(enrollment.WriteAndVerify, ct); }
                finally { _posting = false; }
            }
            ct.ThrowIfCancellationRequested();
            _pending.Write = pending;
            _missingReceipt = false;
            ClearPreview();
            UpdateActions();
            await SubmitAsync(pending, ct);
        });
        enrollment?.Dispose();
        if (_pending.Write is null && _preview is not null) { ClearPreview(); UpdateActions(); }
    }

    private async Task SubmitAsync(PendingWrite pending, CancellationToken ct)
    {
        ClassroomActionReceipt? receipt;
        _posting = true;
        try { receipt = await _client.ApplyClassroomActionAsync(_school.Id, pending.Change, pending.Version, pending.RequestId, ct); }
        catch (ConsoleApiException error) when (!_closed && error.StatusCode is 401 or 403 || !_closed && error.Code is
            "ACTION_POLICY_STALE" or "ACTION_POLICY_TERM_CHANGED" or "ACTION_POLICY_SCOPE_NOT_FOUND" or "ACTION_POLICY_NO_CHANGE" or
            "ACTION_POLICY_USB_REGISTERED" or "ACTION_POLICY_USB_CHANGED" or "ACTION_POLICY_USB_LIMIT" or "ACTION_POLICY_USB_REISSUE_UNAVAILABLE" or "INVALID_REQUEST")
        {
            _pending.Write = null;
            throw;
        }
        catch (ConsoleApiException) when (!_closed) { receipt = null; }
        finally { _posting = false; UpdateActions(); }
        if (_closed) return;
        if (receipt is not null) await ShowReceiptAsync(receipt, ct);
        else await ReadBackAsync(pending, ct);
    }

    private async void ReadReceiptClicked(object? sender, RoutedEventArgs args)
    {
        if (_pending.Write is { } pending) await RunAsync(ct => ReadBackAsync(pending, ct));
    }

    private async void RetryClicked(object? sender, RoutedEventArgs args)
    {
        if (_missingReceipt && _pending.Write is { } pending && MatchesSelection(pending.Change))
            await RunAsync(ct => SubmitAsync(pending, ct));
    }

    private async Task ReadBackAsync(PendingWrite pending, CancellationToken ct)
    {
        ShowPending(pending);
        try { await ShowReceiptAsync(await _client.ReadClassroomActionReceiptAsync(_school.Id, pending.Change, pending.RequestId, ct), ct); }
        catch (ConsoleApiException error) when (!_closed && error.Code == "ACTION_POLICY_RECEIPT_NOT_FOUND")
        {
            _missingReceipt = true;
            ActionPolicyStatus.Text = $"未查到本人的原回执 {pending.RequestId:D}。可在原范围显式重试同一编号。";
        }
    }

    private async Task ShowReceiptAsync(ClassroomActionReceipt receipt, CancellationToken ct)
    {
        if (_closed) return;
        _pending.Write = null;
        _missingReceipt = false;
        var target = new ClassroomActionTarget(receipt.TermId, receipt.ScopeKind, receipt.ScopeId);
        var message = $"本人已发布 · 版本 {receipt.Revision} · {receipt.ChangedClasses} 个班的有效策略变化。逐屏是否已应用请看当前回执。";
        if (_showingHistory)
        {
            if (receipt.Operation == "REVOKE_USB") message = $"已撤销原登记 · 版本 {receipt.Revision}。离线大屏仍需同步。";
            try { await ReadHistoryAsync(ct); }
            catch (ConsoleApiException error) when (error.StatusCode is not (401 or 403)) { message += " 历史列表暂不可刷新。"; }
        }
        else if (SelectedTarget == target)
        {
            try { await ReadScopeAsync(target, ct); }
            catch (ConsoleApiException error) when (error.StatusCode is not (401 or 403)) { message += " 当前范围暂不可读。"; }
        }
        if (!_closed) ActionPolicyStatus.Text = message;
    }

    private void ShowPending(PendingWrite pending)
    {
        ActionPolicyStatus.Text = $"原请求待核对 {pending.RequestId:D} · 原范围 {pending.Change.ScopeId}。可只读查本人回执。";
    }

    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (_closed || _busy) return;
        _busy = true;
        UpdateActions();
        try { await work(_lifetime.Token); }
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error)
        {
            if (_closed) return;
            if (error.StatusCode is 401 or 403)
            {
                _pending.Write = null;
                AccessLost?.Invoke(error);
                ClearAndClose();
            }
            else ActionPolicyStatus.Text = _pending.Write is { } pending ? $"{error.Message} 原请求 {pending.RequestId:D} 待核对。" : error.Message;
        }
        catch (Exception error) when (!_closed && error is IOException or UnauthorizedAccessException)
        { ActionPolicyStatus.Text = error is IOException && error.Message.StartsWith("原", StringComparison.Ordinal) ? error.Message : "没有完成 U 盘文件核对或写入，请检查原盘后重新预览。"; }
        finally { _busy = false; UpdateActions(); }
    }

    private void UpdateActions()
    {
        if (_closed || ActionPolicyTabs is null) return;
        bool previewing = _preview is not null;
        bool canRead = !_busy && !previewing;
        bool canEdit = canRead && !_showingHistory && _scope is not null && _pending.Write is null;
        ActionPolicyKind.IsEnabled = ActionPolicyTarget.IsEnabled = canRead && !_showingHistory && _catalog?.Term is not null;
        ActionPolicyLoad.IsEnabled = canRead && !_showingHistory && SelectedTarget is not null;
        ActionPolicyOpenHistory.IsEnabled = canRead && !_showingHistory;
        ActionPolicyRefresh.IsEnabled = canRead;
        ActionPolicyTabs.IsVisible = _scope is not null && !_showingHistory && !previewing;
        ActionPolicyHistoryPane.IsVisible = _showingHistory && !previewing;
        ActionPolicyHistoryBack.IsEnabled = canRead;
        ActionPolicyHistoryRevoke.IsEnabled = canRead && _pending.Write is null && SelectedHistoricalKey is not null;
        ActionPolicyReviewPane.IsVisible = previewing;
        ActionPolicyReviewBack.IsVisible = previewing;
        ActionPolicyReviewBack.IsEnabled = !_busy;
        ActionPolicyConfirmed.IsVisible = ActionPolicyApply.IsVisible = _preview?.CanApply == true;
        ActionPolicyApply.IsEnabled = !_busy && _pending.Write is null && ActionPolicyConfirmed.IsChecked == true;
        ActionPolicyStopMode.IsEnabled = ActionPolicyExitMode.IsEnabled = canEdit;
        ActionPolicyStopFactor.IsEnabled = canEdit && ((Choice?)ActionPolicyStopMode.SelectedItem)?.Id == "VERIFY";
        ActionPolicyExitFactor.IsEnabled = canEdit && ((Choice?)ActionPolicyExitMode.SelectedItem)?.Id == "VERIFY";
        ActionPolicySavePreview.IsEnabled = canEdit;
        ActionPolicyInherit.IsEnabled = canEdit && _scope?.SavedActions is not null;
        ActionPolicyScanUsb.IsEnabled = canEdit;
        ActionPolicyEnroll.IsEnabled = ActionPolicyReissue.IsEnabled = canEdit && ActionPolicyUsbVolumes.SelectedItem is ClassroomUsbVolume &&
            !string.IsNullOrWhiteSpace(ActionPolicyUsbLabel.Text);
        ActionPolicyRevoke.IsEnabled = canEdit && ActionPolicyUsbRegistry.SelectedItem is Choice;
        ActionPolicyReadReceipt.IsVisible = _pending.Write is not null;
        ActionPolicyReadReceipt.IsEnabled = !_busy;
        ActionPolicyRetry.IsVisible = _missingReceipt && _pending.Write is { } pending && MatchesSelection(pending.Change);
        ActionPolicyRetry.IsEnabled = !_busy;
    }

    private static IEnumerable<string> Evidence(ClassroomActionScope scope)
    {
        foreach (var screen in scope.Screens)
        {
            var room = scope.Rooms.Single(row => row.Id == screen.ClassId);
            string state = screen.DeviceId is null ? "未配对" :
                screen.ObservationCurrent && screen.Observed is { Error: null } observed && observed.PolicyId == room.Before.PolicyId &&
                    observed.Revision >= room.Before.RequiredRevision ? "设备已应用" :
                screen.Observed is { Error: not null } error ? $"设备待核对（{error.Error}）" : "等待同步或支持此策略的回执";
            string historical = screen.LastApplied is { } last && screen.LastAppliedAt is { } at ?
                $" · 上次应用版本 {last.Revision}（{at.ToLocalTime():MM-dd HH:mm:ss}）" : "";
            yield return $"{room.Name} · {screen.BindingName} · {state}\n{(screen.DeviceName ?? "暂无原生设备")}{historical} · 大屏编号 {screen.BindingId}";
        }
    }

    private static string PolicyText(ClassroomEffectiveAction policy) => policy.Actions is { } actions ?
        $"{(policy.Source == "CLASS" ? "班级覆盖" : "年级默认")} · 停止 {RuleText(actions.StopScheduledMonitoring)} · 退出 {RuleText(actions.ExitBackground)} · U 盘 {policy.UsbCount} 支" : "沿用原教室端规则";

    private static string RuleText(ClassroomActionRule rule) => rule.Mode == "VERIFY" ? Factors.Single(item => item.Id == rule.Factor).Label :
        Modes.Single(item => item.Id == rule.Mode).Label;

    private static string ActionName(string action) => action == "exitBackground" ? "退出后台" : "停止本场";

    private static string HistoricalTermText(ClassroomActionHistoricalTerm term) => term.Status switch
    {
        "DRAFT" => $"{term.Name}（草稿）", "ACTIVE" => $"{term.Name}（启用中）", "ARCHIVED" => $"{term.Name}（已归档）",
        _ => $"原学期 {term.Id}（资料待核对）"
    };

    private static string ScopeText(string kind, string name, string id, string? state) =>
        $"{(kind == "GRADE" ? "年级" : "班级")} {name}{(state == "INACTIVE" ? "（已停用）" : state == "MISSING" ? $"（{id}）" : "")}";
}
