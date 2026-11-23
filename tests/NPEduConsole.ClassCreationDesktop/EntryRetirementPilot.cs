using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private sealed record EntryRetirementAccess(string Origin, string SchoolId, string SchoolCode, string Password,
        string ArchivedTermId, string ActiveTermId, string ArchivedClassId, string ActiveClassId, string SubjectId,
        string PeerId, string TeacherId, string InviteId, string RaceInviteId, string EmptySchoolId, string AuditId, string AuditDate);

    private static int StartEntryRetirementPilot(string fixture, string output)
    {
        var access = JsonSerializer.Deserialize<EntryRetirementAccess>(File.ReadAllText(fixture), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Expected the isolated entry retirement fixture.");
        Check(IsLoopback(access.Origin), "Retirement pilot uses a loopback fixture.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyEntryRetirementPilotAsync(access, output); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyEntryRetirementPilotAsync(EntryRetirementAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        using var control = new HttpClient {BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(50)};
        try
        {
            main.Width = 1160;
            main.Height = 780;
            Find<TextBox>(main, "ServerAddress").Text = access.Origin;
            Find<TextBox>(main, "SchoolCode").Text = access.SchoolCode;
            Find<TextBox>(main, "Username").Text = "admin";
            Find<TextBox>(main, "Password").Text = access.Password;
            Click(Find<Button>(main, "LoginButton"));
            await PickSchool(access.SchoolId);
            var hwnd = main.TryGetPlatformHandle();
            Check(hwnd is { HandleDescriptor: "HWND" } && hwnd.Handle != IntPtr.Zero, "main window has a real Windows handle");
            Click(Find<Button>(main, "OpenClassWorkspaceButton"));
            Click(Find<Button>(main, "OpenTermPreparationButton"));
            var terms = await AcademicPageAsync<TermPreparationView>(main);
            SameMainWindow(terms);
            await SelectTerm(terms, access.ArchivedTermId);
            Click(Find<Button>(terms, "TermViewDiagnostics"));
            var diagnostics = await AcademicPageAsync<TermDiagnosticsView>(main);
            SameMainWindow(diagnostics);
            var diagView = Find<SchoolOverviewView>(diagnostics, "DiagnosticsOverview");
            var rows = Find<ItemsControl>(diagView, "OverviewTasksList");
            var history = await TaskButton("ADMIN_CLASS_SUBJECT_WITHOUT_TEACHER", access.ArchivedClassId);
            Check(rows.Items.OfType<OverviewTaskItem>().All(row => row.Task.ClassId != access.ActiveClassId), "same-code active class excluded");
            Check(Find<TextBlock>(diagnostics, "DiagnosticsHeading").Text!.Contains("已归档") &&
                Find<TextBlock>(diagnostics, "DiagnosticsActiveTerm").Text!.Contains("现用春季"), "archive and school active term visible together");
            await Capture(diagnostics, output, "01-wide-archive-diagnostics");
            main.Width = 840;
            main.Height = 480;
            await Capture(diagnostics, output, "01b-narrow-archive-diagnostics");
            Find<ComboBox>(diagView, "OverviewTaskFilter").SelectedIndex = 1;
            history = await TaskButton("ADMIN_CLASS_SUBJECT_WITHOUT_TEACHER", access.ArchivedClassId);
            history.Focus();
            Click(history);
            var plan = await WindowOf<GradeTeachingPlanWindow>();
            await Until(() => (Find<ComboBox>(plan, "PlanSubject").SelectedItem as GradePlanLabel)?.Id == access.SubjectId &&
                Find<TextBlock>(plan, "PlanScope").Text!.Contains("历史秋季"), "exact archived term and physics selected");
            Check(!Find<Button>(plan, "PlanPreview").IsEnabled, "archive open remains read-only");
            await Capture(plan, output, "02-archive-teaching-target");
            plan.Close();
            await Until(() => Find<ComboBox>(diagView, "OverviewTaskFilter").SelectedIndex == 1 &&
                diagView.GetVisualDescendants().OfType<Button>().Any(btn => btn.IsFocused && AutomationProperties.GetAutomationId(btn)?.Contains(access.ArchivedClassId) == true),
                "return restores filtered task focus after reread");
            await Signal("legacy-overview-on");
            Click(Find<Button>(diagView, "RefreshSchoolOverviewButton"));
            await Until(() => Find<TextBlock>(diagView, "OverviewFeedback").Text!.Contains("旧 KV"), "legacy specified payload is readable without guessing targets");
            await Signal("legacy-overview-off");
            Click(Find<Button>(diagnostics, "ReturnTermDiagnostics"));
            await Until(() => terms.IsEffectivelyEnabled && Find<Button>(terms, "TermOrganizationImport").IsEffectivelyEnabled,
                "return to same selected term and finish refreshing actions");

            Click(Find<Button>(terms, "TermOrganizationImport"));
            var org = await AcademicPageAsync<OrganizationImportView>(main);
            var grade = Find<ComboBox>(org, "OrgGrade");
            await Until(() => grade.Items.Count > 1, "archive correction catalog loaded");
            grade.SelectedItem = grade.Items.Cast<object>().Single(item => item.ToString()!.Contains("高二（G2）"));
            await Until(() => Find<Button>(org, "OrgTeacherAllocation").IsEffectivelyEnabled, "selected grade access action ready");
            Click(Find<Button>(org, "OrgTeacherAllocation"));
            var allocation = await WindowOf<TeacherAllocationWindow>();
            allocation.Width = 840;
            allocation.Height = 480;
            await Until(() => Find<TextBlock>(allocation, "AllocationScope").Text!.Contains("历史秋季"), "history teacher access opened");
            Find<TabControl>(allocation, "AllocationTabs").SelectedIndex = 3;
            var spaces = Find<ListBox>(allocation, "AllocationDirectorySpaces");
            spaces.SelectedItem = spaces.Items.OfType<AllocationWorkspace>().Single(room => room.Id == access.ArchivedClassId);
            Click(await InvitationButton(access.InviteId));
            var target = await ConfirmWindow();
            await Capture(target, output, "03-narrow-invitation-confirmation");
            Click(target.GetVisualDescendants().OfType<Button>().Single(btn => Equals(btn.Content, "确认撤回这条邀请")));
            await Until(() => Find<TextBlock>(allocation, "AllocationStatus").Text!.Contains("服务器已确认撤回"), "only selected invitation confirmed");
            await Signal("only-target-revoked");
            Click(await InvitationButton(access.RaceInviteId));
            target = await ConfirmWindow();
            await Signal("claim-before-confirm");
            Click(target.GetVisualDescendants().OfType<Button>().Single(btn => Equals(btn.Content, "确认撤回这条邀请")));
            await Until(() => Find<TextBlock>(allocation, "AllocationStatus").Text!.Contains("已被认领"), "claim wins without false revoke");
            await Capture(allocation, output, "04-narrow-invitation-claimed");
            allocation.Close();
            await PressScoped(org, "OrgClose");
            await Until(() => terms.IsEffectivelyEnabled, "invitation workflow returns to term directory");

            await SelectTerm(terms, access.ActiveTermId);
            main.Width = 1160;
            main.Height = 780;
            Click(Find<Button>(terms, "TermViewDiagnostics"));
            diagnostics = await AcademicPageAsync<TermDiagnosticsView>(main);
            diagView = Find<SchoolOverviewView>(diagnostics, "DiagnosticsOverview");
            Click(await TaskButton("ADMIN_CLASS_SUBJECT_WITHOUT_TEACHER", access.ActiveClassId));
            var teaching = await AcademicPageAsync<TeachingView>(main);
            SameMainWindow(teaching);
            await Until(() => (Find<ComboBox>(teaching, "TeachingSubjectPicker").SelectedItem as TeachingSubject)?.Id == access.SubjectId,
                "active physics assignment scoped");
            Click(Find<Button>(teaching, "TeachingNewTeacher"));
            var form = await WindowOf<TeacherAccountForm>();
            var emailEntry = Find<Button>(form, "TeacherFormAllocation");
            await Until(() => emailEntry.IsEffectivelyVisible && emailEntry.IsEnabled, "OAuth school form has desktop allocation");
            Click(emailEntry);
            allocation = await WindowOf<TeacherAllocationWindow>();
            await Until(() => Find<TextBlock>(allocation, "AllocationScope").Text!.Contains("现用春季"), "OAuth workbench retains original term");
            var load = typeof(TeacherAllocationWindow).GetMethod("LoadAllocationText", BindingFlags.Instance | BindingFlags.NonPublic)!;
            load.Invoke(allocation, ["""{"assignments":[{"email":"pilot-new@example.test","role":"VIEWER","workspaceCodes":["G2-C08"]}]}""", true]);
            Click(Find<Button>(allocation, "AllocationPreviewButton"));
            var approve = Find<CheckBox>(allocation, "AllocationApprove");
            await Until(() => approve.IsEffectivelyEnabled, "actual email invitation preview");
            approve.IsChecked = true;
            Click(Find<Button>(allocation, "AllocationSave"));
            await Until(() => Find<TextBlock>(allocation, "AllocationStatus").Text!.Contains("服务器已确认"), "ordinary invitation saved separately");
            await Capture(allocation, output, "05-oauth-assignment-workbench");
            allocation.Close();
            await Until(() => !form.IsVisible && Find<Button>(teaching, "TeachingReadButton").IsEffectivelyEnabled, "return to exact assignment form");
            Check((Find<ComboBox>(teaching, "TeachingSubjectPicker").SelectedItem as TeachingSubject)?.Id == access.SubjectId, "original subject and draft retained");
            await Signal("oauth-invitation-only");

            var openTeacher = Find<Button>(teaching, "OpenTeachingTeacherChoice");
            var candidates = Find<ListBox>(teaching, "TeachingTeacherCandidates");
            var choice = Find<Border>(teaching, "TeachingTeacherChoice");
            var search = Find<TextBox>(teaching, "TeachingTeacherSearch");
            Click(openTeacher);
            await Until(() => search.IsFocused && choice.HorizontalAlignment == HorizontalAlignment.Right, "wide teacher sidebar and entry focus");
            search.Text = "林老师";
            await Until(() => candidates.Items.OfType<TeachingTeacherCandidate>().Count() == 2, "same-name teachers use separate identities");
            Check(candidates.Items.OfType<TeachingTeacherCandidate>().Select(item => item.Detail).Distinct().Count() == 2,
                "short accounts distinguish same-name teachers");
            SameMainWindow(choice);
            await CaptureWithFrame(main, output, "07-wide-teacher-sidebar");
            search.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            await Until(() => openTeacher.IsFocused && !choice.IsEffectivelyVisible, "escape restores the scoped teacher field");

            main.Width = 840;
            main.Height = 480;
            await Until(() => main.ClientSize is { Width: <= 845, Height: <= 485 }, "actual narrow window at 840 by 480");
            Click(openTeacher);
            await Until(() => search.IsFocused && choice.HorizontalAlignment == HorizontalAlignment.Stretch, "full-width narrow chooser");
            search.Text = "pilot-teacher-a";
            await Until(() => candidates.ItemCount == 1 && (candidates.Items[0] as TeachingTeacherCandidate)?.Teacher.Id == access.TeacherId,
                "search by real short account");
            candidates.SelectedIndex = 0;
            await CaptureWithFrame(main, output, "08-narrow-teacher-selection");
            candidates.Focus();
            candidates.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await Until(() => openTeacher.IsFocused && ChosenAccount(teaching) == "@pilot-teacher-a",
                "keyboard confirms the exact teacher, then returns focus");
            var peer = Find<StackPanel>(teaching, "TeachingClassChoices").Children.OfType<CheckBox>()
                .Single(check => AutomationProperties.GetAutomationId(check) == "TeachingClass-" + access.PeerId);
            peer.IsChecked = true;
            await PickSchool(access.EmptySchoolId);
            var keep = Find<Button>(teaching, "TeachingKeepDraft");
            Check(keep.IsEffectivelyVisible && teaching.IsEffectivelyVisible &&
                (Find<ComboBox>(main, "SchoolPicker").SelectedItem as School)?.Id == access.SchoolId, "school switch does not discard an edited draft");
            Click(keep);
            await Until(() => diagnostics.IsEffectivelyVisible, "pausing returns to the original diagnostics");
            string activeTaskId = $"SchoolOverviewTask-ADMIN_CLASS_SUBJECT_WITHOUT_TEACHER-{access.ActiveClassId}-{access.SubjectId}";
            await Until(() => diagView.GetVisualDescendants().OfType<Button>().Any(button => button.IsFocused &&
                AutomationProperties.GetAutomationId(button) == activeTaskId), "pause restores focus to the original task after reread");
            Click(await TaskButton("ADMIN_CLASS_SUBJECT_WITHOUT_TEACHER", access.ActiveClassId));
            teaching = await AcademicPageAsync<TeachingView>(main);
            openTeacher = Find<Button>(teaching, "OpenTeachingTeacherChoice");
            await Until(() => ChosenAccount(teaching) == "@pilot-teacher-a" &&
                Find<TextBlock>(teaching, "TeachingFeedback").Text?.Contains("已恢复") == true, "same-scope draft comes back without reusing its preview");
            peer = Find<StackPanel>(teaching, "TeachingClassChoices").Children.OfType<CheckBox>()
                .Single(check => AutomationProperties.GetAutomationId(check) == "TeachingClass-" + access.PeerId);
            Check(peer.IsChecked == true, "same-scope reopened draft keeps the checked peer");
            var preview = Find<Button>(teaching, "TeachingPreviewButton");
            var apply = Find<Button>(teaching, "TeachingApplyButton");
            var feedback = Find<TextBlock>(teaching, "TeachingFeedback");
            Click(preview);
            await Until(() => apply.IsEffectivelyEnabled, "same-window preview is ready");
            await Signal("add-peer-assignment");
            Click(apply);
            await Until(() => feedback.Text?.Contains("本批零写入") == true, "stale preview reports no writes");
            var comparison = Find<ListBox>(teaching, "TeachingPreviewList");
            await Until(() => comparison.GetVisualDescendants().OfType<Border>().Any(row => row.IsFocused &&
                AutomationProperties.GetAutomationId(row) == "TeachingPreviewClass-" + access.PeerId), "stale peer row has focus");
            Check(peer.IsChecked == true && ChosenAccount(teaching) == "@pilot-teacher-a" && !apply.IsEnabled,
                "stale preview preserves teacher and peer, requires a new preview");
            await Signal("stale-no-write");
            await CaptureWithFrame(main, output, "09-narrow-stale-row");
            peer.IsChecked = false;
            Click(preview);
            await Until(() => apply.IsEffectivelyEnabled, "revised exact-target preview is ready");
            Click(apply);
            await Until(() => feedback.Text?.Contains("服务器当前安排与本次提交一致") == true, "lost reply reconciles actual assignment");
            await Signal("assignment-readback-confirmed");
            await CaptureWithFrame(main, output, "10-narrow-assignment-readback");
            Click(Find<Button>(teaching, "CloseTeachingButton"));
            await Until(() => diagnostics.IsEffectivelyVisible && Find<ComboBox>(diagView, "OverviewTaskFilter").IsFocused &&
                !rowsFor(diagView).Any(item => item.Task.ClassId == access.ActiveClassId && item.Task.SubjectId == access.SubjectId),
                "returns to diagnostics, rereads the task, places focus on the list filter once the task is resolved");

            await Signal("hold-overview");
            Click(Find<Button>(diagView, "RefreshSchoolOverviewButton"));
            await Signal("await-held-overview");
            await PickSchool(access.EmptySchoolId);
            await Signal("release-overview");
            await Until(() => !diagnostics.IsVisible && !terms.IsVisible && Find<SchoolOverviewView>(main, "SchoolOverviewWorkspace").IsEffectivelyVisible,
                "late diagnostics from the former school cannot reopen the prior page");

            await PickSchool(access.SchoolId);
            Click(Find<Button>(main, "OpenClassWorkspaceButton"));
            Click(Find<Button>(main, "OpenTeacherAccountsButton"));
            var directory = await WindowOf<TeacherAccountsWindow>();
            var emailDirectory = Find<Button>(directory, "TeacherAccountBulk");
            await Until(() => Equals(emailDirectory.Content, "按邮箱分配") && emailDirectory.IsEnabled, "teacher directory points to same desktop workbench");
            Click(emailDirectory);
            allocation = await WindowOf<TeacherAllocationWindow>();
            allocation.Close();
            await Until(() => directory.IsEnabled, "returns to teacher directory");
            directory.Close();

            Click(Find<Button>(main, "OpenTermPreparationButton"));
            terms = await AcademicPageAsync<TermPreparationView>(main);
            await SelectTerm(terms, access.ActiveTermId);
            Click(Find<Button>(terms, "TermViewDiagnostics"));
            diagnostics = await AcademicPageAsync<TermDiagnosticsView>(main);
            diagView = Find<SchoolOverviewView>(diagnostics, "DiagnosticsOverview");
            await Until(() => Find<TextBlock>(diagView, "OverviewFeedback").Text?.Contains("核对时间") == true, "diagnostics loaded before revocation");

            await Signal("hold-overview");
            Click(Find<Button>(diagView, "RefreshSchoolOverviewButton"));
            await Signal("await-held-overview");
            Click(main.GetVisualDescendants().OfType<Button>().Single(button => button.IsEffectivelyVisible && Equals(button.Content, "退出登录")));
            await Until(() => Find<Grid>(main, "LoginShell").IsEffectivelyVisible && Find<Button>(main, "LoginButton").IsEnabled, "real logout returns to login");
            await Signal("release-overview");
            Check(!diagnostics.IsVisible && !terms.IsVisible, "late response after logout remains out of scope");
            Find<TextBox>(main, "Password").Text = access.Password;
            Click(Find<Button>(main, "LoginButton"));
            await PickSchool(access.SchoolId);
            Click(Find<Button>(main, "OpenClassWorkspaceButton"));
            Click(Find<Button>(main, "OpenTermPreparationButton"));
            terms = await AcademicPageAsync<TermPreparationView>(main);
            await SelectTerm(terms, access.ActiveTermId);
            Click(Find<Button>(terms, "TermViewDiagnostics"));
            diagnostics = await AcademicPageAsync<TermDiagnosticsView>(main);
            diagView = Find<SchoolOverviewView>(diagnostics, "DiagnosticsOverview");
            await Until(() => Find<TextBlock>(diagView, "OverviewFeedback").Text?.Contains("核对时间") == true, "new session loads fresh diagnostics");
            await Signal("revoke-teaching-scope");
            Click(Find<Button>(diagView, "RefreshSchoolOverviewButton"));
            await Until(() => !diagnostics.IsVisible && Find<TextBlock>(main, "SchoolSelectionMessage").Text?.Contains("已无法管理") == true &&
                Find<ComboBox>(main, "SchoolPicker").Items.OfType<School>().All(row => row.Id != access.SchoolId), "revoked school data and pages clear");
            await CaptureWithFrame(main, output, "11-narrow-revoked-school");
            await Signal("restore-teaching-scope");
            await PickSchool(access.EmptySchoolId);
            await Until(() => main.IsEnabled, "main workspace restored");

            var auditEntry = Find<Button>(main, "OpenSchoolAuditButton");
            await Until(() => auditEntry.IsEffectivelyEnabled, "audit available without a term");
            Click(auditEntry);
            var audit = Find<SchoolAuditView>(main, "SchoolAuditWorkspace");
            await Until(() => Find<Button>(audit, "QueryAuditButton").IsEffectivelyEnabled, "initial audit query complete");
            Find<TextBox>(audit, "AuditFromDate").Text = Find<TextBox>(audit, "AuditToDate").Text = access.AuditDate;
            Find<ComboBox>(audit, "AuditResultFilter").SelectedIndex = 2;
            Click(Find<Button>(audit, "QueryAuditButton"));
            var auditRows = Find<ListBox>(audit, "AuditList");
            await Until(() => auditRows.ItemCount == 1 && Find<Button>(audit, "QueryAuditButton").IsEnabled, "date and failed result filter");
            auditRows.SelectedIndex = 0;
            auditRows.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await Until(() => Find<SelectableTextBlock>(audit, "AuditDetailId").Text == access.AuditId, "same audit id in desktop detail");
            await Capture(main, output, "06-console-audit-id-without-term");
            Console.WriteLine($"PASS: main HWND 0x{hwnd!.Handle:X}, wide and narrow teacher workflows, stale and lost replies, school isolation, permissions, historical diagnostics, and no-term audit.");
            desktop.Shutdown(0);

            async Task<Button> TaskButton(string code, string classroom)
            {
                var id = $"SchoolOverviewTask-{code}-{classroom}-{access.SubjectId}";
                await Until(() => diagView.GetVisualDescendants().OfType<Button>().Any(btn => btn.IsEffectivelyVisible &&
                    AutomationProperties.GetAutomationId(btn) == id), "scoped diagnostics task");
                return diagView.GetVisualDescendants().OfType<Button>().First(btn => btn.IsEffectivelyVisible && AutomationProperties.GetAutomationId(btn) == id);
            }
            async Task<Button> InvitationButton(string id)
            {
                await Until(() => allocation.GetVisualDescendants().OfType<Button>().Any(btn =>
                    btn.IsEffectivelyVisible && AutomationProperties.GetAutomationId(btn) == "RevokeArchivedInvitation-" + id), "exact invitation action");
                return allocation.GetVisualDescendants().OfType<Button>().First(btn =>
                    btn.IsEffectivelyVisible && AutomationProperties.GetAutomationId(btn) == "RevokeArchivedInvitation-" + id);
            }

            void SameMainWindow(Control view) => Check(ReferenceEquals(TopLevel.GetTopLevel(view), main) &&
                main.TryGetPlatformHandle()?.Handle == hwnd!.Handle && desktop.Windows.Count(window => window.IsVisible) == 1,
                "work remains on the sole original visible HWND");

            static IEnumerable<OverviewTaskItem> rowsFor(SchoolOverviewView view) =>
                Find<ItemsControl>(view, "OverviewTasksList").Items.OfType<OverviewTaskItem>();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (desktop.Windows.LastOrDefault(row => row.IsVisible) is { } failure)
                try { await Capture(failure, output, "desktop-failure"); } catch { }
            desktop.Shutdown(1);
        }

        async Task PickSchool(string id)
        {
            var picker = Find<ComboBox>(main, "SchoolPicker");
            await Until(() => picker.Items.OfType<School>().Any(row => row.Id == id), "school appears");
            picker.SelectedItem = picker.Items.OfType<School>().Single(row => row.Id == id);
        }
        async Task SelectTerm(TermPreparationView window, string id)
        {
            var list = Find<ListBox>(window, "PreparedTerms");
            await Until(() => list.Items.OfType<ManagedAcademicTerm>().Any(row => row.Id == id), "term appears");
            list.SelectedItem = list.Items.OfType<ManagedAcademicTerm>().Single(row => row.Id == id);
            await Until(() => Find<Button>(window, "TermViewDiagnostics").IsEffectivelyEnabled, "term selection is ready");
        }
        async Task<Window> ConfirmWindow()
        {
            await Until(() => desktop.Windows.Any(row => row.IsVisible && row.Title == "撤回归档邀请"), "scoped invitation confirmation");
            return desktop.Windows.Single(row => row.IsVisible && row.Title == "撤回归档邀请");
        }
        async Task<T> WindowOf<T>() where T : Window
        {
            await Until(() => desktop.Windows.OfType<T>().Any(row => row.IsVisible), typeof(T).Name + " visible");
            return desktop.Windows.OfType<T>().Single(row => row.IsVisible);
        }
        async Task Signal(string mark)
        {
            using var response = await control.PostAsync("/__pilot/" + mark, null);
            Check(response.IsSuccessStatusCode, "database and transport checkpoint: " + mark);
        }
    }

    private static async Task CaptureWithFrame(Window window, string output, string name)
    {
        await Task.Delay(150);
        var frame = window.GetVisualAncestors().LastOrDefault() ?? window;
        using var image = new RenderTargetBitmap(new PixelSize((int)frame.Bounds.Width, (int)frame.Bounds.Height));
        image.Render(frame);
        image.Save(Path.Combine(output, name + ".png"), new PngBitmapEncoderOptions());
    }
}
