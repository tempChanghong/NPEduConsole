using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class StaffReviewWindow : Window
{
    private sealed record Filter(string? Id, string Label) { public override string ToString() => Label; }
    private sealed record Destination(string Id, string Name, bool Walking, GradePlanLabel[] Subjects)
    {
        public override string ToString() => Name;
    }

    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly CancellationTokenSource _lifetime = new();
    private StaffPolicySnapshot? _snapshot;
    private StaffReviewIssue[] _visible = [];
    private Window? _child;
    private string? _initialTerm, _initialKey, _initialGrade, _initialClass;
    private string? _archiveEditScope;
    private bool _busy, _closed, _binding, _compact, _showDetail;
    private CancellationTokenSource? _choices;

    public StaffReviewWindow() => InitializeComponent();

    internal StaffReviewWindow(ConsoleClient client, School school, string? term = null, string? issue = null,
        string? grade = null, string? classroom = null) : this()
    {
        _client = client;
        _school = school;
        _initialTerm = term;
        _initialKey = issue;
        _initialGrade = grade;
        _initialClass = classroom;
        ReviewScope.Text = school.Name;
        ReviewKind.ItemsSource = new Filter[] { new(null, "全部提醒"), new("TEACHING", "待核对任课"), new("STAFF", "待核对岗位"), new("ERROR", "账号停用等问题") };
        ReviewKind.SelectedIndex = 0;
        ReviewIssues.SelectionChanged += (_, _) => { if (!_binding) SelectIssue(); };
        ReviewIssues.OpenRequested += (_, _) => OpenIssue();
        SizeChanged += (_, _) => LayoutPage();
        Opened += async (_, _) =>
        {
            BatchWorkspaceWindow.FitSchoolWindow(this);
            LayoutPage();
            await RunAsync(() => ReadAsync(_initialTerm, resetFilters: true, focus: true));
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _choices?.Cancel();
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
        RenderControls();
    }

    internal Action<ConsoleApiException>? SessionExpired { get; init; }
    internal bool WorkspaceChanged { get; private set; }

    internal void CloseForScopeChange()
    {
        switch (_child)
        {
            case StaffPolicyWindow policy: policy.CloseForScopeChange(); break;
            case GradeTeachingPlanWindow teaching: teaching.CloseForScopeChange(); break;
            case StaffResponsibilitiesWindow roles: roles.CloseForScopeChange(); break;
        }
        Close();
    }

    private StaffReviewIssue? Selected => _visible.FirstOrDefault(row => row.Key == ReviewIssues.SelectedKey);
    private bool Restricted => _busy || _child is not null;
    private bool CanEditRoles => _snapshot is { } state && (state.Term?.Status != "ARCHIVED" || _archiveEditScope == state.ScopeVersion);

    private void LayoutPage()
    {
        if (_client is null || _closed) return;
        _compact = ClientSize.Width < 900;
        bool detailOnly = _compact && _showDetail;
        ReviewIssues.IsVisible = !detailOnly;
        ReviewDetail.IsVisible = !_compact || detailOnly;
        ReviewFilters.IsVisible = !detailOnly;
        ReviewBackList.IsVisible = detailOnly;
        ReviewOpenDetail.IsVisible = _compact && !detailOnly;
        Grid.SetColumn(ReviewDetail, _compact ? 0 : 1);
        ReviewBody.ColumnDefinitions[0].Width = new GridLength(_compact ? 1 : 3, GridUnitType.Star);
        ReviewBody.ColumnDefinitions[1].Width = new GridLength(_compact ? 0 : 2, GridUnitType.Star);
        ReviewBody.ColumnSpacing = _compact ? 0 : 10;
    }

    private async Task ReadAsync(string? termId, bool resetFilters = false, bool focus = false, bool afterAction = false)
    {
        var oldKey = _initialKey ?? ReviewIssues.SelectedKey;
        int oldIndex = Array.FindIndex(_visible, item => item.Key == oldKey);
        var grade = _initialGrade ?? (resetFilters ? null : (ReviewGrade.SelectedItem as Filter)?.Id);
        var snapshot = await _client.GetStaffPolicyAsync(_school.Id, termId, _lifetime.Token);
        if (_closed) return;
        _snapshot = snapshot;
        string term = snapshot.Term?.ToString() ?? (snapshot.Terms.Count == 0 ? "本校尚未建立学期" : "本校暂无启用学期");
        ReviewScope.Text = $"{_school.Name} / {term}";
        _binding = true;
        ReviewTerm.ItemsSource = snapshot.Terms;
        ReviewTerm.SelectedItem = snapshot.Term;
        var grades = snapshot.Issues.Where(row => row.GradeId is not null)
            .DistinctBy(row => row.GradeId).Select(row => new Filter(row.GradeId, row.GradeName)).ToList();
        if (grade is not null && !grades.Any(row => row.Id == grade) && ReviewGrade.SelectedItem is Filter retained && retained.Id == grade)
            grades.Add(retained);
        grades.Insert(0, new(null, "全部年级"));
        ReviewGrade.ItemsSource = grades;
        ReviewGrade.SelectedItem = grades.FirstOrDefault(row => row.Id == grade) ?? grades[0];
        if (resetFilters) ReviewKind.SelectedIndex = 0;
        _binding = false;
        string? target = _initialKey ?? (_initialClass is { } room ? snapshot.Issues.FirstOrDefault(row => row.ClassId == room)?.Key : oldKey);
        _initialKey = _initialGrade = _initialClass = null;
        _showDetail = _compact && target is not null && snapshot.Issues.Any(row => row.Key == target) && !afterAction && resetFilters;
        ShowIssues(target, oldIndex);
        if (afterAction && oldKey is not null && !snapshot.Issues.Any(row => row.Key == oldKey))
            ReviewStatus.Text = "已按服务器重新核对，原提醒已消除。" + ReviewStatus.Text;
        else if (resetFilters && oldKey is not null && !snapshot.Issues.Any(row => row.Key == oldKey))
            ReviewStatus.Text = "原待办已变化，已显示最新师资结果。" + ReviewStatus.Text;
        LayoutPage();
        if (focus) RestoreFocus();
    }

    private void ShowIssues(string? preferred, int previousIndex = -1)
    {
        var grade = (ReviewGrade.SelectedItem as Filter)?.Id;
        var kind = (ReviewKind.SelectedItem as Filter)?.Id;
        _visible = _snapshot?.Issues.Where(row => (grade is null || row.GradeId == grade) && (kind switch
        {
            "TEACHING" => row.Action == "TEACHING",
            "STAFF" => row.Action != "TEACHING",
            "ERROR" => row.Severity == "ERROR",
            _ => true
        })).ToArray() ?? [];
        string? selection = _visible.FirstOrDefault(row => row.Key == preferred)?.Key;
        if (selection is null && _visible.Length > 0 && previousIndex >= 0)
            selection = _visible[Math.Min(previousIndex, _visible.Length - 1)].Key;
        _binding = true;
        ReviewIssues.SetRows(_visible.Select(row => new StaffReviewLine(row.Key,
            row.ClassName is null ? row.GradeName : $"{row.GradeName} / {row.ClassName}",
            row.TeacherLabel is { } identity ? $"{row.Message}（{identity}）" : row.Message,
            row.Severity == "ERROR" ? "需处理" : "待核对")).ToArray(), selection);
        _binding = false;
        ReviewStatus.Text = _snapshot?.Terms.Count == 0 ? "本校尚无学期可核对。全校规则可以直接编辑。"
            : _snapshot?.Term is null ? "选择已有学期可查看师资提醒。全校规则可以直接编辑。"
            : _visible.Length == 0 ? "此范围暂无岗位或任课提醒。" : $"{_visible.Length} 项提醒";
        SelectIssue();
    }

    private void SelectIssue()
    {
        _choices?.Cancel();
        _choices = null;
        ReviewTeachingLocation.IsVisible = false;
        ReviewTeachingRoom.ItemsSource = ReviewTeachingSubject.ItemsSource = null;
        var row = Selected;
        ReviewDetailTitle.Text = row?.ClassName ?? row?.GradeName ?? "没有选中的提醒";
        ReviewDetailTeacher.Text = row?.TeacherName is { } teacher ? $"{teacher} · {row.TeacherLabel}" : null;
        ReviewDetailReason.Text = row?.Message;
        ReviewResolve.Content = row?.Action switch
        {
            "TEACHING" => "核对任课",
            "CLASS_STAFF" => "打开班主任安排",
            _ => "打开年级组长安排"
        };
        RenderControls();
    }

    private void RenderControls()
    {
        if (_client is null || _closed) return;
        ReviewRules.IsEnabled = !Restricted && _snapshot is not null;
        ReviewTerm.IsEnabled = !Restricted && _snapshot?.Terms.Count > 0;
        ReviewGrade.IsEnabled = ReviewKind.IsEnabled = !Restricted && _snapshot?.Term is not null;
        ReviewRefresh.IsEnabled = !Restricted;
        var selected = Selected;
        bool teaching = selected?.Action == "TEACHING";
        ReviewResolve.IsVisible = selected is not null && !ReviewTeachingLocation.IsVisible && (teaching || CanEditRoles);
        ReviewHandoff.IsVisible = teaching && CanEditRoles;
        ReviewEditArchive.IsVisible = selected is not null && !CanEditRoles && _snapshot?.Term?.Status == "ARCHIVED";
        ReviewOpenDetail.IsEnabled = ReviewResolve.IsEnabled = ReviewHandoff.IsEnabled = ReviewEditArchive.IsEnabled = !Restricted && selected is not null;
        ReviewOpenTeaching.IsEnabled = !Restricted && ReviewTeachingRoom.SelectedItem is Destination &&
            ReviewTeachingSubject.SelectedItem is GradePlanLabel;
    }

    private void RestoreFocus()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed) return;
            if (_compact && _showDetail && ReviewResolve.IsEffectivelyVisible) ReviewResolve.Focus();
            else if (_compact && _showDetail && ReviewEditArchive.IsEffectivelyVisible) ReviewEditArchive.Focus();
            else if (Selected is not null) ReviewIssues.FocusSelected();
            else ReviewRefresh.Focus();
        }, DispatcherPriority.Loaded);
    }

    private async void TermChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_binding || Restricted || ReviewTerm.SelectedItem is not StaffReviewTerm term || term.Id == _snapshot?.Term?.Id) return;
        _snapshot = null;
        _archiveEditScope = null;
        _showDetail = false;
        ShowIssues(null);
        ReviewScope.Text = $"{_school.Name} / {term}";
        await RunAsync(() => ReadAsync(term.Id, resetFilters: true));
    }

    private void FilterChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!_binding && !Restricted && _snapshot is not null) ShowIssues(ReviewIssues.SelectedKey);
    }

    private void OpenDetailClicked(object? sender, RoutedEventArgs args) => OpenIssue();
    private void BackToListClicked(object? sender, RoutedEventArgs args) { _showDetail = false; LayoutPage(); RestoreFocus(); }
    private void OpenIssue()
    {
        if (Restricted || Selected is null) return;
        if (_compact) { _showDetail = true; LayoutPage(); RestoreFocus(); }
        else if (ReviewResolve.IsEffectivelyVisible) ReviewResolve.Focus();
        else ReviewEditArchive.Focus();
    }

    private async void ResolveClicked(object? sender, RoutedEventArgs args)
    {
        if (Restricted || _snapshot?.Term is not { } term || Selected is not { } issue) return;
        if (issue.Action == "TEACHING" && issue.GradeId is { } grade)
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _choices = request;
            ReviewTeachingLocation.IsVisible = true;
            ReviewTeachingNote.Text = "正在读取本年级可分配的教学空间…";
            RenderControls();
            try
            {
                var catalog = await _client.GetGradePlanAsync(_school.Id, term.Id, grade, null, request.Token, specifiedTerm: true);
                if (_closed || _choices != request || Selected?.Key != issue.Key) return;
                var classes = catalog.Classes.Select(room => new Destination(room.Id, $"{room.Name}（{room.Code}）", false,
                    catalog.Subjects.Where(subject => room.Rules.Any(rule => rule.SubjectId == subject.Id && rule.Mode == "ADMIN_CLASS")).ToArray()));
                var assigned = catalog.Groups.Where(group => group.IsActive).Select(group => new Destination(group.Id, group.ToString(), true,
                    catalog.Subjects.Where(subject => subject.Id == group.SubjectId).ToArray()));
                var locations = classes.Concat(assigned).Where(row => row.Subjects.Length > 0).ToArray();
                ReviewTeachingRoom.ItemsSource = locations;
                ReviewTeachingNote.Text = locations.Length == 0 ? "本年级暂无可分配的科目。可先在所选学期的教学安排中设置授课方式。"
                    : "明确选择该教师实际任教的教学空间和科目。本年级的其他班也可满足要求。";
                ReviewTeachingRoom.Focus();
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (ConsoleApiException error) { if (_choices == request && !_closed) HandleError(error); }
            finally { if (_choices == request) _choices = null; }
            return;
        }
        await OpenRolesAsync(term, issue);
    }

    private async void HandoffClicked(object? sender, RoutedEventArgs args)
    {
        if (CanEditRoles && _snapshot?.Term is { } term && Selected is { Action: "TEACHING" } issue)
            await OpenRolesAsync(term, issue);
    }

    private void EditArchiveClicked(object? sender, RoutedEventArgs args)
    {
        if (Restricted || _snapshot is not { Term.Status: "ARCHIVED" } state) return;
        _archiveEditScope = state.ScopeVersion;
        ReviewStatus.Text = "正在更正归档学期的岗位。请核对所选教师和安排。";
        RenderControls();
        if (ReviewHandoff.IsEffectivelyVisible) ReviewHandoff.Focus();
        else ReviewResolve.Focus();
    }

    private Task OpenRolesAsync(StaffReviewTerm term, StaffReviewIssue issue)
    {
        if (!CanEditRoles) return Task.CompletedTask;
        string? reviewVersion = _snapshot?.ScopeVersion;
        return RunAsync(async () =>
        {
            if (term.Status == "ARCHIVED")
            {
                var latest = await _client.GetStaffPolicyAsync(_school.Id, term.Id, _lifetime.Token);
                if (_closed || latest.ScopeVersion != reviewVersion)
                {
                    if (!_closed) await ReadAsync(term.Id, focus: true);
                    return;
                }
            }
            var scope = await _client.GetGradePlanAsync(_school.Id, term.Id, null, null, _lifetime.Token, specifiedTerm: true);
            if (_closed || scope.Term?.Status != term.Status) { if (!_closed) await ReadAsync(term.Id, focus: true); return; }
            string? target = issue.ClassId ?? issue.GradeId;
            if (target is null) return;
            var window = new StaffResponsibilitiesWindow(_client, _school, term.Id, issue.ClassId is null ? "grades" : "classes",
                target, issue.AccountId, scope.Term, scope.ActiveTerm, scope.ScopeVersion) { SessionExpired = SessionExpired };
            await ShowChild(window);
            if (_closed) return;
            WorkspaceChanged |= window.RolesChanged;
            await ReadAsync(term.Id, focus: true, afterAction: window.RolesChanged);
        });
    }

    private void TeachingRoomChanged(object? sender, SelectionChangedEventArgs args)
    {
        ReviewTeachingSubject.ItemsSource = (ReviewTeachingRoom.SelectedItem as Destination)?.Subjects;
        ReviewTeachingSubject.SelectedIndex = -1;
        RenderControls();
    }

    private void TeachingSubjectChanged(object? sender, SelectionChangedEventArgs args) => RenderControls();

    private async void OpenTeachingClicked(object? sender, RoutedEventArgs args)
    {
        if (Restricted || _snapshot?.Term is not { } term || Selected is not { GradeId: { } grade, AccountId: { } teacher } ||
            ReviewTeachingRoom.SelectedItem is not Destination room || ReviewTeachingSubject.SelectedItem is not GradePlanLabel subject) return;
        await RunAsync(async () =>
        {
            var window = new GradeTeachingPlanWindow(_client, _school, grade, subject.Id, room.Walking ? room.Id : null, term.Id,
                room.Walking ? null : room.Id, teacher) { SessionExpired = SessionExpired };
            await ShowChild(window);
            if (_closed) return;
            WorkspaceChanged |= window.WorkspaceChanged;
            await ReadAsync(term.Id, focus: true, afterAction: window.WorkspaceChanged);
        });
    }

    private async void OpenRulesClicked(object? sender, RoutedEventArgs args)
    {
        if (Restricted || _snapshot is not { } snapshot) return;
        await RunAsync(async () =>
        {
            var window = new StaffPolicyWindow(_client, _school, snapshot) { SessionExpired = SessionExpired };
            await ShowChild(window);
            if (_closed) return;
            WorkspaceChanged |= window.PolicyChanged;
            await ReadAsync(snapshot.Term?.Id, focus: true, afterAction: window.PolicyChanged);
        });
    }

    private async Task ShowChild(Window window)
    {
        _child = window;
        try { await window.ShowDialog(this); }
        finally { _child = null; }
    }

    private async Task RunAsync(Func<Task> work)
    {
        if (Restricted || _closed) return;
        _busy = true;
        RenderControls();
        try { await work(); }
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error) { if (!_closed) HandleError(error); }
        finally { _busy = false; RenderControls(); }
    }

    private void HandleError(ConsoleApiException error)
    {
        if (error.StatusCode is 401 or 403)
        {
            _snapshot = null;
            ShowIssues(null);
            _binding = true;
            ReviewTerm.ItemsSource = ReviewGrade.ItemsSource = null;
            _binding = false;
            ReviewScope.Text = _school.Name;
            if (error.StatusCode == 401) SessionExpired?.Invoke(error);
        }
        if (!_closed) ReviewStatus.Text = error.Message;
    }

    private async void RefreshClicked(object? sender, RoutedEventArgs args) =>
        await RunAsync(() => ReadAsync(_snapshot?.Term?.Id ?? (ReviewTerm.SelectedItem as StaffReviewTerm)?.Id, focus: true));
    private void CloseClicked(object? sender, RoutedEventArgs args) => CloseForScopeChange();
}
