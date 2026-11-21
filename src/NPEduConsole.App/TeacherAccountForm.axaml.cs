using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class TeacherAccountForm : Window
{
    private sealed record Intent(Guid RequestId, TeacherAccountDirectory Baseline, string Username, string Name);
    private static readonly ConditionalWeakTable<ConsoleClient, Dictionary<(string School, string Target), Intent>> PendingBySession = new();
    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly string? _resetId;
    private readonly string _createLabel = "创建教师";
    private readonly CancellationTokenSource _lifetime = new();
    private TeacherAccountDirectory? _directory;
    private Guid? _abandonedRequest, _missingReceipt;
    private bool _closed, _busy, _writing, _retryAllowed, _accessLost;

    public TeacherAccountForm() => InitializeComponent();

    internal TeacherAccountForm(ConsoleClient client, School school, string? resetId = null, string? createLabel = null) : this()
    {
        _client = client;
        _school = school;
        _resetId = resetId;
        if (createLabel is not null) _createLabel = createLabel;
        TeacherFormPin.CopyingToClipboard += (_, args) => args.Handled = true;
        TeacherFormPin.CuttingToClipboard += (_, args) => args.Handled = true;
        // Esc 只关本窗；写入中被 Closing 的守卫拦下，不会把未写完的请求留在后台。
        AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            Close();
        }, RoutingStrategies.Tunnel);
        TeacherFormScope.Text = school.Name;
        TeacherFormHeading.Text = resetId is null ? "新建教师账号" : "重置教师 PIN";
        Opened += async (_, _) =>
        {
            BatchWorkspaceWindow.FitSchoolWindow(this);
            await RunAsync(LoadAsync);
        };
        Closing += (_, args) => args.Cancel = _writing && !_closed;
        Closed += (_, _) =>
        {
            _closed = true;
            TeacherFormPin.Clear();
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
    }

    internal TeacherAccountReceipt? Receipt { get; private set; }
    internal Action<ConsoleApiException>? SessionExpired { get; init; }
    private string Action => _resetId is null ? "CREATE" : "RESET_PIN";
    private (string, string) OperationKey => (_school.Id, _resetId ?? "");
    private Dictionary<(string, string), Intent> Operations => PendingBySession.GetOrCreateValue(_client);
    private Intent? Pending => Operations.GetValueOrDefault(OperationKey);

    private async Task LoadAsync()
    {
        Status("正在核对学校登录方式…");
        var directory = await _client.GetTeacherAccountsAsync(_school.Id, _lifetime.Token);
        if (_closed) return;
        UseDirectory(directory);
        _accessLost = false;
        if (Pending is { } pending)
        {
            TeacherFormName.Text = pending.Name;
            TeacherFormUsername.Text = pending.Username;
            await VerifyAsync(pending);
            return;
        }
        if (directory.Mode == "OAUTH_EMAIL") Status("本校使用 OAuth 邮箱登录。请在原网页按邮箱分配教师。");
        else if (_resetId is not null && !directory.UsesPin) Status("本校采用学校通用口令。教师无需个人 PIN。");
        else if (_resetId is not null && directory.Accounts.All(account => account.Id != _resetId))
            Status("该教师已不在本校普通账号目录，请返回核对。", error: true);
        else
        {
            Status(null);
            Focus(_resetId is null ? TeacherFormName : TeacherFormPin);
        }
    }

    private void UseDirectory(TeacherAccountDirectory directory)
    {
        _directory = directory;
        if (!directory.UsesPin) TeacherFormPin.Clear();
        string mode = directory.Mode switch { "LOCAL_PIN" => "个人 PIN", "SHARED_PASSWORD" => "学校通用口令", _ => "OAuth 邮箱" };
        var account = directory.Accounts.FirstOrDefault(account => account.Id == _resetId);
        TeacherFormScope.Text = $"{_school.Name} · {mode}" + (account is null ? "" : $" · {account.Display}");
        TeacherFormExplanation.Text = Explain(directory) + AbandonedNote();
    }

    private string Explain(TeacherAccountDirectory directory)
    {
        if (_resetId is not null)
            return directory.UsesPin
                ? "重置后，教师的现有会话会退出。请让教师用新 PIN 重新登录。任课和岗位不受影响。"
                : "本校教师用学校通用口令或 OAuth 邮箱登录，没有个人 PIN 可重置。";
        return directory.UsesPin
            ? "创建普通学校成员。任课和岗位在相应页面另行保存。"
            : "教师使用学校通用口令登录。任课和岗位在相应页面另行保存。";
    }

    private string AbandonedNote() => _abandonedRequest is { } abandoned
        ? $"\n已放弃的请求编号：{abandoned:D}。若服务器此前完成了该请求，目录中会显示对应账号，请按目录核对。"
        : "";

    private void Render()
    {
        bool modeAllows = _resetId is null ? _directory?.SupportsLocalWrites == true :
            _directory?.UsesPin == true && _directory.Accounts.Any(account => account.Id == _resetId);
        var pending = Pending;
        bool allowWork = !_accessLost;
        TeacherFormCard.IsVisible = allowWork && (modeAllows || pending is not null);
        TeacherIdentityFields.IsVisible = _resetId is null;
        TeacherIdentityFields.IsEnabled = !_busy && pending is null;
        TeacherPinField.IsVisible = allowWork && _directory?.UsesPin == true && (pending is null || _retryAllowed);
        TeacherPinField.IsEnabled = !_busy && modeAllows;
        TeacherFormWeb.IsVisible = allowWork && _directory?.Mode == "OAUTH_EMAIL";
        TeacherFormCheck.IsVisible = allowWork && (pending is not null || _directory is null);
        TeacherFormCheck.Content = pending is null ? "重新读取" : "核对回执";
        TeacherFormCheck.Classes.Set("accent", pending is not null && !_retryAllowed);
        TeacherFormCheck.IsEnabled = !_busy;
        TeacherFormAbandon.IsVisible = allowWork && pending is not null && _missingReceipt == pending.RequestId && !_retryAllowed;
        TeacherFormAbandon.IsEnabled = !_busy;
        TeacherFormSave.IsVisible = allowWork && modeAllows && (pending is null || _retryAllowed);
        TeacherFormSave.IsEnabled = !_busy;
        TeacherFormSave.Content = _retryAllowed ? "按原请求重试" : _resetId is null ? _createLabel : "确认重置 PIN";
        TeacherFormClose.IsEnabled = !_writing;
    }

    private async void SaveClicked(object? sender, RoutedEventArgs args)
    {
        if (_busy || !TeacherFormSave.IsEffectivelyVisible || _directory is not { } directory) return;
        if (Pending is not null && !_retryAllowed) return;
        TeacherFormNameError.IsVisible = TeacherFormUsernameError.IsVisible = TeacherFormPinError.IsVisible = false;
        var intent = Pending;
        string name = intent?.Name ?? (TeacherFormName.Text ?? "").Trim();
        string username = intent?.Username ?? (TeacherFormUsername.Text ?? "").Trim().ToLowerInvariant();
        if (_resetId is null && (name.Length is < 1 or > 64))
        { FieldError(TeacherFormNameError, TeacherFormName, "姓名需为 1 至 64 个字符。"); return; }
        if (_resetId is null && !Regex.IsMatch(username, @"\A[a-z0-9][a-z0-9._-]{1,31}\z"))
        { FieldError(TeacherFormUsernameError, TeacherFormUsername, "短账号为 2 至 32 位，以字母或数字开头，可用点、横线、下划线。"); return; }
        string? pin = directory.UsesPin ? TeacherFormPin.Text : null;
        if (directory.UsesPin && (pin is null || !Regex.IsMatch(pin, @"\A[0-9]{4,8}\z")))
        { FieldError(TeacherFormPinError, TeacherFormPin, "个人 PIN 需为 4 至 8 位数字。"); return; }
        TeacherFormPin.Clear();
        bool wasPending = intent is not null;
        intent ??= new(Guid.NewGuid(), directory, username, name);
        Operations[OperationKey] = intent;
        _abandonedRequest = null;
        await RunAsync(async () =>
        {
            _retryAllowed = false;
            _missingReceipt = null;
            _writing = true;
            Render();
            Status(_resetId is null ? "正在创建教师账号…" : "正在重置教师 PIN…");
            TeacherAccountReceipt result;
            try
            {
                result = _resetId is null
                    ? await _client.CreateTeacherAccountAsync(intent.Baseline, intent.Username, intent.Name, pin, intent.RequestId, _lifetime.Token)
                    : await _client.UpdateTeacherAccountAsync(intent.Baseline, _resetId, Action, pin, intent.RequestId, _lifetime.Token);
            }
            catch (ConsoleApiException error) when (Uncertain(error))
            {
                pin = null;
                await VerifyAsync(intent);
                return;
            }
            catch (ConsoleApiException error)
            {
                if (_closed) return;
                if (wasPending && error.Code is ("TEACHER_MODE_CHANGED" or "TEACHER_VERSION_CHANGED" or "TEACHER_ACCOUNT_NOT_FOUND" or "TEACHER_USERNAME_EXISTS"))
                {
                    await VerifyAsync(intent);
                    return;
                }
                Operations.Remove(OperationKey);
                if (error.Code == "TEACHER_USERNAME_EXISTS")
                {
                    Status(null);
                    FieldError(TeacherFormUsernameError, TeacherFormUsername, error.Message);
                    return;
                }
                if (error.Code is "TEACHER_MODE_CHANGED" or "TEACHER_VERSION_CHANGED" or "TEACHER_ACCOUNT_NOT_FOUND")
                {
                    var observed = await _client.GetTeacherAccountsAsync(_school.Id, _lifetime.Token);
                    if (!_closed) UseDirectory(observed);
                }
                throw;
            }
            finally { pin = null; }
            if (!_closed) Confirm(intent, result);
        });
    }

    private async Task VerifyAsync(Intent intent)
    {
        _retryAllowed = false;
        _missingReceipt = null;
        Status("正在按原请求编号只读核对回执…");
        var result = await _client.GetTeacherAccountReceiptAsync(_school.Id, intent.RequestId, Action, _lifetime.Token);
        if (_closed) return;
        if (result is not null) { Confirm(intent, result); return; }
        var current = await _client.GetTeacherAccountsAsync(_school.Id, _lifetime.Token);
        if (_closed) return;
        UseDirectory(current);
        _missingReceipt = intent.RequestId;
        bool duplicate = _resetId is null && current.Accounts.Any(account => account.Username == intent.Username);
        _retryAllowed = !duplicate && current.Mode == intent.Baseline.Mode && (_resetId is null ||
            current.Accounts.SingleOrDefault(account => account.Id == _resetId)?.Version ==
            intent.Baseline.Accounts.Single(account => account.Id == _resetId).Version);
        string observation = duplicate ? "目录中已有同码教师，该目录项无法证明本次创建完成。" : "";
        Status("暂未找到本次操作的回执。" + observation + (_retryAllowed ?
            (current.UsesPin ? "可再次核对，或重新输入原 PIN，用原请求编号重试。" : "可再次核对，或用原请求编号重试。") :
            "请核对目录当前值。可继续核对回执，或放弃核对后返回。"));
        Focus(_retryAllowed ? TeacherFormSave : TeacherFormAbandon);
    }

    private void Confirm(Intent intent, TeacherAccountReceipt receipt)
    {
        if (_resetId is null ? receipt.Account.Username != intent.Username : receipt.Account.Id != _resetId)
            throw new ConsoleApiException(0, "回执与原账号不一致，请重新核对目录。");
        Operations.Remove(OperationKey);
        Receipt = receipt;
        _writing = false;
        Close();
    }

    private async void CheckClicked(object? sender, RoutedEventArgs args) =>
        await RunAsync(() => Pending is { } intent ? VerifyAsync(intent) : LoadAsync());

    /// <summary>
    /// 放弃待核对状态：只丢弃本地请求编号，不判定成功。放弃后以目录当前值为准，并把编号留在说明里供人工核对。
    /// </summary>
    private async void AbandonClicked(object? sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        if (Pending is not { } intent || _missingReceipt != intent.RequestId || _retryAllowed) return;
        var result = await _client.GetTeacherAccountReceiptAsync(_school.Id, intent.RequestId, Action, _lifetime.Token);
        if (_closed) return;
        if (result is not null) { Confirm(intent, result); return; }
        var directory = await _client.GetTeacherAccountsAsync(_school.Id, _lifetime.Token);
        if (_closed) return;
        Operations.Remove(OperationKey);
        _missingReceipt = null;
        _abandonedRequest = intent.RequestId;
        UseDirectory(directory);
        Status("已放弃本次核对。可用同一目录查看服务器当前账号。");
        Focus(_resetId is null && directory.SupportsLocalWrites ? TeacherFormName : TeacherFormClose);
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
            if (error.StatusCode is 401 or 403) _directory = null;
            if (error.StatusCode == 403) _accessLost = true;
            if (error.Code == "SNAPSHOT_CHANGED") _directory = null;
            Status(Pending is null ? error.Message : $"操作回执待核对。{error.Message}", error: true);
            if (error.StatusCode == 401) SessionExpired?.Invoke(error);
        }
        finally { _busy = _writing = false; if (!_closed) Render(); }
    }

    private void Status(string? value, bool error = false)
    {
        TeacherFormNoticeBox.IsVisible = !string.IsNullOrEmpty(value);
        TeacherFormNotice.Text = value;
        TeacherFormNotice.Classes.Set("error", error);
    }

    private void FieldError(TextBlock label, Control input, string message)
    {
        label.IsVisible = true;
        label.Text = message;
        Focus(input);
    }

    private void Focus(Control control) => Dispatcher.UIThread.Post(() =>
    {
        if (!_closed && control.IsEffectivelyVisible && control.IsEffectivelyEnabled) control.Focus();
    }, DispatcherPriority.Loaded);

    private void OpenWebClicked(object? sender, RoutedEventArgs args)
    {
        if (SchoolAdminBrowser.Link(_directory?.AdminUrl, _school.Id, _directory?.ActiveTermId, "teachers") is { } link && SchoolAdminBrowser.Open(link)) return;
        Status("请在原网页的「教师分配」中按邮箱办理。");
    }

    private void CloseClicked(object? sender, RoutedEventArgs args) => Close();
    internal void CloseForScopeChange() { _closed = true; Close(); }
    internal static bool Uncertain(ConsoleApiException error) => error.StatusCode is 0 or 408 or >= 500;
}
