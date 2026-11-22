using System.Net.Http.Json;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private sealed record ProfileAccess(string Origin, string SchoolId, string SchoolCode, string OtherSchoolId,
        string Pin, string NewPin, string ReplacementPassword, string AccountId, bool Isolated);

    private static int StartSchoolProfile(string fixture, string output)
    {
        var access = JsonSerializer.Deserialize<ProfileAccess>(File.ReadAllText(fixture), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Expected the isolated school profile fixture.");
        Check(access.Isolated && IsLoopback(access.Origin), "Policy writes require the loopback fixture.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifySchoolProfileAsync(access, output); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifySchoolProfileAsync(ProfileAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        var schools = Find<ComboBox>(main, "SchoolPicker");
        using var checkpoints = new HttpClient {BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromMinutes(2)};
        try
        {
            main.Width = 1160;
            main.Height = 780;
            Find<TextBox>(main, "ServerAddress").Text = access.Origin;
            Find<TextBox>(main, "SchoolCode").Text = access.SchoolCode;
            Find<TextBox>(main, "Username").Text = "admin";
            Find<TextBox>(main, "Password").Text = access.Pin;
            Click(Find<Button>(main, "LoginButton"));
            await SelectSchool(access.SchoolId);
            var management = await OpenManagement();
            var profile = await OpenProfile(management);
            profile.Width = 1160;
            profile.Height = 780;
            Check(Find<RadioButton>(profile, "ProfileSharedMode").IsChecked == true, "current shared-password strategy is shown");
            Find<RadioButton>(profile, "ProfilePinMode").IsChecked = true;
            await Review(profile);
            var impact = Find<TeacherLoginImpactView>(profile, "ProfileImpact");
            var rows = Find<ListBox>(impact, "TeacherLoginImpactRows");
            Check(rows.Items.OfType<TeacherLoginImpactRow>().Any(row => row.Id == access.AccountId &&
                row.NextLogin.Contains("PIN") && row.Action is not null), "only-shared-password teacher has an actionable missing PIN");
            var filter = Find<ComboBox>(impact, "TeacherLoginImpactFilter");
            filter.SelectedIndex = 3;
            Check(rows.Items.OfType<TeacherLoginImpactRow>().Single().Id == access.AccountId &&
                Find<TextBlock>(impact, "TeacherLoginImpactCounts").Text?.Contains("1 项需要处理") == true,
                "the attention filter displays the actual rows in the summary");
            filter.SelectedIndex = 0;
            await Capture(profile, output, "01-wide-policy-preview");
            await Signal("previewed");
            Click(Primary(profile));
            await Until(() => Notice(profile).Contains("草稿") && Primary(profile).IsEffectivelyEnabled, "stale preview preserves the input");
            Check(Find<RadioButton>(profile, "ProfilePinMode").IsChecked == true &&
                Find<TextBox>(profile, "ProfileName").Text == "新版网页已更新校名", "Edge rename is retained and the intended policy is preserved");
            await Capture(profile, output, "02-edge-conflict");
            await Review(profile);
            Click(Primary(profile));
            await Saved(profile);
            await Signal("switched");

            profile.Width = 840;
            profile.Height = 480;
            await Until(() => profile.ClientSize is { Width: <= 845, Height: <= 485 }, "actual compact desktop window");
            await Until(() => rows.Items.OfType<TeacherLoginImpactRow>().Any(row => row.Id == access.AccountId),
                "refresh kept the exact target");
            Check(impact.FocusTarget("ACCOUNT", access.AccountId), "the affected teacher can be focused in the narrow preview");
            var openTarget = Find<Button>(impact, "TeacherLoginImpactOpen");
            await Until(() => openTarget.IsEffectivelyEnabled, "affected account action is enabled");
            await Capture(profile, output, "03-compact-missing-pin");
            Click(openTarget);
            var accounts = await WindowOf<TeacherAccountsWindow>();
            var reset = Find<Button>(accounts, "ResetTeacherPin");
            await Until(() => reset.IsEffectivelyVisible && reset.IsEffectivelyEnabled, "exact teacher opens ordinary PIN workflow");
            Click(reset);
            var resetDialog = await WindowOf<TeacherAccountForm>();
            var pin = Find<TextBox>(resetDialog, "TeacherFormPin");
            await Until(() => pin.IsEffectivelyEnabled && pin.IsEffectivelyVisible, "ordinary PIN field available");
            pin.Text = access.NewPin;
            Click(Find<Button>(resetDialog, "TeacherFormSave"));
            await Until(() => !resetDialog.IsVisible || Find<TextBlock>(resetDialog, "TeacherFormNotice").Text?.Contains("已") == true,
                "individual PIN write completed");
            if (resetDialog.IsVisible) resetDialog.Close();
            accounts.Close();
            await Signal("pin-set");
            await Until(() => Find<Button>(profile, "ProfileCompactEditor").IsEffectivelyEnabled, "policy returns after individual account work");
            Click(Find<Button>(profile, "ProfileCompactEditor"));

            Find<RadioButton>(profile, "ProfileSharedMode").IsChecked = true;
            Find<CheckBox>(profile, "ProfileRotate").IsChecked = true;
            Find<TextBox>(profile, "ProfilePassword").Text = access.ReplacementPassword;
            await Review(profile);
            Click(Primary(profile));
            await Saved(profile);
            Check(Find<TextBox>(profile, "ProfilePassword").Text == "", "temporary password disappears after original receipt is read");
            Check(Notice(profile).Contains("已核对"), "lost reply is resolved by the original receipt");
            await Capture(profile, output, "04-compact-rotation-receipt");
            await Signal("rotated");
            await Signal("hold-old-preview");
            Click(Find<Button>(profile, "ProfileRefresh"));
            await Signal("old-preview-in-flight");
            Click(Find<Button>(profile, "ProfileClose"));
            management.Close();
            await Until(() => schools.IsEffectivelyEnabled, "school selector restored");
            await SelectSchool(access.OtherSchoolId);
            management = await OpenManagement();
            profile = await OpenProfile(management);
            await Signal("release-old-preview");
            await Task.Delay(250);
            Check(Find<RadioButton>(profile, "ProfilePinMode").IsChecked == true, "school without an active term has independent settings");
            Check(Find<TextBlock>(profile, "ProfileScope").Text?.Contains("另一所学校") == true,
                "prior school preview does not overwrite the new school");
            await Capture(profile, output, "05-other-school-no-term");
            await Signal("no-term");
            await Signal("withdrew-other-school");
            Click(Find<Button>(profile, "ProfileRefresh"));
            await Until(() => string.IsNullOrEmpty(Find<TextBox>(profile, "ProfileName").Text) &&
                !Primary(profile).IsEffectivelyEnabled && Notice(profile).Length > 0,
                "a rejected refresh withdraws the previous school's editable settings");
            await File.WriteAllTextAsync(Path.Combine(output, "windows-profile.json"), JsonSerializer.Serialize(new {
                wide = "1160x780", compact = "840x480", edgeConflictKeptDraft = true, exactAccountOpened = true,
                lostReplyReadOriginalReceipt = true, noTermOtherSchool = true, priorSchoolLateReplyIgnored = true, revokedSettingsCleared = true
            }, new JsonSerializerOptions {WriteIndented = true}));
            desktop.Shutdown(0);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            desktop.Shutdown(1);
        }

        async Task SelectSchool(string schoolId)
        {
            await Until(() => schools.Items.OfType<School>().Any(school => school.Id == schoolId), "real management login lists the authorized school");
            schools.SelectedItem = schools.Items.OfType<School>().Single(school => school.Id == schoolId);
        }
        async Task<SchoolManagementAccountsWindow> OpenManagement()
        {
            var button = Find<Button>(main, "OpenSchoolManagementAccountsButton");
            await Until(() => button.IsEffectivelyVisible && button.IsEffectivelyEnabled, "accounts and permissions available");
            Click(button);
            return await WindowOf<SchoolManagementAccountsWindow>();
        }
        async Task Signal(string point)
        {
            using var result = await checkpoints.PostAsJsonAsync("/__profile-test/" + point, new { });
            result.EnsureSuccessStatusCode();
        }
    }

    private static Button Primary(SchoolProfileWindow window) => Find<Button>(window, "ProfilePrimary");
    private static string Notice(SchoolProfileWindow window) => Find<TextBlock>(window, "ProfileNotice").Text ?? "";

    private static async Task<T> WindowOf<T>() where T : Window
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        await Until(() => desktop.Windows.OfType<T>().Any(w => w.IsVisible), typeof(T).Name + " opened");
        return desktop.Windows.OfType<T>().Single(w => w.IsVisible);
    }

    private static async Task<SchoolProfileWindow> OpenProfile(SchoolManagementAccountsWindow window)
    {
        var action = Find<Button>(window, "ManagementSchoolProfile");
        await Until(() => action.IsEffectivelyEnabled && action.IsEffectivelyVisible, "teacher login setting is reachable from management");
        Click(action);
        var profile = await WindowOf<SchoolProfileWindow>();
        await Until(() => Find<TextBox>(profile, "ProfileName").IsEffectivelyEnabled, "school profile loaded");
        return profile;
    }

    private static async Task Review(SchoolProfileWindow window)
    {
        await Until(() => Primary(window).Content?.ToString() == "预览教师影响" && Primary(window).IsEffectivelyEnabled,
            "preview the current draft");
        Click(Primary(window));
        await Until(() => Primary(window).Content?.ToString() == "保存学校设置" && Primary(window).IsEffectivelyEnabled,
            "reviewed profile may be confirmed");
    }

    private static Task Saved(SchoolProfileWindow window) => Until(() =>
        (Notice(window).Contains("服务器已确认本次设置") || Notice(window).Contains("已核对本次口令设置已保存")) &&
        !Primary(window).IsEffectivelyEnabled,
        "server confirmed the saved profile");
}
