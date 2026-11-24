using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class OwnerRecoveryView : UserControl
{
    internal sealed record LoginTarget(Uri Server, string SchoolCode, string Username, bool Confirmed)
    {
        internal string Notice => Confirmed ? "服务器已确认恢复。请用新 PIN 登录此账号。" :
            "恢复结果暂未确认。请在原服务器用刚设置的新 PIN 登录核对。";
    }

    private CancellationTokenSource? _read;
    private Uri? _verifiedServer;
    private OwnerRecoverySchool? _confirmedSchool;
    private LoginTarget? _result;
    private string? _checkedInput;
    private string _originalSchoolCode = "";
    private string _originalUsername = "";
    private bool _open, _writing;

    public OwnerRecoveryView()
    {
        InitializeComponent();
        foreach (var field in new[] { OwnerRecoverySetupKey, OwnerRecoveryNewPin, OwnerRecoveryRepeatPin })
        {
            field.CopyingToClipboard += (_, args) => args.Handled = true;
            field.CuttingToClipboard += (_, args) => args.Handled = true;
        }
    }

    internal event Action<LoginTarget?>? ReturnRequested;
    internal bool CanExit => !_writing;

    internal void Open(string address, string schoolCode, string username)
    {
        _originalSchoolCode = schoolCode.Trim();
        _originalUsername = username.Trim();
        OwnerRecoveryAddress.Text = address;
        _open = true;
        ResetTarget();
        Focus(OwnerRecoveryAddress);
    }

    internal void Deactivate()
    {
        _open = false;
        ResetTarget();
        OwnerRecoveryAddress.Clear();
        _originalSchoolCode = _originalUsername = "";
    }

    private void ResetTarget()
    {
        var read = _read;
        _read = null;
        read?.Cancel();
        _verifiedServer = null;
        _confirmedSchool = null;
        _result = null;
        _checkedInput = null;
        OwnerRecoveryAddressField.ErrorMessage = OwnerRecoveryUsernameField.ErrorMessage = null;
        OwnerRecoverySchool.ItemsSource = null;
        OwnerRecoverySchool.SelectedItem = null;
        OwnerRecoveryUsername.Clear();
        ClearSecrets();
        SetNotice(null);
        Render();
    }

    private void AddressChanged(object? sender, TextChangedEventArgs args)
    {
        if (_open && !_writing && _result is null && _checkedInput is { } input && input != ReadAddress()) ResetTarget();
    }

    private string ReadAddress() => OwnerRecoveryAddress.Text?.Trim() ?? "";

    private void SchoolChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_open && _verifiedServer is not null && _confirmedSchool is null) Render();
    }

    private async void CheckClicked(object? sender, RoutedEventArgs args)
    {
        if (!_open || _read is not null || _confirmedSchool is not null || _writing || _result is not null) return;
        ResetTarget();
        string input = ReadAddress();
        Uri server;
        try { server = ConsoleClient.NormalizeKvAddress(input); }
        catch (ConsoleApiException error)
        {
            OwnerRecoveryAddressField.ReportError(error.Message);
            return;
        }

        _checkedInput = input;
        using var request = _read = new CancellationTokenSource();
        SetNotice("正在读取此 KV 的公开学校目录…");
        Render();
        try
        {
            using var client = new ConsoleClient(server.AbsoluteUri);
            var schools = await client.GetOwnerRecoverySchoolsAsync(request.Token);
            if (_read != request || !_open || input != ReadAddress()) return;
            _verifiedServer = server;
            OwnerRecoverySchool.ItemsSource = schools;
            OwnerRecoverySchool.SelectedItem = schools.SingleOrDefault(school =>
                string.Equals(school.Code.Trim(), _originalSchoolCode, StringComparison.OrdinalIgnoreCase));
            SetNotice(schools.Count == 0 ? "此服务器未返回可选学校。请核对实际 KV 地址。" : null);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (ConsoleApiException error)
        {
            if (_read == request && _open) SetNotice($"无法核对学校。{error.Message}", error: true);
        }
        finally
        {
            if (_read == request)
            {
                _read = null;
                Render();
                if (OwnerRecoverySchoolField.IsVisible)
                    Focus(OwnerRecoverySchool.SelectedItem is null ? OwnerRecoverySchool : OwnerRecoveryConfirmTarget);
            }
        }
    }

    private void ConfirmTargetClicked(object? sender, RoutedEventArgs args)
    {
        if (!_open || _verifiedServer is null || _read is not null || _confirmedSchool is not null || _checkedInput != ReadAddress() ||
            OwnerRecoverySchool.SelectedItem is not OwnerRecoverySchool school) return;
        _confirmedSchool = school;
        OwnerRecoveryUsername.Text = string.Equals(school.Code.Trim(), _originalSchoolCode, StringComparison.OrdinalIgnoreCase)
            ? _originalUsername : "";
        SetNotice(null);
        Render();
        Focus(string.IsNullOrWhiteSpace(OwnerRecoveryUsername.Text) ? OwnerRecoveryUsername : OwnerRecoverySetupKey);
    }

    private void ChangeTargetClicked(object? sender, RoutedEventArgs args)
    {
        if (!_open || _writing || _result is not null) return;
        _confirmedSchool = null;
        OwnerRecoveryUsername.Clear();
        ClearSecrets();
        SetNotice(null);
        Render();
        Focus(OwnerRecoveryAddress);
    }

    private async void SaveClicked(object? sender, RoutedEventArgs args)
    {
        if (!_open || _writing || _result is not null || _verifiedServer is not { } server || _confirmedSchool is not { } school) return;
        var username = OwnerRecoveryUsername.Text?.Trim() ?? "";
        var key = OwnerRecoverySetupKey.Text ?? "";
        var pin = OwnerRecoveryNewPin.Text ?? "";
        if (username.Length == 0) { OwnerRecoveryUsernameField.ReportError("请输入所有者短账号。"); return; }
        if (string.IsNullOrWhiteSpace(key)) { OwnerRecoveryKeyField.ReportError("请输入此 KV 的服务器恢复密钥。"); return; }
        if (pin.Length is < 4 or > 8 || !pin.All(char.IsAsciiDigit))
        { OwnerRecoveryNewPinField.ReportError("新 PIN 需为 4 至 8 位数字。"); return; }
        if (!string.Equals(pin, OwnerRecoveryRepeatPin.Text, StringComparison.Ordinal))
        { OwnerRecoveryRepeatPinField.ReportError("两次输入的新 PIN 不一致。"); return; }

        _writing = true;
        ClearSecrets();
        SetNotice("正在提交恢复。请等待服务器回应…");
        Render();
        try
        {
            using var client = new ConsoleClient(server.AbsoluteUri);
            await client.RecoverLocalOwnerAsync(school.Code, username, key, pin, CancellationToken.None);
            _result = new(server, school.Code, username, Confirmed: true);
        }
        catch (ConsoleApiException error) when (error.Code == "OWNER_RECOVERY_UNCONFIRMED")
        {
            _result = new(server, school.Code, username, Confirmed: false);
        }
        catch (ConsoleApiException error) { SetNotice(error.Message, error: true); }
        finally
        {
            key = pin = "";
            _writing = false;
            Render();
        }
        if (_result is { } result)
        {
            SetNotice(result.Notice);
            Focus(OwnerRecoveryReturnToLogin);
        }
        else Focus(OwnerRecoverySetupKey);
    }

    private void Render()
    {
        bool targetConfirmed = _confirmedSchool is not null;
        bool hasResult = _result is not null;
        OwnerRecoveryEditor.IsVisible = !hasResult;
        Grid.SetRow(OwnerRecoveryActions, hasResult ? 1 : 2);
        OwnerRecoveryActions.VerticalAlignment = hasResult ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        OwnerRecoveryActions.BorderThickness = hasResult ? new Thickness(0) : new Thickness(0, 1, 0, 0);
        OwnerRecoveryActions.Padding = hasResult ? new Thickness(20, 16) : new Thickness(20, 9);
        OwnerRecoveryVerifiedAddress.IsVisible = _verifiedServer is not null;
        OwnerRecoveryVerifiedAddress.Text = _verifiedServer is { } server ? $"KV 服务 · {server.AbsoluteUri.TrimEnd('/')}" : null;
        OwnerRecoveryVerifiedSchool.IsVisible = targetConfirmed;
        OwnerRecoveryVerifiedSchool.Text = _confirmedSchool is { } school ? $"{school.Name}（{school.Code}）" : null;
        OwnerRecoveryVerifiedAccount.IsVisible = hasResult;
        OwnerRecoveryVerifiedAccount.Text = _result is { } result ? $"所有者短账号 · {result.Username}" : null;
        OwnerRecoveryTargetPane.IsVisible = !targetConfirmed;
        OwnerRecoverySchoolField.IsVisible = _verifiedServer is not null && OwnerRecoverySchool.ItemCount > 0;
        OwnerRecoveryCredentials.IsVisible = targetConfirmed && !hasResult;
        OwnerRecoveryCredentials.IsEnabled = !_writing;
        OwnerRecoveryBack.IsVisible = !hasResult;
        OwnerRecoveryBack.IsEnabled = !_writing;
        OwnerRecoveryCheckServer.IsVisible = !targetConfirmed;
        OwnerRecoveryCheckServer.IsEnabled = _read is null;
        OwnerRecoveryCheckServer.Classes.Set("accent", !OwnerRecoverySchoolField.IsVisible);
        OwnerRecoveryConfirmTarget.IsVisible = !targetConfirmed && OwnerRecoverySchoolField.IsVisible;
        OwnerRecoveryConfirmTarget.IsEnabled = _read is null && OwnerRecoverySchool.SelectedItem is OwnerRecoverySchool;
        OwnerRecoveryChangeTarget.IsVisible = OwnerRecoverySave.IsVisible = targetConfirmed && !hasResult;
        OwnerRecoveryChangeTarget.IsEnabled = OwnerRecoverySave.IsEnabled = !_writing;
        OwnerRecoveryReturnToLogin.IsVisible = hasResult;
        OwnerRecoveryReturnToLogin.Content = _result is { Confirmed: true } ? "返回并使用新 PIN 登录" : "返回登录核对";
    }

    private void ClearSecrets()
    {
        OwnerRecoverySetupKey.Clear();
        OwnerRecoveryNewPin.Clear();
        OwnerRecoveryRepeatPin.Clear();
        OwnerRecoveryKeyField.ErrorMessage = OwnerRecoveryNewPinField.ErrorMessage = OwnerRecoveryRepeatPinField.ErrorMessage = null;
    }

    private void SetNotice(string? message, bool error = false)
    {
        OwnerRecoveryNotice.Text = message;
        OwnerRecoveryNotice.IsVisible = !string.IsNullOrEmpty(message);
        OwnerRecoveryNotice.Classes.Set("school-server-warning", error);
    }

    private void Focus(Control target) => Dispatcher.UIThread.Post(() =>
    {
        if (!_open || !target.IsEffectivelyVisible || !target.IsEffectivelyEnabled) return;
        target.Focus();
        target.BringIntoView();
    }, DispatcherPriority.Loaded);

    private void BackClicked(object? sender, RoutedEventArgs args)
    {
        if (_open && CanExit) ReturnRequested?.Invoke(null);
    }

    private void LoginClicked(object? sender, RoutedEventArgs args)
    {
        if (_open && _result is { } result) ReturnRequested?.Invoke(result);
    }
}
