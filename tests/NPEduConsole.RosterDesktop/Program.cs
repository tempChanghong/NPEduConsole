using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Input;
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

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is not ([_] or ["--native-scroll", _] or ["--live" or "--live-save", _, _]))
            throw new ArgumentException("Specify [--native-scroll] <directory> or --live/--live-save <fixture.json> <directory>.");
        bool nativeScroll = args is ["--native-scroll", _];
        using var fixture = args.Length <= 2 ? new RosterFixture() : null;
        var access = fixture?.Access ?? JsonSerializer.Deserialize<Access>(File.ReadAllText(args[1]), JsonSerializerOptions.Web)!;
        Check(new Uri(access.Origin).IsLoopback, "The roster desktop test must use a loopback service.");
        bool saveToFixture = args[0] == "--live-save";
        if (saveToFixture && !access.AllowRosterTestWrites) throw new InvalidDataException("Expected a disposable roster fixture.");
        string output = args[^1];
        Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyAsync(access, fixture, output, saveToFixture, nativeScroll); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyAsync(Access access, RosterFixture? fixture, string output, bool saveToFixture, bool nativeScroll)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        try
        {
            var main = (MainWindow)desktop.MainWindow!;
            var open = Find<Button>(main, "OpenClassRosterButton");
            var classes = Find<ListBox>(main, "ClassPicker");
            var login = Find<Button>(main, "LoginButton");
            Find<TextBox>(main, "ServerAddress").Text = access.Origin;
            Find<TextBox>(main, "SchoolCode").Text = access.SchoolCode;
            Find<TextBox>(main, "Username").Text = access.Username;
            Find<TextBox>(main, "Password").Text = access.Password;
            Click(login);
            await Until(() => Find<Button>(main, "OpenClassWorkspaceButton").IsEnabled, "login has selected the fixture school");
            Click(Find<Button>(main, "OpenClassWorkspaceButton"));
            await Until(() => classes.IsEffectivelyVisible && classes.ItemCount >= 2, "classes load when device overview is unavailable");
            Check(!open.IsEnabled, "A class must be selected before editing the roster.");
            SelectClass(access.ClassId);
            Check(main.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "设备状态暂不可用") &&
                !main.GetVisualDescendants().OfType<Button>().Any(button => button.Content is "配置口令接入"), "A failed device lookup cannot suggest binding the screen again.");
            Click(open);
            var window = desktop.Windows.OfType<ClassRosterWindow>().Single();
            T Roster<T>(string name) where T : Control => Find<T>(window, name);
            void Action(string name) => Click(Roster<Button>(name));
            var list = Roster<ItemsControl>("RosterRows");
            var paste = Roster<TextBox>("RosterPasteText");
            var preview = Roster<Button>("PreviewRosterButton");
            string Feedback() => Roster<TextBlock>("RosterFeedback").Text ?? "";
            await Until(() => list.ItemCount == 3, "initial roster populated");
            Check(main.TryGetPlatformHandle() is { HandleDescriptor: "HWND" } owner && !IsWindowEnabled(owner.Handle) &&
                  Roster<TextBlock>("RosterScope").Text!.Contains(((Classroom)classes.SelectedItem!).Name),
                "A modal roster displays the selected class and locks the owner.");

            if (fixture is null)
            {
                if (saveToFixture)
                {
                    Roster<ComboBox>("RosterImportMode").SelectedIndex = 1;
                    paste.Text = "001\t陈晨修正\r\n002\t陈晨\r\n009\tExcel 新增";
                    Action("ApplyRosterImport");
                }
                else
                {
                    Action("AddRosterStudent");
                    await Edit(3, "桌面测试草稿", "009");
                }
                Click(preview);
                await Until(() => Roster<ListBox>("RosterChangesList").ItemCount == (saveToFixture ? 3 : 1),
                    "preview in real KV workspace");
                if (saveToFixture)
                {
                    var previewRows = Roster<ListBox>("RosterChangesList");
                    await RosterWorkspaceFocus.AssertRosterPreviewAsync(window, previewRows.Items.Cast<object>().ToArray());
                    Check(previewRows.GetVisualDescendants().OfType<Border>().Any(row =>
                        AutomationProperties.GetAutomationId(row) == "RosterChange-roster-a-Edit"),
                        "the review identifies the exact edited homonym");
                }
                await Capture(window, output, "live-preview");
                if (saveToFixture)
                {
                    Action("ConfirmRosterSave");
                    await Until(() => Feedback().Contains("名单已保存"), "commit the confirmed desktop preview");
                    Action("CloseRosterButton");
                    await Until(() => !window.IsVisible && open.IsEffectivelyEnabled, "close the saved roster");
                    Click(open);
                    window = desktop.Windows.OfType<ClassRosterWindow>().Single();
                    await Until(() => listFor(window).ItemCount == 3, "read the saved roster again from KV");
                    Check((await RowControls<TextBox>(2)).Any(field => field.Text == "009"), "reopened roster retains the leading-zero number");
                    Check((await RowControls<TextBox>(0)).Any(field =>
                        field.Text == "陈晨修正" && AutomationProperties.GetAutomationId(field) == "RosterStudentName-roster-a"),
                        "reopened roster retains the original ID for the same-named student edited by number");
                    Check((await RowControls<TextBox>(1)).Any(field =>
                        field.Text == "陈晨" && AutomationProperties.GetAutomationId(field) == "RosterStudentName-roster-b"),
                        "the other same-named student keeps the original identity");
                    await Capture(window, output, "live-reopened");
                    Action("CloseRosterButton");
                }
                else
                {
                    Action("CancelRosterReview");
                    await RosterWorkspaceFocus.AssertRosterPreviewCancelFocusAsync(window);
                    Action("CloseRosterButton");
                    Action("ConfirmRosterDiscard");
                }
                await Until(() => !window.IsVisible && open.IsEffectivelyEnabled, "return to real school classes");
                SelectClass(access.EmptyClassId);
                Click(open);
                window = desktop.Windows.OfType<ClassRosterWindow>().Single();
                await Until(() => Roster<Grid>("RosterEditorPane").IsVisible && listFor(window).ItemCount == 0, "real class without any screen or students");
                await Capture(window, output, "live-empty-class");
                window.Close();
                await SignOut();
                End("PASS: Windows with real KV/PostgreSQL, disabled NPEP, no-screen class, " +
                    (saveToFixture ? "confirmed save and reopened roster." : "and an unsubmitted roster preview."), 0);
                return;
            }

            Check(window.Width >= 1100 && window.Height >= 730, "the editor uses its school's working area");
            window.Width = 840;
            window.Height = 480;
            await Until(() => window.Content is Control { Bounds.Width: <= 845, Bounds.Height: <= 485 },
                "narrow content has been arranged before measuring fixed bars");
            paste.Text = string.Join(Environment.NewLine, Enumerable.Range(1, 97).Select(i => $"S{i:D3}\t长名单学生{i:D3}"));
            Action("ApplyRosterImport");
            await Until(() => list.ItemCount == 100, "import 97 rows alongside the original identities");
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            await RosterWorkspaceFocus.AssertRosterWorkspaceAsync(window, 100);
            var hint = Roster<TextBlock>("RosterImportHint");
            Check(paste.TranslatePoint(default, window) is { } pasteOrigin &&
                hint.TranslatePoint(default, window) is { } hintOrigin &&
                pasteOrigin.Y + paste.Bounds.Height <= hintOrigin.Y + 1,
                "the compact paste box does not overlap import guidance");
            await Capture(window, output, "00-long-editor-compact");
            if (nativeScroll)
            {
                await RosterWorkspaceFocus.AssertNativeScrollAsync(window);
                await Capture(window, output, "00-native-scroll-compact");
            }
            Click(preview);
            await Until(() => Roster<ListBox>("RosterChangesList").ItemCount == 97, "97 explicit additions appear in the long preview");
            var longPreview = Roster<ListBox>("RosterChangesList");
            await RosterWorkspaceFocus.AssertRosterPreviewAsync(window, [longPreview.Items[0]!, longPreview.Items[^1]!]);
            await Capture(window, output, "00-long-preview-compact");
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Source = window, Key = Key.Escape });
            await RosterWorkspaceFocus.AssertRosterPreviewCancelFocusAsync(window);
            Action("ReloadRosterButton");
            Action("ConfirmRosterDiscard");
            await Until(() => list.ItemCount == 3, "discard the isolated long draft without saving");
            window.Width = 1160;
            window.Height = 780;

            await Edit(0, "张晓", "007");
            await RowAction(1, "上移");
            await RowAction(0, "下移");
            await RowAction(1, "上移");
            await RowAction(2, "移出");
            Action("AddRosterStudent");
            await Edit(2, "刘同学", "007");
            Click(preview);
            Check(Feedback().Contains("重复") && fixture.Puts == 0, "Duplicate numbers are rejected before any network write.");
            await Edit(2, "刘同学", "003");
            paste.Text = "007\t张晓\r\n004\t王同学";
            Action("ApplyRosterImport");
            Check(list.ItemCount == 4, "Excel TSV updates an existing number and appends a new row.");
            paste.Text = "无号同学";
            Action("ApplyRosterImport");
            Check(list.ItemCount == 5, "Plain text adds an unnumbered student.");

            paste.Text = "未应用的粘贴内容";
            window.Close();
            Check(window.IsVisible && Roster<Grid>("RosterDiscardActions").IsVisible, "Closing asks before discarding draft and unapplied text.");
            Action("KeepRosterEditing");
            Action("ReloadRosterButton");
            Check(Roster<Grid>("RosterDiscardActions").IsVisible, "Reload also asks before discarding edits.");
            Action("KeepRosterEditing");
            Check(paste.Text == "未应用的粘贴内容" && list.ItemCount == 5, "Canceling either prompt preserves table and paste.");
            paste.Clear();
            await Capture(window, output, "01-draft");

            Click(preview);
            var changes = Roster<ListBox>("RosterChangesList");
            Check(new[] { "新增", "修改", "调整顺序", "移出" }.All(kind => changes.Items.Cast<object>().Any(change => change.ToString()!.Contains(kind))) && fixture.Puts == 0,
                "Preview shows all real change categories without saving.");
            await Capture(window, output, "02-preview");
            Action("ConfirmRosterSave");
            var first = await fixture.NextPut();
            Check(first.ExpectedRevision == "r1" && first.Students[1].Id == "id-1" && first.Students[1].StudentNumber == "007", "Editing and reorder preserve the student ID.");
            fixture.Current = new("r2", [.. fixture.Current.Students, new("id-remote", "另一位管理员新增", "010")]);
            first.Reply(409, "CLASS_ROSTER_CONFLICT");
            var current = Roster<ListBox>("CurrentRosterRows");
            await Until(() => current.ItemCount == 4 && Roster<Button>("AdoptRosterRevisionButton").IsEnabled, "409 reads current roster for comparison");
            Check(list.ItemCount == 5 && !preview.IsEnabled && fixture.Puts == 1, "Conflict preserves edits and prevents automatic resubmission.");
            await Capture(window, output, "03-conflict");
            window.Width = 840;
            window.Height = 480;
            await Capture(window, output, "03-conflict-compact");
            window.Width = 1160;
            window.Height = 780;
            current.SelectedIndex = 0;
            await Until(() => list.GetVisualDescendants().OfType<Border>().Any(box =>
                box.Classes.Contains("compared") && box.GetVisualDescendants().OfType<TextBox>().Any(field =>
                    AutomationProperties.GetAutomationId(field) == "RosterStudentName-id-1" && field.Text == "张晓")),
                "compare locates the same student ID without replacing the draft text");
            current.SelectedIndex = 3;
            Action("UseCurrentStudentButton");
            Action("AdoptRosterRevisionButton");
            Click(preview);
            Check(!changes.Items.Cast<object>().Any(change => change.ToString()!.Contains("移出") && change.ToString()!.Contains("另一位管理员新增")), "Adopting the visible row keeps the remote addition.");
            Action("ConfirmRosterSave");
            var second = await fixture.NextPut();
            Check(second.ExpectedRevision == "r2" && second.Students.Any(row => row.Id == "id-remote"), "Reconciled save preserves the remotely added student's ID.");
            fixture.Current = new("r3", second.Students.Select((row, i) => row with { Id = row.Id ?? "new-" + i }).ToArray());
            second.Reply(408);
            await Until(() => Feedback().Contains("服务器名单与本次提交一致"), "ambiguous save is verified read-only");
            Check(fixture.Puts == 2 && !preview.IsEnabled, "A lost receipt never repeats the write.");
            await Capture(window, output, "04-save-rechecked");

            Roster<ComboBox>("RosterImportMode").SelectedIndex = 1;
            paste.Text = "010 另一位管理员新增";
            Action("ApplyRosterImport");
            Click(preview);
            Check(changes.Items.Cast<object>().Count(change => change.ToString()!.Contains("移出")) == 5, "Whole-class replacement previews actual removals.");
            Action("ConfirmRosterSave");
            var third = await fixture.NextPut();
            third.Reply(409, "CLASS_ROSTER_TERM_CHANGED");
            await Until(() => Feedback().Contains("学期已切换") && Roster<Grid>("RosterComparisonPane").IsVisible && current.ItemCount == 6, "changed term blocks editing even when read succeeds");
            Check(!Roster<Button>("AdoptRosterRevisionButton").IsEnabled && !preview.IsEnabled && list.ItemCount == 1, "Old-term draft is preserved and cannot be submitted again.");
            Action("CloseRosterButton");
            Action("ConfirmRosterDiscard");
            await Until(() => open.IsEffectivelyEnabled, "back to class selection");

            SelectClass(access.EmptyClassId);
            Click(open);
            window = desktop.Windows.OfType<ClassRosterWindow>().Single();
            await Until(() => Roster<TextBlock>("EmptyRosterDraft").IsEffectivelyVisible, "explicit empty roster for a class without a screen");
            await Capture(window, output, "05-empty-class");
            Action("AddRosterStudent");
            await Edit(0, "不会跨账号的草稿", "");
            Action("PreviewRosterButton");
            Action("ConfirmRosterSave");
            var denied = await fixture.NextPut();
            denied.Reply(403);
            await Until(() => Feedback().Contains("无权"), "permission loss");
            Check(listFor(window).ItemCount == 0 && !Roster<Grid>("RosterEditorPane").IsVisible, "A permission rejection clears protected student data.");
            window.Close();
            await Until(() => open.IsEffectivelyEnabled, "return after permission rejection");

            fixture.HoldNextRead = true;
            Click(open);
            window = desktop.Windows.OfType<ClassRosterWindow>().Single();
            var staleWindow = window;
            await fixture.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await SignOut();
            fixture.ReleaseRead.TrySetResult();
            await Task.Delay(100);
            Check(!staleWindow.IsVisible && listFor(staleWindow).ItemCount == 0, "Logout closes the modal and discards late data.");
            End("PASS: Windows roster selection without NPEP, row edits and both imports, discard protection, actual preview, conflict reconciliation, unknown save, term and permission guards, and late logout isolation.", 0);

            async Task SignOut()
            {
                Click(main.GetVisualDescendants().OfType<Button>().Single(button => button.Content is "退出登录"));
                await Until(() => login.IsEffectivelyVisible && login.IsEffectivelyEnabled, "signed out");
            }

            void SelectClass(string id) => classes.SelectedItem = classes.Items.Cast<Classroom>().Single(room => room.Id == id);

            static ItemsControl listFor(Window selected) => Find<ItemsControl>(selected, "RosterRows");

            async Task Edit(int index, string name, string number)
            {
                var boxes = await RowControls<TextBox>(index);
                boxes[0].Text = name;
                boxes[1].Text = number;
            }

            async Task RowAction(int index, string title) => Click((await RowControls<Button>(index)).Single(button => Equals(button.Content, title)));

            async Task<T[]> RowControls<T>(int index) where T : Control
            {
                T[] controls = [];
                await Until(() =>
                {
                    var rows = listFor(window);
                    controls = rows.GetVisualDescendants().OfType<T>().Where(control => index < rows.ItemCount && control.DataContext == rows.Items[index]).ToArray();
                    return controls.Length >= 2;
                }, "interactive row controls rendered");
                return controls;
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint window);

    private static T Find<T>(Window window, string name) where T : Control => window.FindControl<T>(name) ?? throw new InvalidDataException(name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task Capture(Window target, string output, string name)
    {
        await Task.Delay(120);
        var root = (Control)target.Content!;
        using var image = new RenderTargetBitmap(new PixelSize((int)root.Bounds.Width, (int)root.Bounds.Height));
        image.Render(root);
        image.Save(Path.Combine(output, name + ".png"), new PngBitmapEncoderOptions());
    }

    private static async Task Until(Func<bool> condition, string step)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try { while (!condition()) await Task.Delay(25, deadline.Token); }
        catch (OperationCanceledException) { throw new TimeoutException(step); }
    }

    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidDataException(message);
    }

    private sealed record Access(string Origin, string SchoolCode, string Username, string Password, string ClassId, string EmptyClassId,
        bool AllowRosterTestWrites = false);
    private sealed record Snapshot(string Revision, RosterStudent[] Students);
    private sealed record Submission(string ExpectedRevision, RosterStudent[] Students, bool RequireActiveTerm);

    private sealed class PendingPut(Submission request)
    {
        public string ExpectedRevision => request.ExpectedRevision;
        public RosterStudent[] Students => request.Students;
        public TaskCompletionSource<IResult> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Reply(int status, string? code = null) => Response.SetResult(Results.Json(new { success = false, code }, statusCode: status));
    }

    private sealed class RosterFixture : IDisposable
    {
        private readonly WebApplication _app;
        private readonly Channel<PendingPut> _writes = Channel.CreateUnbounded<PendingPut>();
        public Access Access { get; }
        public Snapshot Current { get; set; } = new("r1", [new("id-1", "张同学", "001"), new("id-2", "周同学", "002"), new("id-3", "赵同学", null)]);
        public int Puts { get; private set; }
        public bool HoldNextRead { get; set; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RosterFixture()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            _app = builder.Build();
            _app.MapPost("/accounts/local/login", () => Reply(new { access_token = "isolated-fixture-token" }));
            _app.MapPost("/accounts/logout", () => Reply(new { }));
            var term = new { id = "term", status = "ACTIVE" };
            _app.MapGet("/api/v2/me/schools", () => Reply(new[] { new { role = "ADMIN", school = new { id = "school", name = "名单测试学校", terms = new[] { term } } } }));
            _app.MapGet("/api/v2/admin/schools/school/classroom-screens", () => Reply(new[]
            {
                new { id = "screen", schoolId = "school", administrativeClassId = "room-a", name = "测试大屏", isActive = true, npepBindingRevision = 1 }
            }));
            _app.MapGet("/api/v2/admin/schools/school/academic-structure", () => Reply(new { term, administrativeClasses = new[]
            {
                new { id = "room-a", name = "高二（1）班", isActive = true, grade = new { name = "高二" } },
                new { id = "room-b", name = "高二（3）班", isActive = true, grade = new { name = "高二" } }
            } }));
            _app.MapGet("/api/v2/npep/schools/school/device-overview", () => Results.StatusCode(426));
            const string rosterPath = "/api/v2/admin/schools/school/administrative-classes/{classId}/students";
            _app.MapGet(rosterPath, async (string classId, HttpContext context) =>
            {
                if (HoldNextRead)
                {
                    HoldNextRead = false;
                    ReadStarted.TrySetResult();
                    await ReleaseRead.Task.WaitAsync(context.RequestAborted);
                }
                var roster = classId == "room-a" ? Current : new Snapshot("empty", []);
                return Reply(new { roster.Revision, students = roster.Students.Select(row => new { row.Id, row.Name, row.StudentNumber, administrativeClassId = classId }) });
            });
            _app.MapPut(rosterPath, async (Submission submission, HttpContext context) =>
            {
                Check(submission.RequireActiveTerm, "Roster edits require the active-term guard.");
                Puts++;
                var pending = new PendingPut(submission);
                await _writes.Writer.WriteAsync(pending, context.RequestAborted);
                return await pending.Response.Task.WaitAsync(context.RequestAborted);
            });
            _app.StartAsync().GetAwaiter().GetResult();
            Access = new(_app.Urls.Single(), "FIXTURE", "admin", "fixture-only", "room-a", "room-b");
        }

        public async Task<PendingPut> NextPut()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            return await _writes.Reader.ReadAsync(deadline.Token);
        }

        private static IResult Reply(object data) => Results.Json(new { success = true, data });

        public void Dispose() => Task.Run(async () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _app.StopAsync(deadline.Token);
            await _app.DisposeAsync();
        }).GetAwaiter().GetResult();
    }
}
