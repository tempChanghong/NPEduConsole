using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private ClassroomActionPolicyView.Pending _classroomActionPending = new();
    private WebsitePolicyView? _websitePolicyView;
    private WebsitePolicyView.Pending _websitePolicyPending = new();

    private async Task ShowClassroomActionPolicyAsync(Control anchor)
    {
        if (_client is not { } client || _school is not { } school ||
            _page != ShellPage.Devices || _academicPages.Count != 0) return;
        var view = new ClassroomActionPolicyView(client, school, _classroomActionPending);
        view.CloseRequested = () => ReturnFromAcademicPage(view);
        view.AccessLost += error =>
        {
            if (_client == client && _school == school && !ReturnToLoginIfExpired(error))
            {
                ReturnFromAcademicPage(view);
                DeviceDirectory.ShowNavigationMessage("当前账号已无法管理本校 NPEduTools 安全策略。请重新核对学校和权限。");
            }
        };
        var exit = PushAcademicPage(view, view.CanClose, view.End);
        await view.StartAsync();
        if (!await exit) return;
        RestoreDevicePolicyEntry(client, school, anchor);
    }

    private void CloseClassroomActionPolicy()
    {
        _classroomActionPending = new();
        _websitePolicyPending = new();
    }

    private async Task ShowWebsitePolicyAsync(Control anchor)
    {
        if (_client is not { } client || _school is not { } school || _websitePolicyView is not null ||
            _page != ShellPage.Devices || _academicPages.Count != 0) return;
        var view = _websitePolicyView = new WebsitePolicyView(client, school, _websitePolicyPending);
        view.CloseRequested = () => ReturnFromAcademicPage(view);
        view.AccessLost += error =>
        {
            if (_client == client && _school == school && !ReturnToLoginIfExpired(error))
            {
                ReturnFromAcademicPage(view);
                DeviceDirectory.ShowNavigationMessage("当前账号已无法管理本校网站访问规则。请重新核对学校和权限。");
            }
        };
        var exit = PushAcademicPage(view, view.CanClose, () =>
        {
            view.End();
            if (_websitePolicyView == view) _websitePolicyView = null;
        });
        await view.StartAsync();
        if (!await exit) return;
        RestoreDevicePolicyEntry(client, school, anchor);
    }

    private void RestoreDevicePolicyEntry(NPEduConsole.Core.ConsoleClient client, NPEduConsole.Core.School school, Control anchor)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_client != client || _school != school || _page != ShellPage.Devices || _academicPages.Count != 0 ||
                !anchor.IsEffectivelyVisible || !anchor.IsEffectivelyEnabled) return;
            anchor.BringIntoView();
            anchor.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }
}
