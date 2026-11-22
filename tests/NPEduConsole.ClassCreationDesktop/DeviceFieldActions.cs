using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private sealed record DeviceFieldAccess(string Origin, string SchoolCode, string SchoolId, string Pin, string RoomId,
        string FrontId, string RearId, string FrontDeviceId, string RearDeviceId, bool AllowWrites);

    private static int StartDeviceFieldActions(string path, string output)
    {
        var access = JsonSerializer.Deserialize<DeviceFieldAccess>(File.ReadAllText(path), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Expected isolated field-action fixture.");
        Check(access.AllowWrites && IsLoopback(access.Origin), "Field action writes require a disposable loopback fixture.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => _ = VerifyDeviceFieldActionsAsync(access, output)))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyDeviceFieldActionsAsync(DeviceFieldAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        using var control = new HttpClient { BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(90) };
        try
        {
            main.Width = 1160;
            main.Height = 780;
            Find<TextBox>(main, "ServerAddress").Text = access.Origin;
            Find<TextBox>(main, "SchoolCode").Text = access.SchoolCode;
            Find<TextBox>(main, "Username").Text = "admin";
            Find<TextBox>(main, "Password").Text = access.Pin;
            await PressScoped(main, "LoginButton");
            var schools = Find<ComboBox>(main, "SchoolPicker");
            await Until(() => schools.Items.OfType<School>().Any(item => item.Id == access.SchoolId), "fixture school available");
            schools.SelectedItem = schools.Items.OfType<School>().Single(item => item.Id == access.SchoolId);
            await PressScoped(main, "OpenDeviceDirectoryButton");
            var directory = Find<SchoolDeviceDirectoryView>(main, "DeviceDirectory");
            var search = Find<TextBox>(directory, "DeviceSearch");
            await Until(() => search.IsEffectivelyVisible, "usual device directory");
            search.Text = "七班后屏";
            var list = Find<ListBox>(directory, "DeviceDirectoryRows");
            await Until(() => list.Items.OfType<OverviewScreenItem>().Any(item => item.ScreenId == access.RearId), "exact rear binding found");
            var rear = list.Items.OfType<OverviewScreenItem>().Single(item => item.ScreenId == access.RearId);
            Button? details = null;
            await Until(() => (details = list.GetVisualDescendants().OfType<Button>().FirstOrDefault(button =>
                button.IsEffectivelyVisible && ReferenceEquals(button.Tag, rear))) is not null, "clickable rear detail");
            Click(details!);
            await Signal("arm");
            await PressScoped(main, "OpenScreenFieldButton");
            var window = await FieldWindow();
            window.Width = 1160;
            window.Height = 780;
            await Until(() => Ready(window, "FieldResume") && Read(window, "FieldIdentifiers").Contains(access.RearDeviceId), "fixed rear device and eligible stopped window");
            await Capture(window, output, "01-wide-rear-preflight");
            await PressScoped(window, "FieldResume");
            await Until(() => Read(window, "FieldResumeReceipt").Contains("等待设备处理"), "school accepted without device processing");
            await Capture(window, output, "02-wide-queued");
            await Signal("received");
            await Until(() => Read(window, "FieldResumeReceipt").Contains("新鲜定时采样仍待核对"), "receiver handled command without a fresh signal");
            await Capture(window, output, "03-wide-handled-without-sampling");
            await Signal("processed");
            await Until(() => Read(window, "FieldResumeReceipt").Contains("核对时仍有本次恢复的采样信号"), "same native rear window reports synthetic signal");
            await Capture(window, output, "04-wide-fresh-sampling");
            await Until(() => Read(window, "FieldResumeReceipt").Contains("本次采样为历史证据") &&
                Read(window, "FieldResumeReason").Contains("当前待核对"), "the unchanged open window locally ages the last live observation into history");
            await Signal("sample-shown");
            await Signal("second-stop");
            await PressScoped(window, "FieldRead");
            await Until(() => Read(window, "FieldResumeReceipt").Contains("本次采样为历史证据") &&
                Read(window, "FieldResumeReason").Contains("已停止"), "second local stop retained when original request is replayed");

            window.Width = 840;
            window.Height = 480;
            await Until(() => window.ClientSize is { Width: <= 845, Height: <= 485 }, "actual 840 x 480 window");
            var confirmation = Find<DeviceFieldActionConfirmation>(window, "FieldConfirmation");
            var cancel = Find<Button>(confirmation, "DismissButton");
            await PressScoped(window, "FieldRevoke");
            await Until(() => confirmation.IsPresenting && cancel.IsFocused, "confirmation defaults to cancel");
            cancel.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            await Until(() => !confirmation.IsPresenting && Find<Button>(window, "FieldRevoke").IsFocused, "Escape restores original authorization button focus");
            await PressScoped(window, "FieldRevoke");
            await Until(() => confirmation.IsPresenting && cancel.IsFocused, "narrow confirmation available without changing target");
            Check(confirmation.Target.Contains(access.RearDeviceId) && confirmation.Target.Contains("七班后屏"), "fixed confirmation identity");
            await Capture(window, output, "05-narrow-revoke-confirmation");
            await PressScoped(confirmation, "ApproveButton");
            await Until(() => Read(window, "FieldRevokeReceipt").Contains("学校已撤销原生授权") && !Ready(window, "FieldRevoke"),
                "lost success acknowledgement read back without resending");
            await Signal("revoked");
            Find<TextBlock>(window, "FieldRevokeReceipt").BringIntoView();
            await Capture(window, output, "06-narrow-revoke-receipt");
            await PressScoped(window, "FieldReturn");
            await Until(() => !window.IsVisible && !Ready(main, "OpenScreenFieldButton") && main.GetVisualDescendants().OfType<Control>().Any(control =>
                control.IsFocused && AutomationProperties.GetAutomationId(control) == "SchoolScreen-" + access.RearId), "return focus to the original screen after revocation");

            await PressScoped(main, "OpenClassWorkspaceButton");
            await PressScoped(main, "OpenSchoolStudyButton");
            var calendar = await Open<SchoolStudyWindow>();
            var rooms = Find<ListBox>(calendar, "StudyRooms");
            await Until(() => rooms.Items.OfType<SchoolStudyRoomRow>().Any(item => item.Room.ClassId == access.RoomId), "report calendar contains the original class ID");
            rooms.SelectedItem = rooms.Items.OfType<SchoolStudyRoomRow>().Single(item => item.Room.ClassId == access.RoomId);
            if (Find<Button>(calendar, "StudyRoomOpenButton").IsEffectivelyVisible) await PressScoped(calendar, "StudyRoomOpenButton");
            await PressScoped(calendar, "StudyReportsButton");
            await Until(() => desktop.Windows.Any(item => item.IsVisible && item.Title == "本日场次与回执"), "regular report entry");
            var reports = desktop.Windows.Single(item => item.IsVisible && item.Title == "本日场次与回执");
            var evidence = reports.GetVisualDescendants().OfType<StudyEvidenceView>().Single();
            await PressScoped(evidence, "StudyEvidenceField");
            var front = await FieldWindow();
            await Until(() => Read(front, "FieldIdentifiers").Contains(access.FrontDeviceId) &&
                Read(front, "FieldAuthorizationState").Contains("授权有效"), "reports target the unaffected front device");
            await PressScoped(front, "FieldReturn");
            await Until(() => Find<Button>(evidence, "StudyEvidenceField").IsFocused, "report position and focus returned");
            reports.Close();
            await PressScoped(calendar, "StudySeatingButton");
            var seating = await Open<SchoolStudySeatingWindow>();
            Find<TabControl>(seating, "SeatTabs").SelectedIndex = 1;
            var screens = Find<ListBox>(seating, "SeatDevices");
            Button? frontAction = null;
            await Until(() => (frontAction = screens.GetVisualDescendants().OfType<Button>().FirstOrDefault(button =>
                Equals(button.Content, "现场处置") && button.IsEffectivelyVisible && button.IsEffectivelyEnabled && button.DataContext is StudySeatingDeviceView row &&
                row.Registration.BindingId == access.FrontId)) is not null, "per-screen evidence opens the front binding by ID");
            Click(frontAction!);
            front = await FieldWindow();
            await Until(() => Read(front, "FieldIdentifiers").Contains(access.FrontDeviceId) &&
                Read(front, "FieldAuthorizationState").Contains("授权有效"), "per-screen evidence links the unaffected front device");
            await PressScoped(front, "FieldReturn");
            await Until(() => frontAction!.IsFocused, "per-screen row focus returned");
            Console.WriteLine("PASS: Windows device, report and per-screen evidence entries, distinct accepted, handled and fresh sampling checkpoints, second local stop and lost revoke receipt.");
            Environment.ExitCode = 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (desktop.Windows.LastOrDefault(window => window.IsVisible) is { } window) await Capture(window, output, "failure");
            Environment.ExitCode = 1;
        }
        finally { desktop.Shutdown(Environment.ExitCode); }

        async Task Signal(string stage)
        {
            using var response = await control.PostAsync("/__field/console/" + stage, null);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(stage + " → " + await response.Content.ReadAsStringAsync());
        }

        async Task<T> Open<T>() where T : Window
        {
            await Until(() => desktop.Windows.OfType<T>().Any(window => window.IsVisible), "open " + typeof(T).Name);
            return desktop.Windows.OfType<T>().Single(window => window.IsVisible);
        }
        Task<DeviceFieldActionWindow> FieldWindow() => Open<DeviceFieldActionWindow>();
        static bool Ready(Control control, string name) => Find<Button>(control, name) is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true };
    }
}
