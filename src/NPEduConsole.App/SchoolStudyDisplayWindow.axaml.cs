using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed record StudyDisplayLine(string Title, string Detail, StudySeatingBinding? Registration = null)
{
    public bool CanOpenField => Registration?.DeviceId is not null;
}

public sealed partial class SchoolStudyDisplayWindow : Window
{
    private sealed record Choice(string Id, string Label) { public override string ToString() => Label; }
    private sealed record Target(string Type, string Id, string Label, bool Independent, bool Editable)
    {
        public override string ToString() => Label;
    }
    private sealed record Operation(SchoolStudyEdit Edit, SchoolStudyReview Preview, Guid Id);
    private static readonly ConditionalWeakTable<ConsoleClient, Dictionary<(string, string, string), Operation>> PendingByScope = new();
    private static readonly Choice[] NoiseStyles = [new("Status", "只显示状态"), new("StatusTrend", "状态与实时趋势"),
        new("StatusDbfs", "状态与当前单位数值")];
    private static readonly Choice[] SourceModes = [new("OwnClass", "各班自己的作业"), new("SelectedClasses", "指定一个或多个来源班")];
    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly StudyEvidenceScope _scope = null!;
    private readonly DateOnly _date;
    private readonly bool _legacy;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StudyDisplaySourcesView _sources = new();
    private SchoolStudyCatalog _catalog = null!;
    private StudyDisplayOptions _original = null!;
    private StudyDisplayOptions? _draftBeforeReview;
    private StudyDisplaySnapshot? _display;
    private SchoolStudyReview? _review;
    private SchoolStudyEdit? _edit;
    private DeviceFieldActionWindow? _fieldAction;
    private bool _busy, _filling, _closed, _scopeLost, _closeConfirmed, _showPreview, _compact, _sampleSignal = true, _checkedMissing;
    private int? _savedRevision;
    private int _page;
    private DisplayPage[] _pages = [];

    private Dictionary<(string, string, string), Operation> Operations => PendingByScope.GetOrCreateValue(_client);
    private (string, string, string) OperationKey => (_school.Id, _scope.TermId, _scope.ClassId);
    private Operation? Pending => Operations.GetValueOrDefault(OperationKey);
    private Target? Selected => DisplayScopeChoice.SelectedItem as Target;
    private bool Dirty => _original is not null && (ReadControls() is not { } value || !_original.SameSettings(value));
    internal bool Published { get; private set; }
    internal bool OpenSchedule { get; private set; }

    public SchoolStudyDisplayWindow()
    {
        InitializeComponent();
        DisplaySourceControl.Content = _sources;
        DisplayNoiseStyle.ItemsSource = NoiseStyles;
        DisplaySourceMode.ItemsSource = SourceModes;
        _sources.SelectionChanged += (_, _) => Changed();
    }

    internal SchoolStudyDisplayWindow(ConsoleClient client, School school, SchoolStudyCatalog catalog, string room, DateOnly date,
        bool legacy) : this()
    {
        _client = client;
        _school = school;
        _catalog = catalog;
        _date = date;
        _legacy = legacy;
        _scope = new(school.Id, catalog.TermId!, room);
        BindTargets();
        Opened += async (_, _) =>
        {
            BatchWorkspaceWindow.FitSchoolWindow(this);
            Arrange();
            await RunAsync(Pending is { } pending ? () => VerifyAsync(pending) : ReadDisplayAsync);
        };
        SizeChanged += (_, _) => Arrange();
        Closing += (_, args) =>
        {
            if (_closeConfirmed || _scopeLost || _closed) return;
            if (_busy) { args.Cancel = true; return; }
            if (!Dirty || Pending is not null) return;
            args.Cancel = true;
            _closeConfirmed = true;
            DisplayClose.Content = "放弃草稿并返回";
            Notice("有尚未保存的设置。可继续编辑，或再次确认返回。");
        };
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _lifetime.Dispose(); };
        Render();
    }

    internal void CloseForScopeChange()
    {
        _closeConfirmed = true;
        _fieldAction?.Close();
        Close();
    }

    private void BindTargets(Target? previous = null, StudyDisplayOptions? preserve = null)
    {
        var room = _catalog.Classes.First(row => row.Id == _scope.ClassId);
        var local = _catalog.Policy("CLASS", room.Id);
        var grade = _catalog.Grades.FirstOrDefault(row => row.Id == room.GradeId);
        var gradePolicy = grade is null ? null : _catalog.Policy("GRADE", grade.Id);
        bool inherited = local?.Mode is null or "Inherit" && gradePolicy?.Mode == "Override";
        var current = new Target("CLASS", room.Id, inherited ? $"仅为{room.Name}单独设置" : $"本班 · {room.Name}",
            inherited, inherited || local?.Mode == "Override");
        var options = new List<Target> { current };
        if (grade is not null && gradePolicy?.Mode == "Override") options.Add(new("GRADE", grade.Id, $"年级 · {grade.Name}", false, true));
        var target = options.FirstOrDefault(row => row.Type == previous?.Type && row.Id == previous.Id) ?? (inherited ? options[1] : current);
        _filling = true;
        DisplayScopeChoice.ItemsSource = options;
        DisplayScopeChoice.SelectedItem = target;
        _filling = false;
        BindTarget(target, preserve);
        string roomLabel = grade is null || room.Name.StartsWith(grade.Name, StringComparison.Ordinal) ? room.Name : $"{grade.Name} {room.Name}";
        DisplayHeading.Text = $"{_school.Name} · {_catalog.Terms.First(row => row.Id == _scope.TermId).Name} · " +
            $"{roomLabel} · {_date:M月d日}（学校日期）";
    }

    private void BindTarget(Target target, StudyDisplayOptions? preserve = null)
    {
        _original = StudyDisplayOptions.Read(_catalog.BasePolicy(target.Type, target.Id));
        var value = preserve ?? _original;
        _filling = true;
        DisplayNoiseStyle.SelectedItem = NoiseStyles.SingleOrDefault(row => row.Id == value.NoiseDisplay);
        DisplaySourceMode.SelectedItem = SourceModes.SingleOrDefault(row => row.Id == value.SourceMode);
        DisplayHomeworkEnabled.IsChecked = value.HomeworkEnabled;
        DisplayRotation.Text = value.RotationSeconds.ToString(CultureInfo.InvariantCulture);
        var grades = _catalog.Grades.ToDictionary(row => row.Id, row => row.Name);
        var available = _catalog.Classes.Select(room => new StudyDisplaySourceOption(room.Id, room.Name, room.Code ?? "",
            room.GradeId is { } grade ? grades.GetValueOrDefault(grade) ?? "" : "")).ToArray();
        var removed = value.SourceClassIds.Except(available.Select(row => row.Id)).Select((id, index) =>
            new StudyDisplaySourceOption(id, $"不可用来源 {index + 1}（取消勾选后可继续）", "", ""));
        _sources.SetOptions(available.Concat(removed).ToArray(), value.SourceClassIds);
        _filling = false;
        DisplayInheritance.Text = target.Independent
            ? "单独设置会复制本班当前生效的整套年级策略。此后日常、考试排期、监测和照片设置也由本班独立维护。"
            : target.Type == "GRADE" ? "年级设置作用于继承的班级。选择各班自己的作业时，每个继承班分别取本班。"
            : target.Editable ? "只修改本班展示和作业选项。" :
                _catalog.BasePolicy("CLASS", _scope.ClassId)?.Mode == "Disabled" ? "本班学校自习已关闭。请先核对自习安排。"
                : _legacy ? "本班沿用旧定时噪音安排。启用学校自习请先办理排期。"
                : "本班暂无可用的学校自习策略。请先办理自习安排。";
    }

    private StudyDisplayOptions? ReadControls()
    {
        if (DisplayNoiseStyle.SelectedItem is not Choice style || DisplaySourceMode.SelectedItem is not Choice source ||
            !int.TryParse(DisplayRotation.Text, out int seconds)) return null;
        return new(style.Id, DisplayHomeworkEnabled.IsChecked == true, source.Id,
            source.Id == "SelectedClasses" ? _sources.SelectedIds.ToArray() : [], seconds);
    }

    private void ScopeChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_filling || _client is null || Selected is not { } target) return;
        if (Dirty)
        {
            var prior = _targetWithDraft;
            if (prior is not null)
            {
                _filling = true;
                DisplayScopeChoice.SelectedItem = prior;
                _filling = false;
                Notice("本范围有草稿。先保存或放弃草稿，再切换范围。");
                return;
            }
        }
        _review = null; _edit = null; _draftBeforeReview = null;
        BindTarget(target);
        _targetWithDraft = target;
        DropDraftPreview();
        Render();
    }

    private Target? _targetWithDraft;
    private void FieldSelectionChanged(object? sender, SelectionChangedEventArgs args) => Changed();
    private void FieldToggled(object? sender, RoutedEventArgs args) => Changed();
    private void FieldChanged(object? sender, TextChangedEventArgs args) => Changed();
    private void Changed()
    {
        if (_filling || _client is null) return;
        _targetWithDraft = Selected;
        _closeConfirmed = false;
        _review = null; _edit = null; _draftBeforeReview = null;
        DropDraftPreview();
        Render();
    }

    private StudyDisplaySnapshot? _savedDisplay;
    private void DropDraftPreview()
    {
        if (_display != _savedDisplay) SetDisplay(_savedDisplay);
        Notice(Dirty ? "设置已形成草稿，预览会重新核对实际来源。" : "");
    }

    private async Task RunAsync(Func<Task> work)
    {
        if (_busy || _closed) return;
        _busy = true;
        _closeConfirmed = false;
        DisplayClose.Content = "返回";
        Render();
        try { await work(); }
        catch (OperationCanceledException) when (_closed) { }
        catch (ArgumentException error) { if (!_closed) Notice(error.Message); }
        catch (ConsoleApiException error)
        {
            if (_closed) return;
            if (error.StatusCode is 401 or 403 || error.Code is "TERM_NOT_ACTIVE")
            {
                _scopeLost = true;
                _savedDisplay = null;
                SetDisplay(null);
                DisplayScreenRows.ItemsSource = null;
                DisplayScopeChoice.ItemsSource = null;
            }
            Notice(error.Message);
        }
        finally { _busy = false; if (!_closed) Render(); }
    }

    private void Notice(string text) => DisplayNotice.Text = text;
    private void Render()
    {
        var target = Selected;
        var pending = Pending;
        var editable = !_scopeLost && !_busy && pending is null && target?.Editable == true;
        DisplayScopeChoice.IsEnabled = !_scopeLost && !_busy && pending is null;
        DisplayFields.IsEnabled = editable;
        DisplayFields.IsVisible = !_scopeLost && target?.Editable == true;
        DisplayOpenSchedule.IsVisible = !_scopeLost && target is { Type: "CLASS", Editable: false };
        DisplayOpenSchedule.IsEnabled = !_busy && pending is null;
        DisplaySourcePane.IsVisible = DisplaySourceMode.SelectedItem is Choice { Id: "SelectedClasses" };
        DisplayPlan.IsVisible = !_scopeLost && _review is null && pending is null;
        DisplayPlan.IsEnabled = editable && (Dirty || target?.Independent == true);
        DisplaySave.IsVisible = !_scopeLost && _review is not null && pending is null;
        DisplaySave.IsEnabled = editable && _review is not null;
        DisplaySave.Content = target?.Independent == true ? "确认本班独立并保存" : "确认保存";
        DisplayReload.IsEnabled = !_scopeLost && !_busy && pending is null;
        DisplayDiscard.IsVisible = editable && Dirty;
        DisplayCheckReceipt.IsVisible = !_scopeLost && pending is not null;
        DisplayRetry.IsVisible = DisplayCheckReceipt.IsVisible && _checkedMissing;
        DisplayCheckReceipt.IsEnabled = DisplayRetry.IsEnabled = !_busy;
        DisplayClose.IsEnabled = !_busy;
        DisplayImpactPanel.IsVisible = _review is not null;
        DisplayRefreshScreens.IsEnabled = !_scopeLost && !_busy;
        DisplayScreenRows.IsEnabled = DisplayRefreshScreens.IsEnabled;
        RenderSample();
    }

    private void Arrange()
    {
        _compact = ClientSize.Width is > 0 and < 900;
        ShowDisplaySettings.IsVisible = ShowDisplayPreview.IsVisible = _compact;
        DisplayColumns.ColumnDefinitions[0].Width = new(1, GridUnitType.Star);
        DisplayColumns.ColumnDefinitions[1].Width = _compact ? new(0) : new(1, GridUnitType.Star);
        DisplayColumns.ColumnSpacing = _compact ? 0 : 8;
        DisplaySettingsPane.IsVisible = !_compact || !_showPreview;
        DisplayPreviewPane.IsVisible = !_compact || _showPreview;
        Grid.SetColumn(DisplayPreviewPane, _compact ? 0 : 1);
        ShowDisplaySettings.Classes.Set("accent", !_showPreview);
        ShowDisplayPreview.Classes.Set("accent", _showPreview);
    }

    private void ShowSettings(object? sender, RoutedEventArgs args) { _showPreview = false; Arrange(); }
    private void ShowPreview(object? sender, RoutedEventArgs args) { _showPreview = true; Arrange(); }
    private void OpenScheduleClicked(object? sender, RoutedEventArgs args) { OpenSchedule = true; Close(); }
    private void BackClicked(object? sender, RoutedEventArgs args) => Close();
}
