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
    private sealed record HomeworkAccess(string Origin, string SchoolId, string OtherSchoolId, string SchoolCode,
        string Pin, string PhysicsId, bool AllowWrites);

    private static int StartHomeworkSettings(string fixture, string output)
    {
        var access = JsonSerializer.Deserialize<HomeworkAccess>(File.ReadAllText(fixture), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Expected a disposable homework-settings fixture");
        Check(access.AllowWrites && IsLoopback(access.Origin), "homework writes require a disposable loopback fixture");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyHomeworkSettingsDesktopAsync(access, output); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyHomeworkSettingsDesktopAsync(HomeworkAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        var mainHandle = main.TryGetPlatformHandle()?.Handle;
        using var checkpoints = new HttpClient { BaseAddress = new(access.Origin), Timeout = TimeSpan.FromSeconds(35) };
        try
        {
            main.Width = 1160;
            main.Height = 780;
            Find<TextBox>(main, "ServerAddress").Text = access.Origin;
            Find<TextBox>(main, "SchoolCode").Text = access.SchoolCode;
            Find<TextBox>(main, "Username").Text = "admin";
            Find<TextBox>(main, "Password").Text = access.Pin;
            Click(Find<Button>(main, "LoginButton"));
            var schools = Find<ComboBox>(main, "SchoolPicker");
            await Until(() => schools.Items.OfType<School>().Any(item => item.Id == access.OtherSchoolId), "both authorized schools");
            schools.SelectedItem = schools.Items.OfType<School>().Single(item => item.Id == access.SchoolId);
            var classNav = Find<Button>(main, "OpenClassWorkspaceButton");
            await Until(() => classNav.IsEffectivelyVisible && classNav.IsEnabled, "classroom and academics workspace");
            Click(classNav);
            var window = await Open();
            Button B(string name) => Find<Button>(window, name);
            TextBox T(string name) => Find<TextBox>(window, name);
            string L(string name) => Find<TextBlock>(window, name).Text ?? "";
            var rows = Find<ListBox>(window, "HomeworkRows");
            await Until(() => rows.ItemCount > 0 && B("HomeworkAdd").IsEffectivelyEnabled, "school shortcuts loaded");
            Click(B("HomeworkAdd"));
            const string label = "推导例题", contents = "完成课本物理例题推导";
            T("HomeworkLabel").Text = label;
            T("HomeworkWordText").Text = contents;
            T("HomeworkWordGroup").Text = "物理练习";
            var physics = Find<WrapPanel>(window, "HomeworkSubjectChecks").Children.OfType<CheckBox>()
                .Single(box => (string?)box.Tag == access.PhysicsId);
            physics.IsChecked = true;
            await Until(() => B("HomeworkSave").IsEffectivelyEnabled && L("HomeworkDirty").Contains("一个分区"), "physics draft is scoped and dirty");
            var preview = Find<HomeworkSettingsPreview>(window, "HomeworkPreview");
            var subject = preview.FindControl<ComboBox>("HomeworkPreviewSubject")!;
            subject.SelectedItem = subject.Items.Cast<object>().Single(item => item.ToString()?.Contains("物理") == true);
            Check(Find<Border>(window, "HomeworkEditor").IsEffectivelyVisible && Find<Border>(window, "HomeworkPreviewPane").IsEffectivelyVisible &&
                Grid.GetColumn(Find<Border>(window, "HomeworkPreviewPane")) == 1 && !Find<Expander>(window, "HomeworkBatchActions").IsExpanded,
                "wide layout puts trial beside the editor and folds secondary operations");
            await CaptureWithFrame(main, output, "01-wide-word-edit-and-try");

            T("HomeworkWordText").Focus(NavigationMethod.Tab);
            main.Width = 840;
            main.Height = 480;
            await Until(() => B("HomeworkPreviewToggle").IsEffectivelyVisible && main.ClientSize.Height <= 485, "compact homework layout");
            Click(B("HomeworkPreviewToggle"));
            var body = preview.FindControl<TextBox>("HomeworkPreviewBody")!;
            await Until(() => body.IsEffectivelyVisible, "narrow trial panel");
            Check(HomeworkSettingsPreview.MatchesSubject(new("完成", "完成", "动作", [], "INLINE"), access.PhysicsId),
                "globally available terms remain available in a subject preview");
            body.Text = "原尝试";
            body.SelectionStart = 0;
            body.SelectionEnd = 3;
            var quick = preview.GetVisualDescendants().OfType<Control>().Single(control => control.IsEffectivelyVisible &&
                (control is Button button ? Equals(button.Content, label) : control is Border && AutomationProperties.GetName(control) == $"插入 {label}"));
            quick.BringIntoView();
            Check(quick.Focus(NavigationMethod.Tab), "the narrow word can receive keyboard focus");
            if (quick is Button quickButton) Click(quickButton);
            else quick.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await Until(() => body.Text == contents && body.IsFocused, "narrow trial replaces selected text and restores focus");
            await CaptureWithFrame(main, output, "02-narrow-word-trial");
            main.Width = 1160;
            main.Height = 780;
            await Until(() => !B("HomeworkPreviewToggle").IsEffectivelyVisible && T("HomeworkWordText").IsEffectivelyVisible && body.IsEffectivelyVisible,
                "wide layout restores the editor next to the same trial");
            main.Width = 840;
            main.Height = 480;
            await Until(() => B("HomeworkPreviewToggle").IsEffectivelyVisible && body.IsEffectivelyVisible && !T("HomeworkWordText").IsEffectivelyVisible,
                "shrinking preserves the selected trial step");
            Check(body.Text == contents, "resizing preserves the trial text");
            Click(B("HomeworkPreviewToggle"));
            await Until(() => T("HomeworkWordText").IsFocused && T("HomeworkWordText").Text == contents, "narrow return preserves edited row and field focus");

            await Signal("web-deadline");
            Click(B("HomeworkSave"));
            await Until(() => L("HomeworkFeedback").Contains("保存结果待核对") && B("HomeworkRead").IsEffectivelyEnabled,
                "lost write reply and first receipt error preserve the original request");
            Check(!B("HomeworkDiscard").IsEffectivelyVisible && Equals(B("HomeworkBack").Content, "返回，稍后核对") &&
                Find<Border>(window, "HomeworkPendingView").IsEffectivelyVisible && L("HomeworkPendingRequest").StartsWith("请求编号") &&
                !rows.IsEffectivelyVisible, "pending state presents its original number and one clear action to leave");
            await CaptureWithFrame(main, output, "02b-narrow-receipt-pending");
            Click(B("HomeworkBack"));
            await Until(() => !window.IsEffectivelyVisible && Find<Button>(main, "OpenHomeworkSettingsButton").IsFocused, "return to the original entry");
            await Signal("allow-receipt");
            window = await Open();
            await Until(() => L("HomeworkFeedback").Contains("已找回本次保存回执"), "success response lost and recovered by exact user request");
            await Signal("receipt");
            rows = Find<ListBox>(window, "HomeworkRows");
            preview = Find<HomeworkSettingsPreview>(window, "HomeworkPreview");
            Click(B("HomeworkDeadlinesTab"));
            Click(B("HomeworkPreviewToggle"));
            var date = preview.FindControl<DatePicker>("HomeworkPreviewBaseDate")!;
            date.SelectedDate = new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.FromHours(8));
            TextBlock? mondayDeadline = null;
            await Until(() => (mondayDeadline = preview.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(line =>
                line.IsEffectivelyVisible && line.Text?.Contains("2026-11-30 周一 07:30") == true)) is not null,
                "narrow Monday preview uses actual next Monday");
            var dateResults = Find<ScrollViewer>(preview, "HomeworkPreviewScroll");
            dateResults.ScrollToEnd();
            await Until(() => mondayDeadline is { } result && result.TranslatePoint(default, dateResults) is { } position && position.Y >= 0 &&
                position.Y + result.Bounds.Height <= dateResults.Bounds.Height && date.IsEffectivelyVisible,
                "the preview shows the computed next Monday and pinned base date together");
            await CaptureWithFrame(main, output, "03-narrow-monday-preview");
            Click(B("HomeworkPreviewToggle"));
            rows.SelectedIndex = 0;
            T("HomeworkLabel").Text = "本地未提交的时间";
            await Until(() => L("HomeworkDirty").Contains("一个分区"), "deadline draft captured");
            Click(B("HomeworkWordsTab"));
            rows.SelectedIndex = 0;
            T("HomeworkWordText").Text = "本地尚未保存的正文";
            T("HomeworkWordText").Focus(NavigationMethod.Tab);
            await Until(() => L("HomeworkDirty").Contains("两个分区"), "both sections remain dirty after switching");
            await Signal("same-zone");
            Click(B("HomeworkSave"));
            await Until(() => L("HomeworkFeedback").Contains("本次未保存") && !B("HomeworkSave").IsEnabled &&
                L("HomeworkConflictRows").Contains("学校另一次编辑") && L("HomeworkConflictRows").Contains("本地尚未保存的正文"),
                "same-section conflict identifies the differing row and retains both drafts");
            var review = Find<Border>(window, "HomeworkReviewView");
            var difference = Find<TextBlock>(window, "HomeworkConflictRows");
            await Until(() => review.IsEffectivelyVisible && !rows.IsEffectivelyVisible &&
                difference.TranslatePoint(default, window) is { } position && position.Y >= 0 &&
                position.Y + difference.Bounds.Height < window.Bounds.Height && B("HomeworkAdopt").IsEffectivelyVisible,
                "narrow conflict values and confirm action fit on the separate review screen");
            await CaptureWithFrame(main, output, "04-narrow-conflict-draft");
            await Signal("zero-write");

            Click(B("HomeworkReviewBack"));
            await Until(() => !review.IsEffectivelyVisible && T("HomeworkWordText").IsFocused && rows.SelectedIndex == 0 &&
                T("HomeworkWordText").Text == "本地尚未保存的正文", "review return restores the edited row and its field");
            var other = schools.Items.OfType<School>().Single(item => item.Id == access.OtherSchoolId);
            schools.SelectedItem = other;
            await Until(() => (schools.SelectedItem as School)?.Id == access.SchoolId && B("HomeworkDiscard").IsEffectivelyVisible &&
                L("HomeworkDirty").Contains("两个分区"), "switching school keeps both drafts until explicitly discarded");
            Click(B("HomeworkReviewOpen"));
            Click(B("HomeworkAdopt"));
            await Until(() => B("HomeworkSave").IsEffectivelyEnabled && !review.IsEffectivelyVisible && L("HomeworkDirty").Contains("两个分区"),
                "reviewing version keeps both drafts ready for explicit save");
            await Signal("reviewed-no-write");
            Click(B("HomeworkDeadlinesTab"));
            Check(T("HomeworkLabel").Text == "本地未提交的时间", "the independent deadline draft survived conflict review");
            Click(B("HomeworkSave"));
            await Until(() => L("HomeworkFeedback").Contains("已保存") && L("HomeworkDirty").Length == 0, "explicit save commits both reviewed drafts");
            await Signal("saved-after-review");

            Click(B("HomeworkRead"));
            await Signal("wait-old-read");
            schools.SelectedItem = schools.Items.OfType<School>().Single(item => item.Id == access.OtherSchoolId);
            await Until(() => !window.IsEffectivelyVisible, "switching schools closes the clean origin workspace while its read is pending");
            await Signal("allow-old-read");
            if (classNav.IsEffectivelyVisible) Click(classNav);
            window = await Open();
            await Until(() => L("HomeworkScope").Contains("另一所学校") && B("HomeworkAdd").IsEffectivelyEnabled,
                "late response did not cross school boundaries");
            Click(B("HomeworkDeadlinesTab"));
            T("HomeworkLabel").Text = "无学期也可设置";
            await Until(() => B("HomeworkSave").IsEffectivelyEnabled, "no-term draft is ready to save");
            Click(B("HomeworkSave"));
            await Until(() => L("HomeworkFeedback").Contains("已保存"), "settings can be saved without an active term");
            await Signal("no-term");
            await CaptureWithFrame(main, output, "05-other-school-no-term");
            desktop.Shutdown(0);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (desktop.Windows.LastOrDefault(item => item.IsVisible) is { } current)
                try { await CaptureWithFrame(current, output, "desktop-homework-failure"); } catch { }
            desktop.Shutdown(1);
        }

        async Task<HomeworkSettingsView> Open()
        {
            var entry = Find<Button>(main, "OpenHomeworkSettingsButton");
            await Until(() => entry.IsEffectivelyVisible && entry.IsEnabled, "regular homework settings entrance");
            Click(entry);
            var view = await AcademicPageAsync<HomeworkSettingsView>(main);
            Check(mainHandle is not null && mainHandle == main.TryGetPlatformHandle()?.Handle &&
                desktop.Windows.Count(item => item.IsVisible) == 1 && ReferenceEquals(TopLevel.GetTopLevel(view), main),
                "homework editor uses the original HWND and main window controls");
            return view;
        }

        async Task Signal(string name)
        {
            using var response = await checkpoints.PostAsync("/__homework/" + name, null);
            Check(response.IsSuccessStatusCode, "homework settings checkpoint " + name);
        }
    }
}
