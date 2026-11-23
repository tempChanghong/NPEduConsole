using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private sealed record StudyDisplayAccess(string Origin, string SchoolCode, string SchoolId, string Password,
        string GradeId, string SevenId, string EightId, string NineId, DateOnly Date, bool AllowDisplayWrites);

    private static int StartStudyDisplay(string path, string output)
    {
        var access = JsonSerializer.Deserialize<StudyDisplayAccess>(File.ReadAllText(path), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Missing isolated study display fixture.");
        Check(access.AllowDisplayWrites && IsLoopback(access.Origin), "Study display writes require an isolated fixture.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => _ = VerifyStudyDisplayAsync(access, output)))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyStudyDisplayAsync(StudyDisplayAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        using var control = new HttpClient { BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(145) };
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
            await Until(() => schools.Items.OfType<School>().Any(row => row.Id == access.SchoolId), "学校目录已读取");
            schools.SelectedItem = schools.Items.OfType<School>().Single(row => row.Id == access.SchoolId);
            await PressScoped(main, "OpenClassWorkspaceButton");
            await PressScoped(main, "OpenSchoolStudyButton");
            var calendar = await WindowOf<SchoolStudyWindow>();
            var rooms = Find<ListBox>(calendar, "StudyRooms");
            await Until(() => rooms.Items.OfType<SchoolStudyRoomRow>().Any(row => row.Room.ClassId == access.SevenId), "高二七班");
            Find<CalendarDatePicker>(calendar, "StudyJumpDate").SelectedDate = access.Date.ToDateTime(TimeOnly.MinValue);
            await OpenDisplay(access.NineId);
            var closed = await WindowOf<SchoolStudyDisplayWindow>();
            await Until(() => Read(closed, "DisplayHomeworkState").Contains("暂无生效"), "九班已关闭");
            Check(!Find<Button>(closed, "DisplayPlan").IsEffectivelyEnabled && Find<Button>(closed, "DisplayOpenSchedule").IsEffectivelyVisible,
                "仅展示不能启用已关闭自习");
            await PressScoped(closed, "DisplayClose");

            await OpenDisplay(access.EightId);
            var uncalibrated = await WindowOf<SchoolStudyDisplayWindow>();
            await Until(() => Read(uncalibrated, "DisplayNoiseSample").Contains("dBFS"), "原始单位样例使用 dBFS");
            await PressScoped(uncalibrated, "DisplayClose");

            await OpenDisplay(access.SevenId);
            var waiting = await WindowOf<SchoolStudyDisplayWindow>();
            await Signal("display-read-held");
            Check(Ready(waiting, "DisplayClose"), "查看展示期间返回仍可用，即使服务器尚未返回");
            await PressScoped(waiting, "DisplayClose");
            await Until(() => !waiting.IsVisible && Find<Button>(calendar, "StudyClassDisplay").IsFocused, "慢读取可退出并恢复原班焦点");
            await Signal("display-read-cancelled");

            await OpenDisplay(access.SevenId);
            var editor = await WindowOf<SchoolStudyDisplayWindow>();
            var scope = Find<ComboBox>(editor, "DisplayScopeChoice");
            await Until(() => Read(editor, "DisplayHomeworkState").Contains("暂无可展示"), "各班默认使用本班，七班无作业");
            Check(scope.SelectedItem?.ToString()?.StartsWith("年级", StringComparison.Ordinal) == true &&
                Find<TextBlock>(editor, "DisplaySampleTrend").IsEffectivelyVisible, "七班继承优先编辑年级且显示趋势样例");
            Find<ComboBox>(editor, "DisplayNoiseStyle").SelectedIndex = 2;
            Find<ComboBox>(editor, "DisplaySourceMode").SelectedIndex = 1;
            var sourceControl = (StudyDisplaySourcesView)Find<ContentControl>(editor, "DisplaySourceControl").Content!;
            Find<TextBox>(sourceControl, "SourceSearchBox").Text = "DISPLAY-8";
            var sources = Find<ListBox>(sourceControl, "StudySourceListBox");
            await Until(() => sources.SelectedItem is StudyDisplaySourceRow row && row.Option.Id == access.EightId, "可用短代码定位来源");
            sources.Focus();
            sources.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space });
            await Until(() => sourceControl.SelectedIds.SequenceEqual(new[] { access.EightId }), "从实际搜索结果键盘选择来源");
            var search = Find<TextBox>(sourceControl, "SourceSearchBox");
            search.Text = "";
            await Until(() => sources.ItemCount == 4, "清空筛选后保留原选择");
            var visibleSources = sources.Items.OfType<StudyDisplaySourceRow>().ToArray();
            var anchor = Array.FindIndex(visibleSources, row => row.Option.Id == access.EightId);
            var endpoint = anchor >= visibleSources.Length / 2 ? 0 : visibleSources.Length - 1;
            sources.SelectedItem = visibleSources[endpoint];
            sources.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space, KeyModifiers = KeyModifiers.Shift });
            var chosen = visibleSources[Math.Min(anchor, endpoint)..(Math.Max(anchor, endpoint) + 1)];
            Check(sourceControl.SelectedIds.ToHashSet().SetEquals(chosen.Select(row => row.Option.Id)), "Shift+空格从原勾选处连续多选");
            foreach (var row in chosen.Where(row => row.Option.Id != access.EightId))
            {
                sources.SelectedItem = row;
                sources.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space });
            }
            search.Text = "DISPLAY-8";
            Check(sourceControl.SelectedIds.SequenceEqual(new[] { access.EightId }), "缩回单一来源后只保留八班");
            Find<TextBox>(editor, "DisplayRotation").Text = "30";
            await PressScoped(editor, "DisplayPlan");
            await Until(() => Ready(editor, "DisplaySave") && Read(editor, "DisplayHomeworkState").Contains("1 项"), "仅投一份作业");
            Check(Read(editor, "DisplayAssignmentSources").Contains("高二（8）班") && Read(editor, "DisplayAssignmentSources").Contains("物理 A1"),
                "同份作业保留两个命中标签");
            Check(Read(editor, "DisplayNoiseSample").Contains("样例") && Read(editor, "DisplayNoiseSample").Contains("dB(A)"),
                "数值样例显示实际单位口径");
            await Capture(editor, output, "01-wide-grade-and-homework");
            await Signal("legacy");

            editor.Width = 840;
            editor.Height = 480;
            await Until(() => editor.ClientSize is { Width: <= 845, Height: <= 485 }, "840×480 实际窗口");
            await PressScoped(editor, "DisplaySave");
            await Until(() => Read(editor, "DisplayNotice").Contains("本次未保存") && !Ready(editor, "DisplaySave"), "旧网页变更使旧预览零写入");
            Check(Find<TextBox>(editor, "DisplayRotation").Text == "30", "轮播草稿保留");
            await Signal("stale");
            await Capture(editor, output, "02-narrow-preserves-draft");
            await PressScoped(editor, "DisplayPlan");
            await Until(() => Ready(editor, "DisplaySave") && Ready(editor, "DisplayNext"), "重新预览展开长作业");
            await PressScoped(editor, "DisplayNext");
            Check(Read(editor, "DisplayPageNumber").StartsWith("内容预览第 2/", StringComparison.Ordinal), "窄窗片段计数不当作大屏页数");
            var passage = Find<TextBlock>(editor, "DisplayAssignmentText");
            var viewport = Find<ScrollViewer>(editor, "DisplayPreviewScroll");
            await Until(() => passage.TranslatePoint(new Point(), viewport) is { Y: >= 0 } point &&
                point.Y + 40 < viewport.Bounds.Height, "翻页后窄窗可读新段正文");
            await Capture(editor, output, "03-narrow-long-homework");
            await PressScoped(editor, "DisplayToggleSignal");
            Check(Read(editor, "DisplayNoiseSample").Contains("暂无有效") && !Read(editor, "DisplayNoiseSample").Contains("dB"), "无信号样例清除数值");
            await PressScoped(editor, "DisplaySave");
            await Signal("display-write-held");
            Check(!Ready(editor, "DisplayClose"), "写请求等待结果期间仍保护返回");
            editor.Close();
            Check(editor.IsVisible, "窗口关闭同样保护正在提交的写请求");
            await Signal("display-lose-save-reply");
            await Signal("display-receipt-held");
            Check(Ready(editor, "DisplayClose"), "由写入转为只读核对后允许返回");
            await CaptureWithFrame(editor, output, "03a-slow-receipt-can-close");
            editor.Close();
            await Until(() => !editor.IsVisible && Find<Button>(calendar, "StudyClassDisplay").IsFocused, "只读回执可取消并返回原班");
            await Signal("display-receipt-cancelled");
            await OpenDisplay(access.SevenId);
            editor = await WindowOf<SchoolStudyDisplayWindow>();
            editor.Width = 840;
            editor.Height = 480;
            scope = Find<ComboBox>(editor, "DisplayScopeChoice");
            sourceControl = (StudyDisplaySourcesView)Find<ContentControl>(editor, "DisplaySourceControl").Content!;
            search = Find<TextBox>(sourceControl, "SourceSearchBox");
            await Until(() => Read(editor, "DisplayNotice").Contains("本人回执 v3"), "成功回包丢失只读找回");
            await Signal("saved");
            await PressScoped(editor, "DisplayRefreshScreens");
            var screens = Find<ItemsControl>(editor, "DisplayScreenRows");
            await Until(() => screens.Items.OfType<StudyDisplayLine>().Any(row => row.Title == "七班前屏" && row.Detail.Contains("近期观测")) &&
                screens.Items.OfType<StudyDisplayLine>().Any(row => row.Title == "七班后屏" && row.Detail.Contains("待核对")), "前后两屏证据分开");
            await Signal("checked");
            await Capture(editor, output, "04-narrow-two-screens");
            await PressScoped(editor, "ShowDisplaySettings");
            await PressScoped(editor, "DisplayReload");
            var rotation = Find<TextBox>(editor, "DisplayRotation");
            await Until(() => rotation.Text == "60" && rotation.IsEffectivelyEnabled, "本班独立草稿基于年级 v4 的 60 秒");
            scope.SelectedItem = scope.Items.Cast<object>().Single(item => item.ToString()!.Contains("单独设置"));
            Check(Read(editor, "DisplayInheritance").Contains("日常、考试排期、监测和照片"), "独立的影响由现有年级策略决定");
            rotation.Text = "30";
            await Signal("independent-basis");
            await PressScoped(editor, "DisplayPlan");
            await Until(() => Read(editor, "DisplayNotice").Contains("年级当前来源为高二（10）班") && Ready(editor, "DisplayPlan") &&
                rotation.Text == "30" && !sourceControl.SelectedIds.Contains(access.EightId), "首次预览拒绝旧年级来源并保留 30 秒");
            await Signal("independent-stale");
            search.Text = "DISPLAY-10";
            rotation.BringIntoView();
            await Capture(editor, output, "05-narrow-inheritance-conflict");
            Find<ComboBox>(editor, "DisplayNoiseStyle").SelectedIndex = 0;
            await PressScoped(editor, "DisplayPlan");
            await Until(() => Ready(editor, "DisplaySave") && Read(editor, "DisplayHomeworkState").Contains("每页 30 秒") &&
                Read(editor, "DisplayAssignmentSources").Contains("高二（10）班"), "本班独立采用十班来源和管理员改过的 30 秒");
            Check(!Find<TextBlock>(editor, "DisplaySampleTrend").IsEffectivelyVisible, "只显示状态样例无趋势");
            await PressScoped(editor, "DisplaySave");
            await Until(() => Read(editor, "DisplayNotice").Contains("本人回执 v1"), "本班独立回执");
            await Signal("independent");
            Console.WriteLine("PASS: 真实 Windows 展示预览、旧网页冲突、本人回执、原生双屏证据以及继承班明确独立。");
            Environment.ExitCode = 0;

            async Task OpenDisplay(string classId)
            {
                rooms.SelectedItem = rooms.Items.OfType<SchoolStudyRoomRow>().Single(row => row.Room.ClassId == classId);
                var openCompact = Find<Button>(calendar, "StudyRoomOpenButton");
                if (openCompact.IsEffectivelyVisible) Click(openCompact);
                await PressScoped(calendar, "StudyClassDisplay");
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (desktop.Windows.LastOrDefault(window => window.IsVisible) is { } window)
                await Capture(window, output, "study-display-failure");
            Environment.ExitCode = 1;
        }
        finally { desktop.Shutdown(Environment.ExitCode); }

        async Task Signal(string step)
        {
            using var response = await control.PostAsync("/__display/console/" + step, null);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(step + " → " + await response.Content.ReadAsStringAsync());
        }
        async Task<T> WindowOf<T>() where T : Window
        {
            await Until(() => desktop.Windows.OfType<T>().Any(window => window.IsVisible), "打开 " + typeof(T).Name);
            return desktop.Windows.OfType<T>().Single(window => window.IsVisible);
        }
        static bool Ready(Control window, string name) => Find<Button>(window, name) is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true };
    }
}
