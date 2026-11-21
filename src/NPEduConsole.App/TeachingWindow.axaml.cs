using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class TeachingWindow : Window
{
    private readonly ConsoleClient _client = null!;
    private readonly SubjectSetupFlow _subjectSetup = new();
    private readonly School _school = null!;
    private readonly string _schoolId = "", _schoolName = "", _termId = "", _classId = "";
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, CheckBox> _choices = new(StringComparer.Ordinal);
    private string _action = "ASSIGN";
    private TeachingAssignment? _original;
    private (TeachingDraft Draft, TeachingPreview Preview)? _preview;
    private TeachingDraft? _unconfirmed;
    private bool _busy, _closed, _submitting;
    private bool _binding = true;
    public event Action<ConsoleApiException>? SessionExpired;

    public TeachingWindow()
    {
        InitializeComponent();
        _binding = false;
    }

    public TeachingWindow(ConsoleClient client, School school, string termId, string classId, string? subjectId) : this()
    {
        _client = client;
        _school = school;
        _schoolId = school.Id;
        _schoolName = school.Name;
        _termId = termId;
        _classId = classId;
        TeachingContext.Text = school.Name;
        Opened += async (_, _) => await RunAsync(() => ReadAsync(subjectId));
        Closing += (_, args) => args.Cancel = _submitting && !_closed;
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
    }

    private TeachingSubject? Subject => TeachingSubjectPicker.SelectedItem as TeachingSubject;
    private TeachingTeacher? Teacher => TeachingTeacherPicker.SelectedItem as TeachingTeacher;
    private string AssignmentPosition => TeachingPositionPicker.SelectedIndex == 1 ? "CO_TEACHER" : "PRIMARY";

    private async Task ReadAsync(string? subjectId, bool announce = true)
    {
        var catalog = await _client.GetTeachingAsync(_schoolId, _termId, _classId, _lifetime.Token);
        if (_closed) return;
        var teacherId = Teacher?.Id;
        _binding = true;
        TeachingContext.Text = $"{_schoolName} · {catalog.Classroom.Name}";
        TeachingTeacherPicker.ItemsSource = catalog.Teachers;
        TeachingTeacherPicker.SelectedItem = catalog.Teachers.FirstOrDefault(row => row.Id == teacherId);
        TeachingSubjectPicker.ItemsSource = catalog.Subjects;
        TeachingSubjectPicker.SelectedItem = subjectId is null ? catalog.Subjects.FirstOrDefault()
            : catalog.Subjects.FirstOrDefault(row => row.Id == subjectId);
        _binding = false;
        BindSubject();
        TeachingSubjectSetupButton.IsVisible = catalog.Subjects.Length == 0;
        if (announce) TeachingFeedback.Text = catalog.Subjects.Length == 0 ? "该班暂无随行政班授课的科目。"
            : Subject is null ? "原待办的科目已变化，请核对当前班级的科目。"
            : catalog.Teachers.Length == 0 ? "暂无可分配的本校教师。" : "请选择教师和任课身份，先预览再提交。";
    }

    private void SubjectChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!_binding) BindSubject();
    }

    private void BindSubject()
    {
        var subject = Subject;
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
            check.IsCheckedChanged += (_, _) => ResetPreview();
            TeachingClassChoices.Children.Add(check);
            _choices.Add(room.Id, check);
        }
        SetOperation("ASSIGN");
        UpdateControls();
    }

    private void AddClicked(object? sender, RoutedEventArgs args) => SetOperation("ASSIGN");
    private void ChangeClicked(object? sender, RoutedEventArgs args) => SetOperation("CHANGE", TeachingExistingPicker.SelectedItem as TeachingAssignment);
    private void RemoveClicked(object? sender, RoutedEventArgs args) => SetOperation("REMOVE", TeachingExistingPicker.SelectedItem as TeachingAssignment);
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
            ? "移除只删除这条任课关系。教师现有的班级访问权限会保留，包括因其他科目或职责获得的访问。"
            : "分配主讲或协同时，KV 可能授予或提升教师的班级成员权限。修改任课会保留原有班级成员权限。";
        if (_action == "CHANGE" && original is not null)
        {
            TeachingTeacherPicker.SelectedItem = TeachingTeacherPicker.Items.Cast<TeachingTeacher>().FirstOrDefault(row => row.Id == original.AccountId);
            TeachingPositionPicker.SelectedIndex = original.Position == "CO_TEACHER" ? 1 : 0;
        }
        ResetPreview();
    }

    private void DraftChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!_binding) ResetPreview();
    }

    private void ResetPreview()
    {
        _preview = null;
        TeachingRows.Children.Clear();
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
        bool editable = !_busy && !_closed && _unconfirmed is null;
        TeachingFields.IsEnabled = editable && TeachingSubjectPicker.Items.Count > 0;
        TeachingExistingPicker.IsEnabled = TeachingExistingPicker.ItemCount > 0;
        TeachingChangeButton.IsEnabled = TeachingRemoveButton.IsEnabled = TeachingExistingPicker.SelectedItem is TeachingAssignment;
        TeachingPreviewButton.IsEnabled = editable && Draft() is not null;
        TeachingApplyButton.IsEnabled = editable && _preview is { Preview: { CanApply: true, HasChanges: true } };
        TeachingReadButton.IsEnabled = !_busy && !_closed;
        TeachingSubjectSetupButton.IsEnabled = editable;
        TeachingReadButton.Content = _unconfirmed is null ? "刷新安排" : "核对提交结果";
    }

    private async void PreviewClicked(object? sender, RoutedEventArgs args)
    {
        if (Draft() is not { } draft || _busy) return;
        await RunAsync(async () =>
        {
            ResetPreview();
            TeachingFeedback.Text = "正在读取各班当前安排…";
            var preview = await _client.PreviewTeachingAsync(_schoolId, _classId, draft, _lifetime.Token);
            if (_closed) return;
            _preview = (draft, preview);
            ShowRows(preview.Rows);
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
                result = await _client.SaveTeachingAsync(_schoolId, _classId, pending.Draft, pending.Preview, _lifetime.Token);
            }
            catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or >= 500)
            {
                _unconfirmed = pending.Draft;
                await VerifyAsync();
                return;
            }
            finally { _submitting = false; }

            if (_closed) return;
            if (result.State != "APPLIED")
            {
                ShowRows(result.Rows);
                var names = string.Join("、", result.Rows.Select(row => row.ClassName));
                TeachingFeedback.Text = result.State == "STALE" ? $"服务器安排已变化：{names}。本批零写入，请核对当前安排并重新预览。"
                    : "本批零写入。请处理下列冲突，再重新预览。";
                return;
            }
            int changed = result.Rows.Count(row => row.State is "ADD" or "CHANGE" or "REMOVE");
            TeachingFeedback.Text = $"已保存 {changed} 个班的任课变化。";
            await RefreshAfterResultAsync(pending.Draft.SubjectId);
            if (!_closed) ShowRows(result.Rows);
        });
    }

    private async Task VerifyAsync()
    {
        if (_unconfirmed is not { } draft || _closed) return;
        TeachingFeedback.Text = "提交回执未确认，正在只读核对服务器安排…";
        var current = await _client.PreviewTeachingAsync(_schoolId, _classId, draft, _lifetime.Token);
        if (_closed) return;
        bool confirmed = current.Rows.All(row => draft.Action switch
        {
            "REMOVE" => row.State == "ALREADY_REMOVED",
            "CHANGE" => row.Assignments.Any(item => item.AccountId == draft.AccountId && item.Position == draft.Position) &&
                (row.State == "UNCHANGED" || row.Assignments.All(item => item.Id != draft.AssignmentId)),
            _ => row.Assignments.Any(item => item.AccountId == draft.AccountId && item.Position == draft.Position)
        });
        _unconfirmed = null;
        TeachingFeedback.Text = confirmed ? "已核对，服务器当前安排与本次提交一致。"
            : "已核对，服务器当前安排与本次提交有差异。请检查下列实际安排。";
        if (confirmed) await RefreshAfterResultAsync(draft.SubjectId);
        if (!_closed) ShowRows(current.Rows, observed: true);
    }

    private async Task RefreshAfterResultAsync(string subjectId)
    {
        try { await ReadAsync(subjectId, announce: false); }
        catch (ConsoleApiException error) when (error.StatusCode is not (401 or 403))
        {
            if (!_closed) TeachingFeedback.Text += " 当前列表刷新失败，请点「刷新安排」。";
        }
    }

    private void ShowRows(IEnumerable<TeachingRow> rows, bool observed = false)
    {
        TeachingRows.Children.Clear();
        Control? firstIssue = null;
        foreach (var row in rows)
        {
            string assigned = string.Join("、", row.Assignments.Select(item => item.ToString()));
            string detail = assigned.Length > 0 ? $" · 当前任课：{assigned}" : "";
            var text = new TextBlock { Text = $"{row.ClassName} · {(observed && assigned.Length > 0 ? "服务器当前安排" : row.Message)}{detail}",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            var card = new Border { Classes = { "card" }, Padding = new Thickness(10), Child = text };
            TeachingRows.Children.Add(card);
            if (row.State is "OTHER_PRIMARY" or "EXISTING_ROLE" or "INVALID") firstIssue ??= card;
        }
        if ((firstIssue ?? TeachingRows.Children.FirstOrDefault()) is { } first)
            Dispatcher.UIThread.Post(() => { if (!_closed && first.IsEffectivelyVisible) first.BringIntoView(); },
                DispatcherPriority.Loaded);
    }

    private async void ReadClicked(object? sender, RoutedEventArgs args) =>
        await RunAsync(() => _unconfirmed is not null ? VerifyAsync() : ReadAsync(Subject?.Id));

    private async void SubjectSetupClicked(object? sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        await _subjectSetup.OpenAsync(this, _client, _school, _termId, _classId, [_classId], error => SessionExpired?.Invoke(error));
        if (!_closed) await ReadAsync(null);
    });

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
            if (error.StatusCode == 401) SessionExpired?.Invoke(error);
        }
        finally { _busy = false; if (!_closed) UpdateControls(); }
    }

    private void CloseClicked(object? sender, RoutedEventArgs args) => Close();
    public void CloseForScopeChange() { _closed = true; Close(); }
}
