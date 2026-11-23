using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class TeachingView : UserControl
{
    private readonly ConsoleClient _client = null!;
    private readonly SubjectSetupFlow _subjectSetup = new();
    private readonly bool _courseGroup;
    private readonly School _school = null!;
    private readonly string _schoolId = "", _schoolName = "", _termId = "", _classId = "";
    private readonly string? _entrySubjectId, _entryAccountId;
    private readonly string? _termName;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, CheckBox> _choices = new(StringComparer.Ordinal);
    private string _action = "ASSIGN";
    private string _className = "";
    private TeachingAssignment? _original;
    private TeacherAccountForm? _teacherForm;
    private (TeachingDraft Draft, TeachingPreview Preview)? _preview;
    private static readonly ConditionalWeakTable<ConsoleClient,
        Dictionary<(string School, string Term, string Class), TeachingPending>> PendingBySession = new();
    private TeachingPending? _unconfirmed;
    private bool _busy, _closed, _submitting, _scopeUnavailable;
    private bool _binding = true;
    private bool _draftChanged;
    public event Action<ConsoleApiException>? SessionExpired;
    internal event Action<ConsoleApiException>? AuthorizationLost;
    internal Action? CloseRequested { get; set; }
    internal bool InWorkspace { get; }
    private Window HostWindow => (Window)TopLevel.GetTopLevel(this)!;

    public TeachingView()
    {
        InitializeComponent();
        _binding = false;
    }

    internal TeachingView(ConsoleClient client, School school, string termId, string classId, string? subjectId,
        string? accountId = null, bool courseGroup = false, bool inWorkspace = false, string? termName = null) : this()
    {
        _client = client;
        _courseGroup = courseGroup;
        if (courseGroup) TeachingHeading.Text = "走班任课";
        _school = school;
        _schoolId = school.Id;
        _schoolName = school.Name;
        _termId = termId;
        _classId = classId;
        _entrySubjectId = subjectId;
        _entryAccountId = accountId;
        _termName = termName;
        InWorkspace = inWorkspace;
        TeachingTeacherPicker.IsVisible = !inWorkspace;
        OpenTeachingTeacherChoice.IsVisible = inWorkspace;
        if (inWorkspace) TeachingTeacherCandidates.AddHandler(KeyDownEvent, CandidateKeyDown, RoutingStrategies.Tunnel);
        TeachingContext.Text = school.Name;
        if (PendingBySession.TryGetValue(client, out var saved))
            _unconfirmed = saved.GetValueOrDefault((_schoolId, _termId, _classId));
        SizeChanged += (_, _) => ArrangeTeacherChoice();
        UpdateControls();
    }

    internal async Task StartAsync()
    {
        Control? entryFocus = null;
        await RunAsync(async () =>
        {
            await ReadAsync(_unconfirmed?.Draft.SubjectId ?? _entrySubjectId);
            if (!_closed && _entryAccountId is { } accountId && _unconfirmed is null)
            {
                var existing = TeachingExistingPicker.Items.OfType<TeachingAssignment>().FirstOrDefault(row => row.AccountId == accountId);
                if (existing is not null)
                {
                    TeachingExistingPicker.SelectedItem = existing;
                    entryFocus = TeachingExistingPicker;
                }
                else if (TeachingTeacherPicker.Items.OfType<TeachingTeacher>().FirstOrDefault(row => row.Id == accountId) is { } teacher)
                {
                    TeachingTeacherPicker.SelectedItem = teacher;
                    entryFocus = TeacherChoiceAnchor;
                }
            }
            if (_closed || _unconfirmed is not { } pending) return;
            TeachingFeedback.Text = "已恢复待核对的提交。请先读取服务器当前任课安排。";
            ShowBeforeReadback(pending);
        });
        if (_closed) return;
        if (_unconfirmed is null && InWorkspace) RestorePausedDraft();
        if (_unconfirmed is not null) TeachingReadButton.Focus(NavigationMethod.Tab);
        else (entryFocus ?? (InWorkspace ? TeacherChoiceAnchor : null))?.Focus(NavigationMethod.Tab);
    }

    private TeachingSubject? Subject => TeachingSubjectPicker.SelectedItem as TeachingSubject;
    private TeachingTeacher? Teacher => TeachingTeacherPicker.SelectedItem as TeachingTeacher;
    private string AssignmentPosition => TeachingPositionPicker.SelectedIndex == 1 ? "CO_TEACHER" : "PRIMARY";

    private async Task ReadAsync(string? subjectId, bool announce = true, bool preserveDraft = false)
    {
        string? priorSubject = Subject?.Id;
        string priorAction = _action;
        string? priorAssignment = _original?.Id;
        int position = TeachingPositionPicker.SelectedIndex;
        bool assignmentChanged = false;
        string[] chosenPeers = preserveDraft ? _choices.Where(row => row.Value.IsChecked == true).Select(row => row.Key).ToArray() : [];
        var catalog = await _client.GetTeachingAsync(_schoolId, _termId, _classId, _lifetime.Token, _courseGroup);
        if (_closed) return;
        _scopeUnavailable = false;
        _className = catalog.Classroom.Name;
        var teacherId = Teacher?.Id;
        _binding = true;
        TeachingContext.Text = $"{_schoolName} · {_termName ?? "当前学期"} · {catalog.Classroom.Name}";
        TeachingTeacherPicker.ItemsSource = catalog.Teachers;
        TeachingTeacherPicker.SelectedItem = catalog.Teachers.FirstOrDefault(row => row.Id == teacherId);
        TeachingSubjectPicker.ItemsSource = catalog.Subjects;
        TeachingSubjectPicker.SelectedItem = subjectId is null ? catalog.Subjects.FirstOrDefault()
            : catalog.Subjects.FirstOrDefault(row => row.Id == subjectId);
        BindSubject();
        if (preserveDraft && priorSubject is not null && Subject?.Id == priorSubject)
        {
            if (priorAssignment is not null && TeachingExistingPicker.Items.OfType<TeachingAssignment>()
                .FirstOrDefault(row => row.Id == priorAssignment) is { } assignment)
            {
                TeachingExistingPicker.SelectedItem = assignment;
                SetOperation(priorAction, assignment);
                TeachingTeacherPicker.SelectedItem = catalog.Teachers.FirstOrDefault(row => row.Id == teacherId);
            }
            else if (priorAssignment is not null)
            {
                assignmentChanged = true;
                TeachingTeacherPicker.SelectedItem = null;
            }
            TeachingPositionPicker.SelectedIndex = position;
            foreach (string classId in chosenPeers)
                if (_choices.TryGetValue(classId, out var check)) check.IsChecked = true;
        }
        _binding = false;
        TeachingSubjectSetupButton.IsVisible = !_courseGroup && catalog.Subjects.Length == 0;
        TeachingSubjectPicker.IsEnabled = !_courseGroup && (!InWorkspace || _entrySubjectId is null);
        if (announce) TeachingFeedback.Text = assignmentChanged ? "原草稿对应的任课关系已变化。请核对当前任课，再选择要办理的操作。"
            : catalog.Subjects.Length == 0 ? (_courseGroup ? "原教学班学科已变化，请返回核对。" : "该班暂无随行政班授课的科目。")
            : Subject is null ? "原待办的科目已变化，请核对当前班级的科目。"
            : catalog.Teachers.Length == 0 ? "暂无可分配的本校教师。" : "请选择教师和任课身份，先预览再提交。";
    }

    private void SubjectChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!_binding) { MarkDraftChanged(); BindSubject(); }
    }

    private void BindSubject()
    {
        var subject = Subject;
        TeachingChoiceContext.Text = $"{_className} · {subject?.Name}";
        var rows = subject?.Classes.First(row => row.Id == _classId).Assignments ?? [];
        TeachingExistingPicker.ItemsSource = rows;
        TeachingExistingPicker.IsVisible = rows.Length > 0;
        TeachingExistingEmpty.IsVisible = rows.Length == 0;
        TeachingExistingPicker.SelectedIndex = rows.Length > 0 ? 0 : -1;
        _choices.Clear();
        TeachingClassChoices.Children.Clear();
        foreach (var room in subject?.Classes.Where(row => row.Id != _classId) ?? [])
        {
            var check = new CheckBox { Content = room.Name };
            AutomationProperties.SetAutomationId(check, "TeachingClass-" + room.Id);
            check.IsCheckedChanged += (_, _) => { MarkDraftChanged(); ResetPreview(); };
            TeachingClassChoices.Children.Add(check);
            _choices.Add(room.Id, check);
        }
        SetOperation("ASSIGN");
        UpdateControls();
    }

    private void AddClicked(object? sender, RoutedEventArgs args) { MarkDraftChanged(); SetOperation("ASSIGN"); }
    private void ChangeClicked(object? sender, RoutedEventArgs args) { MarkDraftChanged(); SetOperation("CHANGE", TeachingExistingPicker.SelectedItem as TeachingAssignment); }
    private void RemoveClicked(object? sender, RoutedEventArgs args) { MarkDraftChanged(); SetOperation("REMOVE", TeachingExistingPicker.SelectedItem as TeachingAssignment); }
    private void ExistingChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_binding) return;
        if (_action != "ASSIGN") SetOperation(_action, TeachingExistingPicker.SelectedItem as TeachingAssignment);
        UpdateControls();
    }

    private void SetOperation(string action, TeachingAssignment? original = null)
    {
        _action = original is null ? "ASSIGN" : action;
        _original = original;
        TeachingOperation.Text = _action switch
        {
            "CHANGE" => $"修改任课 · {original}",
            "REMOVE" => $"移除任课 · {original}",
            _ => "新增任课"
        };
        TeachingAssignmentFields.IsVisible = _action != "REMOVE";
        TeachingPeers.IsVisible = _action == "ASSIGN" && _choices.Count > 0;
        TeachingAccessNote.Text = _action == "REMOVE"
            ? "仅移除所选任课关系。教师现有的班级访问权限会保留。"
            : "新增任课可能授予或提升教师的班级成员权限。原有权限会保留。";
        if (_action == "CHANGE" && original is not null)
        {
            TeachingTeacherPicker.SelectedItem = TeachingTeacherPicker.Items.Cast<TeachingTeacher>().FirstOrDefault(row => row.Id == original.AccountId);
            TeachingPositionPicker.SelectedIndex = original.Position == "CO_TEACHER" ? 1 : 0;
        }
        ResetPreview();
    }

    private void DraftChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!_binding) { MarkDraftChanged(); ResetPreview(); }
    }

    private void ResetPreview()
    {
        _preview = null;
        TeachingPreviewList.ItemsSource = null;
        UpdateControls();
    }

    private TeachingDraft? Draft()
    {
        if (Subject is not { } subject || _action != "REMOVE" && Teacher is null) return null;
        string[] ids = _action == "ASSIGN" ? [_classId, .. _choices.Where(row => row.Value.IsChecked == true).Select(row => row.Key)] : [_classId];
        return new(_termId, subject.Id, _action, ids, _action == "REMOVE" ? null : Teacher?.Id,
            _action == "REMOVE" ? null : AssignmentPosition, _original?.Id);
    }

    private void UpdateControls()
    {
        bool editable = !_busy && !_closed && !_scopeUnavailable && _unconfirmed is null;
        TeachingFields.IsEnabled = editable && TeachingSubjectPicker.Items.Count > 0;
        TeachingExistingPicker.IsEnabled = TeachingExistingPicker.ItemCount > 0;
        TeachingChangeButton.IsEnabled = TeachingRemoveButton.IsEnabled = TeachingExistingPicker.SelectedItem is TeachingAssignment;
        TeachingPreviewButton.IsEnabled = editable && Draft() is not null;
        TeachingApplyButton.IsEnabled = editable && _preview is { Preview: { CanApply: true, HasChanges: true } };
        TeachingReadButton.IsEnabled = !_busy && !_closed;
        OpenTeachingTeacherChoice.Content = Teacher?.ToString() ?? "选择教师";
        OpenTeachingTeacherChoice.IsEnabled = editable && TeachingTeacherPicker.ItemCount > 0;
        TeachingSubjectSetupButton.IsEnabled = editable;
        TeachingNewTeacher.IsVisible = _action != "REMOVE";
        TeachingNewTeacher.IsEnabled = editable && Subject is not null;
        TeachingReadButton.Content = _unconfirmed is null ? "刷新安排" : "核对提交结果";
        TeachingReadButton.Classes.Set("accent", _unconfirmed is not null);
        TeachingPreviewButton.IsVisible = TeachingApplyButton.IsVisible = _unconfirmed is null;
        TeachingPreviewEmpty.IsVisible = TeachingPreviewList.ItemCount == 0;
        TeachingTargetCount.Text = _unconfirmed is { } pending
            ? $"{pending.Draft.ClassIds.Length} 个班待核对"
            : _preview is { } review ? $"本次预览 {review.Preview.Rows.Length} 个班"
            : Draft() is { } draft ? $"当前选择 {draft.ClassIds.Length} 个班" : "";
    }

    private async void PreviewClicked(object? sender, RoutedEventArgs args)
    {
        if (!TeachingPreviewButton.IsEnabled || Draft() is not { } draft) return;
        await RunAsync(async () =>
        {
            ResetPreview();
            TeachingFeedback.Text = "正在读取各班当前安排…";
            var preview = await _client.PreviewTeachingAsync(_schoolId, _classId, draft, _lifetime.Token, _courseGroup);
            if (_closed) return;
            _preview = (draft, preview);
            TeachingBeforeHeading.Text = "当前任课";
            TeachingAfterHeading.Text = "预计提交后";
            ShowRows(preview.Rows.Select(row => TeachingPreviewRow.Planned(row, Teacher?.ToString(), draft)));
            TeachingFeedback.Text = !preview.CanApply ? "本批尚未写入。请核对以下班级，调整选择后重新预览。"
                : preview.HasChanges ? "请核对逐班变化，然后确认提交。" : "服务器已有相同安排，无需提交。";
        });
    }

    private async void ApplyClicked(object? sender, RoutedEventArgs args)
    {
        if (_preview is not { } pending || !pending.Preview.CanApply || !pending.Preview.HasChanges) return;
        await RunAsync(async () =>
        {
            _preview = null;
            _submitting = true;
            TeachingFeedback.Text = "正在提交任课安排…";
            TeachingResult result;
            try
            {
                result = await _client.SaveTeachingAsync(_schoolId, _classId, pending.Draft, pending.Preview, _lifetime.Token, _courseGroup);
            }
            catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or >= 500)
            {
                SetUnconfirmed(new(pending.Draft, pending.Preview));
                ShowBeforeReadback(_unconfirmed!);
                await VerifyAsync();
                return;
            }
            finally { _submitting = false; }

            if (_closed) return;
            if (result.State != "APPLIED")
            {
                ShowRows(TeachingPreviewRow.Compare(result.Rows, new TeachingPending(pending.Draft, pending.Preview),
                    result.State == "STALE" ? TeachingObservation.Stale : TeachingObservation.Blocked), observed: true);
                var names = string.Join("、", result.Rows.Select(row => row.ClassName));
                TeachingFeedback.Text = result.State == "STALE" ? $"服务器安排已变化：{names}。本批零写入，请核对当前安排并重新预览。"
                    : "本批零写入。请处理下列冲突，再重新预览。";
                return;
            }
            int changed = result.Rows.Count(row => row.State is "ADD" or "CHANGE" or "REMOVE");
            _draftChanged = false;
            TeachingFeedback.Text = $"已保存 {changed} 个班的任课变化。";
            await RefreshAfterResultAsync(pending.Draft.SubjectId);
            if (!_closed) ShowRows(TeachingPreviewRow.Compare(result.Rows, new TeachingPending(pending.Draft, pending.Preview),
                TeachingObservation.Applied), observed: true);
        });
    }

    private async Task VerifyAsync()
    {
        if (_unconfirmed is not { } pending || _closed) return;
        var draft = pending.Draft;
        TeachingFeedback.Text = "提交回执未确认，正在只读核对服务器安排…";
        var current = await _client.PreviewTeachingAsync(_schoolId, _classId, draft, _lifetime.Token, _courseGroup);
        if (_closed) return;
        var observed = TeachingPreviewRow.Compare(current.Rows, pending, TeachingObservation.Readback);
        bool confirmed = observed.All(row => !row.NeedsAttention);
        SetUnconfirmed(null);
        TeachingFeedback.Text = confirmed ? "已核对，服务器当前安排与本次提交一致。"
            : "已核对，服务器当前安排与本次提交有差异。请检查下列实际安排。";
        if (confirmed) await RefreshAfterResultAsync(draft.SubjectId);
        if (confirmed) _draftChanged = false;
        if (!_closed) ShowRows(observed, observed: true);
    }

    private async Task RefreshAfterResultAsync(string subjectId)
    {
        try { await ReadAsync(subjectId, announce: false); }
        catch (ConsoleApiException error) when (error.StatusCode is not (401 or 403))
        {
            if (!_closed) TeachingFeedback.Text += " 当前列表刷新失败，请点「刷新安排」。";
        }
    }

    private void SetUnconfirmed(TeachingPending? pending)
    {
        _unconfirmed = pending;
        var key = (_schoolId, _termId, _classId);
        var byScope = PendingBySession.GetOrCreateValue(_client);
        if (pending is null) byScope.Remove(key);
        else byScope[key] = pending;
    }

    private void ShowBeforeReadback(TeachingPending pending)
    {
        TeachingBeforeHeading.Text = "预览时";
        TeachingAfterHeading.Text = "服务器当前";
        ShowRows(pending.Preview.Rows.Select(row =>
        {
            var observed = TeachingPreviewRow.Observed(row, row, changed: false, applied: false);
            return observed with { After = "待核对", Status = "回执待确认" };
        }));
    }

    private void ShowRows(IEnumerable<TeachingPreviewRow> rows, bool observed = false)
    {
        if (observed)
        {
            TeachingBeforeHeading.Text = "预览时";
            TeachingAfterHeading.Text = "服务器当前";
        }
        var items = rows.ToArray();
        TeachingPreviewList.ItemsSource = items;
        TeachingPreviewEmpty.IsVisible = items.Length == 0;
        if (items.FirstOrDefault(row => row.NeedsAttention) is not { } target) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed || TeachingPreviewList.ItemsSource != items) return;
            TeachingPreviewList.SelectedItem = target;
            TeachingPreviewList.ScrollIntoView(target);
            Dispatcher.UIThread.Post(() =>
            {
                if (_closed || TeachingPreviewList.ItemsSource != items || !ReferenceEquals(TeachingPreviewList.SelectedItem, target)) return;
                var row = TeachingPreviewList.GetVisualDescendants().OfType<Border>().FirstOrDefault(control =>
                    AutomationProperties.GetAutomationId(control) == target.AutomationId);
                row?.Focus(NavigationMethod.Tab);
            }, DispatcherPriority.Background);
        }, DispatcherPriority.Loaded);
    }

    private async void ReadClicked(object? sender, RoutedEventArgs args) =>
        await RunAsync(() => _unconfirmed is not null ? VerifyAsync() : ReadAsync(Subject?.Id, preserveDraft: InWorkspace));

    private async void SubjectSetupClicked(object? sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        await _subjectSetup.OpenAsync(HostWindow, _client, _school, _termId, _classId, [_classId], error => SessionExpired?.Invoke(error));
        if (!_closed) await ReadAsync(null);
    });

    private async void NewTeacherClicked(object? sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        if (Subject is not { } subject) return;
        var form = new TeacherAccountForm(_client, _school, createLabel: "创建并返回任课")
        {
            SessionExpired = error => SessionExpired?.Invoke(error),
            AllocationTermId = _termId,
            AllocationWorkspaceId = _classId
        };
        _teacherForm = form;
        try { await form.ShowDialog(HostWindow); }
        finally { _teacherForm = null; }
        if (_closed || form.Receipt is null && !form.AllocationChanged) return;
        string? teacherId = form.Receipt?.Account.Id ?? form.AllocatedAccountId ?? Teacher?.Id;
        string completed = form.Receipt is not null ? "账号已创建" : "邮箱分配已保存";
        TeachingFeedback.Text = completed + "，正在核对原任课目标…";
        var catalog = await ReadAfterTeacherCreationAsync();
        if (_closed || catalog is null) return;
        if (!catalog.Subjects.Any(row => row.Id == subject.Id))
        {
            _scopeUnavailable = true;
            TeachingFeedback.Text = completed + "。原科目已变化，请刷新原任课范围后核对。";
            return;
        }
        _binding = true;
        TeachingTeacherPicker.ItemsSource = catalog.Teachers;
        TeachingTeacherPicker.SelectedItem = catalog.Teachers.FirstOrDefault(teacher => teacher.Id == teacherId);
        _binding = false;
        ResetPreview();
        TeachingFeedback.Text = Teacher is null ? completed + "。待认领邀请需等教师本人完成登录后才能选用。" :
            completed + "。原班级、科目及任课身份仍在，请预览并确认任课。";
        Dispatcher.UIThread.Post(() => { if (!_closed) TeacherChoiceAnchor.Focus(NavigationMethod.Tab); }, DispatcherPriority.Loaded);
    });

    private async Task<TeachingCatalog?> ReadAfterTeacherCreationAsync()
    {
        try { return await _client.GetTeachingAsync(_schoolId, _termId, _classId, _lifetime.Token, _courseGroup); }
        catch (ConsoleApiException error) when (error.StatusCode is not (401 or 403))
        {
            if (!_closed)
            {
                _scopeUnavailable = true;
                TeachingFeedback.Text = $"账号已创建。原任课草稿需重新核对。{error.Message}";
            }
            return null;
        }
    }

    private async Task RunAsync(Func<Task> work)
    {
        if (_busy || _closed) return;
        _busy = true;
        UpdateControls();
        try { await work(); }
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error)
        {
            if (_closed) return;
            TeachingFeedback.Text = _unconfirmed is null ? error.Message : $"提交回执仍未确认。{error.Message}";
            _scopeUnavailable |= error.Code is "TEACHING_TERM_CHANGED" or "TEACHING_CLASS_CHANGED" or "TEACHING_SUBJECT_CHANGED"
                || error.StatusCode is 401 or 403;
            if (error.StatusCode is 401 or 403)
            {
                SetUnconfirmed(null);
                TeachingSubjectPicker.ItemsSource = null;
                TeachingTeacherPicker.ItemsSource = null;
                ResetPreview();
            }
            if (error.StatusCode == 401) SessionExpired?.Invoke(error);
            else if (error.StatusCode == 403) AuthorizationLost?.Invoke(error);
        }
        finally { _busy = false; if (!_closed) UpdateControls(); }
    }

    private void CloseClicked(object? sender, RoutedEventArgs args) => CloseRequested?.Invoke();

    internal bool CanClose()
    {
        if (_submitting) return false;
        if (!InWorkspace || !_draftChanged || _unconfirmed is not null) return true;
        TeachingKeepDraft.IsVisible = Draft() is not null;
        TeachingDiscard.IsVisible = true;
        TeachingFeedback.Text = TeachingKeepDraft.IsVisible ? "任课草稿尚未保存。可在本次登录中保留原草稿，也可放弃返回。"
            : "任课草稿尚未保存。可以继续办理，或放弃草稿后返回。";
        (TeachingKeepDraft.IsVisible ? TeachingKeepDraft : TeachingDiscard).Focus(NavigationMethod.Tab);
        return false;
    }

    internal void End()
    {
        if (_closed) return;
        _closed = true;
        _lifetime.Cancel();
        _teacherForm?.CloseForScopeChange();
        _subjectSetup.Close();
        _lifetime.Dispose();
    }
}
