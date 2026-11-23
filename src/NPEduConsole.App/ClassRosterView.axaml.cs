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

public sealed partial class ClassRosterView : UserControl
{
    private readonly ObservableCollection<RosterDraftRow> _rows = [];
    private ConsoleClient? _client;
    private string _schoolId = "";
    private string _schoolName = "";
    private string _classId = "";
    private Classroom _room = null!;
    private ClassCreationContext? _specifiedTerm;
    private bool _currentEntry, _legacyRoster, _archiveEditing, _importOpen;
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
    private RosterDraftRow? _selected;

    public event Action<ConsoleApiException>? SessionExpired;
    internal Action? CloseRequested { get; set; }
    internal bool HasConfirmedSave { get; private set; }

    public ClassRosterView()
    {
        InitializeComponent();
        RosterRows.ItemsSource = _rows;
        RosterImportMode.SelectedIndex = 0;
        AddHandler(KeyDownEvent, CloseOnEscape, RoutingStrategies.Tunnel);
    }

    internal ClassRosterView(ConsoleClient client, School school, Classroom room,
        ClassCreationContext? specifiedTerm = null) : this()
    {
        _client = client;
        _schoolId = school.Id;
        _schoolName = school.Name;
        _classId = room.Id;
        _room = room;
        _specifiedTerm = specifiedTerm;
        _currentEntry = specifiedTerm is null;
        ShowRosterIdentity();
    }

    internal async Task StartAsync()
    {
        await LoadAsync(compare: false);
        if (_closed) return;
        var action = RosterCorrectArchived.IsEffectivelyVisible ? RosterCorrectArchived :
            OpenRosterImport.IsEffectivelyVisible ? OpenRosterImport : CloseRosterButton;
        action.Focus(NavigationMethod.Tab);
    }

    internal void End()
    {
        if (_closed) return;
        _closed = true;
        var request = _request;
        _request = null;
        request?.Cancel();
        ClearRoster();
        _client = null;
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
            if (_currentEntry && !_legacyRoster && !compare)
            {
                try
                {
                    var directory = await client.GetTermClassStructureAsync(_schoolId, _room.TermId!, request.Token);
                    if (_request != request) return;
                    if (directory.Term.Status != "ACTIVE" || !directory.Classes.Any(room => room.Id == _classId))
                        throw new ConsoleApiException(409, "当前启用学期或班级已变化，请返回班级目录刷新。", "CLASS_ROSTER_TERM_CHANGED");
                    _specifiedTerm = directory.Creation;
                    _room = _room with { GradeName = directory.Classes.Single(room => room.Id == _classId).GradeName };
                }
                catch (ConsoleApiException error) when (error.Code == "TERM_STRUCTURE_UNSUPPORTED") { _legacyRoster = true; }
            }
            var roster = _specifiedTerm is { } context
                ? await client.GetTermClassRosterAsync(context, _classId, request.Token, compare)
                : await client.GetClassRosterAsync(_schoolId, _classId, request.Token);
            if (_request != request) return;
            if (!compare)
            {
                SetBaseline(roster);
                SetFeedback(!roster.IsActive ? "该行政班已停用，名单仅供查看。" : _legacyRoster
                    ? "旧版学校服务只能核对当前班级名单。页头名称、代码取自进入时的班级目录。" : null);
            }
            else if (CanAdoptRoster(roster) && _baseline is { } baseline && _uncertainSubmission is { } submitted && RosterEditor.MatchesSaved(baseline, submitted, roster) &&
                     _rawSubmittedDraft is { } raw && Rows().SequenceEqual(raw))
            {
                SetBaseline(roster);
                HasConfirmedSave = true;
                SetFeedback("已核对，服务器名单与本次提交一致。");
            }
            else
            {
                if (!CanAdoptRoster(roster))
                    _scopeChanged = ClassIdentityChanged(roster)
                        ? $"班级资料已变更为「{roster.ClassName}」（{roster.ClassCode}）。"
                        : "学期或班级状态已变化。";
                SetCurrent(roster);
                _uncertainSubmission = _rawSubmittedDraft = null;
                SetFeedback(_scopeChanged is { } reason
                    ? $"{reason} 可核对当前名单并复制草稿，再返回班级列表。"
                    : $"{_checkReason} 已读取服务器当前名单。请逐行核对，再以它为基准重新预览。", error: _scopeChanged is not null);
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (ConsoleApiException error)
        {
            if (_request == request && !HandlePermissionLoss(error))
            {
                if (error.Code is "CLASS_ROSTER_TERM_CHANGED" or "CLASS_ROSTER_CLASS_INACTIVE" or "TERM_STRUCTURE_SCOPE_CHANGED")
                {
                    _scopeChanged = error.Message;
                    _checkingRequired = _baseline is not null;
                    SetCurrent(null);
                }
                SetFeedback(compare ? $"暂时无法核对当前名单，草稿仍保留。{error.Message}" : error.Message, error: true);
            }
        }
        finally { EndRequest(request); }
    }

    private bool CanAdoptRoster(ClassRoster roster) => roster.IsActive &&
        (_specifiedTerm is null || ConsoleClient.IsInTermStructure(roster, _specifiedTerm) && !ClassIdentityChanged(roster));

    private bool ClassIdentityChanged(ClassRoster roster) => _specifiedTerm is not null && _baseline is { } before &&
        (before.ClassName != roster.ClassName || before.ClassCode != roster.ClassCode);

    private void ShowRosterIdentity(ClassRoster? roster = null)
    {
        string term = _specifiedTerm is { } context ? $"{context.TermName} · {context.TermStatus}" : "当前启用学期";
        RosterScope.Text = $"{_schoolName} / {term} / {_room.GradeName ?? "未设年级"} / " +
            $"{roster?.ClassName ?? _room.Name}（{roster?.ClassCode ?? _room.Code ?? "代码待核对"}）";
    }

    private void SetBaseline(ClassRoster roster)
    {
        _baseline = roster;
        ShowRosterIdentity(roster);
        SetCurrent(null);
        ClearPreview();
        _uncertainSubmission = _rawSubmittedDraft = null;
        _checkingRequired = false;
        _checkReason = null;
        _scopeChanged = null;
        _importOpen = false;
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
        SessionExpired?.Invoke(error);
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
        SelectDraftRow(null);
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
        SelectDraftRow(row);
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

    private void SelectStudentRow(object? sender, FocusChangedEventArgs args)
    {
        if (sender is Control { DataContext: RosterDraftRow row }) SelectDraftRow(row);
    }

    private void SelectDraftRow(RosterDraftRow? row)
    {
        if (_selected == row) return;
        if (_selected is not null) _selected.IsCompared = false;
        _selected = row;
        if (row is not null) row.IsCompared = true;
        Render();
    }

    private void ToggleImportClicked(object? sender, RoutedEventArgs args)
    {
        _importOpen = !_importOpen;
        Render();
        if (_importOpen) RosterPasteText.Focus(NavigationMethod.Tab);
    }

    private void EnableCorrectionClicked(object? sender, RoutedEventArgs args)
    {
        _archiveEditing = true;
        Render();
        AddRosterStudent.Focus(NavigationMethod.Tab);
    }

    private void MoveUpClicked(object? sender, RoutedEventArgs args) => Move(-1);
    private void MoveDownClicked(object? sender, RoutedEventArgs args) => Move(1);

    private void Move(int delta)
    {
        if (_selected is not { } row) return;
        int index = _rows.IndexOf(row);
        if (index < 0 || index + delta < 0 || index + delta >= _rows.Count) return;
        _rows.Move(index, index + delta);
        UpdateRowPositions();
        BringRosterRowIntoView(row);
    }

    private void RemoveClicked(object? sender, RoutedEventArgs args)
    {
        if (_selected is not { } row) return;
        RemoveDraftRow(row);
        if (_selected is null) AddRosterStudent.Focus(NavigationMethod.Tab);
        else BringRosterRowIntoView(_selected, _selected.NameEditorId);
    }

    private void RemoveDraftRow(RosterDraftRow row)
    {
        int index = _rows.IndexOf(row);
        if (index < 0) return;
        _rows.RemoveAt(index);
        row.PropertyChanged -= RowChanged;
        if (_selected == row) SelectDraftRow(_rows.Count == 0 ? null : _rows[Math.Min(index, _rows.Count - 1)]);
        UpdateRowPositions();
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
            _importOpen = false;
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
            SetFeedback(_specifiedTerm is { } term
                ? $"已保存「{term.TermName}」的「{saved.ClassName}」名单。"
                : "名单已保存。在线大屏将在同步时更新。");
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
            if (error.Code is "CLASS_ROSTER_TERM_CHANGED" or "CLASS_ROSTER_CLASS_INACTIVE" or "TERM_STRUCTURE_SCOPE_CHANGED")
                _scopeChanged = error.Message;
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
        ShowComparison();
    }

    private void ShowComparison(string? selectKey = null)
    {
        var rows = new List<RosterComparisonRow>();
        if (_current is { } current)
        {
            var draftById = _rows.Select((row, index) => (row, Position: index + 1))
                .Where(entry => entry.row.Id is not null).ToDictionary(entry => entry.row.Id!, StringComparer.Ordinal);
            var serverIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < current.Students.Count; i++)
            {
                var student = current.Students[i];
                serverIds.Add(student.Id!);
                draftById.TryGetValue(student.Id!, out var draft);
                rows.Add(new(student.Id!, student, draft.row?.ToStudent(), i + 1, draft.row is null ? null : draft.Position));
            }
            for (int i = 0; i < _rows.Count; i++)
            {
                var draft = _rows[i];
                if (draft.Id is null || !serverIds.Contains(draft.Id))
                    rows.Add(new(draft.Id ?? draft.NameEditorId, null, draft.ToStudent(), null, i + 1));
            }
        }
        var visible = rows.OrderByDescending(row => row.Different).ToArray();
        CurrentRosterRows.ItemsSource = visible;
        CurrentRosterRows.SelectedItem = visible.FirstOrDefault(row => row.Key == selectKey) ?? visible.FirstOrDefault(row => row.Different);
    }

    private void CurrentSelectionChanged(object? sender, SelectionChangedEventArgs args) => Render();

    private void UseCurrentStudentClicked(object? sender, RoutedEventArgs args)
    {
        if (_request is not null || _scopeChanged is not null || CurrentRosterRows.SelectedItem is not RosterComparisonRow selected) return;
        var existing = _rows.FirstOrDefault(row => row.Id == selected.Key || row.NameEditorId == selected.Key);
        if (selected.Server is not { } student)
        {
            if (existing is null) return;
            RemoveDraftRow(existing);
        }
        else if (existing is not null)
        {
            existing.Name = student.Name;
            existing.StudentNumber = student.StudentNumber ?? "";
            int position = Math.Min(selected.ServerPosition!.Value - 1, _rows.Count - 1);
            _rows.Move(_rows.IndexOf(existing), position);
            UpdateRowPositions();
        }
        else if (_rows.Count < RosterEditor.MaxStudents)
        {
            var added = AppendRow(student);
            _rows.Move(_rows.IndexOf(added), Math.Min(selected.ServerPosition!.Value - 1, _rows.Count - 1));
            UpdateRowPositions();
        }
        else
        {
            SetFeedback("草稿已达 120 人，请先核对并移出不再保留的记录。", error: true);
            return;
        }
        ShowComparison(selected.Key);
        CurrentRosterRows.Focus(NavigationMethod.Tab);
        SetFeedback("已按服务器处理这位学生的草稿。名单尚未保存。");
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
        Render();
        if (IsDirty) PreviewClicked(sender, args);
        else
        {
            SetFeedback("当前草稿和服务器一致，无需保存。");
            CloseRosterButton.Focus(NavigationMethod.Tab);
        }
    }

    private async void CopyDraftClicked(object? sender, RoutedEventArgs args)
    {
        if (_baseline is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
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

    private void CloseClicked(object? sender, RoutedEventArgs args) => CloseRequested?.Invoke();

    private void CloseOnEscape(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape || RosterImportMode.IsDropDownOpen) return;
        args.Handled = true;
        if (_discard != DiscardAction.None) KeepEditingClicked(sender, args);
        else if (_preview is not null && !_saving) CancelReviewClicked(sender, args);
        else CloseRequested?.Invoke();
    }

    internal bool CanClose()
    {
        if (_allowClose) return true;
        if (_saving)
        {
            SetFeedback("正在保存，请等待学校返回结果。");
            return false;
        }
        if (IsDirty)
        {
            AskToDiscard(DiscardAction.Close);
            return false;
        }
        return true;
    }

    private void AskToDiscard(DiscardAction action)
    {
        _discard = action;
        RosterDiscardWarning.Text = action == DiscardAction.Close ? "离开此页会丢弃未保存的名单草稿。" : "重新载入学校名单会替换未保存的草稿。";
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
        if (action == DiscardAction.Close)
        {
            _allowClose = true;
            CloseRequested?.Invoke();
        }
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
        bool archived = _specifiedTerm?.TermStatus == "已归档";
        bool readOnly = _baseline?.IsActive == false || archived && !_archiveEditing;
        RosterEditorPane.IsVisible = loaded && !reviewing && !_checkingRequired;
        RosterEditorPane.IsEnabled = !busy;
        RosterContentHost.IsEnabled = !confirmingDiscard;
        RosterReviewPane.IsVisible = reviewing;
        RosterCount.Text = !readOnly && IsDirty ? $"草稿 · {_rows.Count} 人" : $"学生名单 · {_rows.Count} 人";
        EmptyRosterDraft.IsVisible = _rows.Count == 0;
        EmptyRosterDraft.Text = readOnly ? "此班名单为空。" : "暂无学生。可添加单行，或从 Excel 批量粘贴。";
        AddRosterStudent.IsVisible = !readOnly;
        AddRosterStudent.IsEnabled = !readOnly && _rows.Count < RosterEditor.MaxStudents;
        RosterCorrectArchived.IsVisible = archived && !_archiveEditing && _baseline?.IsActive == true;
        ApplyRosterImport.IsEnabled = HasPaste;
        RosterRows.IsEnabled = !readOnly;
        OpenRosterImport.IsVisible = !readOnly;
        RosterImportPanel.IsVisible = _importOpen && !readOnly;
        RosterSelectedActions.IsVisible = !readOnly && _selected is not null;
        SelectedRosterStudent.Text = _selected is { } row
            ? $"第 {row.Position} 行 · {row.Name} · 学号 {(row.StudentNumber.Length > 0 ? row.StudentNumber : "无")}" : "选择一位学生以调整或移出";
        MoveRosterStudentUp.IsEnabled = _selected?.CanMoveUp == true;
        MoveRosterStudentDown.IsEnabled = _selected?.CanMoveDown == true;
        RemoveRosterStudent.IsEnabled = _selected is not null;
        AutomationProperties.SetAutomationId(MoveRosterStudentUp, _selected?.MoveUpId);
        AutomationProperties.SetAutomationId(MoveRosterStudentDown, _selected?.MoveDownId);
        RosterComparisonPane.IsVisible = _checkingRequired;
        RosterComparisonHint.Text = _current switch
        {
            { } when _scopeChanged is not null => "可核对学校当前名单。请返回班级列表重新选择可编辑的班级。",
            { } => $"服务器 {_current.Students.Count} 人 · 草稿 {_rows.Count} 人 · {CurrentRosterRows.Items.OfType<RosterComparisonRow>().Count(row => row.Different)} 行不同",
            _ => "尚未核实当前名单。可重新核对或复制草稿。"
        };
        RecheckRosterButton.IsEnabled = !busy;
        var selected = CurrentRosterRows.SelectedItem as RosterComparisonRow;
        UseCurrentStudentButton.Content = selected is { Server: null } ? "从草稿移出此行" : "此行采用服务器内容";
        UseCurrentStudentButton.IsEnabled = !busy && _scopeChanged is null && selected?.Different == true;
        AdoptRosterRevisionButton.IsEnabled = !busy && _current is not null && _scopeChanged is null;
        ReloadRosterButton.IsVisible = !_checkingRequired && !reviewing;
        ReloadRosterButton.IsEnabled = !busy && !_permissionLost && !confirmingDiscard && _scopeChanged is null;
        RosterDraftState.Text = loaded ? (readOnly ? "只读名单" : IsDirty ? "有未保存的修改" : "与学校当前名单一致") : null;
        RosterMainActions.IsVisible = !confirmingDiscard;
        RosterDiscardActions.IsVisible = confirmingDiscard;
        CopyRosterDraftButton.IsVisible = loaded && !readOnly && !reviewing;
        CopyRosterDraftButton.IsEnabled = !busy && _rows.Count > 0;
        CloseRosterButton.IsEnabled = !_saving;
        PreviewRosterButton.IsVisible = loaded && !readOnly && !reviewing && !_checkingRequired;
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
