using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class TeacherAllocationView
{
    private sealed record DirectoryPerson(string? Id, string Name, string? Username, string? Email, bool Disabled, bool Manageable)
    {
        public override string ToString() => $"{Name}（{Username ?? Email ?? Id}）{(Disabled ? " · 已停用" : "")}";
    }
    private TeacherAllocationReceipt? _lastReceipt;
    private TeacherAccessWindow? _targetedAccess;
    private Guid[] _visibleAccessRequests = [];
    private string _appliedTeacherQuery = "", _appliedSpaceQuery = "";

    private void FocusInvitation(string invitationId)
    {
        var invitation = _catalog?.Invitations.SingleOrDefault(row => row.Id == invitationId);
        if (invitation is null)
        {
            Status("这条邀请已不存在或已经超出当前范围，请返回刷新名单。");
            return;
        }
        AllocationSpaceSearch.Clear();
        RebuildDirectories();
        SetStage(Stage.BySpace, showDirectoryDetail: true);
        AllocationDirectorySpaces.SelectedItem = AllocationDirectorySpaces.Items.OfType<AllocationWorkspace>()
            .SingleOrDefault(room => room.Id == invitation.WorkspaceId);
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed) return;
            var target = AllocationSpaceDetails.Children.OfType<Border>().FirstOrDefault(card => Equals(card.Tag, invitationId));
            target?.BringIntoView();
            target?.Focus();
        }, DispatcherPriority.Loaded);
    }

    private void DirectorySearchChanged(object? sender, TextChangedEventArgs args)
    {
        if (_client is null || _closed) return;
        if ((AllocationTeacherSearch.Text?.Trim() ?? "") != _appliedTeacherQuery ||
            (AllocationSpaceSearch.Text?.Trim() ?? "") != _appliedSpaceQuery) RebuildDirectories();
    }

    private void RebuildDirectories()
    {
        _appliedTeacherQuery = AllocationTeacherSearch.Text?.Trim() ?? "";
        _appliedSpaceQuery = AllocationSpaceSearch.Text?.Trim() ?? "";
        if (_catalog is not { } data)
        {
            AllocationDirectoryTeachers.ItemsSource = null;
            AllocationDirectorySpaces.ItemsSource = null;
            RenderTeacherDirectory();
            RenderSpaceDirectory();
            return;
        }
        var picked = AllocationDirectoryTeachers.SelectedItem as DirectoryPerson;
        var board = AllocationDirectorySpaces.SelectedItem as AllocationWorkspace;
        var people = data.Accounts.Select(row => new DirectoryPerson(row.Id, row.Name, row.Username, row.Email, row.Disabled, row.Manageable)).ToList();
        foreach (var email in data.Invitations.Select(row => row.NormalizedEmail).Concat(
            _lastReceipt?.TermId == data.TermId ? _lastReceipt?.Teachers.Where(row => row.Email is not null).Select(row => row.Email!) ?? [] : [])
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (people.Any(row => string.Equals(row.Email, email, StringComparison.OrdinalIgnoreCase))) continue;
            people.Add(new(null, "邮箱邀请", null, email, false, true));
        }
        AllocationDirectoryTeachers.ItemsSource = people.Where(row => row.ToString().Contains(_appliedTeacherQuery, StringComparison.OrdinalIgnoreCase)).ToArray();
        AllocationDirectoryTeachers.SelectedItem = AllocationDirectoryTeachers.Items.OfType<DirectoryPerson>().FirstOrDefault(row =>
            picked?.Id is not null ? row.Id == picked.Id : picked is not null && row.Email == picked.Email)
            ?? AllocationDirectoryTeachers.Items.OfType<DirectoryPerson>().FirstOrDefault();

        AllocationDirectorySpaces.ItemsSource = data.Workspaces.Where(row => row.ToString().Contains(_appliedSpaceQuery, StringComparison.OrdinalIgnoreCase)).ToArray();
        AllocationDirectorySpaces.SelectedItem = AllocationDirectorySpaces.Items.OfType<AllocationWorkspace>().FirstOrDefault(row => row.Id == board?.Id)
            ?? AllocationDirectorySpaces.Items.OfType<AllocationWorkspace>().FirstOrDefault();
        RenderTeacherDirectory();
        RenderSpaceDirectory();
    }

    private void DirectoryPersonChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_closed || !ReferenceEquals(args.Source, AllocationDirectoryTeachers)) return;
        RenderTeacherDirectory();
        AllocationOpenTeacherDetails.IsEnabled = AllocationDirectoryTeachers.SelectedItem is not null;
    }

    private void DirectorySpaceChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_closed || !ReferenceEquals(args.Source, AllocationDirectorySpaces)) return;
        RenderSpaceDirectory();
        AllocationOpenSpaceDetails.IsEnabled = AllocationDirectorySpaces.SelectedItem is not null;
    }

    private void RenderTeacherDirectory()
    {
        AllocationTeacherDetails.Children.Clear();
        if (_catalog is not { } data || AllocationDirectoryTeachers.SelectedItem is not DirectoryPerson teacher)
        {
            AllocationTeacherResultHeading.Text = "选择教师或邀请";
            return;
        }
        AllocationTeacherResultHeading.Text = teacher.ToString();
        if (teacher.Disabled) AllocationTeacherDetails.Children.Add(Description("账号当前已停用。以下为服务器中保留的关系。"));
        if (data.TermId is null) AllocationTeacherDetails.Children.Add(Description("当前查看的是学校级账号。选择学期后可查看教学空间访问。"));
        foreach (var room in data.Workspaces)
        {
            var member = data.Members.FirstOrDefault(row => row.WorkspaceId == room.Id && row.AccountId == teacher.Id);
            var invite = teacher.Email is null ? null : data.Invitations.FirstOrDefault(row => row.WorkspaceId == room.Id &&
                string.Equals(row.NormalizedEmail, teacher.Email, StringComparison.OrdinalIgnoreCase));
            var duties = Duties(data, room, teacher.Id);
            var assignments = data.Teaching.Where(row => row.WorkspaceId == room.Id && row.AccountId == teacher.Id).ToArray();
            if (member is null && invite is null && duties.Length == 0 && assignments.Length == 0) continue;
            AllocationTeacherDetails.Children.Add(AccessCard(room.Name + "（" + room.Code + "）", data, room, teacher, member, invite, duties, assignments));
        }
        if (AllocationTeacherDetails.Children.Count == 0)
        {
            var wasRevoked = _lastReceipt?.TermId == data.TermId && _lastReceipt?.Teachers.Any(row => row.Email == teacher.Email &&
                row.Access.Any(item => item.State == "REVOKED")) == true;
            AllocationTeacherDetails.Children.Add(Description(wasRevoked ? "本次已撤回指定邀请；当前无该邮箱的待认领访问。" :
                "当前范围未登记教学空间访问。"));
        }
    }

    private void RenderSpaceDirectory()
    {
        AllocationSpaceDetails.Children.Clear();
        if (_catalog is not { } data || AllocationDirectorySpaces.SelectedItem is not AllocationWorkspace room)
        {
            AllocationSpaceResultHeading.Text = "选择教学空间";
            return;
        }
        AllocationSpaceResultHeading.Text = room.ToString();
        var accounts = data.Accounts.ToDictionary(row => row.Id);
        var membership = data.Members.Where(row => row.WorkspaceId == room.Id).ToDictionary(row => row.AccountId);
        var assigned = data.Teaching.Where(row => row.WorkspaceId == room.Id).ToLookup(row => row.AccountId);
        var invited = data.Invitations.Where(row => row.WorkspaceId == room.Id).ToArray();
        var ids = membership.Keys.Concat(assigned.Select(group => group.Key)).Concat(data.Duties.Select(row => row.AccountId)
            .Where(id => Duties(data, room, id).Length > 0)).Distinct().ToArray();
        foreach (var id in ids)
        {
            var account = accounts.GetValueOrDefault(id);
            var teacher = account is null ? new DirectoryPerson(id, "其他身份", null, null, false, false) :
                new DirectoryPerson(id, account.Name, account.Username, account.Email, account.Disabled, account.Manageable);
            var invite = invited.FirstOrDefault(row => row.ClaimedByAccountId == id ||
                teacher.Email is not null && string.Equals(row.NormalizedEmail, teacher.Email, StringComparison.OrdinalIgnoreCase));
            AllocationSpaceDetails.Children.Add(AccessCard(teacher.ToString(), data, room, teacher,
                membership.GetValueOrDefault(id), invite, Duties(data, room, id), assigned[id].ToArray()));
        }
        foreach (var invite in invited.Where(row => row.ClaimedAt is null).Where(row =>
            !ids.Any(id => accounts.TryGetValue(id, out var account) && string.Equals(account.Email, row.NormalizedEmail, StringComparison.OrdinalIgnoreCase))))
            AllocationSpaceDetails.Children.Add(AccessCard(invite.NormalizedEmail, data, room,
                new(null, "邮箱邀请", null, invite.NormalizedEmail, false, true), null, invite, [], []));
        if (AllocationSpaceDetails.Children.Count == 0) AllocationSpaceDetails.Children.Add(Description("当前没有成员、职责访问或待认领邀请。"));
    }

    private static AllocationDuty[] Duties(TeacherAllocationCatalog data, AllocationWorkspace room, string? account) =>
        account is null ? [] : data.Duties.Where(row => row.AccountId == account && (row.Type == "GRADE" ? row.TargetId == room.GradeId :
            row.TargetId == room.Id || room.SourceClasses.Any(source => source.AdministrativeClassId == row.TargetId))).ToArray();

    private Border AccessCard(string title, TeacherAllocationCatalog data, AllocationWorkspace room, DirectoryPerson teacher,
        AllocationMember? member, AllocationInvitation? invite, AllocationDuty[] duties, AllocationTeaching[] assignments)
    {
        var content = new StackPanel { Spacing = 3 };
        content.Children.Add(new TextBlock {Text = title, Classes = { "label" }, TextWrapping = TextWrapping.Wrap});
        if (member is { } direct) content.Children.Add(Description("直接访问 · " + RoleLabel(direct.Role)));
        if (duties.Length > 0)
        {
            var role = duties.Any(row => row.Type == "GRADE" || row.TargetId == room.Id) ? "教师" : "只读";
            content.Children.Add(Description("职责访问 · " + role + " · " + string.Join("、", duties.Select(row => row.Title))));
        }
        if (assignments.Length > 0)
            content.Children.Add(Description("任课记录 · " + string.Join("、", assignments.Select(row =>
                row.Subject.Name + (row.Position == "CO_TEACHER" ? "协同" : "主讲")))));
        if (invite is { } item)
            content.Children.Add(Description(item.ClaimedAt is null ? (_archivedCorrection ? "同邮箱邀请待认领 · " : "邮箱待认领 · ") + RoleLabel(item.Role) :
                member is null ? "邮箱已认领 · 当前无直接成员权限" : "邮箱已认领"));
        var actions = new WrapPanel {ItemSpacing = 6, LineSpacing = 4};
        if (teacher.Manageable && !teacher.Disabled && member is { Role: not "OWNER" })
        {
            var remove = new Button {Content = "把移除加入教师表", Classes = { "action" }};
            remove.Click += (_, _) => AddDirectoryAction(teacher, new(room.Id, room.Code, "REMOVE", null));
            actions.Children.Add(remove);
        }
        else if (data.CanManageTargetedAccess && teacher.Id is { } account && member is { Role: "VIEWER" or "TEACHER" or "ASSISTANT" } &&
            data.Accounts.Any(row => row.Id == account && row.CanRemoveDirectAccess))
        {
            var remove = new Button {Content = "核对并移除直接访问", Classes = {"action"}};
            remove.Click += async (_, _) => await OpenTargetedAccessAsync(data, new("REMOVE", room.TermId, room.Id, AccountId: account), remove);
            actions.Children.Add(remove);
        }
        if (!_archivedCorrection && invite is {ClaimedAt: null, Role: not "OWNER"} pending && data.Mode == "OAUTH_EMAIL")
        {
            var revoke = new Button {Content = "把撤回加入教师表", Classes = { "action" }};
            revoke.Click += (_, _) => AddDirectoryAction(new(null, "邮箱邀请", null, pending.NormalizedEmail, false, true),
                new(room.Id, room.Code, "REVOKE", null, InvitationId: pending.Id));
            actions.Children.Add(revoke);
        }
        else if (!_archivedCorrection && data.CanManageTargetedAccess && invite is {ClaimedAt: null, Role: "VIEWER" or "TEACHER" or "ASSISTANT"} otherMode)
        {
            var revoke = new Button {Content = "核对并撤回这条邀请", Classes = {"action"}};
            Avalonia.Automation.AutomationProperties.SetAutomationId(revoke, "TargetedInvitation-" + otherMode.Id);
            revoke.Click += async (_, _) => await OpenTargetedAccessAsync(data, new("REVOKE", room.TermId, room.Id, InvitationId: otherMode.Id), revoke);
            actions.Children.Add(revoke);
        }
        if (_archivedCorrection && invite is {ClaimedAt: null} archived)
        {
            if (data.CanRevokeArchivedInvitations && archived.UpdatedAt is not null)
            {
                var revoke = new Button {Content = "撤回这条邀请", Classes = { "action" }};
                Avalonia.Automation.AutomationProperties.SetAutomationId(revoke, "RevokeArchivedInvitation-" + archived.Id);
                revoke.Click += async (_, _) => await ConfirmArchivedRevocationAsync(data, room, archived);
                actions.Children.Add(revoke);
            }
            else content.Children.Add(Description("此 KV 版本未提供归档邀请的核对式撤回。"));
        }
        if (teacher.Id is { } id && teacher.Manageable && !teacher.Disabled && Term is { } term &&
            (term.Status == "ACTIVE" || _archivedCorrection && term.Status == "ARCHIVED") &&
            (member is not null || duties.Length > 0 || assignments.Length > 0))
        {
            var teach = new Button {Content = _archivedCorrection ? "查看历史任课" : "继续分配任课", Classes = { "action" }};
            teach.Click += async (_, _) => await ContinueTeachingAsync(term, room, id, teach);
            actions.Children.Add(teach);
        }
        if (actions.Children.Count > 0) content.Children.Add(actions);
        return new Border {Classes = { "card" }, Padding = new Thickness(8), Child = content, Tag = invite?.Id, Focusable = invite is not null};
    }

    private async Task OpenTargetedAccessAsync(TeacherAllocationCatalog catalog, TeacherAccessTarget target, Control anchor)
    {
        if (_closed || _targetedAccess is not null || !ReferenceEquals(_catalog, catalog)) return;
        var window = new TeacherAccessWindow(_client, _school, target) { AccessLost = SessionExpired };
        _targetedAccess = window;
        try { await window.ShowDialog(HostWindow); }
        finally { _targetedAccess = null; }
        if (_closed || !ReferenceEquals(_catalog, catalog)) return;
        if (!window.Changed) { Render(); anchor.Focus(); return; }
        AccessChanged = true;
        await RunAsync(async () =>
        {
            await LoadAsync(catalog.TermId, catalog.GradeId);
            if (!_closed) Status(target.Action == "REVOKE" ? "已撤回原邀请。其他访问与邀请按原规则保留。" :
                "已移除原直接成员。任课和职责未改动。");
        });
    }

    private void UpdateAccessPendingActions()
    {
        if (AllocationAccessPendingActions is null || _closed || _client is null) return;
        var catalog = _catalog;
        var pending = catalog is {CanManageTargetedAccess: true, TermId: { } term} ?
            TeacherAccessWindow.PendingFor(_client, _school.Id, term) : [];
        var ids = pending.Select(row => row.Id).ToArray();
        AllocationAccessPendingActions.IsVisible = ids.Length > 0 && !_discard;
        AllocationAccessPendingActions.IsEnabled = !_busy;
        if (_visibleAccessRequests.SequenceEqual(ids)) return;
        _visibleAccessRequests = ids;
        AllocationAccessPendingActions.Children.Clear();
        foreach (var request in pending)
        {
            var button = new Button {Classes = {"action"}, Content = new TextBlock {TextWrapping = TextWrapping.Wrap,
                Text = $"核对本人原请求 · {request.Label} · {request.Id:D}"}};
            Avalonia.Automation.AutomationProperties.SetAutomationId(button, $"TeacherAccessPending-{request.Id:D}");
            button.Click += async (_, _) =>
            {
                if (_catalog is {CanManageTargetedAccess: true} current && current.TermId == request.Target.TermId)
                    await OpenTargetedAccessAsync(current, request.Target, button);
            };
            AllocationAccessPendingActions.Children.Add(button);
        }
    }

    private void AddDirectoryAction(DirectoryPerson person, AllocationAccess change)
    {
        if (!CanEdit || _archivedCorrection && (person.Id is null || change.Operation != "REMOVE")) return;
        var row = GetOrAdd(person.Id, person.Username, person.Email, person.Id is null ? null : person.Name);
        row.Access.RemoveAll(item => item.WorkspaceId == change.WorkspaceId || item.WorkspaceCode == change.WorkspaceCode);
        row.Access.Add(change);
        RebuildEditors();
        SetStage(Stage.Edit);
        AllocationTeachers.SelectedIndex = _teachers.IndexOf(row);
        RevealAllocationAccess();
        FocusAllocationField($"teachers[{AllocationTeachers.SelectedIndex}].access[{row.Access.Count - 1}]");
        Edited();
        Status("已加入草稿。请预览访问影响并确认保存。");
    }

    private static TextBlock Description(string text) => new() {Text = text, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap};
}
