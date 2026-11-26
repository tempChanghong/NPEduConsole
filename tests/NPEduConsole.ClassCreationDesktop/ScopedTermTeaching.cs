using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private sealed record TermTeachingAccess(string Origin, string SchoolId, string SchoolCode, string Password, string SpringId,
        string ArchiveId, string PhysicsId, string ChineseId, string GradeId, string SevenId, string EightId, string NineId,
        string ArchiveSevenId, string ArchiveGroupId, string OutgoingId, string IncomingId, string PrimaryId, bool AllowTermTeachingWrites);

    private static int StartScopedTermTeaching(string path, string output)
    {
        var access = JsonSerializer.Deserialize<TermTeachingAccess>(File.ReadAllText(path), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Missing isolated term teaching fixture.");
        Check(access.AllowTermTeachingWrites && IsLoopback(access.Origin), "Term teaching writes require the isolated fixture.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => _ = VerifyScopedTermTeachingAsync(access, output)))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyScopedTermTeachingAsync(TermTeachingAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        using var control = new HttpClient { BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(100) };
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
            await Until(() => schools.Items.OfType<School>().Any(school => school.Id == access.SchoolId), "已加载可管理的学校");
            schools.SelectedItem = schools.Items.OfType<School>().Single(school => school.Id == access.SchoolId);
            await PressScoped(main, "OpenClassWorkspaceButton");
            await Signal("before");
            await PressScoped(main, "OpenTermPreparationButton");
            var terms = await AcademicPageAsync<TermPreparationView>(main);
            var termList = Find<ListBox>(terms, "PreparedTerms");
            await Until(() => termList.Items.OfType<ManagedAcademicTerm>().Count() == 3, "三学期到达界面");
            SelectTerm(access.SpringId);
            await PressScoped(terms, "TermManageTeaching");
            var planner = await AcademicPageAsync<GradeTeachingPlanView>(main);
            var tabs = Find<TabControl>(planner, "PlanTabs");
            var rows = Find<ListBox>(planner, "PlanRows");
            await Until(() => Read(planner, "PlanScope").Contains("春季师资草稿") &&
                Read(planner, "PlanScope").Contains("秋季学期"), "所选草稿及学校启用学期在同一页头");
            await PickSubject(planner, access.PhysicsId, access.SevenId);
            Check((Find<ComboBox>(planner, "PlanGrade").SelectedItem as GradePlanLabel)?.Id == access.GradeId, "恢复具体年级 ID");
            SelectRow(rows, access.SevenId);
            Find<ComboBox>(planner, "PlanCompulsory").SelectedIndex = 1;
            await PressScoped(planner, "PlanStageRules");
            tabs.SelectedIndex = 1;
            Find<TextBox>(planner, "PlanGroupName").Text = "物理 A1";
            Find<TextBox>(planner, "PlanGroupCode").Text = "SPRING-A1";
            var sourceList = Find<ListBox>(planner, "PlanSources");
            sourceList.SelectedItem = sourceList.Items.Cast<object>().Single(item => item.ToString()!.Contains("SCOPED-8"));
            Find<CheckBox>(planner, "PlanSetSourceRules").IsChecked = true;
            await PressScoped(planner, "PlanStageGroup");
            tabs.SelectedIndex = 2;
            var target = Find<ComboBox>(planner, "PlanTeachingTarget");
            target.SelectedItem = target.Items.Cast<object>().Single(item => item.ToString() == "物理 A1");
            await StageTeacher(planner, access.PrimaryId, 0);
            await StageTeacher(planner, access.OutgoingId, 1);

            await PressScoped(planner, "PlanPreview");
            var review = Find<TermTeachingReviewView>(planner, "PlanScopedReview");
            await Until(() => Find<Button>(planner, "PlanSave").IsEffectivelyEnabled && review.IsEffectivelyVisible,
                "同一份方案显示规则、教学班和两位任课");
            var firstReview = Find<ListBox>(review, "TermTeachingReviewList");
            Check(firstReview.Items.OfType<TermTeachingReviewLine>().Any(row => row.Key == $"{access.SevenId}:{access.PhysicsId}" &&
                row.Issue?.Contains("本学期的任课页") == true && !row.Issue.Contains("学校总览")),
                "春季预览将七班缺教师引导至春季");
            await Capture(planner, output, "01-wide-draft-plan");
            await PressScoped(planner, "PlanSave");
            await Until(() => Read(planner, "PlanNotice").Contains("整份方案已保存") &&
                Find<Button>(planner, "PlanPreview").Content?.ToString() == "预览 0 项调整", "按本人原号核对到回执");
            await Until(() => Find<Button>(planner, "PlanContinueTeaching").IsEffectivelyVisible &&
                Read(planner, "PlanNotice").Contains("本学期的任课页"), "结果仅指向所选学期");
            await PressScoped(planner, "PlanContinueTeaching");
            Check(tabs.SelectedIndex == 2 && rows.SelectedItem is GradeTeachingPlanRowView { ClassId: var continued } && continued == access.SevenId,
                "补任课按同一学期、班与学科定位");
            await Signal("prepared");

            tabs.SelectedItem = Find<TabItem>(planner, "PlanClassSummaryTab");
            var byClass = Find<ComboBox>(planner, "PlanSummaryClass");
            byClass.SelectedItem = byClass.Items.Cast<object>().Single(item => item.ToString()!.Contains("SCOPED-7"));
            var allSubjects = Find<ListBox>(planner, "PlanClassSubjects");
            await Until(() => allSubjects.Items.OfType<TermTeachingSubjectLine>().Any(item => item.Id == access.PhysicsId &&
                item.Detail.Contains("随行政班 · 必修")) && allSubjects.Items.OfType<TermTeachingSubjectLine>().Any(item =>
                item.Id == access.ChineseId && item.Detail.Contains("随行政班 · 必修")), "七班全科保留原语文必修");
            await Capture(planner, output, "02-seven-all-subjects");
            tabs.SelectedIndex = 1;
            var groups = Find<ComboBox>(planner, "PlanExistingGroup");
            var a1 = groups.Items.OfType<GradePlanCourse>().Single(item => item.Code == "SPRING-A1");
            groups.SelectedItem = a1;
            Find<TextBox>(planner, "PlanGroupName").Text = "物理 A1 校订";
            await PressScoped(planner, "PlanStageGroup");
            main.Width = 840;
            main.Height = 480;
            await Until(() => main.ClientSize is { Width: <= 845, Height: <= 485 }, "840×480 桌面");
            await PressScoped(planner, "PlanPreview");
            await Until(() => Find<Button>(planner, "PlanSave").IsEffectivelyEnabled, "有可提交的原预览");
            await Signal("web-sources");
            await PressScoped(planner, "PlanSave");
            await Until(() => Read(planner, "PlanNotice").Contains("整份零写入") && review.SelectedKey == a1.Id,
                "并发修改定位到具体走班");
            var reviewList = Find<ListBox>(review, "TermTeachingReviewList");
            await Until(() => reviewList.Items.OfType<TermTeachingReviewLine>().Any(item => item.Key == a1.Id &&
                item.Before.Contains("高二（9）班") && item.Issue?.Contains("未覆盖") == true), "对照服务器新来源");
            await Until(() => review.IsKeyboardFocusWithin, "窄窗冲突行取得键盘焦点");
            await Signal("zero-write");
            await Capture(planner, output, "03-compact-source-conflict");
            await PressScoped(planner, "PlanReset");
            await Until(() => Find<Button>(planner, "PlanBack").IsEffectivelyEnabled &&
                Find<Button>(planner, "PlanPreview").Content?.ToString() == "预览 0 项调整", "明确结束旧草稿");

            tabs.SelectedItem = Find<TabItem>(planner, "PlanStaffTab");
            var staffClass = Find<ComboBox>(planner, "PlanStaffClass");
            staffClass.SelectedItem = staffClass.Items.Cast<object>().Single(item => item.ToString()!.Contains("SCOPED-8"));
            await Until(() => Read(planner, "PlanClassStaffText").Contains("卸任王老师"), "按八班 ID 找到现任");
            await PressScoped(planner, "PlanOpenClassStaff");
            var staff = await WindowOf<StaffResponsibilitiesWindow>();
            staff.Width = 840;
            staff.Height = 480;
            await AddTeacher(staff, access.IncomingId);
            await ChangeRole(staff, access.IncomingId, 0);
            await ChangeRole(staff, access.OutgoingId, 2);
            await PressScoped(staff, "StaffPrimaryAction");
            await Until(() => Find<Button>(staff, "StaffPrimaryAction").Content?.ToString() == "确认保存" &&
                Find<ItemsControl>(staff, "StaffAdvisories").Items.OfType<string>().Any(item => item.Contains("卸任王老师") &&
                    item.Contains("独立成员") && item.Contains("任课")), "交接预览分开展示保留来源");
            await Capture(staff, output, "04-compact-class-handoff");
            await PressScoped(staff, "StaffPrimaryAction");
            await Until(() => Read(staff, "StaffStatus").Contains("服务器当前职责与本次提交一致"), "岗位丢回包后只读核对");
            await Signal("handoff");
            await PressScoped(staff, "StaffClose");
            await Until(() => Read(planner, "PlanClassStaffText").Contains("接任李老师") &&
                Find<Button>(planner, "PlanOpenClassStaff").IsFocused, "返回八班并恢复焦点，仅重读岗位");
            await PressScoped(planner, "PlanBack");
            await Until(() => !planner.IsVisible && Find<Button>(terms, "TermManageTeaching").IsEffectivelyEnabled, "回到学期列表");

            SelectTerm(access.ArchiveId);
            await PressScoped(terms, "TermManageTeaching");
            planner = await AcademicPageAsync<GradeTeachingPlanView>(main);
            tabs = Find<TabControl>(planner, "PlanTabs");
            rows = Find<ListBox>(planner, "PlanRows");
            await PickSubject(planner, access.PhysicsId, access.ArchiveSevenId);
            tabs.SelectedIndex = 1;
            var archiveGroups = Find<ComboBox>(planner, "PlanExistingGroup");
            archiveGroups.SelectedItem = archiveGroups.Items.OfType<GradePlanCourse>().Single(item => item.Id == access.ArchiveGroupId);
            await Until(() => Read(planner, "PlanGroupImpact").Contains("1 条发布历史"), "历史教学班原发布证据可读");
            Check(!Find<Button>(planner, "PlanStageGroup").IsEnabled, "归档默认不可编辑");
            await PressScoped(planner, "PlanEditArchive");
            await PickSubject(planner, access.ChineseId, access.ArchiveSevenId);
            tabs.SelectedIndex = 0;
            SelectRow(rows, access.ArchiveSevenId);
            Find<ComboBox>(planner, "PlanCompulsory").SelectedIndex = 2;
            await PressScoped(planner, "PlanStageRules");
            await PressScoped(planner, "PlanPreview");
            await Capture(planner, output, "05-archived-rule-preview");
            await PressScoped(planner, "PlanSave");
            await Until(() => Read(planner, "PlanNotice").Contains("整份方案已保存"), "归档班逐科更正成功");
            await Signal("archive");
            await PressScoped(planner, "PlanBack");
            await Until(() => !planner.IsVisible && Find<Button>(terms, "TermManageTeaching").IsEffectivelyEnabled, "归档校订返回");

            SelectTerm(access.SpringId);
            await PressScoped(terms, "TermStatusActivate");
            var status = await WindowOf<TermStatusWindow>();
            await PressScoped(status, "StatusCommit");
            await Until(() => Read(status, "StatusAfterLabel") == "服务器确认的结果", "正常启用春季");
            await Signal("active-spring");
            await Capture(status, output, "06-spring-activated");
            Console.WriteLine("PASS: Windows and Edge with isolated KV/PostgreSQL, specified teaching, responsibilities, legacy concurrency and spring student/teacher access.");
            Environment.ExitCode = 0;

            void SelectTerm(string id) => termList.SelectedItem = termList.Items.OfType<ManagedAcademicTerm>().Single(term => term.Id == id);
            async Task Signal(string step)
            {
                using var response = await control.PostAsync("/fixture/teaching/" + step, null);
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
                await Capture(window, output, "term-teaching-failure");
            Environment.ExitCode = 1;
        }
        finally { desktop.Shutdown(Environment.ExitCode); }
    }

    private static async Task PickSubject(Control planner, string subjectId, string classId)
    {
        var picker = Find<ComboBox>(planner, "PlanSubject");
        var rows = Find<ListBox>(planner, "PlanRows");
        await Until(() => picker.IsEffectivelyEnabled && picker.Items.OfType<GradePlanLabel>().Any(row => row.Id == subjectId), "选择学科");
        picker.SelectedItem = picker.Items.OfType<GradePlanLabel>().Single(row => row.Id == subjectId);
        await Until(() => picker.IsEffectivelyEnabled && rows.Items.OfType<GradeTeachingPlanRowView>().Any(row =>
            row.Key == $"{classId}:{subjectId}"), "学期内按班和科 ID 定位");
    }

    private static void SelectRow(ListBox rows, string id)
    {
        rows.SelectedItems?.Clear();
        rows.SelectedItem = rows.Items.OfType<GradeTeachingPlanRowView>().Single(row => row.ClassId == id);
    }

    private static async Task StageTeacher(Control planner, string teacherId, int role)
    {
        var teachers = Find<ComboBox>(planner, "PlanTeacher");
        teachers.SelectedItem = teachers.Items.OfType<GradePlanTeacher>().Single(row => row.Id == teacherId);
        Find<ComboBox>(planner, "PlanTeacherRole").SelectedIndex = role;
        await PressScoped(planner, "PlanStageTeacher");
    }
}
