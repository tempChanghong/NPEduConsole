using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class TeacherAllocationWindow
{
    private sealed record DirectoryPerson(string? Id, string Name, string? Username, string? Email, bool Disabled, bool Manageable)
    {
        public override string ToString() => $"{Name}（{Username ?? Email ?? Id}）{(Disabled ? " · 已停用" : "")}";
    }
    private TeacherAllocationReceipt? _lastReceipt;

    private void DirectorySearchChanged(object? sender, TextChangedEventArgs args)
    {
        if (_client is not null) RebuildDirectories();
    }

    private void RebuildDirectories()
    {
        if (_catalog is not { } data) return;
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
        var teacherQuery = AllocationTeacherSearch.Text?.Trim() ?? "";
        AllocationDirectoryTeachers.ItemsSource = people.Where(row => row.ToString().Contains(teacherQuery, StringComparison.OrdinalIgnoreCase)).ToArray();
        AllocationDirectoryTeachers.SelectedItem = AllocationDirectoryTeachers.Items.OfType<DirectoryPerson>().FirstOrDefault(row =>
            picked?.Id is not null ? row.Id == picked.Id : picked is not null && row.Email == picked.Email)
            ?? AllocationDirectoryTeachers.Items.OfType<DirectoryPerson>().FirstOrDefault();

        var spaceQuery = AllocationSpaceSearch.Text?.Trim() ?? "";
        AllocationDirectorySpaces.ItemsSource = data.Workspaces.Where(row => row.ToString().Contains(spaceQuery, StringComparison.OrdinalIgnoreCase)).ToArray();
        AllocationDirectorySpaces.SelectedItem = AllocationDirectorySpaces.Items.OfType<AllocationWorkspace>().FirstOrDefault(row => row.Id == board?.Id)
            ?? AllocationDirectorySpaces.Items.OfType<AllocationWorkspace>().FirstOrDefault();
        RenderTeacherDirectory();
        RenderSpaceDirectory();
    }

    private void DirectoryPersonChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (ReferenceEquals(args.Source, AllocationDirectoryTeachers)) RenderTeacherDirectory();
    }

    private void DirectorySpaceChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (ReferenceEquals(args.Source, AllocationDirectorySpaces)) RenderSpaceDirectory();
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
            content.Children.Add(Description(item.ClaimedAt is null ? "邮箱待认领 · " + RoleLabel(item.Role) :
                member is null ? "邮箱已认领 · 当前无直接成员权限" : "邮箱已认领"));
        var actions = new WrapPanel {ItemSpacing = 6, LineSpacing = 4};
        if (teacher.Manageable && !teacher.Disabled && member is { Role: not "OWNER" })
        {
            var remove = new Button {Content = "把移除加入教师表", Classes = { "action" }};
            remove.Click += (_, _) => AddDirectoryAction(teacher, new(room.Id, room.Code, "REMOVE", null));
            actions.Children.Add(remove);
        }
        if (invite is {ClaimedAt: null, Role: not "OWNER"} pending && data.Mode == "OAUTH_EMAIL")
        {
            var revoke = new Button {Content = "把撤回加入教师表", Classes = { "action" }};
            revoke.Click += (_, _) => AddDirectoryAction(new(null, "邮箱邀请", null, pending.NormalizedEmail, false, true),
                new(room.Id, room.Code, "REVOKE", null, InvitationId: pending.Id));
            actions.Children.Add(revoke);
        }
        if (teacher.Id is { } id && teacher.Manageable && !teacher.Disabled && Term is { Status: "ACTIVE" } term &&
            (member is not null || duties.Length > 0 || assignments.Length > 0))
        {
            var teach = new Button {Content = "继续分配任课", Classes = { "action" }};
            teach.Click += (_, _) =>
            {
                if (_busy || _dirty || Pending is not null)
                {
                    Status("请先保存或核对教师表中的访问变更。");
                    return;
                }
                TeachingTarget = (term.Id, room.Id, room.SubjectId, id, room.IsGroup);
                _allowClose = true;
                Close();
            };
            actions.Children.Add(teach);
        }
        if (actions.Children.Count > 0) content.Children.Add(actions);
        return new Border {Classes = { "card" }, Padding = new Thickness(8), Child = content};
    }

    private void AddDirectoryAction(DirectoryPerson person, AllocationAccess change)
    {
        if (!CanEdit) return;
        var row = GetOrAdd(person.Id, person.Username, person.Email, person.Id is null ? null : person.Name);
        row.Access.RemoveAll(item => item.WorkspaceId == change.WorkspaceId || item.WorkspaceCode == change.WorkspaceCode);
        row.Access.Add(change);
        RebuildEditors();
        AllocationTabs.SelectedIndex = 0;
        AllocationTeachers.SelectedIndex = _teachers.IndexOf(row);
        RevealAllocationAccess();
        FocusAllocationField($"teachers[{AllocationTeachers.SelectedIndex}].access[{row.Access.Count - 1}]");
        Edited();
        Status("已加入草稿。请预览访问影响并确认保存。");
    }

    private static TextBlock Description(string text) => new() {Text = text, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap};
}
