using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class SchoolTransferExportWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private ConsoleClient _client = null!;
    private School _school = null!;
    private SchoolTransferReadiness? _readiness;
    private bool _busy, _closed;

    public SchoolTransferExportWindow() => InitializeComponent();

    internal SchoolTransferExportWindow(ConsoleClient client, School school) : this()
    {
        _client = client;
        _school = school;
        TransferExportScope.Text = $"{school.Name}  ·  源服务器  {client.ServerUri.AbsoluteUri.TrimEnd('/')}";
        Opened += async (_, _) => { BatchWorkspaceWindow.FitSchoolWindow(this); await RunAsync(LoadAsync); };
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            ClearSecrets();
            if (!_busy) _lifetime.Dispose();
        };
    }

    internal Func<string, Task<string?>>? ChooseSavePath { get; init; }

    private async Task LoadAsync()
    {
        _readiness = null;
        TransferExportCounts.Text = "正在核对这所学校…";
        TransferExportRetained.Text = "";
        TransferExportNotice.Text = "";
        var readiness = await _client.GetSchoolTransferReadinessAsync(_school.Id, _lifetime.Token);
        if (_closed) return;
        _readiness = readiness;
        TransferExportScope.Text = $"{readiness.School.Name}（{readiness.School.Code}） · 源服务器  {_client.ServerUri.AbsoluteUri.TrimEnd('/')}";
        int Count(string key) => readiness.Counts.GetValueOrDefault(key);
        TransferExportCounts.Text = $"{Count("terms")} 个学期、{Count("workspaces")} 个教学空间、{Count("students")} 名学生、" +
            $"{Count("accounts")} 个账号、{Count("publications")} 条作业或通知、{Count("screens")} 台大屏。\n" +
            $"{Count("studySettings")} 条自习策略、{Count("seatVersions")} 个座位图版本（{Count("seatImages")} 个有图）、" +
            $"{Count("portableClassCredentials")} 份可沿用的班级口令。";
        TransferExportRetained.Text = $"留在源服务器的在期记录：{readiness.Retained.Photos} 张自习照片，{readiness.Retained.ExitEvents} 条退出记录。";
        TransferExportNotice.Text = readiness.Role == "ADMIN" ? "管理员可以核对准备情况，请由本校所有者导出。" :
            readiness.ReauthMethod == "UNAVAILABLE" ? "此身份提供方暂无法核实本次重新认证。普通登录仍可用，请由本校有效的本地所有者办理，或联系运维配置认证。" : "";
    }

    private async void Refresh(object? sender, RoutedEventArgs e) => await RunAsync(LoadAsync);
    private void CloseClicked(object? sender, RoutedEventArgs e) => Close();
    private async void Export(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_readiness is not { Role: "OWNER" } readiness || readiness.ReauthMethod is not ("PIN" or "OAUTH")) return;
        var secret = TransferExportPassword.Text ?? "";
        var pin = TransferExportPin.Text ?? "";
        if (secret.Length is < 12 or > 256)
        {
            TransferExportNotice.Text = "请输入用于打开文件的 12 至 256 字符迁移包口令。";
            return;
        }
        if (readiness.ReauthMethod == "PIN" && (pin.Length is < 4 or > 8 || !pin.All(char.IsAsciiDigit)))
        {
            TransferExportNotice.Text = "请输入所有者本人的个人 PIN。";
            return;
        }
        var suggested = $"{readiness.School.Code}-{DateTime.Today:yyyy-MM-dd}.npcw-transfer";
        var path = ChooseSavePath is { } choose ? await choose(suggested) : await PickSavePathAsync(suggested);
        if (path is null || _closed) return;
        ClearSecrets();
        var requestId = Guid.NewGuid();
        ExportVerification? verification = null;
        if (readiness.ReauthMethod == "OAUTH")
        {
            verification = await _client.BeginTransferReauthenticationAsync(_school.Id, requestId, _lifetime.Token);
            if (_closed) return;
            TransferExportNotice.Text = "请在浏览器重新验证同一所有者身份。验证通过后，将生成本次迁移包。";
            Process.Start(new ProcessStartInfo(new Uri(_client.ServerUri, verification.BrowserPath).AbsoluteUri) { UseShellExecute = true });
            await WaitForVerificationAsync(verification);
        }
        TransferExportNotice.Text = "正在生成加密迁移包…";
        await _client.SaveSchoolTransferAsync(_school.Id, requestId, secret, readiness.ReauthMethod == "PIN" ? pin : null,
            verification, path, _lifetime.Token);
        if (!_closed) TransferExportNotice.Text = $"已保存 {Path.GetFileName(path)}。请妥善保管迁移包和口令。";
    });

    private async Task WaitForVerificationAsync(ExportVerification verification)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(verification.ExpiresIn))
        {
            var status = await _client.ReadTransferReauthenticationAsync(_school.Id, verification, _lifetime.Token);
            if (status == "VERIFIED") return;
            if (status is not ("PENDING" or "AUTHENTICATING" or "VERIFYING")) break;
            await Task.Delay(TimeSpan.FromSeconds(2), _lifetime.Token);
        }
        throw new ConsoleApiException(428, "尚未核实同一所有者的新鲜认证，请从此窗口重新发起。");
    }

    private async Task<string?> PickSavePathAsync(string name)
    {
        using var file = await StorageProvider.SaveFilePickerAsync(new()
        {
            Title = "保存学校迁移包", SuggestedFileName = name, DefaultExtension = "npcw-transfer",
            FileTypeChoices = [new FilePickerFileType("学校迁移包") { Patterns = ["*.npcw-transfer"] }],
        });
        return file?.TryGetLocalPath();
    }

    private void ClearSecrets() { TransferExportPin.Clear(); TransferExportPassword.Clear(); }

    private void Render()
    {
        if (_closed) return;
        var canExport = _readiness is { Role: "OWNER", ReauthMethod: "PIN" or "OAUTH" };
        TransferExportFields.IsVisible = TransferExportPrimary.IsVisible = canExport;
        TransferExportPin.IsVisible = _readiness?.ReauthMethod == "PIN";
        TransferExportMethod.Text = _readiness?.ReauthMethod == "OAUTH" ? "导出时会在浏览器重新核验同一所有者身份。" :
            "用该校所有者的个人 PIN 授权导出。迁移包口令用于在目标服务器打开文件。";
        TransferExportContent.IsEnabled = TransferExportPrimary.IsEnabled = TransferExportRefresh.IsEnabled = !_busy;
    }

    private async Task RunAsync(Func<Task> work)
    {
        if (_closed || _busy) return;
        _busy = true;
        Render();
        try { await work(); }
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error)
        {
            if (!_closed)
            {
                if (error.StatusCode == 403) _readiness = null;
                TransferExportNotice.Text = error.Message;
            }
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            if (!_closed) TransferExportNotice.Text = "未保存迁移包。请检查浏览器、文件访问权限，或选择一个尚未使用的文件名。";
        }
        finally { _busy = false; if (_closed) _lifetime.Dispose(); else Render(); }
    }
}
