using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private sealed record TeacherAccess(string Origin, string SchoolId, string SchoolCode, string NoTermSchoolId,
        string Password, string EightId, string NineId, string PhysicsId, string TeacherUsername, string TeacherPin, string ResetPin, string SameNameId,
        bool AllowTeacherTestWrites);

    private static int StartTeacherAccounts(string path, string output)
    {
        var access = JsonSerializer.Deserialize<TeacherAccess>(File.ReadAllText(path), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Expected an isolated teacher account fixture.");
        Check(access.AllowTeacherTestWrites && IsLoopback(access.Origin), "Teacher account writes require the isolated test fixture.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyTeacherAccountsAsync(access, output); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyTeacherAccountsAsync(TeacherAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        using var control = new HttpClient { BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(12) };
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
            await Until(() => schools.Items.Cast<School>().Any(school => school.Id == access.NoTermSchoolId), "authorized test schools loaded");
            schools.SelectedItem = schools.Items.Cast<School>().Single(school => school.Id == access.NoTermSchoolId);
            var adminEntry = Find<Button>(main, "OpenTeacherAccountsButton");
            Click(Find<Button>(main, "OpenClassWorkspaceButton"));
            await Until(() => adminEntry.IsEffectivelyVisible && adminEntry.IsEffectivelyEnabled, "teacher directory available without a term");
            Click(adminEntry);
            var accounts = await WindowOf<TeacherAccountsWindow>();
            await Until(() => Find<Button>(accounts, "NewTeacherAccountButton").IsEffectivelyEnabled &&
                Find<Button>(accounts, "NewTeacherAccountButton").IsEffectivelyVisible, "no-term school can create teachers");
            Check(StaffText(accounts, "TeacherListMessage").Contains("暂无"), "the empty directory is explicit");
            await Capture(accounts, output, "01-school-without-term");
            Click(Find<Button>(accounts, "NewTeacherAccountButton"));
            await CreateInlineTeacher("无学期教师", "no-term-teacher");
            await Until(() => StaffText(accounts, "TeacherDetailName").Contains("no-term-teacher") &&
                StaffText(accounts, "TeacherDetailLogin") == "从未登录", "created without an active term or class");
            Click(Find<Button>(accounts, "CloseTeacherAccountsButton"));
            await Until(() => !accounts.IsVisible, "empty school directory closed");

            schools.SelectedItem = schools.Items.Cast<School>().Single(school => school.Id == access.SchoolId);
            Click(Find<Button>(main, "OpenClassWorkspaceButton"));
            var classRows = Find<ListBox>(main, "ClassPicker");
            await Until(() => classRows.IsEffectivelyVisible && classRows.Items.Cast<Classroom>().Any(row => row.Id == access.EightId), "class 8 loaded");
            classRows.SelectedItem = classRows.Items.Cast<Classroom>().Single(row => row.Id == access.EightId);
            await Until(() => Find<Button>(main, "OpenTeachingButton").IsEffectivelyEnabled &&
                Find<Button>(main, "OpenTeachingButton").IsEffectivelyVisible, "class teaching entry available");
            Click(Find<Button>(main, "OpenTeachingButton"));
            var teaching = await AcademicPageAsync<TeachingView>(main);
            var subject = Find<ComboBox>(teaching, "TeachingSubjectPicker");
            await Until(() => subject.SelectedItem is TeachingSubject current && current.Id == access.PhysicsId, "class 8 physics selected");
            var peer = Find<StackPanel>(teaching, "TeachingClassChoices").Children.OfType<CheckBox>()
                .Single(row => AutomationProperties.GetAutomationId(row) == "TeachingClass-" + access.NineId);
            var role = Find<ComboBox>(teaching, "TeachingPositionPicker");
            role.SelectedIndex = 1;
            peer.IsChecked = true;
            Click(Find<Button>(teaching, "TeachingNewTeacher"));
            var create = await WindowOf<TeacherAccountForm>();
            var save = Find<Button>(create, "TeacherFormSave");
            await Until(() => save.IsEffectivelyVisible && save.IsEffectivelyEnabled, "create form has read school PIN mode");
            var pin = Find<TextBox>(create, "TeacherFormPin");
            Check(pin.PasswordChar != default, "the PIN input is masked before entry");
            Find<TextBox>(create, "TeacherFormName").Text = "王老师";
            Find<TextBox>(create, "TeacherFormUsername").Text = access.TeacherUsername;
            pin.Text = access.TeacherPin;
            Find<CheckBox>(create, "TeacherWantDelivery").IsChecked = true;
            Click(save);
            await Until(() => create.Receipt is not null && StaffText(create, "TeacherFormNotice").Contains("没有可交付"),
                "lost successful create recovers the account without an initial PIN to deliver");
            Check(!Find<WrapPanel>(create, "TeacherDeliveryActions").IsVisible, "recovered create has no clipboard or export");
            Click(Find<Button>(create, "TeacherFormClose"));
            var teachers = Find<ComboBox>(teaching, "TeachingTeacherPicker");
            await Until(() => !create.IsVisible && teachers.SelectedItem is TeachingTeacher teacher &&
                teacher.Username == access.TeacherUsername && teachers.IsEffectivelyEnabled, "lost create response recovered by exact server receipt");
            Check(string.IsNullOrEmpty(pin.Text), "PIN was erased from the closed dialog");
            var id = ((TeachingTeacher)teachers.SelectedItem!).Id;
            Check(id != access.SameNameId && subject.SelectedItem is TeachingSubject currentSubject && currentSubject.Id == access.PhysicsId &&
                peer.IsChecked == true && role.SelectedIndex == 1, "confirmed ID selected and previous class/subject/role draft retained");
            await Signal("before-assignment");
            await Capture(teaching, output, "02-return-to-physics-draft");
            peer.IsChecked = false;
            Click(Find<Button>(teaching, "TeachingPreviewButton"));
            var apply = Find<Button>(teaching, "TeachingApplyButton");
            await Until(() => apply.IsEffectivelyEnabled, "single class assignment previewed");
            Click(apply);
            await Until(() => Find<TextBlock>(teaching, "TeachingFeedback").Text?.Contains("已保存 1 个班") == true, "physics assignment saved separately");
            Click(Find<Button>(teaching, "CloseTeachingButton"));
            await Until(() => !teaching.IsVisible, "returned to shell from teaching");

            var overview = Find<SchoolOverviewView>(main, "SchoolOverviewWorkspace");
            Click(Find<Button>(main, "OpenSchoolOverviewButton"));
            await ExpandTaskQueue(overview);
            var list = overview.FindControl<ListBox>("OverviewTasksList")!;
            overview.FindControl<ComboBox>("OverviewTaskFilter")!.SelectedIndex = 3;
            await Until(() => FindTask("LOCAL_ACCOUNT_NEVER_LOGGED_IN") is not null, "first-login task is present for the exact new account");
            await OpenTask("LOCAL_ACCOUNT_NEVER_LOGGED_IN");
            accounts = await WindowOf<TeacherAccountsWindow>();
            var directoryRows = Find<ListBox>(accounts, "TeacherAccountRows");
            await Until(() => directoryRows.SelectedItem is ManagedTeacherAccount row && row.Id == id, "task opens exact account rather than same-name teacher");
            var search = Find<TextBox>(accounts, "TeacherAccountSearch");
            search.Text = "王老师";
            await Until(() => directoryRows.ItemCount == 2, "both same-name accounts are distinguishable");
            Check(StaffText(accounts, "TeacherDetailLogin") == "从未登录", "assignment does not count as a login");
            await Capture(accounts, output, "03-wide-same-name-accounts");

            await Signal("sign-in");
            Click(Find<Button>(accounts, "TeacherAccountRefresh"));
            await Until(() => StaffText(accounts, "TeacherDetailLogin").Length > 10, "actual teacher login updates last-login evidence");
            Click(Find<Button>(accounts, "CloseTeacherAccountsButton"));
            await Until(() => !accounts.IsVisible && FindTask("LOCAL_ACCOUNT_NEVER_LOGGED_IN") is null, "return refresh clears first-login task after actual sign-in");

            main.Width = 840;
            main.Height = 480;
            await Until(() => main.ClientSize is { Width: <= 845, Height: <= 485 }, "compact shell");
            Click(Find<Button>(main, "OpenClassWorkspaceButton"));
            Click(Find<Button>(main, "OpenSchoolAffairsButton"));
            await Until(() => adminEntry.IsEffectivelyVisible, "teacher directory is visible in school affairs");
            Click(adminEntry);
            accounts = await WindowOf<TeacherAccountsWindow>();
            directoryRows = Find<ListBox>(accounts, "TeacherAccountRows");
            search = Find<TextBox>(accounts, "TeacherAccountSearch");
            await Until(() => directoryRows.Items.Cast<ManagedTeacherAccount>().Any(row => row.Id == id), "compact directory loaded");
            search.Text = "王老师";
            directoryRows.SelectedItem = directoryRows.Items.Cast<ManagedTeacherAccount>().Single(row => row.Id == id);
            var openDetails = Find<Button>(accounts, "OpenTeacherDetails");
            await Until(() => openDetails.IsEffectivelyVisible, "compact row can be opened");
            Click(openDetails);
            var toList = Find<Button>(accounts, "BackToTeacherList");
            await Until(() => toList.IsEffectivelyVisible && !directoryRows.IsEffectivelyVisible, "compact detail occupies the available width");
            Click(toList);
            await Until(() => directoryRows.IsEffectivelyVisible && search.Text == "王老师" &&
                directoryRows.SelectedItem is ManagedTeacherAccount row && row.Id == id, "compact return preserves filter and exact selection");
            Click(openDetails);
            Click(Find<Button>(accounts, "ChangeTeacherStatus"));
            var confirm = Find<Button>(accounts, "ConfirmTeacherStatus");
            await Until(() => confirm.IsEffectivelyVisible && confirm.IsEffectivelyEnabled, "disable confirmation is visible");
            Check(StaffText(accounts, "TeacherStatusExplanation").Contains("现有会话会退出，任课和岗位仍保留"), "invalidate-session and retained-role consequences are explicit");
            var cancelStatus = Find<Button>(accounts, "CancelTeacherStatus");
            await Until(() => cancelStatus.IsFocused, "disable confirmation starts on cancel");
            cancelStatus.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
            await Until(() => !confirm.IsEffectivelyVisible && Find<Button>(accounts, "ChangeTeacherStatus").IsFocused,
                "Enter on cancel returns to the original action without disabling the account");
            Check(StaffText(accounts, "TeacherDetailState") == "已启用", "cancel preserves the active teacher");
            Click(Find<Button>(accounts, "ChangeTeacherStatus"));
            await Until(() => cancelStatus.IsFocused, "reopening still starts on cancel");
            cancelStatus.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Escape });
            await Until(() => !confirm.IsEffectivelyVisible, "Escape leaves confirmation");
            Click(Find<Button>(accounts, "ChangeTeacherStatus"));
            await Until(() => cancelStatus.IsFocused, "an explicit choice is required to disable");
            await Capture(accounts, output, "04-narrow-disable-confirmation", includeFrame: true);
            Click(confirm);
            await Until(() => StaffText(accounts, "TeacherDetailState") == "已停用" &&
                Find<Button>(accounts, "CloseTeacherAccountsButton").IsEffectivelyEnabled, "directory confirms disabled");
            await Signal("disabled");
            Click(Find<Button>(accounts, "CloseTeacherAccountsButton"));
            await Until(() => !accounts.IsVisible, "returns to class workspace");
            Click(Find<Button>(main, "OpenSchoolOverviewButton"));
            await ExpandTaskQueue(overview);
            overview.FindControl<ComboBox>("OverviewTaskFilter")!.SelectedIndex = 3;
            await OpenTask("DISABLED_ACCOUNT_HAS_ASSIGNMENT");
            accounts = await WindowOf<TeacherAccountsWindow>();
            await Until(() => StaffText(accounts, "TeacherDetailName").Contains(access.TeacherUsername) &&
                StaffText(accounts, "TeacherDetailState") == "已停用", "disabled-with-assignment task selects the original account");
            Click(Find<Button>(accounts, "ChangeTeacherStatus"));
            Click(Find<Button>(accounts, "ConfirmTeacherStatus"));
            await Until(() => StaffText(accounts, "TeacherDetailState") == "已启用" &&
                Find<Button>(accounts, "CloseTeacherAccountsButton").IsEffectivelyEnabled, "restored same teacher in desktop");
            await Signal("restored");
            await Capture(accounts, output, "05-narrow-restored-original-account");
            Click(Find<Button>(accounts, "ResetTeacherPin"));
            var reset = await WindowOf<TeacherAccountForm>();
            var resetSave = Find<Button>(reset, "TeacherFormSave");
            await Until(() => resetSave.IsEffectivelyEnabled && resetSave.IsEffectivelyVisible, "the original account supports a PIN reset");
            Check(StaffText(reset, "TeacherFormScope").Contains(access.TeacherUsername), "PIN reset applies only to the selected account");
            await Capture(reset, output, "08-narrow-reset-pin");
            Find<TextBox>(reset, "TeacherFormPin").Text = access.ResetPin;
            Click(resetSave);
            await Until(() => !reset.IsVisible && StaffText(accounts, "TeacherAccountsNotice").Contains("PIN 已重置"), "PIN reset reported by the server");
            await Signal("pin-reset");
            Click(Find<Button>(accounts, "CloseTeacherAccountsButton"));
            await Until(() => !accounts.IsVisible && FindTask("DISABLED_ACCOUNT_HAS_ASSIGNMENT") is null, "restoring account refreshes the corresponding task");
            Click(Find<Button>(main, "OpenClassWorkspaceButton"));
            await Until(() => classRows.IsEffectivelyVisible && classRows.Items.Cast<Classroom>().Any(row => row.Id == access.EightId), "return to class 8");
            classRows.SelectedItem = classRows.Items.Cast<Classroom>().Single(row => row.Id == access.EightId);
            await VerifyStaffPicker("OpenClassResponsibilityButton", "homeroom-draft");
            await VerifyStaffPicker("OpenGradeResponsibilityButton", "grade-draft");
            Finish("PASS: Windows 1160×780 / 840×480, create without a term, recovered exact create receipt, preserved teaching and responsibility drafts, first login, revoked sessions, restored original teacher and reset PIN.", 0);

            OverviewTaskItem? FindTask(string code) => list.Items.Cast<OverviewTaskItem>()
                .FirstOrDefault(item => item.Task.Code == code && item.Task.AccountId == id);

            async Task OpenTask(string code)
            {
                Button? action = null;
                await Until(() =>
                {
                    if (FindTask(code) is not { } item) return false;
                    Check(item.Task.Action == "OPEN_TEACHER_ACCOUNT", "account task routes to the Console directory");
                    action = list.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => ReferenceEquals(button.Tag, item) && button.IsEffectivelyVisible);
                    if (action is null) list.ScrollIntoView(list.Items.IndexOf(item));
                    return action is not null;
                }, "teacher account task " + code);
                Click(action!);
            }
        }
        catch (Exception error)
        {
            try
            {
                if (desktop.Windows.LastOrDefault(window => window.IsVisible) is { } window)
                {
                    if (window is TeacherAccountForm) Find<TextBox>(window, "TeacherFormPin").Clear();
                    await Capture(window, output, "failure");
                }
            }
            finally { Finish(error.ToString(), 1); }
        }

        async Task CreateInlineTeacher(string name, string username)
        {
            var form = await WindowOf<TeacherAccountForm>();
            var submit = Find<Button>(form, "TeacherFormSave");
            await Until(() => submit.IsEffectivelyEnabled && submit.IsEffectivelyVisible, "ready to create " + username);
            Find<TextBox>(form, "TeacherFormName").Text = name;
            Find<TextBox>(form, "TeacherFormUsername").Text = username;
            Find<TextBox>(form, "TeacherFormPin").Text = access.TeacherPin;
            var oneTeacherHandoff = username == "no-term-teacher";
            Find<CheckBox>(form, "TeacherWantDelivery").IsChecked = oneTeacherHandoff;
            Click(submit);
            if (oneTeacherHandoff)
            {
                var button = Find<Button>(form, "TeacherSavePin");
                await Until(() => button.IsEffectivelyVisible && button.IsEffectivelyEnabled, "single-teacher handoff is ready");
                var file = Path.Combine(output, "temporary-single-teacher.txt");
                await File.WriteAllTextAsync(file, "");
                try
                {
                    form.SelectPinFile = _ => form.StorageProvider.TryGetFileFromPathAsync(new Uri(file));
                    Click(button);
                    await Until(() => StaffText(form, "TeacherFormNotice").Contains("已另存"), "single-teacher PIN delivered");
                    var saved = await File.ReadAllTextAsync(file);
                    Check(saved.Contains(username) && saved.EndsWith("PIN " + access.TeacherPin), "single-teacher delivery uses the confirmed new account");
                }
                finally { File.Delete(file); }
                Click(Find<Button>(form, "TeacherFormClose"));
            }
            await Until(() => !form.IsVisible && form.Receipt?.Account.Username == username, "confirmed " + username);
        }

        async Task VerifyStaffPicker(string entry, string username)
        {
            var opener = Find<Button>(main, entry);
            await Until(() => opener.IsEffectivelyVisible && opener.IsEffectivelyEnabled, "responsibility entry " + entry);
            Click(opener);
            var staff = await WindowOf<StaffResponsibilitiesWindow>();
            var candidates = Find<ComboBox>(staff, "StaffTeacherPicker");
            await Until(() => candidates.IsEffectivelyEnabled && candidates.Items.Cast<StaffTeacher>().Any(row => row.Id == access.SameNameId), "original responsibility candidates ready");
            candidates.SelectedItem = candidates.Items.Cast<StaffTeacher>().Single(row => row.Id == access.SameNameId);
            Click(Find<Button>(staff, "StaffAddTeacher"));
            Click(Find<Button>(staff, "StaffNewTeacher"));
            await CreateInlineTeacher("职责候选", username);
            await Until(() => candidates.SelectedItem is StaffTeacher selected && selected.AccountLabel == username,
                "new teacher selected by ID in the reopened responsibility picker");
            Check(Find<ListBox>(staff, "StaffDraftRows").Items.Cast<StaffEditorRow>().Single().Teacher.Id == access.SameNameId &&
                Find<ListBox>(staff, "StaffCurrentRows").ItemCount == 0, "original unsaved responsibility stays in the draft only");
            await Capture(staff, output, username == "homeroom-draft" ? "06-homeroom-draft" : "07-grade-draft");
            Click(Find<Button>(staff, "StaffClose"));
            await Until(() => Find<Button>(staff, "StaffDiscard").IsEffectivelyVisible, "responsibility draft is explicitly discarded");
            Click(Find<Button>(staff, "StaffDiscard"));
            await Until(() => !staff.IsVisible, "responsibility window closed");
        }

        async Task<T> WindowOf<T>() where T : Window
        {
            await Until(() => desktop.Windows.OfType<T>().Any(window => window.IsVisible), typeof(T).Name + " opens");
            return desktop.Windows.OfType<T>().Single(window => window.IsVisible);
        }

        async Task Signal(string step)
        {
            using var response = await control.PostAsync("/__teacher/" + step, null);
            Check(response.IsSuccessStatusCode, "server check: " + step);
        }

        void Finish(string message, int code)
        {
            try { File.WriteAllText(Path.Combine(output, "result.txt"), message); }
            finally { System.Console.WriteLine(message); desktop.Shutdown(code); }
        }
    }
}
