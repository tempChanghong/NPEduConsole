using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Threading;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private sealed record MonitoringAccess(string Origin, string SchoolCode, string SchoolId, string OtherSchoolId, string Password, string GradeId,
        string SevenId, string EightId, string NineId, string FrontId, string PhotoId, string RearPhotoId, string ExitId, DateOnly Date, bool Isolated);

    private static int StartStudyMonitoring(string fixture, string output)
    {
        var access = JsonSerializer.Deserialize<MonitoringAccess>(File.ReadAllText(fixture), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Expected isolated monitoring fixture.");
        Check(access.Isolated && IsLoopback(access.Origin), "Study monitoring requires the isolated loopback fixture.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyMonitoringAsync(access, output); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyMonitoringAsync(MonitoringAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        using var checkpoints = new HttpClient {BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(90)};
        try
        {
            main.Width = 1160;
            main.Height = 780;
            Find<TextBox>(main, "ServerAddress").Text = access.Origin;
            Find<TextBox>(main, "SchoolCode").Text = access.SchoolCode;
            Find<TextBox>(main, "Username").Text = "admin";
            Find<TextBox>(main, "Password").Text = access.Password;
            Click(Find<Button>(main, "LoginButton"));
            var schools = Find<ComboBox>(main, "SchoolPicker");
            await Until(() => schools.Items.OfType<School>().Any(row => row.Id == access.SchoolId), "school available");
            schools.SelectedItem = schools.Items.OfType<School>().Single(row => row.Id == access.SchoolId);
            var classes = Find<Button>(main, "OpenClassWorkspaceButton");
            await Until(() => classes.IsEffectivelyEnabled && classes.IsEffectivelyVisible, "class and teaching workspace");
            Click(classes);
            var entry = Find<Button>(main, "OpenSchoolStudyButton");
            await Until(() => entry.IsEffectivelyVisible && entry.IsEffectivelyEnabled, "school study reachable");
            Click(entry);
            var study = await WindowOf<SchoolStudyWindow>();
            var rooms = Find<ListBox>(study, "StudyRooms");
            var classEdit = Find<Button>(study, "StudyClassMonitoring");
            var picker = Find<ComboBox>(study, "SchoolStudyGrade");
            await Until(() => picker.SelectedItem is StudyTargetLabel label && label.Id == access.GradeId && classEdit.IsEffectivelyEnabled,
                "current grade can open monitoring");
            Find<CalendarDatePicker>(study, "StudyJumpDate").SelectedDate = access.Date.ToDateTime(TimeOnly.MinValue);
            await Until(() => classEdit.IsEffectivelyEnabled && rooms.Items.OfType<SchoolStudyRoomRow>().Any(row =>
                row.Room.ClassId == access.SevenId && row.Daily.Contains("19:00")), "exact Monday and classroom loaded");
            rooms.SelectedItem = rooms.Items.OfType<SchoolStudyRoomRow>().Single(row => row.Room.ClassId == access.SevenId);

            Click(Find<Button>(study, "StudyReportsButton"));
            Window? reports = null;
            await Until(() => (reports = desktop.Windows.FirstOrDefault(w => w.IsVisible && w.Title == "本日场次与回执")) is not null, "day evidence opened");
            var evidence = ((StackPanel)((ScrollViewer)reports!.Content!).Content!).Children.OfType<StudyEvidenceView>().Single();
            var query = evidence.FindControl<Button>("ReadReportsButton")!;
            await Until(() => query.IsEffectivelyEnabled &&
                evidence.FindControl<TextBlock>("ReportsStatus")!.Text?.Contains("开始且已上传") == true, "query exact date");
            Click(query);
            var reportPicker = evidence.FindControl<ComboBox>("ReportPicker")!;
            await Until(() => reportPicker.ItemCount == 2, "front and rear groups separated");
            reportPicker.SelectedItem = reportPicker.Items.Cast<object>().Single(x => x.ToString()!.Contains("前屏"));
            var sessionRecords = evidence.FindControl<Button>("StudySessionRecords")!;
            await Until(() => sessionRecords.IsEffectivelyVisible, "report scope validated");
            Click(sessionRecords);
            var window = await WindowOf<StudyRecordsWindow>();
            var page = RecordsPage(window);
            var records = Find<StudyRecordsView>(page, "Records");
            var list = records.FindControl<ListBox>("StudyRecordsList")!;
            var image = records.FindControl<Image>("StudyRecordsImage")!;
            await Until(() => list.ItemCount == 2 && list.IsEffectivelyEnabled, "front session has actual photo and exit");
            Check(list.Items.OfType<StudyRecordRow>().Any(row => row.Key == "exit:" + access.ExitId && row.PhotoId is null &&
                row.Details.Contains("服务器收到")), "exit without a picture uses the server receipt time");
            Check(list.Items.OfType<StudyRecordRow>().All(row => row.Title.Contains("前屏")), "no rear rows leak into the front report");
            list.SelectedItem = list.Items.OfType<StudyRecordRow>().Single(row => row.PhotoId == access.PhotoId);
            await Until(() => image.Source is not null, "actual stored JPEG renders in Windows");
            Check(Find<TextBlock>(page, "RecordsScope").Text?.Contains("场次学校时间") == true, "report distinguishes school clock");
            await CaptureWithFrame(window, output, "01-wide-front-report");
            await Signal("expire-image");
            list.RaiseEvent(new KeyEventArgs {RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter});
            await Until(() => Find<TextBlock>(page, "RecordsStatus").Text?.Contains("该照片已不可查看") == true &&
                image.Source is null && list.ItemCount == 2, "expired photo releases its image without clearing other records");
            await Capture(window, output, "01b-expired-photo");
            await Signal("unexpire-fixture-image");
            window.Close();
            await Until(() => !window.IsVisible, "report child closed");
            reportPicker.SelectedItem = reportPicker.Items.Cast<object>().Single(row => row.ToString()!.Contains("后屏"));
            await Until(() => sessionRecords.IsEffectivelyVisible, "second report loaded");
            Click(sessionRecords);
            var rear = await WindowOf<StudyRecordsWindow>();
            var rearStatus = Find<TextBlock>(RecordsPage(rear), "RecordsStatus");
            await Until(() => rearStatus.Text?.Contains("暂无可查看") == true, "rear receipt yields no front photos");
            rear.Close();

            await Signal("seed-report-modes");
            var reportMode = evidence.FindControl<ComboBox>("StudyReportModePicker")!;
            var earlier = evidence.FindControl<Button>("EarlierReports")!;
            string[] ReportLabels() => reportPicker.Items.Cast<object>().Select(item => item.ToString()!).ToArray();
            bool PageHasMode(int size, string mode) => reportPicker.ItemCount == size && ReportLabels().All(label =>
                label.Contains($" · {mode} · ", StringComparison.Ordinal));
            await Until(() => query.IsEffectivelyEnabled && reportMode.SelectedIndex == 0, "reports initially query all modes");
            Click(query);
            await Until(() => reportPicker.ItemCount == 20 && ReportLabels().Any(label => label.Contains(" · 日常 · ")) &&
                ReportLabels().Any(label => label.Contains(" · 考试 · ")), "first unfiltered page contains both modes");
            reportMode.SelectedIndex = 1;
            await Until(() => PageHasMode(20, "日常") && earlier.IsEffectivelyVisible && sessionRecords.IsEffectivelyVisible, "daily first page and detail loaded");
            reports.Width = 1160;
            reports.Height = 780;
            await Until(() => reports.ClientSize is {Width: >= 1140, Height: >= 740}, "wide report window measured");
            reportMode.BringIntoView();
            await CaptureWithFrame(reports, output, "08-wide-daily-reports");
            Click(earlier);
            await Until(() => PageHasMode(3, "日常") && !earlier.IsEffectivelyVisible && sessionRecords.IsEffectivelyVisible,
                "daily next page retains mode and cursor");

            await Signal("hold-daily-reports");
            Click(query);
            await Signal("daily-response-held");
            Check(!reportPicker.IsEffectivelyVisible && !sessionRecords.IsEffectivelyVisible, "pending query clears previous report and records");
            reportMode.SelectedIndex = 2;
            await Until(() => PageHasMode(20, "考试") && earlier.IsEffectivelyVisible && sessionRecords.IsEffectivelyVisible, "exam starts again on page one");
            reports.Width = 840;
            reports.Height = 480;
            await Until(() => reports.ClientSize is {Width: <= 845, Height: <= 485}, "compact report window measured");
            earlier.BringIntoView();
            await CaptureWithFrame(reports, output, "09-compact-exam-reports");
            await Signal("release-daily-reports");
            Click(earlier);
            await Until(() => PageHasMode(2, "考试") && !earlier.IsEffectivelyVisible && sessionRecords.IsEffectivelyVisible,
                "exam next page retains mode and delayed daily results cannot replace it");

            reportMode.SelectedIndex = 1;
            await Until(() => PageHasMode(20, "日常") && sessionRecords.IsEffectivelyVisible, "daily selection resets pagination");
            reportPicker.SelectedItem = reportPicker.Items.Cast<object>().First(row => row.ToString()!.Contains("前屏"));
            await Until(() => sessionRecords.IsEffectivelyVisible, "original front session ready after filter");
            Click(sessionRecords);
            var filteredRecords = await WindowOf<StudyRecordsWindow>();
            var filteredList = Find<StudyRecordsView>(RecordsPage(filteredRecords), "Records").FindControl<ListBox>("StudyRecordsList")!;
            await Until(() => filteredList.Items.OfType<StudyRecordRow>().Any(row => row.PhotoId == access.PhotoId) &&
                filteredList.Items.OfType<StudyRecordRow>().Any(row => row.Key == "exit:" + access.ExitId && row.PhotoId is null),
                "filtered report still opens the exact front photo and no-image exit");
            filteredRecords.Close();
            reports.Close();
            await Signal("seed-large-rear");

            Click(Find<Button>(study, "StudyClassRecords"));
            window = await WindowOf<StudyRecordsWindow>();
            window.Width = 840;
            window.Height = 480;
            page = RecordsPage(window);
            var compact = Find<StudyRecordsView>(page, "Records");
            list = compact.FindControl<ListBox>("StudyRecordsList")!;
            image = compact.FindControl<Image>("StudyRecordsImage")!;
            await Until(() => list.ItemCount == 3 && list.IsEffectivelyEnabled && window.ClientSize is {Width: <= 845, Height: <= 485},
                "compact class records measured");
            await Capture(window, output, "02-compact-class-records");
            var selected = list.Items.OfType<StudyRecordRow>().Single(row => row.PhotoId == access.PhotoId);
            list.SelectedItem = selected;
            var back = compact.FindControl<Button>("StudyRecordsBack")!;
            await Until(() => back.IsEffectivelyVisible && image.Source is not null && !list.IsEffectivelyVisible, "narrow screen uses a single photo page");
            var details = compact.FindControl<TextBlock>("StudyRecordsPreviewDetails")!;
            await Until(() => details.IsEffectivelyVisible && details.Text?.Contains("拍摄") == true &&
                details.Text.Contains("到期") && details.Bounds.Height >= 15 &&
                details.TranslatePoint(new Point(0, details.Bounds.Height), compact)?.Y <= image.TranslatePoint(new Point(0, 0), compact)?.Y,
                "compact capture and expiry text stays above the image");
            Check(Find<TextBlock>(page, "RecordsScope").Text?.Contains("学校日期 " + access.Date.ToString("yyyy/M/d")) == true,
                "class selector labels the school-clock date separately from capture time");
            var filters = Find<WrapPanel>(page, "RecordsFilters");
            var confirm = Find<Button>(page, "RemoveRecordConfirm");
            var keep = Find<Button>(page, "RemoveRecordCancel");
            var photoDelete = Find<Button>(page, "StudyRecordsDelete");
            Check(!filters.IsEffectivelyVisible && photoDelete.IsEffectivelyVisible, "single-photo view gives the former filter area to the image");
            Click(photoDelete);
            await Until(() => keep.IsFocused && confirm.IsEffectivelyVisible, "keep is the default destructive-action focus");
            keep.RaiseEvent(new KeyEventArgs {RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape});
            await Until(() => !confirm.IsEffectivelyVisible && photoDelete.IsFocused && image.Source is not null, "Escape preserves the photo before leaving");
            Click(photoDelete);
            Click(back);
            await Until(() => list.IsEffectivelyVisible && list.SelectedItem is StudyRecordRow row && row.Key == selected.Key &&
                list.ContainerFromItem(row) is ListBoxItem {IsFocused: true}, "back restores selection and keyboard focus");
            Check(filters.IsEffectivelyVisible && image.Source is null && !confirm.IsEffectivelyVisible,
                "return restores list filters, releases image and cancels pending deletion");
            list.SelectedItem = list.Items.OfType<StudyRecordRow>().Single(row => row.PhotoId == access.RearPhotoId);
            await Until(() => image.Source is { } loaded && loaded.Size.Width >= 1200 &&
                Math.Min(image.Bounds.Height, image.Bounds.Width * loaded.Size.Height / loaded.Size.Width) >= 180 &&
                Find<TextBlock>(compact, "StudyRecordsPreviewTitle").Text?.Contains("后屏") == true,
                "a distinct 1280-pixel image takes at least 180 DIP of actual preview height");
            Check(!confirm.IsEffectivelyVisible && !filters.IsEffectivelyVisible, "opening the rear photo cannot inherit the front photo's confirmation");
            Click(confirm);
            await Signal("photos-retained-after-compact-cancel");
            await CaptureWithFrame(window, output, "03-compact-single-photo");
            window.Width = 1160;
            window.Height = 780;
            await Until(() => filters.IsEffectivelyVisible && list.IsEffectivelyVisible && image.Source is not null,
                "widening keeps the rear image alongside the restored list");
            await Signal("revoke");
            Click(Find<Button>(page, "RecordsRefresh"));
            await Until(() => Find<TextBlock>(page, "RecordsStatus").Text?.Contains("无本校照片查看权限") == true && list.ItemCount == 0 && image.Source is null,
                "revocation while open clears rows and pixels");
            await Capture(window, output, "04-revoked-records");
            await Signal("restore");
            window.Close();
            await Until(() => classEdit.IsEffectivelyEnabled, "return to class after records");

            Click(classEdit);
            var classMonitor = await WindowOf<SchoolStudyEditWindow>();
            Check(Find<TextBlock>(classMonitor, "StudyMonitorCurrent").Text?.Contains("继承年级") == true, "class summary explains inherited policy");
            Click(Find<Button>(classMonitor, "StudyMonitorGrade"));
            await Until(() => !classMonitor.IsVisible, "grade action returns to the same calendar");
            var gradeMonitor = await WindowOf<SchoolStudyEditWindow>();
            var preview = Find<Button>(gradeMonitor, "StudyEditPreview");
            var apply = Find<Button>(gradeMonitor, "StudyEditSave");
            var review = Find<ListBox>(gradeMonitor, "SchoolStudyImpact");
            Find<TextBox>(gradeMonitor, "MonitorPhotoSeconds").Text = "60";
            Click(preview);
            await Until(() => apply.IsEffectivelyEnabled, "photo interval-only review ready");
            var byClass = review.Items.OfType<SchoolStudyImpactView>().ToDictionary(row => row.ClassId);
            Check(byClass[access.SevenId].Before.Contains("拍照间隔 30 秒") && byClass[access.SevenId].After.Contains("拍照间隔 60 秒") &&
                byClass[access.EightId].Action.Contains("保持现有") && byClass[access.NineId].After.Contains("已关闭"),
                "interval-only preview shows both values for the inheriting class");
            await Capture(gradeMonitor, output, "04b-photo-interval-only");
            Find<TextBox>(gradeMonitor, "MonitorPhotoSeconds").Text = "30";
            Find<TextBox>(gradeMonitor, "MonitorRetention").Text = "14";
            Click(preview);
            await Until(() => apply.IsEffectivelyEnabled, "monitoring preview ready");
            StudyImpactViewCheck();
            await Capture(gradeMonitor, output, "05-wide-monitoring-impact");
            await Signal("web-change");
            gradeMonitor.Width = 840;
            gradeMonitor.Height = 480;
            Click(apply);
            await Until(() => review.Items.OfType<SchoolStudyImpactView>().Any(row => row.ClassId == access.EightId && row.NeedsReview) &&
                !apply.IsEffectivelyEnabled && Find<TextBlock>(gradeMonitor, "StudyEditStatus").Text?.Contains("零写入") == true,
                "old Web edit is detected without losing monitoring fields");
            await Signal("stale");
            await Capture(gradeMonitor, output, "06-compact-monitoring-conflict");
            Click(preview);
            await Until(() => apply.IsEffectivelyEnabled, "confirm after renewed review");
            StudyImpactViewCheck();
            Click(apply);
            await Until(() => Find<TextBlock>(gradeMonitor, "StudyEditStatus").Text?.Contains("学校已保存监测策略") == true,
                "lost monitoring acknowledgement recovered via original ID");
            await Signal("applied");
            Click(Find<Button>(gradeMonitor, "StudyEditClose"));
            await Until(() => study.IsEffectivelyEnabled && rooms.Items.OfType<SchoolStudyRoomRow>().Any(row => row.Room.ClassId == access.NineId &&
                row.Source == "已关闭"), "school calendar reloads without changing the closed class");
            await Signal("extend");

            Click(Find<Button>(study, "StudyClassRecords"));
            var late = await WindowOf<StudyRecordsWindow>();
            var latePage = RecordsPage(late);
            var lateRecords = Find<StudyRecordsView>(latePage, "Records");
            var lateList = lateRecords.FindControl<ListBox>("StudyRecordsList")!;
            var lateImage = lateRecords.FindControl<Image>("StudyRecordsImage")!;
            await Until(() => lateList.ItemCount == 3 && lateList.IsEffectivelyEnabled, "both photos are still within shortened retention");
            await Signal("hold-photo");
            lateList.SelectedItem = lateList.Items.OfType<StudyRecordRow>().Single(row => row.PhotoId == access.PhotoId);
            await Signal("photo-awaiting-release");
            Click(Find<Button>(latePage, "RecordsDone"));
            study.Close();
            await Until(() => !study.IsVisible && schools.IsEffectivelyEnabled, "close the old school's work area");
            schools.SelectedItem = schools.Items.OfType<School>().Single(row => row.Id == access.OtherSchoolId);
            await Signal("release-photo");
            Check(!late.IsVisible && lateImage.Source is null && lateList.ItemCount == 0, "latency after switching schools leaves no old picture or rows");
            schools.SelectedItem = schools.Items.OfType<School>().Single(row => row.Id == access.SchoolId);
            await Until(() => classes.IsEffectivelyVisible && classes.IsEffectivelyEnabled, "class workspace is available after switching back");
            Click(classes);
            await Until(() => entry.IsEffectivelyVisible && entry.IsEffectivelyEnabled, "return to original school");
            Click(entry);
            study = await WindowOf<SchoolStudyWindow>();
            rooms = Find<ListBox>(study, "StudyRooms");
            var reopen = Find<Button>(study, "StudyClassRecords");
            await Until(() => reopen.IsEffectivelyEnabled && rooms.Items.OfType<SchoolStudyRoomRow>().Any(row => row.Room.ClassId == access.SevenId),
                "original class remains available");
            Find<CalendarDatePicker>(study, "StudyJumpDate").SelectedDate = access.Date.ToDateTime(TimeOnly.MinValue);
            await Until(() => reopen.IsEffectivelyEnabled && rooms.Items.OfType<SchoolStudyRoomRow>().Any(row => row.Room.ClassId == access.SevenId &&
                row.Daily.Contains("19:00")), "original school date restored after reopening");
            rooms.SelectedItem = rooms.Items.OfType<SchoolStudyRoomRow>().Single(row => row.Room.ClassId == access.SevenId);
            Click(reopen);
            var removal = await WindowOf<StudyRecordsWindow>();
            var removalPage = RecordsPage(removal);
            var removalList = Find<StudyRecordsView>(removalPage, "Records").FindControl<ListBox>("StudyRecordsList")!;
            var delete = Find<Button>(removalPage, "StudyRecordsDelete");
            await Until(() => removalList.ItemCount == 3 && removalList.IsEffectivelyEnabled, "server confirms the same records");
            removalList.SelectedItem = removalList.Items.OfType<StudyRecordRow>().Single(row => row.PhotoId == access.PhotoId);
            await Until(() => delete.IsEffectivelyEnabled && delete.IsEffectivelyVisible, "photo is loaded and may be deleted");
            Click(delete);
            var summary = Find<TextBlock>(removalPage, "RemoveRecordSummary");
            Check(summary.IsEffectivelyVisible && summary.Text?.Contains("7）班") == true && summary.Text.Contains("拍摄") &&
                summary.Text.Contains("连续超限"), "delete confirmation identifies class, capture time and event");
            await CaptureWithFrame(removal, output, "07-delete-confirmation");
            removalList.SelectedItem = removalList.Items.OfType<StudyRecordRow>().Single(row => row.PhotoId == access.RearPhotoId);
            await Until(() => !summary.IsEffectivelyVisible && !Find<Button>(removalPage, "RemoveRecordConfirm").IsEffectivelyVisible &&
                delete.IsEffectivelyEnabled && delete.IsEffectivelyVisible, "switching between actual photos cancels the previous photo deletion");
            Click(Find<Button>(removalPage, "RemoveRecordConfirm"));
            await Signal("photo-retained-after-selection-change");
            removalList.SelectedItem = removalList.Items.OfType<StudyRecordRow>().Single(row => row.PhotoId == access.PhotoId);
            await Until(() => delete.IsEffectivelyEnabled && delete.IsEffectivelyVisible, "original photo still opens");
            Click(delete);
            Click(Find<Button>(removalPage, "RemoveRecordConfirm"));
            await Until(() => removalList.ItemCount == 2 && removalList.Items.OfType<StudyRecordRow>().Any(row => row.Key == "exit:" + access.ExitId) &&
                removalList.Items.OfType<StudyRecordRow>().Any(row => row.PhotoId == access.RearPhotoId), "deleting the front photo keeps the rear photo and exit record");
            Console.WriteLine("PASS: actual Windows monitoring editor, exact session/class records, narrow restoration and authorization revocation.");
            desktop.Shutdown(0);

            void StudyImpactViewCheck()
            {
                var rows = review.Items.OfType<SchoolStudyImpactView>().ToDictionary(x => x.ClassId);
                Check(rows[access.SevenId].After.Contains("保留 14 天") && rows[access.SevenId].After.Contains("缩短现有") &&
                    rows[access.EightId].After.Contains("保留 30 天") && rows[access.NineId].After.Contains("已关闭"),
                    "inheritance, independent and disabled classes resolve separately in preview");
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
            using var response = await checkpoints.PostAsync("/__monitor/" + step, null);
            Check(response.IsSuccessStatusCode, "Isolated monitoring checkpoint " + step);
        }

        static StudyRecordsPage RecordsPage(StudyRecordsWindow window) => (StudyRecordsPage)window.Content!;
    }
}
