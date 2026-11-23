using Avalonia.Controls;
using Avalonia.Interactivity;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class SchoolConnectionSetupView : UserControl
{
    internal sealed record Target(Uri Kv, Uri? Website, bool LegacyAcknowledged)
    {
        internal async Task VerifyDeclarationAsync(CancellationToken ct)
        {
            if (Website is null) return;
            var current = await SchoolSetupDeployment.ReadDeclarationAsync(Website, ct);
            if (current.State == DeploymentDeclarationState.Declared && current.ApiBaseUrl == Kv ||
                current.State == DeploymentDeclarationState.Missing && LegacyAcknowledged) return;
            var message = current.ApiBaseUrl is { } api
                ? $"网页现在声明使用 {api.AbsoluteUri}。请返回检查地址后继续。"
                : "无法再次确认网页部署声明。请返回连接检查。";
            throw new ConsoleApiException(409, message);
        }
    }

    private sealed record AddressInput(string Website, string Api, bool Separate, bool ApiOnly);
    private sealed record KvCheck(SetupStatus? Status, ConsoleApiException? Error = null);
    private sealed record CheckedTarget(Uri Kv, Uri? Website, DeploymentDeclaration? Declaration, KvCheck KvRead)
    {
        public SetupStatus? Status => KvRead.Status;
    }
    private CancellationTokenSource? _request;
    private CheckedTarget? _checked;
    private AddressInput? _checkedInput;

    public SchoolConnectionSetupView() => InitializeComponent();

    internal event Action? ReturnRequested;
    internal event Action<Uri>? LoginRequested;
    internal event Action<Target, SchoolSetupView.Work>? SetupRequested;
    internal Func<Uri, bool>? BrowserLauncher { get; set; }

    internal void Open(string address)
    {
        SetupWebsiteAddress.Text = address;
        ClearCheck();
        SetupWebsiteAddress.Focus();
    }

    internal void Reset()
    {
        ClearCheck();
        SetupWebsiteAddress.Clear();
        SetupApiAddress.Clear();
        SetupSeparateApi.IsChecked = SetupApiOnly.IsChecked = false;
    }

    internal void ClearCheck()
    {
        _request?.Cancel();
        _request = null;
        _checked = null;
        _checkedInput = null;
        SetupConnectionStatus.Text = "";
        SetupLegacyAcknowledged.IsChecked = false;
        Render();
    }

    internal void FocusCheck() => SetupCheckConnection.Focus();

    private void AddressChanged(object? sender, RoutedEventArgs args)
    {
        if (SetupCheckConnection is not null && _checkedInput != ReadInputs()) ClearCheck();
    }

    private AddressInput ReadInputs() => new(SetupWebsiteAddress.Text?.Trim() ?? "", SetupApiAddress.Text?.Trim() ?? "",
        SetupSeparateApi.IsChecked == true, SetupApiOnly.IsChecked == true);

    private async void CheckClicked(object? sender, RoutedEventArgs args)
    {
        ClearCheck();
        var input = _checkedInput = ReadInputs();
        Uri? website;
        Uri kv;
        try
        {
            website = input.ApiOnly ? null : SchoolSetupDeployment.NormalizeWebsite(input.Website);
            kv = website is null || input.Separate ? ConsoleClient.NormalizeKvAddress(input.Api) : website;
        }
        catch (ConsoleApiException error)
        {
            SetupConnectionStatus.Text = error.Message;
            return;
        }

        using var request = _request = new CancellationTokenSource();
        SetupConnectionStatus.Text = "正在读取服务器状态与网页部署声明…";
        SetupCheckConnection.IsEnabled = false;
        try
        {
            using var client = new ConsoleClient(kv.AbsoluteUri);
            var status = ReadStatusAsync(client, request.Token);
            var declaration = ReadDeclarationAsync(website, request.Token);
            await Task.WhenAll(status, declaration);
            if (_request != request || input != ReadInputs()) return;
            _checked = new(kv, website, declaration.Result, status.Result);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally
        {
            if (_request == request)
            {
                _request = null;
                Render();
            }
        }
    }

    private static async Task<KvCheck> ReadStatusAsync(ConsoleClient client, CancellationToken ct)
    {
        try { return new(await client.GetSetupStatusAsync(ct)); }
        catch (ConsoleApiException error) { return new(null, error); }
    }

    private static async Task<DeploymentDeclaration?> ReadDeclarationAsync(Uri? website, CancellationToken ct) =>
        website is null ? null : await SchoolSetupDeployment.ReadDeclarationAsync(website, ct);

    private void Render()
    {
        var apiOnly = SetupApiOnly.IsChecked == true;
        SetupWebsiteField.IsVisible = SetupSeparateApi.IsVisible = !apiOnly;
        SetupApiField.IsVisible = apiOnly || SetupSeparateApi.IsChecked == true;
        SetupCheckConnection.IsEnabled = _request is null;
        SetupConnectionDetails.IsVisible = _checked is not null;
        if (_checked is not { } result)
        {
            InitializeNewSchool.IsVisible = RestoreSchoolTransfer.IsVisible = ResumeSchoolSetup.IsVisible = SetupLoginWithTarget.IsVisible = false;
            SetupOpenBrowserCheck.IsVisible = SetupOpenLegacy.IsVisible = false;
            SetupCheckConnection.Classes.Set("accent", true);
            return;
        }

        SetupCheckedOrigin.Text = $"KV 目标  {result.Kv.AbsoluteUri}";
        SetupConnectionStatus.Text = DescribeStatus(result.KvRead);
        SetupDeclarationStatus.Text = DescribeDeclaration(result);
        SetupOpenBrowserCheck.IsVisible = result.Website is not null;
        SetupOpenLegacy.IsVisible = result.Website is not null && result.Status?.State != "COMPLETED" &&
            (result.Status is { SupportsDesktopSetup: false } || result.KvRead.Error?.StatusCode == 404) &&
            (result.Declaration?.State == DeploymentDeclarationState.Missing ||
             result.Declaration?.ApiBaseUrl == result.Kv);
        SetupLegacyAcknowledged.IsVisible = result.Declaration?.State == DeploymentDeclarationState.Missing &&
            result.Status is { SupportsDesktopSetup: true, State: "NEW" or "CONFIGURING", CanStart: true };
        var supported = result.Status is { SupportsDesktopSetup: true };
        InitializeNewSchool.IsVisible = RestoreSchoolTransfer.IsVisible = supported && result.Status?.State == "NEW";
        ResumeSchoolSetup.IsVisible = supported && result.Status?.State == "CONFIGURING";
        SetupLoginWithTarget.IsVisible = result.Status?.State == "COMPLETED";
        UpdateSetupActions();
    }

    private static string DescribeStatus(KvCheck result) => result switch
    {
        { Error.Code: "SETUP_STATUS_INVALID" } => "无法核对 KV。该地址返回了网页或无法识别的数据。请检查 /api 是否转发给 KV。",
        { Error.StatusCode: 404 } => "未找到初始化状态接口（HTTP 404）。请检查 API 路径或 KV 版本。旧版本可检查原 /setup 页面。",
        { Error.StatusCode: 0 } => "无法连接 KV。请检查 API 地址、网络或证书。",
        { Error.StatusCode: 408 } => "读取 KV 状态超时。请检查网络和服务状态后重试。",
        { Error: { } error } => $"无法读取 KV 状态（HTTP {error.StatusCode}）。请检查服务和网关。",
        { Status.State: "COMPLETED" } => "此服务器已完成初始化，可以登录。",
        { Status.SupportsDesktopSetup: false } => "当前 KV 未提供桌面初始化能力。可使用原网页流程，或请部署人员升级。",
        { Status: { CanStart: false, State: "NEW" or "CONFIGURING" } status } => "服务器暂不能初始化。" +
            string.Join("；", (status.Checks ?? []).Where(check => !check.Ok && check.Severity == "ERROR").Select(check => check.Message)),
        { Status.State: "NEW" } => "此服务器尚未建校。可新建学校，或从加密迁移包恢复。",
        { Status.State: "CONFIGURING" } => "检测到未完成的初始化。可在原服务器继续。",
        _ => "无法识别 KV 返回的初始化状态，请核对版本。",
    };

    private static string DescribeDeclaration(CheckedTarget target) => target.Declaration switch
    {
        null => "只核对了 KV。师生网页及浏览器连接待核对。",
        { State: DeploymentDeclarationState.Declared, ApiBaseUrl: { } api } when api == target.Kv =>
            "网页声明的 KV 地址一致。请在浏览器另行检查实际连接。",
        { State: DeploymentDeclarationState.Declared, ApiBaseUrl: { } api } =>
            $"地址不一致。网页声明使用 {api.AbsoluteUri}。调整地址并重新检查后才可验证密钥。",
        { State: DeploymentDeclarationState.Missing } =>
            "网页未提供部署声明。请另行核对它实际使用的 KV，浏览器连接待核对。",
        { State: DeploymentDeclarationState.Invalid } => "网页部署声明无效或版本不支持，请核对后重试。",
        _ => "无法读取网页部署声明，请检查网页地址与网络后重试。",
    };

    private bool CanStart => _checkedInput == ReadInputs() &&
        _checked is { Status: { SupportsDesktopSetup: true, CanStart: true, State: "NEW" or "CONFIGURING" } } result &&
        (result.Website is null || result.Declaration?.ApiBaseUrl == result.Kv ||
         result.Declaration?.State == DeploymentDeclarationState.Missing && SetupLegacyAcknowledged.IsChecked == true);

    private void UpdateSetupActions()
    {
        var canStart = CanStart;
        InitializeNewSchool.IsEnabled = RestoreSchoolTransfer.IsEnabled = ResumeSchoolSetup.IsEnabled = canStart;
        SetupCheckConnection.Classes.Set("accent", !canStart && _checked?.Status?.State != "COMPLETED");
    }

    private void AcknowledgementChanged(object? sender, RoutedEventArgs args)
    {
        if (InitializeNewSchool is not null) UpdateSetupActions();
    }

    private void Start(SchoolSetupView.Work work)
    {
        if (CanStart && _checked is { } target &&
            (work == SchoolSetupView.Work.Resume ? target.Status?.State == "CONFIGURING" : target.Status?.State == "NEW"))
            SetupRequested?.Invoke(new(target.Kv, target.Website, SetupLegacyAcknowledged.IsChecked == true), work);
    }

    private void NewClicked(object? sender, RoutedEventArgs args) => Start(SchoolSetupView.Work.NewSchool);
    private void ImportClicked(object? sender, RoutedEventArgs args) => Start(SchoolSetupView.Work.Import);
    private void ResumeClicked(object? sender, RoutedEventArgs args) => Start(SchoolSetupView.Work.Resume);
    private void ReturnClicked(object? sender, RoutedEventArgs args) => ReturnRequested?.Invoke();
    private void LoginClicked(object? sender, RoutedEventArgs args)
    {
        if (_checkedInput == ReadInputs() && _checked is { Status.State: "COMPLETED" } target) LoginRequested?.Invoke(target.Kv);
    }

    private void OpenBrowserCheck(object? sender, RoutedEventArgs args)
    {
        if (_checked is { Website: { } website } target) OpenWebsite(SchoolSetupDeployment.BrowserCheckUrl(website, target.Kv));
    }

    private void OpenLegacy(object? sender, RoutedEventArgs args)
    {
        if (SetupOpenLegacy.IsEffectivelyVisible && _checked?.Website is { } website)
            OpenWebsite(SchoolSetupDeployment.LegacySetupUrl(website));
    }

    private void OpenWebsite(Uri destination)
    {
        if (!(BrowserLauncher ?? SchoolAdminBrowser.Open)(destination))
            SetupConnectionStatus.Text = "无法打开系统浏览器。请直接在浏览器中访问网页。";
    }
}
