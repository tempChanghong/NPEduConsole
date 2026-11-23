using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed class StaffEditorRow(StaffTeacher teacher, string primary, string secondary, string[] labels, int selection, Action changed,
    string area) : INotifyPropertyChanged
{
    private int _selection = selection;
    public StaffTeacher Teacher { get; } = teacher;
    public string Identity => Teacher.AccountLabel + (Teacher.LocalDisabled ? " · 已停用" : "");
    public IReadOnlyList<string> Positions { get; } = labels;
    public string PositionLabel => Positions[Selection];
    public string RoleSelectorLabel => $"{Teacher.DisplayName}（{Teacher.AccountLabel}）的职责";
    public string AutomationId => $"Staff{area}-{Teacher.Id}";
    public string? Position => Selection switch { 0 => primary, 1 => secondary, _ => null };
    public int Selection
    {
        get => _selection;
        set
        {
            if (value is < 0 or > 2 || _selection == value) return;
            _selection = value;
            PropertyChanged?.Invoke(this, new(nameof(Selection)));
            PropertyChanged?.Invoke(this, new(nameof(PositionLabel)));
            changed();
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class StaffResponsibilitiesWindow : Window
{
    private sealed record PendingStaff(StaffSnapshot Baseline, IReadOnlyList<StaffRole> Submitted);
    private static readonly ConditionalWeakTable<ConsoleClient,
        Dictionary<(string School, string Term, string Kind, string Id, bool Specified), PendingStaff>> PendingBySession = new();

    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly string _termId = "", _kind = "", _targetId = "";
    private readonly string? _accountId;
    private readonly TermStructureIdentity? _term;
    private readonly string? _openedScope;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<StaffEditorRow> _draft = [];
    private StaffSnapshot? _baseline, _observed;
    private StaffPreview? _preview;
    private TeacherAccountForm? _teacherForm;
    private string? _newTeacherId;
    private bool _busy, _writing, _closed, _allowClose, _scopeLost, _compare;

    public StaffResponsibilitiesWindow() => InitializeComponent();

    internal StaffResponsibilitiesWindow(ConsoleClient client, School school, string termId, string kind, string targetId,
        string? accountId, TermStructureIdentity? term = null, TermStructureIdentity? active = null, string? scope = null,
        string? currentTermName = null) : this()
    {
        _client = client;
        _school = school;
        _termId = termId;
        _kind = kind;
        _targetId = targetId;
        _accountId = accountId;
        _term = term;
        _openedScope = scope;
        StaffScope.Text = term is null ? $"{school.Name} · {currentTermName ?? "当前学期"}"
            : $"{school.Name} / {term.Name} · {term.StatusLabel}　全校启用 {active?.Name ?? "暂无"}";
        StaffHeading.Text = kind == "classes" ? "班主任" : "年级组长";
        StaffAccessNote.Text = kind == "classes"
            ? "班主任与协同班主任均有本班管理权限，并可查看相关走班。调整职责只改变这一路访问，任课和班级成员权限各自保留。"
            : "年级组长与副组长均管理本年级教学空间。调整职责只改变这一路访问，其他年级、任课和班级成员权限各自保留。";
        SizeChanged += (_, _) => LayoutCards();
        Opened += async (_, _) =>
        {
            BatchWorkspaceWindow.FitSchoolWindow(this);
            await RunAsync(LoadAsync);
        };
        Closing += (_, args) =>
        {
            args.Cancel = !_allowClose && (_writing || _pending is null && HasChanges && !_scopeLost);
            if (args.Cancel && !_writing)
            {
                StaffStatus.Text = "仍有未保存的职责草稿。可继续核对，或放弃草稿后返回。";
                StaffDiscard.IsVisible = true;
            }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
        Render();
    }

    internal Action<ConsoleApiException>? SessionExpired { get; init; }
    internal bool RolesChanged { get; private set; }
    private (string, string, string, string, bool) Key => (_school.Id, _termId, _kind, _targetId, _term is not null);
    private PendingStaff? _pending => PendingBySession.GetOrCreateValue(_client).GetValueOrDefault(Key);
    private string[] Labels => _kind == "classes" ? ["班主任", "协同班主任", "卸任"] : ["年级组长", "副组长", "卸任"];
    private bool HasChanges => _baseline is { } snapshot && !ConsoleClient.SameStaffRoles(snapshot.Assignments, Desired());
    private StaffRole[] Desired() => _draft.Where(row => row.Position is not null).Select(row => new StaffRole(row.Teacher.Id, row.Position!)).ToArray();

    private void LayoutCards()
    {
        bool stacked = ClientSize.Width < 930;
        int columns = stacked ? 1 : 2;
        if (StaffComparison.ColumnDefinitions.Count != columns)
        {
            StaffComparison.ColumnDefinitions = new ColumnDefinitions(stacked ? "*" : "*,1.15*");
            StaffComparison.RowDefinitions = new RowDefinitions(stacked ? "Auto,Auto" : "Auto");
        }
        Grid.SetColumn(StaffDraftCard, stacked ? 0 : 1);
        Grid.SetRow(StaffDraftCard, stacked ? 1 : 0);
        StaffDraftCard.Margin = stacked ? new Thickness(0, 8, 0, 0) : new Thickness(10, 0, 0, 0);
        StaffCurrentRows.MaxHeight = stacked ? 155 : 260;
        StaffDraftRows.MaxHeight = stacked ? 190 : 280;
    }

    private async Task<StaffSnapshot> ReadAsync(bool compare = false)
    {
        var snapshot = await _client.GetStaffResponsibilitiesAsync(_school.Id, _termId, _kind, _targetId, _lifetime.Token, _term is not null);
        if (!compare && _term is not null && snapshot.ScopeVersion != _openedScope)
            throw new ConsoleApiException(409, "所选学期或全校启用学期已变化，请返回学期管理重新核对。", "TERM_STRUCTURE_SCOPE_CHANGED");
        return snapshot;
    }

    private async Task LoadAsync()
    {
        StaffStatus.Text = "正在读取当前安排…";
        if (_pending is { } pending)
        {
            SetBaseline(pending.Baseline);
            SetDraft(pending.Baseline, pending.Submitted, pending.Baseline);
            await VerifyPendingAsync(pending);
            return;
        }
        var current = await ReadAsync();
        if (_closed) return;
        SetBaseline(current);
        StaffStatus.Text = current.Assignments.Count(row => row.Position == current.PrimaryRole) > 1
            ? $"当前有多位主要负责人。请逐行决定谁继续担任、谁改为{Labels[1]}或卸任。"
            : $"选择教师并核对保存后的安排。交接时可让原负责人转为{Labels[1]}或卸任。";
        FocusAccount();
    }

    private void SetBaseline(StaffSnapshot snapshot)
    {
        _baseline = snapshot;
        _observed = null;
        _compare = _scopeLost = false;
        _preview = null;
        StaffDiscard.IsVisible = false;
        StaffHeading.Text = $"{snapshot.TargetName} · {(_kind == "classes" ? "班主任" : "年级组长")}";
        if (snapshot.Policies is { } policies)
            StaffPolicy.Text = _kind == "classes"
                ? $"全校联动要求（只读）　班主任需有本年级任课：{(policies.HomeroomMustTeach ? "是" : "否")}"
                : $"全校联动要求（只读）　组长需担任本年级班主任：{(policies.GradeLeaderMustBeHomeroom ? "是" : "否")}，需有本年级任课：{(policies.GradeLeaderMustTeach ? "是" : "否")}";
        ShowCurrent(snapshot);
        SetDraft(snapshot, snapshot.Assignments.Select(row => row.Role).ToArray(), snapshot);
        ShowAdvisories(snapshot.Diagnostics, "当前岗位提醒");
        FilterTeachers();
        Render();
    }

    private void ShowCurrent(StaffSnapshot snapshot)
    {
        StaffCurrentRows.ItemsSource = snapshot.Assignments.Select(row => new StaffEditorRow(row.Teacher, snapshot.PrimaryRole,
            snapshot.SecondaryRole, Labels, row.Position == snapshot.PrimaryRole ? 0 : 1, () => { }, "Current")).ToArray();
        StaffCurrentRows.IsVisible = snapshot.Assignments.Count != 0;
        StaffNoCurrent.IsVisible = snapshot.Assignments.Count == 0;
    }

    private void SetDraft(StaffSnapshot baseline, IReadOnlyList<StaffRole> desired, StaffSnapshot names, bool preview = false)
    {
        var requested = desired.ToDictionary(row => row.AccountId, row => row.Position);
        var teachers = names.Teachers.Concat(baseline.Teachers).Concat(names.Assignments.Select(row => row.Teacher))
            .Concat(baseline.Assignments.Select(row => row.Teacher)).DistinctBy(item => item.Id).ToDictionary(item => item.Id);
        _draft.Clear();
        foreach (string id in baseline.Assignments.Select(row => row.AccountId).Concat(requested.Keys).Distinct())
        {
            int role = !requested.TryGetValue(id, out string? position) ? 2 : position == baseline.PrimaryRole ? 0 : 1;
            if (teachers.TryGetValue(id, out var teacher)) _draft.Add(new(teacher, baseline.PrimaryRole, baseline.SecondaryRole, Labels, role, ChangeDraft, "Draft"));
        }
        if (preview)
        {
            var ordered = _draft.OrderBy(row => row.Selection).ToArray();
            _draft.Clear();
            _draft.AddRange(ordered);
        }
        BindDraft();
    }

    private void BindDraft()
    {
        StaffDraftRows.ItemsSource = _draft.ToArray();
        StaffDraftRows.IsVisible = _draft.Count != 0;
        StaffNoDraft.IsVisible = _draft.Count == 0;
    }

    private void ChangeDraft()
    {
        if (_closed || _baseline is null) return;
        if (_preview is not null)
        {
            _preview = null;
            ShowAdvisories((_observed ?? _baseline).Diagnostics, "当前岗位提醒");
        }
        if (!_compare) StaffStatus.Text = _draft.Count(row => row.Selection == 0) > 1
            ? $"仍有多位主要负责人。请确定谁保留主要职责，并逐行选择转为{Labels[1]}或卸任。"
            : "职责草稿已更新，请预览后确认。";
        Render();
    }

    private void TeacherSearchChanged(object? sender, TextChangedEventArgs args) => FilterTeachers();
    private void TeacherChanged(object? sender, SelectionChangedEventArgs args) => Render();

    private void FilterTeachers()
    {
        if (_baseline is not { } current || StaffTeacherPicker is null) return;
        var selected = (StaffTeacherPicker.SelectedItem as StaffTeacher)?.Id;
        string query = (StaffTeacherSearch.Text ?? "").Trim();
        var rows = current.Teachers.Where(item => !item.LocalDisabled && _draft.All(row => row.Teacher.Id != item.Id) &&
            item.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        StaffTeacherPicker.ItemsSource = rows;
        StaffTeacherPicker.SelectedItem = rows.FirstOrDefault(item => item.Id == selected);
        if (query.Length > 0 && rows.Length == 1) StaffTeacherPicker.SelectedIndex = 0;
        Render();
    }

    private void FocusAccount()
    {
        if (_accountId is not { } accountId)
        {
            Focus(StaffTeacherSearch);
            return;
        }
        var current = StaffCurrentRows.Items.Cast<StaffEditorRow>().FirstOrDefault(row => row.Teacher.Id == accountId);
        if (current is not null)
        {
            StaffCurrentRows.SelectedItem = current;
            StaffCurrentRows.ScrollIntoView(current);
            Dispatcher.UIThread.Post(() =>
            {
                if (!_closed && StaffCurrentRows.ContainerFromItem(current) is ListBoxItem row) row.Focus();
            }, DispatcherPriority.Loaded);
            return;
        }
        StaffTeacherPicker.SelectedItem = StaffTeacherPicker.Items.Cast<StaffTeacher>().FirstOrDefault(teacher => teacher.Id == accountId);
        Focus(StaffTeacherPicker);
    }

    private void AddClicked(object? sender, RoutedEventArgs args)
    {
        if (!StaffAddTeacher.IsEnabled || _baseline is not { } current || StaffTeacherPicker.SelectedItem is not StaffTeacher teacher) return;
        var row = new StaffEditorRow(teacher, current.PrimaryRole, current.SecondaryRole, Labels, _draft.Any(item => item.Selection == 0) ? 1 : 0,
            ChangeDraft, "Draft");
        _draft.Add(row);
        BindDraft();
        FilterTeachers();
        ChangeDraft();
        StaffDraftRows.ScrollIntoView(row);
        Focus(StaffPrimaryAction);
    }

    private async void NewTeacherClicked(object? sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        if (_baseline is not { } baseline) return;
        var form = new TeacherAccountForm(_client, _school, createLabel: "创建并返回职责")
        {
            SessionExpired = SessionExpired,
            AllocationTermId = _termId,
            AllocationGradeId = _kind == "grades" ? _targetId : null,
            AllocationWorkspaceId = _kind == "classes" ? _targetId : null
        };
        _teacherForm = form;
        try { await form.ShowDialog(this); }
        finally { _teacherForm = null; }
        if (_closed || form.Receipt is null && !form.AllocationChanged) return;
        _newTeacherId = form.Receipt?.Account.Id ?? form.AllocatedAccountId;
        string completed = form.Receipt is not null ? "账号已创建" : "邮箱分配已保存";
        StaffSnapshot current;
        try { current = await ReadAsync(); }
        catch (ConsoleApiException error) when (error.StatusCode is not (401 or 403))
        {
            if (_closed) return;
            _scopeLost = true;
            StaffStatus.Text = $"{completed}。原职责草稿需重新核对。{error.Message}";
            return;
        }
        if (_closed) return;
        if (current.Version != baseline.Version)
        {
            ShowCompare(current, completed + "。服务器职责安排有变化，草稿已保留。请核对后继续。");
            return;
        }
        _baseline = current;
        _preview = null;
        ShowAdvisories(current.Diagnostics, "当前岗位提醒");
        SelectNewTeacher();
        StaffStatus.Text = completed + "，原职责草稿已保留。可选用已登录的教师并另行保存安排。";
    });

    private void SelectNewTeacher()
    {
        if (_newTeacherId is not { } id) return;
        _newTeacherId = null;
        StaffTeacherSearch.Clear();
        FilterTeachers();
        StaffTeacherPicker.SelectedItem = StaffTeacherPicker.Items.Cast<StaffTeacher>().FirstOrDefault(teacher => teacher.Id == id);
        Focus(StaffTeacherPicker);
    }

    private void ShowAdvisories(IEnumerable<StaffDiagnostic> diagnostics, string title)
    {
        var lines = diagnostics.Select(item => item.Message).ToArray();
        StaffAdvisoryBox.IsVisible = lines.Length > 0;
        StaffAdvisoryTitle.Text = title;
        StaffAdvisories.ItemsSource = lines;
    }

    private void Render()
    {
        if (StaffPrimaryAction is null || _client is null) return;
        bool awaiting = _pending is not null;
        bool editable = _baseline is not null && !_busy && !_compare && !awaiting && !_scopeLost;
        StaffComparison.IsVisible = StaffAccessNote.IsVisible = _baseline is not null;
        StaffPolicy.IsVisible = _baseline?.Policies is not null;
        StaffCurrentHeading.Text = awaiting || _scopeLost ? "上次读取的安排" : "当前安排";
        StaffAddFields.IsEnabled = StaffDraftRows.IsEnabled = editable;
        StaffNewTeacher.IsVisible = _term is null;
        StaffNewTeacher.IsEnabled = editable;
        StaffAddTeacher.IsEnabled = editable && StaffTeacherPicker.SelectedItem is StaffTeacher;
        StaffClose.IsEnabled = !_writing;
        StaffClose.Content = awaiting ? "返回，稍后核对" : "返回";
        StaffReload.IsEnabled = !_busy;
        StaffReload.Content = awaiting ? "核对保存结果" : _compare ? "刷新服务器安排" : "刷新安排";
        StaffReload.Classes.Set("accent", awaiting);
        StaffPrimaryAction.IsVisible = !awaiting && !_scopeLost;
        StaffPrimaryAction.Content = _compare ? "采用当前版本，保留改动" : _preview is not null ? "确认保存" : "预览安排";
        StaffPrimaryAction.IsEnabled = !_busy && !_scopeLost && (_compare || editable && HasChanges && _draft.Count(row => row.Selection == 0) <= 1);
    }

    private async void PrimaryClicked(object? sender, RoutedEventArgs args)
    {
        if (!StaffPrimaryAction.IsEnabled || _baseline is not { } baseline) return;
        if (_compare) { AdoptCurrent(baseline); return; }
        await RunAsync(async () =>
        {
            var roles = Desired();
            if (_preview is null)
            {
                var preview = await _client.PreviewStaffResponsibilitiesAsync(baseline, roles, _lifetime.Token);
                if (_closed) return;
                if (preview.State == "STALE") { ShowCompare(preview.Current); return; }
                ShowCurrent(preview.Current);
                SetDraft(baseline, roles, preview.Current, preview: true);
                _preview = preview;
                ShowAdvisories(preview.Diagnostics, "保存后的岗位联动提醒");
                if (preview.RemainingRelations is { Length: > 0 } remaining)
                {
                    var lines = preview.Diagnostics.Select(item => item.Message).Concat(remaining.Select(row =>
                    {
                        var sources = row.Memberships.Select(item => $"独立成员　{item}")
                            .Concat(row.Teaching.Select(item => $"任课　{item}"))
                            .Concat(row.Duties.Select(item => $"其他岗位　{item}"))
                            .Concat(row.SchoolRole is { } role ? [$"学校角色　{role}"] : []);
                        string detail = string.Join("\n", sources);
                        return $"{row.TeacherName}卸任后\n{(detail.Length == 0 ? "本年级未查到其他成员、任课或岗位关系。" : detail)}";
                    })).ToArray();
                    StaffAdvisoryBox.IsVisible = true;
                    StaffAdvisoryTitle.Text = "岗位提醒与保留的关系";
                    StaffAdvisories.ItemsSource = lines;
                }
                StaffStatus.Text = $"已核对保存后 {roles.Length} 位教师的职责。请确认名单和每行岗位。";
                Focus(StaffPrimaryAction);
                return;
            }
            _writing = true;
            Render();
            StaffStatus.Text = "正在保存本次职责安排…";
            var pending = new PendingStaff(baseline, roles);
            PendingBySession.GetOrCreateValue(_client)[Key] = pending;
            StaffSaveResult result;
            try { result = await _client.SaveStaffResponsibilitiesAsync(baseline, roles, _lifetime.Token); }
            catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or >= 500)
            {
                _preview = null;
                await VerifyPendingAsync(pending);
                return;
            }
            catch (ConsoleApiException) { ClearPending(); throw; }
            if (_closed) return;
            ClearPending();
            if (result.State == "STALE") { ShowCompare(result.Current); return; }
            RolesChanged = true;
            SetBaseline(result.Current);
            StaffStatus.Text = _term is null ? "职责已保存，可返回核对学校总览。" : "所选学期的职责已保存，可返回查看岗位。";
            Focus(StaffClose);
        });
    }

    private async Task VerifyPendingAsync(PendingStaff pending)
    {
        StaffStatus.Text = "保存回执未确认，正在只读核对当前安排…";
        var current = await ReadAsync(compare: true);
        if (_closed) return;
        ClearPending();
        if (_term is not null && current.ScopeVersion != _openedScope)
        {
            ShowCompare(current, "已读取服务器当前职责，学期状态已变化。请返回学期管理重新核对。");
            _scopeLost = true;
            return;
        }
        if (ConsoleClient.SameStaffRoles(current.Assignments, pending.Submitted))
        {
            RolesChanged = true;
            SetBaseline(current);
            StaffStatus.Text = "已核对，服务器当前职责与本次提交一致。";
            Focus(StaffClose);
        }
        else ShowCompare(current, "服务器当前安排与本次提交有差异。草稿已保留，请核对后再决定。");
    }

    private void ClearPending() => PendingBySession.GetOrCreateValue(_client).Remove(Key);

    private void ShowCompare(StaffSnapshot current, string? message = null)
    {
        _observed = current;
        _compare = true;
        _preview = null;
        ShowCurrent(current);
        ShowAdvisories(current.Diagnostics, "服务器当前提醒");
        StaffStatus.Text = message ?? "服务器安排已变化，本次未写入。请先核对当前安排与保留的草稿。";
        Focus(StaffPrimaryAction);
    }

    private void AdoptCurrent(StaffSnapshot baseline)
    {
        if (_observed is not { } current) return;
        var before = baseline.Assignments.ToDictionary(row => row.AccountId, row => row.Position);
        var desired = Desired().ToDictionary(row => row.AccountId, row => row.Position);
        var rebased = current.Assignments.ToDictionary(row => row.AccountId, row => row.Position);
        foreach (string id in before.Keys.Except(desired.Keys)) rebased.Remove(id);
        foreach (var (id, role) in desired)
            if (before.GetValueOrDefault(id) != role) rebased[id] = role;
        var priorDraftTeachers = _draft.Select(row => row.Teacher);
        var names = current with { Teachers = current.Teachers.Concat(priorDraftTeachers).DistinctBy(item => item.Id).ToArray() };
        SetBaseline(current);
        SetDraft(current, rebased.Select(row => new StaffRole(row.Key, row.Value)).ToArray(), names);
        FilterTeachers();
        SelectNewTeacher();
        ChangeDraft();
        if (_draft.Count(row => row.Selection == 0) <= 1) StaffStatus.Text = "已合并服务器当前安排与本次改动。请重新核对预览。";
    }

    private async void ReloadClicked(object? sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        if (_pending is { } pending) { await VerifyPendingAsync(pending); return; }
        if (!HasChanges && !_compare) { await LoadAsync(); return; }
        var current = await ReadAsync();
        if (!_closed) ShowCompare(current, "已读取服务器当前安排。草稿仍在，采用当前版本后可以继续核对。");
    });

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _closed) return;
        _busy = true;
        Render();
        try { await action(); }
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error)
        {
            if (_closed) return;
            _scopeLost |= error.Code is "STAFF_TERM_CHANGED" or "STAFF_TARGET_CHANGED" or "TERM_STRUCTURE_SCOPE_CHANGED" || error.StatusCode is 401 or 403;
            if (error.StatusCode is 401 or 403)
            {
                ClearPending();
                _baseline = _observed = null;
                _draft.Clear();
                BindDraft();
                StaffCurrentRows.ItemsSource = null;
                StaffCurrentRows.IsVisible = StaffNoCurrent.IsVisible = false;
                ShowAdvisories([], "");
            }
            StaffStatus.Text = _pending is null ? error.Message : $"保存结果仍需核对。{error.Message}";
            if (error.StatusCode == 401) SessionExpired?.Invoke(error);
        }
        finally { _busy = _writing = false; if (!_closed) Render(); }
    }

    private void Focus(Control control) => Dispatcher.UIThread.Post(() =>
    {
        if (!_closed && control.IsEffectivelyVisible && control.IsEffectivelyEnabled) control.Focus();
    }, DispatcherPriority.Loaded);

    private void CloseClicked(object? sender, RoutedEventArgs args) => Close();
    private void DiscardClicked(object? sender, RoutedEventArgs args) => CloseForScopeChange();
    internal void CloseForScopeChange() { _allowClose = true; _teacherForm?.CloseForScopeChange(); Close(); }
}
