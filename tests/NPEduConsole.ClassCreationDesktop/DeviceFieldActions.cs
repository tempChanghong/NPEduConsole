using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private sealed record DeviceFieldAccess(string Origin, string SchoolCode, string SchoolId, string Pin, string RoomId,
        string FrontId, string RearId, string FrontDeviceId, string RearDeviceId, bool AllowWrites, bool WithReports,
        string? FrontReportId, string? OtherSchoolId);

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
            if (access.WithReports)
            {
                var reportsBeforeEnd = await LinkedReports(window, "尚未读到同一会话的结束报告");
                var old = Find<ListBox>(reportsBeforeEnd, "RunReportRows");
                Check(old.Items.Count == 1 && old.SelectedItem is null, "old rear report is visible but never automatically linked");
                await Capture(reportsBeforeEnd, output, "07-wide-no-new-ended-report");
                await PressScoped(reportsBeforeEnd, "RunReportBack");
            }
            await Signal("sample-shown");
            await Signal("second-stop");
            await PressScoped(window, "FieldRead");
            await Until(() => Read(window, "FieldResumeReceipt").Contains("本次采样为历史证据") &&
                Read(window, "FieldResumeReason").Contains("已停止"), "second local stop retained when original request is replayed");

            if (access.WithReports)
            {
                var ended = await LinkedReports(window, "已定位此次处置对应的结束报告");
                var row = (DeviceRunReportRow)Find<ListBox>(ended, "RunReportRows").SelectedItem!;
                Check(row.Report.Ownership?.ClassId == access.RoomId && row.Report.Summary.SampledSeconds > 0, "ended native report has exact class and sampling");
                await Capture(ended, output, "08-wide-resume-linked-report");
                await PressScoped(ended, "RunReportRecords");
                var exitRecords = await Open<StudyRecordsWindow>();
                await Until(() => Read(exitRecords, "RecordsStatus").Contains("已读取 1 条"), "only the matched capture's one authorized exit record is listed");
                exitRecords.Close();
                ended.Width = 840;
                ended.Height = 480;
                await Until(() => ended.ClientSize is { Width: <= 845, Height: <= 485 }, "narrow report window");
                await Capture(ended, output, "09-narrow-resume-linked-report");
                await PressScoped(ended, "RunReportBack");
                var reportList = Find<ListBox>(ended, "RunReportRows");
                await Until(() => reportList.IsEffectivelyVisible && reportList.IsKeyboardFocusWithin && ReferenceEquals(reportList.SelectedItem, row),
                    "narrow report returns to the selected row and focus");
                await PressScoped(ended, "RunReportBack");
            }

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
            if (access.WithReports)
            {
                var historic = await LinkedReports(window, "已定位此次处置对应的结束报告");
                Check(Read(historic, "RunReportTarget").Contains(access.RearDeviceId) && Read(historic, "RunReportFeedback").Contains("设备报告读取失败"),
                    "replacement leaves old class-owned exact report accessible without reading revoked device history");
                await Capture(historic, output, "10-historical-replaced-rear");
                historic.Close();
            }
            await PressScoped(window, "FieldReturn");
            await Until(() => !window.IsVisible && (Find<Button>(main, "OpenScreenFieldButton").IsFocused || main.GetVisualDescendants().OfType<Control>().Any(control =>
                control.IsFocused && AutomationProperties.GetAutomationId(control) == "SchoolScreen-" + access.RearId)), "return focus to the original screen after revocation");

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
            var reportDevices = Find<ComboBox>(evidence, "DevicePicker");
            await Until(() => reportDevices.Items.Any(item => item?.ToString()?.Contains("七班前屏") == true), "front screen listed under its own binding");
            reportDevices.SelectedItem = reportDevices.Items.Single(item => item?.ToString()?.Contains("七班前屏") == true);
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
            if (access.WithReports)
            {
                seating.Close();
                calendar.Close();
                await PressScoped(main, "OpenDeviceDirectoryButton");
                await Until(() => search.IsEffectivelyVisible, "return to device directory");
                search.Text = "七班前屏";
                await Until(() => (details = list.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.IsEffectivelyVisible &&
                    button.Tag is OverviewScreenItem item && item.ScreenId == access.FrontId)) is not null, "independent front screen action");
                Click(details!);
                await PressScoped(main, "OpenScreenReportsButton");
                var frontReports = await Open<DeviceRunReportsWindow>();
                await Until(() => Find<ListBox>(frontReports, "RunReportRows").Items.OfType<DeviceRunReportRow>().Any(row =>
                    row.Report.SessionId == access.FrontReportId && row.Report.Estimate?.Status == "DEVICE_CHANGED" && row.Report.Timing?.ClockDiscontinuity == true),
                    "front sample fixture shows lost reference and clock discontinuity separately");
                await Capture(frontReports, output, "11-front-invalidated-reference");
                frontReports.Width = 840;
                frontReports.Height = 480;
                await Until(() => frontReports.ClientSize is { Width: <= 845, Height: <= 485 }, "narrow clock report");
                var clockRows = Find<ListBox>(frontReports, "RunReportRows");
                var clockRow = clockRows.Items.OfType<DeviceRunReportRow>().Single(row => row.Report.SessionId == access.FrontReportId);
                clockRows.SelectedItem = clockRow;
                Check(clockRow.Title.StartsWith("报告开始 ") && clockRow.Subtitle.Contains("设备时钟跳变"), "list identifies reported time and clock discontinuity");
                await Capture(frontReports, output, "11a-narrow-clock-list");
                await PressScoped(frontReports, "RunReportOpen");
                var clockDetail = Find<DeviceRunReportDetail>(frontReports, "RunReportDetail");
                Check(Read(clockDetail, "ReportWindow").StartsWith("报告起止（结束按设备计时推算）") &&
                    Read(clockDetail, "ReportHints").Contains("报告起止时间仍待核对") &&
                    Read(clockDetail, "ReportTrace").Contains("服务器收到于") && Read(clockDetail, "ReportTrace").Contains("设备墙钟观测结束"),
                    "reported interval, server receipt and device wall clock are named separately");
                await Capture(frontReports, output, "11b-narrow-clock-detail");
                await Signal("report-revoke");
                if (Ready(frontReports, "RunReportRefresh")) await PressScoped(frontReports, "RunReportRefresh");
                await Until(() => Read(frontReports, "RunReportTarget").Contains("报告访问已失效") &&
                    Find<ListBox>(frontReports, "RunReportRows").Items.Count == 0 && Find<DeviceRunReportDetail>(frontReports, "RunReportDetail").Report is null,
                    "revocation clears the report contents and current device information");
                await Capture(frontReports, output, "11c-report-access-revoked");
                await Signal("report-restored");
                frontReports.Close();
                await PressScoped(main, "OpenDeviceDirectoryButton");
                await Until(() => search.IsEffectivelyVisible, "export from the ordinary directory");
                search.Text = "诊断批次";
                await PressScoped(directory, "DeviceDiagnosticExport");
                var diagnostic = await Open<DeviceDiagnosticsWindow>();
                diagnostic.Width = 840;
                diagnostic.Height = 480;
                await Until(() => Read(diagnostic, "DiagnosticLoadState").Contains("匹配 205 台，已加载 100 台"), "filtered first page loaded");
                await PressScoped(diagnostic, "DiagnosticReview");
                Check(Read(diagnostic, "DiagnosticCounts").Contains("实际导出 100 台") && Read(diagnostic, "DiagnosticObservation").Contains("仍有下一页"),
                    "partial-page confirmation matches the file");
                await Capture(diagnostic, output, "12-narrow-partial-export-preview");
                await SaveDiagnostic(diagnostic, "diagnostics-partial.json", 100);
                await PressScoped(diagnostic, "DiagnosticReturn");
                await PressScoped(diagnostic, "DiagnosticMore");
                await Until(() => Read(diagnostic, "DiagnosticLoadState").Contains("已加载 200 台"), "second page loaded");
                await PressScoped(diagnostic, "DiagnosticMore");
                await Until(() => Read(diagnostic, "DiagnosticLoadState").Contains("已加载 205 台"), "third page loaded");
                await PressScoped(diagnostic, "DiagnosticReview");
                Check(Read(diagnostic, "DiagnosticCounts").Contains("实际导出 200 台") && Read(diagnostic, "DiagnosticCounts").Contains("5 台不写入"),
                    "hard cap is visible before saving");
                await Capture(diagnostic, output, "13-narrow-capped-export-preview");
                await SaveDiagnostic(diagnostic, "diagnostics-capped.json", 200);
                bool pickerOpened = false;
                diagnostic.PickLocation = _ => { pickerOpened = true; return Task.FromResult<IStorageFile?>(null); };
                await Signal("export-revoke");
                if (Ready(diagnostic, "DiagnosticSave")) await PressScoped(diagnostic, "DiagnosticSave");
                await Until(() => !Ready(diagnostic, "DiagnosticSave") && Read(diagnostic, "DiagnosticSchool").Contains("访问已失效"), "revocation clears the frozen range");
                Check(!pickerOpened && Find<ListBox>(diagnostic, "DiagnosticRows").Items.Count == 0, "no revoked preview, picker or export remains");
                await Capture(diagnostic, output, "14-narrow-access-revoked");
                await Signal("export-restored");
                diagnostic.Close();

                await PressScoped(directory, "DeviceDiagnosticExport");
                diagnostic = await Open<DeviceDiagnosticsWindow>();
                await Until(() => Ready(diagnostic, "DiagnosticRead"), "initial export refresh complete");
                Find<TextBox>(diagnostic, "DiagnosticSearch").Text = "延迟诊断";
                await PressScoped(diagnostic, "DiagnosticRead");
                await Signal("export-begin-switch");
                schools.SelectedItem = schools.Items.OfType<School>().Single(item => item.Id == access.OtherSchoolId);
                await Until(() => !diagnostic.IsVisible, "school switch closes the original diagnostic window");
                await Signal("export-finish-switch");
                await PressScoped(main, "OpenDeviceDirectoryButton");
                await PressScoped(directory, "DeviceDiagnosticExport");
                diagnostic = await Open<DeviceDiagnosticsWindow>();
                await Until(() => Read(diagnostic, "DiagnosticSchool").Contains("另一所学校") &&
                    Read(diagnostic, "DiagnosticLoadState").Contains("匹配 0 台"), "late response never repopulates the new school export");
                Console.WriteLine("PASS: exact report before/after upload, replacement history, reference loss, narrow focus, actual JSON saves, revocation and late school result.");
            }
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
        async Task<DeviceRunReportsWindow> LinkedReports(DeviceFieldActionWindow field, string feedback)
        {
            await PressScoped(field, "FieldReports");
            var reports = await Open<DeviceRunReportsWindow>();
            await Until(() => Read(reports, "RunReportFeedback").Contains(feedback), feedback);
            return reports;
        }
        async Task SaveDiagnostic(DeviceDiagnosticsWindow diagnostic, string name, int count)
        {
            string file = Path.GetFullPath(Path.Combine(output, name));
            await File.WriteAllBytesAsync(file, []);
            diagnostic.PickLocation = _ => diagnostic.StorageProvider.TryGetFileFromPathAsync(new Uri(file));
            await PressScoped(diagnostic, "DiagnosticSave");
            await Until(() => Read(diagnostic, "DiagnosticNotice").Contains($"已保存 {count} 台"), "JSON saved through the directory export action");
            using var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(file));
            Check(saved.RootElement.GetProperty("items").GetArrayLength() == count, "written JSON row count matches confirmation");
        }
        static bool Ready(Control control, string name) => Find<Button>(control, name) is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true };
    }
}
