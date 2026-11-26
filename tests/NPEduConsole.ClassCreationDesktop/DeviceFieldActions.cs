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
            Check(ReferenceEquals(TopLevel.GetTopLevel(window), main) && desktop.Windows.Count(item => item.IsVisible) == 1,
                "ordinary field action shares the main window and its three controls");
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
                await Until(() => main.ClientSize.Width >= 1155 && Find<Border>(ended, "RunReportListPane").IsEffectivelyVisible &&
                    Find<Border>(ended, "RunReportDetailPane").IsEffectivelyVisible, "wide report workspace places list and detail side by side");
                var reportRows = Find<ListBox>(ended, "RunReportRows");
                var filter = Find<ComboBox>(ended, "RunReportFilter");
                filter.SelectedIndex = 1;
                var row = (DeviceRunReportRow)reportRows.SelectedItem!;
                Check(row.Report.Ownership?.ClassId == access.RoomId && row.Report.Summary.SampledSeconds > 0, "ended native report has exact class and sampling");
                Check(row.Report.Study is { } study && Read(ended, "RunReportSessionTime").Contains(study.Window.Label) &&
                    Read(Find<DeviceRunReportDetail>(ended, "RunReportDetail"), "ReportWindow").StartsWith("报告起止"),
                    "school period and device report interval are identified independently");
                await CaptureWithFrame(main, output, "08-wide-resume-linked-report");
                var sessionId = row.Report.SessionId;
                var liveToggle = Find<Avalonia.Controls.Primitives.ToggleButton>(ended, "ReportLiveToggle");
                liveToggle.IsChecked = true;
                await Until(() => Find<TextBlock>(ended,"RunReportLive").IsEffectivelyVisible &&
                    Read(ended,"RunReportDevice").Contains(access.RearDeviceId), "current device state identifies the exact rear device");
                liveToggle.IsChecked = false;
                foreach (var fault in new[] { "missing-fragment-fields", "empty-fragment-row", "missing-elapsed-seconds" })
                {
                    await Signal(fault);
                    await PressScoped(ended, "RunReportRecords");
                    await Until(() => Read(ended, "RunReportFeedback").Contains("不兼容") && Ready(ended, "RunReportRecords"),
                        "incomplete archived fragment stays recoverable in the original report window");
                    Check(!Find<ContentControl>(ended, "ReportRecordsHost").IsVisible && !desktop.Windows.OfType<StudyRecordsWindow>().Any(w => w.IsVisible),
                        "an unreadable historical report opens no records");
                    await PressScoped(ended, "RunReportRefresh");
                    await Until(() => Read(ended, "RunReportFeedback").Contains("已定位") && Ready(ended, "RunReportRecords"),
                        "same report can be refreshed after a failed details read");
                    row = (DeviceRunReportRow)Find<ListBox>(ended, "RunReportRows").SelectedItem!;
                    Check(row.Report.SessionId == sessionId, "refresh keeps the originally selected capture session");
                }
                main.Width = 840;
                main.Height = 480;
                await Until(() => main.ClientSize is { Width: <= 845, Height: <= 485 } && !reportRows.IsEffectivelyVisible, "narrow report shows only the selected detail");
                Console.WriteLine($"Report narrow window {main.Width}×{main.Height} DIP at {main.RenderScaling * 100:0}% display scaling.");
                var reportDetail = Find<DeviceRunReportDetail>(ended, "RunReportDetail");
                var scroll = Find<ScrollViewer>(reportDetail, "ReportScroll");
                await Until(() => new[] {"SummarySampled","SummaryElapsed","SummaryCoverage","ReportHints"}.All(name =>
                    FullyInView(Find<TextBlock>(reportDetail, name), scroll)), "three measured values and warnings are fully visible without scrolling");
                await CaptureWithFrame(main, output, "09-narrow-resume-linked-report");
                await Signal("hold-records-read");
                await PressScoped(ended, "RunReportRecords");
                await Signal("records-read-started");
                await PressScoped(ended, "RunReportBack");
                await Signal("release-records-read");
                await Until(() => reportRows.IsEffectivelyVisible && reportRows.IsKeyboardFocusWithin &&
                    !Find<ContentControl>(ended, "ReportRecordsHost").IsVisible, "cancelled records lookup keeps focus in the report list");
                await PressScoped(ended, "RunReportOpen");
                await Until(() => Ready(ended, "RunReportRecords") && !Find<ContentControl>(ended, "ReportRecordsHost").IsVisible,
                    "late historical details do not reopen records");
                var identity = Find<Expander>(reportDetail, "ReportIdentity");
                identity.BringIntoView();
                identity.IsExpanded = true;
                var trace = Find<TextBlock>(reportDetail, "ReportTrace");
                await Until(() => trace.IsEffectivelyVisible && trace.Bounds.Height > 0, "expanded time evidence laid out");
                scroll.PageDown();
                trace.BringIntoView();
                await Until(() => scroll.Offset.Y > 0 && FullyInView(Find<TextBlock>(reportDetail,"ReportTrace"), scroll),
                    "one detail viewport reaches the final time evidence");
                var previousOffset = scroll.Offset;
                var windowHandle = main.TryGetPlatformHandle()!.Handle;
                var windowCount = desktop.Windows.Count(w => w.IsVisible);
                var records = await OpenReportPhoto(ended);
                Check(windowCount == 1 && desktop.Windows.Count(w => w.IsVisible) == 1 && main.TryGetPlatformHandle()!.Handle == windowHandle &&
                    ReferenceEquals(TopLevel.GetTopLevel(records), main), "matched photo opens in the original main HWND");
                await CaptureWithFrame(main, output, "09a-narrow-report-photo");
                var view = Find<StudyRecordsView>(records, "Records");
                var photos = Find<ListBox>(view, "StudyRecordsList");
                var originalPhoto = photos.SelectedItem;
                var photoBack = Find<Button>(view, "StudyRecordsBack");
                photoBack.RaiseEvent(new KeyEventArgs {RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape});
                await Until(() => photos.IsKeyboardFocusWithin && ReferenceEquals(originalPhoto, photos.SelectedItem) &&
                    Find<Image>(view, "StudyRecordsImage").Source is null, "photo returns to its row and releases the image");
                photos.RaiseEvent(new KeyEventArgs {RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape});
                await Until(() => Find<Button>(ended, "RunReportRecords").IsFocused &&
                    ReferenceEquals(reportRows.SelectedItem, row) && filter.SelectedIndex == 1 &&
                    identity.IsExpanded && Math.Abs(previousOffset.Y - scroll.Offset.Y) < 2, "return restores the original report, filter, scroll and focus");
                Console.WriteLine($"Report, photo and return shared HWND 0x{windowHandle:X} and session {sessionId}.");

                await PressScoped(ended, "RunReportBack");
                var reportList = Find<ListBox>(ended, "RunReportRows");
                await Until(() => reportList.IsEffectivelyVisible && reportList.IsKeyboardFocusWithin && ReferenceEquals(reportList.SelectedItem, row),
                    "narrow report returns to the selected row and focus");
                await PressScoped(ended, "RunReportBack");
                await Until(() => !ended.IsEffectivelyVisible && Find<Button>(window, "FieldReports").IsFocused && Ready(window, "FieldReports"),
                    "report list returns to the field action and its original button");
                ended = await LinkedReports(window, "已定位此次处置对应的结束报告");
                reportList = Find<ListBox>(ended, "RunReportRows");
                var switchingPhoto = Find<StudyRecordsView>(await OpenReportPhoto(ended), "Records");
                schools.SelectedItem = schools.Items.OfType<School>().Single(item => item.Id == access.OtherSchoolId);
                await Until(() => !ended.IsEffectivelyVisible && !window.IsEffectivelyVisible &&
                    Find<Image>(switchingPhoto, "StudyRecordsImage").Source is null && reportList.ItemCount == 0,
                    "changing school clears the old photo, reports and field page together");
                schools.SelectedItem = schools.Items.OfType<School>().Single(item => item.Id == access.SchoolId);
                await PressScoped(main, "OpenDeviceDirectoryButton");
                search.Text = "七班后屏";
                await Until(() => (details = list.GetVisualDescendants().OfType<Button>().FirstOrDefault(button =>
                    button.IsEffectivelyVisible && button.Tag is OverviewScreenItem item && item.ScreenId == access.RearId)) is not null,
                    "explicitly reopen the original rear screen after school switch");
                Click(details!);
                await PressScoped(main, "OpenScreenFieldButton");
                window = await FieldWindow();
                await Until(() => Read(window, "FieldIdentifiers").Contains(access.RearDeviceId) && Ready(window, "FieldRevoke"),
                    "same account can reopen the original field action by its binding");
            }

            main.Width = 840;
            main.Height = 480;
            await Until(() => main.ClientSize is { Width: <= 845, Height: <= 485 }, "actual 840 x 480 window");
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
                Check(Read(historic, "RunReportDevice").Contains(access.RearDeviceId) && Read(historic, "RunReportFeedback").Contains("设备报告读取失败"),
                    "replacement leaves old class-owned exact report accessible without reading revoked device history");
                await Capture(historic, output, "10-historical-replaced-rear");
                var historicalRecords = await OpenReportPhoto(historic);
                var formerView = Find<StudyRecordsView>(historicalRecords, "Records");
                await Signal("records-revoke");
                // Keep the photo open while the report window rechecks its school access.
                await Until(() => Find<Image>(formerView, "StudyRecordsImage").Source is null &&
                    Find<ListBox>(formerView,"StudyRecordsList").ItemCount == 0 && !Find<ContentControl>(historic, "ReportRecordsHost").IsVisible &&
                    Read(historic, "RunReportTarget").Contains("访问已失效") && Find<ListBox>(historic,"RunReportRows").ItemCount == 0,
                    "periodic access check clears the open photo and the original report after school access is revoked");
                await Signal("records-restored");
                await PressScoped(historic, "RunReportBack");
            }
            await PressScoped(window, "FieldReturn");
            await Until(() => !window.IsVisible && (Find<Button>(main, "OpenScreenFieldButton").IsFocused || main.GetVisualDescendants().OfType<Control>().Any(control =>
                control.IsFocused && AutomationProperties.GetAutomationId(control) == "SchoolScreen-" + access.RearId)), "return focus to the original screen after revocation");

            await PressScoped(main, "OpenClassWorkspaceButton");
            await PressScoped(main, "OpenSchoolAffairsButton");
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
                main.Width = 1160;
                main.Height = 780;
                await PressScoped(main, "OpenScreenReportsButton");
                var frontReports = await AcademicPageAsync<DeviceRunReportsView>(main);
                await Until(() => Find<ListBox>(frontReports, "RunReportRows").Items.OfType<DeviceRunReportRow>().Any(row =>
                    row.Report.SessionId == access.FrontReportId && row.Report.Estimate?.Status == "DEVICE_CHANGED" && row.Report.Timing?.ClockDiscontinuity == true),
                    "front sample fixture shows lost reference and clock discontinuity separately");
                await Capture(frontReports, output, "11-front-invalidated-reference");
                main.Width = 840;
                main.Height = 480;
                await Until(() => main.ClientSize is { Width: <= 845, Height: <= 485 }, "narrow clock report");
                var clockRows = Find<ListBox>(frontReports, "RunReportRows");
                var clockRow = clockRows.Items.OfType<DeviceRunReportRow>().Single(row => row.Report.SessionId == access.FrontReportId);
                clockRows.SelectedItem = clockRow;
                Check(clockRow.Title.StartsWith("报告开始 ") && clockRow.Subtitle.Contains("设备时钟跳变"), "list identifies reported time and clock discontinuity");
                await Capture(frontReports, output, "11a-narrow-clock-list");
                await PressScoped(frontReports, "RunReportOpen");
                var clockDetail = Find<DeviceRunReportDetail>(frontReports, "RunReportDetail");
                Check(Read(clockDetail, "ReportWindow").StartsWith("报告起止（结束按设备计时推算）") &&
                    Read(clockDetail, "ReportHints").Contains("报告起止时间仍待核对") && Read(clockDetail, "ReportHints").Contains("麦克风变化，参考失效") &&
                    Read(clockDetail, "ReportTrace").Contains("服务器收到于") && Read(clockDetail, "ReportTrace").Contains("设备墙钟观测结束"),
                    "reported interval, server receipt and device wall clock are named separately");
                await Until(() => FullyInView(Find<TextBlock>(clockDetail, "ReportHints"), Find<ScrollViewer>(clockDetail, "ReportScroll")),
                    "clock warning is visible alongside coverage");
                await CaptureWithFrame(main, output, "11b-narrow-clock-detail");
                main.RequestedThemeVariant = ConsoleHighContrast.Dark;
                await CaptureWithFrame(main, output, "11e-high-contrast-clock-detail");
                main.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
                await CaptureWithFrame(main, output, "11d-dark-clock-detail");
                main.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
                await PressScoped(frontReports, "RunReportField");
                var frontField = await FieldWindow();
                await Until(() => Read(frontField, "FieldIdentifiers").Contains(access.FrontDeviceId) &&
                    !Ready(frontField, "FieldReports"), "report opens the exact front field action without a circular child link");
                await PressScoped(frontField, "FieldReturn");
                await Until(() => Find<Button>(frontReports, "RunReportField").IsFocused && clockDetail.Report?.SessionId == access.FrontReportId,
                    "returning from field action restores the original report and focus");
                await Signal("report-revoke");
                if (Ready(frontReports, "RunReportRefresh")) await PressScoped(frontReports, "RunReportRefresh");
                await Until(() => Read(frontReports, "RunReportTarget").Contains("报告访问已失效") &&
                    Find<ListBox>(frontReports, "RunReportRows").Items.Count == 0 && Find<DeviceRunReportDetail>(frontReports, "RunReportDetail").Report is null,
                    "revocation clears the report contents and current device information");
                await Capture(frontReports, output, "11c-report-access-revoked");
                await Signal("report-restored");
                await PressScoped(frontReports, "RunReportBack");
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
        async Task<DeviceFieldActionView> FieldWindow()
        {
            DeviceFieldActionView? view = null;
            await Until(() => (view = desktop.Windows.Where(item => item.IsVisible).SelectMany(item => item.GetVisualDescendants())
                .OfType<DeviceFieldActionView>().SingleOrDefault(item => item.IsEffectivelyVisible)) is not null, "open field action page");
            return view!;
        }
        async Task<DeviceRunReportsView> LinkedReports(DeviceFieldActionView field, string feedback)
        {
            await PressScoped(field, "FieldReports");
            var reports = await AcademicPageAsync<DeviceRunReportsView>(main);
            Check(ReferenceEquals(TopLevel.GetTopLevel(reports), main) && desktop.Windows.Count(item => item.IsVisible) == 1,
                "field and linked report use the same main window");
            await Until(() => Read(reports, "RunReportFeedback").Contains(feedback), feedback);
            return reports;
        }
        async Task<StudyRecordsPage> OpenReportPhoto(DeviceRunReportsView host)
        {
            await PressScoped(host, "RunReportRecords");
            var content = Find<ContentControl>(host, "ReportRecordsHost");
            await Until(() => content is {IsEffectivelyVisible:true,Content:StudyRecordsPage}, "embedded records page");
            var records = (StudyRecordsPage)content.Content!;
            await Until(() => Read(records,"RecordsStatus").Contains("已读取 2 条"), "exact capture has one photo and one exit");
            var view = Find<StudyRecordsView>(records, "Records");
            var list = Find<ListBox>(view, "StudyRecordsList");
            list.SelectedItem = list.Items.OfType<StudyRecordRow>().Single(item => item.PhotoId is not null);
            await Until(() => Find<Image>(view, "StudyRecordsImage").Source is not null, "matching photo loaded from isolated KV");
            return records;
        }
        static bool FullyInView(Control item, ScrollViewer viewport) =>
            item.IsEffectivelyVisible && item.Bounds.Height > 0 && item.TranslatePoint(default, viewport) is { } at &&
            at.Y >= -1 && at.Y + item.Bounds.Height <= viewport.Bounds.Height + 1;

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
