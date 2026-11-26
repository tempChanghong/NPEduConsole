using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--live-website-policy", var websiteFixture, var websiteOutput])
            return StartWebsitePolicy(websiteFixture, websiteOutput);
        if (args is ["--live-classroom-action-policy", var classroomActionFixture, var classroomActionOutput])
            return StartClassroomActionPolicy(classroomActionFixture, classroomActionOutput);
        if (args is ["--live-school-overview", var overviewFixture, var overviewOutput])
            return StartSchoolOverview(overviewFixture, overviewOutput);
        if (args is ["--live-school-opening", var openingFixture, var openingOutput])
            return StartSchoolOpening(openingFixture, openingOutput);
        if (args is ["--live-staff-configuration" or "--live-staff-configuration-reopen", var staffPlanFixture, var staffPlanOutput])
            return StartStaffConfiguration(staffPlanFixture, staffPlanOutput, args[0].EndsWith("-reopen", StringComparison.Ordinal));
        if (args is ["--live-teaching", var teachingFixture, var teachingOutput])
            return StartTeaching(teachingFixture, teachingOutput);
        if (args is ["--live-subject-setup", var subjectFixture, var subjectOutput])
            return StartSubjectSetup(subjectFixture, subjectOutput);
        if (args is ["--live-school-subjects" or "--live-school-subjects-reopen", var catalogFixture, var catalogOutput])
            return StartSchoolSubjects(catalogFixture, catalogOutput, args[0].EndsWith("-reopen"));
        if (args is ["--live-term-screen-placement" or "--live-term-screen-placement-reopen", var placementFixture, var placementOutput])
            return StartTermScreenPlacement(placementFixture, placementOutput, args[0].EndsWith("-reopen", StringComparison.Ordinal));
        if (args is ["--live-term-activation", var activationFixture, var activationOutput])
            return StartTermActivation(activationFixture, activationOutput);
        if (args is ["--live-term-status", var statusFixture, var statusOutput])
            return StartTermStatus(statusFixture, statusOutput);
        if (args is ["--live-scoped-term-classes", var scopedFixture, var scopedOutput])
            return StartScopedTermClasses(scopedFixture, scopedOutput);
        if (args is ["--live-scoped-term-teaching", var termTeachingFixture, var termTeachingOutput])
            return StartScopedTermTeaching(termTeachingFixture, termTeachingOutput);
        if (args is ["--live-class-rules", var classRulesFixture, var classRulesOutput])
            return StartClassRules(classRulesFixture, classRulesOutput);
        if (args is ["--live-staff-review", var staffReviewFixture, var staffReviewOutput])
            return StartStaffReview(staffReviewFixture, staffReviewOutput);
        if (args is ["--live-term-preparation", var termFixture, var termOutput])
            return StartTermPreparation(termFixture, termOutput);
        if (args is ["--live-school-study", var schoolStudyFixture, var schoolStudyOutput])
            return StartSchoolStudy(schoolStudyFixture, schoolStudyOutput);
        if (args is ["--live-study-display", var studyDisplayFixture, var studyDisplayOutput])
            return StartStudyDisplay(studyDisplayFixture, studyDisplayOutput);
        if (args is ["--live-device-field-actions", var fieldFixture, var fieldOutput])
            return StartDeviceFieldActions(fieldFixture, fieldOutput);
        if (args is ["--live-study-monitoring", var monitoringFixture, var monitoringOutput])
            return StartStudyMonitoring(monitoringFixture, monitoringOutput);
        if (args is ["--live-management-accounts", var managementFixture, var managementOutput])
            return StartManagementAccounts(managementFixture, managementOutput);
        if (args is ["--owner-recovery-ui", var ownerRecoveryOutput])
            return StartOwnerRecovery(ownerRecoveryOutput);
        if (args is ["--live-school-profile", var profileFixture, var profileOutput])
            return StartSchoolProfile(profileFixture, profileOutput);
        if (args is ["--live-first-school-console", var setupFixture, var setupOutput])
            return StartSchoolTransfer(setupFixture, setupOutput, firstSchool: true);
        if (args is ["--live-school-transfer", var transferFixture, var transferOutput])
            return StartSchoolTransfer(transferFixture, transferOutput, firstSchool: false);
        if (args is ["--live-desktop-oauth", var oauthFixture, var oauthOutput])
            return StartDesktopOAuth(oauthFixture, oauthOutput);
        if (args is ["--live-organization-import", var organizationFixture, var organizationOutput])
            return StartOrganizationImport(organizationFixture, organizationOutput);
        if (args is ["--live-archived-batch-correction", var archiveFixture, var archiveOutput])
            return StartArchivedBatchCorrection(archiveFixture, archiveOutput);
        if (args is ["--live-entry-retirement-pilot", var entryFixture, var entryOutput])
            return StartEntryRetirementPilot(entryFixture, entryOutput);
        if (args is ["--live-teacher-allocation", var allocationFixture, var allocationOutput])
            return StartTeacherAllocation(allocationFixture, allocationOutput);
        if (args is ["--live-screen-duty", var dutyFixture, var dutyOutput])
            return StartScreenDuty(dutyFixture, dutyOutput);
        if (args is ["--live-exam-workbench", var examFixture, var examOutput])
            return StartExamWorkbench(examFixture, examOutput);
        if (args is ["--live-homework-settings", var homeworkFixture, var homeworkOutput])
            return StartHomeworkSettings(homeworkFixture, homeworkOutput);
        if (args is ["--live-teacher-retirement", var retirementFixture, var retirementOutput])
            return StartTeacherRetirement(retirementFixture, retirementOutput);
        if (args is ["--live-structure-retirement", var structureFixture, var structureOutput])
            return StartStructureRetirement(structureFixture, structureOutput);
        if (args is ["--live-grade-teaching-plan", var gradePlanFixture, var gradePlanOutput])
            return StartGradeTeachingPlan(gradePlanFixture, gradePlanOutput);
        if (args is ["--live-course-groups", var walkingFixture, var walkingOutput])
            return StartCourseGroups(walkingFixture, walkingOutput);
        if (args is ["--live-teacher-accounts", var teacherFixture, var teacherOutput])
            return StartTeacherAccounts(teacherFixture, teacherOutput);
        if (args is ["--live-staff-responsibilities", var staffFixture, var staffOutput])
            return StartStaffResponsibilities(staffFixture, staffOutput);
        if (args is ["--live-class-maintenance" or "--live-class-maintenance-reopen", var maintenanceFixture, var maintenanceOutput])
            return StartClassMaintenance(maintenanceFixture, maintenanceOutput, reopen: args[0].EndsWith("-reopen"));
        if (args is ["--live-class-batch" or "--live-class-batch-reopen", var batchFixture, var batchOutput])
            return StartClassBatch(batchFixture, batchOutput, reopen: args[0] == "--live-class-batch-reopen");
        if (args is not ([_] or ["--live" or "--live-grade", _, _] or ["--live-grade-rename", _, _]))
            throw new ArgumentException("Specify an output directory, --live/--live-grade <fixture.json> <directory>, or --live-grade-rename <fixture.json> <directory>.");
        if (args[0] == "--live-grade-rename") return StartGradeRename(args[1], args[^1]);
        bool gradeCreation = args[0] == "--live-grade";
        using var fixture = args.Length == 1 ? new Fixture() : null;
        var access = fixture?.Access ?? JsonSerializer.Deserialize<Access>(File.ReadAllText(args[1]), JsonSerializerOptions.Web)!;
        Check((gradeCreation ? access.AllowGradeCreationTestWrites : access.AllowClassCreationTestWrites) &&
            new Uri(access.Origin).IsLoopback && new Uri(access.LostReplyOrigin).IsLoopback,
            "Use an isolated loopback fixture that explicitly allows desktop writes.");
        string output = args[^1];
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() =>
            { _ = gradeCreation ? VerifyGradeAsync(access, output) : VerifyAsync(access, fixture, output); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyAsync(Access access, Fixture? fixture, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        try
        {
            var main = (MainWindow)desktop.MainWindow!;
            var schools = Find<ComboBox>(main, "SchoolPicker");
            var classes = Find<ListBox>(main, "ClassPicker");
            var entry = Find<Button>(main, "OpenClassCreationButton");
            var login = Find<Button>(main, "LoginButton");
            string Notice() => Find<TextBlock>(main, "ClassCreationNoticeText").Text ?? "";
            Find<TextBox>(main, "ServerAddress").Text = access.LostReplyOrigin;
            Find<TextBox>(main, "SchoolCode").Text = access.SchoolCode;
            Find<TextBox>(main, "Username").Text = access.Username;
            Find<TextBox>(main, "Password").Text = access.Password;
            Click(login);
            await Until(() => schools.ItemCount == 2, "both authorized schools are loaded");
            Check(!entry.IsEnabled, "The entry requires a selected school.");
            SelectSchool(access.EmptySchoolId);
            await Until(() => entry.IsEnabled && Find<TextBlock>(main, "ClassListMessage").Text == "该学校暂无可查看班级。", "no-class school can still create");
            Check(classes.ItemCount == 0, "The empty school starts with no classes.");
            var dialog = await OpenCreation();
            Check(Grade().ItemCount == 1 && Grade().SelectedIndex == 0 && Find<CheckBox>(dialog, "CreationStudentSelectable").IsChecked == true,
                "Empty school preselects its only grade and defaults to selectable.");
            string createdName = "桌面新建班";
            Fill(0, "-bad", createdName);
            Submit();
            Check(Feedback().Contains("2–64"), "Invalid workspace codes are caught before a request.");
            Fill(0, "name.check", new string('班', 192));
            Submit();
            Check(Feedback().Contains("191"), "An overlong class name gets a specific validation message.");
            Fill(0, " desk.first ", createdName);
            await Capture(dialog, output, "01-form");
            Submit();

            if (fixture is null)
            {
                await WaitForUnconfirmed();
                await Capture(dialog, output, "02-uncertain");
                dialog.Close();
                await Until(() => Notice().Contains("未确认"), "main window preserves the unresolved result");
                dialog = await OpenCreation();
                Check(Fields().IsEnabled == false && Code().Text == "DESK.FIRST" && Name().Text == createdName, "Reopening keeps the exact normalized submission locked.");
                Submit();
                var roster = await WaitForRoster();
                var opened = (Classroom)classes.SelectedItem!;
                Check(opened.Name == createdName && opened.Screens.Count == 0, "New class is selected after the idempotent retry.");
                await PressScoped(roster, "OpenRosterImport");
                Find<TextBox>(roster, "RosterPasteText").Text = "001\t录入示例甲\n002\t录入示例乙";
                Click(Find<Button>(roster, "ApplyRosterImport"));
                Click(Find<Button>(roster, "PreviewRosterButton"));
                await Until(() => Find<ListBox>(roster, "RosterChangesList").ItemCount == 2, "first roster preview");
                await Capture(roster, output, "03-first-roster");
                Click(Find<Button>(roster, "ConfirmRosterSave"));
                await Until(() => Find<TextBlock>(roster, "RosterFeedback").Text?.Contains("已保存") == true, "first roster saved to real KV");
                Click(Find<Button>(roster, "CloseRosterButton"));
                await Until(() => Notice().Contains("本次名单录入结果已确认"), "main window confirms both steps");
                await Capture(main, output, "04-complete");
                SelectSchool(access.SchoolId);
                dialog = await OpenCreation();
                Check(Grade().Items.Cast<ClassCreationGrade>().Any(grade => grade.Id == access.EmptyGradeId), "An existing school lists a grade with no classes.");
                dialog.Close();
                await SignOut();
                End("PASS: Windows + real KV/PostgreSQL, zero-class school, zero-class grade, aborted write receipt/retry, exact new class, and first roster save.", 0);
                return;
            }

            var first = await fixture.NextPost();
            Check(first.Body is { Code: "DESK.FIRST", IsStudentSelectable: true } && first.Body.RequestId != Guid.Empty,
                "Normalized payload and request ID are sent to the current-term route.");
            first.Fail(408);
            await WaitForUnconfirmed();
            await Capture(dialog, output, "02-uncertain");
            dialog.Close();
            await Until(() => Notice().Contains("未确认"), "uncertain request is remembered after closing");
            SelectSchool(access.SchoolId);
            dialog = await OpenCreation();
            Check(Grade().Items.Cast<ClassCreationGrade>().Any(grade => grade.Id == access.EmptyGradeId) && string.IsNullOrEmpty(Code().Text),
                "A different school offers its own grades and no pending payload.");
            dialog.Close();
            SelectSchool(access.EmptySchoolId);
            dialog = await OpenCreation();
            Check(!Fields().IsEnabled && Name().Text == createdName, "Returning to the same school resumes the locked payload.");
            Submit();
            var retried = await fixture.NextPost();
            Check(retried.SchoolId == first.SchoolId && retried.Body == first.Body, "Uncertain retries reuse the exact request ID and payload.");
            fixture.ClassMade = true;
            fixture.FailNextSchoolList = true;
            retried.Created("new-class");
            await Until(() => Notice().Contains("列表未能刷新"), "creation is acknowledged even if refresh fails");
            Check(fixture.RosterGetCount == 0 && !main.GetVisualDescendants().OfType<ClassRosterView>().Any(view => view.IsEffectivelyVisible), "A bad refresh cannot open a same-name class.");
            await Capture(main, output, "03-created-refresh-failed");
            Click(Find<Button>(main, "RefreshButton"));
            var createdRoster = await WaitForRoster();
            Check(((Classroom)classes.SelectedItem!).Id == "new-class" && fixture.LastRosterClass == "new-class", "Selecting a similar name cannot redirect the roster handoff.");
            Click(Find<Button>(createdRoster, "CloseRosterButton"));
            await Until(() => Notice().Contains("本次未确认名单保存"), "cancel leaves class created without making claims about the current roster");

            dialog = await OpenCreation();
            Fill(0, "duplicate", "其他新班");
            Submit();
            var duplicate = await fixture.NextPost();
            duplicate.Fail(409, "WORKSPACE_CODE_EXISTS");
            await Until(() => Feedback().Contains("更换代码") && Fields().IsEnabled, "code conflict can be corrected");
            Check(fixture.RosterGetCount == 1, "A duplicate code never opens another class.");
            Fill(0, "other.code", "其他新班");
            Submit();
            var staleTerm = await fixture.NextPost();
            Check(staleTerm.Body.RequestId != duplicate.Body.RequestId, "Changed input uses a new ID after a definitive rejection.");
            fixture.NewTerm = true;
            staleTerm.Fail(409, "CLASS_CREATION_TERM_CHANGED");
            await Until(() => Feedback().Contains("年级已变化") && !SubmitButton().IsEnabled, "a stale term cannot submit again without refreshing");
            Click(Find<Button>(dialog, "ReloadCreationContext"));
            await Until(() => Grade().Items.Cast<ClassCreationGrade>().Any(grade => grade.Id == "grade-next"), "new term grades read");
            Check(Grade().SelectedItem is null && Name().Text == "其他新班", "Refreshing context preserves input but clears the old grade.");
            Fill(0, "other.code", "其他新班");
            Submit();
            var oldAccount = await fixture.NextPost();
            await SignOut();
            oldAccount.Created("late-old-account");
            await Task.Delay(120);
            Check(!dialog.IsVisible && string.IsNullOrEmpty(Code().Text) && string.IsNullOrEmpty(Notice()) && fixture.RosterGetCount == 1,
                "A response after logout cannot update the next workspace or open a roster.");
            End("PASS: Windows mode, scoped pending receipt, fixed payload, duplicate code, new term, failed refresh recovery by ID, canceled roster, and late logout isolation.", 0);

            void SelectSchool(string schoolId) => schools.SelectedItem = schools.Items.Cast<School>().Single(s => s.Id == schoolId);

            async Task<ClassCreationWindow> OpenCreation()
            {
                Click(entry);
                await Until(() => desktop.Windows.OfType<ClassCreationWindow>().Any(window => window.IsVisible), "open the creation modal from its button");
                var open = desktop.Windows.OfType<ClassCreationWindow>().Single();
                await Until(() => Find<ComboBox>(open, "CreationGrade").ItemCount > 0, "available grades");
                return open;
            }

            TextBox Code() => Find<TextBox>(dialog, "CreationCode");
            TextBox Name() => Find<TextBox>(dialog, "CreationName");
            ComboBox Grade() => Find<ComboBox>(dialog, "CreationGrade");
            StackPanel Fields() => Find<StackPanel>(dialog, "CreationFields");
            Button SubmitButton() => Find<Button>(dialog, "SubmitClassCreationButton");
            string Feedback() => Find<TextBlock>(dialog, "CreationFeedback").Text ?? "";
            void Submit() => Click(SubmitButton());
            void Fill(int grade, string code, string name) { Grade().SelectedIndex = grade; Code().Text = code; Name().Text = name; }
            async Task WaitForUnconfirmed() => await Until(() => SubmitButton().Content is "重试确认" && SubmitButton().IsEnabled && !Fields().IsEnabled,
                "unconfirmed request offers an exact retry");
            async Task<ClassRosterView> WaitForRoster()
            {
                var roster = await AcademicPageAsync<ClassRosterView>(main);
                await Until(() => Find<Grid>(roster, "RosterEditorPane").IsEffectivelyVisible, "automatic handoff to the roster page");
                return roster;
            }
            async Task SignOut()
            {
                Click(main.GetVisualDescendants().OfType<Button>().Single(button => button.Content is "退出登录"));
                await Until(() => login.IsEffectivelyVisible && login.IsEffectivelyEnabled, "sign out");
            }
        }
        catch (Exception error) { End("FAIL: " + error, 1); }

        void End(string message, int code)
        {
            File.WriteAllText(Path.Combine(output, "result.txt"), message);
            Console.WriteLine(message);
            desktop.Shutdown(code);
        }
    }

    private static T Find<T>(Control root, string name) where T : Control =>
        (root switch { TeachingWindow teaching => (Control)teaching.Content!, GradeTeachingPlanWindow plan => plan.View, _ => root })
            .FindControl<T>(name) ?? throw new InvalidDataException(name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task<T> AcademicPageAsync<T>(MainWindow main) where T : UserControl
    {
        await Until(() => main.GetVisualDescendants().OfType<T>().Any(view => view.IsEffectivelyVisible), typeof(T).Name + " visible in the main window");
        return main.GetVisualDescendants().OfType<T>().Single(view => view.IsEffectivelyVisible);
    }

    private static Task Capture(UserControl view, string output, string name) => Capture((Window)TopLevel.GetTopLevel(view)!, output, name);

    private static async Task Capture(Window target, string output, string name, bool includeFrame = false)
    {
        await Task.Delay(120);
        var root = includeFrame ? target.GetVisualAncestors().LastOrDefault() ?? target : (Control)target.Content!;
        using var image = new RenderTargetBitmap(new PixelSize((int)root.Bounds.Width, (int)root.Bounds.Height));
        image.Render(root);
        image.Save(Path.Combine(output, name + ".png"), new PngBitmapEncoderOptions());
    }

    private static async Task Until(Func<bool> condition, string step)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try { while (!condition()) await Task.Delay(25, timeout.Token); }
        catch (OperationCanceledException) { throw new TimeoutException(step); }
    }

    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidDataException(message);
    }

    private sealed record Access(string Origin, string LostReplyOrigin, string SchoolCode, string Username, string Password,
        string SchoolId, string EmptySchoolId, string EmptyGradeId, bool AllowClassCreationTestWrites,
        string? ZeroGradeSchoolId = null, string? NextTermId = null, bool AllowGradeCreationTestWrites = false);
    private sealed record Body(string TermId, string GradeId, string Code, string Name, bool IsStudentSelectable, Guid RequestId);

    private sealed record CreationCall(string SchoolId, Body Body)
    {
        public TaskCompletionSource<IResult> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Fail(int status, string? code = null) => Response.SetResult(Results.Json(new { success = false, code }, statusCode: status));
        public void Created(string id) => Response.SetResult(Envelope(new { id, SchoolId, Body.TermId, Body.GradeId, Body.RequestId }));
    }

    private static IResult Envelope(object data) => Results.Json(new { success = true, data });

    private sealed class Fixture : IDisposable
    {
        private readonly WebApplication _app;
        private readonly Channel<CreationCall> _writes = Channel.CreateUnbounded<CreationCall>();
        public Access Access { get; }
        public bool ClassMade { get; set; }
        public bool FailNextSchoolList { get; set; }
        public bool NewTerm { get; set; }
        public int RosterGetCount { get; private set; }
        public string? LastRosterClass { get; private set; }

        public Fixture()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            _app = builder.Build();
            string TermId(string schoolId) => schoolId == "main" ? "term-main" : NewTerm ? "term-next" : "term-empty";
            _app.MapPost("/accounts/local/login", () => Envelope(new { access_token = "fixture-access" }));
            _app.MapPost("/accounts/logout", () => Envelope(new { }));
            _app.MapGet("/api/v2/me/schools", () => Envelope(new[] { "main", "empty" }.Select(id => new
            {
                role = "ADMIN", school = new { id, name = id == "main" ? "演练学校" : "零班学校", terms = new[] { new { id = TermId(id), status = "ACTIVE" } } }
            })));
            const string schoolPath = "/api/v2/admin/schools/{schoolId}";
            _app.MapGet(schoolPath + "/classroom-screens", () =>
            {
                if (!FailNextSchoolList) return Envelope(Array.Empty<object>());
                FailNextSchoolList = false;
                return Results.StatusCode(503);
            });
            _app.MapGet("/api/v2/npep/schools/{schoolId}/device-overview", () => Results.StatusCode(503));
            _app.MapGet(schoolPath + "/academic-structure", (string schoolId) => Envelope(new
            {
                term = new { id = TermId(schoolId), status = "ACTIVE" },
                administrativeClasses = schoolId == "main" || ClassMade ?
                    new[] { "same-name-class", schoolId == "empty" ? "new-class" : "older-class" }.Select(id => new
                    { id, name = "桌面新建班", isActive = true, grade = new { name = "高一" } }).Cast<object>().ToArray() : []
            }));
            const string creation = schoolPath + "/current-term/administrative-classes";
            _app.MapGet(creation, (string schoolId) => Envelope(new
            {
                term = new { id = TermId(schoolId), schoolId, status = "ACTIVE", name = NewTerm ? "新学期" : "秋季学期" },
                grades = new[] { new { id = schoolId == "main" ? "grade-empty" : NewTerm ? "grade-next" : "grade-in-empty-school",
                    termId = TermId(schoolId), code = "G1", name = "高一" } }
            }));
            _app.MapPost(creation, async (string schoolId, Body body, HttpContext context) =>
            {
                var pending = new CreationCall(schoolId, body);
                await _writes.Writer.WriteAsync(pending, context.RequestAborted);
                return await pending.Response.Task.WaitAsync(context.RequestAborted);
            });
            _app.MapGet(schoolPath + "/administrative-classes/{classId}/students", (string classId) =>
            {
                RosterGetCount++;
                LastRosterClass = classId;
                return Envelope(new { revision = "first", students = Array.Empty<object>() });
            });
            _app.StartAsync().GetAwaiter().GetResult();
            string origin = _app.Urls.Single();
            Access = new(origin, origin, "LOCAL", "admin", "synthetic", "main", "empty", "grade-empty", true);
        }

        public async Task<CreationCall> NextPost()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            return await _writes.Reader.ReadAsync(deadline.Token);
        }

        public void Dispose() => Task.Run(async () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _app.StopAsync(deadline.Token);
            await _app.DisposeAsync();
        }).GetAwaiter().GetResult();
    }
}
