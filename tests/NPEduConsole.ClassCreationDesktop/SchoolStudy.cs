using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private sealed record SchoolStudyAccess(string Origin, string SchoolCode, string SchoolId, string Password, string GradeId,
        string SevenId, string EightId, string NineId, string LastId, DateOnly Date, string Image1, string Image2, bool AllowStudyTestWrites);

    private static void CheckImpactColumns(Window editor, ListBox list)
    {
        var headers = Find<Grid>(editor, "StudyImpactHeader").Children;
        var rows = list.GetVisualDescendants().OfType<Grid>().Where(grid => grid.Classes.Contains("study-impact")).ToArray();
        Check(rows.Length > 0 && rows.Length < list.ItemCount, "impact table shows a virtualized, scrollable list");
        foreach (var row in rows)
            for (int column = 0; column < headers.Count; column++)
            {
                double heading = headers[column].TranslatePoint(default, editor)!.Value.X;
                double cell = row.Children[column].TranslatePoint(default, editor)!.Value.X;
                Check(Math.Abs(heading - cell) <= 1 / editor.RenderScaling + 0.01,
                    $"column {column + 1} begins at {heading:F1} in the header and {cell:F1} in the row");
            }
        Console.WriteLine($"PASS: all three impact columns align at {editor.ClientSize.Width:F0} DIP, scaling {editor.RenderScaling:P0}.");
    }

    private static int StartSchoolStudy(string fixture, string output)
    {
        var access = JsonSerializer.Deserialize<SchoolStudyAccess>(File.ReadAllText(fixture), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Expected isolated school study fixture.");
        Check(access.AllowStudyTestWrites && IsLoopback(access.Origin), "School study writes require the isolated loopback fixture.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifySchoolStudyAsync(access, output); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifySchoolStudyAsync(SchoolStudyAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        using var checkpoints = new HttpClient {BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(70)};
        try
        {
            main.Width = 1160;
            main.Height = 780;
            Find<TextBox>(main, "ServerAddress").Text = access.Origin;
            Find<TextBox>(main, "SchoolCode").Text = access.SchoolCode;
            Find<TextBox>(main, "Username").Text = "admin";
            Find<TextBox>(main, "Password").Text = access.Password;
            Click(Find<Button>(main, "LoginButton"));
            var picker = Find<ComboBox>(main, "SchoolPicker");
            await Until(() => picker.Items.OfType<School>().Any(row => row.Id == access.SchoolId), "school list loaded");
            picker.SelectedItem = picker.Items.OfType<School>().Single(row => row.Id == access.SchoolId);
            var classes = Find<Button>(main, "OpenClassWorkspaceButton");
            await Until(() => classes.IsEffectivelyVisible && classes.IsEffectivelyEnabled, "class workspace reachable");
            Click(classes);
            var entry = Find<Button>(main, "OpenSchoolStudyButton");
            await Until(() => entry.IsEffectivelyVisible && entry.IsEffectivelyEnabled, "school study reachable without choosing a room");
            Click(entry);
            var window = await WindowOf<SchoolStudyWindow>();
            var grade = Find<ComboBox>(window, "SchoolStudyGrade");
            var jump = Find<CalendarDatePicker>(window, "StudyJumpDate");
            var days = Find<ListBox>(window, "StudyDays");
            var roomList = Find<ListBox>(window, "StudyRooms");
            var editGrade = Find<Button>(window, "SchoolStudyEditGrade");
            await Until(() => editGrade.IsEffectivelyEnabled && grade.SelectedItem is StudyTargetLabel picked &&
                picked.Id == access.GradeId && roomList.ItemCount == 53, "current grade catalog and default week loaded");
            Check(days.Items.OfType<SchoolStudyDayOption>().Count() == 7, "default signed-in week shows all seven dates");
            var dateInput = jump.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "PART_TextBox");
            dateInput.Focus(NavigationMethod.Tab);
            dateInput.Text = access.Date.ToString("yyyy/MM/dd");
            dateInput.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await Until(() => editGrade.IsEffectivelyEnabled && Room(access.EightId).Daily.Contains("20:40"), "Saturday catalog displays the independent eighth class");
            Check(jump.SelectedDate == access.Date.ToDateTime(TimeOnly.MinValue), "typing a date and pressing Enter selects the requested Saturday");
            var dateButton = jump.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_Button");
            var weekButton = Find<Button>(window, "StudyPriorWeek");
            Check(dateButton.Bounds.Width >= 32 && Math.Abs(weekButton.Bounds.Height - jump.Bounds.Height) <= 1 / window.RenderScaling + 0.01,
                "date button has a usable target and the field matches its neighboring week buttons");
            Click(dateButton);
            await Until(() => jump.IsDropDownOpen, "calendar button opens the native popup");
            var popup = jump.GetVisualDescendants().OfType<Popup>().Single();
            var calendar = popup.Child as Calendar ?? popup.Child!.GetVisualDescendants().OfType<Calendar>().Single();
            calendar.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            await Until(() => !jump.IsDropDownOpen && jump.IsKeyboardFocusWithin, "Escape dismisses the calendar and returns focus");
            Check(jump.SelectedDate == access.Date.ToDateTime(TimeOnly.MinValue), "closing the popup preserves the typed date");
            Click(editGrade);
            var editor = await WindowOf<SchoolStudyEditWindow>();
            Find<CalendarDatePicker>(editor, "StudyExceptionDate").SelectedDate = access.Date.ToDateTime(TimeOnly.MinValue);
            Find<ComboBox>(editor, "StudyExceptionKind").SelectedIndex = 1;
            Find<ComboBox>(editor, "StudyExceptionWeekday").SelectedIndex = 3;
            Click(Find<Button>(editor, "StudyApplyException"));
            var review = Find<ListBox>(editor, "SchoolStudyImpact");
            var preview = Find<Button>(editor, "StudyEditPreview");
            var apply = Find<Button>(editor, "StudyEditSave");
            Click(preview);
            await Until(() => apply.IsEffectivelyEnabled, "grade wide preview ready");
            Check(review.ItemCount == 53 && Impact(access.LastId).Action.Contains("保持现有") &&
                Impact(access.SevenId).After.Contains("19:00") && Impact(access.SevenId).After.Contains("13:30") &&
                Impact(access.EightId).After.Contains("20:40") && Impact(access.NineId).After.Contains("已关闭"),
                "complete preview includes class 51 and the exact daily/exam/override outcomes");
            CheckImpactColumns(editor, review);
            await CaptureWithFrame(editor, output, "01-wide-grade-preview");
            await Signal("web-change");

            editor.Width = 840;
            editor.Height = 480;
            await Until(() => editor.ClientSize is {Width: <= 845, Height: <= 485}, "short editor measured");
            Click(apply);
            await Until(() => Impact(access.EightId).NeedsReview && !apply.IsEffectivelyEnabled &&
                Find<TextBlock>(editor, "StudyEditStatus").Text?.Contains("零写入") == true,
                "stale grade review locates the class edited by the other admin");
            Check(review.Items.OfType<SchoolStudyImpactView>().Count(row => row.NeedsReview) == 1 &&
                Impact(access.EightId).After.Contains("20:55") && Impact(access.SevenId).After.Contains("19:00"), "only changed class requires review and the draft is retained");
            await Until(() => review.SelectedItem is SchoolStudyImpactView selected && selected.ClassId == access.EightId &&
                review.ContainerFromItem(selected) is ListBoxItem {IsFocused: true}, "conflict focuses exactly the eighth class");
            await Signal("stale");
            CheckImpactColumns(editor, review);
            await CaptureWithFrame(editor, output, "02-compact-eighth-conflict");
            var originalTheme = editor.RequestedThemeVariant;
            foreach (var (theme, file) in new[] { (ThemeVariant.Dark, "02a-dark-conflict"), (ConsoleHighContrast.Dark, "02b-high-contrast-conflict") })
            {
                editor.RequestedThemeVariant = theme;
                await CaptureWithFrame(editor, output, file);
                CheckImpactColumns(editor, review);
            }
            editor.RequestedThemeVariant = originalTheme;
            Click(preview);
            await Until(() => apply.IsEffectivelyEnabled, "explicitly re-previewed state can be confirmed");
            Click(apply);
            await Until(() => Find<TextBlock>(editor, "StudyEditStatus").Text?.Contains("学校已保存排期") == true,
                "lost schedule acknowledgement recovered by original request");
            await Signal("schedule-applied");
            Click(Find<Button>(editor, "StudyEditClose"));
            await Until(() => !editor.IsVisible && editGrade.IsEffectivelyEnabled && Room(access.SevenId).Daily.Contains("按周四"),
                "return refreshes selected Saturday from server");
            Check(Room(access.EightId).Daily.Contains("20:55") && Room(access.NineId).Source == "已关闭" &&
                Room(access.SevenId).Exam.Contains("13:30"), "calendar retains independent, disabled and exam states");
            await CaptureWithFrame(window, output, "03-wide-saturday-calendar");
            originalTheme = window.RequestedThemeVariant;
            foreach (var (theme, file) in new[] { (ThemeVariant.Dark, "03a-dark-calendar"), (ConsoleHighContrast.Dark, "03b-high-contrast-calendar") })
            {
                window.RequestedThemeVariant = theme;
                dateInput.Focus(NavigationMethod.Tab);
                await CaptureWithFrame(window, output, file);
                Check(jump.IsKeyboardFocusWithin, "the date entry keeps keyboard focus");
            }
            window.RequestedThemeVariant = originalTheme;

            roomList.SelectedItem = Room(access.SevenId);
            var evidenceActions = Find<Border>(window, "StudyEvidenceActions");
            var back = Find<Button>(window, "StudyRoomBackButton");
            Check(Grid.GetColumn(evidenceActions) == 1 && Grid.GetRow(evidenceActions) == 0 && !back.IsEffectivelyVisible,
                "wide class detail places settings and evidence side by side");
            await CaptureWithFrame(window, output, "03c-wide-seven-detail-actions");
            window.Width = 840;
            window.Height = 480;
            var openRoom = Find<Button>(window, "StudyRoomOpenButton");
            await Until(() => openRoom.IsEffectivelyVisible && window.ClientSize is {Width: <= 845, Height: <= 485}, "compact calendar is a list");
            var seven = Room(access.SevenId);
            roomList.SelectedItem = seven;
            Click(openRoom);
            var seatingEntry = Find<Button>(window, "StudySeatingButton");
            await Until(() => back.IsEffectivelyVisible && seatingEntry.IsEffectivelyVisible && !roomList.IsEffectivelyVisible, "selected class detail replaces compact list");
            Check(Grid.GetColumn(evidenceActions) == 0 && Grid.GetRow(evidenceActions) == 1,
                "compact class detail stacks the two action groups");
            await Until(() => back.IsFocused, "compact detail gives focus to the fixed return action");
            var closeStudy = Find<Button>(window, "SchoolStudyClose");
            Check(Equals(closeStudy.Content, "退出学校自习"), "compact class detail distinguishes exiting school study from returning to the class list");
            var detailScroll = Find<ScrollViewer>(window, "StudyDetailScroll");
            var roomActions = new[] {"StudyEditClassButton", "StudyClassMonitoring", "StudyClassDisplay",
                "StudyReportsButton", "StudySeatingButton", "StudyClassRecords"}.Select(name => Find<Button>(window, name)).ToArray();
            Check(!days.IsEffectivelyVisible && !jump.IsEffectivelyVisible &&
                Find<TextBlock>(window, "StudyRoomDate").Text?.Contains($"{access.Date:M 月 d 日}") == true,
                "compact detail keeps the selected school date visible without the date navigation");
            await Until(() => roomActions.All(button => button.TranslatePoint(default, detailScroll) is { } point && point.Y >= 0 &&
                point.Y + button.Bounds.Height <= detailScroll.Bounds.Height + 0.5), "all six compact class actions fit in the initial viewport");
            await CaptureWithFrame(window, output, "04-compact-seven-details");
            var returnPosition = back.TranslatePoint(default, window);
            detailScroll.Offset = new Vector(0, detailScroll.Extent.Height);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            Check(returnPosition.HasValue && back.TranslatePoint(default, window) == returnPosition,
                "return action stays fixed while class detail scrolls");
            Click(back);
            await Until(() => ReferenceEquals(roomList.SelectedItem, seven) && roomList.IsEffectivelyVisible &&
                roomList.ContainerFromItem(seven) is ListBoxItem {IsFocused: true}, "back restores the same class and list focus");
            Check(days.IsEffectivelyVisible && jump.IsEffectivelyVisible && days.SelectedItem is SchoolStudyDayOption {Date: var selectedDate} &&
                selectedDate == access.Date && Equals(closeStudy.Content, "返回"), "back restores date navigation and keeps Saturday");
            Click(openRoom);

            Click(seatingEntry);
            var waiting = await WindowOf<SchoolStudySeatingWindow>();
            await Signal("seat-read-started");
            Check(Find<Button>(waiting, "SeatClose").IsEffectivelyEnabled, "A delayed seat read must leave the return action available.");
            waiting.Close();
            Check(!waiting.IsVisible, "Window close must release the parent while a seat read is still pending.");
            await Signal("seat-read-cancelled");
            await Until(() => seatingEntry.IsEffectivelyEnabled, "same classroom usable after cancelling a seat read");
            Click(seatingEntry);
            var seating = await WindowOf<SchoolStudySeatingWindow>();
            var saveSeat = Find<Button>(seating, "SeatSave");
            await Until(() => Find<TextBlock>(seating, "SeatFeedback").Text?.Contains("不兼容") == true, "report a malformed seat record without closing the app");
            Check(!saveSeat.IsEffectivelyEnabled, "no seat edits based on an unreadable snapshot");
            Click(Find<Button>(seating, "SeatRefresh"));
            await Until(() => Find<Button>(seating, "SeatChoose").IsEffectivelyEnabled, "exact class seating editor loaded");
            var seatTabs = Find<TabControl>(seating, "SeatTabs");
            var imageLabel = Find<TextBlock>(seating, "SeatImageLabel");
            Check(!saveSeat.IsEffectivelyVisible, "unmodified seating preview has no confirmation button");
            seating.StageImage(SchoolStudySeatingWindow.NormalizePng(File.ReadAllBytes(access.Image1)));
            Check(saveSeat.IsEffectivelyEnabled, "synthetic image passes production normalizer and preview");
            seatTabs.SelectedIndex = 1;
            var seatDevices = Find<ListBox>(seating, "SeatDevices");
            await Until(() => IsOnSeatPage(seatDevices) && !IsOnSeatPage(saveSeat) && !IsOnSeatPage(Find<Button>(seating, "SeatWithdraw")) &&
                !IsOnSeatPage(Find<Button>(seating, "SeatRestore")), "device observation hides every seating write action");
            Click(saveSeat);
            await Signal("seat-draft-observed");
            seatTabs.SelectedIndex = 0;
            await Until(() => IsOnSeatPage(saveSeat) && saveSeat.IsEffectivelyEnabled && imageLabel.Text == "待发布的新图片",
                "switching tabs keeps the unpublished image");
            seating.Close();
            Check(seating.IsVisible && Find<Button>(seating, "SeatClose").Content is "放弃草稿", "an unpublished draft still needs an explicit discard");
            Click(saveSeat);
            seating.Close();
            Check(seating.IsVisible && !Find<Button>(seating, "SeatClose").IsEffectivelyEnabled, "publishing protects the in-flight write");
            await WaitSeat(1);
            await Signal("start-native");
            seating.StageImage(SchoolStudySeatingWindow.NormalizePng(File.ReadAllBytes(access.Image2)));
            await Signal("drop-next-seat-write");
            Click(saveSeat);
            await Until(() => Find<Button>(seating, "SeatCheck").IsEffectivelyEnabled &&
                Equals(saveSeat.Content, "确认按原编号重试"), "missing receipt exposes read and retry on the preview tab");
            Click(Find<Button>(seating, "SeatClose"));
            await Until(() => !seating.IsVisible && seatingEntry.IsFocused, "a pending receipt can be revisited from the same class");
            Click(seatingEntry);
            seating = await WindowOf<SchoolStudySeatingWindow>();
            saveSeat = Find<Button>(seating, "SeatSave");
            seatTabs = Find<TabControl>(seating, "SeatTabs");
            imageLabel = Find<TextBlock>(seating, "SeatImageLabel");
            var checkSeat = Find<Button>(seating, "SeatCheck");
            await Until(() => saveSeat.IsEffectivelyVisible && saveSeat.IsEffectivelyEnabled && checkSeat.IsEffectivelyVisible &&
                imageLabel.Text?.Contains("原学校版本 1") == true && Find<Image>(seating, "SeatImage").Source is not null,
                "reopened preview recovers the outstanding original image and baseline");
            await PressScoped(seating, "SeatCheck");
            await Until(() => checkSeat.IsEffectivelyEnabled, "original receipt can be read from preview");
            seatTabs.SelectedIndex = 1;
            var backToPreview = Find<Button>(seating, "SeatRetryInPreview");
            await Until(() => backToPreview.IsEffectivelyVisible && checkSeat.IsEffectivelyEnabled && !saveSeat.IsEffectivelyVisible,
                "observation can read the receipt and offers only navigation back to preview");
            seating.Width = 840;
            seating.Height = 480;
            await Until(() => seating.ClientSize is {Width: <= 845, Height: <= 485}, "narrow seat observation measured");
            await PressScoped(seating, "SeatCheck");
            await Until(() => backToPreview.IsEffectivelyEnabled, "original receipt can be read from observation");
            Click(saveSeat);
            await CaptureWithFrame(seating, output, "05a-narrow-seat-observation-pending");
            await PressScoped(seating, "SeatRetryInPreview");
            await Until(() => seatTabs.SelectedIndex == 0 && saveSeat.IsEffectivelyVisible && saveSeat.IsEffectivelyEnabled,
                "return to original operation is separate from explicit retry");
            await CaptureWithFrame(seating, output, "05b-narrow-seat-confirm-original-retry");
            await Signal("seat-retry-navigation-only");
            Click(saveSeat);
            await WaitSeat(2);
            await Signal("old-in-use");
            seating.Width = 1160;
            seating.Height = 780;
            await RefreshDevices();
            var deviceRows = Find<ListBox>(seating, "SeatDevices");
            await Until(() => Device("前屏").Download.Contains("v2") && Device("前屏").Opened.Contains("旧图 v1"), "front shows v2 downloaded, only v1 open");
            Check(Device("前屏").Applied.Contains("待核对") && Device("后屏").Opened.Contains("待核对") &&
                Device("后屏").Configuration.Contains("待核对"), "old display and unobserved rear cannot report current image");
            Check(!saveSeat.IsEffectivelyVisible && !checkSeat.IsEffectivelyVisible,
                "observation has no write or stale receipt action after confirmation");
            await CaptureWithFrame(seating, output, "05-front-old-and-rear-unconfirmed");
            await Signal("allow-close");
            await Signal("new-in-use");
            await RefreshDevices();
            await Until(() => Device("前屏").Applied.Contains("当前图 v2") && Device("前屏").Opened.Contains("当前图 v2"),
                "same screen confirms new image after closing and reopening");
            Check(Device("后屏").Applied.Contains("待核对"), "rear remains unknown");
            await CaptureWithFrame(seating, output, "06-front-reopened-current");
            await CaptureFieldAction("前屏", "06a-wide-front-field-action");
            await CaptureFieldAction("后屏", "06b-wide-rear-field-action");

            Find<TabControl>(seating, "SeatTabs").SelectedIndex = 0;
            Click(Find<Button>(seating, "SeatWithdraw"));
            Click(saveSeat);
            await WaitSeat(3);
            await Signal("withdrawn");
            var versions = Find<ComboBox>(seating, "SeatVersions");
            versions.SelectedItem = versions.Items.Cast<object>().Single(item => item.ToString()!.StartsWith("版本 1 ·", StringComparison.Ordinal));
            var restore = Find<Button>(seating, "SeatRestore");
            await Until(() => restore.IsEffectivelyEnabled, "retained history can be prepared");
            Click(restore);
            await Until(() => saveSeat.IsEffectivelyEnabled && saveSeat.Content?.ToString() == "确认重新发布", "previewed source can be re-published");
            Click(saveSeat);
            await WaitSeat(4);
            await Signal("restored");
            await Capture(seating, output, "07-history-republished-v4");
            Click(Find<Button>(seating, "SeatClose"));
            await Until(() => !seating.IsVisible, "return from seating");
            Click(Find<Button>(window, "StudyReportsButton"));
            Window? reports = null;
            await Until(() => (reports = desktop.Windows.FirstOrDefault(item => item.IsVisible && item.Title == "本日场次与回执")) is not null,
                "existing evidence window opened for selected class and day");
            var view = ((StackPanel)((ScrollViewer)reports!.Content!).Content!).Children.OfType<StudyEvidenceView>().Single();
            var readReports = view.FindControl<Button>("ReadReportsButton")!;
            var reportStatus = view.FindControl<TextBlock>("ReportsStatus")!;
            await Until(() => readReports.IsEffectivelyEnabled && reportStatus.Text?.Contains("开始且已上传") == true,
                "selected date scopes the existing reports view");
            Click(readReports);
            await Until(() => reportStatus.Text?.Contains("当前没有符合条件的已上传报告") == true,
                "reports view reads the selected date from KV");
            reports.Close();

            Click(back);
            await Until(() => roomList.IsEffectivelyVisible, "compact class list restored from reports");
            roomList.SelectedItem = Room(access.LastId);
            roomList.ScrollIntoView(roomList.SelectedItem);
            Click(openRoom);
            Click(Find<Button>(window, "StudyEditClassButton"));
            var restoreEditor = await WindowOf<SchoolStudyEditWindow>();
            var scopeMode = Find<ComboBox>(restoreEditor, "StudyPolicyMode");
            Check(scopeMode.SelectedIndex == 1, "class 51 has its own schedule");
            scopeMode.SelectedIndex = 0;
            Click(Find<Button>(restoreEditor, "StudyEditPreview"));
            var restoreSave = Find<Button>(restoreEditor, "StudyEditSave");
            await Until(() => restoreSave.IsEffectivelyEnabled && Find<ListBox>(restoreEditor, "SchoolStudyImpact")
                .Items.OfType<SchoolStudyImpactView>().Single().After.Contains("继承年级"), "restore inheritance preview uses current grade");
            Click(restoreSave);
            await Until(() => Find<TextBlock>(restoreEditor, "StudyEditStatus").Text?.Contains("学校已保存排期") == true,
                "restore inheritance saved");
            await Signal("restored-inheritance");
            Click(Find<Button>(restoreEditor, "StudyEditClose"));
            Console.WriteLine("PASS: Windows school study calendar, legacy conflict, owned receipts, narrow focus, and per-screen evidence.");
            desktop.Shutdown(0);
            return;

            SchoolStudyRoomRow Room(string id) => roomList.Items.OfType<SchoolStudyRoomRow>().Single(row => row.Room.ClassId == id);
            SchoolStudyImpactView Impact(string id) => review.Items.OfType<SchoolStudyImpactView>().Single(row => row.ClassId == id);
            StudySeatingDeviceView Device(string name) => deviceRows.Items.OfType<StudySeatingDeviceView>().Single(row => row.Name == name);
            bool IsOnSeatPage(Control control) => control.IsEffectivelyVisible && TopLevel.GetTopLevel(control) == seating;
            Task WaitSeat(int revision) => Until(() => Find<TextBlock>(seating, "SeatFeedback").Text?.Contains("版本 " + revision) == true &&
                Find<Button>(seating, "SeatChoose").IsEffectivelyEnabled, "seat version " + revision + " publication and recovery");
            async Task RefreshDevices()
            {
                Find<TabControl>(seating, "SeatTabs").SelectedIndex = 1;
                Click(Find<Button>(seating, "SeatRefresh"));
                await Until(() => Find<Button>(seating, "SeatRefresh").IsEffectivelyEnabled, "per-screen evidence read");
            }
            async Task CaptureFieldAction(string name, string file)
            {
                var row = Device(name);
                deviceRows.ScrollIntoView(row);
                Button? entry = null;
                await Until(() => (entry = deviceRows.ContainerFromItem(Device(name))?.GetVisualDescendants().OfType<Button>()
                    .FirstOrDefault(button => Equals(button.Content, "现场处置"))) is {IsEffectivelyEnabled: true}, name + " action is visible");
                Click(entry!);
                var field = await WindowOf<DeviceFieldActionWindow>();
                field.Width = 1160;
                field.Height = 780;
                await Until(() => field.ClientSize.Width >= 1100 && Find<TextBlock>(field, "FieldTargetName").Text?.EndsWith(name) == true &&
                    Find<TextBlock>(field, "FieldIdentifiers").Text is { } ids && ids.Contains(row.Registration.BindingId) &&
                    ids.Contains(row.Registration.DeviceId!) &&
                    Find<Button>(field, "FieldRead").IsEffectivelyEnabled, "wide action window preserves the selected screen and device");
                var identifiers = field.GetVisualDescendants().OfType<Expander>().Single(panel => Equals(panel.Header, "设备和绑定编号"));
                identifiers.IsExpanded = true;
                await Until(() => Find<TextBlock>(field, "FieldIdentifiers").IsEffectivelyVisible, "validation identifiers visible in capture");
                await CaptureWithFrame(field, output, file);
                field.Close();
                await Until(() => !field.IsVisible && seating.IsActive && (entry!.IsFocused || deviceRows.IsFocused),
                    "returned to the seating device list after closing " + name);
            }
        }
        catch (Exception error)
        {
            if (desktop.Windows.LastOrDefault(candidate => candidate.IsVisible) is { } window)
                try { await Capture(window, output, "failure"); } catch (Exception captureError) { Console.Error.WriteLine(captureError.Message); }
            await File.WriteAllTextAsync(Path.Combine(output, "failure.txt"), error.ToString());
            Console.Error.WriteLine(error);
            desktop.Shutdown(1);
        }

        async Task<T> WindowOf<T>() where T : Window
        {
            await Until(() => desktop.Windows.OfType<T>().Any(window => window.IsVisible), typeof(T).Name + " opened");
            return desktop.Windows.OfType<T>().Single(window => window.IsVisible);
        }
        async Task Signal(string step)
        {
            using var response = await checkpoints.PostAsync("/__study/" + step, null);
            Check(response.IsSuccessStatusCode, "Isolated checkpoint " + step);
        }
    }
}
