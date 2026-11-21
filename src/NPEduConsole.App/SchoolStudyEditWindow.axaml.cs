using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed record SchoolStudyImpactView(string ClassId, string Name, string Before, string After, string Action, bool NeedsReview);

public sealed partial class SchoolStudyEditWindow : Window
{
    private const string ChineseWeek = "一二三四五六日";
    private sealed record Choice(string Key, string Label) { public override string ToString() => Label; }
    private sealed record RuleItem(int Index, StudyRule Rule)
    {
        public override string ToString() => "周" + string.Join("、", Rule.Days.Select(day => ChineseWeek[day - 1])) +
            $" · {Rule.Start:HH\\:mm}–{Rule.End:HH\\:mm}" + (Rule.End < Rule.Start ? " · 跨午夜" : "");
    }
    private sealed record ExceptionItem(int Index, StudyDateException Exception)
    {
        public override string ToString() => $"{Exception.Date:yyyy/MM/dd} · " + (Exception.Weekday is { } day
            ? "按周" + ChineseWeek[day - 1] + "安排" : "当天停排");
    }
    private sealed record Operation(SchoolStudyEdit Edit, SchoolStudyReview Review, Guid Id);
    private static readonly ConditionalWeakTable<ConsoleClient, Dictionary<string, Operation>> PendingSaves = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly string _targetType = null!, _targetId = null!, _term = null!, _key = null!;
    private readonly DateOnly _date;
    private SchoolStudyCatalog _catalog = null!;
    private readonly CheckBox[] _days = new CheckBox[7];
    private readonly StudySchedule[] _draft = new StudySchedule[2];
    private readonly bool[] _edited = new bool[2];
    private readonly HashSet<string> _affected = [];
    private int _calendarIndex;
    private bool _filling, _busy, _closed, _allowClose, _dirty, _confirm, _retry, _scopeChanged;
    private SchoolStudyEdit? _edit;
    private SchoolStudyReview? _review;

    public SchoolStudyEditWindow() => InitializeComponent();

    internal SchoolStudyEditWindow(ConsoleClient client, School school, SchoolStudyCatalog catalog, string type, string id, string name, DateOnly date) : this()
    {
        _client = client;
        _school = school;
        _catalog = catalog;
        _targetType = type;
        _targetId = id;
        _term = catalog.TermId ?? throw new ArgumentException("An active term is required.");
        _key = string.Join("/", school.Id, _term, type, id);
        _date = date;
        StudyEditTitle.Text = name + (type == "GRADE" ? " · 年级自习" : " · 校内自习");
        StudyEditScope.Text = $"{school.Name} · {catalog.Terms.FirstOrDefault(row => row.Id == _term)?.Name} · 预览 {date:yyyy/M/d}";
        _filling = true;
        StudyPolicyMode.ItemsSource = type == "CLASS" ? new Choice[] {new("Inherit", "继承年级"), new("Override", "本班独立设置"), new("Disabled", "本班关闭")}
            : new Choice[] {new("Override", "设置年级安排"), new("Disabled", "关闭年级安排")};
        string savedMode = catalog.Policy(type, id)?.Mode ?? (type == "CLASS" ? "Inherit" : "Override");
        StudyPolicyMode.SelectedItem = StudyPolicyMode.Items.OfType<Choice>().First(row => row.Key == savedMode);
        StudyScheduleMode.ItemsSource = new[] {"日常", "考试"};
        StudyScheduleMode.SelectedIndex = 0;
        StudyExceptionKind.ItemsSource = new Choice[] {new("Skip", "当天停排"), new("UseWeekday", "按另一个星期几")};
        StudyExceptionWeekday.ItemsSource = Enumerable.Range(0, 7).Select(index => "周" + ChineseWeek[index]).ToArray();
        for (int index = 0; index < 7; index++)
        {
            _days[index] = new CheckBox { Content = "周" + ChineseWeek[index] };
            StudyRuleDays.Children.Add(_days[index]);
        }
        var basis = catalog.BasePolicy(type, id);
        _draft[0] = ConsoleClient.SchoolSchedule(basis, false);
        _draft[1] = ConsoleClient.SchoolSchedule(basis, true);
        _filling = false;
        BindSchedule();
        Opened += async (_, _) =>
        {
            BatchWorkspaceWindow.FitSchoolWindow(this);
            if (Pending is { } operation) await RunAsync(() => VerifyAsync(operation));
        };
        Closing += (_, args) =>
        {
            if (_allowClose) return;
            args.Cancel = _busy || _dirty && !Published && Pending is null;
            if (args.Cancel && !_busy) WarnClose();
        };
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _lifetime.Dispose(); };
        UpdateControls();
    }

    public bool Published { get; private set; }
    private Dictionary<string, Operation> Operations => PendingSaves.GetOrCreateValue(_client);
    private Operation? Pending => Operations.GetValueOrDefault(_key);
    private string Mode => (StudyPolicyMode.SelectedItem as Choice)?.Key ?? "Override";
    private StudySchedule Current => _draft[_calendarIndex];
    internal void CloseForScopeChange() { _allowClose = true; Close(); }

    private void BindSchedule()
    {
        _filling = true;
        StudyFrom.SelectedDate = Current.From?.ToDateTime(TimeOnly.MinValue);
        StudyTo.SelectedDate = Current.To?.ToDateTime(TimeOnly.MinValue);
        StudyRules.ItemsSource = Current.Rules.Select((rule, index) => new RuleItem(index, rule)).ToArray();
        StudyExceptions.ItemsSource = Current.Exceptions.Select((exception, index) => new ExceptionItem(index, exception)).ToArray();
        _filling = false;
        ResetRuleInputs();
        ResetExceptionInputs();
        UpdateControls();
    }

    private void ModeChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_client is null || _filling) return;
        Changed();
    }

    private void ScheduleModeChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_client is null || _filling || StudyScheduleMode.SelectedIndex is < 0 or > 1) return;
        _calendarIndex = StudyScheduleMode.SelectedIndex;
        BindSchedule();
    }

    private void DateBoundChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_client is null || _filling) return;
        SetDraft(Current with {From = Date(StudyFrom), To = Date(StudyTo)}, bind: false);
    }

    private static DateOnly? Date(CalendarDatePicker control) => control.SelectedDate is { } date ? DateOnly.FromDateTime(date) : null;
    private void ClearEndDate(object? sender, RoutedEventArgs args) => StudyTo.SelectedDate = null;
    private void NewRule(object? sender, RoutedEventArgs args) => ResetRuleInputs();
    private void NewException(object? sender, RoutedEventArgs args) => ResetExceptionInputs();

    private void RuleSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (_filling || StudyRules.SelectedItem is not RuleItem item) return;
        StudyRuleStart.Text = item.Rule.Start.ToString("HH:mm", CultureInfo.InvariantCulture);
        StudyRuleEnd.Text = item.Rule.End.ToString("HH:mm", CultureInfo.InvariantCulture);
        for (int index = 0; index < _days.Length; index++) _days[index].IsChecked = item.Rule.Days.Contains(index + 1);
        UpdateControls();
    }

    private void ResetRuleInputs()
    {
        StudyRules.SelectedItem = null;
        StudyRuleStart.Text = StudyRuleEnd.Text = "";
        foreach (var day in _days) day.IsChecked = false;
        UpdateControls();
    }

    private void ApplyRule(object? sender, RoutedEventArgs args)
    {
        var days = _days.Select((item, index) => (item, index)).Where(value => value.item.IsChecked == true).Select(value => value.index + 1).ToArray();
        if (days.Length == 0 || !TimeOnly.TryParseExact(StudyRuleStart.Text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
            !TimeOnly.TryParseExact(StudyRuleEnd.Text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) ||
            start == end || (end - start).TotalMinutes > 180)
        { ShowStatus("请选择星期并输入 1 至 180 分钟的时段，时间格式为 HH:mm。"); return; }
        var rules = Current.Rules.ToList();
        if (StudyRules.SelectedItem is RuleItem item) rules[item.Index] = new(days, start, end);
        else if (rules.Count < 32) rules.Add(new(days, start, end));
        else { ShowStatus("每种模式最多 32 条每周时段。"); return; }
        var from = Current.From ?? _date;
        SetDraft(Current with {Rules = rules, From = from});
    }

    private void RemoveRule(object? sender, RoutedEventArgs args)
    {
        if (StudyRules.SelectedItem is RuleItem item)
            SetDraft(Current with {Rules = Current.Rules.Where((_rule, index) => index != item.Index).ToArray()});
    }

    private void ExceptionKindChanged(object? sender, SelectionChangedEventArgs args) =>
        StudyExceptionWeekday.IsEnabled = (StudyExceptionKind.SelectedItem as Choice)?.Key == "UseWeekday";

    private void ExceptionSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (_filling || StudyExceptions.SelectedItem is not ExceptionItem item) return;
        StudyExceptionDate.SelectedDate = item.Exception.Date.ToDateTime(TimeOnly.MinValue);
        StudyExceptionKind.SelectedIndex = item.Exception.Kind == "Skip" ? 0 : 1;
        StudyExceptionWeekday.SelectedIndex = (item.Exception.Weekday ?? 1) - 1;
        UpdateControls();
    }

    private void ResetExceptionInputs()
    {
        StudyExceptions.SelectedItem = null;
        StudyExceptionDate.SelectedDate = _date.ToDateTime(TimeOnly.MinValue);
        StudyExceptionKind.SelectedIndex = 0;
        StudyExceptionWeekday.SelectedIndex = 3;
        UpdateControls();
    }

    private void ApplyException(object? sender, RoutedEventArgs args)
    {
        var selected = StudyExceptions.SelectedItem as ExceptionItem;
        if (Date(StudyExceptionDate) is not { } date || StudyExceptionKind.SelectedItem is not Choice kind ||
            kind.Key == "UseWeekday" && StudyExceptionWeekday.SelectedIndex < 0)
        { ShowStatus("请选择例外日期及处理方式。"); return; }
        var entries = Current.Exceptions.Where((_entry, index) => index != selected?.Index).ToList();
        if (entries.Count >= 366 || entries.Any(entry => entry.Date == date))
        { ShowStatus("每种模式在同一天只能有一个例外。请先选择已有记录再修改。"); return; }
        entries.Add(new(date, kind.Key, kind.Key == "UseWeekday" ? StudyExceptionWeekday.SelectedIndex + 1 : null));
        SetDraft(Current with {Exceptions = entries.OrderBy(row => row.Date).ToArray(), From = Current.From ?? date});
    }

    private void RemoveException(object? sender, RoutedEventArgs args)
    {
        if (StudyExceptions.SelectedItem is ExceptionItem item)
            SetDraft(Current with {Exceptions = Current.Exceptions.Where((_entry, index) => index != item.Index).ToArray()});
    }

    private void SetDraft(StudySchedule value, bool bind = true)
    {
        _draft[_calendarIndex] = value;
        _edited[_calendarIndex] = true;
        if (bind) BindSchedule();
        Changed();
    }

    private void Changed()
    {
        _dirty = true;
        _confirm = false;
        _review = null;
        SchoolStudyImpact.ItemsSource = null;
        StudyImpactSummary.Text = "";
        ShowStatus(null);
        UpdateControls();
    }

    private SchoolStudyEdit BuildEdit()
    {
        if (_catalog.TermId != _term) throw new ArgumentException("当前启用学期已改变，请返回学校自习刷新。");
        if (Mode == "Override")
            foreach (var schedule in _draft.Where((_item, index) => _edited[index]))
                if (schedule.From is null && schedule.Rules.Count > 0 || schedule.To is not null && (schedule.From is null || schedule.To < schedule.From) ||
                    schedule.Exceptions.Any(row => schedule.From > row.Date || schedule.To < row.Date))
                    throw new ArgumentException("请核对生效日、截止日和例外日期。");
        return ConsoleClient.BuildSchoolStudyEdit(_school.Id, _catalog, _targetType, _targetId, Mode,
            _edited[0] ? _draft[0] : null, _edited[1] ? _draft[1] : null);
    }

    private async void Preview(object? sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        var edit = BuildEdit();
        var result = await _client.PreviewSchoolStudyAsync(edit, _date, _lifetime.Token);
        if (_closed) return;
        _edit = edit; _review = result; _confirm = true;
        _affected.Clear();
        PresentReview(result);
        ShowStatus("已读取逐班影响。核对当前与保存后的时段后确认。");
    });

    private async void Save(object? sender, RoutedEventArgs args)
    {
        if (Pending is { } old)
        {
            if (_retry) await RunAsync(() => SubmitAsync(old));
            return;
        }
        if (!_confirm || _edit is null || _review is null) return;
        var pending = new Operation(_edit, _review, Guid.NewGuid());
        Operations[_key] = pending;
        await RunAsync(() => SubmitAsync(pending));
    }

    private async Task SubmitAsync(Operation operation)
    {
        _retry = false;
        try
        {
            int revision = await _client.SaveSchoolStudyAsync(operation.Edit, operation.Review, operation.Id, _lifetime.Token);
            if (!_closed) Saved(revision);
        }
        catch (ConsoleApiException error) when (error.StatusCode is 0 or 408 or >= 500)
        { if (!_closed) await VerifyAsync(operation); }
        catch (ConsoleApiException error)
        {
            if (_closed) return;
            Operations.Remove(_key);
            _confirm = false;
            if (error.Code == "TERM_NOT_ACTIVE") _scopeChanged = true;
            if (error.Code is "STUDY_SETTING_VERSION_CONFLICT" or "STUDY_PREVIEW_CHANGED")
            {
                await ReviewChangedAsync(operation);
                return;
            }
            throw;
        }
    }

    private async Task ReviewChangedAsync(Operation previous)
    {
        ShowStatus("原预览已失效。本次零写入，草稿已保留，正在重新检查发生变化的班级。");
        var catalog = await _client.ReadSchoolStudyAsync(_school.Id, _lifetime.Token);
        if (_closed) return;
        if (catalog.TermId != _term)
        {
            _scopeChanged = true;
            ShowStatus("当前学期已变更，本次零写入。请返回刷新学期。");
            return;
        }
        _catalog = catalog;
        var edit = BuildEdit();
        var review = await _client.PreviewSchoolStudyAsync(edit, _date, _lifetime.Token);
        if (_closed) return;
        _review = review;
        _affected.Clear();
        var versions = previous.Review.Items.ToDictionary(row => row.ClassId, row => row.ReviewVersion);
        foreach (var row in review.Items.Where(row => versions.GetValueOrDefault(row.ClassId) != row.ReviewVersion)) _affected.Add(row.ClassId);
        PresentReview(review, previous.Review);
        ShowStatus(_affected.Count > 0 || SchoolStudyImpact.Items.OfType<SchoolStudyImpactView>().Any(row => row.NeedsReview)
            ? "学校安排已有变化，本次零写入。请核对标记的班级，再次预览后确认。"
            : "关联的学校设置已变化，本次零写入。请再次预览后确认。");
        if (SchoolStudyImpact.Items.OfType<SchoolStudyImpactView>().FirstOrDefault(row => row.NeedsReview) is { } changed)
        {
            SchoolStudyImpact.SelectedItem = changed;
            Dispatcher.UIThread.Post(() =>
            {
                if (_closed) return;
                SchoolStudyImpact.ScrollIntoView(changed);
                if (SchoolStudyImpact.ContainerFromItem(changed) is ListBoxItem row) row.Focus(NavigationMethod.Tab);
            }, DispatcherPriority.Loaded);
        }
    }

    private void PresentReview(SchoolStudyReview review, SchoolStudyReview? old = null)
    {
        var items = review.Items.Select(row => new SchoolStudyImpactView(row.ClassId, row.Name,
            Summary(row.Before.Source, row.BeforeWindows, row.BeforeExamWindows, row.LegacyNoiseBefore),
            Summary(row.Effective.Source, row.CalendarWindows, row.CalendarExamWindows, row.LegacyNoiseAfter),
            (_affected.Contains(row.ClassId) ? "服务器安排已变化" : row.Changed ? "将随本次改变" : "保持现有安排") +
            (row.LegacyNoiseReplaced ? "\n将接管旧定时噪音安排" : row.LegacyNoiseAfter && !row.LegacyNoiseBefore ? "\n将恢复旧定时噪音安排" : ""), _affected.Contains(row.ClassId))).ToList();
        if (old is not null)
            foreach (var removed in old.Items.Where(row => !items.Any(item => item.ClassId == row.ClassId)))
                items.Add(new(removed.ClassId, removed.Name, Summary(removed.Before.Source, removed.BeforeWindows, removed.BeforeExamWindows, removed.LegacyNoiseBefore),
                    "已从本次范围移出", "班级范围已变化", true));
        SchoolStudyImpact.ItemsSource = items;
        StudyImpactSummary.Text = $"{_date:M 月 d 日} · 共 {review.TotalClasses} 班 · {review.ChangedClasses} 班会改变";
        StudyEditTabs.SelectedIndex = 1;
    }

    private static string Summary(string source, StudyWindow[] daily, StudyWindow[] exam, bool legacy) => SchoolStudyWindow.SourceText(source, legacy) +
        "\n日常 · " + (daily.Length == 0 ? "无时段" : SchoolStudyWindow.WindowsText(daily)) +
        "\n考试 · " + (exam.Length == 0 ? "无时段" : SchoolStudyWindow.WindowsText(exam));

    private async void CheckSave(object? sender, RoutedEventArgs args)
    {
        if (Pending is { } operation) await RunAsync(() => VerifyAsync(operation));
    }

    private async Task VerifyAsync(Operation operation)
    {
        _retry = false;
        var revision = await _client.ReadSchoolStudySaveAsync(operation.Edit, operation.Review, operation.Id, _lifetime.Token);
        if (_closed) return;
        if (revision.HasValue) Saved(revision.Value);
        else
        {
            _retry = true;
            ShowStatus("服务器暂未找到本次回执。可重新核对，或按原编号重试同一次保存。");
        }
    }

    private void Saved(int revision)
    {
        Operations.Remove(_key);
        Published = true;
        _dirty = _confirm = false;
        ShowStatus($"学校已保存排期 · 策略版本 {revision}。教室应用请在本班的大屏回执中核对。");
        StudyEditClose.Content = "完成";
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _closed) return;
        _busy = true;
        UpdateControls();
        try { await action(); }
        catch (OperationCanceledException) when (_closed) { }
        catch (ConsoleApiException error)
        {
            if (!_closed) ShowStatus((Pending is null ? "" : "本次保存回执待核对。") + error.Message);
        }
        catch (ArgumentException error) { if (!_closed) ShowStatus(error.Message); }
        finally { _busy = false; if (!_closed) UpdateControls(); }
    }

    private void ShowStatus(string? text) => StudyEditStatus.Text = text ?? "";

    private void UpdateControls()
    {
        if (_client is null) return;
        bool waiting = Pending is not null;
        bool editing = !_busy && !_scopeChanged && !Published && !waiting;
        bool own = Mode == "Override";
        StudyEditTabs.IsEnabled = !_busy && !waiting;
        StudyPolicyMode.IsEnabled = StudyScheduleMode.IsEnabled = editing;
        StudyScheduleFields.IsVisible = own;
        StudyScheduleFields.IsEnabled = editing;
        StudyEditModeNote.Text = own ? "日常和考试可分别编辑。未编辑的时段、作业来源及监测设置沿用原配置。" :
            Mode == "Inherit" ? "保存后本班采用年级完整策略，清除本班独立安排。" : "保存后关闭此范围的学校自习。年级下的独立班级继续沿用本班安排。";
        StudyEditPreview.IsEnabled = editing;
        StudyEditSave.IsEnabled = !_busy && (_confirm && editing || waiting && _retry);
        StudyEditSave.Content = waiting ? "按原编号重试" : "确认保存";
        StudyReadSave.IsVisible = waiting;
        StudyReadSave.IsEnabled = !_busy;
        StudyEditClose.IsEnabled = !_busy;
        StudyRemoveRule.IsEnabled = StudyRules.SelectedItem is not null;
        StudyRemoveException.IsEnabled = StudyExceptions.SelectedItem is not null;
        StudyApplyRule.Content = StudyRules.SelectedItem is not null ? "更新所选时段" : "加入时段";
        StudyApplyException.Content = StudyExceptions.SelectedItem is not null ? "更新所选例外" : "加入例外";
    }

    private void WarnClose()
    {
        ShowStatus("本页仍有未保存的修改。可继续编辑，或确认放弃后返回。");
        StudyEditClose.Content = "放弃草稿";
    }

    private void CloseEditor(object? sender, RoutedEventArgs args)
    {
        if (_dirty && Pending is null && !Published)
        {
            if (StudyEditClose.Content?.ToString() != "放弃草稿") { WarnClose(); return; }
            _allowClose = true;
        }
        Close();
    }
}
