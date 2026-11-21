using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed partial class ClassRosterWindow : Window
{
    private readonly ObservableCollection<RosterDraftRow> _rows = [];
    private ConsoleClient? _client;
    private string _schoolId = "";
    private string _classId = "";
    private CancellationTokenSource? _request;
    private ClassRoster? _baseline;
    private ClassRoster? _current;
    private IReadOnlyList<RosterStudent>? _preview;
    private IReadOnlyList<RosterStudent>? _uncertainSubmission;
    private IReadOnlyList<RosterStudent>? _rawSubmittedDraft;
    private bool _checkingRequired;
    private string? _checkReason;
    private string? _scopeChanged;
    private bool _saving;
    private bool _permissionLost;
    private bool _allowClose;
    private bool _closed;
    private DiscardAction _discard;

    private enum DiscardAction { None, Close, Reload }
    private RosterDraftRow? _compared;

    public event Action<ConsoleApiException>? SessionExpired;
    internal bool HasConfirmedSave { get; private set; }

    public ClassRosterWindow()
    {
        InitializeComponent();
        RosterRows.ItemsSource = _rows;
        RosterImportMode.SelectedIndex = 0;
        Closing += ConfirmClose;
        Closed += (_, _) =>
        {
            _closed = true;
            var request = _request;
            _request = null;
            request?.Cancel();
            ClearRoster();
            _client = null;
        };
        AddHandler(KeyDownEvent, CloseOnEscape, RoutingStrategies.Tunnel);
    }

    internal ClassRosterWindow(ConsoleClient client, string schoolId, string classId, string schoolName, string className) : this()
    {
        _client = client;
        _schoolId = schoolId;
        _classId = classId;
        RosterScope.Text = $"{schoolName} / {className}";
        Opened += async (_, _) =>
        {
            BatchWorkspaceWindow.FitSchoolWindow(this);
            await LoadAsync(compare: false);
        };
    }

    internal void CloseForScopeChange()
    {
        _allowClose = true;
        Close();
    }

    private async Task LoadAsync(bool compare)
    {
        if (_client is not { } client || _request is not null || _permissionLost) return;
        using var request = new CancellationTokenSource();
        _request = request;
        if (compare) SetCurrent(null);
        SetFeedback(compare ? "正在核对学校当前名单…" : "正在读取学生名单…");
        Render();
        try
        {
            var roster = await client.GetClassRosterAsync(_schoolId, _classId, request.Token);
            if (_request != request) return;
            if (!compare)
            {
                SetBaseline(roster);
                SetFeedback(null);
            }
            else if (_baseline is { } baseline && _uncertainSubmission is { } submitted && RosterEditor.MatchesSaved(baseline, submitted, roster) &&
                     _rawSubmittedDraft is { } raw && Rows().SequenceEqual(raw))
            {
                SetBaseline(roster);
                HasConfirmedSave = true;
                SetFeedback("已核对，服务器名单与本次提交一致。");
            }
            else
            {
                SetCurrent(roster);
                _uncertainSubmission = _rawSubmittedDraft = null;
                SetFeedback(_scopeChanged is { } reason
                    ? $"{reason} 可核对当前名单并复制草稿，再返回班级列表。"
                    : $"{_checkReason} 已读取学校当前名单。请核对两份名单，再决定是否采用当前版本继续编辑。", error: _scopeChanged is not null);
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (ConsoleApiException error)
        {
            if (_request == request && !HandlePermissionLoss(error))
                SetFeedback(compare ? $"暂时无法核对当前名单，草稿仍保留。{error.Message}" : error.Message, error: true);
        }
        finally { EndRequest(request); }
    }

    private void SetBaseline(ClassRoster roster)
    {
        _baseline = roster;
        SetCurrent(null);
        ClearPreview();
        _uncertainSubmission = _rawSubmittedDraft = null;
        _checkingRequired = false;
        _checkReason = null;
        _scopeChanged = null;
        RosterPasteText.Clear();
        RosterImportMode.SelectedIndex = 0;
        SetRows(roster.Students);
    }

    private void ClearRoster()
    {
        _baseline = null;
        SetCurrent(null);
        _preview = _uncertainSubmission = _rawSubmittedDraft = null;
        _checkingRequired = false;
        RosterPasteText.Clear();
        SetRows([]);
        RosterChangesList.ItemsSource = null;
    }

    private bool HandlePermissionLoss(ConsoleApiException error)
    {
        if (error.StatusCode is not (401 or 403)) return false;
        _permissionLost = true;
        ClearRoster();
        SetFeedback(error.StatusCode == 403 ? "当前账号已无权查看这个班级的名单。" : "登录已失效，请重新登录。", error: true);
        if (error.StatusCode == 401) SessionExpired?.Invoke(error);
        return true;
    }

    private void EndRequest(CancellationTokenSource request)
    {
        if (_request != request) return;
        _request = null;
        _saving = false;
        Render();
    }

    private RosterStudent[] Rows() => _rows.Select(row => row.ToStudent()).ToArray();
    private bool HasPaste => !string.IsNullOrWhiteSpace(RosterPasteText.Text);
    private bool IsDirty => _baseline is { } roster && (HasPaste || !Rows().SequenceEqual(roster.Students.Select(row =>
        row.StudentNumber == "" ? row with { StudentNumber = null } : row)));

    private void SetRows(IEnumerable<RosterStudent> students)
    {
        foreach (var row in _rows) row.PropertyChanged -= RowChanged;
        _rows.Clear();
        foreach (var student in students) AppendRow(student);
        UpdateRowPositions();
    }

    private RosterDraftRow AppendRow(RosterStudent student)
    {
        var row = new RosterDraftRow(student);
        row.PropertyChanged += RowChanged;
        _rows.Add(row);
        return row;
    }

    private void UpdateRowPositions()
    {
        for (int i = 0; i < _rows.Count; i++) _rows[i].SetPosition(i + 1, _rows.Count);
        Render();
    }

    private void RowChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(RosterDraftRow.Name) or nameof(RosterDraftRow.StudentNumber)) Render();
    }

    private void DraftTextChanged(object? sender, TextChangedEventArgs args) => Render();

    private void AddClicked(object? sender, RoutedEventArgs args)
    {
        if (_rows.Count >= RosterEditor.MaxStudents) return;
        var row = AppendRow(new(null, "", null));
        UpdateRowPositions();
        BringRosterRowIntoView(row, row.NameEditorId);
    }

    private void BringRosterRowIntoView(RosterDraftRow row, string? focusId = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed || !_rows.Contains(row)) return;
            var controls = RosterRows.GetVisualDescendants().OfType<Control>();
            var editor = controls.FirstOrDefault(control => AutomationProperties.GetAutomationId(control) == (focusId ?? row.NameEditorId));
            if (editor is null || !editor.IsEffectivelyVisible) return;
            editor.BringIntoView();
            if (focusId is null) return;
            if (!editor.IsEffectivelyEnabled)
                editor = RosterRows.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(box =>
                    AutomationProperties.GetAutomationId(box) == row.NameEditorId);
            editor?.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private void MoveUpClicked(object? sender, RoutedEventArgs args) => Move(sender, -1);
    private void MoveDownClicked(object? sender, RoutedEventArgs args) => Move(sender, 1);

    private void Move(object? sender, int delta)
    {
        if (sender is not Button { DataContext: RosterDraftRow row }) return;
        int index = _rows.IndexOf(row);
        if (index < 0 || index + delta < 0 || index + delta >= _rows.Count) return;
        _rows.Move(index, index + delta);
        UpdateRowPositions();
        BringRosterRowIntoView(row, delta < 0 ? row.MoveUpId : row.MoveDownId);
    }

    private void RemoveClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is not Button { DataContext: RosterDraftRow row }) return;
        int index = _rows.IndexOf(row);
        if (index < 0) return;
        _rows.RemoveAt(index);
        row.PropertyChanged -= RowChanged;
        if (_compared == row) _compared = null;
        UpdateRowPositions();
        if (_rows.Count == 0) AddRosterStudent.Focus(NavigationMethod.Tab);
        else
        {
            var neighbor = _rows[Math.Min(index, _rows.Count - 1)];
            BringRosterRowIntoView(neighbor, neighbor.NameEditorId);
        }
    }

    private void ImportModeChanged(object? sender, SelectionChangedEventArgs args)
    {
        bool replace = RosterImportMode.SelectedIndex == 1;
        RosterImportHint.Text = replace ? "确认保存后，未列出的学生会移出，历史考勤仍保留。" : "有学号按学号匹配，无学号按姓名匹配，其他学生保留。";
        ApplyRosterImport.Content = replace ? "替换草稿" : "应用到草稿";
    }

    private void ApplyImportClicked(object? sender, RoutedEventArgs args)
    {
        try
        {
            SetRows(RosterEditor.Import(RosterPasteText.Text ?? "", Rows(), replace: RosterImportMode.SelectedIndex == 1));
            RosterPasteText.Clear();
            SetFeedback("已应用到草稿。预览并确认保存后才会更新学校名单。");
        }
        catch (ArgumentException error) { SetFeedback(error.Message, error: true); }
        Render();
    }

    private void PreviewClicked(object? sender, RoutedEventArgs args)
    {
        if (_baseline is not { } baseline || _checkingRequired || _request is not null) return;
        if (HasPaste)
        {
            SetFeedback("还有未应用的粘贴内容，请先应用到草稿或清空粘贴区。", error: true);
            return;
        }
        try
        {
            var draft = RosterEditor.Validate(Rows());
            var changes = RosterEditor.Compare(baseline.Students, draft);
            if (changes.Count == 0)
            {
                SetFeedback("名单没有变化。", error: true);
                return;
            }
            _preview = draft;
            RosterChangesList.ItemsSource = changes.Select(change => new RosterReviewRow(change)).ToArray();
            RosterReviewSummary.Text = $"{baseline.Students.Count} 人 → {draft.Count} 人 · " +
                $"新增 {changes.Count(change => change.Kind == RosterChangeKind.Add)} · 移出 {changes.Count(change => change.Kind == RosterChangeKind.Remove)} · " +
                $"修改 {changes.Count(change => change.Kind == RosterChangeKind.Edit)} · 调序 {changes.Count(change => change.Kind == RosterChangeKind.Move)}。移出时保留历史考勤。";
            SetFeedback(null);
            Render();
            CancelRosterReview.Focus();
        }
        catch (ArgumentException error) { SetFeedback(error.Message, error: true); }
    }

    private void CancelReviewClicked(object? sender, RoutedEventArgs args)
    {
        ClearPreview();
        Render();
        PreviewRosterButton.Focus();
    }

    private void ClearPreview()
    {
        _preview = null;
        RosterChangesList.ItemsSource = null;
    }

    private async void SaveClicked(object? sender, RoutedEventArgs args)
    {
        if (_client is not { } client || _baseline is not { } baseline || _preview is not { } preview || _request is not null || _checkingRequired) return;
        using var request = new CancellationTokenSource();
        _request = request;
        _saving = true;
        SetFeedback("正在保存学生名单…");
        Render();
        bool checkCurrent = false;
        try
        {
            var saved = await client.SaveClassRosterAsync(baseline, preview, request.Token);
            if (_request != request) return;
            SetBaseline(saved);
            HasConfirmedSave = true;
            SetFeedback("名单已保存。在线大屏将在同步时更新。");
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (ArgumentException error)
        {
            if (_request != request) return;
            ClearPreview();
            SetFeedback(error.Message, error: true);
        }
        catch (ConsoleApiException error)
        {
            if (_request != request || HandlePermissionLoss(error)) return;
            ClearPreview();
            bool conflicting = error.StatusCode is 409 or 428;
            bool uncertain = error.StatusCode is 0 or 408 or >= 500;
            if (error.Code is "CLASS_ROSTER_TERM_CHANGED" or "CLASS_ROSTER_CLASS_INACTIVE") _scopeChanged = error.Message;
            if (conflicting || uncertain || error.StatusCode == 404)
            {
                _checkingRequired = checkCurrent = true;
                _checkReason = uncertain ? "保存结果未确认。" : "名单版本或班级状态已变化。";
                if (uncertain)
                {
                    _uncertainSubmission = preview;
                    _rawSubmittedDraft = Rows();
                }
            }
            SetFeedback(error.Message, error: true);
        }
        finally { EndRequest(request); }

        if (checkCurrent) await LoadAsync(compare: true);
    }

    private async void RecheckClicked(object? sender, RoutedEventArgs args) => await LoadAsync(compare: true);

    private void SetCurrent(ClassRoster? current)
    {
        _current = current;
        CurrentRosterRows.ItemsSource = current?.Students.Select((student, index) => new RosterCurrentRow(index + 1, student)).ToArray();
    }

    private void CurrentSelectionChanged(object? sender, SelectionChangedEventArgs args) => HighlightCurrentStudent();

    private void HighlightCurrentStudent()
    {
        if (_compared is not null) _compared.IsCompared = false;
        _compared = CurrentRosterRows.SelectedItem is RosterCurrentRow { Student.Id: { } id }
            ? _rows.FirstOrDefault(row => row.Id == id) : null;
        if (_compared is { } match)
        {
            match.IsCompared = true;
            BringRosterRowIntoView(match);
        }
        Render();
    }

    private void UseCurrentStudentClicked(object? sender, RoutedEventArgs args)
    {
        if (_request is not null || CurrentRosterRows.SelectedItem is not RosterCurrentRow selected) return;
        var student = selected.Student;
        var existing = _rows.FirstOrDefault(row => row.Id == student.Id);
        if (existing is not null)
        {
            existing.Name = student.Name;
            existing.StudentNumber = student.StudentNumber ?? "";
        }
        else if (_rows.Count < RosterEditor.MaxStudents)
        {
            AppendRow(student);
            UpdateRowPositions();
            HighlightCurrentStudent();
        }
        else
        {
            SetFeedback("草稿已达 120 人，请先核对并移出不再保留的记录。", error: true);
            return;
        }
        SetFeedback("已将学校当前名单中的选中行用到草稿。");
    }

    private void AdoptRevisionClicked(object? sender, RoutedEventArgs args)
    {
        if (_current is not { } current || _request is not null || _scopeChanged is not null) return;
        var currentIds = current.Students.Select(row => row.Id).ToHashSet();
        if (_rows.Any(row => row.Id is not null && !currentIds.Contains(row.Id)))
        {
            SetFeedback("草稿含有已从学校名单移出的学生。请先从草稿移出对应行。如需重新加入，请添加新行。", error: true);
            return;
        }
        _baseline = current;
        SetCurrent(null);
        _checkingRequired = false;
        _checkReason = null;
        _uncertainSubmission = _rawSubmittedDraft = null;
        SetFeedback("已采用当前版本并保留草稿。再次预览时会以学校当前名单为准。");
        Render();
        PreviewRosterButton.Focus();
    }

    private async void CopyDraftClicked(object? sender, RoutedEventArgs args)
    {
        if (_baseline is null || Clipboard is not { } clipboard) return;
        var draft = string.Join(Environment.NewLine, _rows.Select(row => $"{row.StudentNumber}\t{row.Name}"));
        try
        {
            await clipboard.SetTextAsync(draft);
            if (!_closed && !_permissionLost) SetFeedback("表格草稿已复制，可粘贴到 Excel 或文本编辑器。");
        }
        catch (Exception error) when (error is InvalidOperationException or PlatformNotSupportedException)
        {
            if (!_closed && !_permissionLost) SetFeedback("暂时无法访问剪贴板，请稍后重试。", error: true);
        }
    }

    private async void ReloadClicked(object? sender, RoutedEventArgs args)
    {
        if (_request is not null || _scopeChanged is not null) return;
        if (IsDirty) AskToDiscard(DiscardAction.Reload);
        else await LoadAsync(compare: false);
    }

    private void CloseClicked(object? sender, RoutedEventArgs args) => Close();

    private void CloseOnEscape(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape || RosterImportMode.IsDropDownOpen) return;
        args.Handled = true;
        if (_discard != DiscardAction.None) KeepEditingClicked(sender, args);
        else if (_preview is not null && !_saving) CancelReviewClicked(sender, args);
        else Close();
    }

    private void ConfirmClose(object? sender, WindowClosingEventArgs args)
    {
        if (_allowClose) return;
        if (_saving)
        {
            args.Cancel = true;
            SetFeedback("正在保存，请等待学校返回结果。");
        }
        else if (IsDirty)
        {
            args.Cancel = true;
            AskToDiscard(DiscardAction.Close);
        }
    }

    private void AskToDiscard(DiscardAction action)
    {
        _discard = action;
        RosterDiscardWarning.Text = action == DiscardAction.Close ? "关闭窗口会丢弃未保存的名单草稿。" : "重新载入学校名单会替换未保存的草稿。";
        Render();
        KeepRosterEditing.Focus();
    }

    private void KeepEditingClicked(object? sender, RoutedEventArgs args)
    {
        _discard = DiscardAction.None;
        Render();
        CloseRosterButton.Focus();
    }

    private async void DiscardClicked(object? sender, RoutedEventArgs args)
    {
        var action = _discard;
        _discard = DiscardAction.None;
        if (action == DiscardAction.Close) CloseForScopeChange();
        else if (action == DiscardAction.Reload) await LoadAsync(compare: false);
    }

    private void SetFeedback(string? message, bool error = false)
    {
        RosterFeedback.Text = message;
        RosterFeedback.IsVisible = message is not null;
        RosterFeedback.Classes.Set("roster-error", error);
    }

    private void Render()
    {
        if (_closed) return;
        bool loaded = _baseline is not null;
        bool busy = _request is not null;
        bool reviewing = _preview is not null;
        bool confirmingDiscard = _discard != DiscardAction.None;
        RosterEditorPane.IsVisible = loaded && !reviewing;
        RosterEditorPane.IsEnabled = !busy;
        RosterContentHost.IsEnabled = !confirmingDiscard;
        RosterReviewPane.IsVisible = reviewing;
        RosterCount.Text = $"草稿 · {_rows.Count} 人";
        EmptyRosterDraft.IsVisible = _rows.Count == 0;
        AddRosterStudent.IsEnabled = _rows.Count < RosterEditor.MaxStudents;
        ApplyRosterImport.IsEnabled = HasPaste;
        RosterImportPane.IsVisible = !_checkingRequired;
        RosterComparisonPane.IsVisible = _checkingRequired;
        RosterComparisonHint.Text = _current switch
        {
            { Students.Count: 0 } => "学校当前名单为空。左侧保留你的草稿。",
            { } when _scopeChanged is not null => "可核对学校当前名单。请返回班级列表重新选择可编辑的班级。",
            { } => $"学校当前有 {_current.Students.Count} 人。选中学生可定位草稿中的同一学生。",
            _ => "尚未核实当前名单。可重新核对或复制草稿。"
        };
        RecheckRosterButton.IsEnabled = !busy;
        UseCurrentStudentButton.IsEnabled = !busy && CurrentRosterRows.SelectedItem is RosterCurrentRow;
        AdoptRosterRevisionButton.IsEnabled = !busy && _current is not null && _scopeChanged is null;
        ReloadRosterButton.IsEnabled = !busy && !_permissionLost && !confirmingDiscard && !reviewing && _scopeChanged is null;
        RosterDraftState.Text = loaded ? (IsDirty ? "有未保存的修改" : "草稿与当前版本一致") : null;
        RosterMainActions.IsVisible = !confirmingDiscard;
        RosterDiscardActions.IsVisible = confirmingDiscard;
        CopyRosterDraftButton.IsVisible = loaded && !reviewing;
        CopyRosterDraftButton.IsEnabled = !busy && _rows.Count > 0;
        CloseRosterButton.IsEnabled = !_saving;
        PreviewRosterButton.IsVisible = loaded && !reviewing;
        PreviewRosterButton.IsEnabled = !busy && !_checkingRequired && IsDirty;
        CancelRosterReview.IsVisible = ConfirmRosterSave.IsVisible = reviewing;
        CancelRosterReview.IsEnabled = ConfirmRosterSave.IsEnabled = !busy;
    }
}

internal sealed class RosterDraftRow(RosterStudent student) : INotifyPropertyChanged
{
    private readonly string _rowKey = student.Id ?? Guid.NewGuid().ToString("N");
    public string? Id { get; } = student.Id;
    public string NameEditorId => $"RosterStudentName-{_rowKey}";
    public string NumberEditorId => $"RosterStudentNumber-{_rowKey}";
    public string MoveUpId => $"RosterStudentUp-{_rowKey}";
    public string MoveDownId => $"RosterStudentDown-{_rowKey}";
    private bool _isCompared;
    public bool IsCompared
    {
        get => _isCompared;
        set
        {
            if (_isCompared == value) return;
            _isCompared = value;
            PropertyChanged?.Invoke(this, new(nameof(IsCompared)));
        }
    }
    private string _name = student.Name;
    private string _number = student.StudentNumber ?? "";

    public string Name
    {
        get => _name;
        set { value ??= ""; if (_name == value) return; _name = value; PropertyChanged?.Invoke(this, new(nameof(Name))); }
    }

    public string StudentNumber
    {
        get => _number;
        set { value ??= ""; if (_number == value) return; _number = value; PropertyChanged?.Invoke(this, new(nameof(StudentNumber))); }
    }

    public string Position { get; private set; } = "";
    public bool CanMoveUp { get; private set; }
    public bool CanMoveDown { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public RosterStudent ToStudent() => new(Id, Name, string.IsNullOrEmpty(StudentNumber) ? null : StudentNumber);

    public void SetPosition(int position, int count)
    {
        Position = position.ToString(CultureInfo.InvariantCulture);
        CanMoveUp = position > 1;
        CanMoveDown = position < count;
        PropertyChanged?.Invoke(this, new(nameof(Position)));
        PropertyChanged?.Invoke(this, new(nameof(CanMoveUp)));
        PropertyChanged?.Invoke(this, new(nameof(CanMoveDown)));
    }
}
