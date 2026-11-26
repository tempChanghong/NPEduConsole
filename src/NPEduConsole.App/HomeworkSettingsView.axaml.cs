using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed class HomeworkEditRow(string caption) : INotifyPropertyChanged
{
    public string Caption { get; private set; } = caption;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetCaption(string value)
    {
        if (Caption == value) return;
        Caption = value;
        PropertyChanged?.Invoke(this, new(nameof(Caption)));
    }
}

public sealed partial class HomeworkSettingsView : UserControl
{
    private const double CompactWidth = 880;
    private static readonly string[] Weekdays = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];
    private static readonly string[] Rules = Enumerable.Range(0, 15)
        .Select(day => day switch { 0 => "当天", 1 => "明天", 2 => "后天", _ => $"{day} 天后" })
        .Concat(Weekdays.Select(day => $"下{day}")).ToArray();
    private sealed record PendingSave(HomeworkSettings Baseline, HomeworkQuickInput[]? Words, HomeworkQuickDeadline[]? Deadlines, Guid Id);
    private static readonly ConditionalWeakTable<ConsoleClient, Dictionary<string, PendingSave>> PendingSaves = new();

    private readonly ConsoleClient _client = null!;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<HomeworkQuickInput> _words = [];
    private readonly List<HomeworkQuickDeadline> _deadlines = [];
    private readonly ObservableCollection<HomeworkEditRow> _rows = [];
    private HomeworkSettings? _baseline;
    private HomeworkSettings? _latest;
    private PendingSave? _pending;
    private Control? _lastWordEditorFocus, _lastDeadlineEditorFocus, _lastWordPreviewFocus, _lastDeadlinePreviewFocus;
    private bool _compact, _showDeadlines, _showPreview, _showReview, _binding, _working, _posting, _closed, _discard, _allowRetry;
    private int _wordIndex = -1, _deadlineIndex = -1;

    public Action<ConsoleApiException>? SessionExpired { get; init; }
    private bool WordsChanged => _baseline is { } b && (_words.Count != b.QuickInputs.Count ||
        !_words.Zip(b.QuickInputs).All(pair => SameWord(pair.First, pair.Second)));
    private bool DeadlinesChanged => _baseline is { } b && !_deadlines.SequenceEqual(b.QuickDeadlines);
    private bool WordsConflict => _baseline is { } b && _latest is { } latest && WordsChanged && b.Versions.QuickInputs != latest.Versions.QuickInputs;
    private bool DeadlinesConflict => _baseline is { } b && _latest is { } latest && DeadlinesChanged && b.Versions.QuickDeadlines != latest.Versions.QuickDeadlines;
    private bool CurrentConflict => _showDeadlines ? DeadlinesConflict : WordsConflict;
    private int RowCount => _showDeadlines ? _deadlines.Count : _words.Count;
    private int CurrentIndex
    {
        get => _showDeadlines ? _deadlineIndex : _wordIndex;
        set { if (_showDeadlines) _deadlineIndex = value; else _wordIndex = value; }
    }

    public HomeworkSettingsView() => InitializeComponent();

    internal HomeworkSettingsView(ConsoleClient client, School school) : this()
    {
        _client = client;
        HomeworkScope.Text = $"{school.Name} · 全校生效";
        HomeworkWordMode.ItemsSource = new[] { "插入文字", "换行" };
        HomeworkDeadlineRule.ItemsSource = Rules;
        HomeworkRows.ItemsSource = _rows;
        SchoolId = school.Id;
        if (PendingSaves.GetOrCreateValue(client).GetValueOrDefault(SchoolId) is { } unresolved)
        {
            _pending = unresolved;
            _baseline = _latest = unresolved.Baseline;
            _words.AddRange(unresolved.Words ?? unresolved.Baseline.QuickInputs);
            _deadlines.AddRange(unresolved.Deadlines ?? unresolved.Baseline.QuickDeadlines);
            PopulateRows();
        }
        SizeChanged += (_, _) => LayoutPanes();
        UpdateState();
    }

    private string SchoolId { get; } = "";

    internal Action? CloseRequested { get; set; }

    internal async Task StartAsync()
    {
        LayoutPanes();
        await RunAsync(ReadAsync);
    }

    internal bool CanClose()
    {
        if (_closed || _discard) return true;
        if (_posting)
        {
            Feedback("正在提交本校配置，请等服务器响应后再离开。", true);
            return false;
        }
        if (_pending is not null) return true;
        CaptureSelectedFields();
        if (!WordsChanged && !DeadlinesChanged) return true;
        HomeworkDiscard.IsVisible = true;
        Feedback("有未保存的修改。可以继续编辑，或放弃草稿。", true);
        return false;
    }

    internal void End()
    {
        if (_closed) return;
        _closed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (_working || _closed) return;
        var ct = _lifetime.Token;
        _working = true;
        UpdateState();
        try { await work(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (ConsoleApiException error)
        {
            if (!ct.IsCancellationRequested)
            {
                Feedback(_pending is null ? error.Message : "保存结果待核对。连接恢复后请核对原请求。", true);
                if (error.StatusCode == 401) SessionExpired?.Invoke(error);
            }
        }
        finally
        {
            _working = false;
            if (!_closed) UpdateState();
        }
    }

    private async Task ReadAsync(CancellationToken ct)
    {
        if (_pending is not null) { await ReconcileAsync(ct); return; }
        var settings = await _client.GetHomeworkSettingsAsync(SchoolId, ct);
        if (ct.IsCancellationRequested) return;
        ApplyRead(settings);
        if (WordsConflict || DeadlinesConflict) OpenReview();
        Feedback(WordsConflict || DeadlinesConflict ? "服务器有更新，请核对相应分区。草稿已保留。" : "");
    }

    private void ApplyRead(HomeworkSettings settings, bool savedWords = false, bool savedDeadlines = false)
    {
        bool keepWords = _baseline is not null && !savedWords && WordsChanged;
        bool keepDeadlines = _baseline is not null && !savedDeadlines && DeadlinesChanged;
        _latest = settings;
        _baseline = settings with
        {
            QuickInputs = keepWords ? _baseline!.QuickInputs : settings.QuickInputs,
            QuickDeadlines = keepDeadlines ? _baseline!.QuickDeadlines : settings.QuickDeadlines,
            Versions = new(keepWords ? _baseline!.Versions.QuickInputs : settings.Versions.QuickInputs,
                keepDeadlines ? _baseline!.Versions.QuickDeadlines : settings.Versions.QuickDeadlines)
        };
        if (!keepWords) { _words.Clear(); _words.AddRange(settings.QuickInputs); }
        if (!keepDeadlines) { _deadlines.Clear(); _deadlines.AddRange(settings.QuickDeadlines); }
        PopulateRows();
    }

    private async void ReadClicked(object? sender, RoutedEventArgs args) => await RunAsync(ReadAsync);

    private async void SaveClicked(object? sender, RoutedEventArgs args)
    {
        if (_working || _pending is not null || _baseline is null) return;
        CaptureSelectedFields();
        if (WordsConflict || DeadlinesConflict) return;
        if (!ValidateDraft()) return;
        var words = WordsChanged ? _words.Select(word => word with
        {
            Label = word.Label.Trim(), Group = word.Group.Trim(), Text = word.Text.Trim(), SubjectIds = word.SubjectIds.ToArray()
        }).ToArray() : null;
        var deadlines = DeadlinesChanged ? _deadlines.Select(row => row with { Label = row.Label.Trim(), Time = row.Time.Trim() }).ToArray() : null;
        if (words is null && deadlines is null) return;
        SetPending(new(_baseline, words, deadlines, Guid.NewGuid()));
        await RunAsync(WriteAsync);
    }

    private async void RetryClicked(object? sender, RoutedEventArgs args)
    {
        if (_pending is not null && _allowRetry) await RunAsync(WriteAsync);
    }

    private async Task WriteAsync(CancellationToken ct)
    {
        if (_pending is not { } request) return;
        _allowRetry = false;
        try
        {
            _posting = true;
            UpdateState();
            HomeworkSettingsReceipt receipt;
            try { receipt = await _client.SaveHomeworkSettingsAsync(request.Baseline, request.Words, request.Deadlines, request.Id, ct); }
            finally { _posting = false; if (!_closed) UpdateState(); }
            if (!ct.IsCancellationRequested) Accept(receipt, receipt.Settings, request, recovered: false);
        }
        catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or 429 or >= 500)
        {
            await ReconcileAsync(ct);
        }
        catch (ConsoleApiException error)
        {
            if (ct.IsCancellationRequested) return;
            SetPending(null);
            if (error.Code is "HOMEWORK_SETTINGS_CONFLICT" or "HOMEWORK_SETTINGS_INVALID")
            {
                var current = await _client.GetHomeworkSettingsAsync(SchoolId, ct);
                if (ct.IsCancellationRequested) return;
                ApplyRead(current);
                if (WordsConflict || DeadlinesConflict) OpenReview();
                else ValidateDraft();
            }
            Feedback(error.Message, true);
            if (error.StatusCode == 401) SessionExpired?.Invoke(error);
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        if (_pending is not { } request) return;
        _allowRetry = false;
        var receipt = await _client.GetHomeworkSettingsReceiptAsync(SchoolId, request.Id, ct);
        var latest = await _client.GetHomeworkSettingsAsync(SchoolId, ct);
        if (ct.IsCancellationRequested) return;
        if (receipt is not null) Accept(receipt, latest, request, recovered: true);
        else
        {
            ApplyRead(latest);
            _allowRetry = true;
            Feedback("尚未找到本次保存回执。已读取学校当前值，可继续核对或重试原请求。", true);
        }
    }

    private void Accept(HomeworkSettingsReceipt receipt, HomeworkSettings latest, PendingSave request, bool recovered)
    {
        SetPending(null);
        ApplyRead(latest, request.Words is not null, request.Deadlines is not null);
        bool advanced = (request.Words is not null && latest.Versions.QuickInputs != receipt.Settings.Versions.QuickInputs) ||
            (request.Deadlines is not null && latest.Versions.QuickDeadlines != receipt.Settings.Versions.QuickDeadlines);
        Feedback(advanced ? "已找到本次保存回执。界面显示服务器后续更新的配置。" :
            recovered ? "已找回本次保存回执。刷新后的教师端与大屏将使用新配置。" :
            receipt.Applied.Count == 0 ? "服务器配置与本次提交一致。" : "已保存。刷新后的教师端与大屏将使用新配置。");
    }

    private void SetPending(PendingSave? request)
    {
        _pending = request;
        var pending = PendingSaves.GetOrCreateValue(_client);
        if (request is null) pending.Remove(SchoolId);
        else pending[SchoolId] = request;
    }

    private bool ValidateDraft()
    {
        if (WordsChanged)
        {
            var known = (_latest?.Subjects ?? []).Select(subject => subject.Id).ToHashSet(StringComparer.Ordinal);
            int index = _words.FindIndex(word => word.Label.Trim().Length == 0 ||
                word.InsertMode == "INLINE" && word.Text.Trim().Length == 0 ||
                word.SubjectIds.Count > 32 || word.SubjectIds.Any(id => !known.Contains(id)));
            if (index >= 0)
            {
                SelectSection(preferDeadlines: false);
                HomeworkRows.SelectedIndex = index;
                HomeworkRows.ScrollIntoView(index);
                HomeworkLabel.Focus(NavigationMethod.Tab);
                Feedback($"快捷词第 {index + 1} 行需填写按钮名和插入内容，或重新选择有效学科（最多 32 科）。", true);
                return false;
            }
        }
        if (DeadlinesChanged)
        {
            int index = _deadlines.FindIndex(row => row.Label.Trim().Length == 0 ||
                !TimeOnly.TryParseExact(row.Time.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
            if (index >= 0)
            {
                SelectSection(preferDeadlines: true);
                HomeworkRows.SelectedIndex = index;
                HomeworkRows.ScrollIntoView(index);
                HomeworkDeadlineTime.Focus(NavigationMethod.Tab);
                Feedback($"截止时间第 {index + 1} 行需填写按钮名，并使用 24 小时制（例如 07:30）。", true);
                return false;
            }
        }
        return true;
    }

    private void AdoptClicked(object? sender, RoutedEventArgs args)
    {
        if (!_showReview || !CurrentConflict || _working || _baseline is not { } b || _latest is not { } latest || _pending is not null) return;
        _baseline = _showDeadlines
            ? b with { QuickDeadlines = latest.QuickDeadlines, Versions = b.Versions with { QuickDeadlines = latest.Versions.QuickDeadlines } }
            : b with { QuickInputs = latest.QuickInputs, Versions = b.Versions with { QuickInputs = latest.Versions.QuickInputs } };
        Feedback("已核对本分区的服务器版本。草稿已保留，请确认后保存。");
        ReturnToEditor();
    }

    private void BackClicked(object? sender, RoutedEventArgs args)
    {
        if (_pending is null && (_showReview || _compact && _showPreview)) ReturnToEditor();
        else CloseRequested?.Invoke();
    }

    private void DiscardClicked(object? sender, RoutedEventArgs args)
    {
        if (_posting) return;
        _discard = true;
        CloseRequested?.Invoke();
    }
    private void WordsClicked(object? sender, RoutedEventArgs args) => ChangeSection(preferDeadlines: false);
    private void DeadlinesClicked(object? sender, RoutedEventArgs args) => ChangeSection(preferDeadlines: true);

    private void ChangeSection(bool preferDeadlines)
    {
        if (!_working && _pending is null) CaptureSelectedFields();
        SelectSection(preferDeadlines);
    }

    private void SelectSection(bool preferDeadlines)
    {
        _showDeadlines = preferDeadlines;
        _showPreview = _showReview = false;
        LayoutPanes();
        PopulateRows();
    }

    private void LayoutPanes()
    {
        _compact = Bounds.Width is > 0 and < CompactWidth;
        UpdatePaneVisibility();
        HomeworkPreviewToggle.Content = _showPreview && _compact ? "返回编辑" : _showDeadlines ? "预览日期" : "试用快捷词";
        HomeworkPanes.ColumnDefinitions[1].Width = _compact ? new(0) : new(1, GridUnitType.Star);
        HomeworkPanes.ColumnSpacing = _compact ? 0 : 12;
        Grid.SetColumn(HomeworkPreviewPane, _compact ? 0 : 1);
        HomeworkEditorRows.RowDefinitions[0].Height = new(_compact ? .65 : 1, Avalonia.Controls.GridUnitType.Star);
        HomeworkEditor.IsVisible = !_compact || !_showPreview;
        HomeworkPreviewPane.IsVisible = !_compact || _showPreview;
    }

    private void UpdatePaneVisibility()
    {
        bool hasRequest = _pending is not null;
        HomeworkPreviewToggle.IsVisible = _compact && !_showReview && !hasRequest;
        HomeworkPanes.IsVisible = !_showReview && !hasRequest;
        HomeworkReviewView.IsVisible = _showReview && !hasRequest;
        HomeworkPendingView.IsVisible = hasRequest;
        HomeworkBack.IsVisible = !_showReview || hasRequest;
        HomeworkWordsTab.IsEnabled = HomeworkDeadlinesTab.IsEnabled = !hasRequest;
    }

    private void ReviewOpenClicked(object? sender, RoutedEventArgs args)
    {
        if (_working || _pending is not null) return;
        CaptureSelectedFields();
        OpenReview();
    }

    private void OpenReview()
    {
        if (!WordsConflict && !DeadlinesConflict) return;
        if (!CurrentConflict) SelectSection(preferDeadlines: !WordsConflict);
        _showReview = true;
        LayoutPanes();
        UpdateState();
        Dispatcher.UIThread.Post(() =>
        {
            if (!_closed && _showReview && HomeworkReviewBack.IsEffectivelyVisible) HomeworkReviewBack.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private void ReviewBackClicked(object? sender, RoutedEventArgs args) => ReturnToEditor();

    private void ReturnToEditor()
    {
        _showReview = _showPreview = false;
        LayoutPanes();
        UpdateState();
        RestorePaneFocus(_showDeadlines ? _lastDeadlineEditorFocus : _lastWordEditorFocus, preview: false);
    }

    private void TogglePreviewClicked(object? sender, RoutedEventArgs args)
    {
        if (_showPreview) { ReturnToEditor(); return; }
        _showPreview = true;
        LayoutPanes();
        RestorePaneFocus(_showDeadlines ? _lastDeadlinePreviewFocus : _lastWordPreviewFocus, preview: true);
    }

    private void RestorePaneFocus(Control? destination, bool preview)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed || _showReview) return;
            if (destination is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } && destination.Focus(NavigationMethod.Tab)) return;
            if (preview) HomeworkPreview.FocusPreview();
            else HomeworkRows.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    protected override void OnGotFocus(FocusChangedEventArgs args)
    {
        base.OnGotFocus(args);
        if (args.Source is not Control control) return;
        if (HomeworkEditor.IsVisualAncestorOf(control))
        {
            if (_showDeadlines) _lastDeadlineEditorFocus = control;
            else _lastWordEditorFocus = control;
        }
        if (HomeworkPreviewPane.IsVisualAncestorOf(control))
        {
            if (_showDeadlines) _lastDeadlinePreviewFocus = control;
            else _lastWordPreviewFocus = control;
        }
    }

    private void PopulateRows()
    {
        _binding = true;
        _rows.Clear();
        for (int i = 0; i < RowCount; i++) _rows.Add(new(Caption(i)));
        CurrentIndex = RowCount == 0 ? -1 : Math.Clamp(CurrentIndex, 0, RowCount - 1);
        HomeworkRows.SelectedIndex = CurrentIndex;
        _binding = false;
        BindSelectedRow();
        UpdateState();
    }

    private string Caption(int index) => _showDeadlines
        ? $"{index + 1}. {_deadlines[index].Label} · {RuleLabel(_deadlines[index])} {_deadlines[index].Time}"
        : $"{index + 1}. {Group(_words[index])} · {_words[index].Label}{(_words[index].InsertMode == "NEW_LINE" ? " · 换行" : "")}";

    private void RowChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_binding) return;
        CurrentIndex = HomeworkRows.SelectedIndex;
        BindSelectedRow();
        UpdateState();
    }

    private void BindSelectedRow()
    {
        HomeworkRowEditor.IsVisible = CurrentIndex >= 0 && CurrentIndex < RowCount;
        if (!HomeworkRowEditor.IsVisible) return;
        _binding = true;
        HomeworkWordGroupBox.IsVisible = HomeworkWordFields.IsVisible = !_showDeadlines;
        HomeworkDeadlineTimeBox.IsVisible = HomeworkDeadlineFields.IsVisible = _showDeadlines;
        if (_showDeadlines)
        {
            var row = _deadlines[CurrentIndex];
            HomeworkLabel.Text = row.Label;
            HomeworkDeadlineTime.Text = row.Time;
            HomeworkDeadlineRule.SelectedIndex = RuleIndex(row);
        }
        else
        {
            var row = _words[CurrentIndex];
            HomeworkLabel.Text = row.Label;
            HomeworkWordGroup.Text = row.Group;
            HomeworkWordText.Text = row.Text;
            HomeworkWordMode.SelectedIndex = row.InsertMode == "NEW_LINE" ? 1 : 0;
            PopulateSubjects(row);
        }
        _binding = false;
    }

    private void PopulateSubjects(HomeworkQuickInput row)
    {
        HomeworkSubjectChecks.Children.Clear();
        var known = _latest?.Subjects ?? [];
        foreach (var subject in known)
            AddSubjectCheck(subject.Id, subject.Name, row.SubjectIds.Contains(subject.Id));
        foreach (string id in row.SubjectIds.Where(id => !known.Any(subject => subject.Id == id)))
            AddSubjectCheck(id, $"失效学科（{id}）", true);
    }

    private void AddSubjectCheck(string id, string name, bool selected)
    {
        var check = new CheckBox { Content = name, Tag = id, IsChecked = selected };
        check.IsCheckedChanged += (_, _) =>
        {
            if (_binding || _working || CurrentIndex < 0) return;
            var word = _words[CurrentIndex];
            bool adding = check.IsChecked == true;
            if (!adding && word.SubjectIds.Count == 1)
            {
                _binding = true;
                check.IsChecked = true;
                _binding = false;
                Feedback("若要让此词条适用于所有学科，请明确选择「设为全科」。", true);
                return;
            }
            _words[CurrentIndex] = word with { SubjectIds = adding ? [..word.SubjectIds, id] : word.SubjectIds.Where(value => value != id).ToArray() };
            UpdateState();
        };
        HomeworkSubjectChecks.Children.Add(check);
    }

    private void GlobalClicked(object? sender, RoutedEventArgs args)
    {
        if (_showDeadlines || CurrentIndex < 0) return;
        _words[CurrentIndex] = _words[CurrentIndex] with { SubjectIds = [] };
        BindSelectedRow();
        UpdateState();
    }

    private void FieldChanged(object? sender, RoutedEventArgs args)
    {
        if (_binding || _working || !CaptureSelectedFields()) return;
        _rows[CurrentIndex].SetCaption(Caption(CurrentIndex));
        UpdateState();
    }

    private bool CaptureSelectedFields()
    {
        if (CurrentIndex < 0 || CurrentIndex >= RowCount) return false;
        if (_showDeadlines)
        {
            int rule = HomeworkDeadlineRule.SelectedIndex;
            if (rule < 0) return false;
            _deadlines[CurrentIndex] = new(HomeworkLabel.Text ?? "", HomeworkDeadlineTime.Text ?? "", rule < 15 ? rule : null,
                rule >= 15 ? "next-weekday" : null, rule >= 15 ? rule - 15 : null);
        }
        else
        {
            _words[CurrentIndex] = _words[CurrentIndex] with
            {
                Label = HomeworkLabel.Text ?? "", Group = HomeworkWordGroup.Text ?? "", Text = HomeworkWordText.Text ?? "",
                InsertMode = HomeworkWordMode.SelectedIndex == 1 ? "NEW_LINE" : "INLINE"
            };
        }
        return true;
    }

    private void AddClicked(object? sender, RoutedEventArgs args)
    {
        if (RowCount >= (_showDeadlines ? 8 : 64)) return;
        if (_showDeadlines) _deadlines.Add(new("新时间", "17:30", 1));
        else _words.Add(new("新词", "", "常用", [], "INLINE"));
        CurrentIndex = RowCount - 1;
        PopulateRows();
        HomeworkRows.ScrollIntoView(CurrentIndex);
        HomeworkLabel.Focus();
        HomeworkLabel.SelectAll();
    }

    private void RemoveClicked(object? sender, RoutedEventArgs args)
    {
        if (CurrentIndex < 0 || _showDeadlines && _deadlines.Count <= 1) return;
        if (_showDeadlines) _deadlines.RemoveAt(CurrentIndex);
        else _words.RemoveAt(CurrentIndex);
        PopulateRows();
        HomeworkRows.Focus(NavigationMethod.Tab);
    }

    private void EarlierClicked(object? sender, RoutedEventArgs args) => MoveRow(-1);
    private void LaterClicked(object? sender, RoutedEventArgs args) => MoveRow(1);
    private void GroupEarlierClicked(object? sender, RoutedEventArgs args) => MoveGroup(-1);
    private void GroupLaterClicked(object? sender, RoutedEventArgs args) => MoveGroup(1);

    private void MoveRow(int direction)
    {
        int index = CurrentIndex, next = index + direction;
        if (index < 0 || next < 0 || next >= RowCount) return;
        if (_showDeadlines) (_deadlines[index], _deadlines[next]) = (_deadlines[next], _deadlines[index]);
        else (_words[index], _words[next]) = (_words[next], _words[index]);
        CurrentIndex = next;
        PopulateRows();
        HomeworkRows.ScrollIntoView(next);
    }

    private void MoveGroup(int direction)
    {
        if (_showDeadlines || CurrentIndex < 0) return;
        var row = _words[CurrentIndex];
        var groups = _words.GroupBy(Group).ToArray();
        int index = Array.FindIndex(groups, group => group.Key == Group(row));
        int next = index + direction;
        if (next < 0 || next >= groups.Length) return;
        (groups[index], groups[next]) = (groups[next], groups[index]);
        _words.Clear();
        _words.AddRange(groups.SelectMany(group => group));
        CurrentIndex = _words.FindIndex(word => ReferenceEquals(word, row));
        PopulateRows();
        HomeworkRows.ScrollIntoView(CurrentIndex);
    }

    private void DefaultsClicked(object? sender, RoutedEventArgs args)
    {
        if (_latest is not { } latest) return;
        if (_showDeadlines) { _deadlines.Clear(); _deadlines.AddRange(latest.Defaults.QuickDeadlines); }
        else { _words.Clear(); _words.AddRange(latest.Defaults.QuickInputs); }
        PopulateRows();
        Feedback("已载入本分区默认值。确认后保存修改。");
    }

    private void ClearWordsClicked(object? sender, RoutedEventArgs args)
    {
        if (_showDeadlines || _words.Count == 0) return;
        _words.Clear();
        PopulateRows();
        HomeworkAdd.Focus(NavigationMethod.Tab);
        Feedback("快捷词草稿已清空。保存后教师端和大屏将关闭快捷词。");
    }

    private void Feedback(string text, bool error = false)
    {
        HomeworkFeedback.Text = text;
        HomeworkFeedback.IsVisible = text.Length > 0;
        HomeworkFeedback.Classes.Set("error", error);
    }

    private void UpdateState()
    {
        var selected = !_showDeadlines && CurrentIndex >= 0 && CurrentIndex < _words.Count ? _words[CurrentIndex] : null;
        var enabled = _baseline is not null && !_working && _pending is null;
        UpdatePaneVisibility();
        if (_pending is { } request)
        {
            var sections = request.Words is null ? "截止时间" : request.Deadlines is null ? "快捷词" : "快捷词和截止时间";
            HomeworkPendingSummary.Text = $"本次提交包含{sections}。重新打开本校设置后可继续核对。";
            HomeworkPendingRequest.Text = $"请求编号 {request.Id:D}";
        }
        HomeworkEditor.IsEnabled = enabled;
        HomeworkBack.IsEnabled = HomeworkDiscard.IsEnabled = !_posting;
        HomeworkRead.IsEnabled = !_working;
        if (_pending is not null || !WordsChanged && !DeadlinesChanged) HomeworkDiscard.IsVisible = false;
        HomeworkBack.Content = _pending is null ? "返回" : "返回，稍后核对";
        HomeworkRead.Content = _pending is null ? "刷新配置" : "核对原请求";
        HomeworkRetry.IsVisible = !_working && _pending is not null && _allowRetry;
        HomeworkSave.IsEnabled = enabled && !_showReview && (WordsChanged || DeadlinesChanged) && !WordsConflict && !DeadlinesConflict;
        HomeworkSave.IsVisible = !_showReview && _pending is null;
        HomeworkWordsTab.Classes.Set("current", !_showDeadlines);
        HomeworkDeadlinesTab.Classes.Set("current", _showDeadlines);
        HomeworkWordsTab.Content = "快捷词" + (WordsConflict ? " · 待核对" : WordsChanged ? " · 已修改" : "");
        HomeworkDeadlinesTab.Content = "截止时间" + (DeadlinesConflict ? " · 待核对" : DeadlinesChanged ? " · 已修改" : "");
        HomeworkDirty.Text = WordsChanged && DeadlinesChanged ? "两个分区有修改" : WordsChanged || DeadlinesChanged ? "一个分区有修改" : "";
        HomeworkAdd.IsEnabled = RowCount < (_showDeadlines ? 8 : 64);
        HomeworkRemove.IsEnabled = CurrentIndex >= 0 && (!_showDeadlines || RowCount > 1);
        HomeworkEarlier.IsEnabled = CurrentIndex > 0;
        HomeworkLater.IsEnabled = CurrentIndex >= 0 && CurrentIndex + 1 < RowCount;
        HomeworkGroupEarlier.IsVisible = HomeworkGroupLater.IsVisible = !_showDeadlines;
        HomeworkClearWords.IsVisible = !_showDeadlines;
        HomeworkClearWords.IsEnabled = !_showDeadlines && RowCount > 0;
        var groups = selected is null ? [] : _words.Select(Group).Distinct().ToArray();
        int groupIndex = selected is null ? -1 : Array.IndexOf(groups, Group(selected));
        HomeworkGroupEarlier.IsEnabled = groupIndex > 0;
        HomeworkGroupLater.IsEnabled = groupIndex >= 0 && groupIndex + 1 < groups.Length;
        HomeworkGlobal.IsEnabled = selected is { SubjectIds.Count: > 0 };
        HomeworkWordText.IsEnabled = selected?.InsertMode != "NEW_LINE";
        HomeworkWordScope.Text = selected is { SubjectIds.Count: > 0 } ? "限定学科" : "全科通用";
        HomeworkRowsHint.Text = _baseline is null ? "请先读取当前配置。" : RowCount == 0 ? "快捷词关闭，保存后各端不再显示。" : "";
        HomeworkReviewOpen.IsVisible = !_showReview && _pending is null && (WordsConflict || DeadlinesConflict);
        HomeworkReviewOpen.IsEnabled = !_working;
        HomeworkReviewOpen.Content = CurrentConflict ? (_showDeadlines ? "核对截止时间" : "核对快捷词")
            : WordsConflict ? "核对快捷词" : "核对截止时间";
        HomeworkConflict.IsVisible = CurrentConflict;
        HomeworkAdopt.IsEnabled = !_working && _pending is null && CurrentConflict;
        HomeworkReviewTitle.Text = _showDeadlines ? "核对截止时间变更" : "核对快捷词变更";
        if (CurrentConflict && _latest is { } latest)
        {
            HomeworkConflictRows.Text = _showDeadlines
                ? Differences(latest.QuickDeadlines, _deadlines, (a, b) => a == b, row => $"{row.Label} · {RuleLabel(row)} {row.Time}")
                : Differences(latest.QuickInputs, _words, SameWord, row => WordSummary(row, latest.Subjects));
            if (HomeworkConflictRows.Text.Length == 0) HomeworkConflictRows.Text = "当前值与草稿相同，服务器版本已更新。";
        }
        HomeworkPreview.SetPreview(_showDeadlines, _words.ToArray(), _deadlines.ToArray(), _latest?.Subjects ?? []);
    }

    private static string Differences<T>(IReadOnlyList<T> server, IReadOnlyList<T> draft, Func<T, T, bool> same, Func<T, string> show) =>
        string.Join("\n", Enumerable.Range(0, Math.Max(server.Count, draft.Count))
            .Where(index => index >= server.Count || index >= draft.Count || !same(server[index], draft[index]))
            .Select(index => $"第 {index + 1} 行　服务器：{(index < server.Count ? show(server[index]) : "无")}\n" +
                $"　　　　　 草稿：{(index < draft.Count ? show(draft[index]) : "无")}"));

    private static string WordSummary(HomeworkQuickInput row, IReadOnlyList<HomeworkSubject> subjects) =>
        $"{Group(row)} · {row.Label} · {(row.InsertMode == "NEW_LINE" ? "换行" : row.Text)} · " +
        (row.SubjectIds.Count == 0 ? "全科" : string.Join("、", row.SubjectIds.Select(id =>
            subjects.FirstOrDefault(subject => subject.Id == id)?.Name ?? $"失效学科 {id}")));

    private static bool SameWord(HomeworkQuickInput a, HomeworkQuickInput b) =>
        a.Label == b.Label && a.Text == b.Text && a.Group == b.Group && a.InsertMode == b.InsertMode && a.SubjectIds.SequenceEqual(b.SubjectIds);

    private static string Group(HomeworkQuickInput word) => string.IsNullOrWhiteSpace(word.Group) ? "常用" : word.Group.Trim();
    private static int RuleIndex(HomeworkQuickDeadline row) => row.DateRule == "next-weekday" ? 15 + row.Weekday.GetValueOrDefault() : row.DayOffset.GetValueOrDefault();
    private static string RuleLabel(HomeworkQuickDeadline row) => Rules[RuleIndex(row)];
}
