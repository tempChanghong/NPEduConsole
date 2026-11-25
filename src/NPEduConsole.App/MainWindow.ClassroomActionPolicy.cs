using Avalonia.Controls;

namespace NPEduConsole.App;

public sealed partial class MainWindow
{
    private ClassroomActionPolicyWindow? _classroomActionPolicyWindow;
    private ClassroomActionPolicyWindow.Pending _classroomActionPending = new();
    private WebsitePolicyWindow? _websitePolicyWindow;
    private WebsitePolicyWindow.Pending _websitePolicyPending = new();

    private async Task ShowClassroomActionPolicyAsync(Control anchor)
    {
        if (_client is not { } client || _school is not { } school || _classroomActionPolicyWindow is not null) return;
        var window = new ClassroomActionPolicyWindow(client, school, _classroomActionPending);
        window.AccessLost += error =>
        {
            if (_client == client && _school == school && !ReturnToLoginIfExpired(error))
                SetWorkspaceMessage("本校管理访问已失效，请重新选择有权限的学校。", isError: true);
        };
        _classroomActionPolicyWindow = window;
        try { await window.ShowDialog(this); }
        finally { if (_classroomActionPolicyWindow == window) _classroomActionPolicyWindow = null; }
        if (_client == client && _school == school && anchor.IsEffectivelyVisible) anchor.Focus();
    }

    private void CloseClassroomActionPolicy()
    {
        _classroomActionPolicyWindow?.ClearAndClose();
        _classroomActionPending = new();
        _websitePolicyWindow?.ClearAndClose();
        _websitePolicyPending = new();
    }

    private async Task ShowWebsitePolicyAsync(Control anchor)
    {
        if (_client is not { } client || _school is not { } school || _websitePolicyWindow is not null) return;
        var window = new WebsitePolicyWindow(client, school, _websitePolicyPending);
        window.AccessLost += error =>
        {
            if (_client == client && _school == school && !ReturnToLoginIfExpired(error))
                SetWorkspaceMessage("本校管理访问已失效，请重新选择有权限的学校。", isError: true);
        };
        _websitePolicyWindow = window;
        try { await window.ShowDialog(this); }
        finally { if (_websitePolicyWindow == window) _websitePolicyWindow = null; }
        if (_client == client && _school == school && anchor.IsEffectivelyVisible) anchor.Focus();
    }
}
