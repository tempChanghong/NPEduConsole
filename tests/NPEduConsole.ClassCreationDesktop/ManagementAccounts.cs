using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private sealed record ManagementAccess(string Origin,string SchoolId,string SchoolCode,string OwnerId,string TeacherId,
        string ClassId,string PhotoId,string Pin,string NewPin,string HeirPin,bool Isolated);

    private static int StartManagementAccounts(string fixture, string output)
    {
        var access = JsonSerializer.Deserialize<ManagementAccess>(File.ReadAllText(fixture), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Expected isolated management fixture.");
        Check(access.Isolated && IsLoopback(access.Origin), "Management writes require the loopback fixture.");
        Directory.CreateDirectory(output);
        var rosterPath = Path.GetFullPath(Path.Combine(output, "roster-no-pin.csv"));
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => { _ = VerifyManagementAccountsAsync(access, output, rosterPath); }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyManagementAccountsAsync(ManagementAccess access, string output, string rosterPath)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        using var checkpoints = new HttpClient {BaseAddress = new Uri(access.Origin), Timeout = TimeSpan.FromSeconds(40)};
        try
        {
            main.Width = 1160;
            main.Height = 780;
            await Login("admin", access.Pin);
            var window = await OpenManagement();
            Check(Find<Button>(window,"ManagementCreate").IsEffectivelyVisible, "owner may create a management account without an active term");
            await Capture(window, output, "01-owner-wide-no-term");
            Select(window, access.TeacherId);
            ChooseRole(window, "学校管理员");
            var first = await Review(window, "ManagementRolePreview");
            Check(ImpactText(first).Contains("全校自习照片"), "grant explains the school-wide photo permission");
            Check(Find<TextBox>(window,"ManagementPin").IsEffectivelyVisible, "shared-password teacher can prepare a personal PIN while granting");
            await Capture(window, output, "02-grant-photo-impact");
            Find<TextBox>(window,"ManagementPin").Text = access.NewPin;
            Click(Confirm(first));
            await Until(() => Find<TextBlock>(window,"ManagementStatus").Text?.Contains("请勿交付")==true &&
                Find<Grid>(window,"ManagementPanes").IsEffectivelyEnabled, "lost successful write is confirmed by read-only receipt");
            Check(Find<TextBox>(window,"ManagementPin").Text == "", "initial PIN leaves the edit field once sent");
            Check(Find<TextBlock>(window,"ManagementAccountAuthority").Text?.Contains("待接任者") == true, "pending actual management login is shown");
            await Signal("teacher-granted");
            Check(!Find<Button>(window,"ManagementSavePin").IsEffectivelyVisible, "a lost successful write never offers the original PIN after receipt lookup");
            Select(window, access.OwnerId);
            Check(!Find<Button>(window,"ManagementSavePin").IsEffectivelyVisible, "changing the selected identity clears any previous PIN");
            await ExportRoster(window, access, rosterPath);
            await SignOut(window);

            await Login("viewer", access.NewPin);
            var teacherOwn = await OpenManagement();
            Check(!Find<Button>(teacherOwn,"ManagementCreate").IsEffectivelyVisible, "admin cannot create managers");
            Select(teacherOwn,access.OwnerId);
            Check(!Find<StackPanel>(teacherOwn,"ManagementRoleFields").IsEffectivelyVisible &&
                !Find<Button>(teacherOwn,"ManagementResetPin").IsEffectivelyVisible, "admin cannot maintain the owner");
            teacherOwn.Close();
            var classes = Find<Button>(main,"OpenClassWorkspaceButton");
            await Until(() => classes.IsEffectivelyEnabled && classes.IsEffectivelyVisible,"class workspace reachable");
            Click(classes);
            Click(Find<Button>(main,"OpenSchoolAffairsButton"));
            var openStudy = Find<Button>(main,"OpenSchoolStudyButton");
            await Until(() => openStudy.IsEffectivelyEnabled && openStudy.IsEffectivelyVisible,"self-study workspace reachable");
            Click(openStudy);
            var study = await WindowOf<SchoolStudyWindow>();
            var studyRows = Find<ListBox>(study,"StudyRooms");
            var classPhotos = Find<Button>(study,"StudyClassRecords");
            await Until(() => classPhotos.IsEffectivelyEnabled &&
                studyRows.Items.OfType<SchoolStudyRoomRow>().Any(row=>row.Room.ClassId==access.ClassId),"find original classroom");
            studyRows.SelectedItem = studyRows.Items.OfType<SchoolStudyRoomRow>().Single(row=>row.Room.ClassId==access.ClassId);
            Click(classPhotos);
            var photos = await WindowOf<StudyRecordsWindow>();
            var records = Find<StudyRecordsView>((StudyRecordsPage)photos.Content!,"Records");
            await Until(() => records.FindControl<ListBox>("StudyRecordsList")!.Items.OfType<StudyRecordRow>()
                .Any(row=>row.PhotoId==access.PhotoId),"candidate used actual Console login to obtain the scoped photo directory");
            await Capture(photos,output,"03-candidate-photo-access");
            photos.Close();
            study.Close();
            await SignOut();

            await Login("admin",access.Pin);
            window = await OpenManagement();
            Select(window,access.TeacherId);
            ChooseRole(window,"普通学校成员");
            var demote = await Review(window,"ManagementRolePreview");
            Check(ImpactText(demote).Contains("照片访问立即失效") && ImpactText(demote).Contains("独立教学权限"),"removal impact distinguishes school and teaching");
            Click(Confirm(demote));
            await Saved(window);
            await Signal("teacher-demoted");

            Click(Find<Button>(window,"ManagementCreate"));
            Find<TextBox>(window,"ManagementNewName").Text = "接任所有者";
            Find<TextBox>(window,"ManagementNewUsername").Text = "successor";
            var newRole = Find<ComboBox>(window,"ManagementNewRole");
            newRole.SelectedItem = newRole.Items.Cast<object>().Single(row=>row.ToString()=="学校所有者");
            var create = await Review(window,"ManagementCreatePreview");
            Find<TextBox>(window,"ManagementPin").Text = access.HeirPin;
            Click(Confirm(create));
            await RetryReady(window);
            Check(Find<TextBlock>(window,"ManagementRetryTarget").Text?.Contains("successor")==true,
                "the original target is visible before PIN re-entry");
            await Capture(window, output,"04a-retry-missing-receipt");
            window.Close();
            await Until(() => !window.IsVisible, "unreceived request may be left pending");
            Click(Find<Button>(main, "OpenClassWorkspaceButton"));
            Click(Find<Button>(main, "OpenSchoolAffairsButton"));
            Click(Find<Button>(main, "OpenTeacherAccountsButton"));
            var teacherDirectory = await AcademicPageAsync<TeacherAccountsView>(main);
            var teacherRows = Find<ListBox>(teacherDirectory, "TeacherAccountRows");
            await Until(() => teacherRows.Items.OfType<ManagedTeacherAccount>().Any(row => row.Id == access.TeacherId), "the original teacher remains in the active directory");
            teacherRows.SelectedItem = teacherRows.Items.OfType<ManagedTeacherAccount>().Single(row => row.Id == access.TeacherId);
            var teacherDetails = Find<Button>(teacherDirectory, "OpenTeacherDetails");
            if (teacherDetails.IsEffectivelyVisible) Click(teacherDetails);
            Click(Find<Button>(teacherDirectory, "TeacherSchoolAccount"));
            var pinned = await WindowOf<SchoolManagementAccountsWindow>();
            await Until(() => Find<TextBlock>(pinned, "ManagementAccountTitle").Text?.Contains("viewer")==true &&
                Find<TextBlock>(pinned, "ManagementOtherPending").IsEffectivelyVisible, "the original account stays selected next to another unresolved request");
            Check(!Find<Button>(pinned, "ManagementReceipt").IsEffectivelyVisible &&
                !Find<Button>(pinned, "ManagementRetry").IsEffectivelyVisible, "another account cannot read or retry the pending create from this target");
            await Capture(pinned, output, "04a-pinned-account-other-pending", includeFrame: true);
            pinned.Close();
            await Until(() => !pinned.IsVisible, "the pinned account closes without ending the original request");
            Click(Find<Button>(teacherDirectory, "CloseTeacherAccountsButton"));
            var globalAccounts = Find<Button>(main,"OpenSchoolManagementAccountsButton");
            await Until(() => !teacherDirectory.IsEffectivelyVisible && globalAccounts.IsEffectivelyEnabled, "the school-level receipt entry is available again");
            await Dispatcher.UIThread.InvokeAsync(() => Click(globalAccounts), DispatcherPriority.ApplicationIdle);
            window = await WindowOf<SchoolManagementAccountsWindow>();
            await RetryReady(window);
            newRole = Find<ComboBox>(window,"ManagementNewRole");
            Find<TextBox>(window,"ManagementRetryPin").Text=access.HeirPin;
            Click(Find<Button>(window,"ManagementRetry"));
            await Saved(window);
            Check(Find<Button>(window,"ManagementSavePin").IsEffectivelyVisible,"the retry actually wrote and may deliver that PIN");
            await Signal("missing-request-retried");

            Click(Find<Button>(window,"ManagementCreate"));
            Find<TextBox>(window,"ManagementNewName").Text="迟到回执测试管理员";
            Find<TextBox>(window,"ManagementNewUsername").Text="late-receipt";
            newRole.SelectedItem = newRole.Items.Cast<object>().Single(row=>row.ToString()=="学校管理员");
            var late = await Review(window,"ManagementCreatePreview");
            Find<TextBox>(window,"ManagementPin").Text=access.HeirPin;
            Click(Confirm(late));
            await RetryReady(window);
            Find<TextBox>(window,"ManagementRetryPin").Text=access.NewPin;
            Click(Find<Button>(window,"ManagementRetry"));
            await Until(() => Find<TextBlock>(window,"ManagementStatus").Text?.Contains("请勿交付")==true &&
                Find<Grid>(window,"ManagementPanes").IsEffectivelyEnabled,"server identifies the earlier write instead of the retry PIN");
            Check(!Find<Button>(window,"ManagementSavePin").IsEffectivelyVisible,"late original receipt never yields the replacement PIN");
            await Capture(window, output,"04b-retry-late-original");
            await Signal("late-receipt-kept-original-pin");
            window.Width=840;
            window.Height=480;
            await Until(() => window.ClientSize is {Width: <= 845,Height: <= 485}, "actual compact window");

            var backToList=Find<Button>(window,"ManagementBackToList");
            Click(backToList);
            Select(window,access.OwnerId,compact:true);
            ChooseRole(window,"学校管理员");
            var awaiting=await Review(window,"ManagementRolePreview",blocked:true);
            Check(ImpactText(awaiting).Contains("重新登录"), "app refuses the unverified heir");
            await Capture(window,output,"04-compact-awaiting-successor");
            var escape=new KeyEventArgs {RoutedEvent=InputElement.KeyDownEvent,Key=Key.Escape};
            awaiting.RaiseEvent(escape);
            await Until(() => Find<Button>(window,"ManagementRolePreview").IsFocused, "Escape restores focus to the originating action");

            await Signal("heir-created");
            Click(Find<Button>(window,"ManagementRefresh"));
            await Until(() => Find<TextBlock>(window,"ManagementStatus").Text == "", "disabled heir reloaded");
            ChooseRole(window,"学校管理员");
            var disabled=await Review(window,"ManagementRolePreview",blocked:true);
            Check(!Confirm(disabled).IsEffectivelyEnabled,"disabled heir cannot receive the handover");
            await Signal("heir-disabled");
            Click(disabled.FindControl<Button>("SchoolAccountImpactBack")!);
            await Signal("heir-verified");
            Click(Find<Button>(window,"ManagementRefresh"));
            await Until(() => Find<TextBlock>(window,"ManagementStatus").Text == "", "new verified login refreshed");
            ChooseRole(window,"学校管理员");
            var ready=await Review(window,"ManagementRolePreview");
            await Capture(window,output,"05-compact-handoff-ready");
            Click(Confirm(ready));
            await Saved(window);
            await Signal("handoff-done");
            Check(Find<TextBlock>(window,"ManagementScope").Text?.Contains("当前会话为学校管理员")==true,
                "owner self-demotion refreshes real management capabilities");
            await Capture(window,output,"06-handoff-complete");

            const string original = "MUST REMAIN EXACTLY THE SAME";
            string protectedPath = Path.Combine(output, "preserved-existing-file.txt");
            await File.WriteAllTextAsync(protectedPath, original);
            Click(Find<Button>(window,"ManagementBackToList"));
            Select(window, access.TeacherId, compact:true);
            await ResetForDelivery(window);
            bool cancelled = false;
            window.SelectPinFile = _ => { cancelled = true; return Task.FromResult<IStorageFile?>(null); };
            Click(Find<Button>(window,"ManagementSavePin"));
            await Until(() => cancelled && Find<Button>(window,"ManagementSavePin").IsEffectivelyEnabled, "cancelled file choice can be retried");
            Check(await File.ReadAllTextAsync(protectedPath) == original, "cancel keeps the original file");
            window.SelectPinFile = _ => window.StorageProvider.TryGetFileFromPathAsync(protectedPath);
            using (var locked = new FileStream(protectedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Click(Find<Button>(window,"ManagementSavePin"));
                await Until(() => Find<TextBlock>(window,"ManagementStatus").Text?.Contains("交付未完成")==true &&
                    Find<Button>(window,"ManagementSavePin").IsEffectivelyEnabled, "a file replacement failure leaves delivery available");
            }
            Check(await File.ReadAllTextAsync(protectedPath) == original, "write error keeps the original file");
            await TestBlockedDelivery(window, "rotate-in-file-picker", "凭据已变更");
            await ResetForDelivery(window);
            await TestBlockedDelivery(window, "revoke-in-file-picker", "无本校管理权限");
            await Capture(window, output, "07-compact-delivery-revoked", includeFrame:true);
            Console.WriteLine("PASS: Windows management directory, freshly authorized photo access, compact impact focus and verified owner handover.");
            desktop.Shutdown(0);

            async Task ResetForDelivery(SchoolManagementAccountsWindow selected)
            {
                await Until(() => Find<Button>(selected,"ManagementResetPin").IsEffectivelyEnabled, "the original selected account can be reset");
                var review = await Review(selected,"ManagementResetPin");
                Find<TextBox>(selected,"ManagementPin").Text = access.NewPin;
                Click(Confirm(review));
                await Saved(selected);
                Check(Find<Button>(selected,"ManagementSavePin").IsEffectivelyVisible,"a fresh verified reset has one delivery");
            }

            async Task TestBlockedDelivery(SchoolManagementAccountsWindow selected, string checkpoint, string notice)
            {
                selected.SelectPinFile = async _ =>
                {
                    await Signal(checkpoint);
                    return await selected.StorageProvider.TryGetFileFromPathAsync(protectedPath);
                };
                Click(Find<Button>(selected,"ManagementSavePin"));
                await Until(() => Find<TextBlock>(selected,"ManagementStatus").Text?.Contains(notice)==true &&
                    !Find<Button>(selected,"ManagementSavePin").IsEffectivelyVisible,"the current server decision stops stale delivery");
                Check(await File.ReadAllTextAsync(protectedPath) == original, "the previous file survives the server rejecting delivery");
            }
        }
        catch (Exception error)
        {
            if (desktop.Windows.LastOrDefault(candidate=>candidate.IsVisible) is { } window)
            {
                if (window is SchoolManagementAccountsWindow) Find<TextBox>(window,"ManagementPin").Text = "";
                try { await Capture(window,output,"failure"); } catch (Exception captureError) { Console.Error.WriteLine(captureError.Message); }
            }
            await File.WriteAllTextAsync(Path.Combine(output,"failure.txt"),error.ToString());
            Console.Error.WriteLine(error);
            desktop.Shutdown(1);
        }

        async Task Login(string username,string password)
        {
            await Until(() => Find<Button>(main,"LoginButton").IsEffectivelyVisible,"login form ready");
            Find<TextBox>(main,"ServerAddress").Text=access.Origin;
            Find<TextBox>(main,"SchoolCode").Text=access.SchoolCode;
            Find<TextBox>(main,"Username").Text=username;
            Find<TextBox>(main,"Password").Text=password;
            Click(Find<Button>(main,"LoginButton"));
            var picker=Find<ComboBox>(main,"SchoolPicker");
            await Until(() => picker.Items.OfType<School>().Any(row=>row.Id==access.SchoolId),"actual management login succeeds");
            picker.SelectedItem=picker.Items.OfType<School>().Single(row=>row.Id==access.SchoolId);
        }
        async Task SignOut(Window? child=null)
        {
            child?.Close();
            await Until(() => main.GetVisualDescendants().OfType<Button>().Any(b=>b.IsEffectivelyVisible && b.IsEffectivelyEnabled &&
                b.Content?.ToString()=="退出登录"),"sign-out available");
            Click(main.GetVisualDescendants().OfType<Button>().Single(b=>b.IsEffectivelyVisible && b.Content?.ToString()=="退出登录"));
            await Until(() => Find<Button>(main,"LoginButton").IsEffectivelyEnabled && Find<Button>(main,"LoginButton").IsEffectivelyVisible,"session closed");
        }
        async Task<SchoolManagementAccountsWindow> OpenManagement()
        {
            var entry=Find<Button>(main,"OpenSchoolManagementAccountsButton");
            await Until(() => entry.IsEffectivelyEnabled && entry.IsEffectivelyVisible,"management sidebar entry reachable");
            Click(entry);
            var window=await WindowOf<SchoolManagementAccountsWindow>();
            await Until(() => Find<ListBox>(window,"ManagementRows").ItemCount>0 && Find<ListBox>(window,"ManagementRows").IsEffectivelyEnabled,"school management roster loaded");
            return window;
        }
        async Task<T> WindowOf<T>() where T:Window
        {
            await Until(() => desktop.Windows.OfType<T>().Any(w=>w.IsVisible),typeof(T).Name+" opened");
            return desktop.Windows.OfType<T>().Single(w=>w.IsVisible);
        }
        static void Select(Window window,string account,bool compact=false)
        {
            var filter=Find<ComboBox>(window,"ManagementFilter");
            filter.SelectedIndex=1;
            var rows=Find<ListBox>(window,"ManagementRows");
            rows.SelectedItem=rows.Items.OfType<ManagementAccountRow>().Single(row=>row.Account.Id==account);
            if(compact) Click(Find<Button>(window,"ManagementOpenDetail"));
        }
        static void ChooseRole(Window window,string role)
        {
            var picker=Find<ComboBox>(window,"ManagementRole");
            picker.SelectedItem=picker.Items.Cast<object>().Single(row=>row.ToString()==role);
        }
        static async Task<SchoolAccountImpactView> Review(Window window,string button,bool blocked=false)
        {
            Click(Find<Button>(window,button));
            var view=Find<SchoolAccountImpactView>(window,"ManagementImpact");
            await Until(() => view.IsEffectivelyVisible && view.IsEffectivelyEnabled && Confirm(view).IsEffectivelyEnabled!=blocked,
                "server impact preview available");
            return view;
        }
        static Button Confirm(SchoolAccountImpactView view)=>view.FindControl<Button>("SchoolAccountImpactConfirm")!;
        static string ImpactText(SchoolAccountImpactView view)=>string.Join("\n",view.FindControl<StackPanel>("SchoolAccountImpactItems")!
            .GetVisualDescendants().OfType<TextBlock>().Select(block=>block.Text));
        static Task Saved(Window window)=>Until(() => Find<TextBlock>(window,"ManagementStatus").Text=="服务器已确认本次操作。" &&
            Find<Grid>(window,"ManagementPanes").IsEffectivelyVisible && Find<Grid>(window,"ManagementPanes").IsEffectivelyEnabled,
            "server acknowledged the exact request");
        static Task RetryReady(Window window)=>Until(() => Find<Button>(window,"ManagementRetry").IsEffectivelyVisible &&
            Find<Button>(window,"ManagementRetry").IsEffectivelyEnabled,"the exact receipt was read and retry is available");
        async Task Signal(string step)
        {
            using var response=await checkpoints.PostAsync("/__management/"+step,null);
            Check(response.IsSuccessStatusCode,"isolated management checkpoint "+step);
        }
    }

    private static async Task ExportRoster(SchoolManagementAccountsWindow window, ManagementAccess access, string destination)
    {
        await File.WriteAllTextAsync(destination, "preexisting fixture data");
        window.SelectRosterFile = options =>
        {
            Check(options.Title == "导出全校无密账号名册" && options.DefaultExtension == "csv", "roster and PIN use separate export actions");
            return window.StorageProvider.TryGetFileFromPathAsync(destination);
        };
        Click(Find<Button>(window,"ManagementExportRoster"));
        await Until(() => Find<TextBlock>(window,"ManagementStatus").Text?.StartsWith("已导出全校名册")==true, "CSV export completed");
        var csv = await File.ReadAllTextAsync(destination);
        Check(csv.Contains("最近登录") && csv.Contains("当前学期的直接教学空间") && csv.Contains("教学组候选") &&
            csv.Contains("／TEACHER") && csv.Contains(access.TeacherId) && !csv.Contains(access.Pin) &&
            !csv.Contains(access.NewPin) && !csv.Contains(access.HeirPin) && !csv.Contains("$2b$"),
            "actual CSV includes the whole school scope, logged-in status and assigned classroom without any credential");
        window.SelectRosterFile = null;
    }
}
