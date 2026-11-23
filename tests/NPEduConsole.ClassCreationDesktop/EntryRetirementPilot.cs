using System.Reflection;
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
    private sealed record EntryRetirementAccess(string Origin, string SchoolId, string SchoolCode, string Password,
        string ArchivedTermId, string ActiveTermId, string ArchivedClassId, string ActiveClassId, string SubjectId,
        string InviteId, string RaceInviteId, string EmptySchoolId, string AuditId, string AuditDate);

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
            Click(Find<Button>(main, "OpenClassWorkspaceButton"));
            Click(Find<Button>(main, "OpenTermPreparationButton"));
            var terms = await WindowOf<TermPreparationWindow>();
            await SelectTerm(terms, access.ArchivedTermId);
            Click(Find<Button>(terms, "TermViewDiagnostics"));
            var diagnostics = await WindowOf<TermDiagnosticsWindow>();
            var diagView = Find<SchoolOverviewView>(diagnostics, "DiagnosticsOverview");
            var rows = Find<ItemsControl>(diagView, "OverviewTasksList");
            var history = await TaskButton("ADMIN_CLASS_SUBJECT_WITHOUT_TEACHER", access.ArchivedClassId);
            Check(rows.Items.OfType<OverviewTaskItem>().All(row => row.Task.ClassId != access.ActiveClassId), "same-code active class excluded");
            Check(Find<TextBlock>(diagnostics, "DiagnosticsHeading").Text!.Contains("已归档") &&
                Find<TextBlock>(diagnostics, "DiagnosticsActiveTerm").Text!.Contains("现用春季"), "archive and school active term visible together");
            await Capture(diagnostics, output, "01-wide-archive-diagnostics");
            diagnostics.Width = 840;
            diagnostics.Height = 480;
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
            diagnostics.Close();
            await Until(() => terms.IsEnabled && Find<Button>(terms, "TermOrganizationImport").IsEffectivelyEnabled,
                "return to same selected term and finish refreshing actions");

            Click(Find<Button>(terms, "TermOrganizationImport"));
            var org = await WindowOf<OrganizationImportWindow>();
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
            await Until(() => terms.IsEnabled, "invitation workflow returns to term directory");

            await SelectTerm(terms, access.ActiveTermId);
            Click(Find<Button>(terms, "TermViewDiagnostics"));
            diagnostics = await WindowOf<TermDiagnosticsWindow>();
            diagView = Find<SchoolOverviewView>(diagnostics, "DiagnosticsOverview");
            Click(await TaskButton("ADMIN_CLASS_SUBJECT_WITHOUT_TEACHER", access.ActiveClassId));
            var teaching = await WindowOf<TeachingWindow>();
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
            await Until(() => !form.IsVisible && teaching.IsEnabled, "return to exact assignment form");
            Check((Find<ComboBox>(teaching, "TeachingSubjectPicker").SelectedItem as TeachingSubject)?.Id == access.SubjectId, "original subject and draft retained");
            await Signal("oauth-invitation-only");
            teaching.Close();
            diagnostics.Close();
            terms.Close();
            await Until(() => main.IsEnabled, "main workspace restored");

            Click(Find<Button>(main, "OpenTeacherAccountsButton"));
            var directory = await WindowOf<TeacherAccountsWindow>();
            var emailDirectory = Find<Button>(directory, "TeacherAccountBulk");
            await Until(() => Equals(emailDirectory.Content, "按邮箱分配") && emailDirectory.IsEnabled, "teacher directory points to same desktop workbench");
            Click(emailDirectory);
            allocation = await WindowOf<TeacherAllocationWindow>();
            allocation.Close();
            await Until(() => directory.IsEnabled, "returns to teacher directory");
            directory.Close();

            await PickSchool(access.EmptySchoolId);
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
            Console.WriteLine("PASS: Windows historical diagnostics, invitation scope, OAuth workflow, and no-term audit record.");
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
        async Task SelectTerm(TermPreparationWindow window, string id)
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
}
