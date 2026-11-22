using System.Runtime.CompilerServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed record ManagementAccountRow(SchoolManagementAccount Account, string Display, string Label);

public sealed partial class SchoolManagementAccountsWindow : Window
{
    private sealed record RoleChoice(string Key, string Name) { public override string ToString() => Name; }
    private sealed record PendingAccountOperation(Guid Id, SchoolManagementCommand Command, SchoolManagementPreview Preview,
        string ActorId, bool CheckedMissing = false);
    private sealed record PinDelivery(string AccountId, string Pin);
    private static readonly ConditionalWeakTable<ConsoleClient, Dictionary<string, PendingAccountOperation>> Operations = new();
    private static readonly RoleChoice[] Roles = [new("OWNER","学校所有者"),new("ADMIN","学校管理员"),new("MANAGER","学校 MANAGER"),new("VIEWER","普通学校成员")];
    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly CancellationTokenSource _lifetime = new();
    private SchoolManagementDirectory? _directory;
    private SchoolManagementCommand? _command;
    private SchoolManagementPreview? _preview;
    private Control? _returnFocus;
    private PinDelivery? _pinDelivery;
    private string? _selectedId, _duplicate;
    private bool _busy, _writing, _closed, _binding, _detail, _create, _ownPin;
    private bool _matchesOwner;

    public SchoolManagementAccountsWindow() => InitializeComponent();

    internal SchoolManagementAccountsWindow(ConsoleClient client, School school, string? accountId) : this()
    {
        _client = client;
        _school = school;
        _selectedId = accountId;
        _detail = accountId is not null;
        ManagementScope.Text = school.Name + " · 全校账号";
        ManagementFilter.ItemsSource = new[] {"管理账号","全部账号","待接任登录"};
        ManagementFilter.SelectedIndex = accountId is null ? 0 : 1;
        ManagementNewRole.ItemsSource = Roles.Take(2).Reverse().ToArray();
        ManagementNewRole.SelectedIndex = 0;
        ManagementRole.ItemsSource = Roles;
        ManagementImpact.BackRequested += LeavePreview;
        ManagementImpact.ConfirmRequested += () => _ = SubmitAsync();
        SizeChanged += (_, _) => LayoutPanes();
        Opened += async (_, _) =>
        {
            BatchWorkspaceWindow.FitSchoolWindow(this);
            await RunAsync(async () =>
            {
                if (Pending is { } pending) await RecoverAsync(pending);
                else await LoadAsync();
            });
            if (!_closed) FocusControl(accountId is null ? ManagementSearch : ManagementBackToList);
        };
        Closing += (_, args) => args.Cancel = _writing && !_closed;
        Closed += (_, _) =>
        {
            _closed = true;
            ClearSecrets();
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
        Render();
    }

    internal bool RequiresSignIn { get; private set; }
    internal bool Changed { get; private set; }
    internal Func<FilePickerSaveOptions, Task<IStorageFile?>>? SelectRosterFile { private get; set; }
    internal void CloseForScopeChange() { _writing = false; Close(); }
    private Dictionary<string, PendingAccountOperation> PendingBySchool => Operations.GetOrCreateValue(_client);
    private PendingAccountOperation? Pending => PendingBySchool.GetValueOrDefault(_school.Id);
    private SchoolManagementAccount? Current => _directory?.Accounts.SingleOrDefault(account => account.Id == _selectedId);

    private async Task LoadAsync()
    {
        var directory = await _client.GetSchoolManagementAccountsAsync(_school.Id, _lifetime.Token);
        if (_closed) return;
        _directory = directory;
        _matchesOwner = directory.ActorRole == "OWNER";
        ManagementScope.Text = $"{_school.Name} · 当前会话为{RoleText(directory.ActorRole)}";
        ManagementRole.SelectedItem = Roles.FirstOrDefault(choice => choice.Key == Current?.Role) ?? Roles[^1];
        Filter();
    }

    private void FilterChanged(object? sender, TextChangedEventArgs e) => Filter();
    private void AccountFilterChanged(object? sender, SelectionChangedEventArgs e) => Filter();
    private void Filter()
    {
        if (_client is null) return;
        var query = ManagementSearch.Text?.Trim() ?? "";
        var rows = _directory?.Accounts.Where(account =>
            (ManagementFilter.SelectedIndex == 1 || (ManagementFilter.SelectedIndex == 2 ? account.AwaitsManagementLogin : account.IsManagement)) &&
            account.Display.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(account => new ManagementAccountRow(account, account.Display, AccountLabel(account))).ToArray() ?? [];
        _binding = true;
        ManagementRows.ItemsSource = rows;
        ManagementRows.SelectedItem = rows.FirstOrDefault(row => row.Account.Id == _selectedId);
        _binding = false;
        Render();
    }

    private void AccountChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_binding || ManagementRows.SelectedItem is not ManagementAccountRow row) return;
        if (_selectedId != row.Account.Id) _pinDelivery = null;
        _selectedId = row.Account.Id;
        _create = _ownPin = false;
        ClearInputPins();
        ManagementRole.SelectedItem = Roles.FirstOrDefault(choice => choice.Key == row.Account.Role) ?? Roles[^1];
        Render();
    }

    private void OpenDetails(object? sender, RoutedEventArgs e) => EnterDetail();
    private void AccountListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ManagementRows.SelectedItem is not null) { e.Handled = true; EnterDetail(); }
    }

    private void EnterDetail()
    {
        if (ManagementRows.SelectedItem is not ManagementAccountRow row) return;
        _selectedId = row.Account.Id;
        _detail = true;
        Render();
        FocusControl(ManagementBackToList.IsEffectivelyVisible ? ManagementBackToList : ManagementRole);
    }

    private void BackToList(object? sender, RoutedEventArgs e)
    {
        _detail = _create = _ownPin = false;
        ClearInputPins();
        Render();
        FocusControl(ManagementRows, selectedRow:true);
    }

    private void LayoutPanes()
    {
        bool narrow = ClientSize.Width < 900;
        bool detail = _detail && (Current is not null || _create);
        ManagementPanes.ColumnDefinitions = new(narrow ? "*" : "*,1.2*");
        ManagementListPane.IsVisible = !narrow || !detail;
        ManagementDetailPane.IsVisible = !narrow || detail;
        Grid.SetColumn(ManagementDetailPane, narrow ? 0 : 1);
        ManagementBackToList.IsVisible = narrow && detail;
        ManagementOpenDetail.IsVisible = narrow && !detail && ManagementRows.SelectedItem is not null;
    }

    private static string RoleText(string? value) => Roles.FirstOrDefault(role => role.Key == value)?.Name ?? "教学空间成员";
    private static string TimeLabel(DateTimeOffset value) => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    private static string AccountLabel(SchoolManagementAccount account) => $"{RoleText(account.Role)} · {(account.Source == "LOCAL" ? "本地" : "OAuth")} · " +
        (account.Disabled ? "已停用" : account.AwaitsManagementLogin ? "待管理登录" : "可登录");

    private void Render()
    {
        if (_client is null || _closed) return;
        var account = Current;
        bool editing = !_busy && Pending is null && _directory is not null;
        bool self = account?.Id == _directory?.ActorId;
        bool otherCanEdit = account is not null && !self && (_matchesOwner || !account.IsManagement);
        bool details = account is not null && !_create;
        ManagementAccountTitle.Text = _create ? "填写新账号身份" : account?.Display ??
            (_selectedId is not null ? "该账号已不在当前名册" : "选择账号核对角色和登录状态");
        ManagementAccountState.Text = details ? AccountLabel(account!) : "";
        ManagementAccountLogin.Text = details ? account!.LastLoginAt is { } last ? $"最近登录 {TimeLabel(last)}" : "暂无登录记录" : "";
        ManagementAccountAuthority.Text = !details ? "" : account!.Disabled ? "该账号已停止本地登录。" :
            account.AwaitsManagementLogin ? account.Source == "LOCAL"
                ? "待接任者使用个人 PIN 重新登录。授予前的原会话不会获得新管理权。"
                : "待接任者在旧网页重新完成 OAuth 登录。原会话的其他学校权限继续有效。"
            : account.ManagementLoginAt is { } verified ? $"已核对{(account.ManagementLoginMethod == "OAUTH" ? "网页 OAuth" : "本地 PIN")}登录 · {TimeLabel(verified)}"
            : account.IsManagement ? "现有管理角色。交接时请确认接任者的登录证据。" : "";
        ManagementRoleFields.IsVisible = details && _matchesOwner;
        ManagementToggle.IsVisible = details && otherCanEdit && account!.Source == "LOCAL";
        ManagementToggle.Content = account?.Disabled == true ? "恢复本地登录" : "停用本地登录";
        ManagementResetPin.IsVisible = ManagementToggle.IsVisible;
        ManagementRemove.IsVisible = details && otherCanEdit;
        ManagementOwnPin.IsVisible = details && self && account!.Source == "LOCAL";
        ManagementCreateFields.IsVisible = _create;
        ManagementOwnPinFields.IsVisible = _ownPin && ManagementOwnPin.IsVisible;
        ManagementPanes.IsVisible = _preview is null && Pending is null;
        ManagementPanes.IsEnabled = editing;
        ManagementConfirmation.IsVisible = _preview is not null;
        ManagementConfirmation.IsEnabled = !_busy;
        ManagementPinFields.IsVisible = _preview is {NeedsPin: true,CanConfirm:true};
        ManagementCreate.IsVisible = _matchesOwner && _preview is null;
        ManagementCreate.IsEnabled = editing;
        ManagementRefresh.IsEnabled = !_busy && Pending is null && _preview is null;
        ManagementExportRoster.IsVisible = _preview is null && Pending is null && _directory is not null;
        ManagementExportRoster.IsEnabled = editing;
        ManagementReceipt.IsVisible = Pending is not null;
        ManagementReceipt.IsEnabled = !_busy;
        ManagementRetryPane.IsVisible = Pending is { } && _preview is null;
        ManagementRetryPane.IsEnabled = !_busy;
        ManagementRetry.IsVisible = Pending is {CheckedMissing:true};
        ManagementRetryPin.IsVisible = Pending is {CheckedMissing:true, Preview.NeedsPin:true};
        ManagementRetryTarget.Text = Pending is { } pending ? pending.Preview.Consequences[0] : "";
        ManagementRetryDescription.Text = Pending switch
        {
            {CheckedMissing:true, Preview.NeedsPin:true} => "暂未查到回执。若按原编号重试，请重新输入 PIN。只有服务器明确确认本次重试实际写入时，才能交付这次输入的 PIN。",
            {CheckedMissing:true} => "暂未查到回执。可按原编号重试同一项操作，服务器会重新核对原预览。",
            _ => "请先按原编号核对回执。服务器确认结果前，账号状态可能已经变化。",
        };
        ManagementLocate.IsVisible = _duplicate is not null && _preview is null;
        ManagementLocate.IsEnabled = editing;
        ManagementSavePin.IsVisible = ManagementClearPin.IsVisible = _pinDelivery is not null && _preview is null;
        ManagementSavePin.IsEnabled = ManagementClearPin.IsEnabled = !_busy;
        ManagementClose.IsEnabled = !_writing;
        LayoutPanes();
    }

    private void OpenCreate(object? sender, RoutedEventArgs e)
    {
        _create = _detail = true;
        _ownPin = false;
        ClearSecrets();
        Render();
        FocusControl(ManagementNewName);
    }

    private void OpenOwnPin(object? sender, RoutedEventArgs e)
    {
        _ownPin = true;
        Render();
        FocusControl(ManagementPriorPin);
    }

    private async void SaveOwnPin(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        try
        {
            await _client.ChangeOwnSchoolPinAsync(ManagementPriorPin.Text ?? "", ManagementNewOwnPin.Text ?? "", _lifetime.Token);
            if (_closed) return;
            RequiresSignIn = true;
        }
        finally { ClearInputPins(); }
        Close();
    });

    private async void PreviewCreate(object? sender, RoutedEventArgs e) =>
        await BeginPreview(new("CREATE", Username:ManagementNewUsername.Text ?? "", Name:ManagementNewName.Text ?? "",
            Role:(ManagementNewRole.SelectedItem as RoleChoice)?.Key), ManagementCreatePreview);
    private async void PreviewRole(object? sender, RoutedEventArgs e)
    {
        if (Current is { } account)
            await BeginPreview(new("ROLE", account.Id, Role:(ManagementRole.SelectedItem as RoleChoice)?.Key), ManagementRolePreview);
    }
    private async void PreviewToggle(object? sender, RoutedEventArgs e)
    {
        if (Current is { } account) await BeginPreview(new(account.Disabled ? "ENABLE" : "DISABLE", account.Id), ManagementToggle);
    }
    private async void PreviewReset(object? sender, RoutedEventArgs e)
    {
        if (Current is { } account) await BeginPreview(new("RESET_PIN", account.Id), ManagementResetPin);
    }
    private async void PreviewRemove(object? sender, RoutedEventArgs e)
    {
        if (Current is { } account) await BeginPreview(new("REMOVE", account.Id), ManagementRemove);
    }

    private Task BeginPreview(SchoolManagementCommand command, Control origin) => RunAsync(async () =>
    {
        if (_directory is not { } directory || Pending is not null) return;
        _duplicate = null;
        _pinDelivery = null;
        ClearInputPins();
        ManagementStatus.Text = "正在核对服务器上的当前角色及影响…";
        var preview = await _client.PreviewSchoolManagementAccountAsync(directory, command, _lifetime.Token);
        if (_closed) return;
        _command = command;
        _preview = preview;
        _returnFocus = origin;
        ManagementStatus.Text = preview.CanConfirm ? "" : "请先满足交接条件。";
        Render();
        ManagementImpact.Present(preview.Title, _school.Name, preview.Consequences, ConfirmLabel(command.Action), preview.CanConfirm);
    });

    private static string ConfirmLabel(string action) => action switch
    {
        "CREATE" => "确认新建", "ROLE" => "确认角色变更", "RESET_PIN" => "确认重置",
        "DISABLE" => "确认停用", "ENABLE" => "确认恢复", _ => "确认注销本校权限"
    };

    private void LeavePreview()
    {
        if (_busy) return;
        ClearInputPins();
        _preview = null;
        _command = null;
        ManagementStatus.Text = "";
        Render();
        if (_returnFocus is { } origin) FocusControl(origin);
    }

    private Task SubmitAsync() => RunAsync(async () =>
    {
        if (_directory is not { } directory || _command is not { } command ||
            _preview is not {CanConfirm:true} preview || Pending is not null) return;
        if (!ReadPin(ManagementPin, preview.NeedsPin, out var pin)) return;
        var pending = new PendingAccountOperation(Guid.NewGuid(), command, preview, directory.ActorId);
        await SavePendingAsync(directory, pending, pin, retry: false);
    });

    private bool ReadPin(TextBox entry, bool needsPin, out string? pin)
    {
        pin = needsPin ? entry.Text : null;
        if (!needsPin || pin is {Length: >= 4 and <= 8} && pin.All(char.IsAsciiDigit)) return true;
        ManagementStatus.Text = "请输入 4 至 8 位数字 PIN。";
        FocusControl(entry);
        return false;
    }

    private async Task SavePendingAsync(SchoolManagementDirectory directory, PendingAccountOperation pending, string? pin, bool retry)
    {
        pending = pending with {CheckedMissing = false};
        PendingBySchool[_school.Id] = pending;
        _writing = true;
        ClearInputPins();
        Render();
        ManagementStatus.Text = "正在保存本次操作…";
        try
        {
            SchoolManagementReceipt result;
            try
            {
                result = await _client.SaveSchoolManagementAccountAsync(directory, pending.Command, pending.Preview,
                    pending.Id, pin, _lifetime.Token);
            }
            catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or >= 500)
            {
                await RecoverAsync(pending, retry ? null : pin);
                return;
            }
            catch (ConsoleApiException)
            {
                if (retry)
                {
                    await RecoverAsync(pending);
                    if (Pending is null || _closed) return;
                }
                PendingBySchool.Remove(_school.Id);
                _preview = null;
                _command = null;
                throw;
            }
            if (!_closed) await FinishAsync(pending, result, result.Replayed ? null : pin);
        }
        finally { _writing = false; }
    }

    private void ShowUnconfirmed()
    {
        _preview = null;
        _command = null;
        ManagementStatus.Text = Pending is {CheckedMissing:true} ? "保存结果待核对" : "保存回执待核对。请重新读取原请求回执。";
    }

    private async Task RecoverAsync(PendingAccountOperation operation, string? originalPin = null)
    {
        var result = await _client.GetSchoolManagementReceiptAsync(_school.Id, operation.Command, operation.Id, _lifetime.Token);
        if (_closed) return;
        if (result is not null) await FinishAsync(operation, result, originalPin);
        else
        {
            PendingBySchool[_school.Id] = operation with {CheckedMissing = true};
            ShowUnconfirmed();
        }
    }

    private async Task FinishAsync(PendingAccountOperation pending, SchoolManagementReceipt result, string? pin)
    {
        PendingBySchool.Remove(_school.Id);
        bool self = result.AccountId == pending.ActorId;
        Changed = true;
        _preview = null;
        _command = null;
        _create = false;
        _selectedId = result.AccountId;
        _detail = true;
        _pinDelivery = pin is null ? null : new(result.AccountId, pin);
        ManagementNewName.Text = ManagementNewUsername.Text = "";
        ManagementStatus.Text = pending.Preview.NeedsPin && pin is null
            ? "服务器已确认原操作。无法确认这次输入的 PIN 已生效，请勿交付。需要新 PIN 时请另行重置。"
            : "服务器已确认本次操作。";
        if (self && result.Action == "ROLE" && result.Role is not ("OWNER" or "ADMIN"))
        {
            _directory = null;
            _matchesOwner = false;
            RequiresSignIn = true;
            Filter();
            ManagementStatus.Text = "服务器已确认交接。此账号的本校管理和照片访问已结束。";
            return;
        }
        await LoadAsync();
    }

    private async void RefreshClicked(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        ManagementStatus.Text = "正在读取学校名册…";
        await LoadAsync();
        if (!_closed) ManagementStatus.Text = _directory?.Accounts.Length is > 0 ? "" : "暂无可显示的本校账号。";
    });
    private async void ReadReceiptClicked(object? sender, RoutedEventArgs e)
    {
        if (Pending is { } pending) await RunAsync(() => RecoverAsync(pending));
    }
    private async void RetryClicked(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Pending is not {CheckedMissing:true} pending ||
            !ReadPin(ManagementRetryPin, pending.Preview.NeedsPin, out var pin)) return;
        ClearInputPins();
        PendingBySchool[_school.Id] = pending with {CheckedMissing = false};
        await RecoverAsync(pending);
        if (_closed || Pending is null) return;
        if (_directory is null) await LoadAsync();
        if (!_closed && _directory is { } directory) await SavePendingAsync(directory, pending, pin, retry: true);
    });
    private async void LocateDuplicate(object? sender, RoutedEventArgs e)
    {
        if (_duplicate is not { } id) return;
        await RunAsync(async () =>
        {
            _selectedId = id;
            ClearSecrets();
            _detail = true;
            _create = false;
            _duplicate = null;
            ManagementFilter.SelectedIndex = 1;
            ManagementSearch.Text = "";
            await LoadAsync();
            if (!_closed && Current is { } account)
                ManagementRole.SelectedItem = Roles.FirstOrDefault(role => role.Key == account.Role) ?? Roles[^1];
        });
    }

    private async void ExportRosterClicked(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var selectFile = SelectRosterFile ?? StorageProvider.SaveFilePickerAsync;
        using var file = await selectFile(new FilePickerSaveOptions
        {
            Title = "导出全校无密账号名册", SuggestedFileName = "全校账号名册.csv", DefaultExtension = "csv",
            FileTypeChoices = [new("CSV 账号名册") {Patterns = ["*.csv"]}]
        });
        _lifetime.Token.ThrowIfCancellationRequested();
        if (file is null) return;
        await LoadAsync();
        if (_closed || _directory is not { } directory) return;
        await using var stream = await file.OpenWriteAsync();
        if (stream.CanSeek) stream.SetLength(0);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync("学校,账号ID,姓名,短账号,邮箱,身份来源,学校角色,登录状态,最近登录,当前学期的直接教学空间（名称／代码／角色）".AsMemory(), _lifetime.Token);
        foreach (var account in directory.Accounts)
        {
            var workspaces = string.Join("；", (account.CurrentWorkspaces ?? []).Select(space => $"{space.Name}／{space.Code}／{space.Role}"));
            string?[] cells = [_school.Name, account.Id, account.Name, account.Username, account.Email,
                account.Source == "LOCAL" ? "本地" : "OAuth", RoleText(account.Role),
                account.Disabled ? "已停用" : account.AwaitsManagementLogin ? "待管理登录" : "可登录",
                account.LastLoginAt?.ToString("O"), workspaces];
            await writer.WriteLineAsync(string.Join(',', cells.Select(CsvCell)).AsMemory(), _lifetime.Token);
        }
        ManagementStatus.Text = "已导出全校名册。当前列表筛选不影响导出范围。";
    });

    private static string CsvCell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        var first = value.AsSpan().TrimStart();
        if (first.Length > 0 && first[0] is '=' or '+' or '-' or '@' || value[0] is '\t' or '\r' or '\n') value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private async void ExportPin(object? sender, RoutedEventArgs e)
    {
        if (_pinDelivery is not { } delivery || _directory?.Accounts.SingleOrDefault(row => row.Id == delivery.AccountId)?.Username is not { } username) return;
        await RunAsync(async () =>
        {
            using var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {Title = "另存本次交付的账号与 PIN", SuggestedFileName = $"账号交付-{username}.txt", DefaultExtension = "txt"});
            _lifetime.Token.ThrowIfCancellationRequested();
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek) stream.SetLength(0);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync($"{_school.Name}\r\n短账号 {username}\r\n个人 PIN {delivery.Pin}".AsMemory(), _lifetime.Token);
            _pinDelivery = null;
            ManagementStatus.Text = "已另存本次凭据，窗口中的临时 PIN 已清除。";
        });
    }

    private void DiscardPin(object? sender, RoutedEventArgs e) { ClearSecrets(); Render(); }
    private void CloseClicked(object? sender, RoutedEventArgs e) => Close();
    private void ClearSecrets() { ClearInputPins(); _pinDelivery = null; }
    private void ClearInputPins() => ManagementPin.Text = ManagementRetryPin.Text = ManagementPriorPin.Text = ManagementNewOwnPin.Text = "";

    private async Task RunAsync(Func<Task> operation)
    {
        if (_busy || _closed) return;
        _busy = true;
        Render();
        try { await operation(); }
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error) when (!_closed)
        {
            if (error.StatusCode == 401 && error.Code != "CURRENT_PIN_INCORRECT")
            { RequiresSignIn = true; ClearSecrets(); Close(); return; }
            if (error.Code == "SCHOOL_ADMIN_REQUIRED") { _directory = null; _matchesOwner = false; Filter(); ClearSecrets(); }
            if (Pending is not null) ShowUnconfirmed();
            _duplicate = error.RelatedAccountId;
            ManagementStatus.Text = (Pending is null ? "" : "本次回执仍待核对。") + error.Message;
        }
        catch (Exception error) when (!_closed && error is ArgumentException or IOException or UnauthorizedAccessException)
        { ManagementStatus.Text = error is ArgumentException ? error.Message : "无法保存到所选位置。"; }
        finally { _busy = false; Render(); }
    }

    private void FocusControl(Control control, bool selectedRow = false) => Dispatcher.UIThread.Post(() =>
    {
        if (_closed || !control.IsEffectivelyVisible || !control.IsEffectivelyEnabled) return;
        if (selectedRow && ManagementRows.SelectedItem is { } row)
        {
            ManagementRows.ScrollIntoView(row);
            if (ManagementRows.ContainerFromItem(row) is ListBoxItem item) { item.Focus(); return; }
        }
        control.Focus();
    }, DispatcherPriority.Loaded);
}
