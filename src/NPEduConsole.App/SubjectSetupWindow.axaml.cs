using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed class SubjectSetupCell(SubjectSetupRow row, bool included, bool canExclude, Action<SubjectSetupCell> changed)
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
            changed(this);
        }
    }
    public string Title => $"{Row.ClassName} · {Row.SubjectName}";
    public string Detail => Row.Message;
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
    private SubjectSetupTarget[]? _unconfirmed;
    private bool _validPreview, _busy, _saving, _closed, _scopeChanged;
    internal SubjectSetupTarget? NextTeaching { get; private set; }
    internal event Action<ConsoleApiException>? SessionExpired;

    public SubjectSetupWindow() => InitializeComponent();

    internal SubjectSetupWindow(ConsoleClient client, School school, string termId, string classId, string[] initialClasses) : this()
    {
        _client = client;
        _school = school;
        _termId = termId;
        _classId = classId;
        _initialClasses = initialClasses;
        SubjectSetupScope.Text = school.Name;
        Opened += async (_, _) => await RunAsync(ReadCatalogAsync);
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
        var selectedRooms = first ? _initialClasses.ToHashSet(StringComparer.Ordinal) : CheckedIds(_rooms).ToHashSet(StringComparer.Ordinal);
        var selectedSubjects = CheckedIds(_subjects).ToHashSet(StringComparer.Ordinal);
        var catalog = await _client.GetSubjectSetupAsync(_school.Id, _termId, _classId, _lifetime.Token);
        if (_closed) return;
        _catalog = catalog;
        _scopeChanged = false;
        SubjectSetupScope.Text = $"{_school.Name} · {catalog.Classes.Single(room => room.Id == _classId).Name}";
        BindChoices(SubjectSetupClasses, _rooms, catalog.Classes.Select(room => (room.Id, room.Name)), selectedRooms, "SubjectSetupClass-");
        BindChoices(SubjectSetupSubjects, _subjects, catalog.Subjects.Select(subject => (subject.Id, subject.ToString())),
            selectedSubjects, "SubjectSetupSubject-");
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
        SubjectSetupTeachingPicker.IsVisible = SubjectSetupTeachingButton.IsVisible = false;
    }

    private void ShowRows(IEnumerable<SubjectSetupRow> rows, bool canExclude)
    {
        _cells = rows.Select(row => new SubjectSetupCell(row, !_excluded.Contains(row.Target), canExclude, ChoiceChanged)).ToArray();
        SubjectSetupRows.ItemsSource = _cells;
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
            ? "请核对冲突项，逐行取消勾选后再保存。"
            : "请核对逐班逐科的安排，再确认保存。";
    }

    private void Render()
    {
        bool editable = !_busy && !_closed && !_scopeChanged && _catalog is not null && _unconfirmed is null;
        long count = (long)CheckedIds(_rooms).Count() * CheckedIds(_subjects).Count();
        var included = _cells.Where(cell => cell.Included).ToArray();
        SubjectSetupInputs.IsEnabled = editable;
        SubjectSetupRows.IsEnabled = !_busy && _unconfirmed is null;
        SubjectSetupRows.IsVisible = _cells.Length > 0;
        SubjectSetupReadButton.IsEnabled = !_busy;
        SubjectSetupReadButton.Content = _unconfirmed is null ? "刷新目录" : "核对保存结果";
        SubjectSetupPreviewButton.IsEnabled = editable && count > 0;
        SubjectSetupApplyButton.IsEnabled = editable && _validPreview && included.Length > 0 && included.All(cell => cell.Row.CanApply);
        SubjectSetupTeachingButton.IsEnabled = editable && SubjectSetupTeachingPicker.SelectedItem is SubjectSetupCell;
        SubjectSetupWebButton.IsEnabled = !_busy;
        SubjectSetupCount.IsVisible = _catalog is { Subjects.Length: > 0 };
        SubjectSetupCount.Text = _validPreview
            ? $"预览 {_cells.Length} 项 · 已勾选 {included.Length} 项 · 可取消勾选单个班级和学科"
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
        ShowRows(preview.Rows, true);
        UpdatePreviewMessage();
        if (_cells.FirstOrDefault(cell => cell.Included && !cell.Row.CanApply) is { } conflict)
            Dispatcher.UIThread.Post(() => { if (!_closed && _cells.Contains(conflict)) SubjectSetupRows.ScrollIntoView(conflict); },
                DispatcherPriority.Loaded);
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
                _unconfirmed = selected.Select(row => row.Target).ToArray();
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
            ShowRows(_cells.Select(cell => updated.GetValueOrDefault(cell.Row.Target, cell.Row)).ToArray(), true);
            SubjectSetupFeedback.Text = result.State == "STALE"
                ? $"本批未写入。下列安排已变化：{string.Join("、", result.Rows.Take(3).Select(row => $"{row.ClassName} · {row.SubjectName}"))}。请重新预览。"
                : "本批未写入。请取消冲突项后重新预览。";
        });
    }

    private async Task VerifyAsync()
    {
        if (_unconfirmed is not { } targets) return;
        SubjectSetupFeedback.Text = "保存回执未确认，正在只读核对服务器规则…";
        var preview = await _client.PreviewSubjectSetupAsync(_school.Id, _termId, _classId, targets, _lifetime.Token);
        if (_closed) return;
        _unconfirmed = null;
        if (preview.Rows.All(row => row.State == "PRESENT"))
        {
            ShowResult(preview.Rows, "已核对，服务器当前规则与本次选择一致。");
            return;
        }
        var current = preview.Rows.ToDictionary(row => row.Target);
        ShowRows(_cells.Select(cell => current.GetValueOrDefault(cell.Row.Target, cell.Row)).ToArray(), true);
        SubjectSetupFeedback.Text = "已核对，服务器规则与本次选择有差异。请核对后重新预览。";
    }

    private void ShowResult(SubjectSetupRow[] rows, string message)
    {
        ShowRows(rows, false);
        SubjectSetupTeachingPicker.ItemsSource = _cells.OrderByDescending(cell => cell.Row.State == "ADDED")
            .ThenByDescending(cell => cell.Row.ClassId == _classId).ToArray();
        SubjectSetupTeachingPicker.SelectedIndex = 0;
        SubjectSetupTeachingPicker.IsVisible = SubjectSetupTeachingButton.IsVisible = true;
        SubjectSetupFeedback.Text = message;
    }

    private async void ReadClicked(object? sender, RoutedEventArgs args) =>
        await RunAsync(() => _unconfirmed is null ? ReadCatalogAsync() : VerifyAsync());

    private void WebClicked(object? sender, RoutedEventArgs args)
    {
        string path = "请在网页学校管理的「组织与班级 → 学校与学科」打开学科目录。";
        if (SchoolAdminBrowser.Link(_catalog?.AdminUrl, _school.Id, _termId, "structure") is { } url && SchoolAdminBrowser.Open(url))
            SubjectSetupFeedback.Text = "已打开网页学科目录。维护后请返回这里刷新。";
        else SubjectSetupFeedback.Text = path;
    }

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
            SubjectSetupFeedback.Text = _unconfirmed is null ? error.Message : $"保存回执仍未确认。{error.Message}";
            if (error.StatusCode == 401) SessionExpired?.Invoke(error);
        }
        finally { _busy = false; if (!_closed) Render(); }
    }

    private void CloseClicked(object? sender, RoutedEventArgs args) => Close();
    internal void CloseForScopeChange() { _closed = true; Close(); }
}
