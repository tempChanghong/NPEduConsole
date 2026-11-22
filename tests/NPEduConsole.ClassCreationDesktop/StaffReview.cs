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
    private sealed record StaffReviewAccess(string Origin, string SchoolId, string SchoolCode, string OtherSchoolId, string OtherSchoolName, string Password, string FallId,
        string SpringId, string GradeId, string FallEightId, string SevenId, string EightId, string SubjectId, string TeacherId,
        string FormerId, string ReplacementId,
        string ArchivedId, string ArchivedEightId, string ArchivedGradeId,
        bool AllowStaffReviewWrites);

    private static int StartStaffReview(string path, string output)
    {
        var access = JsonSerializer.Deserialize<StaffReviewAccess>(File.ReadAllText(path), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Missing isolated staff review fixture.");
        Check(access.AllowStaffReviewWrites && IsLoopback(access.Origin), "Staff review writes require the isolated fixture.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => _ = VerifyStaffReviewAsync(access, output)))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyStaffReviewAsync(StaffReviewAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        using var control = new HttpClient { BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(100) };
        const string classIssue = "HOMEROOM_NOT_TEACHING";
        string springIssue = StaffReviewIssue.IssueKey(classIssue, access.GradeId, access.EightId, access.TeacherId);
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
            await Until(() => schools.Items.OfType<School>().Any(school => school.Id == access.SchoolId), "学校已授权");
            schools.SelectedItem = schools.Items.OfType<School>().Single(school => school.Id == access.SchoolId);
            var overview = Find<SchoolOverviewView>(main, "SchoolOverviewWorkspace");
            await PressScoped(overview, "ShowAllOverviewButton");
            var tasks = Find<ListBox>(overview, "OverviewTasksList");
            await Until(() => tasks.Items.OfType<OverviewTaskItem>().Any(row => row.Task.ClassId == access.FallEightId &&
                row.Task.AccountId == access.TeacherId && row.Task.Code == classIssue), "总览对应当前学期八班的提醒");
            var task = tasks.Items.OfType<OverviewTaskItem>().Single(row => row.Task.ClassId == access.FallEightId &&
                row.Task.AccountId == access.TeacherId && row.Task.Code == classIssue);
            Check(task.Task.Action == "OPEN_STAFF_REVIEW", "总览使用同一个师资入口");
            tasks.ScrollIntoView(task);
            await Until(() => tasks.GetVisualDescendants().OfType<Button>().Any(button => ReferenceEquals(button.Tag, task)), "待办按钮在视口");
            Click(tasks.GetVisualDescendants().OfType<Button>().First(button => ReferenceEquals(button.Tag, task)));
            var review = await WindowOf<StaffReviewWindow>();
            var issues = Find<StaffReviewList>(review, "ReviewIssues");
            await Until(() => Read(review, "ReviewScope").Contains("启用中") &&
                issues.SelectedKey?.Contains(access.FallEightId, StringComparison.Ordinal) == true, "总览按实际班级和教师落到秋季");
            await PressScoped(review, "ReviewClose");
            await Until(() => !review.IsVisible, "返回总览");
            await PressScoped(main, "OpenClassWorkspaceButton");
            await PressScoped(main, "OpenStaffReviewButton");
            review = await WindowOf<StaffReviewWindow>();
            issues = Find<StaffReviewList>(review, "ReviewIssues");
            var terms = Find<ComboBox>(review, "ReviewTerm");
            await Until(() => terms.Items.OfType<StaffReviewTerm>().Any(term => term.Id == access.SpringId), "可选择春季草稿");
            terms.SelectedItem = terms.Items.OfType<StaffReviewTerm>().Single(term => term.Id == access.SpringId);
            var issueList = Find<ListBox>(issues, "StaffReviewListBox");
            await Until(() => issueList.Items.OfType<StaffReviewLine>().Any(row => row.Key == springIssue) &&
                Read(review, "ReviewScope").Contains("春季师资核对 · 草稿"), "春季八班可从独立提醒定位");
            issueList.SelectedItem = issueList.Items.OfType<StaffReviewLine>().Single(row => row.Key == springIssue);
            await Capture(review, output, "01-wide-spring-diagnostics");
            await Signal("before");

            var rules = await Rules();
            Find<CheckBox>(rules, "PolicyHomeTeach").IsChecked = false;
            await Preview(rules);
            Check(RuleRows(rules).Any(row => row.Contains("消失") && row.Contains("春季班主任周老师")), "预览列出实际影响教师");
            await Signal("web-write");
            await PressScoped(rules, "PolicyPrimary");
            await Until(() => Read(rules, "PolicyStatus").Contains("本账号的规则保存回执") &&
                RuleRows(rules).Any(row => row.Contains("本人当时 关闭，服务器现在 启用")), "掉线后本人回执与后续回改分开");
            await Capture(rules, output, "02-wide-lost-reply-with-later-change");
            await Signal("rules-only");
            await CloseRules(rules);

            review.Width = 840;
            review.Height = 480;
            await Until(() => review.ClientSize is {Width: <= 845, Height: <= 485}, "窄窗列表");
            rules = await Rules();
            rules.Width = 840;
            rules.Height = 480;
            Find<CheckBox>(rules, "PolicyHomeTeach").IsChecked = false;
            Find<CheckBox>(rules, "PolicyLeaderHome").IsChecked = false;
            await Preview(rules);
            await Signal("same-field");
            await PressScoped(rules, "PolicyPrimary");
            await Until(() => Read(rules, "PolicyStatus").Contains("整次零写入") &&
                RuleRows(rules).Any(row => row.Contains("学校当前 关闭")) &&
                RuleRows(rules).Any(row => row.Contains("学校当前 启用／草稿 关闭")), "同字段冲突保留两项草稿");
            await Capture(rules, output, "03-compact-conflicting-rules");
            await Signal("stale-checked");
            await PressScoped(rules, "PolicyDiscard");
            await CloseRules(rules);

            rules = await Rules();
            rules.Width = 840;
            rules.Height = 480;
            Find<CheckBox>(rules, "PolicyHomeTeach").IsChecked = false;
            await Preview(rules);
            await Signal("drop-before-write");
            await PressScoped(rules, "PolicyPrimary");
            await Until(() => Read(rules, "PolicyStatus").Contains("当前值与本次提交一致，暂未找到本账号") &&
                Find<Button>(rules, "PolicyDiscard").Content?.ToString() == "结束核对", "同值也不认领他人保存");
            await Capture(rules, output, "04-compact-current-value-without-own-receipt");
            await Signal("no-own-receipt");
            await PressScoped(rules, "PolicyDiscard");
            await CloseRules(rules);

            await Until(() => issueList.Items.OfType<StaffReviewLine>().Any(row => row.Key == springIssue), "春季八班提醒仍可办理");
            issueList.SelectedItem = issueList.Items.OfType<StaffReviewLine>().Single(row => row.Key == springIssue);
            issues.FocusSelected();
            await Until(() => issues.IsKeyboardFocusWithin, "问题列表获得键盘焦点");
            issueList.RaiseEvent(new KeyEventArgs {RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = issueList});
            await Until(() => Find<Button>(review, "ReviewBackList").IsEffectivelyVisible &&
                Read(review, "ReviewDetailReason").Contains("尚无本年级任课"), "键盘打开正确的春季八班详情");
            await PressScoped(review, "ReviewResolve");
            var roomPicker = Find<ComboBox>(review, "ReviewTeachingRoom");
            await Until(() => roomPicker.Items.Cast<object>().Any(row => row.ToString()!.Contains("STAFF-7")), "列表提供同年级七班");
            Check(roomPicker.SelectedItem is null, "未默认视为应给原班任课");
            roomPicker.SelectedItem = roomPicker.Items.Cast<object>().Single(row => row.ToString()!.Contains("STAFF-7"));
            var subjects = Find<ComboBox>(review, "ReviewTeachingSubject");
            subjects.SelectedItem = subjects.Items.OfType<GradePlanLabel>().Single(row => row.Id == access.SubjectId);
            await Capture(review, output, "05-compact-choose-spring-seven");
            await PressScoped(review, "ReviewOpenTeaching");
            var teaching = await WindowOf<GradeTeachingPlanWindow>();
            var teacher = Find<ComboBox>(teaching, "PlanTeacher");
            var classes = Find<ListBox>(teaching, "PlanRows");
            await Until(() => teacher.SelectedItem is GradePlanTeacher row && row.Id == access.TeacherId &&
                classes.SelectedItem is GradeTeachingPlanRowView room && room.ClassId == access.SevenId &&
                ((GradePlanLabel?)Find<ComboBox>(teaching, "PlanSubject").SelectedItem)?.Id == access.SubjectId, "任课保留指定教师、七班及物理");
            await PressScoped(teaching, "PlanStageTeacher");
            await PressScoped(teaching, "PlanPreview");
            await PressScoped(teaching, "PlanSave");
            await Until(() => Read(teaching, "PlanNotice").Contains("整份方案已保存"), "正式保存春季七班任课");
            await PressScoped(teaching, "PlanBack");
            await Until(() => !teaching.IsVisible && Read(review, "ReviewStatus").Contains("原提醒已消除") &&
                !issueList.Items.OfType<StaffReviewLine>().Any(row => row.Key == springIssue), "返回同一学期并重新核对");
            await Until(() => issues.IsKeyboardFocusWithin && issueList.SelectedItem is StaffReviewLine,
                "原问题消失后焦点落到相邻行");
            await Capture(review, output, "06-compact-after-teaching-focus");
            await Signal("teaching-checked");

            string formerKey = StaffReviewIssue.IssueKey(classIssue, access.GradeId, access.EightId, access.FormerId);
            issueList.SelectedItem = issueList.Items.OfType<StaffReviewLine>().Single(row => row.Key == formerKey);
            await PressScoped(review, "ReviewOpenDetail");
            await PressScoped(review, "ReviewHandoff");
            var handoff = await WindowOf<StaffResponsibilitiesWindow>();
            var currentRoles = Find<ListBox>(handoff, "StaffCurrentRows");
            await Until(() => currentRoles.SelectedItem is StaffEditorRow original && original.Teacher.Id == access.FormerId &&
                Read(handoff, "StaffScope").Contains("春季师资核对"), "缺任课提醒按春季八班和原协同教师打开岗位");
            await AddTeacher(handoff, access.ReplacementId);
            await ChangeRole(handoff, access.ReplacementId, 1);
            await ChangeRole(handoff, access.FormerId, 2);
            await PressScoped(handoff, "StaffPrimaryAction");
            await Until(() => Find<Button>(handoff, "StaffPrimaryAction").Content?.ToString() == "确认保存", "明确同岗位交接");
            await Capture(handoff, output, "07-spring-handoff-from-teaching-reminder");
            await PressScoped(handoff, "StaffPrimaryAction");
            await Until(() => Read(handoff, "StaffStatus").Contains("职责已保存"), "只保存春季同班岗位");
            await PressScoped(handoff, "StaffClose");
            await Until(() => !handoff.IsVisible && issues.IsKeyboardFocusWithin &&
                !issueList.Items.OfType<StaffReviewLine>().Any(row => row.Key == formerKey) &&
                issueList.Items.OfType<StaffReviewLine>().Any(row => row.Key ==
                    StaffReviewIssue.IssueKey(classIssue, access.GradeId, access.EightId, access.ReplacementId)),
                "交接返回后旧提醒消失，新提醒显示并恢复焦点");
            await Capture(review, output, "08-compact-after-handoff-focus");
            await Signal("handoff-only");

            terms.SelectedItem = terms.Items.OfType<StaffReviewTerm>().Single(term => term.Id == access.ArchivedId);
            string archiveKey = StaffReviewIssue.IssueKey(classIssue, access.ArchivedGradeId, access.ArchivedEightId, access.FormerId);
            await Until(() => issueList.Items.OfType<StaffReviewLine>().Any(row => row.Key == archiveKey), "归档学期同名八班身份独立");
            issueList.SelectedItem = issueList.Items.OfType<StaffReviewLine>().Single(row => row.Key == archiveKey);
            await PressScoped(review, "ReviewOpenDetail");
            await Until(() => Find<Button>(review, "ReviewEditArchive").IsEffectivelyVisible, "归档先浏览");
            Check(!Find<Button>(review, "ReviewHandoff").IsEffectivelyVisible, "未明确更正前不开放归档岗位交接");
            await PressScoped(review, "ReviewEditArchive");
            await PressScoped(review, "ReviewHandoff");
            handoff = await WindowOf<StaffResponsibilitiesWindow>();
            await Until(() => Read(handoff, "StaffScope").Contains("归档师资核对") &&
                Find<ListBox>(handoff, "StaffCurrentRows").SelectedItem is StaffEditorRow selected && selected.Teacher.Id == access.FormerId,
                "明确更正后按归档同名班和账号打开");
            await PressScoped(handoff, "StaffClose");
            await Until(() => !handoff.IsVisible && Find<Button>(review, "ReviewRules").IsEffectivelyEnabled, "返回归档核对");
            await Signal("archive-explicit");

            await PressScoped(review, "ReviewClose");
            await Signal("hold-old-school");
            await PressScoped(main, "OpenStaffReviewButton");
            var oldSchoolWindow = await WindowOf<StaffReviewWindow>();
            await Signal("await-old-school");
            schools.SelectedItem = schools.Items.OfType<School>().Single(school => school.Id == access.OtherSchoolId);
            await Until(() => !oldSchoolWindow.IsVisible, "切校关闭旧校核对模态窗口");
            await PressScoped(main, "OpenClassWorkspaceButton");
            await PressScoped(main, "OpenStaffReviewButton");
            review = await WindowOf<StaffReviewWindow>();
            await Until(() => Read(review, "ReviewScope").Contains(access.OtherSchoolName) &&
                Read(review, "ReviewScope").Contains("尚未建立学期"), "新校无学期空态和入口");
            await Signal("release-old-school");
            await PressScoped(review, "ReviewRules");
            rules = await WindowOf<StaffPolicyWindow>();
            Check(Read(rules, "PolicyScope").Contains(access.OtherSchoolName) && Read(rules, "PolicyScope").Contains("未选学期"),
                "迟到结果没有改变规则窗口的学校与学期");
            Find<CheckBox>(rules, "PolicyHomeTeach").IsChecked = false;
            await Preview(rules);
            await PressScoped(rules, "PolicyPrimary");
            await Until(() => Read(rules, "PolicyStatus").Contains("本账号的规则保存回执"), "无学期可以保存本校规则");
            await Capture(rules, output, "09-no-term-school-rule-saved");
            await Signal("no-term-school");
            await Signal("web-revocation");
            Console.WriteLine("PASS: Windows staff review, Edge legacy administration, real teacher login and PostgreSQL.");
            Environment.ExitCode = 0;

            async Task<StaffPolicyWindow> Rules()
            {
                await PressScoped(review, "ReviewRules");
                var window = await WindowOf<StaffPolicyWindow>();
                await Until(() => Read(window, "PolicyScope").Contains("春季师资核对"), "全校规则使用春季预览");
                return window;
            }
            async Task CloseRules(StaffPolicyWindow window)
            {
                await PressScoped(window, "PolicyBack");
                await Until(() => !window.IsVisible && Find<Button>(review, "ReviewRules").IsEffectivelyEnabled, "返回师资核对");
            }
            async Task Signal(string step)
            {
                using var response = await control.PostAsync("/fixture/staff/" + step, null);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException(step + " → " + await response.Content.ReadAsStringAsync());
            }
            async Task<T> WindowOf<T>() where T : Window
            {
                await Until(() => desktop.Windows.OfType<T>().Any(window => window.IsVisible), "打开 " + typeof(T).Name);
                return desktop.Windows.OfType<T>().Single(window => window.IsVisible);
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (desktop.Windows.LastOrDefault(window => window.IsVisible) is { } window)
                await Capture(window, output, "staff-review-failure");
            Environment.ExitCode = 1;
        }
        finally { desktop.Shutdown(Environment.ExitCode); }

        static IEnumerable<string> RuleRows(Window window) => Find<ItemsControl>(window, "PolicyImpactRows").Items.OfType<string>();
        static async Task Preview(Window window)
        {
            await PressScoped(window, "PolicyPrimary");
            await Until(() => Find<Button>(window, "PolicyPrimary").Content?.ToString() == "确认保存全校规则", "规则预览已生成");
        }
    }
}
