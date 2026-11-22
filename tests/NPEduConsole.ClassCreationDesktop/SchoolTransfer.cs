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
        string? TargetOrigin, string? File, string? Passphrase, string? SchoolId, string? FrontId, string? RearId);

    private static int StartSchoolTransfer(string fixture, string output, bool firstSchool)
    {
        var access = JsonSerializer.Deserialize<TransferAccess>(File.ReadAllText(fixture), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Expected an isolated transfer fixture.");
        Check(access.Isolated && IsLoopback(access.SourceOrigin) && (firstSchool || IsLoopback(access.TargetOrigin!)),
            "First school and migration tests require loopback isolation.");
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
                var wizard = await OpenSetup("InitializeNewSchool");
                await Until(() => Field(wizard, "SetupSchoolCode").IsVisible, "school fields after valid key");
                await Capture(wizard, output, "01-first-school-wide");
                Field(wizard, "SetupSchoolName").Text = "Console 新建学校";
                Field(wizard, "SetupSchoolCode").Text = access.SchoolCode;
                Field(wizard, "SetupAdministratorName").Text = "首位所有者";
                Field(wizard, "SetupUsername").Text = "founder";
                Field(wizard, "SetupPin").Text = access.Pin;
                Field(wizard, "SetupTermName").Text = "首个启用学期";
                Click(Find<Button>(wizard, "SetupPrimary"));
                await Until(() => Find<Border>(wizard, "SetupOwnerPanel").IsVisible &&
                    Find<Button>(wizard, "SetupPrimary").IsEffectivelyEnabled, "school exists but awaits real owner login");
                await Capture(wizard, output, "02-first-school-written");
                wizard.Close();
                wizard = await OpenSetup("ResumeSchoolSetup");
                await Until(() => Find<Border>(wizard, "SetupOwnerPanel").IsEffectivelyVisible &&
                    Find<Button>(wizard, "SetupPrimary").IsEffectivelyEnabled, "resume the originally written school");
                EnterOwner(wizard);
                await Until(() => Find<Button>(main, "OpenClassWorkspaceButton").IsEffectivelyEnabled, "manager workspace opens");
                Click(Find<Button>(main, "OpenClassWorkspaceButton"));
                var create = Find<Button>(main, "OpenClassCreationButton");
                await Until(() => create.IsEffectivelyVisible && create.IsEffectivelyEnabled, "school can create initial classes");
                Click(create);
                var dialog = await WindowOf<ClassCreationWindow>();
                var openGrade = Find<Button>(dialog, "OpenGradeCreationButton");
                await Until(() => openGrade.IsEffectivelyEnabled, "new school can create its first grade");
                Click(openGrade);
                Find<TextBox>(dialog, "NewGradeCode").Text = "G1";
                Find<TextBox>(dialog, "NewGradeName").Text = "高一";
                Click(Find<Button>(dialog, "SubmitGradeCreationButton"));
                await Until(() => Find<ComboBox>(dialog, "CreationGrade").SelectedItem is ClassCreationGrade { Code: "G1" },
                    "created grade selected by its server ID");
                Find<TextBox>(dialog, "CreationName").Text = "高一（1）班";
                Find<TextBox>(dialog, "CreationCode").Text = "G1-1";
                Click(Find<Button>(dialog, "SubmitClassCreationButton"));
                var roster = await WindowOf<ClassRosterWindow>();
                await Until(() => Find<Grid>(roster, "RosterEditorPane").IsEffectivelyVisible, "first class opens the standard roster");
                await Capture(roster, output, "03-first-roster");
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
                await Until(() => loginAddress.IsEffectivelyVisible, "can change to target origin after sign out");
                loginAddress.Text = access.TargetOrigin;
                var wizard = await OpenSetup("RestoreSchoolTransfer");
                wizard.Width = 840;
                wizard.Height = 480;
                await Until(() => Find<Button>(wizard, "SetupChoosePackage").IsEffectivelyVisible &&
                    wizard.ClientSize is { Width: <= 845, Height: <= 485 }, "compact target restoration wizard");
                Click(Find<Button>(wizard, "SetupChoosePackage"));
                await Until(() => Find<TextBlock>(wizard, "SetupFileName").Text?.EndsWith(".npcw-transfer") == true, "exact package chosen");
                Field(wizard, "SetupPackagePassphrase").Text = access.Passphrase;
                Click(Find<Button>(wizard, "SetupPrimary"));
                await Until(() => Find<Button>(wizard, "SetupPrimary").Content is "确认迁入" &&
                    Find<Button>(wizard, "SetupPrimary").IsEffectivelyEnabled, "target verified package and destination");
                Check(Find<TextBlock>(wizard, "SetupServer").Text?.Contains(access.TargetOrigin!) == true, "target origin is visible");
                await Capture(wizard, output, "02-target-preview-compact");
                Click(Find<Button>(wizard, "SetupPrimary"));
                await Until(() => Find<Border>(wizard, "SetupOwnerPanel").IsVisible && Find<Button>(wizard, "SetupPrimary").IsEffectivelyEnabled,
                    "target found the original receipt after the proxy dropped the import response");
                Check(Find<TextBlock>(wizard, "SetupNotice").Text?.Contains("确认这份迁移包写入") == true, "target reports committed data");
                await Capture(wizard, output, "03-target-receipt-and-owner");
                Field(wizard, "SetupOwnerUsername").Text = "admin";
                EnterOwner(wizard);
                await Until(() => transfer.IsEffectivelyEnabled, "original owner reached target workspace");
                Check(Find<ComboBox>(main, "SchoolPicker").SelectedItem is School school && school.Id == access.SchoolId,
                    "target preserves the original school ID");
                await Capture(main, output, "04-target-owner-workspace");
            }
            await File.WriteAllTextAsync(Path.Combine(output, firstSchool ? "windows-first-school.json" : "windows-transfer.json"),
                JsonSerializer.Serialize(new { firstSchool, passed = true }));
            desktop.Shutdown(0);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            desktop.Shutdown(1);
        }

        async Task<SchoolSetupWindow> OpenSetup(string buttonName)
        {
            var button = Find<Button>(main, buttonName);
            await Until(() => button.IsEffectivelyEnabled && button.IsEffectivelyVisible, "server exposes " + buttonName);
            Click(button);
            var wizard = await WindowOf<SchoolSetupWindow>();
            Field(wizard, "SetupKey").Text = access.SetupKey;
            Click(Find<Button>(wizard, "SetupPrimary"));
            return wizard;
        }
        TextBox Field(Window window, string name) => Find<TextBox>(window, name);
        void EnterOwner(SchoolSetupWindow wizard)
        {
            Field(wizard, "SetupOwnerPin").Text = access.Pin;
            Click(Find<Button>(wizard, "SetupPrimary"));
        }
    }
}
