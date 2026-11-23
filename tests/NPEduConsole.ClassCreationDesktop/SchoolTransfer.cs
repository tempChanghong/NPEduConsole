using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private sealed record TransferAccess(string SourceOrigin, string SetupKey, string Pin, string SchoolCode, bool Isolated,
        string? TargetOrigin, string? File, string? Passphrase, string? SchoolId, string? FrontId, string? RearId,
        string? SplitWebsite = null, string? SplitApi = null);

    private static int StartSchoolTransfer(string fixture, string output, bool firstSchool)
    {
        var access = JsonSerializer.Deserialize<TransferAccess>(File.ReadAllText(fixture), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Expected an isolated transfer fixture.");
        Check(access.Isolated && IsLoopback(access.SourceOrigin) && (firstSchool || IsLoopback(access.TargetOrigin!)),
            "First school and migration tests require loopback isolation.");
        if (firstSchool) Check(IsLoopback(access.SplitWebsite!) && IsLoopback(access.SplitApi!), "separate desktop gateways are isolated");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifySchoolTransferAsync(access, output, firstSchool); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifySchoolTransferAsync(TransferAccess access, string output, bool firstSchool)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        try
        {
            main.Width = 1160;
            main.Height = 780;
            var loginAddress = Find<TextBox>(main, "ServerAddress");
            loginAddress.Text = access.SourceOrigin;
            if (firstSchool)
            {
                var handle = main.TryGetPlatformHandle()?.Handle ?? throw new InvalidDataException("Windows main HWND missing.");
                main.Width = 840;
                main.Height = 480;
                await Until(() => main.ClientSize is { Width: <= 845, Height: <= 485 }, "840×480 main window");
                await CaptureWithFrame(main, output, "00-narrow-login-entry");
                var connection = OpenConnection();
                Uri? browserTarget = null;
                connection.BrowserLauncher = uri => { browserTarget = uri; return true; };
                Field(connection, "SetupWebsiteAddress").Text = access.SplitWebsite;
                await CheckConnection(connection, "无法核对 KV");
                Check(!Ready(connection, "InitializeNewSchool"), "frontend-only address cannot open a key form");
                Find<CheckBox>(connection, "SetupSeparateApi").IsChecked = true;
                Field(connection, "SetupApiAddress").Text = access.SourceOrigin;
                await CheckConnection(connection, "尚未建校");
                Check(!Ready(connection, "InitializeNewSchool") && Text(connection, "SetupDeclarationStatus").Contains("地址不一致"),
                    "an otherwise healthy KV with a different declared API cannot receive a key");
                Click(Find<Button>(connection, "InitializeNewSchool"));
                Check(!Find<ContentControl>(main, "SchoolSetupHost").IsVisible, "even a routed click cannot bypass the mismatch");
                Click(Find<Button>(connection, "SetupOpenBrowserCheck"));
                Check(browserTarget?.AbsolutePath == "/connection-check.html" &&
                    Uri.UnescapeDataString(browserTarget.Fragment) == "#expected=" + new Uri(access.SourceOrigin + "/").AbsoluteUri,
                    "browser diagnostics receives only the expected API root in a fragment");
                AssertInClient(main, Find<Button>(connection, "SetupOpenBrowserCheck"), "browser check in narrow footer", 1);
                await CaptureWithFrame(main, output, "01-narrow-mismatched-origins");

                Field(connection, "SetupApiAddress").Text = access.SplitApi;
                await SetFixture("/api-response/missing");
                await CheckConnection(connection, "HTTP 404");
                Check(Ready(connection, "SetupOpenLegacy") && !Ready(connection, "InitializeNewSchool"),
                    "404 allows inspecting legacy web setup without unlocking desktop writes");
                Click(Find<Button>(connection, "SetupOpenLegacy"));
                Check(browserTarget?.AbsolutePath == "/setup", "legacy entry opens the chosen website's setup page");
                await SetFixture("/api-response/unknown");
                await CheckConnection(connection, "无法识别的数据");
                Check(!Ready(connection, "InitializeNewSchool"), "unknown KV status cannot create a school");
                await SetFixture("/api-response/closed");
                await CheckConnection(connection, "无法连接 KV");
                Check(!Ready(connection, "InitializeNewSchool"), "transport failure cannot create a school");
                await SetFixture("/api-response/live");
                await SetFixture("/delay-next-declaration");
                Click(Find<Button>(connection, "SetupCheckConnection"));
                Check(!Ready(connection, "SetupCheckConnection"), "old deployment read is in flight");
                Find<CheckBox>(connection, "SetupSeparateApi").IsChecked = false;
                Field(connection, "SetupWebsiteAddress").Text = access.SourceOrigin;
                await CheckConnection(connection, "尚未建校");
                await Task.Delay(1250);
                Check(Ready(connection, "InitializeNewSchool") && Text(connection, "SetupDeclarationStatus").Contains("地址一致"),
                    "late split declaration cannot overwrite the freshly checked same-origin result");

                await SetFixture("/same-declaration/missing");
                await CheckConnection(connection, "尚未建校");
                Check(!Ready(connection, "InitializeNewSchool"), "website without a deployment declaration needs an explicit check");
                Find<CheckBox>(connection, "SetupLegacyAcknowledged").IsChecked = true;
                Check(Ready(connection, "InitializeNewSchool"), "explicit confirmation unlocks a legacy website");
                await SetFixture("/same-declaration/same");
                await CheckConnection(connection, "尚未建校");

                main.Width = 1160;
                main.Height = 780;
                await Until(() => main.ClientSize is { Width: >= 1155, Height: >= 775 }, "1160×780 main window");
                await CaptureWithFrame(main, output, "02-wide-same-origin");
                await SetFixture("/same-declaration/changed");
                var outdated = await OpenSetup(connection, "InitializeNewSchool");
                await Until(() => Text(outdated, "SetupNotice").Contains("现在声明使用") && Ready(outdated, "SetupPrimary"),
                    "a changed declaration stops key transmission at the last check");
                Click(Find<Button>(outdated, "SetupClose"));
                await SetFixture("/same-declaration/same");
                await CheckConnection(connection, "尚未建校");
                var wizard = await OpenSetup(connection, "InitializeNewSchool");
                await Until(() => Field(wizard, "SetupSchoolCode").IsEffectivelyVisible && Ready(wizard, "SetupPrimary"),
                    "school fields after valid key");
                Check(TopLevel.GetTopLevel(wizard) is Window host && host.TryGetPlatformHandle()?.Handle == handle &&
                    desktop.Windows.Count == 1, "server authorization and school fields use the original HWND");
                Field(wizard, "SetupSchoolName").Text = "Console 新建学校";
                Field(wizard, "SetupSchoolCode").Text = access.SchoolCode;
                Field(wizard, "SetupAdministratorName").Text = "首位所有者";
                Field(wizard, "SetupUsername").Text = "founder";
                Field(wizard, "SetupPin").Text = access.Pin;
                Field(wizard, "SetupTermName").Text = "首个启用学期";
                Click(Find<Button>(wizard, "SetupPrimary"));
                await Until(() => Field(wizard, "SetupKey").IsEffectivelyVisible && Text(wizard, "SetupNotice").Contains("已过期"),
                    "expired authorization returns to key without finishing setup");
                EnterKey(wizard);
                await Until(() => Field(wizard, "SetupSchoolCode").IsEffectivelyVisible && Ready(wizard, "SetupPrimary"),
                    "same target can renew authorization");
                Check(Field(wizard, "SetupSchoolCode").Text == access.SchoolCode &&
                    string.IsNullOrEmpty(Field(wizard, "SetupPin").Text), "only nonsensitive school draft is preserved");
                Field(wizard, "SetupPin").Text = access.Pin;
                Click(Find<Button>(wizard, "SetupPrimary"));
                await WaitForHeldReplyAsync(access.SplitApi + "/__oobe/recovery/held/initialize-write");
                Check(!wizard.CanExit && !Ready(wizard, "SetupClose"), "actual creation POST still protects closing before it returns");
                main.Close();
                Check(main.IsVisible && wizard.IsEffectivelyVisible, "closing the actual window is blocked while the creation POST is pending");
                await SetFixture("/recovery/release/initialize-write");
                await WaitForHeldReplyAsync(access.SplitApi + "/__oobe/recovery/held/initialize");
                await CaptureWithFrame(main, output, "03-wide-create-recovery-reading");
                Check(Ready(wizard, "SetupClose"), "a held read-only check after the creation POST must allow returning to connection setup");
                Click(Find<Button>(wizard, "SetupClose"));
                await Until(() => connection.IsEffectivelyVisible, "return to checked-address flow");
                Check(Field(connection, "SetupWebsiteAddress").Text == access.SourceOrigin, "return retains the original checked server address");
                Field(connection, "SetupWebsiteAddress").Text = access.SplitWebsite;
                Find<CheckBox>(connection, "SetupSeparateApi").IsChecked = true;
                Field(connection, "SetupApiAddress").Text = access.SplitApi;
                await CheckConnection(connection, "未完成的初始化");
                main.Width = 840;
                main.Height = 480;
                await Until(() => main.ClientSize is { Width: <= 845, Height: <= 485 }, "compact split resume");
                await CaptureWithFrame(main, output, "04-narrow-split-resume");
                wizard = await OpenSetup(connection, "ResumeSchoolSetup");
                await Until(() => Find<Border>(wizard, "SetupOwnerPanel").IsEffectivelyVisible &&
                    Find<Button>(wizard, "SetupPrimary").IsEffectivelyEnabled, "resume the originally written school");
                Check(Text(wizard, "SetupOwnerInfo").Contains(access.SchoolCode) &&
                    Text(wizard, "SetupServer").Contains(access.SplitApi!), "owner verification in the confirmed split API");
                await SetFixture("/recovery/release/initialize");
                Check(Ready(wizard, "SetupPrimary") && Text(wizard, "SetupOwnerInfo").Contains(access.SchoolCode),
                    "the old read can finish without replacing the reopened school");
                await CaptureWithFrame(main, output, "05-narrow-owner-verification");
                EnterOwner(wizard);
                await WaitForHeldReplyAsync(access.SplitApi + "/__oobe/recovery/held/complete-write");
                Check(!wizard.CanExit && !Ready(wizard, "SetupClose"), "actual owner completion POST still protects closing");
                await SetFixture("/recovery/release/complete-write");
                await WaitForHeldReplyAsync(access.SplitApi + "/__oobe/recovery/held/complete");
                Check(Ready(wizard, "SetupClose"), "a held read-only check after the completion POST must allow returning");
                await CaptureWithFrame(main, output, "05a-narrow-owner-recovery-reading");
                await SetFixture("/recovery/release/complete");
                var create = Find<Button>(main, "OpenFirstSchoolClass");
                await Until(() => create.IsEffectivelyVisible && create.IsEffectivelyEnabled, "owner completion reply lost but readback opens scoped first class action");
                Check(Text(main, "FirstSchoolClassScope").Contains("首个启用学期"), "first class names the exact confirmed term");
                await CheckOpeningAvailable("0 个启用行政班", "06a-narrow-fresh-school-checklist");
                await CaptureWithFrame(main, output, "06-narrow-first-class-entry");
                Click(create);
                var dialog = await WindowOf<ClassCreationWindow>();
                await Until(() => Field(dialog, "NewGradeCode") is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true },
                    "scoped first class action opens its existing grade form");
                Find<TextBox>(dialog, "NewGradeCode").Text = "G1";
                Find<TextBox>(dialog, "NewGradeName").Text = "高一";
                await Until(() => Ready(dialog, "SubmitGradeCreationButton"), "entered first grade name and code");
                Click(Find<Button>(dialog, "SubmitGradeCreationButton"));
                await Until(() => Find<ComboBox>(dialog, "CreationGrade").SelectedItem is ClassCreationGrade { Code: "G1" },
                    "created grade selected by its server ID");
                Find<TextBox>(dialog, "CreationName").Text = "高一（1）班";
                Find<TextBox>(dialog, "CreationCode").Text = "G1-1";
                Click(Find<Button>(dialog, "SubmitClassCreationButton"));
                var roster = await AcademicPageAsync<ClassRosterView>(main);
                await Until(() => Find<Grid>(roster, "RosterEditorPane").IsEffectivelyVisible, "first class opens the standard roster");
                await CaptureWithFrame(main, output, "07-first-roster");
                Click(Find<Button>(roster, "CloseRosterButton"));
                await SignOut();
                Check(loginAddress.Text == access.SplitApi, "returning to login preserves the verified split API");
                connection = OpenConnection();
                Find<CheckBox>(connection, "SetupApiOnly").IsChecked = true;
                Field(connection, "SetupApiAddress").Text = access.SplitApi;
                await CheckConnection(connection, "已完成初始化");
                Check(!Ready(connection, "InitializeNewSchool") && !Ready(connection, "ResumeSchoolSetup") &&
                    Text(connection, "SetupDeclarationStatus").Contains("网页及浏览器连接待核对"),
                    "explicit API-only flow reports completed with website unchecked");
                await CaptureWithFrame(main, output, "08-completed-login");
                Click(Find<Button>(connection, "SetupLoginWithTarget"));
                Check(loginAddress.Text == access.SplitApi, "normal login uses API, not previously checked website");
                Field(main, "SchoolCode").Text = access.SchoolCode;
                Field(main, "Username").Text = "founder";
                Field(main, "Password").Text = access.Pin;
                Click(Find<Button>(main, "LoginButton"));
                await Until(() => Find<Button>(main, "OpenClassWorkspaceButton").IsEffectivelyEnabled, "normal login still works after setup");
                Console.WriteLine($"PASS: school OOBE, reused main HWND, 1160×780 / 840×480, {main.RenderScaling:P0} scaling.");

                async Task SetFixture(string path)
                {
                    using var http = new HttpClient();
                    using var response = await http.GetAsync(access.SplitApi + "/__oobe" + path);
                    response.EnsureSuccessStatusCode();
                }

            }
            else
            {
                Find<TextBox>(main, "SchoolCode").Text = access.SchoolCode;
                Find<TextBox>(main, "Username").Text = "admin";
                Find<TextBox>(main, "Password").Text = access.Pin;
                main.SaveSchoolTransferPackage = _ => Task.FromResult(access.File);
                main.ChooseSchoolTransferPackage = () => Task.FromResult(access.File);
                Click(Find<Button>(main, "LoginButton"));
                var transfer = Find<Button>(main, "OpenSchoolTransferButton");
                await Until(() => transfer.IsEffectivelyEnabled && transfer.IsEffectivelyVisible, "source owner sees school migration");
                Click(transfer);
                var export = await WindowOf<SchoolTransferExportWindow>();
                export.Width = 1160;
                export.Height = 780;
                var exportAction = Find<Button>(export, "TransferExportPrimary");
                await Until(() => exportAction.IsEffectivelyVisible && exportAction.IsEffectivelyEnabled, "source snapshot and proof field");
                Check(Find<TextBlock>(export, "TransferExportRetained").Text?.Contains("1 张自习照片，1 条退出记录") == true,
                    "source shows only counts of private records");
                await Capture(export, output, "01-source-export-wide");
                Field(export, "TransferExportPin").Text = access.Pin;
                Field(export, "TransferExportPassword").Text = access.Passphrase;
                Click(exportAction);
                await Until(() => exportAction.IsEffectivelyEnabled, "source export attempt finishes");
                var exportNotice = Find<TextBlock>(export, "TransferExportNotice").Text;
                Check(File.Exists(access.File!) && exportNotice?.StartsWith("已保存") == true,
                    "source writes an encrypted file: " + exportNotice);
                export.Close();
                await Until(() => main.GetVisualDescendants().OfType<Button>().Any(button => button.IsEffectivelyVisible &&
                    button.IsEffectivelyEnabled && button.Content?.ToString() == "退出登录"), "source session can sign out");
                Click(main.GetVisualDescendants().OfType<Button>().Single(button => button.IsEffectivelyVisible &&
                    button.IsEffectivelyEnabled && button.Content?.ToString() == "退出登录"));
                await Until(() => Ready(main, "OpenFirstSchoolSetup"), "can select the target after sign out");
                var connection = OpenConnection();
                Find<CheckBox>(connection, "SetupApiOnly").IsChecked = true;
                Field(connection, "SetupApiAddress").Text = access.TargetOrigin;
                await CheckConnection(connection, "尚未建校");
                var wizard = await OpenSetup(connection, "RestoreSchoolTransfer");
                main.Width = 840;
                main.Height = 480;
                await Until(() => Find<Button>(wizard, "SetupChoosePackage").IsEffectivelyVisible &&
                    main.ClientSize is { Width: <= 845, Height: <= 485 }, "compact target restoration in the main window");
                Click(Find<Button>(wizard, "SetupChoosePackage"));
                await Until(() => Find<TextBlock>(wizard, "SetupFileName").Text?.EndsWith(".npcw-transfer") == true, "exact package chosen");
                Field(wizard, "SetupPackagePassphrase").Text = access.Passphrase;
                Click(Find<Button>(wizard, "SetupPrimary"));
                await Until(() => Find<Button>(wizard, "SetupPrimary").Content is "确认迁入" &&
                    Find<Button>(wizard, "SetupPrimary").IsEffectivelyEnabled, "target verified package and destination");
                Check(Find<TextBlock>(wizard, "SetupServer").Text?.Contains(access.TargetOrigin!) == true, "target origin is visible");
                await Capture(wizard, output, "02-target-preview-compact");
                Click(Find<Button>(wizard, "SetupPrimary"));
                await WaitForHeldReplyAsync(access.TargetOrigin + "/__migration-recovery/held");
                Check(wizard.CanExit && Ready(wizard, "SetupClose"), "migration receipt lookup does not keep the actual write lock");
                await CaptureWithFrame(main, output, "02a-narrow-migration-receipt-reading");
                using (var http = new HttpClient())
                using (var release = await http.GetAsync(access.TargetOrigin + "/__migration-recovery/release"))
                    release.EnsureSuccessStatusCode();
                await Until(() => Find<Border>(wizard, "SetupOwnerPanel").IsVisible && Find<Button>(wizard, "SetupPrimary").IsEffectivelyEnabled,
                    "target found the original receipt after the proxy dropped the import response");
                Check(Find<TextBlock>(wizard, "SetupNotice").Text?.Contains("确认这份迁移包写入") == true, "target reports committed data");
                await Capture(wizard, output, "03-target-receipt-and-owner");
                Field(wizard, "SetupOwnerUsername").Text = "admin";
                EnterOwner(wizard);
                await Until(() => transfer.IsEffectivelyEnabled, "original owner reached target workspace");
                Check(Find<ComboBox>(main, "SchoolPicker").SelectedItem is School school && school.Id == access.SchoolId,
                    "target preserves the original school ID");
                Check(!Find<Border>(main, "FirstSchoolClassCard").IsVisible, "restored existing school does not offer creation of a first class");
                await CheckOpeningAvailable("个启用行政班", "04a-restored-school-checklist");
                await Capture(main, output, "04-target-owner-workspace");
            }
            await File.WriteAllTextAsync(Path.Combine(output, firstSchool ? "windows-first-school.json" : "windows-transfer.json"),
                JsonSerializer.Serialize(new { firstSchool, passed = true }));
            desktop.Shutdown(0);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            await CaptureWithFrame(main, output, "failure");
            desktop.Shutdown(1);
        }

        SchoolConnectionSetupView OpenConnection()
        {
            Click(Find<Button>(main, "OpenFirstSchoolSetup"));
            var view = Find<SchoolConnectionSetupView>(main, "SchoolConnectionSetup");
            Check(view.IsEffectivelyVisible, "persistent entry opens in the main window");
            return view;
        }
        async Task CheckConnection(SchoolConnectionSetupView view, string status)
        {
            Click(Find<Button>(view, "SetupCheckConnection"));
            await Until(() => Ready(view, "SetupCheckConnection") && Text(view, "SetupConnectionStatus").Contains(status), status);
        }
        async Task<SchoolSetupView> OpenSetup(SchoolConnectionSetupView view, string buttonName)
        {
            await Until(() => Ready(view, buttonName), "server exposes " + buttonName);
            Click(Find<Button>(view, buttonName));
            var wizard = await AcademicPageAsync<SchoolSetupView>(main);
            EnterKey(wizard);
            return wizard;
        }
        void EnterKey(SchoolSetupView wizard)
        {
            Field(wizard, "SetupKey").Text = access.SetupKey;
            Click(Find<Button>(wizard, "SetupPrimary"));
        }
        static bool Ready(Control root, string name) =>
            Find<Button>(root, name) is { IsEffectivelyEnabled: true, IsEffectivelyVisible: true };
        static string Text(Control root, string name) => Find<TextBlock>(root, name).Text ?? "";
        static TextBox Field(Control root, string name) => Find<TextBox>(root, name);
        void EnterOwner(SchoolSetupView wizard)
        {
            Field(wizard, "SetupOwnerPin").Text = access.Pin;
            Click(Find<Button>(wizard, "SetupPrimary"));
        }
        async Task SignOut()
        {
            var signOut = main.GetVisualDescendants().OfType<Button>().Single(button =>
                button.IsEffectivelyVisible && button.IsEffectivelyEnabled && button.Content is "退出登录");
            Click(signOut);
            await Until(() => Ready(main, "OpenFirstSchoolSetup"), "signed out to the setup entry");
        }
        async Task CheckOpeningAvailable(string expected, string name)
        {
            var overview = Find<SchoolOverviewView>(main, "SchoolOverviewWorkspace");
            Check(Ready(overview, "OpenSchoolOpeningButton"), "school overview can continue school opening");
            Click(Find<Button>(overview, "OpenSchoolOpeningButton"));
            var opening = await AcademicPageAsync<SchoolOpeningView>(main);
            await Until(() => Text(opening, "OpeningSummary").Contains(expected) &&
                (firstSchool || !Text(opening, "OpeningSummary").EndsWith(" 0 个启用行政班")), "first or restored school opening summary");
            await CaptureWithFrame(main, output, name);
            Click(Find<Button>(opening, "OpeningBack"));
            await Until(() => Ready(overview, "OpenSchoolOpeningButton"), "returns to the new or restored school overview");
        }
    }

    private static async Task WaitForHeldReplyAsync(string checkpoint)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        for (var attempt = 0; attempt < 200; attempt++)
        {
            using var response = await http.GetAsync(checkpoint);
            if (response.IsSuccessStatusCode) return;
            await Task.Delay(35);
        }
        throw new TimeoutException("The isolated fixture never held the response for " + new Uri(checkpoint).AbsolutePath);
    }
}
