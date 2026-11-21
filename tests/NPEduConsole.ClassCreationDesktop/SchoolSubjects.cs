using System.Net.Http.Json;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private sealed record CatalogAccess(string Origin, string SchoolId, string SchoolCode, string Password, string NoTermId,
        string SevenId, string EightId, string MathId, string TeacherId, bool AllowSchoolSubjectWrites, string? PhysicsId);
    private sealed record PhysicsReceipt(string PhysicsId);

    private static int StartSchoolSubjects(string path, string output, bool reopen)
    {
        var access = JsonSerializer.Deserialize<CatalogAccess>(File.ReadAllText(path), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Use a disposable school-subject fixture.");
        Check(access.AllowSchoolSubjectWrites && IsLoopback(access.Origin), "The school-subject test must stay on loopback.");
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifySchoolSubjectsAsync(access, output, reopen); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifySchoolSubjectsAsync(CatalogAccess access, string output, bool reopen)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        using var fixture = new HttpClient { BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            main.Width = 1160;
            main.Height = 780;
            Find<TextBox>(main, "ServerAddress").Text = access.Origin;
            Find<TextBox>(main, "SchoolCode").Text = access.SchoolCode;
            Find<TextBox>(main, "Username").Text = "admin";
            Find<TextBox>(main, "Password").Text = access.Password;
            Click(Find<Button>(main, "LoginButton"));
            if (reopen)
            {
                await ReopenAndSwitchTerm();
                Finish("PASS: fresh Windows session sees the saved catalog and teaching IDs; term switch blocks continuation into old classes.", 0);
                return;
            }

            await SelectSchool(access.NoTermId);
            var catalog = await OpenCatalog();
            await Until(() => Text(catalog, "SubjectDirectoryEmpty").Contains("还没有学科"), "empty school catalog without a term");
            Check(Text(catalog, "CatalogScope").Contains("全校共用"), "the school-wide scope is visible");
            await Capture(catalog, output, "01-empty-no-term");
            Press(catalog, "NewSubjectButton");
            Fill(catalog, "no-term", "校本阅读", 1, "17");
            Press(catalog, "SaveSubjectButton");
            await Until(() => Text(catalog, "CatalogFeedback").Contains("已确认创建") && Rows(catalog).ItemCount == 1, "create in a school without a term");
            Check(Rows(catalog).Items.Cast<SubjectDirectoryRow>().Single().Subject is { Code: "NO-TERM", Category: "ELECTIVE", SortOrder: 17 },
                "normalized code, category and display order returned from KV");
            catalog.Close();
            await Until(() => !catalog.IsVisible, "close the term-independent directory");

            await SelectSchool(access.SchoolId);
            await Signal("arm-catalog-delay");
            Press(main, "OpenSchoolSubjectsButton");
            var late = await WindowOf<SchoolSubjectCatalogWindow>();
            await Signal("wait-catalog-arrival");
            await SelectSchool(access.NoTermId);
            await Until(() => !late.IsVisible, "switching school closes the previous catalog");
            catalog = await OpenCatalog();
            await Signal("wait-catalog-sent");
            Check(Rows(catalog).Items.Cast<SubjectDirectoryRow>().Single().Subject is { Code: "NO-TERM" } noTermRow &&
                noTermRow.SchoolId == access.NoTermId, "late response cannot populate the next school's catalog");
            catalog.Close();
            await Until(() => !catalog.IsVisible, "close the isolated catalog after switching schools");

            await SelectSchool(access.SchoolId);
            catalog = await OpenCatalog();
            await Until(() => Rows(catalog).ItemCount >= 53, "long real catalog");
            SelectSubject(catalog, access.MathId);
            await Until(() => Find<Border>(catalog, "SubjectDirectoryPane").IsEffectivelyVisible &&
                Find<Border>(catalog, "SubjectEditorPane").IsEffectivelyVisible, "wide editor and directory appear together");
            await Capture(catalog, output, "02-wide-catalog");
            catalog.Close();
            await Until(() => !catalog.IsVisible, "close wide directory");

            main.Width = 840;
            main.Height = 480;
            await Until(() => main.ClientSize is { Width: <= 850, Height: <= 490 }, "compact shell arranged");
            catalog = await OpenCatalog();
            await Until(() => Rows(catalog).ItemCount >= 53, "compact long catalog loaded");
            var last = Rows(catalog).Items.Cast<SubjectDirectoryRow>().Last();
            Rows(catalog).ScrollIntoView(last);
            await Until(() => Rows(catalog).ContainerFromItem(last) is ListBoxItem realized && FitsBatchWindow(catalog, realized), "long catalog can reach its last row");
            await Capture(catalog, output, "03-narrow-long-list");
            var search = Find<TextBox>(catalog, "SubjectSearch");
            search.Text = "CAT-MATH";
            await Until(() => Rows(catalog).ItemCount == 1, "code search in the catalog");
            SelectSubject(catalog, access.MathId);
            await Until(() => !Find<Border>(catalog, "SubjectDirectoryPane").IsEffectivelyVisible, "narrow editor replaces directory");
            var draftName = Find<TextBox>(catalog, "SubjectName");
            draftName.Text = "本机数学草稿";
            await Signal("race-edit");
            Press(catalog, "SaveSubjectButton");
            await Until(() => Text(catalog, "SubjectServerValues").Contains("学校数学新名称") &&
                Find<Button>(catalog, "AdoptSubjectVersionButton").IsEffectivelyEnabled, "conflict re-reads the exact subject");
            Check(draftName.Text == "本机数学草稿" && !Find<Button>(catalog, "SaveSubjectButton").IsEnabled &&
                FitsBatchWindow(catalog, Find<Button>(catalog, "AdoptSubjectVersionButton")), "conflict retains the draft and disables blind overwrite");
            await Capture(catalog, output, "04-narrow-version-conflict");
            Press(catalog, "AdoptSubjectVersionButton");
            Check(draftName.Text == "本机数学草稿", "adopting the school version preserves the local draft");
            draftName.Text = "数学（核对后更新）";
            Press(catalog, "SaveSubjectButton");
            await Until(() => search.IsEffectivelyVisible && search.Text == "CAT-MATH" && Rows(catalog).SelectedItem is SubjectDirectoryRow row &&
                row.Subject.Id == access.MathId && row.Subject.Name == "数学（核对后更新）" &&
                Rows(catalog).ContainerFromItem(row) is ListBoxItem { IsFocused: true }, "saving on the updated baseline restores filter, subject and list focus");
            search.Clear();
            Press(catalog, "NewSubjectButton");
            Fill(catalog, "CAT-MATH", "重复代码草稿", 0, "10");
            Press(catalog, "SaveSubjectButton");
            await Until(() => Text(catalog, "SubjectCodeError").Contains("另一门学科") && Find<TextBox>(catalog, "SubjectCode").IsFocused,
                "duplicate code is displayed beside the focused field");
            Check(draftName.Text == "重复代码草稿", "the duplicate error keeps the full draft");
            await Capture(catalog, output, "05-narrow-duplicate-code");

            Fill(catalog, "lost-other", "本机创建的草稿", 2, "0");
            await Until(() => Find<Button>(catalog, "SaveSubjectButton").IsEffectivelyEnabled, "changing the conflicting code re-enables create");
            Press(catalog, "SaveSubjectButton");
            await Until(() => Text(catalog, "CatalogFeedback").Contains("另一位管理员的学科") &&
                Find<Button>(catalog, "SaveSubjectButton").Content is "按原请求重试", "another admin's same code has no matching receipt");
            Check(draftName.Text == "本机创建的草稿" && catalog.CreatedSubject is null, "another account's result does not replace the draft");
            await Capture(catalog, output, "06-unconfirmed-other-owner");
            Press(catalog, "SaveSubjectButton");
            await Until(() => Find<TextBlock>(catalog, "SubjectCodeError").IsEffectivelyVisible &&
                Text(catalog, "SubjectCodeError").Contains("另一门学科"), "same request is refused because the competing subject owns the code");
            Press(catalog, "CloseSubjectCatalogButton");
            Press(catalog, "DiscardSubjectButton");
            catalog.Close();
            await Until(() => !catalog.IsVisible, "return to the school");

            await SelectClass(access.SevenId);
            var setup = await OpenSetup();
            Choice(setup, "Class", access.EightId).IsChecked = true;
            Choice(setup, "Subject", access.MathId).IsChecked = true;
            Press(setup, "SubjectSetupNewSubjectButton");
            catalog = await NewInlineEditor();
            Fill(catalog, "cat-physics", "物理", 0, "9");
            await Capture(catalog, output, "07-inline-create");
            Press(catalog, "SaveSubjectButton");
            await Until(() => !catalog.IsVisible && Text(setup, "SubjectSetupFeedback").Contains("并选中"), "lost create reply resumes the original setup draft");
            var receipt = await fixture.GetFromJsonAsync<PhysicsReceipt>("/__school-subjects/confirmed-physics") ?? throw new InvalidDataException("No physics receipt.");
            var recovered = Choice(setup, "Subject", receipt.PhysicsId);
            Check(recovered.IsChecked == true && Choice(setup, "Subject", access.MathId).IsChecked == true &&
                Choice(setup, "Class", access.SevenId).IsChecked == true && Choice(setup, "Class", access.EightId).IsChecked == true,
                "confirmed ID, earlier subject and both class selections all survive");
            await Capture(setup, output, "08-inline-restored-selection");
            Choice(setup, "Subject", access.MathId).IsChecked = false;
            Press(setup, "SubjectSetupPreviewButton");
            await Until(() => Find<ListBox>(setup, "SubjectSetupRows").ItemCount == 2 && Find<Button>(setup, "SubjectSetupApplyButton").IsEnabled,
                "both class-and-physics rows before explicit rule save");
            Press(setup, "SubjectSetupApplyButton");
            await Until(() => Find<Button>(setup, "SubjectSetupTeachingButton").IsEffectivelyEnabled, "physics rules explicitly saved");
            Press(setup, "SubjectSetupTeachingButton");
            var teaching = await WindowOf<TeachingWindow>();
            await Until(() => Find<ComboBox>(teaching, "TeachingSubjectPicker").SelectedItem is TeachingSubject item && item.Id == receipt.PhysicsId,
                "opens teaching for the exact physics ID");
            var teachers = Find<ComboBox>(teaching, "TeachingTeacherPicker");
            teachers.SelectedItem = teachers.Items.Cast<TeachingTeacher>().Single(item => item.Id == access.TeacherId);
            Press(teaching, "TeachingPreviewButton");
            await Until(() => Find<Button>(teaching, "TeachingApplyButton").IsEnabled, "physics teaching preview");
            Press(teaching, "TeachingApplyButton");
            await Until(() => Text(teaching, "TeachingFeedback").Contains("已保存 1 个班"), "only the anchor class received the teacher");
            teaching.Close();
            Press(main, "OpenSchoolOverviewButton");
            var overview = Find<SchoolOverviewView>(main, "SchoolOverviewWorkspace");
            await ExpandTaskQueue(overview);
            var tasks = overview.FindControl<ListBox>("OverviewTasksList")!;
            await Until(() => tasks.Items.Cast<OverviewTaskItem>()
                .Where(item => item.Task.Code == "ADMIN_CLASS_SUBJECT_WITHOUT_TEACHER" && item.Task.SubjectId == receipt.PhysicsId)
                .Select(item => item.Task.ClassId).SequenceEqual(new[] { access.EightId }), "only class 8 is missing a physics teacher");
            await Capture(main, output, "09-overview-only-class-8");
            Finish("PASS: Windows school catalog, code conflict, optimistic edit, competing same code, recovered creation, setup for classes 7/8, teacher for 7 and overview for 8.", 0);
        }
        catch (Exception error)
        {
            try
            {
                if (desktop.Windows.LastOrDefault(window => window.IsVisible) is { } visible)
                    await Capture(visible, output, reopen ? "reopen-failure" : "failure");
            }
            finally { Finish(error.ToString(), 1); }
        }

        async Task ReopenAndSwitchTerm()
        {
            string id = access.PhysicsId ?? throw new InvalidDataException("Missing created subject identity.");
            await SelectSchool(access.SchoolId);
            var catalog = await OpenCatalog();
            await Until(() => Rows(catalog).Items.Cast<SubjectDirectoryRow>().Any(row => row.Subject.Id == id), "find physics by the original ID after restart");
            catalog.Close();
            await Until(() => !catalog.IsVisible, "close read-back catalog");
            await SelectClass(access.SevenId);
            Press(main, "OpenTeachingButton");
            var teaching = await WindowOf<TeachingWindow>();
            var subjects = Find<ComboBox>(teaching, "TeachingSubjectPicker");
            await Until(() => subjects.Items.Cast<TeachingSubject>().Any(item => item.Id == id), "class 7 still has physics");
            subjects.SelectedItem = subjects.Items.Cast<TeachingSubject>().Single(item => item.Id == id);
            var existing = Find<ComboBox>(teaching, "TeachingExistingPicker");
            await Until(() => existing.IsEffectivelyVisible && existing.ItemCount == 1, "reopened assignment is present on class 7");
            await Capture(teaching, output, "10-reopened-physics-teaching");
            teaching.Close();
            await SelectClass(access.EightId);
            var setup = await OpenSetup();
            Check(Choice(setup, "Subject", id).IsEnabled, "class 8 setup uses the same subject ID");
            Press(setup, "SubjectSetupNewSubjectButton");
            catalog = await NewInlineEditor();
            Fill(catalog, "cat-chem", "化学", 0, "10");
            await Signal("switch-term");
            Press(catalog, "SaveSubjectButton");
            await Until(() => !catalog.IsVisible && Text(setup, "SubjectSetupFeedback").Contains("已加入全校学科目录") &&
                Text(setup, "SubjectSetupFeedback").Contains("学期已切换"), "school subject survives a term switch but original class setup is blocked");
            Check(!Find<Button>(setup, "SubjectSetupApplyButton").IsEnabled, "old term draft cannot be applied");
            await Capture(setup, output, "11-term-changed");
        }

        async Task SelectSchool(string id)
        {
            var schools = Find<ComboBox>(main, "SchoolPicker");
            await Until(() => schools.Items.Cast<School>().Any(item => item.Id == id), "authorized school loaded");
            schools.SelectedItem = schools.Items.Cast<School>().Single(item => item.Id == id);
            Press(main, "OpenClassWorkspaceButton");
            await Until(() => Find<Button>(main, "OpenSchoolSubjectsButton").IsEffectivelyVisible, "school catalog entry in the class workspace");
        }

        async Task SelectClass(string id)
        {
            var rooms = Find<ListBox>(main, "ClassPicker");
            await Until(() => rooms.Items.Cast<Classroom>().Any(item => item.Id == id), "the class list includes the expected ID");
            rooms.SelectedItem = rooms.Items.Cast<Classroom>().Single(item => item.Id == id);
            await Until(() => Find<Button>(main, "OpenSubjectSetupButton").IsEffectivelyVisible, "the class has a setup action");
        }

        async Task<SchoolSubjectCatalogWindow> OpenCatalog()
        {
            Press(main, "OpenSchoolSubjectsButton");
            var window = await WindowOf<SchoolSubjectCatalogWindow>();
            await Until(() => Find<Button>(window, "NewSubjectButton").IsEffectivelyEnabled, "catalog snapshot loaded");
            return window;
        }

        async Task<SubjectSetupWindow> OpenSetup()
        {
            Press(main, "OpenSubjectSetupButton");
            var window = await WindowOf<SubjectSetupWindow>();
            await Until(() => Find<Button>(window, "SubjectSetupNewSubjectButton").IsEffectivelyEnabled, "setup catalog ready");
            return window;
        }

        async Task<SchoolSubjectCatalogWindow> NewInlineEditor()
        {
            var window = await WindowOf<SchoolSubjectCatalogWindow>();
            await Until(() => Find<Button>(window, "SaveSubjectButton") is { IsEffectivelyEnabled: true, Content: "创建并返回开科" }, "school-wide inline create form is ready");
            return window;
        }

        async Task<T> WindowOf<T>() where T : Window
        {
            await Until(() => desktop.Windows.OfType<T>().Any(item => item.IsVisible), typeof(T).Name);
            return desktop.Windows.OfType<T>().Single(item => item.IsVisible);
        }

        async Task Signal(string path)
        {
            using var reply = await fixture.PostAsync("/__school-subjects/" + path, null);
            Check(reply.IsSuccessStatusCode, "The isolated fixture operation succeeded.");
        }

        void Finish(string message, int code)
        {
            try { File.WriteAllText(Path.Combine(output, reopen ? "reopen-result.txt" : "desktop-result.txt"), message); }
            finally { System.Console.WriteLine(message); desktop.Shutdown(code); }
        }
    }

    private static string Text(Window window, string name) => Find<TextBlock>(window, name).Text ?? "";
    private static ListBox Rows(Window window) => Find<ListBox>(window, "SubjectDirectoryRows");

    private static void SelectSubject(Window window, string id) => Rows(window).SelectedItem = Rows(window).Items
        .Cast<SubjectDirectoryRow>().Single(row => row.Subject.Id == id);

    private static void Fill(Window window, string code, string name, int category, string order)
    {
        Find<TextBox>(window, "SubjectCode").Text = code;
        Find<TextBox>(window, "SubjectName").Text = name;
        Find<ComboBox>(window, "SubjectCategory").SelectedIndex = category;
        Find<TextBox>(window, "SubjectOrder").Text = order;
    }

    private static void Press(Window window, string name)
    {
        var button = Find<Button>(window, name);
        Check(button.IsEffectivelyEnabled && button.IsEffectivelyVisible, $"{name} is available through the real window.");
        Click(button);
    }
}
