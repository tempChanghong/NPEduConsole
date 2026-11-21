using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

internal sealed record TeacherAccountDestination(string Kind, string TermId, string TargetId, string? SubjectId, string AccountId);

public sealed partial class TeacherAccountsWindow : Window
{
    private sealed record StatusIntent(string AccountId, string Action, Guid RequestId);
    private static readonly ConditionalWeakTable<ConsoleClient, Dictionary<string, StatusIntent>> PendingBySession = new();
    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly CancellationTokenSource _lifetime = new();
    private TeacherAccountDirectory? _directory;
    private TeacherAccountForm? _form;
    private string? _selectedId;
    private string? _confirmation;
    private Vector _listOffset;
    private bool _busy, _writing, _closed, _showDetails, _binding;

    public TeacherAccountsWindow() => InitializeComponent();

    internal TeacherAccountsWindow(ConsoleClient client, School school, string? targetId) : this()
    {
        _client = client;
        _school = school;
        _selectedId = targetId;
        _showDetails = targetId is not null;
        TeacherAccountsScope.Text = school.Name;
        SizeChanged += (_, _) => LayoutPanes();
        Opened += async (_, _) =>
        {
            BatchWorkspaceWindow.FitSchoolWindow(this);
            await RunAsync(async () =>
            {
                if (Pending is { } pending) await ReadReceiptAsync(pending);
                else await LoadAsync();
            });
            Focus(targetId is null ? TeacherAccountSearch : BackToTeacherList);
        };
        Closing += (_, args) => args.Cancel = _writing && !_closed;
        Closed += (_, _) =>
        {
            _closed = true;
            _form?.CloseForScopeChange();
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
        Render();
    }

    internal Action<ConsoleApiException>? SessionExpired { get; init; }
    internal Func<Window, Task<bool>>? OpenBulkAllocation { get; init; }

    private async void OpenBulkClicked(object? sender, RoutedEventArgs args)
    {
        if (OpenBulkAllocation is not { } open) return;
        await RunAsync(async () => { if (await open(this) && !_closed) await LoadAsync(); });
        if (!_closed) TeacherAccountBulk.Focus();
    }
    internal TeacherAccountDestination? Destination { get; private set; }
    private Dictionary<string, StatusIntent> Operations => PendingBySession.GetOrCreateValue(_client);
    private StatusIntent? Pending => Operations.GetValueOrDefault(_school.Id);
    private ManagedTeacherAccount? Current => _directory?.Accounts.SingleOrDefault(account => account.Id == _selectedId);

    private async Task LoadAsync()
    {
        var directory = await _client.GetTeacherAccountsAsync(_school.Id, _lifetime.Token);
        if (_closed) return;
        _confirmation = null;
        _directory = directory;
        TeacherAccountsScope.Text = $"{_school.Name} · 全校共用 · " + (directory.Mode switch
        {
            "LOCAL_PIN" => "个人 PIN", "SHARED_PASSWORD" => "学校通用口令", _ => "OAuth 邮箱"
        });
        Filter();
    }

    private void Filter()
    {
        if (TeacherAccountRows is null || _client is null) return;
        string query = (TeacherAccountSearch.Text ?? "").Trim();
        var rows = _directory?.Accounts.Where(account => account.Display.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
        _binding = true;
        TeacherAccountRows.ItemsSource = rows;
        TeacherAccountRows.SelectedItem = rows.FirstOrDefault(account => account.Id == _selectedId);
        _binding = false;
        TeacherListMessage.Text = _directory?.Mode == "OAUTH_EMAIL" ? "本校通过 OAuth 邮箱登录，请在网页教师分配中办理。" :
            rows.Length > 0 ? null : _directory is null ? "暂无可显示的本校目录。" : query.Length > 0 ? "没有匹配的账号。" : "本校暂无普通本地教师账号。";
        TeacherListMessage.IsVisible = TeacherListMessage.Text is not null;
        ShowDetails();
    }

    private void LayoutPanes()
    {
        bool narrow = ClientSize.Width < 900;
        bool details = _showDetails && Current is not null;
        TeacherAccountsWorkspace.ColumnDefinitions = new(narrow ? "*" : "1*,1.2*");
        TeacherListPane.IsVisible = !narrow || !details;
        TeacherDetailsPane.IsVisible = !narrow || details;
        Grid.SetColumn(TeacherDetailsPane, narrow ? 0 : 1);
        TeacherDetailsPane.Margin = narrow ? new Thickness(0) : new Thickness(10, 0, 0, 0);
        BackToTeacherList.IsVisible = narrow && details;
        OpenTeacherDetails.IsVisible = narrow && TeacherAccountRows.SelectedItem is ManagedTeacherAccount;
        OpenTeacherDetails.IsEnabled = !_busy;
    }

    private void ShowDetails()
    {
        var account = Current;
        TeacherDetailName.Text = account?.Display ?? (_selectedId is null ? "选择一个教师账号" : "该账号暂不在本校目录");
        TeacherDetailData.IsVisible = account is not null;
        TeacherDetailState.Text = account?.StateLabel;
        TeacherDetailLogin.Text = account is null ? null : account.LastLoginAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "从未登录";
        TeacherDetailAccess.Text = account?.Disabled == true ? "教师当前无法登录。恢复后可用原账号重新登录，仍保留原任课和岗位。" :
            account?.LastLoginAt is null ? "请让教师用学校代码和短账号登录。首次登录后，学校总览会更新提醒。" : null;
        TeacherDetailAccess.IsVisible = TeacherDetailAccess.Text is not null;
        TeacherDetailAssignments.Children.Clear();
        TeacherDetailDuties.Children.Clear();
        if (account is not null && _directory is { } directory)
        {
            foreach (var row in account.Assignments)
                AddReference(TeacherDetailAssignments, row.Label, row.WorkspaceType == "ADMIN_CLASS"
                    ? new("teaching", row.TermId, row.WorkspaceId, row.SubjectId, account.Id) : null);
            if (directory.ActiveTermId is { } term)
                foreach (var row in account.Duties)
                    AddReference(TeacherDetailDuties, row.Label, new(row.Kind, term, row.TargetId, null, account.Id));
            foreach (var panel in new[] { TeacherDetailAssignments, TeacherDetailDuties })
                if (panel.Children.Count == 0) AddReference(panel, directory.ActiveTermId is null ? "当前无启用学期。" : "暂无安排。", null);
        }
        Render();
    }

    private void AddReference(Panel panel, string label, TeacherAccountDestination? destination)
    {
        if (destination is null)
        {
            panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } });
            return;
        }
        var button = new Button
        {
            Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, Classes = { "action" },
            IsEnabled = !_busy && Pending is null
        };
        button.Click += (_, _) =>
        {
            if (_busy || Pending is not null || Current?.Id != destination.AccountId) return;
            Destination = destination;
            Close();
        };
        panel.Children.Add(button);
    }

    private void Render()
    {
        var account = Current;
        bool writable = !_busy && _directory?.SupportsLocalWrites == true && Pending is null;
        NewTeacherAccountButton.IsVisible = _directory?.SupportsLocalWrites == true;
        NewTeacherAccountButton.IsEnabled = writable;
        TeacherAccountWeb.IsVisible = _directory?.Mode == "OAUTH_EMAIL";
        TeacherAccountBulk.IsEnabled = !_busy && _directory is not null && OpenBulkAllocation is not null;
        TeacherAccountRefresh.IsEnabled = !_busy;
        TeacherAccountRefresh.Content = Pending is null ? "刷新目录" : "核对操作回执";
        TeacherAccountRefresh.Classes.Set("accent", Pending is not null);
        TeacherAccountRows.IsEnabled = TeacherAccountSearch.IsEnabled = !_busy;
        CloseTeacherAccountsButton.IsEnabled = !_writing;
        TeacherDetailActions.IsVisible = account is not null && _confirmation is null;
        ChangeTeacherStatus.IsVisible = _directory?.SupportsLocalWrites == true;
        ChangeTeacherStatus.IsEnabled = ConfirmTeacherStatus.IsEnabled = writable && account is not null;
        ChangeTeacherStatus.Content = account?.Disabled == true ? "恢复账号" : "停用账号";
        ResetTeacherPin.IsVisible = _directory?.UsesPin == true;
        ResetTeacherPin.IsEnabled = writable && account is not null;
        TeacherStatusConfirmation.IsVisible = account is not null && _confirmation is not null;
        LayoutPanes();
    }

    private void SearchChanged(object? sender, TextChangedEventArgs args)
    {
        string query = (TeacherAccountSearch.Text ?? "").Trim();
        if (Current is { } account && !account.Display.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            _selectedId = _confirmation = null;
            _showDetails = false;
        }
        Filter();
    }

    private void Selected(object? sender, SelectionChangedEventArgs args)
    {
        if (_binding || TeacherAccountRows.SelectedItem is not ManagedTeacherAccount account) return;
        _selectedId = account.Id;
        _confirmation = null;
        ShowDetails();
    }

    private void RowsKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || TeacherAccountRows.SelectedItem is not ManagedTeacherAccount) return;
        args.Handled = true;
        OpenDetailsClicked(sender, args);
    }

    private void OpenDetailsClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy || TeacherAccountRows.SelectedItem is not ManagedTeacherAccount) return;
        RememberListPosition();
        _showDetails = true;
        Render();
        if (BackToTeacherList.IsVisible) Focus(BackToTeacherList);
    }

    private void RememberListPosition()
    {
        if (TeacherAccountRows.Scroll is ScrollViewer scroll) _listOffset = scroll.Offset;
    }

    private void BackClicked(object? sender, RoutedEventArgs args)
    {
        _confirmation = null;
        _showDetails = false;
        Render();
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed || !TeacherListPane.IsEffectivelyVisible) return;
            if (TeacherAccountRows.Scroll is ScrollViewer scroll) scroll.Offset = _listOffset;
            if (TeacherAccountRows.SelectedItem is { } account && TeacherAccountRows.ContainerFromItem(account) is ListBoxItem row) row.Focus();
            else TeacherAccountSearch.Focus();
        }, DispatcherPriority.Loaded);
    }

    private async void CreateClicked(object? sender, RoutedEventArgs args) => await ShowFormAsync(null);
    private async void ResetClicked(object? sender, RoutedEventArgs args)
    {
        if (ResetTeacherPin.IsEffectivelyEnabled && Current is { } account) await ShowFormAsync(account.Id);
    }

    private async Task ShowFormAsync(string? resetId) => await RunAsync(async () =>
    {
        var form = new TeacherAccountForm(_client, _school, resetId) { SessionExpired = SessionExpired };
        _form = form;
        try { await form.ShowDialog(this); }
        finally { _form = null; }
        if (_closed || form.Receipt is not { } receipt) return;
        if (resetId is null) TeacherAccountSearch.Clear();
        _showDetails = true;
        await RefreshAfterConfirmedAsync(receipt, resetId is null ? $"已创建 {receipt.Account.Display}。" : "PIN 已重置，教师需重新登录。");
    });

    private void ChangeStatusClicked(object? sender, RoutedEventArgs args)
    {
        if (Current is not { } account || !ChangeTeacherStatus.IsEnabled) return;
        _confirmation = account.Disabled ? "RESTORE" : "DISABLE";
        ConfirmTeacherStatus.Content = account.Disabled ? "确认恢复" : "确认停用";
        TeacherStatusExplanation.Text = account.Disabled
            ? $"恢复 {account.Display} 后，教师可用原账号重新登录。"
            : $"停用 {account.Display}？现有会话会退出，任课和岗位仍保留。";
        Render();
        Focus(ConfirmTeacherStatus);
    }

    private void CancelStatusClicked(object? sender, RoutedEventArgs args)
    {
        _confirmation = null;
        Render();
        Focus(ChangeTeacherStatus);
    }

    private async void ConfirmStatusClicked(object? sender, RoutedEventArgs args)
    {
        if (_confirmation is not { } action || Current is not { } account || _directory is not { } directory || !ConfirmTeacherStatus.IsEnabled) return;
        await RunAsync(async () =>
        {
            _writing = true;
            _confirmation = null;
            var pending = new StatusIntent(account.Id, action, Guid.NewGuid());
            Operations[_school.Id] = pending;
            Render();
            TeacherAccountReceipt result;
            try { result = await _client.UpdateTeacherAccountAsync(directory, account.Id, action, null, pending.RequestId, _lifetime.Token); }
            catch (ConsoleApiException error) when (TeacherAccountForm.Uncertain(error))
            {
                await ReadReceiptAsync(pending);
                return;
            }
            catch (ConsoleApiException error)
            {
                Operations.Remove(_school.Id);
                if (error.StatusCode == 409) await LoadAsync();
                throw;
            }
            await ApplyReceiptAsync(pending, result);
        });
    }

    private async Task ReadReceiptAsync(StatusIntent pending)
    {
        Notice("正在按原请求编号只读核对操作结果…");
        var receipt = await _client.GetTeacherAccountReceiptAsync(_school.Id, pending.RequestId, pending.Action, _lifetime.Token);
        if (_closed) return;
        if (receipt is not null) { await ApplyReceiptAsync(pending, receipt); return; }
        await LoadAsync();
        if (_closed) return;
        Operations.Remove(_school.Id);
        var observed = _directory?.Accounts.SingleOrDefault(account => account.Id == pending.AccountId);
        Notice($"暂无本次操作回执。服务器当前{(observed is null ? "已无此目录项" : $"显示{observed.StateLabel}")}，可刷新复核后再决定。");
    }

    private async Task ApplyReceiptAsync(StatusIntent pending, TeacherAccountReceipt receipt)
    {
        if (_closed) return;
        if (receipt.Account.Id != pending.AccountId) throw new ConsoleApiException(0, "账号回执与原操作不一致，请重新核对。");
        Operations.Remove(_school.Id);
        await RefreshAfterConfirmedAsync(receipt, receipt.Account.Disabled
            ? "服务器确认账号已停用，任课和岗位保留。" : "服务器确认账号已启用，教师可以重新登录。");
    }

    private async Task RefreshAfterConfirmedAsync(TeacherAccountReceipt receipt, string confirmation)
    {
        _selectedId = receipt.Account.Id;
        try { await LoadAsync(); }
        catch (ConsoleApiException error) when (error.StatusCode is not (401 or 403))
        {
            if (_closed) return;
            if (_directory is { } earlier)
            {
                _directory = earlier with { Accounts = earlier.Accounts.Where(row => row.Id != receipt.Account.Id).Append(receipt.Account).ToArray() };
                Filter();
            }
            Notice(confirmation + "目录暂未刷新，请稍后重新读取。", error: true);
            return;
        }
        if (!_closed) Notice(confirmation);
    }

    private async void RefreshClicked(object? sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        if (Pending is { } pending) await ReadReceiptAsync(pending);
        else { await LoadAsync(); if (!_closed) Notice("目录已刷新。"); }
    });

    private async Task RunAsync(Func<Task> work)
    {
        if (_busy || _closed) return;
        _busy = true;
        Render();
        try { await work(); }
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error)
        {
            if (_closed) return;
            if (error.StatusCode is 401 or 403)
            {
                _directory = null;
                _selectedId = null;
                Filter();
            }
            Notice(Pending is null ? error.Message : $"操作回执待核对。{error.Message}", error: true);
            if (error.StatusCode == 401) SessionExpired?.Invoke(error);
        }
        finally
        {
            _busy = _writing = false;
            if (!_closed) ShowDetails();
        }
    }

    private void Notice(string message, bool error = false)
    {
        TeacherAccountsNotice.Text = message;
        TeacherAccountsNotice.IsVisible = true;
        TeacherAccountsNotice.Classes.Set("error", error);
    }

    private void Focus(Control target) => Dispatcher.UIThread.Post(() =>
    {
        if (!_closed && target.IsEffectivelyVisible && target.IsEffectivelyEnabled) target.Focus();
    }, DispatcherPriority.Loaded);

    private void OpenWebClicked(object? sender, RoutedEventArgs args)
    {
        if (SchoolAdminBrowser.Link(_directory?.AdminUrl, _school.Id, _directory?.ActiveTermId, "teachers") is { } link && SchoolAdminBrowser.Open(link)) return;
        Notice("请在原网页的「教师分配」中按邮箱办理。");
    }

    private void CloseClicked(object? sender, RoutedEventArgs args) => Close();
    internal void CloseForScopeChange() { _closed = true; _form?.CloseForScopeChange(); Close(); }
}
