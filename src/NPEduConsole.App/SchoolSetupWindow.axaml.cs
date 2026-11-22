using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class SchoolSetupWindow : Window
{
    internal enum Work { NewSchool, Import, Resume }
    private enum Step { Key, School, Package, Owner }
    internal sealed record SignedIn(ConsoleClient Client, IReadOnlyList<School> Schools, string AccountName);
    private sealed record TeacherMode(string Id, string Label) { public override string ToString() => Label; }

    private readonly CancellationTokenSource _lifetime = new();
    private ConsoleClient _client = null!;
    private SchoolSetupSession? _setup;
    private TransferSchool? _school;
    private SchoolTransferPreview? _preview;
    private string? _path;
    private Guid? _pending;
    private Work _work;
    private Step _step;
    private bool _busy, _writing, _closed;

    public SchoolSetupWindow() => InitializeComponent();

    internal SchoolSetupWindow(string origin, Work work) : this()
    {
        _client = new ConsoleClient(origin);
        _work = work;
        SetupServer.Text = $"目标服务器  {_client.ServerUri.AbsoluteUri.TrimEnd('/')}";
        SetupYear.Text = DateTime.Today.Year.ToString();
        SetupSemester.ItemsSource = new[] {"第一学期", "第二学期"};
        SetupSemester.SelectedIndex = 0;
        SetupTeacherMode.ItemsSource = new TeacherMode[]
        {
            new("LOCAL_PIN", "个人 PIN"), new("SHARED_PASSWORD", "学校通用口令"), new("OAUTH_EMAIL", "OAuth 邮箱"),
        };
        SetupTeacherMode.SelectedIndex = 0;
        Opened += (_, _) => { BatchWorkspaceWindow.FitSchoolWindow(this); SetupKey.Focus(); };
        Closing += (_, args) => args.Cancel = _writing && !_closed;
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            ClearSecrets();
            if (!_busy)
            {
                if (Result is null) _client.Dispose();
                _lifetime.Dispose();
            }
        };
        Render();
    }

    internal SignedIn? Result { get; private set; }
    internal Func<Task<string?>>? ChoosePackagePath { get; init; }

    private async void Continue(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        switch (_step)
        {
            case Step.Key: await AuthenticateSetupAsync(); break;
            case Step.School: await InitializeAsync(); break;
            case Step.Package: await PackageStepAsync(); break;
            case Step.Owner: await LocalOwnerAsync(); break;
        }
    });

    private async Task AuthenticateSetupAsync()
    {
        var key = SetupKey.Text ?? "";
        if (key.Length == 0) { SetupNotice.Text = "请填写服务器初始化密钥。"; return; }
        SetupKey.Clear();
        _setup = await _client.BeginSchoolSetupAsync(key, _lifetime.Token);
        var status = await _client.GetSetupStatusAsync(_lifetime.Token);
        if (!status.SupportsDesktopSetup) throw new ConsoleApiException(426, "请更新 KV 后使用桌面初始化。");
        if (status.State == "COMPLETED") throw new ConsoleApiException(409, "本实例已完成初始化，请返回登录。");
        if (status.State == "CONFIGURING")
        {
            await ResumeAsync();
            return;
        }
        _step = _work == Work.Import ? Step.Package : Step.School;
        SetupNotice.Text = _work == Work.Import
            ? "确认目标为当前这台空白服务器。生产换服时，请在源服务器停止业务写入后使用最终导出的包。"
            : "先建立学校、所有者和首学期。进入学校后可继续建年级、首班、教师和大屏。";
        ScrollToTop();
    }

    private async Task ResumeAsync()
    {
        SetupContext context;
        try { context = await _client.GetSetupContextAsync(_setup!, _lifetime.Token); }
        catch (ConsoleApiException error) when (error.Code == "SETUP_CORE_INCOMPLETE")
        {
            _step = Step.School;
            SetupNotice.Text = "服务器可能已有预建所有者，但学校尚未建立。请填写原学校代码和所有者信息继续。";
            return;
        }
        if (_closed) return;
        if (context.MigrationId is { } previous)
        {
            var receipt = await _client.GetSchoolTransferReceiptAsync(_setup!, previous, _lifetime.Token);
            ShowTransferReceipt(receipt);
        }
        await ShowOwnerAsync(context.School, context.Owner.LocalUsername);
        SetupNotice.Text = "服务器确认学校数据已写入。请用所有者本人身份真实登录，完成初始化。";
    }

    private async Task InitializeAsync()
    {
        var name = SetupSchoolName.Text?.Trim() ?? "";
        var code = SetupSchoolCode.Text?.Trim() ?? "";
        var owner = SetupAdministratorName.Text?.Trim() ?? "";
        var username = SetupUsername.Text?.Trim() ?? "";
        var pin = SetupPin.Text ?? "";
        var term = SetupTermName.Text?.Trim() ?? "";
        var mode = (SetupTeacherMode.SelectedItem as TeacherMode)?.Id ?? "LOCAL_PIN";
        var pass = mode == "SHARED_PASSWORD" ? SetupShared.Text : null;
        if (name.Length == 0 || code.Length < 2 || owner.Length == 0 || username.Length < 2 || !ValidPin(pin) ||
            term.Length == 0 || !int.TryParse(SetupYear.Text, out var year) || year is < 2020 or > 2100 ||
            mode == "SHARED_PASSWORD" && pass is not {Length: >= 8 and <= 64})
        {
            SetupNotice.Text = "请核对学校、所有者、首学期和教师凭据。个人 PIN 为 4 至 8 位数字。";
            return;
        }
        var start = SetupStart.SelectedDate is { } first ? DateOnly.FromDateTime(first) : (DateOnly?)null;
        var end = SetupEnd.SelectedDate is { } last ? DateOnly.FromDateTime(last) : (DateOnly?)null;
        if (start > end) { SetupNotice.Text = "学期结束日期须在开始日期当天或之后。"; return; }
        SetupPin.Clear();
        SetupShared.Clear();
        _writing = true;
        SetupCreated created;
        try
        {
            created = await _client.InitializeSchoolAsync(_setup!, new(name, code, owner, username, pin,
                term, year, SetupSemester.SelectedIndex + 1, start, end, mode, SetupFallback.IsChecked == true, pass), _lifetime.Token);
        }
        catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or >= 500 || error.Code == "SETUP_SCHOOL_EXISTS")
        {
            var status = await _client.GetSetupStatusAsync(_lifetime.Token);
            if (status.State == "CONFIGURING") await ResumeAsync();
            else SetupNotice.Text = "尚未确认写入，请重新核对目标服务器后继续。";
            return;
        }
        finally { _writing = false; }
        await ShowOwnerAsync(created.School, username);
        SetupNotice.Text = "学校数据已写入。请验证所有者登录，随后进入学校总览。";
    }

    private async void ChoosePackage(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_pending is not null) return;
        var path = ChoosePackagePath is { } choose ? await choose() : await PickPackageAsync();
        if (_closed || path is null) return;
        _path = path;
        SetupFileName.Text = Path.GetFileName(path);
        SetupPackagePassphrase.Clear();
        ClearPreview();
        SetupPackagePassphrase.Focus();
    });

    private async Task<string?> PickPackageAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = "选择加密学校迁移包", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("学校迁移包") { Patterns = ["*.npcw-transfer"] }],
        });
        using var file = files.SingleOrDefault();
        return file?.TryGetLocalPath();
    }

    private async Task PackageStepAsync()
    {
        if (_pending is not null) { await CheckReceiptAsync(); return; }
        var passphrase = SetupPackagePassphrase.Text ?? "";
        if (_path is null || passphrase.Length is < 12 or > 256)
        {
            SetupNotice.Text = "请选择迁移包，并填写 12 至 256 个字符的迁移包口令。";
            return;
        }
        if (_preview is null)
        {
            SetupNotice.Text = "正在核对包内容和此目标实例…";
            _preview = await _client.PreviewSchoolTransferAsync(_setup!, _path, passphrase, _lifetime.Token);
            SetupTransferPreview.Present(_preview, _client.ServerUri.AbsoluteUri);
            SetupNotice.Text = "请核对源学校、目标服务器、留源数据和逐屏接入安排，再确认迁入。";
            return;
        }
        var preview = _preview;
        _pending = preview.Manifest.MigrationId;
        _writing = true;
        SetupNotice.Text = "正在写入此目标实例…";
        try
        {
            var receipt = await _client.ImportSchoolTransferAsync(_setup!, preview, _path, passphrase, _lifetime.Token);
            await AcceptTransferReceiptAsync(receipt, _pending.Value);
        }
        catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or >= 500)
        {
            await CheckReceiptAsync();
        }
        catch (ConsoleApiException)
        {
            _pending = null;
            ClearPreview();
            throw;
        }
        finally { _writing = false; }
    }

    private async void ReadReceipt(object? sender, RoutedEventArgs e) => await RunAsync(CheckReceiptAsync);

    private async Task CheckReceiptAsync()
    {
        if (_pending is not { } id) return;
        try
        {
            var receipt = await _client.GetSchoolTransferReceiptAsync(_setup!, id, _lifetime.Token);
            await AcceptTransferReceiptAsync(receipt, id);
        }
        catch (ConsoleApiException error) when (error.Code == "MIGRATION_RECEIPT_NOT_FOUND")
        {
            _pending = null;
            SetupNotice.Text = "暂未找到这份包的回执。可以再次确认同一份包，服务器会核对包文件和目标，已写入时返回原结果。";
        }
        catch (ConsoleApiException error)
        {
            SetupNotice.Text = $"迁入尚待确认。请继续只读核对本次回执。{error.Message}";
        }
    }

    private async Task AcceptTransferReceiptAsync(SchoolTransferReceipt receipt, Guid expected)
    {
        if (!receipt.Imported || receipt.Manifest.MigrationId != expected || receipt.School.Id != receipt.Manifest.School.Id)
            throw new ConsoleApiException(502, "服务器结果与原迁移编号不一致。");
        _pending = null;
        ShowTransferReceipt(receipt);
        var context = await _client.GetSetupContextAsync(_setup!, _lifetime.Token);
        if (context.School.Id != receipt.School.Id) throw new ConsoleApiException(502, "服务器初始化学校已变化。");
        await ShowOwnerAsync(receipt.School, context.Owner.LocalUsername);
        SetupNotice.Text = "KV 已确认这份迁移包写入。核对逐屏接入安排后，用原所有者身份完成登录。";
    }

    private void ShowTransferReceipt(SchoolTransferReceipt receipt)
    {
        _step = Step.Owner;
        SetupPackagePassphrase.Clear();
        _preview = new(receipt.Manifest, receipt.Counts, ["学校数据已写入，实例仍需所有者登录后完成初始化。"],
            null, receipt.ScreenActions, receipt.Retained, receipt.OwnerRoutes);
        SetupTransferPreview.Present(_preview, _client.ServerUri.AbsoluteUri);
    }

    private async Task ShowOwnerAsync(TransferSchool school, string? username)
    {
        _step = Step.Owner;
        _school = school;
        SetupOwnerUsername.Text = username ?? "";
        SetupOwnerInfo.Text = $"{school.Name}（{school.Code}）的所有者登录验证。需要时可择用该服务器已配置的浏览器方式。";
        ScrollToTop();
        try
        {
            var providers = await _client.GetDesktopOAuthProvidersAsync(_lifetime.Token);
            SetupProviders.ItemsSource = providers;
            SetupProviders.DisplayMemberBinding = new Avalonia.Data.Binding(nameof(DesktopOAuthProvider.Name));
            SetupProviders.SelectedIndex = providers.Count == 1 ? 0 : -1;
            SetupProviders.IsVisible = SetupOwnerBrowser.IsVisible = providers.Count > 0;
        }
        catch (ConsoleApiException) { }
    }

    private async Task LocalOwnerAsync()
    {
        var username = SetupOwnerUsername.Text?.Trim() ?? "";
        var pin = SetupOwnerPin.Text ?? "";
        if (_school is null || username.Length == 0 || !ValidPin(pin))
        {
            SetupNotice.Text = "请填写该校所有者的短账号和个人 PIN。";
            return;
        }
        SetupOwnerPin.Clear();
        await VerifyOwnerAsync(await _client.LoginAsync(_school.Code, username, pin, _lifetime.Token), username);
    }

    private async void BrowserOwner(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (SetupProviders.SelectedItem is not DesktopOAuthProvider provider)
        {
            SetupNotice.Text = "先选择该所有者使用的身份提供方。";
            return;
        }
        var request = await _client.BeginDesktopOAuthAsync(provider, _lifetime.Token);
        try
        {
            SetupNotice.Text = $"请完成浏览器验证。核对码 {request.ConfirmationCode}。";
            Process.Start(new ProcessStartInfo(request.BrowserUri.AbsoluteUri) { UseShellExecute = true });
            var signIn = await _client.WaitForDesktopOAuthAsync(request, () => Dispatcher.UIThread.Post(() =>
                SetupNotice.Text = "浏览器认证已完成，正在核对这个实例上的所有者登录。"), _lifetime.Token);
            await VerifyOwnerAsync(signIn.Schools, signIn.AccountName);
        }
        finally
        {
            if (Result is null)
                try { await _client.CancelDesktopOAuthAsync(request, CancellationToken.None); }
                catch (ConsoleApiException) { }
        }
    });

    private async Task VerifyOwnerAsync(IReadOnlyList<School> schools, string username)
    {
        try
        {
            if (_school is null || !schools.Any(school => school.Id == _school.Id && school.ManagementRole == "OWNER"))
                throw new ConsoleApiException(403, "该账号当前不是目标学校的所有者。");
            SetupNotice.Text = "所有者登录已核验，正在确认初始化完成…";
            _writing = true;
            await _client.FinishSchoolSetupAsync(_setup!, _school.Id, _lifetime.Token);
            if (_closed) return;
            Result = new(_client, schools, username);
            _writing = false;
            Close();
        }
        finally
        {
            _writing = false;
            if (Result is null) try { await _client.LogoutAsync(CancellationToken.None); } catch (ConsoleApiException) { }
        }
    }

    private static bool ValidPin(string pin) => pin.Length is >= 4 and <= 8 && pin.All(char.IsAsciiDigit);
    private void TeacherModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SetupShared is null || SetupFallback is null) return;
        SetupShared.IsVisible = (SetupTeacherMode.SelectedItem as TeacherMode)?.Id == "SHARED_PASSWORD";
        if (!SetupShared.IsVisible) SetupShared.Clear();
        SetupFallback.IsEnabled = (SetupTeacherMode.SelectedItem as TeacherMode)?.Id != "OAUTH_EMAIL";
    }
    private void PassphraseChanged(object? sender, TextChangedEventArgs e)
    {
        if (_step == Step.Package && _pending is null && SetupPackagePassphrase.IsVisible) ClearPreview();
    }
    private void ClearPreview()
    {
        _preview = null;
        SetupTransferPreview.Present(null, _client.ServerUri.AbsoluteUri);
        Render();
    }
    private void Render()
    {
        if (_closed) return;
        SetupHeading.Text = _step == Step.Owner ? "核验所有者并完成初始化" :
            _work == Work.Import ? "从迁移包恢复学校" : _work == Work.NewSchool ? "新建学校" : "继续初始化";
        SetupKeyPanel.IsVisible = _step == Step.Key;
        SetupNewPanel.IsVisible = _step == Step.School;
        SetupFilePanel.IsVisible = _step == Step.Package;
        SetupPackagePassphrase.IsVisible = _step == Step.Package && _preview is null;
        SetupOwnerPanel.IsVisible = _step == Step.Owner;
        SetupTransferPreview.IsVisible = _preview is not null;
        SetupContent.IsEnabled = !_busy;
        SetupClose.IsEnabled = !_writing;
        SetupReadReceipt.IsVisible = _pending is not null;
        SetupReadReceipt.IsEnabled = !_busy;
        SetupPrimary.IsEnabled = !_busy && _pending is null;
        SetupPrimary.Content = _step switch
        {
            Step.Key => "继续", Step.School => "建立学校", Step.Package => _preview is null ? "检查迁移包" : "确认迁入",
            _ => "验证所有者并进入学校",
        };
    }
    private void ScrollToTop() => SetupScroll.Offset = new Avalonia.Vector();
    private void CloseClicked(object? sender, RoutedEventArgs e) => Close();
    private void ClearSecrets() { SetupKey.Clear(); SetupPin.Clear(); SetupShared.Clear(); SetupPackagePassphrase.Clear(); SetupOwnerPin.Clear(); }
    private async Task RunAsync(Func<Task> work)
    {
        if (_busy || _closed) return;
        _busy = true;
        Render();
        try { await work(); }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception error) when (error is ConsoleApiException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            if (!_closed) SetupNotice.Text = error is ConsoleApiException ? error.Message : "无法读取文件或打开浏览器，请核对本机访问权限。";
        }
        finally
        {
            _busy = false;
            if (_closed)
            {
                if (Result is null) _client.Dispose();
                _lifetime.Dispose();
            }
            else Render();
        }
    }
}
