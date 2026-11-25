using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class WebsitePolicyWindow : Window
{
    internal sealed class Pending
    {
        public Write? Value { get; set; }
    }

    internal sealed record Write(Guid RequestId, ClassroomActionTarget Target, string[]? Hosts, string Version);
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
    private bool _reading, _posting, _closing, _updating, _dirty, _canRetry, _compact;
    internal event Action<ConsoleApiException>? AccessLost;

    public WebsitePolicyWindow() => InitializeComponent();

    internal WebsitePolicyWindow(ConsoleClient client, School school, Pending pending) : this()
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
            _compact = args.NewSize.Height < 520;
            UpdateActions();
        };
        Opened += async (_, _) => await RunAsync(LoadCatalogAsync);
        Closing += (_, args) =>
        {
            if (_closing || !(_posting || _dirty && _pending.Value is null)) return;
            args.Cancel = true;
            Feedback.Text = _posting ? "原申请正在提交，请等结果。" : "请先发布或放弃本次编辑。";
        };
        Closed += (_, _) => { _closing = true; _lifetime.Cancel(); };
        UpdateActions();
    }

    internal void ClearAndClose()
    {
        if (_closing) return;
        _closing = true;
        _pending.Value = null;
        _lifetime.Cancel();
        Close();
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
        _catalog = await _client.GetWebsitePolicyCatalogAsync(_school.Id, _lifetime.Token);
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
        _updating = true;
        Inherit.IsChecked = _scope.BeforeHosts is null;
        Hosts.Text = string.Join(Environment.NewLine, _scope.BeforeHosts ?? []);
        _updating = false;
        _dirty = false;
        Inherit.Content = _scope.ScopeKind == "CLASS" ? "跟随本班所在年级" : "取消该年级默认规则";
        EditorHeading.Text = $"{(_scope.ScopeKind == "CLASS" ? "班级" : "年级")} · {_scope.ScopeName}";
        RenderRows();
        UpdateActions();
    }

    private string[]? DraftHosts() => Inherit.IsChecked == true ? null :
        (Hosts.Text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToArray();

    private void EditChanged()
    {
        if (_updating || _scope is null || _pending.Value is not null) return;
        _preview = null;
        var draft = DraftHosts();
        var saved = _scope.BeforeHosts;
        _dirty = draft is null ? saved is not null : saved is null || !draft.SequenceEqual(saved);
        RenderRows();
        UpdateActions();
    }

    private async void PreviewClicked(object? sender, RoutedEventArgs args)
    {
        if (_target is not { } target || !_dirty || _pending.Value is not null) return;
        var hosts = DraftHosts();
        await RunAsync(async () =>
        {
            _preview = await _client.PreviewWebsitePolicyAsync(_school.Id, target, hosts, _lifetime.Token);
            Feedback.Text = _preview.CanApply ? "核对班级实际清单与设备后发布。" : "规范化后的域名清单已与学校配置一致。";
            RenderRows();
        });
    }

    private async void PublishClicked(object? sender, RoutedEventArgs args)
    {
        if (_posting || _reading) return;
        if (_pending.Value is not { } write)
        {
            if (_preview is not { CanApply: true } preview || _target is not { } target) return;
            write = new(Guid.NewGuid(), target, preview.AfterHosts, preview.Version);
            _pending.Value = write;
        }
        else if (!_canRetry) return;
        _posting = true;
        _canRetry = false;
        UpdateActions();
        try
        {
            var receipt = await _client.ApplyWebsitePolicyAsync(_school.Id, write.Target, write.Hosts, write.Version, write.RequestId, _lifetime.Token);
            _posting = false;
            await FinishAsync(write, receipt);
        }
        catch (ConsoleApiException e) when (e.Code == "WEBSITE_POLICY_STALE" || e.Code == "WEBSITE_POLICY_TERM_CHANGED")
        {
            _pending.Value = null;
            _preview = null;
            Feedback.Text = "当前范围已变化，本次未保存。保留编辑内容，重新预览后再确认。";
            RenderRows();
        }
        catch (ConsoleApiException e) when (e.StatusCode is 401 or 403) { LoseAccess(e); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception e)
        {
            Feedback.Text = $"原申请待核对 · {write.RequestId:D}。{e.Message}";
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
                _canRetry = true;
                Feedback.Text = $"暂未查到本人原回执 · {write.RequestId:D}。可按原申请重试。";
            }
        });
    }

    private async Task FinishAsync(Write write, WebsitePolicyReceipt receipt)
    {
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
            Feedback.Text += " 当前范围未能重新读取。";
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
            Feedback.Text = "请逐行输入有效域名，去除网址协议、路径、端口和通配符。最多 24 个。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception e) { Feedback.Text = e.Message; }
        finally { _reading = false; UpdateActions(); }
    }

    private void LoseAccess(ConsoleApiException error)
    {
        ClearAndClose();
        AccessLost?.Invoke(error);
    }

    private void RenderRows()
    {
        var shown = _preview ?? _scope;
        PreviewHeading.IsVisible = shown?.Rows.Length > 0;
        PreviewHeading.Text = _preview is null ? "当前班级规则" : "现在与发布后的班级规则";
        RoomRows.ItemsSource = shown?.Rows.Select(row => _preview is null
            ? $"{row.Name} · {row.Code} · {Caption(row.Before)}"
            : $"{row.Name} · {row.Code}\n现在：{Caption(row.Before)}\n发布后：{Caption(row.After)}").ToArray();
        ScreenHeading.IsVisible = shown?.Screens.Length > 0;
        ScreenHeading.Text = _preview is null ? "逐屏核对当前配置" : "逐屏查看发布前状态";
        ScreenRows.ItemsSource = shown?.Screens.OrderBy(screen => screen.Status != "RECEIVED_NOT_APPLIED").Select(screen =>
        {
            var room = shown.Rows.First(row => row.Id == screen.ClassId);
            string status = screen.Status switch
            {
                "RECEIVED_NOT_APPLIED" => $"设备已收到 · Edge 生效未确认 · {screen.ReceivedAt?.ToLocalTime():MM-dd HH:mm:ss}",
                "NOT_PAIRED" => "未配对原生设备",
                _ => "等待当前绑定的设备报告"
            };
            return $"{room.Name} · {screen.BindingName} · {screen.DeviceName ?? "无设备"}\n{status}";
        }).ToArray();
    }

    private static string Caption(WebsitePolicyEffective policy)
    {
        string source = policy.Source switch { "GRADE" => "年级默认", "CLASS" => "本班自定", _ => "未设置" };
        return $"{source} · {(policy.BlockedHosts.Length == 0 ? "无下发域名" : string.Join("、", policy.BlockedHosts))}";
    }

    private void UpdateActions()
    {
        if (_closing) return;
        bool available = !_reading && !_posting;
        bool unresolved = _pending.Value is not null;
        ScopeChoice.IsEnabled = ScopeKind.IsEnabled = available && !_dirty && !unresolved;
        ReadScopeButton.IsEnabled = ScopeChoice.IsEnabled && ScopeChoice.SelectedItem is not null && _catalog?.Term is not null;
        Editor.IsVisible = _scope is not null;
        DetailsBody.Spacing = _compact ? 6 : 12;
        Editor.Padding = new(_compact ? 8 : 12);
        EditorHeading.IsVisible = !_compact;
        PublicationNote.IsVisible = !_compact || _scope is null || _dirty;
        Inherit.IsEnabled = available && !unresolved;
        Hosts.IsEnabled = Inherit.IsEnabled && Inherit.IsChecked != true;
        Hosts.IsVisible = HostTips.IsVisible = Inherit.IsChecked != true;
        PreviewButton.IsVisible = _dirty && _preview is null && !unresolved;
        PreviewButton.IsEnabled = available && _dirty && !unresolved;
        PublishButton.IsVisible = _preview is not null || unresolved && _canRetry;
        PublishButton.IsEnabled = available && (unresolved ? _canRetry : _preview is { CanApply: true });
        PublishButton.Content = unresolved ? "按原申请重试" : "发布这份配置";
        ReceiptButton.IsVisible = unresolved;
        ReceiptButton.IsEnabled = available && unresolved;
        DiscardButton.IsVisible = _dirty && !unresolved;
        DiscardButton.IsEnabled = available;
        ReturnButton.IsEnabled = !_posting;
    }

    private void DiscardClicked(object? sender, RoutedEventArgs args) => ResetEditor();
    private void ReturnClicked(object? sender, RoutedEventArgs args) => Close();
}
