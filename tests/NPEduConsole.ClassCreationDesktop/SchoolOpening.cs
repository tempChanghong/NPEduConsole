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
    private sealed record OpeningAccess(string Origin, string SchoolId, string SchoolCode, string Pin, string OtherId,
        string TermId, string GradeId, string SevenId, string EightId, string OldEightId, string PhysicsId, string ChemistryId,
        string GroupId, string TeacherId, string SameNameId, string FrontId, string RearId, bool Isolated);

    private static int StartSchoolOpening(string fixturePath, string output)
    {
        var data = JsonSerializer.Deserialize<OpeningAccess>(File.ReadAllText(fixturePath), JsonSerializerOptions.Web)!;
        Check(data.Isolated && IsLoopback(data.Origin), "Use the disposable school opening fixture.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifySchoolOpeningAsync(data, output); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifySchoolOpeningAsync(OpeningAccess data, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        var schools = Find<ComboBox>(main, "SchoolPicker");
        var handle = main.TryGetPlatformHandle()?.Handle;
        using var fixture = new HttpClient { BaseAddress = new Uri(data.Origin), Timeout = TimeSpan.FromSeconds(80) };
        try
        {
            main.Width = 1160;
            main.Height = 780;
            await Login("admin");
            SelectSchool(data.SchoolId);
            var view = await OpenChecklist("2 个启用行政班");
            Check(TopLevel.GetTopLevel(view) is Window owner && owner.TryGetPlatformHandle()?.Handle == handle,
                "开学配置与学校总览保持同一 Windows 窗口");
            Click(Find<Button>(view, "OpeningCheckAddresses"));
            await Until(() => Ready(view, "OpeningBrowserCheck"), "显式打开网页地址核对");
            Check(Text(view, "OpeningAddress").Contains(data.Origin) && string.IsNullOrWhiteSpace(Find<TextBox>(view, "OpeningWebsite").Text),
                "网页未知时留空，不把 KV 地址当成师生网页");
            await CaptureWithFrame(main, output, "00-wide-addresses-unverified");
            Click(Find<Button>(view, "OpeningBackToList"));
            Click(Find<Button>(view, "OpeningPrimary"));
            var import = await AcademicPageAsync<OrganizationImportView>(main);
            await Until(() => Text(import, "OrgScope").Contains("秋季学期"), "组织导入定位原启用学期");
            Check(!Find<ComboBox>(import, "OrgTerm").IsEnabled, "由清单进入固定真实学期");
            Click(Find<Button>(import, "OrgClose"));
            await Until(() => view.IsEffectivelyVisible && Ready(view, "OpeningRefresh"), "返回原开学配置");
            Select(view, $"class/{data.EightId}");
            Click(Find<Button>(view, "OpeningHandleRow"));
            var structure = await AcademicPageAsync<TermClassManagementView>(main);
            await Until(() => Text(structure, "StructureTarget").Contains("秋季学期") &&
                Text(structure, "StructureClassName").Contains("高二（8）班"), "班级配置回跳到所选学期的原班");
            Click(Find<Button>(structure, "StructureClose"));
            await WaitRow(view, $"class/{data.EightId}");

            Click(Find<Button>(view, "OpeningTeachers"));
            await Until(() => Rows(view).Any(row => row.Key.StartsWith("teaching/", StringComparison.Ordinal)), "教师按实际记录读取");
            Check(Rows(view).Any(row => row.Key == $"access/{data.SevenId}/{data.SameNameId}") &&
                Rows(view).Any(row => row.Target?.ClassId == data.SevenId && row.Target.Action == OpeningAction.Teaching) &&
                !Rows(view).Any(row => row.Key.Contains(data.OldEightId, StringComparison.Ordinal)),
                "七班已有直接访问仍缺任课，且归档同码班不出现在当前清单");
            Check(Rows(view).Any(row => row.Key.StartsWith("issue/", StringComparison.Ordinal) &&
                row.Target?.AccountId == data.SameNameId && row.Summary.Contains("opening-b") && row.Summary.Contains("副组长")),
                "同名教师的岗位提醒同时显示实际账号和副职");
            var physicsKey = $"teaching/{data.EightId}/{data.PhysicsId}/{data.TeacherId}";
            Select(view, physicsKey);
            await CaptureWithFrame(main, output, "01-wide-physics-teacher");
            var teaching = await OpenTeaching(view, "物理", data.TeacherId);
            Check(Text(teaching, "TeachingContext").Contains("高二（8）班"), "指定八班物理而非同码历史班");
            Click(Find<Button>(teaching, "CloseTeachingButton"));
            await WaitRow(view, physicsKey);

            main.Width = 840;
            main.Height = 480;
            await Until(() => main.ClientSize is { Width: <= 845, Height: <= 485 } && Ready(view, "OpeningShowDetail"), "真实 840×480 事项列表");
            var chemistryKey = $"teaching/{data.GroupId}/{data.ChemistryId}/{data.TeacherId}";
            Select(view, chemistryKey);
            Click(Find<Button>(view, "OpeningShowDetail"));
            await Until(() => Ready(view, "OpeningHandleRow"), "窄窗展开走班任课");
            await CaptureWithFrame(main, output, "02-narrow-course-target");
            teaching = await OpenTeaching(view, "化学", data.TeacherId);
            Check(Text(teaching, "TeachingContext").Contains("化学 A1"), "走班精确定位真实教学班");
            Click(Find<Button>(teaching, "CloseTeachingButton"));
            await WaitRow(view, chemistryKey);
            AssertInClient(main, Find<Button>(view, "OpeningHandleRow"), "原任课办理位置", 1);
            Click(Find<Button>(view, "OpeningBackToList"));
            await Until(() => SelectedRowHasFocus(view, chemistryKey), "窄窗返回原走班行焦点");

            await CheckDuty($"duty/CLASS/{data.EightId}/{data.TeacherId}", data.TeacherId, "高二（8）班", "03-narrow-class-duty");
            await CheckDuty($"duty/GRADE/{data.GradeId}/{data.SameNameId}", data.SameNameId, "高二", "04-narrow-grade-duty");

            var missing = Rows(view).Single(row => row.Target?.ClassId == data.SevenId && row.Target.Action == OpeningAction.Teaching);
            Select(view, missing.Key);
            Click(Find<Button>(view, "OpeningShowDetail"));
            Click(Find<Button>(view, "OpeningHandleRow"));
            teaching = await AcademicPageAsync<TeachingView>(main);
            await Until(() => Text(teaching, "TeachingContext").Contains("高二（7）班"), "打开尚缺任课的七班");
            await Signal("staffed");
            Click(Find<Button>(teaching, "CloseTeachingButton"));
            await Until(() => Ready(view, "OpeningRefresh") && Rows(view).Any(row =>
                row.Key == $"teaching/{data.SevenId}/{data.PhysicsId}/{data.SameNameId}") &&
                !Rows(view).Any(row => row.Key == missing.Key), "从业务页返回后消除已处理的原缺配项");
            Click(Find<Button>(view, "OpeningClassrooms"));
            var frontKey = $"screen/{data.FrontId}";
            var rearKey = $"screen/{data.RearId}";
            Check(Rows(view).Single(row => row.Key == frontKey).Summary.Contains("待首次登录") &&
                Rows(view).Single(row => row.Key == rearKey).Summary.Contains("待配置网页账号"),
                "配置账号、登录、配对和上报未被当成同一件事");
            Select(view, rearKey);
            Click(Find<Button>(view, "OpeningShowDetail"));
            Check(Text(view, "OpeningEvidence").Contains(data.RearId) && !Text(view, "OpeningEvidence").Contains(data.FrontId),
                "同班两屏用不同绑定 ID");
            Click(Find<Button>(view, "OpeningHandleRow"));
            var screen = await WindowOf<ScreenDutyWindow>();
            await Until(() => screen.Detail?.Id == data.RearId && Ready(screen, "DutyNative"), "值守窗口按后屏绑定打开");
            Click(Find<Button>(screen, "DutyNative"));
            await Until(() => Ready(main, "ReturnToSchoolOverviewButton") &&
                Find<Button>(main, "ReturnToSchoolOverviewButton").Content is "返回开学配置" &&
                Text(main, "AdmissionScope").Contains("八班后屏"), "原生接入落在原后屏并保留返回开学配置入口");
            await CaptureWithFrame(main, output, "04a-narrow-rear-native-return");
            Click(Find<Button>(main, "ReturnToSchoolOverviewButton"));
            view = await AcademicPageAsync<SchoolOpeningView>(main);
            await WaitRow(view, rearKey);
            Check(Ready(view, "OpeningHandleRow") && Text(view, "OpeningEvidence").Contains(data.RearId),
                "接入办理返回后直接恢复原后屏详情");
            Click(Find<Button>(view, "OpeningHandleRow"));
            screen = await WindowOf<ScreenDutyWindow>();
            await Until(() => screen.Detail?.Id == data.RearId, "回到清单后可再次打开同一后屏");
            await Signal("connected");
            Click(Find<Button>(screen, "DutyBack"));
            await WaitRow(view, rearKey);
            await Until(() => Rows(view).Single(row => row.Key == frontKey).Summary.Contains("已收到有效上报"), "前屏实测上报显示");
            Check(Rows(view).Single(row => row.Key == rearKey).Summary.Contains("待现场接入") &&
                !Rows(view).Single(row => row.Key == rearKey).Evidence.Contains("已收到有效上报"),
                "前屏的网页登录和 Host 上报不算后屏已接入");
            await CaptureWithFrame(main, output, "05-narrow-rear-still-pending");
            Click(Find<Button>(view, "OpeningBackToList"));
            Select(view, frontKey);
            main.Width = 1160;
            main.Height = 780;
            await Until(() => Ready(view, "OpeningHandleRow") && !Ready(view, "OpeningBackToList"), "宽窗并列证据");
            await CaptureWithFrame(main, output, "06-wide-front-report-and-rear-pending");
            Click(Find<Button>(view, "OpeningCheckAddresses"));
            Check(Text(view, "OpeningAddress").Contains("KV 地址（教室端配对使用）") &&
                Text(view, "OpeningAddress").Contains("KV 提供的师生网页站点") &&
                Find<TextBox>(view, "OpeningWebsite").Text is { } website && !website.StartsWith(data.Origin, StringComparison.Ordinal),
                "分域明确区分教室端 API 与师生网页");
            await CaptureWithFrame(main, output, "06a-wide-website-and-api");

            await Signal("hold");
            Click(Find<Button>(view, "OpeningRefresh"));
            await Signal("held");
            SelectSchool(data.OtherId);
            await Until(() => !view.IsEffectivelyVisible, "切校关闭旧清单");
            await Signal("release");
            var other = await OpenChecklist("请先在学期管理");
            Check(Text(other, "OpeningScope").Contains("另一所学校") && Rows(other).All(row =>
                row.Key != frontKey && row.Key != rearKey), "旧学校迟到响应未串入新校");
            Click(Find<Button>(other, "OpeningBack"));
            await Until(() => Ready(main, "OpenSchoolOverviewButton"), "回学校总览后可再次进入");
            Click(main.GetVisualDescendants().OfType<Button>().Single(button =>
                button.IsEffectivelyVisible && button.Content is "退出登录"));
            await Until(() => Ready(main, "LoginButton"), "前一位管理员已退出");
            await Login("admin2");
            SelectSchool(data.SchoolId);
            var reopened = await OpenChecklist("2 个启用行政班");
            Click(Find<Button>(reopened, "OpeningClassrooms"));
            await Until(() => Rows(reopened).Any(row => row.Key == frontKey && row.Summary.Contains("已收到有效上报")),
                "另一位管理员从普通总览恢复真实开学进度");
            Check(Rows(reopened).Single(row => row.Key == rearKey).Summary.Contains("待现场接入"), "重开后后屏仍待接入");
            await CaptureWithFrame(main, output, "07-reopened-by-other-admin");
            Console.WriteLine($"PASS: Windows school opening 1160×780 / 840×480, {main.RenderScaling:P0}; exact teaching, duty and binding targets.");
            desktop.Shutdown(0);

            async Task CheckDuty(string key, string account, string target, string capture)
            {
                Select(view, key);
                Click(Find<Button>(view, "OpeningShowDetail"));
                await Until(() => Ready(view, "OpeningHandleRow"), "窄窗展开岗位详情");
                await CaptureWithFrame(main, output, capture);
                Click(Find<Button>(view, "OpeningHandleRow"));
                var staff = await WindowOf<StaffResponsibilitiesWindow>();
                await Until(() => Find<ListBox>(staff, "StaffCurrentRows").SelectedItem is StaffEditorRow row && row.Teacher.Id == account,
                    "岗位入口恢复已选择的确切教师 ID");
                Check(Text(staff, "StaffScope").Contains("秋季学期") && Text(staff, "StaffHeading").Contains(target),
                    "岗位范围落到原学期和原班或年级");
                Click(Find<Button>(staff, "StaffClose"));
                await WaitRow(view, key);
                Click(Find<Button>(view, "OpeningBackToList"));
                await Until(() => SelectedRowHasFocus(view, key), "返回原岗位行和焦点");
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            await CaptureWithFrame(main, output, "failure");
            desktop.Shutdown(1);
        }

        async Task Login(string name)
        {
            Find<TextBox>(main, "ServerAddress").Text = data.Origin;
            Find<TextBox>(main, "SchoolCode").Text = data.SchoolCode;
            Find<TextBox>(main, "Username").Text = name;
            Find<TextBox>(main, "Password").Text = data.Pin;
            Click(Find<Button>(main, "LoginButton"));
            await Until(() => schools.IsEffectivelyVisible && schools.Items.OfType<School>().Any(row => row.Id == data.SchoolId), "管理员登录完成");
        }
        void SelectSchool(string id) => schools.SelectedItem = schools.Items.OfType<School>().Single(row => row.Id == id);

        async Task<SchoolOpeningView> OpenChecklist(string summary)
        {
            var overview = Find<SchoolOverviewView>(main, "SchoolOverviewWorkspace");
            await Until(() => Ready(overview, "OpenSchoolOpeningButton"), "学校总览开学配置常驻入口");
            Click(Find<Button>(overview, "OpenSchoolOpeningButton"));
            var view = await AcademicPageAsync<SchoolOpeningView>(main);
            await Until(() => Text(view, "OpeningSummary").Contains(summary) && Ready(view, "OpeningRefresh"), summary);
            return view;
        }

        async Task Signal(string name)
        {
            using var response = await fixture.PostAsync("/__school-opening/" + name, null);
            Check(response.IsSuccessStatusCode, $"隔离验收步骤 {name} 失败");
        }
        async Task<TeachingView> OpenTeaching(SchoolOpeningView view, string subject, string account)
        {
            Click(Find<Button>(view, "OpeningHandleRow"));
            var teaching = await AcademicPageAsync<TeachingView>(main);
            await Until(() => Text(teaching, "TeachingPinnedSubject") == subject &&
                Find<ComboBox>(teaching, "TeachingExistingPicker").SelectedItem is TeachingAssignment assignment && assignment.AccountId == account,
                "任课入口按原班科和教师账号定位");
            return teaching;
        }
    }

    private static IEnumerable<SchoolOpeningRow> Rows(SchoolOpeningView view) =>
        Find<ListBox>(view, "OpeningRows").Items.OfType<SchoolOpeningRow>();
    private static void Select(SchoolOpeningView view, string key) =>
        Find<ListBox>(view, "OpeningRows").SelectedItem = Rows(view).Single(row => row.Key == key);
    private static bool Ready(Control root, string name) =>
        Find<Button>(root, name) is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true };
    private static string Text(Control root, string name) => Find<TextBlock>(root, name).Text ?? "";
    private static Task WaitRow(SchoolOpeningView view, string key) => Until(() => view.IsEffectivelyVisible &&
        Ready(view, "OpeningRefresh") && Find<ListBox>(view, "OpeningRows").SelectedItem is SchoolOpeningRow row && row.Key == key,
        "返回并稳定恢复原事项：" + key);
    private static bool SelectedRowHasFocus(SchoolOpeningView view, string key)
    {
        var list = Find<ListBox>(view, "OpeningRows");
        return list.SelectedItem is SchoolOpeningRow row && row.Key == key &&
            list.ContainerFromItem(row) is ListBoxItem { IsFocused: true };
    }
}
