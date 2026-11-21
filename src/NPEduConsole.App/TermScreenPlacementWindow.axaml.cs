using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class TermScreenPlacementWindow : Window
{
    private sealed record Operation(TermScreenPlacementCheck Check, TermScreenCandidate Candidate, Guid RequestId);
    private static readonly ConditionalWeakTable<ConsoleClient, Dictionary<(string School, string Term, string Binding), Operation>> Requests = new();
    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly string _termId = "", _bindingId = "";
    private readonly CancellationTokenSource _lifetime = new();
    private TermScreenPlacementCheck? _check;
    private TermScreenPlacement? _placement;
    private string? _selectedId;
    private bool _busy, _writing, _closed, _scopeClosed, _filling, _canRetry;
    private Operation? Pending => Requests.GetOrCreateValue(_client).GetValueOrDefault((_school.Id, _termId, _bindingId));
    private TermScreenCandidate? Selected => PlacementCandidates.SelectedItem as TermScreenCandidate;

    internal Action<ConsoleApiException>? SessionExpired { get; init; }
    internal TermPreparationWindow.ScreenDestination? Destination { get; private set; }

    public TermScreenPlacementWindow() => InitializeComponent();

    internal TermScreenPlacementWindow(ConsoleClient client, School school, string termId, string bindingId) : this()
    {
        _client = client;
        _school = school;
        _termId = termId;
        _bindingId = bindingId;
        PlacementScope.Text = school.Name;
        PlacementBindingId.Text = $"绑定编号 {bindingId}";
        PlacementOriginal.Text = "正在读取该屏的原学期和原班。";
        SizeChanged += (_, _) => Arrange();
        Opened += async (_, _) =>
        {
            BatchWorkspaceWindow.FitSchoolWindow(this);
            await RunAsync(async () =>
            {
                if (Pending is { } pending)
                {
                    SetCheck(pending.Check);
                    await VerifyAsync(pending);
                }
                else await ReadAsync(focus: true);
            });
        };
        Closing += (_, args) => args.Cancel = !_scopeClosed && _writing;
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
        Render();
    }

    internal void CloseForScopeChange()
    {
        _scopeClosed = true;
        Close();
    }

    private void Arrange()
    {
        bool compact = ClientSize.Width < 900;
        PlacementEditor.ColumnDefinitions = new(compact ? "*" : "*,*");
        PlacementEditor.RowDefinitions = new(compact ? "Auto,Auto" : "Auto");
        Grid.SetColumn(PlacementTargetCard, compact ? 0 : 1);
        Grid.SetRow(PlacementTargetCard, compact ? 1 : 0);
        PlacementTargetPicker.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        PlacementTargetCard.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
    }

    private async Task ReadAsync(bool focus = false, bool keepNotice = false)
    {
        // A failed refresh must never leave a previous binding actionable.
        if (_check is { } old) _check = old with {CanArrange = false};
        if (_placement is { } known) _placement = known with {Status = "VERIFY", BindingConfirmed = false};
        Render();
        var snapshot = await _client.GetTermScreenPlacementAsync(_school.Id, _termId, _bindingId, _lifetime.Token);
        if (_closed) return;
        SetCheck(snapshot);
        if (!keepNotice) Notice(snapshot.Placement is null && !snapshot.CanArrange ? "当前无法继续安排，请核对学校总览。" : null);
        if (focus) Focus(snapshot.CanArrange ? PlacementSearch : PlacementRefresh);
    }

    private void SetCheck(TermScreenPlacementCheck snapshot)
    {
        _check = snapshot;
        _placement = snapshot.Placement;
        PlacementScope.Text = $"{_school.Name} · {snapshot.Name}";
        ToolTip.SetTip(PlacementScope, PlacementScope.Text);
        PlacementBindingId.Text = $"绑定编号 {snapshot.BindingId}";
        PlacementOriginal.Text = $"原班 · {snapshot.FromClass.TermName} / {snapshot.FromClass.Name}（{snapshot.FromClass.Code}）";
        PlacementTerm.Text = snapshot.Term.Name;
        Filter();
        Render();
    }

    private void SearchChanged(object? sender, TextChangedEventArgs args)
    {
        if (PlacementCandidates is not null && _client is not null) Filter();
    }

    private void SearchKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || PlacementCandidates.ItemCount == 0) return;
        args.Handled = true;
        if (Selected is null) PlacementCandidates.SelectedIndex = 0;
        if (Selected is not { } room) return;
        PlacementCandidates.ScrollIntoView(room);
        Dispatcher.UIThread.Post(() =>
        {
            if (!_closed && ReferenceEquals(Selected, room) && PlacementCandidates.ContainerFromItem(room) is ListBoxItem item)
                item.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private void ClassSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (_filling || _client is null) return;
        _selectedId = Selected?.Id;
        Render();
    }

    private void Filter()
    {
        string query = PlacementSearch.Text?.Trim() ?? "";
        var shown = _check?.Candidates.Where(row => row.Display.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
        _filling = true;
        PlacementCandidates.ItemsSource = shown;
        PlacementCandidates.SelectedItem = shown.FirstOrDefault(row => row.Id == _selectedId);
        _filling = false;
        PlacementNoCandidates.IsVisible = shown.Length == 0;
        PlacementNoCandidates.Text = query.Length > 0 ? "没有匹配的班级。" : "本学期暂无可安排的启用行政班。";
        Render();
    }

    private async void PrimaryClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy) return;
        if (Pending is { } pending)
        {
            if (_canRetry) await RunAsync(() => SaveAsync(pending));
            return;
        }
        if (_placement is { BindingConfirmed: true } placed && _check?.Term.Id == _termId)
        {
            Destination = new(placed.TargetClass.Id, _bindingId, _termId, placed.Status == "AWAITING_PAIRING");
            Close();
            return;
        }
        if (_check is not {CanArrange: true} check || Selected is not { } target) return;
        var operation = new Operation(check, target, Guid.NewGuid());
        Requests.GetOrCreateValue(_client)[(_school.Id, _termId, _bindingId)] = operation;
        await RunAsync(() => SaveAsync(operation));
    }

    private async Task SaveAsync(Operation operation)
    {
        _canRetry = false;
        _writing = true;
        Notice("正在保存本次安排并核对回执…");
        Render();
        TermScreenPlacementResult result;
        try { result = await _client.SaveTermScreenPlacementAsync(operation.Check, operation.Candidate, operation.RequestId, _lifetime.Token); }
        catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or >= 500 || error.Code == "TERM_SCREEN_PLACEMENT_REQUEST_CONFLICT")
        {
            if (!_closed) await VerifyAsync(operation);
            return;
        }
        catch (ConsoleApiException error) when (error.Code == "TERM_SCREEN_PLACEMENT_CHANGED")
        {
            if (_closed) return;
            ClearPending();
            Notice(error.Message, true);
            await ReadAsync(keepNotice: true);
            if (!_closed) Focus(_check?.CanArrange == true ? PlacementSearch : PlacementRefresh);
            return;
        }
        catch (ConsoleApiException)
        {
            if (!_closed) ClearPending();
            throw;
        }
        if (!_closed) ShowResult(result, operation);
    }

    private async Task VerifyAsync(Operation operation)
    {
        _canRetry = false;
        Render();
        var result = await _client.GetTermScreenPlacementReceiptAsync(operation.Check, operation.Candidate, operation.RequestId, _lifetime.Token);
        if (_closed) return;
        if (result is not null) { ShowResult(result, operation); return; }
        _canRetry = true;
        Notice("服务器暂未找到本次回执。可继续只读核对，或按原编号重试本次安排。", true);
        Focus(PlacementRefresh);
    }

    private void ShowResult(TermScreenPlacementResult result, Operation operation)
    {
        ClearPending();
        _placement = result.Placement;
        if (_check is { } original) _check = original with {CanArrange = false};
        Notice($"已核对本次安排回执。确认目标为 {operation.Candidate.Display}。");
        Render();
        Focus(PlacementPrimary);
    }

    private void ClearPending() => Requests.GetOrCreateValue(_client).Remove((_school.Id, _termId, _bindingId));

    private async void RefreshClicked(object? sender, RoutedEventArgs args) => await RunAsync(
        () => Pending is { } pending ? VerifyAsync(pending) : ReadAsync());
    private void BackClicked(object? sender, RoutedEventArgs args) => Close();

    private async Task RunAsync(Func<Task> operation)
    {
        if (_busy || _closed) return;
        _busy = true;
        Render();
        try { await operation(); }
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error)
        {
            if (_closed) return;
            if (error.StatusCode is 401 or 403)
            {
                ClearPending();
                _check = null;
                _placement = null;
                _selectedId = null;
                PlacementCandidates.ItemsSource = null;
                PlacementScope.Text = "学校范围待核对";
                ToolTip.SetTip(PlacementScope, null);
                PlacementBindingId.Text = null;
                PlacementOriginal.Text = null;
            }
            Notice(Pending is null ? error.Message : $"安排回执待核对。{error.Message}", true);
            if (error.StatusCode == 401) SessionExpired?.Invoke(error);
        }
        finally
        {
            _writing = _busy = false;
            if (!_closed) Render();
        }
    }

    private void Render()
    {
        if (_client is null) return;
        bool waiting = Pending is not null;
        var selected = Selected;
        var pendingTarget = waiting ? Pending?.Candidate : null;
        PlacementEditor.IsVisible = _check?.CanArrange == true || waiting;
        PlacementEditor.IsEnabled = !_busy && !waiting;
        PlacementResultCard.IsVisible = _placement is not null;
        PlacementTargetSummary.Text = selected is null ? "选中目标班级后可核对该班已有大屏。" : selected.Display;
        PlacementOtherScreens.Text = selected is null ? null : selected.Screens.Length == 0 ? "目标班暂无已启用大屏。" :
            "已有大屏：" + string.Join("、", selected.Screens.Select(screen => screen.Name));
        PlacementChosen.IsVisible = (pendingTarget ?? selected) is not null && PlacementEditor.IsVisible;
        PlacementChosen.Text = (pendingTarget ?? selected) is { } target ? $"迁入 · {target.Display}" : null;
        PlacementResultTitle.Text = _placement?.StatusText;
        PlacementResultTarget.Text = _placement?.TargetClass.Display;
        PlacementResultEvidence.Text = _placement?.Evidence;
        PlacementPrimary.IsVisible = waiting ? _canRetry : _check?.CanArrange == true || _placement is {BindingConfirmed: true};
        PlacementPrimary.IsEnabled = !_busy && (waiting ? _canRetry : selected is not null && _check?.CanArrange == true ||
            _placement is {BindingConfirmed: true});
        PlacementPrimary.Content = waiting ? "按原编号重试" : _placement is {BindingConfirmed: true} placed ?
            placed.Status == "AWAITING_PAIRING" ? "打开现场接入" : "查看这台大屏" :
            selected is { } candidate ? $"确认安排到 {candidate.Name}" : "确认安排";
        PlacementRefresh.Content = waiting ? "核对安排回执" : "刷新";
        PlacementRefresh.IsEnabled = !_busy;
        PlacementBack.Content = waiting ? "关闭，稍后核对" : "返回";
        PlacementBack.IsEnabled = !_writing;
    }

    private void Notice(string? content, bool error = false)
    {
        PlacementNotice.Text = content;
        PlacementNotice.IsVisible = !string.IsNullOrWhiteSpace(content);
        PlacementNotice.Classes.Set("error", error);
    }

    private void Focus(Control target) => Dispatcher.UIThread.Post(() =>
    {
        if (!_closed && target.IsEffectivelyVisible && target.IsEffectivelyEnabled)
        {
            target.BringIntoView();
            target.Focus(NavigationMethod.Tab);
        }
    }, DispatcherPriority.Loaded);
}
