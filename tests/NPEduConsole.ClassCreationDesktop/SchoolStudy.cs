using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private sealed record SchoolStudyAccess(string Origin, string SchoolCode, string SchoolId, string Password, string GradeId,
        string SevenId, string EightId, string NineId, string LastId, DateOnly Date, string Image1, string Image2, bool AllowStudyTestWrites);

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
            jump.SelectedDate = access.Date.ToDateTime(TimeOnly.MinValue);
            await Until(() => editGrade.IsEffectivelyEnabled && Room(access.EightId).Daily.Contains("20:40"), "Saturday catalog displays the independent eighth class");
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
            await Capture(editor, output, "01-wide-grade-preview");
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
            await Capture(editor, output, "02-compact-eighth-conflict");
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
            await Capture(window, output, "03-wide-saturday-calendar");

            window.Width = 840;
            window.Height = 480;
            var openRoom = Find<Button>(window, "StudyRoomOpenButton");
            await Until(() => openRoom.IsEffectivelyVisible && window.ClientSize is {Width: <= 845, Height: <= 485}, "compact calendar is a list");
            var seven = Room(access.SevenId);
            roomList.SelectedItem = seven;
            Click(openRoom);
            var back = Find<Button>(window, "StudyRoomBackButton");
            var seatingEntry = Find<Button>(window, "StudySeatingButton");
            await Until(() => back.IsEffectivelyVisible && seatingEntry.IsEffectivelyVisible && !roomList.IsEffectivelyVisible, "selected class detail replaces compact list");
            await Capture(window, output, "04-compact-seven-details");
            Click(back);
            await Until(() => ReferenceEquals(roomList.SelectedItem, seven) && roomList.IsEffectivelyVisible &&
                roomList.ContainerFromItem(seven) is ListBoxItem {IsFocused: true}, "back restores the same class and list focus");
            Check(days.SelectedItem is SchoolStudyDayOption {Date: var selectedDate} && selectedDate == access.Date, "back keeps Saturday");
            Click(openRoom);

            Click(seatingEntry);
            var seating = await WindowOf<SchoolStudySeatingWindow>();
            var saveSeat = Find<Button>(seating, "SeatSave");
            await Until(() => Find<Button>(seating, "SeatChoose").IsEffectivelyEnabled, "exact class seating editor loaded");
            seating.StageImage(SchoolStudySeatingWindow.NormalizePng(File.ReadAllBytes(access.Image1)));
            Check(saveSeat.IsEffectivelyEnabled, "synthetic image passes production normalizer and preview");
            Click(saveSeat);
            await WaitSeat(1);
            await Signal("start-native");
            seating.StageImage(SchoolStudySeatingWindow.NormalizePng(File.ReadAllBytes(access.Image2)));
            Click(saveSeat);
            await WaitSeat(2);
            await Signal("old-in-use");
            await RefreshDevices();
            var deviceRows = Find<ListBox>(seating, "SeatDevices");
            await Until(() => Device("前屏").Download.Contains("v2") && Device("前屏").Opened.Contains("旧图 v1"), "front shows v2 downloaded, only v1 open");
            Check(Device("前屏").Applied.Contains("待核对") && Device("后屏").Opened.Contains("待核对") &&
                Device("后屏").Configuration.Contains("待核对"), "old display and unobserved rear cannot report current image");
            await Capture(seating, output, "05-front-old-and-rear-unconfirmed");
            await Signal("allow-close");
            await Signal("new-in-use");
            await RefreshDevices();
            await Until(() => Device("前屏").Applied.Contains("当前图 v2") && Device("前屏").Opened.Contains("当前图 v2"),
                "same screen confirms new image after closing and reopening");
            Check(Device("后屏").Applied.Contains("待核对"), "rear remains unknown");
            await Capture(seating, output, "06-front-reopened-current");

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
            Task WaitSeat(int revision) => Until(() => Find<TextBlock>(seating, "SeatFeedback").Text?.Contains("版本 " + revision) == true &&
                Find<Button>(seating, "SeatChoose").IsEffectivelyEnabled, "seat version " + revision + " publication and recovery");
            async Task RefreshDevices()
            {
                Find<TabControl>(seating, "SeatTabs").SelectedIndex = 1;
                Click(Find<Button>(seating, "SeatRefresh"));
                await Until(() => Find<Button>(seating, "SeatRefresh").IsEffectivelyEnabled, "per-screen evidence read");
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
