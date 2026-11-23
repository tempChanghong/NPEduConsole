using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class TeacherAllocationWindow
{
    private Window? _archiveInvitationConfirmation;

    private async Task ConfirmArchivedRevocationAsync(TeacherAllocationCatalog catalog, AllocationWorkspace room, AllocationInvitation invite)
    {
        if (!CanEdit || _archiveInvitationConfirmation is not null || !ReferenceEquals(_catalog, catalog) || Term is not { } term) return;
        var cancel = new Button {Content = "取消", Classes = {"action"}};
        var revoke = new Button {Content = "确认撤回这条邀请", Classes = {"action", "accent"}};
        AutomationProperties.SetAutomationId(revoke, "ConfirmArchivedInvitationRevocation");
        var confirmation = new Window
        {
            Title = "撤回归档邀请", Width = 470, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18), Spacing = 10,
                Children =
                {
                    new TextBlock {Text = $"{_school.Name} · {term.Name}（已归档）", Classes = {"label"}, TextWrapping = Avalonia.Media.TextWrapping.Wrap},
                    Description($"{room.Name}（{room.Code}）\n邮箱：{invite.NormalizedEmail}\n角色：{RoleLabel(invite.Role)}"),
                    Description("仅撤回所选待认领邀请。其他邀请、已有直接成员、任课和职责继续有效。"),
                    new StackPanel {Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
                        Children = {cancel, revoke}}
                }
            }
        };
        cancel.Click += (_, _) => confirmation.Close(false);
        revoke.Click += (_, _) => confirmation.Close(true);
        confirmation.Opened += (_, _) => cancel.Focus();
        confirmation.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            confirmation.Close(false);
        };
        _archiveInvitationConfirmation = confirmation;
        bool approved;
        try { approved = await confirmation.ShowDialog<bool>(this); }
        finally { _archiveInvitationConfirmation = null; }
        if (!approved || _closed || !ReferenceEquals(_catalog, catalog)) return;

        await RunAsync(async () =>
        {
            bool confirmed = false;
            string message;
            try
            {
                await _client.RevokeArchivedInvitationAsync(catalog, room, invite, _lifetime.Token);
                confirmed = true;
                message = "服务器已确认撤回这条邀请。";
            }
            catch (ConsoleApiException error) when (error is {Code: "WORKSPACE_INVITATION_NOT_FOUND" or
                "ARCHIVED_INVITATION_CHANGED" or "ARCHIVED_INVITATION_CLAIMED"} || error.StatusCode is 0 or 408 or >= 500)
            {
                message = error.StatusCode is 0 or 408 or >= 500 ? "撤回回执未收到。" : error.Message;
            }
            if (_closed) return;
            ClearReview();
            try { await LoadAsync(catalog.TermId, catalog.GradeId); }
            catch (ConsoleApiException error) when (!IsScopeError(error) && error.StatusCode is not (401 or 403))
            {
                if (!_closed) Status(message + " 暂未读到最新邀请，请刷新后核对。");
                return;
            }
            if (_closed) return;
            var current = _catalog?.Invitations.FirstOrDefault(row => row.Id == invite.Id && row.WorkspaceId == room.Id);
            if (!confirmed) message += current is null ? " 目前已无这条邀请，无法归于本次撤回。" :
                current.ClaimedAt is not null ? " 目前已认领，现有成员请另行核对。" : " 目前仍有待认领记录，可重新核对。";
            Status(message);
            if (current is not null) FocusInvitation(current.Id);
            else Dispatcher.UIThread.Post(() =>
            {
                if (!_closed && AllocationTabs.SelectedIndex == 3) AllocationDirectorySpaces.Focus();
                else if (!_closed && AllocationTabs.SelectedIndex == 2) AllocationDirectoryTeachers.Focus();
            }, DispatcherPriority.Loaded);
        }, writing: true);
    }
}
