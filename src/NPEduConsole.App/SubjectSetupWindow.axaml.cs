using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPEduConsole.Core;

namespace NPEduConsole.App;

internal sealed record UnconfirmedSubjectSetup(SubjectSetupRow[] PreviewRows, SubjectSetupTarget[] Submitted);

public sealed class SubjectSetupCell(SubjectSetupRow row, bool included, bool canExclude, Action<SubjectSetupCell> changed,
    string? previousState = null)
    : INotifyPropertyChanged
{
    private bool _included = included;
    public SubjectSetupRow Row { get; } = row;
    public bool CanExclude { get; } = canExclude;
    public bool Included
    {
        get => _included;
        set
        {
            if (_included == value) return;
            _included = value;
            foreach (string property in new[] { nameof(Included), nameof(IsExcluded), nameof(IsConflict),
                         nameof(IsPresent), nameof(IsNew), nameof(StateText) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
            changed(this);
        }
    }
    public string Title => $"{Row.ClassName} · {Row.SubjectName}";
    public bool IsExcluded => CanExclude && !Included;
    public bool IsConflict => Included && !Row.CanApply && CanExclude;
    public bool IsPresent => !IsExcluded && Row.State == "PRESENT";
    public bool IsNew => !IsExcluded && Row.State is "ADD" or "ADDED";
    public string StateText => IsExcluded ? "已排除" : StateLabel(Row.State);
    public string Detail => previousState is null ? Row.Message
        : $"预览：{StateLabel(previousState)} · 当前：{StateLabel(Row.State)}" +
            (Row.CanApply ? "" : $" · {Row.Message}");
    public bool HasDetail => previousState is not null || Row.State is "WALKING_CONFLICT" or "INVALID";

    private static string StateLabel(string state) => state switch
    {
        "ADD" => "将新增",
        "ADDED" => "已新增",
        "PRESENT" => "已随班开设",
        "WALKING_CONFLICT" => "走班冲突",
        _ => "待核对"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    public string ToggleLabel => $"包含 {Title}";
    public string ToggleId => $"SubjectSetupPair-{Row.ClassId}-{Row.SubjectId}";
    public override string ToString() => Title;
}

public sealed partial class SubjectSetupWindow : Window
{
    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly string _termId = "", _classId = "";
    private readonly string[] _initialClasses = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, CheckBox> _rooms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckBox> _subjects = new(StringComparer.Ordinal);
    private readonly HashSet<SubjectSetupTarget> _excluded = [];
    private SubjectSetupCell[] _cells = [];
    private SubjectSetupCatalog? _catalog;
    private bool _choicesOpen = true;
    internal UnconfirmedSubjectSetup? Pending { get; private set; }
    private bool _validPreview, _busy, _saving, _closed, _scopeChanged;
    internal SubjectSetupTarget? NextTeaching { get; private set; }
    internal event Action<ConsoleApiException>? SessionExpired;

    public SubjectSetupWindow() => InitializeComponent();

    internal SubjectSetupWindow(ConsoleClient client, School school, string termId, string classId, string[] initialClasses,
        UnconfirmedSubjectSetup? pending) : this()
    {
        _client = client;
        _school = school;
        _termId = termId;
        _classId = classId;
        _initialClasses = initialClasses;
        SubjectSetupScope.Text = school.Name;
        if (pending is not null)
        {
            Pending = pending;
            _excluded.UnionWith(pending.PreviewRows.Select(row => row.Target).Except(pending.Submitted));
            _choicesOpen = false;
            ShowRows(pending.PreviewRows, true);
        }
        Opened += async (_, _) =>
        {
            BatchWorkspaceWindow.FitSchoolWindow(this);
            await RunAsync(ReadCatalogAsync);
            if (!_closed && Pending is not null) SubjectSetupReadButton.Focus();
        };
        Closing += (_, args) => args.Cancel = _saving && !_closed;
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
    }

    private async Task ReadCatalogAsync()
    {
        bool first = _catalog is null;
        var selectedRooms = (first ? Pending?.PreviewRows.Select(row => row.ClassId) ?? _initialClasses : CheckedIds(_rooms))
            .ToHashSet(StringComparer.Ordinal);
        var selectedSubjects = (first && Pending is { } pending ? pending.PreviewRows.Select(row => row.SubjectId) : CheckedIds(_subjects))
            .ToHashSet(StringComparer.Ordinal);
        var catalog = await _client.GetSubjectSetupAsync(_school.Id, _termId, _classId, _lifetime.Token);
        if (_closed) return;
        _catalog = catalog;
        _scopeChanged = false;
        var entryClass = catalog.Classes.Single(room => room.Id == _classId);
        SubjectSetupScope.Text = $"{_school.Name} · 当前学期 · 从{entryClass.Name}进入";
        BindChoices(SubjectSetupClasses, _rooms, catalog.Classes.Select(room => (room.Id, room.Name)), selectedRooms, "SubjectSetupClass-");
        BindChoices(SubjectSetupSubjects, _subjects, catalog.Subjects.Select(subject => (subject.Id, subject.ToString())),
            selectedSubjects, "SubjectSetupSubject-");
        if (Pending is not null)
        {
            SubjectSetupFeedback.Text = "已恢复上次待核对的选择。请先读取服务器规则。";
            return;
        }
        InvalidatePreview();
        SubjectSetupFeedback.Text = catalog.Subjects.Length == 0
            ? "学校尚无学科。请在网页「组织与班级 → 学校与学科」维护学科目录，再回来刷新。"
            : "勾选班级和学科后预览。已有随班和走班规则都会保留。所需学科未列出时，请前往网页学科目录。";
    }

    private void BindChoices(Panel panel, Dictionary<string, CheckBox> checks, IEnumerable<(string Id, string Title)> choices,
        HashSet<string> selected, string prefix)
    {
        checks.Clear();
        panel.Children.Clear();
        foreach (var (id, title) in choices)
        {
            var check = new CheckBox { Content = title, IsChecked = selected.Contains(id) };
            AutomationProperties.SetAutomationId(check, prefix + id);
            check.IsCheckedChanged += (_, _) => InvalidatePreview();
            panel.Children.Add(check);
            checks.Add(id, check);
        }
    }

    private static IEnumerable<string> CheckedIds(Dictionary<string, CheckBox> choices) =>
        choices.Where(row => row.Value.IsChecked == true).Select(row => row.Key);

    private void InvalidatePreview()
    {
        if (_validPreview) SubjectSetupFeedback.Text = "选择已变化，请重新预览。";
        _validPreview = false;
        ShowRows([], false);
        ClearTeaching();
        Render();
    }

    private void ClearTeaching()
    {
        SubjectSetupTeachingPicker.ItemsSource = null;
        SubjectSetupTeachingRow.IsVisible = SubjectSetupTeachingButton.IsVisible = false;
    }

    private void ShowRows(IEnumerable<SubjectSetupRow> rows, bool canExclude,
        IReadOnlyDictionary<SubjectSetupTarget, string>? previousStates = null)
    {
        _cells = rows.Select(row => new SubjectSetupCell(row, !_excluded.Contains(row.Target), canExclude, ChoiceChanged,
            previousStates?.GetValueOrDefault(row.Target))).ToArray();
        if (!_cells.Any(cell => cell.HasDetail) && SubjectSetupConflictFilter.IsChecked == true)
            SubjectSetupConflictFilter.IsChecked = false;
        FilterRows();
    }

    private void FilterRows()
    {
        SubjectSetupRows.ItemsSource = SubjectSetupConflictFilter.IsChecked == true
            ? _cells.Where(cell => cell.HasDetail).ToArray() : _cells;
    }

    private void ConflictFilterChanged(object? sender, RoutedEventArgs args)
    {
        if (SubjectSetupRows is not null) FilterRows();
    }

    private void ToggleChoicesClicked(object? sender, RoutedEventArgs args)
    {
        _choicesOpen = !_choicesOpen;
        Render();
    }

    private void FocusRow(SubjectSetupCell? target)
    {
        if (target is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed || !_cells.Contains(target)) return;
            SubjectSetupRows.SelectedItem = target;
            SubjectSetupRows.ScrollIntoView(target);
            Dispatcher.UIThread.Post(() =>
            {
                if (_closed || !ReferenceEquals(SubjectSetupRows.SelectedItem, target)) return;
                var toggle = SubjectSetupRows.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault(control =>
                    AutomationProperties.GetAutomationId(control) == target.ToggleId && control.IsEffectivelyVisible);
                toggle?.Focus(NavigationMethod.Tab);
            }, DispatcherPriority.Background);
        }, DispatcherPriority.Loaded);
    }

    private void ChoiceChanged(SubjectSetupCell cell)
    {
        if (cell.Included) _excluded.Remove(cell.Row.Target); else _excluded.Add(cell.Row.Target);
        if (_validPreview) UpdatePreviewMessage();
        Render();
    }

    private void UpdatePreviewMessage()
    {
        SubjectSetupFeedback.Text = _cells.Any(cell => cell.Included && !cell.Row.CanApply)
            ? "含走班或失效安排。可逐行排除冲突项，本次保存只补设随班授课科目。"
            : "请核对逐班逐科的安排，再确认保存。";
    }

    private void Render()
    {
        bool editable = !_busy && !_closed && !_scopeChanged && _catalog is not null && Pending is null;
        int selectedClasses = CheckedIds(_rooms).Count();
        long count = (long)selectedClasses * CheckedIds(_subjects).Count();
        var included = _cells.Where(cell => cell.Included).ToArray();
        SubjectSetupInputs.IsVisible = _choicesOpen && _catalog is { Subjects.Length: > 0 };
        SubjectSetupInputs.IsEnabled = editable;
        SubjectSetupChoiceButton.IsVisible = _catalog is { Subjects.Length: > 0 } && Pending is null;
        SubjectSetupChoiceButton.Content = _choicesOpen ? "收起班级与学科" : "更改班级与学科";
        SubjectSetupChoiceButton.IsEnabled = editable;
        SubjectSetupConflictFilter.IsVisible = _cells.Any(cell => cell.HasDetail);
        SubjectSetupConflictFilter.IsEnabled = !_busy;
        SubjectSetupEmpty.IsVisible = _cells.Length == 0;
        SubjectSetupCloseButton.IsEnabled = !_saving;
        SubjectSetupCloseButton.Content = Pending is null ? "返回" : "关闭，稍后核对";
        SubjectSetupRows.Classes.Set("locked", _busy || Pending is not null || _scopeChanged);
        SubjectSetupRows.IsVisible = _cells.Length > 0;
        SubjectSetupReadButton.IsEnabled = !_busy;
        SubjectSetupReadButton.Content = Pending is null ? "刷新目录" : "核对保存结果";
        SubjectSetupPreviewButton.IsVisible = SubjectSetupApplyButton.IsVisible = Pending is null && !SubjectSetupTeachingRow.IsVisible;
        SubjectSetupPreviewButton.IsEnabled = editable && count > 0;
        SubjectSetupApplyButton.IsEnabled = editable && _validPreview && included.Length > 0 && included.All(cell => cell.Row.CanApply);
        SubjectSetupTeachingButton.IsEnabled = editable && SubjectSetupTeachingPicker.SelectedItem is SubjectSetupCell;
        SubjectSetupReadButton.Classes.Set("accent", Pending is not null);
        SubjectSetupWebButton.IsVisible = Pending is null;
        SubjectSetupWebButton.IsEnabled = !_busy;
        SubjectSetupCount.IsVisible = _cells.Length > 0 || _catalog is { Subjects.Length: > 0 };
        SubjectSetupCount.Text = Pending is { } pending ? $"待核对 {pending.Submitted.Length} 项 · 已保留本次选择"
            : SubjectSetupTeachingRow.IsVisible ? $"服务器已确认 {_cells.Length} 项"
            : _cells.Length > 0 ? $"纳入 {included.Select(cell => cell.Row.ClassId).Distinct().Count()} 个班级（已选 {selectedClasses} 个）" +
                $"  ·  将新增 {included.Count(cell => cell.Row.State == "ADD")} 项" +
                $"  ·  已随班 {included.Count(cell => cell.Row.State == "PRESENT")} 项  ·  冲突 {included.Count(cell => cell.IsConflict)} 项" +
                $"  ·  已排除 {_cells.Length - included.Length} 项"
            : count > 0 ? $"已选 {count} 个班级与学科组合" : "请选择至少一个班级和一门学科。";
    }

    private async void PreviewClicked(object? sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        _validPreview = false;
        ClearTeaching();
        var rooms = CheckedIds(_rooms).ToArray();
        var subjects = CheckedIds(_subjects).ToArray();
        if ((long)rooms.Length * subjects.Length > 4000)
        {
            SubjectSetupFeedback.Text = "每次最多配置 4000 个班级与学科组合，请减少本次选择。";
            return;
        }
        var targets = rooms.SelectMany(room => subjects.Select(subject => new SubjectSetupTarget(room, subject))).ToArray();
        SubjectSetupFeedback.Text = "正在核对所选班级和学科…";
        var preview = await _client.PreviewSubjectSetupAsync(_school.Id, _termId, _classId, targets, _lifetime.Token);
        if (_closed) return;
        _validPreview = true;
        _choicesOpen = false;
        ShowRows(preview.Rows, true);
        UpdatePreviewMessage();
        FocusRow(_cells.FirstOrDefault(cell => cell.IsConflict));
    });

    private async void ApplyClicked(object? sender, RoutedEventArgs args)
    {
        if (!SubjectSetupApplyButton.IsEnabled) return;
        await RunAsync(async () =>
        {
            var selected = _cells.Where(cell => cell.Included).Select(cell => cell.Row).ToArray();
            _validPreview = false;
            _saving = true;
            SubjectSetupFeedback.Text = "正在保存随班授课科目…";
            SubjectSetupResult result;
            try { result = await _client.SaveSubjectSetupAsync(_school.Id, _termId, _classId, selected, _lifetime.Token); }
            catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or >= 500)
            {
                Pending = new(_cells.Select(cell => cell.Row).ToArray(), selected.Select(row => row.Target).ToArray());
                await VerifyAsync();
                return;
            }
            finally { _saving = false; }
            if (_closed) return;
            if (result.State == "APPLIED")
            {
                var changed = result.Rows.Count(row => row.State == "ADDED");
                ShowResult(result.Rows, changed > 0 ? $"已保存 {changed} 项新增授课科目。"
                    : "所选学科已随班开设，无需修改。");
                return;
            }
            var updated = result.Rows.ToDictionary(row => row.Target);
            var previous = _cells.Where(cell => updated.ContainsKey(cell.Row.Target))
                .ToDictionary(cell => cell.Row.Target, cell => cell.Row.State);
            ShowRows(_cells.Select(cell => updated.GetValueOrDefault(cell.Row.Target, cell.Row)).ToArray(), true, previous);
            SubjectSetupFeedback.Text = result.State == "STALE"
                ? $"本批未写入。下列安排已变化：{string.Join("、", result.Rows.Take(3).Select(row => $"{row.ClassName} · {row.SubjectName}"))}。请重新预览。"
                : "本批未写入。请取消冲突项后重新预览。";
            FocusRow(_cells.FirstOrDefault(cell => cell.Row.Target == result.Rows[0].Target));
        });
    }

    private async Task VerifyAsync()
    {
        if (Pending is not { } pending) return;
        SubjectSetupFeedback.Text = "保存回执未确认，正在只读核对服务器规则…";
        var preview = await _client.PreviewSubjectSetupAsync(_school.Id, _termId, _classId, pending.Submitted, _lifetime.Token);
        if (_closed) return;
        Pending = null;
        if (preview.Rows.All(row => row.State == "PRESENT"))
        {
            ShowResult(preview.Rows, "已核对，服务器当前规则与本次选择一致。");
            return;
        }
        var current = preview.Rows.ToDictionary(row => row.Target);
        var different = preview.Rows.Where(row => row.State != "PRESENT").Select(row => row.Target).ToHashSet();
        var previous = _cells.Where(cell => different.Contains(cell.Row.Target)).ToDictionary(cell => cell.Row.Target, cell => cell.Row.State);
        ShowRows(_cells.Select(cell => current.GetValueOrDefault(cell.Row.Target, cell.Row)).ToArray(), true, previous);
        SubjectSetupFeedback.Text = "已核对，服务器规则与本次选择有差异。请核对后重新预览。";
        FocusRow(_cells.FirstOrDefault(cell => different.Contains(cell.Row.Target)));
    }

    private void ShowResult(SubjectSetupRow[] rows, string message)
    {
        _choicesOpen = false;
        ShowRows(rows, false);
        SubjectSetupTeachingPicker.ItemsSource = _cells.OrderByDescending(cell => cell.Row.State == "ADDED")
            .ThenByDescending(cell => cell.Row.ClassId == _classId).ToArray();
        SubjectSetupTeachingPicker.SelectedIndex = 0;
        SubjectSetupTeachingRow.IsVisible = SubjectSetupTeachingButton.IsVisible = true;
        SubjectSetupFeedback.Text = message;
    }

    private async void ReadClicked(object? sender, RoutedEventArgs args) =>
        await RunAsync(() => Pending is null ? ReadCatalogAsync() : VerifyAsync());

    private void WebClicked(object? sender, RoutedEventArgs args)
    {
        string path = "请在网页学校管理的「组织与班级 → 学校与学科」打开学科目录。";
        if (SchoolAdminBrowser.Link(_catalog?.AdminUrl, _school.Id, _termId, "structure") is { } url && SchoolAdminBrowser.Open(url))
            SubjectSetupFeedback.Text = "已打开网页学科目录。维护后请返回这里刷新。";
        else SubjectSetupFeedback.Text = path;
    }

    private void TeachingSelectionChanged(object? sender, SelectionChangedEventArgs args) => Render();

    private void TeachingClicked(object? sender, RoutedEventArgs args)
    {
        if (SubjectSetupTeachingPicker.SelectedItem is not SubjectSetupCell selected) return;
        NextTeaching = selected.Row.Target;
        Close();
    }

    private async Task RunAsync(Func<Task> work)
    {
        if (_busy || _closed) return;
        _busy = true;
        Render();
        try { await work(); }
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error)
        {
            if (_closed) return;
            _scopeChanged = error.Code is "SUBJECT_SETUP_TERM_CHANGED" or "SUBJECT_SETUP_CLASS_CHANGED" || error.StatusCode == 403;
            SubjectSetupFeedback.Text = Pending is null ? error.Message : $"保存回执仍未确认。{error.Message}";
            if (error.StatusCode is 401 or 403)
            {
                Pending = null;
                _excluded.Clear();
                ShowRows([], false);
            }
            if (error.StatusCode == 401) SessionExpired?.Invoke(error);
        }
        finally { _busy = false; if (!_closed) Render(); }
    }

    private void CloseClicked(object? sender, RoutedEventArgs args) => Close();
    internal void CloseForScopeChange() { _closed = true; Close(); }
}
