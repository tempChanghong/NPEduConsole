using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.App;
using NPEduConsole.Core;

internal static partial class Program
{
    private sealed record TeacherRetirementAccess(string Origin, string SchoolId, string SchoolCode, string Password,
        string Room, string Invitation, string DisabledId, string ActiveId, string SubjectId, bool AllowTeacherRetirementWrites);

    private static int StartTeacherRetirement(string path, string output)
    {
        var access = JsonSerializer.Deserialize<TeacherRetirementAccess>(File.ReadAllText(path), JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("Missing teacher fixture");
        Check(access.AllowTeacherRetirementWrites && IsLoopback(access.Origin), "teacher retirement test writes require isolation");
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() => _ = VerifyTeacherRetirementAsync(access, output)))
            .StartWithClassicDesktopLifetime([]);
    }

    private static async Task VerifyTeacherRetirementAsync(TeacherRetirementAccess access, string output)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var main = (MainWindow)desktop.MainWindow!;
        using var control = new HttpClient { BaseAddress = new Uri(access.Origin) };
        try
        {
            main.Width = 1160;
            main.Height = 780;
            Find<TextBox>(main, "ServerAddress").Text = access.Origin;
            Find<TextBox>(main, "SchoolCode").Text = access.SchoolCode;
            Find<TextBox>(main, "Username").Text = "admin";
            Find<TextBox>(main, "Password").Text = access.Password;
            await PressScoped(main, "LoginButton");
            var schools = Find<ComboBox>(main, "SchoolPicker");
            await Until(() => schools.Items.OfType<School>().Any(row => row.Id == access.SchoolId), "authorized school loaded");
            schools.SelectedItem = schools.Items.OfType<School>().Single(row => row.Id == access.SchoolId);
            await PressScoped(main, "OpenClassWorkspaceButton");
            var (owner, workbench) = await Bulk();
            workbench.Width = 1160;
            workbench.Height = 780;
            var page = workbench.View;
            await PickSpace(page);
            await PressAutomation(page, "TargetedInvitation-" + access.Invitation);
            var exact = await WindowOf<TeacherAccessWindow>();
            await Until(() => ButtonById(exact, "TeacherAccessConfirm")?.IsEffectivelyEnabled == true, "confirm original pending OAuth invitation in PIN mode");
            await CaptureWithFrame(exact, output, "01-exact-withdrawal-review");
            await PressAutomation(exact, "TeacherAccessConfirm");
            await Until(() => ButtonById(exact, "TeacherAccessReload")?.IsEffectivelyEnabled == true &&
                ButtonById(exact, "TeacherAccessReload")?.Content?.ToString() == "只读核对回执", "lost save and receipt remain unresolved");
            await Signal("reopen-after-disconnect");
            await PressAutomation(exact, "TeacherAccessBack");
            await PressScoped(page, "AllocationClose");
            await Until(() => !workbench.IsVisible, "close original directory");
            await PressScoped(owner, "TeacherAccountBulk");
            workbench = await WindowOf<TeacherAllocationWindow>();
            page = workbench.View;
            workbench.Width = 840;
            workbench.Height = 480;
            await PickSpace(page);
            Check(ButtonById(page, "TargetedInvitation-" + access.Invitation) is null, "the original invitation row is gone");
            var pending = Find<WrapPanel>(page, "AllocationAccessPendingActions");
            await Until(() => pending.IsEffectivelyVisible && pending.Children.OfType<Button>().Any(), "the exact pending request remains discoverable");
            await CaptureWithFrame(workbench, output, "02-narrow-pending-without-old-row");
            Click(pending.Children.OfType<Button>().Single());
            exact = await WindowOf<TeacherAccessWindow>();
            await Until(() => TextById(exact, "TeacherAccessNotice").Contains("已核对本人回执"), "reopen uses original receipt only");
            await PressAutomation(exact, "TeacherAccessBack");
            await Until(() => !pending.IsEffectivelyVisible, "completed request removed from pending list");
            await PickSpace(page);

            var findRemove = () => page.GetVisualDescendants().OfType<Button>().FirstOrDefault(button =>
                Equals(button.Content, "核对并移除直接访问") && button.IsEffectivelyVisible && button.IsEffectivelyEnabled);
            await Until(() => findRemove() is not null, "disabled teacher's access can be removed without restoring login");
            Click(findRemove()!);
            exact = await WindowOf<TeacherAccessWindow>();
            await Until(() => TextById(exact, "TeacherAccessImpact").Contains("disabled-access") &&
                TextById(exact, "TeacherAccessImpact").Contains("职责访问保持"), "actual disabled identity and preserved responsibilities");
            await CaptureWithFrame(exact, output, "03-disabled-teacher-direct-access");
            await PressAutomation(exact, "TeacherAccessConfirm");
            await Until(() => TextById(exact, "TeacherAccessNotice").Contains("已核对本人回执"), "member removal confirmed");
            await PressAutomation(exact, "TeacherAccessBack");
            await PressScoped(page, "AllocationClose");
            await Until(() => !workbench.IsVisible, "return to accounts");
            Click(Find<Button>(owner, "CloseTeacherAccountsButton"));
            await PressScoped(main, "OpenSchoolOverviewButton");
            var tasks = Find<ListBox>(Find<SchoolOverviewView>(main, "SchoolOverviewWorkspace"), "OverviewTasksList");
            var taskId = "SchoolOverviewTask-TEACHER_ACCESS_MISSING-" + access.Room + "-" + access.SubjectId + "-" + access.ActiveId;
            var item = () => tasks.Items.OfType<OverviewTaskItem>().FirstOrDefault(row => row.AutomationId == taskId);
            await Until(() => item() is not null, "missing access has original account, space and subject");
            tasks.ScrollIntoView(item());
            await PressAutomation(tasks, taskId);
            exact = await WindowOf<TeacherAccessWindow>();
            await Until(() => TextById(exact, "TeacherAccessImpact").Contains("active-access") &&
                TextById(exact, "TeacherAccessImpact").Contains("助教"), "least direct role for original co-teaching assignment");
            await CaptureWithFrame(exact, output, "04-overview-original-account-and-space");
            await PressAutomation(exact, "TeacherAccessConfirm");
            await Until(() => TextById(exact, "TeacherAccessNotice").Contains("已核对本人回执"), "repair confirmed");
            await PressAutomation(exact, "TeacherAccessBack");
            await Until(() => tasks.Items.Count > 0 && item() is null, "overview drops only the repaired task");
            await Signal("members-and-original-receipt");
            Console.WriteLine("PASS: original invitation recovered after lost response/GET, disabled member only, and precise overview repair.");
            desktop.Shutdown(0);
            return;

            async Task<(TeacherAccountsView, TeacherAllocationWindow)> Bulk()
            {
                await PressScoped(main, "OpenTeacherAccountsButton");
                var accounts = await AcademicPageAsync<TeacherAccountsView>(main);
                await PressScoped(accounts, "TeacherAccountBulk");
                return (accounts, await WindowOf<TeacherAllocationWindow>());
            }

            async Task PickSpace(TeacherAllocationView view)
            {
                if (view.DisplayedStage != TeacherAllocationView.Stage.BySpace) await PressScoped(view, "AllocationBySpace");
                var rooms = Find<ListBox>(view, "AllocationDirectorySpaces");
                await Until(() => rooms.Items.OfType<AllocationWorkspace>().Any(row => row.Id == access.Room), "original directory room loaded");
                rooms.SelectedItem = rooms.Items.OfType<AllocationWorkspace>().Single(row => row.Id == access.Room);
                var open = Find<Button>(view, "AllocationOpenSpaceDetails");
                if (open.IsEffectivelyVisible && open.IsEffectivelyEnabled) Click(open);
            }

            async Task<T> WindowOf<T>() where T : Window
            {
                await Until(() => desktop.Windows.OfType<T>().Any(window => window.IsVisible), typeof(T).Name);
                return desktop.Windows.OfType<T>().Single(window => window.IsVisible);
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (desktop.Windows.LastOrDefault(window => window.IsVisible) is { } window) await CaptureWithFrame(window, output, "failure");
            desktop.Shutdown(1);
        }

        async Task Signal(string name)
        {
            using var result = await control.PostAsync("/__teachers/" + name, null);
            Check(result.IsSuccessStatusCode, "true KV and PostgreSQL: " + name);
        }
        static Button? ButtonById(Visual root, string id) => root.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(button => AutomationProperties.GetAutomationId(button) == id && button.IsEffectivelyVisible);
        static string TextById(Visual root, string id) => root.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(text => AutomationProperties.GetAutomationId(text) == id)?.Text ?? "";
        static async Task PressAutomation(Visual root, string id)
        {
            await Until(() => ButtonById(root, id)?.IsEffectivelyEnabled == true, id);
            var button = ButtonById(root, id)!;
            button.BringIntoView();
            Click(button);
        }
    }
}
