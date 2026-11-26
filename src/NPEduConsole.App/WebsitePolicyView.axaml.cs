using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

internal sealed record WebsitePolicyRoomRow(string Title, string StatusBadge, string PublishedSummary, string PendingSummary,
    bool HasPending, bool HasChanges);
internal sealed record WebsitePolicyScreenRow(string Title, string StatusBadge, string Evidence, bool IsUnreported);

public sealed partial class WebsitePolicyView : UserControl
{
    internal sealed class Pending
    {
        public Write? Value { get; set; }
    }

    internal sealed record Write(
        Guid RequestId,
        ClassroomActionTarget Target,
        string[]? Hosts,
        string Version,
        bool UseTimedApi = false,
        WebsitePolicyDraft? Draft = null,
        string? ScopeLabel = null,
        string? TermLabel = null);
    private sealed record Choice(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly Pending _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private ClassroomActionCatalog? _catalog;
    private ClassroomActionTarget? _target;
    private WebsitePolicySnapshot? _scope;
    private WebsitePolicySnapshot? _preview;
    private bool _reading, _posting, _closing, _updating, _dirty, _canRetry, _compact, _narrow, _reviewStep, _downgradeBlocked;
    internal event Action<ConsoleApiException>? AccessLost;
    internal Action? CloseRequested { get; set; }

    public WebsitePolicyView() => InitializeComponent();

    internal WebsitePolicyView(ConsoleClient client, School school, Pending pending) : this()
    {
        _client = client;
        _school = school;
        _pending = pending;
        ContextText.Text = school.Name;
        ScopeKind.SelectedIndex = 0;
        ScopeChoice.SelectionChanged += (_, _) => ScopeSelectionChanged();
        Hosts.TextChanged += (_, _) => EditChanged();
        Inherit.IsCheckedChanged += (_, _) => EditChanged();
        SizeChanged += (_, args) =>
        {
            _compact = args.NewSize.Height < 510;
            _narrow = args.NewSize.Width < 880;
            PolicyRoot.Classes.Set("narrow", _narrow);
            UpdateActions();
        };
        if (_pending.Value is { } original)
        {
            ShowReview();
            Feedback.Text = $"原申请待核对 · {original.RequestId:D}";
        }
        RenderRows();
        UpdateActions();
    }

    internal Task StartAsync()
    {
        (_pending.Value is null ? ReadScopeButton : ReceiptButton).BringIntoView();
        return RunAsync(LoadCatalogAsync);
    }

    internal bool CanClose()
    {
        if (_closing || !(_posting || _dirty && _pending.Value is null)) return true;
        Feedback.Text = _posting ? "原申请正在提交，请等结果。" : "草稿已保留。请先发布或放弃本次编辑，再返回目录。";
        Feedback.IsVisible = true;
        return false;
    }

    internal void End()
    {
        if (_closing) return;
        _closing = true;
        _lifetime.Cancel();
    }

    private void ShowReview()
    {
        _reviewStep = true;
        VerifyDetailsScroll.Offset = default;
        UpdateActions();
        if (_narrow) VerifyTabButton.Focus(NavigationMethod.Tab);
    }

    private void EditTabClicked(object? sender, RoutedEventArgs args)
    {
        _reviewStep = false;
        UpdateActions();
    }

    private void VerifyTabClicked(object? sender, RoutedEventArgs args)
    {
        _reviewStep = true;
        UpdateActions();
    }

    private void ChangeKind(object? sender, SelectionChangedEventArgs args)
    {
        UpdateChoices();
        ScopeSelectionChanged();
    }

    private void ScopeSelectionChanged()
    {
        if (ScopeChoice is null || _updating || _dirty || _pending.Value is not null) return;
        if (_target is { } target && (ScopeChoice.SelectedItem is not Choice choice || choice.Id != target.ScopeId ||
            (ScopeKind.SelectedIndex == 0 ? "GRADE" : "CLASS") != target.ScopeKind))
        {
            _target = null;
            _scope = _preview = null;
            RenderRows();
        }
        UpdateActions();
    }

    private void UpdateChoices()
    {
        if (ScopeChoice is null || _catalog is not { } catalog) return;
        ScopeChoice.ItemsSource = ScopeKind.SelectedIndex == 0
            ? catalog.Grades.Select(row => new Choice(row.Id, $"{row.Name} · {row.Code}")).ToArray()
            : catalog.Rooms.Select(row => new Choice(row.Id, $"{row.Name} · {row.Code}")).ToArray();
        ScopeChoice.SelectedIndex = -1;
    }

    private async Task LoadCatalogAsync()
    {
        var catalog = await _client.GetWebsitePolicyCatalogAsync(_school.Id, _lifetime.Token);
        if (_closing) return;
        _catalog = catalog;
        ContextText.Text = $"{_school.Name} · {(_catalog.Term is { } term ? term.Name + "（启用）" : "暂无启用学期")}";
        UpdateChoices();
        if (_pending.Value is { } pending)
            Feedback.Text = $"原申请待核对 · {pending.RequestId:D}";
        else if (_catalog.Term is null)
            Feedback.Text = "启用学期后可设置年级默认与班级规则。";
    }

    private async void ReadScopeClicked(object? sender, RoutedEventArgs args)
    {
        if (_dirty || _pending.Value is not null || _catalog?.Term is not { } term || ScopeChoice.SelectedItem is not Choice choice) return;
        await RunAsync(() => LoadScopeAsync(new(term.Id, ScopeKind.SelectedIndex == 0 ? "GRADE" : "CLASS", choice.Id)));
    }

    private async Task LoadScopeAsync(ClassroomActionTarget target)
    {
        var result = await _client.GetWebsitePolicyAsync(_school.Id, target, _lifetime.Token);
        if (_closing) return;
        _updating = true;
        ScopeKind.SelectedIndex = target.ScopeKind == "GRADE" ? 0 : 1;
        ScopeChoice.SelectedItem = ScopeChoice.Items.OfType<Choice>().FirstOrDefault(row => row.Id == target.ScopeId);
        _updating = false;
        _target = target;
        _scope = result;
        ResetEditor();
    }

    private void ResetEditor()
    {
        if (_scope is null) return;
        _preview = null;
        _downgradeBlocked = false;
        ResetTimingState(_scope);
        _dirty = false;
        Inherit.Content = _scope.ScopeKind == "CLASS" ? "跟随本班所在年级" : "取消该年级默认规则";
        EditorHeading.Text = "访问规则";
        if (_downgradeBlocked)
            Feedback.Text = "当前服务端不支持分时网站规则，已保留当前分时草稿且禁止降格或分时写入。";
        RenderRows();
        UpdateActions();
    }

    private string[]? DraftHosts() => Inherit.IsChecked == true ? null : DraftHostLines();

    private string[] DraftHostLines() =>
        (Hosts.Text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToArray();

    private void EditChanged()
    {
        if (_updating || _scope is null || _pending.Value is not null) return;
        InvalidatePreview();
        SyncTimingRowsFromHosts();
        RecomputeDirtyState();
        RenderRows();
        UpdateActions();
    }

    private void InvalidatePreview()
    {
        if (_preview is not null) Feedback.Text = "草稿已修改，请重新预览。";
        _preview = null;
    }

    private async void PreviewClicked(object? sender, RoutedEventArgs args)
    {
        if (_target is not { } target || !_dirty || _pending.Value is not null || _downgradeBlocked) return;
        bool useTimed = _scope?.TimingSupported == true;
        if (!TryBuildCurrentDraft(out var draft))
        {
            Feedback.Text = "分时域名请至少勾选「上课」「课间」「放学后」之一，或改回「全天」。";
            UpdateActions();
            return;
        }
        var hosts = DraftHosts();
        if (!useTimed && WebsitePolicyTiming.HasTimedContent(draft))
        {
            _downgradeBlocked = true;
            Feedback.Text = "当前服务端不支持分时网站规则，已保留当前分时草稿且禁止降格或分时写入。";
            UpdateActions();
            return;
        }
        await RunAsync(async () =>
        {
            var preview = useTimed
                ? await _client.PreviewTimedWebsitePolicyAsync(_school.Id, target, draft, _lifetime.Token)
                : await _client.PreviewWebsitePolicyAsync(_school.Id, target, hosts, _lifetime.Token);
            if (_closing) return;
            if (!preview.TimingSupported && WebsitePolicyTiming.HasTimedContent(draft))
            {
                _preview = null;
                _downgradeBlocked = true;
                Feedback.Text = "当前服务端不支持分时网站规则，已保留当前分时草稿且禁止降格或分时写入。";
                RenderRows();
                return;
            }
            _preview = preview;
            Feedback.Text = _preview.CanApply ? "核对班级实际清单与设备后发布。" : "规范化后的域名清单已与学校配置一致。";
            RenderRows();
            ShowReview();
        });
    }

    private async void PublishClicked(object? sender, RoutedEventArgs args)
    {
        if (_posting || _reading) return;
        if (_pending.Value is not { } write)
        {
            if (_downgradeBlocked || _preview is not { CanApply: true } preview || _target is not { } target) return;
            if (!TryBuildCurrentDraft(out var localDraft))
            {
                Feedback.Text = "分时域名请至少勾选「上课」「课间」「放学后」之一，或改回「全天」。";
                UpdateActions();
                return;
            }
            if (!preview.TimingSupported && WebsitePolicyTiming.HasTimedContent(localDraft))
            {
                _downgradeBlocked = true;
                Feedback.Text = "当前服务端不支持分时网站规则，已保留当前分时草稿且禁止降格或分时写入。";
                UpdateActions();
                return;
            }
            bool useTimed = preview.TimingSupported;
            WebsitePolicyDraft? publishDraft = null;
            if (useTimed && !WebsitePolicyTiming.TryResolvePublishDraft(preview, localDraft, out publishDraft))
            {
                _downgradeBlocked = true;
                Feedback.Text = "预览结果缺少分时草稿，已取消发布以避免降格。";
                UpdateActions();
                return;
            }
            write = new(Guid.NewGuid(), target, preview.AfterHosts, preview.Version, useTimed, publishDraft,
                _scope?.ScopeName, _catalog?.Term?.Name);
            _pending.Value = write;
        }
        else if (!_canRetry) return;
        _posting = true;
        _canRetry = false;
        Feedback.Text = $"正在提交申请 · {write.RequestId:D}";
        RenderRows();
        UpdateActions();
        try
        {
            var receipt = write.UseTimedApi
                ? await _client.ApplyTimedWebsitePolicyAsync(_school.Id, write.Target, write.Draft, write.Version, write.RequestId, _lifetime.Token)
                : await _client.ApplyWebsitePolicyAsync(_school.Id, write.Target, write.Hosts, write.Version, write.RequestId, _lifetime.Token);
            _posting = false;
            await FinishAsync(write, receipt);
        }
        catch (ConsoleApiException e) when (e.Code == "WEBSITE_POLICY_STALE" || e.Code == "WEBSITE_POLICY_TERM_CHANGED")
        {
            if (_closing) return;
            _pending.Value = null;
            _preview = null;
            Feedback.Text = "当前范围已变化，本次未保存。保留编辑内容，重新预览后再确认。";
            RenderRows();
        }
        catch (ConsoleApiException e) when (e.StatusCode is 401 or 403) { LoseAccess(e); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception e)
        {
            if (!_closing) Feedback.Text = $"原申请待核对 · {write.RequestId:D}。{e.Message}";
        }
        finally { _posting = false; UpdateActions(); }
    }

    private async void ReceiptClicked(object? sender, RoutedEventArgs args)
    {
        if (_pending.Value is not { } write) return;
        await RunAsync(async () =>
        {
            try
            {
                var receipt = await _client.GetWebsitePolicyReceiptAsync(_school.Id, write.Target, write.RequestId, _lifetime.Token);
                await FinishAsync(write, receipt);
            }
            catch (ConsoleApiException e) when (e.Code == "WEBSITE_POLICY_RECEIPT_NOT_FOUND")
            {
                if (_closing) return;
                _canRetry = true;
                Feedback.Text = $"暂未查到本人原回执 · {write.RequestId:D}。可按原申请重试。";
            }
        });
    }

    private async Task FinishAsync(Write write, WebsitePolicyReceipt receipt)
    {
        if (_closing || _pending.Value != write) return;
        _pending.Value = null;
        _canRetry = _dirty = false;
        _preview = null;
        _scope = null;
        RenderRows();
        Feedback.Text = $"学校配置已保存，{receipt.ChangedClasses} 个班的有效规则变化。回执 {receipt.RequestId:D}。Edge 生效另行核对。";
        try { await LoadScopeAsync(write.Target); }
        catch (Exception e) when (e is not OperationCanceledException &&
            e is not ConsoleApiException { StatusCode: 401 or 403 })
        {
            if (!_closing) Feedback.Text += " 当前范围未能重新读取。";
        }
    }

    private async Task RunAsync(Func<Task> run)
    {
        if (_reading || _posting || _closing) return;
        _reading = true;
        UpdateActions();
        try { await run(); }
        catch (ConsoleApiException e) when (e.StatusCode is 401 or 403) { LoseAccess(e); }
        catch (ConsoleApiException e) when (e.Code == "WEBSITE_POLICY_INVALID_HOSTS")
        {
            if (!_closing) Feedback.Text = "请逐行输入有效域名，去除网址协议、路径、端口和通配符。最多 24 个。";
        }
        catch (ConsoleApiException e) when (e.Code == "WEBSITE_POLICY_TIMING_UNSUPPORTED")
        {
            if (_closing) return;
            _downgradeBlocked = !TryBuildCurrentDraft(out var currentDraft) || WebsitePolicyTiming.HasTimedContent(currentDraft);
            Feedback.Text = "当前服务端不支持分时网站规则，已保留当前草稿且未降格写入。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception e) { if (!_closing) Feedback.Text = e.Message; }
        finally { _reading = false; UpdateActions(); }
    }

    private void LoseAccess(ConsoleApiException error)
    {
        if (_closing) return;
        _pending.Value = null;
        End();
        AccessLost?.Invoke(error);
    }

    private void RenderRows()
    {
        var shown = _preview ?? _scope;
        PreviewHeading.IsVisible = shown?.Rows.Length > 0;
        PreviewHeading.Text = _preview is null ? "当前班级规则" : "现在与发布后的班级规则";
        bool comparing = _preview is not null;
        RoomRows.ItemsSource = shown?.Rows.Select(row =>
        {
            bool changed = comparing && row.Before.PolicyId != row.After.PolicyId;
            return new WebsitePolicyRoomRow($"{row.Name} · {row.Code}",
                changed ? "将变更" : row.Before.Source == "NONE" ? "未设置" : "已发布",
                $"现在：{Caption(row.Before)}", $"发布后：{Caption(row.After)}", comparing, changed);
        }).ToArray();
        ScreenHeading.IsVisible = shown?.Screens.Length > 0;
        ScreenHeading.Text = _preview is null ? "逐屏核对当前配置" : "逐屏查看发布前状态";
        ScreenRows.ItemsSource = shown?.Screens.OrderBy(screen => screen.ReceivedAt is null).Select(screen =>
        {
            var room = shown.Rows.First(row => row.Id == screen.ClassId);
            var report = room.Before.Rules?.Any(rule => rule.Period != WebsitePolicyTiming.PeriodAlways) == true
                ? screen : screen with { SchedulePhase = null };
            return new WebsitePolicyScreenRow($"{room.Name} · {screen.BindingName}" +
                (screen.DeviceName is { } device && device != screen.BindingName ? $" · {device}" : ""), report.Status switch
                {
                    "NOT_PAIRED" => "未配对",
                    "RECEIVED_NOT_APPLIED" => "已接收",
                    "WRITTEN_PENDING" => "已写入",
                    "CLEARED_PENDING" => "已清除",
                    "EDGE_CONFIRMED" => "Edge 已核实",
                    "CONFLICT" => "规则冲突",
                    "FAILED" => "处理失败",
                    "TIMING_UPDATE_REQUIRED" => "需升级",
                    _ => "待报告"
                }, WebsitePolicyTiming.FormatScreenStatus(report), report.Status is "NOT_PAIRED" or "AWAITING_DEVICE");
        }).ToArray();
    }

    private static string Caption(WebsitePolicyEffective policy) => WebsitePolicyTiming.FormatEffective(policy);

    private void UpdateActions()
    {
        if (_closing) return;
        bool available = !_reading && !_posting;
        bool unresolved = _pending.Value is not null;
        bool editable = available && !unresolved && !_downgradeBlocked;
        bool incomplete = HasIncompleteTimingSelection();
        bool showScopeLabel = unresolved || _scope is not null && (_dirty || _narrow && _reviewStep);
        ScopePicker.IsVisible = !showScopeLabel;
        WorkingScope.IsVisible = showScopeLabel;
        if (_pending.Value is { } pending)
        {
            string kind = pending.Target.ScopeKind == "CLASS" ? "班级" : "年级";
            WorkingScope.Text = $"原办理范围 · {pending.TermLabel ?? pending.Target.TermId} · {kind} {pending.ScopeLabel ?? pending.Target.ScopeId}";
        }
        else if (showScopeLabel && _scope is { } scope)
        {
            string kind = scope.ScopeKind == "CLASS" ? "班级规则" : "年级默认";
            WorkingScope.Text = $"{kind} · {scope.ScopeName}";
        }
        PolicyRoot.Classes.Set("show-verify", _reviewStep);
        EditTabButton.Classes.Set("selected", !_reviewStep);
        VerifyTabButton.Classes.Set("selected", _reviewStep);
        EditTabButton.IsEnabled = !unresolved;
        VerifyTabButton.IsEnabled = _scope is not null || unresolved;
        ScopeChoice.IsEnabled = ScopeKind.IsEnabled = available && !_dirty && !unresolved;
        ReadScopeButton.IsEnabled = ScopeChoice.IsEnabled && ScopeChoice.SelectedItem is not null && _catalog?.Term is not null;
        Editor.IsVisible = _scope is not null;
        EditorEmpty.IsVisible = _scope is null && !unresolved;
        ReviewEmpty.IsVisible = _scope is null;
        ReviewEmpty.Text = unresolved ? "按原编号查学校保存结果。" : "读取范围后，可在这里核对班级规则和逐屏状态。";
        PublicationNote.IsVisible = _scope is not null;
        Inherit.IsEnabled = editable;
        Hosts.IsEnabled = editable && Inherit.IsChecked != true;
        Hosts.IsVisible = HostTips.IsVisible = Inherit.IsChecked != true;
        Hosts.MinHeight = _compact ? 54 : 72;
        Hosts.MaxHeight = _compact ? 108 : 168;
        UpdateTimingControls(editable);
        PreviewButton.IsVisible = _dirty && _preview is null && !unresolved;
        PreviewButton.IsEnabled = editable && _dirty && !unresolved && !incomplete;
        PublishButton.IsVisible = unresolved ? _canRetry : _preview is not null && (!_narrow || _reviewStep);
        PublishButton.IsEnabled = available && (unresolved ? _canRetry : !_downgradeBlocked && !incomplete && _preview is { CanApply: true });
        PublishButton.Content = unresolved ? "按原申请重试" : "发布这份配置";
        PublishButton.Classes.Set("accent", !unresolved);
        ReceiptButton.IsVisible = unresolved;
        ReceiptButton.IsEnabled = available && unresolved;
        DiscardButton.IsVisible = _dirty && !unresolved;
        DiscardButton.IsEnabled = available;
        ReturnButton.IsEnabled = !_posting;
        Feedback.IsVisible = !string.IsNullOrWhiteSpace(Feedback.Text);
    }

    private void DiscardClicked(object? sender, RoutedEventArgs args)
    {
        Feedback.Text = null;
        ResetEditor();
    }
    private void ReturnClicked(object? sender, RoutedEventArgs args) => CloseRequested?.Invoke();
}
