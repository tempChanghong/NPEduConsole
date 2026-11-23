using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class TeacherAllocationView
{
    private sealed record ReceiptRow(AllocationSavedTeacher Teacher, AllocationSavedAccess? Access, string Label)
    {
        public override string ToString() => Label;
    }

    private bool _showDirectoryDetail;
    private Guid[] _displayedPending = [];

    internal void ObserveAllocation(TeacherAllocationView recovery)
    {
        if (_closed || !recovery.AccessChanged) return;
        AccessChanged = true;
        if (_fixedScope) AllocatedAccountId = recovery.AllocatedAccountId;
    }

    private void SetStage(Stage stage, bool showDirectoryDetail = false)
    {
        if (stage != Stage.Receipt) _deliveries.Clear();
        _stage = stage;
        _showDirectoryDetail = showDirectoryDetail;
        if (stage != Stage.Edit) _compactDetail = false;
        if (stage == Stage.Edit) RebuildAccessEditor();
        Render();
        ArrangeAllocationLayout(force: true);
    }

    private void EditClicked(object? sender, RoutedEventArgs args)
    {
        SetStage(Stage.Edit);
        RestoreTeacherFocus();
    }

    private void BackToReviewClicked(object? sender, RoutedEventArgs args)
    {
        if (_preview is null) return;
        SetStage(Stage.Review);
        FocusSelected(AllocationDifferences);
    }

    private void BackToReceiptClicked(object? sender, RoutedEventArgs args)
    {
        if (_lastReceipt is null) return;
        SetStage(Stage.Receipt);
        FocusSelected(AllocationReceiptRows);
    }

    private void ByTeacherClicked(object? sender, RoutedEventArgs args)
    {
        SetStage(Stage.ByTeacher);
        FocusLater(AllocationTeacherSearch);
    }

    private void BySpaceClicked(object? sender, RoutedEventArgs args)
    {
        SetStage(Stage.BySpace);
        FocusLater(AllocationSpaceSearch);
    }

    private void OpenTeacherDetailsClicked(object? sender, RoutedEventArgs args)
    {
        if (AllocationDirectoryTeachers.SelectedItem is null) return;
        SetStage(Stage.ByTeacher, showDirectoryDetail: true);
        FocusLater(AllocationBackFromTeacherDetails);
    }

    private void OpenSpaceDetailsClicked(object? sender, RoutedEventArgs args)
    {
        if (AllocationDirectorySpaces.SelectedItem is null) return;
        SetStage(Stage.BySpace, showDirectoryDetail: true);
        FocusLater(AllocationBackFromSpaceDetails);
    }

    private void DirectoryListClicked(object? sender, RoutedEventArgs args)
    {
        SetStage(_stage);
        FocusSelected(_stage == Stage.ByTeacher ? AllocationDirectoryTeachers : AllocationDirectorySpaces);
    }

    private void FocusSelected(ListBox list)
    {
        if (list.SelectedItem is { } selected) list.ScrollIntoView(selected);
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed || !list.IsEffectivelyVisible || !list.IsEffectivelyEnabled) return;
            var target = list.SelectedItem is { } row ? list.ContainerFromItem(row) ?? list : list;
            target.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private void FocusLater(Control control) => Dispatcher.UIThread.Post(() =>
    {
        if (_closed || !control.IsEffectivelyVisible || !control.IsEffectivelyEnabled) return;
        control.BringIntoView();
        control.Focus(NavigationMethod.Tab);
    }, DispatcherPriority.Loaded);

    private void ShowReceiptRows(TeacherAllocationReceipt receipt)
    {
        var prior = AllocationReceiptRows.SelectedItem as ReceiptRow;
        var spaces = _catalog?.TermId == receipt.TermId ? _catalog?.Workspaces.ToDictionary(row => row.Id) : null;
        var rows = receipt.Teachers.SelectMany(teacher =>
        {
            var name = $"{teacher.Name}（{teacher.Username ?? teacher.Email ?? teacher.AccountId}）";
            if (teacher.Access.Length == 0) return [new ReceiptRow(teacher, null, $"{name} · {(teacher.Created ? "已新建" : "复用账号")}")];
            return teacher.Access.Select(access =>
            {
                var space = spaces?.GetValueOrDefault(access.WorkspaceId)?.ToString() ?? access.WorkspaceId;
                var action = access.State switch
                {
                    "REVOKED" => "已撤回邀请", "REMOVED" => "已移除直接访问", "INVITED" => "邮箱待认领",
                    _ => RoleLabel(access.Role)
                };
                return new ReceiptRow(teacher, access, $"{name} · {space} · {action}");
            });
        }).ToArray();
        AllocationReceiptRows.ItemsSource = rows;
        AllocationReceiptRows.SelectedItem = rows.FirstOrDefault(row => row.Teacher == prior?.Teacher && row.Access == prior?.Access)
            ?? rows.FirstOrDefault();
        AllocationReceiptSummary.Text = $"本批 {receipt.Teachers.Length} 位教师 · {receipt.Teachers.Sum(row => row.Access.Length)} 条访问";
        RenderReceiptActions();
    }

    private void ReceiptRowSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (!_closed && _client is not null) RenderReceiptActions();
    }

    private void RenderReceiptActions()
    {
        var selected = AllocationReceiptRows.SelectedItem as ReceiptRow;
        RenderPinDelivery(selected?.Teacher);
        var directory = _lastReceipt is { } receipt && _catalog is { } data &&
            data.TermId == receipt.TermId && data.GradeId == receipt.GradeId;
        var room = selected?.Access is { } access && directory
            ? _catalog!.Workspaces.FirstOrDefault(row => row.Id == access.WorkspaceId) : null;
        AllocationReceiptPerson.Content = selected?.Teacher.AccountId is null && selected?.Access?.InvitationId is not null
            ? "查看这条邮箱邀请" : "查看该教师";
        AllocationReceiptPerson.IsEnabled = directory && !_busy &&
            (selected?.Teacher.AccountId is not null || selected?.Access?.InvitationId is not null);
        AllocationReceiptSpace.IsEnabled = room is not null && !_busy;
        AllocationReceiptTeaching.IsVisible = OpenTeaching is not null && !_archivedCorrection && Term?.Status == "ACTIVE";
        AllocationReceiptTeaching.IsEnabled = !_busy && selected?.Teacher.AccountId is not null && room is not null &&
            selected.Access?.Role is not null && selected.Access.State is not ("REMOVED" or "REVOKED");
    }

    private void ReceiptPersonClicked(object? sender, RoutedEventArgs args)
    {
        if (AllocationReceiptRows.SelectedItem is not ReceiptRow row) return;
        if (row.Teacher.AccountId is not { } accountId)
        {
            if (row.Access?.InvitationId is { } invitationId) FocusInvitation(invitationId);
            return;
        }
        AllocationTeacherSearch.Clear();
        RebuildDirectories();
        AllocationDirectoryTeachers.SelectedItem = AllocationDirectoryTeachers.Items.OfType<DirectoryPerson>()
            .FirstOrDefault(person => person.Id == accountId);
        if (AllocationDirectoryTeachers.SelectedItem is null)
        {
            Status("这位教师已不在当前目录，请刷新后核对。");
            return;
        }
        OpenTeacherDetailsClicked(sender, args);
    }

    private void ReceiptSpaceClicked(object? sender, RoutedEventArgs args)
    {
        if (AllocationReceiptRows.SelectedItem is not ReceiptRow { Access: { } access }) return;
        AllocationSpaceSearch.Clear();
        RebuildDirectories();
        AllocationDirectorySpaces.SelectedItem = AllocationDirectorySpaces.Items.OfType<AllocationWorkspace>()
            .FirstOrDefault(space => space.Id == access.WorkspaceId);
        if (AllocationDirectorySpaces.SelectedItem is null)
        {
            Status("这个教学空间已不在当前目录，请刷新后核对。");
            return;
        }
        OpenSpaceDetailsClicked(sender, args);
    }

    private async void ReceiptTeachingClicked(object? sender, RoutedEventArgs args)
    {
        if (Term is not { } term || AllocationReceiptRows.SelectedItem is not ReceiptRow { Access: { } access } row ||
            row.Teacher.AccountId is not { } account || _catalog?.Workspaces.FirstOrDefault(space => space.Id == access.WorkspaceId) is not { } room)
            return;
        await ContinueTeachingAsync(term, room, account, AllocationReceiptTeaching);
    }

    private async Task ContinueTeachingAsync(OrganizationTerm term, AllocationWorkspace room, string accountId, Control origin)
    {
        if (_busy || _dirty || Pending is not null || _rescope)
        {
            Status("请先保存或核对教师表中的访问变更。");
            return;
        }

        if (OpenTeaching is not { } open)
        {
            TeachingTarget = (term.Id, room.Id, room.SubjectId, accountId, room.IsGroup);
            CloseRequested?.Invoke();
            return;
        }

        await RunAsync(async () =>
        {
            var target = await _client.GetTeacherAllocationAsync(_school.Id, term.Id, _catalog?.GradeId, false,
                _lifetime.Token, _archivedCorrection);
            if (_closed) return;
            if (target.TermId != term.Id || !target.Terms.Any(item => item.Id == term.Id && item.Status == term.Status) ||
                !target.Workspaces.Any(item => item.Id == room.Id && item.SubjectId == room.SubjectId) ||
                !target.Accounts.Any(item => item.Id == accountId && item.Manageable && !item.Disabled))
            {
                Status("这位教师或这个教学空间已变化，请刷新当前范围再办理任课。");
                return;
            }

            TeachingTarget = (term.Id, room.Id, room.SubjectId, accountId, room.IsGroup);
            _deliveries.Clear();
            try { await open(); }
            finally { TeachingTarget = null; }
        });
        FocusLater(origin);
    }

    private void UpdatePendingActions()
    {
        if (AllocationPendingActions is null || _client is null || _closed) return;
        var available = Pending is null && RecoveryId is null && _catalog is not null &&
            Requests.TryGetValue(_client, out var requests)
            ? requests.Values.Where(request => request.SchoolId == _school.Id &&
                request.Draft.ArchivedCorrection == _archivedCorrection &&
                (!_fixedScope || MatchesTarget(request, _catalog.TermId, _catalog.GradeId, _archivedCorrection))).ToArray() : [];
        var ids = available.Select(row => row.Id).ToArray();
        AllocationPendingActions.IsVisible = ids.Length > 0 && !_discard;
        AllocationPendingActions.IsEnabled = !_busy;
        if (_displayedPending.SequenceEqual(ids)) return;
        _displayedPending = ids;
        AllocationPendingActions.Children.Clear();
        foreach (var request in available)
        {
            var button = new Button { Content = $"核对原分配 · {request.ScopeLabel}", Classes = { "action" } };
            AutomationProperties.SetAutomationId(button, $"AllocationResumePending-{request.Id:D}");
            button.Click += async (_, _) =>
            {
                if (_closed || _busy || OpenPendingRequest is not { } open) return;
                await open(request.Id, request.Draft.TermId, request.Draft.GradeId, request.Draft.ArchivedCorrection);
                if (_closed) return;
                Render();
                FocusLater(AllocationPendingActions.Children.Contains(button) ? button : AllocationClose);
            };
            AllocationPendingActions.Children.Add(button);
        }
    }
}
