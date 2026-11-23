using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class SchoolOpeningView : UserControl
{
    internal sealed record Position(OpeningStage Stage, string Search, string? Key, int Index, bool Detail, string? TermId);

    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly Uri? _setupWebsite;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<OpeningStage, Position> _positions = new();
    private SchoolOpeningSnapshot? _snapshot;
    private CancellationTokenSource? _read;
    private OpeningStage _stage;
    private string? _lastTermId, _websiteSuggestion;
    private bool _bound, _detail, _addresses, _navigating, _loading, _closed;

    public SchoolOpeningView() => InitializeComponent();

    internal SchoolOpeningView(ConsoleClient client, School school, Uri? setupWebsite, Position? position = null) : this()
    {
        _client = client;
        _school = school;
        _setupWebsite = setupWebsite;
        _stage = position?.Stage ?? OpeningStage.Organization;
        if (position is not null) _positions[position.Stage] = position;
        _detail = position?.Detail ?? false;
        _lastTermId = position?.TermId;
        OpeningSearch.Text = position?.Search ?? "";
        SizeChanged += (_, _) => RenderLayout();
        AddHandler(KeyDownEvent, ViewKeyDown, RoutingStrategies.Tunnel);
        UpdateAddresses(null);
    }

    internal event Action? ReturnRequested;
    internal event Action<ConsoleApiException>? AccessLost;
    internal Func<OpeningTarget, SchoolOverview, CancellationToken, Task>? OpenTarget { get; init; }

    private bool Compact => Bounds.Width is > 0 and < 860;
    private SchoolOpeningRow? Selected => OpeningRows.SelectedItem as SchoolOpeningRow;

    internal Position RememberPosition() =>
        new(_stage, OpeningSearch.Text ?? "", Selected?.Key, OpeningRows.SelectedIndex, _detail, _lastTermId);

    internal async Task StartAsync()
    {
        var position = _positions.GetValueOrDefault(_stage);
        await RefreshAsync(position, restoreFocus: position is not null);
        if (!_closed && position is null) OpeningRefresh.Focus(NavigationMethod.Tab);
    }

    internal void End()
    {
        if (_closed) return;
        _closed = true;
        _lifetime.Cancel();
        _read?.Cancel();
        _snapshot = null;
        _positions.Clear();
        OpeningWebsite.Clear();
        OpeningRows.ItemsSource = null;
        _lifetime.Dispose();
    }

    private async Task RefreshAsync(Position? position, bool restoreFocus = false)
    {
        if (_closed) return;
        _read?.Cancel();
        using var request = _read = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _loading = true;
        _snapshot = null;
        Status("正在读取学校当前的开学配置…");
        Render(position);
        try
        {
            var overview = await _client.GetSchoolOverviewAsync(_school.Id, request.Token);
            TeacherAllocationCatalog? teachers = null;
            ScreenDutyCatalog? screens = null;
            if (overview.Term is { } term)
            {
                var staff = ReadOptionalAsync(() => _client.GetTeacherAllocationAsync(_school.Id, term.Id, null, false, request.Token));
                var devices = ReadOptionalAsync(() => _client.GetScreenDutyAsync(_school.Id, null, request.Token));
                await Task.WhenAll(staff, devices);
                teachers = staff.Result;
                screens = devices.Result;
                if (teachers is not null && !teachers.Terms.Any(row => row.Id == term.Id && row.Status == "ACTIVE") ||
                    screens is not null && screens.TermId != term.Id)
                    throw new ConsoleApiException(409, "读取期间启用学期发生变化，请重新核对。");
            }
            if (_closed || _read != request) return;
            var changedTerm = _lastTermId is not null && _lastTermId != overview.Term?.Id;
            _lastTermId = overview.Term?.Id;
            if (changedTerm)
            {
                _positions.Clear();
                OpeningSearch.Clear();
                position = null;
                _detail = false;
            }
            _snapshot = new(overview, teachers, screens);
            UpdateAddresses(overview.AdminUrl);
            Status(changedTerm ? "启用学期已变化，已重新读取当前学期。" : null);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (ConsoleApiException error)
        {
            if (_closed || _read != request) return;
            if (error.StatusCode is 401 or 403) AccessLost?.Invoke(error);
            else Status($"开学配置待核对。{error.Message}");
        }
        finally
        {
            if (!_closed && _read == request)
            {
                _read = null;
                _loading = false;
                Render(position);
                if (restoreFocus) RestoreFocus();
            }
        }
    }

    private static async Task<T?> ReadOptionalAsync<T>(Func<Task<T>> read) where T : class
    {
        try { return await read(); }
        catch (ConsoleApiException error) when (error.StatusCode is not (401 or 403)) { return null; }
    }

    private void Render(Position? anchor = null)
    {
        OpeningScope.Text = $"{_school.Name} · " + (_snapshot is null ? "启用学期待核对" :
            _snapshot.Overview.Term is { } term ? $"当前启用学期 {term.Name}" : "暂无启用学期");
        OpeningSummary.Text = _snapshot?.Summary(_stage);
        OpeningOrganization.Classes.Set("accent", _stage == OpeningStage.Organization);
        OpeningTeachers.Classes.Set("accent", _stage == OpeningStage.Teachers);
        OpeningClassrooms.Classes.Set("accent", _stage == OpeningStage.Classrooms);
        RenderRows(anchor);
        RenderActions();
        RenderLayout();
    }

    private void RenderRows(Position? anchor)
    {
        var query = OpeningSearch.Text?.Trim() ?? "";
        var rows = _snapshot?.Rows(_stage).Where(row => query.Length == 0 ||
            row.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || row.Summary.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            row.Key.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
        _bound = true;
        OpeningRows.ItemsSource = rows;
        OpeningRows.SelectedItem = rows.FirstOrDefault(row => row.Key == anchor?.Key) ??
            (rows.Length == 0 ? null : rows[Math.Clamp(anchor?.Index ?? 0, 0, rows.Length - 1)]);
        _bound = false;
        OpeningEmpty.IsVisible = rows.Length == 0;
        OpeningEmpty.Text = _snapshot is null ? "等待读取学校状态。" : query.Length != 0 ? "找不到匹配的事项。" :
            _stage == OpeningStage.Organization && _snapshot.Overview.Term is not null
                ? "当前班级与走班来源诊断没有待办。班级、学科和授课规则可在组织导入核对。"
                : "当前没有可显示的事项。";
        RenderDetail();
    }

    private void RenderActions()
    {
        var available = !_loading && !_navigating && _snapshot is not null;
        var hasTerm = _snapshot?.Overview.Term is not null;
        OpeningRefresh.IsEnabled = !_loading && !_navigating;
        OpeningPrimary.IsVisible = _stage != OpeningStage.Classrooms || !hasTerm;
        OpeningPrimary.Content = _stage switch
        {
            OpeningStage.Organization when hasTerm => "开学组织导入",
            OpeningStage.Teachers => "教师账号",
            _ => "学期管理",
        };
        OpeningPrimary.IsEnabled = available;
        OpeningSecondary.IsVisible = hasTerm && _stage != OpeningStage.Classrooms;
        OpeningSecondary.Content = _stage == OpeningStage.Organization ? "开班" : "批量分配教师访问";
        OpeningSecondary.IsEnabled = available;
        OpeningStaffConfiguration.IsVisible = _stage == OpeningStage.Teachers;
        OpeningStaffConfiguration.IsEnabled = available;
        OpeningRows.IsEnabled = !_loading && !_navigating;
        OpeningHandleRow.IsEnabled = available && Selected?.Target is not null;
    }

    private void RenderDetail()
    {
        OpeningDetailTitle.Text = _addresses ? "核对网页与 KV 地址" : Selected?.Title ?? "选择一项查看详情";
        OpeningEvidence.Text = Selected?.Evidence;
        OpeningEvidence.IsVisible = !_addresses && Selected is not null;
        OpeningAddressCard.IsVisible = _addresses;
        OpeningHandleRow.Content = Selected?.ActionLabel;
        OpeningHandleRow.IsVisible = !_addresses && Selected?.Target is not null;
        OpeningDetailsScroll.Offset = default;
        RenderActions();
        RenderLayout();
    }

    private void RenderLayout()
    {
        OpeningPanes.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        OpeningPanes.ColumnDefinitions[1].Width = Compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        OpeningPanes.ColumnSpacing = Compact ? 0 : 8;
        Grid.SetColumn(OpeningDetailPane, Compact ? 0 : 1);
        OpeningListPane.IsVisible = !Compact || !_detail;
        OpeningDetailPane.IsVisible = !Compact || _detail;
        OpeningBackToList.IsVisible = Compact && _detail || _addresses;
        OpeningBackToList.Content = Compact ? "返回事项列表" : "返回所选事项";
        OpeningShowDetail.IsVisible = Compact && !_detail && Selected is not null;
    }

    private void Switch(OpeningStage next)
    {
        if (_stage == next || _navigating) return;
        _positions[_stage] = RememberPosition();
        _stage = next;
        var position = _positions.GetValueOrDefault(next);
        _bound = true;
        OpeningSearch.Text = position?.Search ?? "";
        _bound = false;
        _detail = false;
        _addresses = false;
        Render(position);
    }

    private void UpdateAddresses(string? adminUrl)
    {
        var declared = SchoolAdminBrowser.Link(adminUrl, _school.Id, null, "structure");
        var website = _setupWebsite ?? (declared is null ? null : new Uri(declared.GetLeftPart(UriPartial.Authority)));
        var value = website?.AbsoluteUri.TrimEnd('/');
        if (string.IsNullOrEmpty(OpeningWebsite.Text) || OpeningWebsite.Text == _websiteSuggestion)
            OpeningWebsite.Text = value;
        _websiteSuggestion = value;
        var source = _setupWebsite is not null ? $"首次配置时所选网页 {_setupWebsite.AbsoluteUri.TrimEnd('/')}" :
            website is not null ? $"KV 提供的师生网页站点 {value}" : "师生网页地址尚待核对";
        OpeningAddress.Text = $"KV 地址（教室端配对使用）{_client.ServerUri.AbsoluteUri.TrimEnd('/')}\n{source}。师生使用网页，请在浏览器核对实际连接。";
    }

    private async Task FollowAsync(OpeningTarget target)
    {
        if (_navigating || _loading || _closed || _snapshot is not { } scope || OpenTarget is not { } open) return;
        var position = RememberPosition();
        string? navigationError = null;
        _navigating = true;
        RenderActions();
        try { await open(target, scope.Overview, _lifetime.Token); }
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error)
        {
            if (_closed) return;
            if (error.StatusCode is 401 or 403) AccessLost?.Invoke(error);
            else navigationError = error.Message;
        }
        finally
        {
            _navigating = false;
            if (!_closed && IsEffectivelyVisible) await RefreshAsync(position, restoreFocus: true);
            if (!_closed && _snapshot is not null && navigationError is not null) Status(navigationError);
        }
    }

    private void RestoreFocus() => Dispatcher.UIThread.Post(() =>
    {
        if (_closed || !IsEffectivelyVisible) return;
        if (Compact && _detail)
        {
            (OpeningHandleRow.IsEffectivelyVisible ? OpeningHandleRow : OpeningBackToList).Focus(NavigationMethod.Tab);
        }
        else if (Selected is { } row)
        {
            OpeningRows.ScrollIntoView(row);
            Dispatcher.UIThread.Post(() =>
            {
                if (!_closed && OpeningRows.IsEffectivelyVisible && Selected == row)
                    ((OpeningRows.ContainerFromItem(row) as Control) ?? OpeningRows).Focus(NavigationMethod.Tab);
            }, DispatcherPriority.Background);
        }
        else OpeningSearch.Focus(NavigationMethod.Tab);
    }, DispatcherPriority.Loaded);

    private void Status(string? message)
    {
        OpeningStatus.Text = message;
        OpeningStatus.IsVisible = !string.IsNullOrWhiteSpace(message);
    }

    private void ShowDetail()
    {
        _detail = true;
        _addresses = false;
        RenderDetail();
        RestoreFocus();
    }

    private void ToList()
    {
        _detail = false;
        _addresses = false;
        RenderDetail();
        RestoreFocus();
    }

    private void OrganizationClicked(object? sender, RoutedEventArgs args) => Switch(OpeningStage.Organization);
    private void TeachersClicked(object? sender, RoutedEventArgs args) => Switch(OpeningStage.Teachers);
    private void ClassroomsClicked(object? sender, RoutedEventArgs args) => Switch(OpeningStage.Classrooms);
    private void BackClicked(object? sender, RoutedEventArgs args) => ReturnRequested?.Invoke();
    private void ShowDetailClicked(object? sender, RoutedEventArgs args) => ShowDetail();
    private void BackToListClicked(object? sender, RoutedEventArgs args) => ToList();
    private async void RefreshClicked(object? sender, RoutedEventArgs args) => await RefreshAsync(RememberPosition());
    private async void HandleClicked(object? sender, RoutedEventArgs args)
    {
        if (Selected?.Target is { } target) await FollowAsync(target);
    }
    private async void PrimaryClicked(object? sender, RoutedEventArgs args) => await FollowAsync(new(_stage switch
    {
        OpeningStage.Organization when _snapshot?.Overview.Term is not null => OpeningAction.Import,
        OpeningStage.Teachers => OpeningAction.Accounts,
        _ => OpeningAction.Terms,
    }));
    private async void SecondaryClicked(object? sender, RoutedEventArgs args) =>
        await FollowAsync(_stage == OpeningStage.Teachers ? new(OpeningAction.Access) : new(OpeningAction.Class,
            CreateGrade: _snapshot?.Overview.Counts.Grades == 0));
    private async void StaffConfigurationClicked(object? sender, RoutedEventArgs args) => await FollowAsync(new(OpeningAction.StaffConfiguration));

    private void SearchChanged(object? sender, TextChangedEventArgs args)
    {
        if (!_bound && _client is not null) RenderRows(RememberPosition());
    }

    private void SelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_bound || _client is null) return;
        _addresses = false;
        RenderDetail();
    }

    private void AddressesClicked(object? sender, RoutedEventArgs args)
    {
        _detail = true;
        _addresses = true;
        RenderDetail();
        Dispatcher.UIThread.Post(() => OpeningWebsite.Focus(NavigationMethod.Tab), DispatcherPriority.Loaded);
    }

    private void BrowserCheckClicked(object? sender, RoutedEventArgs args)
    {
        try
        {
            var site = SchoolSetupDeployment.NormalizeWebsite(OpeningWebsite.Text ?? "");
            var check = SchoolSetupDeployment.BrowserCheckUrl(site, _client.ServerUri);
            Status(SchoolAdminBrowser.Open(check) ? "请在浏览器核对当前网页的实际连接。" : "无法打开系统浏览器。");
        }
        catch (ConsoleApiException error) { Status(error.Message); }
    }

    private void ListTapped(object? sender, TappedEventArgs args)
    {
        if (!Compact || args.Source is not Visual visual ||
            visual.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault() is not { } item ||
            OpeningRows.ItemFromContainer(item) is not SchoolOpeningRow row) return;
        OpeningRows.SelectedItem = row;
        args.Handled = true;
        ShowDetail();
    }

    private void ListKeyDown(object? sender, KeyEventArgs args)
    {
        if (!Compact || args.Key != Key.Enter || Selected is null) return;
        args.Handled = true;
        ShowDetail();
    }

    private void ViewKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape || !_addresses && (!Compact || !_detail)) return;
        args.Handled = true;
        ToList();
    }
}
