using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

public sealed record SchoolStudyDayOption(DateOnly Date)
{
    public override string ToString() => Date.ToString("M/d ddd", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));
}
public sealed record SchoolStudyRoomRow(SchoolStudyCalendarRoom Room, string Name, string Source, string Daily, string Exam);

public sealed partial class SchoolStudyWindow : Window
{
    private readonly ConsoleClient _client = null!;
    private readonly School _school = null!;
    private readonly CancellationTokenSource _lifetime = new();
    private SchoolStudyCatalog? _catalog;
    private SchoolStudyCalendar? _calendar;
    private CancellationTokenSource? _read;
    private Window? _child;
    private DateOnly _week, _date;
    private string? _gradeId, _roomId;
    private bool _filling, _busy, _closed;

    public SchoolStudyWindow() => InitializeComponent();

    internal SchoolStudyWindow(ConsoleClient client, School school, string? initialRoom) : this()
    {
        _client = client;
        _school = school;
        _roomId = initialRoom;
        _date = SchoolToday();
        _week = Monday(_date);
        SchoolStudyScope.Text = school.Name;
        Opened += async (_, _) =>
        {
            BatchWorkspaceWindow.FitSchoolWindow(this);
            ArrangeStudyWindow(force: true);
            await LoadAsync(readCatalog: true);
        };
        SizeChanged += (_, _) => Dispatcher.UIThread.Post(() => { if (!_closed) ArrangeStudyWindow(); }, DispatcherPriority.Loaded);
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            _read?.Cancel();
            _child?.Close();
            _lifetime.Dispose();
        };
        BindWeek();
        UpdateControls();
    }

    internal void CloseForScopeChange()
    {
        if (_child is SchoolStudyEditWindow editor) editor.CloseForScopeChange();
        else if (_child is SchoolStudySeatingWindow seating) seating.CloseForScopeChange();
        else _child?.Close();
        Close();
    }

    internal static DateOnly SchoolToday() => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).DateTime);
    private static readonly DateOnly Earliest = new(2000, 1, 1), Latest = new(9998, 12, 31);
    private static DateOnly Monday(DateOnly value)
    {
        var week = value.AddDays(-(((int)value.DayOfWeek + 6) % 7));
        return week < Earliest ? Earliest : week > Latest.AddDays(-6) ? Latest.AddDays(-6) : week;
    }
    private SchoolStudyRoomRow? Room => StudyRooms.SelectedItem as SchoolStudyRoomRow;

    private async Task LoadAsync(bool readCatalog = false)
    {
        _read?.Cancel();
        using var read = _read = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _busy = true;
        _calendar = null;
        BindRooms();
        SchoolStudyStatus.Text = "正在读取学校安排…";
        try
        {
            if (_catalog is null || readCatalog)
            {
                var catalog = await _client.ReadSchoolStudyAsync(_school.Id, read.Token);
                if (_read != read || _closed) return;
                _catalog = catalog;
                _gradeId = catalog.Grades.FirstOrDefault(row => row.Id == _gradeId)?.Id ??
                    catalog.Classes.FirstOrDefault(row => row.Id == _roomId)?.GradeId ?? catalog.Grades.FirstOrDefault()?.Id;
                SchoolStudyScope.Text = $"{_school.Name} · {catalog.Terms.FirstOrDefault(row => row.Id == catalog.TermId)?.Name ?? "暂无启用学期"}";
                _filling = true;
                SchoolStudyGrade.ItemsSource = catalog.Grades;
                SchoolStudyGrade.SelectedItem = catalog.Grades.FirstOrDefault(row => row.Id == _gradeId);
                _filling = false;
            }
            if (_catalog.TermId is not { } term) { SchoolStudyStatus.Text = "请先在学期管理启用学期。"; return; }
            if (_gradeId is not { } grade) { SchoolStudyStatus.Text = "当前学期暂无年级。"; return; }
            var calendar = await _client.ReadSchoolStudyCalendarAsync(_school.Id, term, grade, _week, 7, read.Token);
            if (_read != read || _closed) return;
            _calendar = calendar;
            SchoolStudyStatus.Text = calendar.Classes.Length == 0 ? "该年级暂无启用的行政班。" : "";
        }
        catch (OperationCanceledException) when (read.IsCancellationRequested) { }
        catch (ConsoleApiException error)
        {
            if (_read != read || _closed) return;
            SchoolStudyStatus.Text = error.Code == "TERM_NOT_ACTIVE" ? "启用学期已变化，请刷新后查看新学期。" : error.Message;
        }
        finally
        {
            if (_read == read)
            {
                _read = null;
                _busy = false;
                if (!_closed) BindRooms();
            }
        }
    }

    private void BindWeek()
    {
        var days = Enumerable.Range(0, 7).Select(index => new SchoolStudyDayOption(_week.AddDays(index))).ToArray();
        _filling = true;
        StudyDays.ItemsSource = days;
        StudyDays.SelectedItem = days.Single(row => row.Date == _date);
        StudyJumpDate.SelectedDate = _date.ToDateTime(TimeOnly.MinValue);
        _filling = false;
        StudyWeekLabel.Text = $"{_week:yyyy/M/d} 至 {_week.AddDays(6):M/d} · 学校时间";
    }

    private void BindRooms()
    {
        int index = _date.DayNumber - _week.DayNumber;
        var rows = _calendar?.Classes.Select(room =>
        {
            var day = room.Dates[index];
            return new SchoolStudyRoomRow(room, room.Name, SourceText(room.Source, room.LegacyNoise),
                "日常 · " + DescribeDay(day.Daily, day.Date), "考试 · " + DescribeDay(day.Exam, day.Date));
        }).ToArray() ?? [];
        _filling = true;
        StudyRooms.ItemsSource = rows;
        StudyRooms.SelectedItem = rows.FirstOrDefault(row => row.Room.ClassId == _roomId) ?? rows.FirstOrDefault();
        _filling = false;
        if (Room is { } selected) _roomId = selected.Room.ClassId;
        StudyDateSummary.Text = $"{_date:M 月 d 日} · 各班实际安排";
        BindRoom();
    }

    private void BindRoom()
    {
        StudyRoomTitle.Text = Room?.Name ?? "当天的班级安排";
        StudyRoomSource.Text = Room?.Source ?? "";
        StudyRoomDay.Text = Room is { } selected ? selected.Daily + "\n" + selected.Exam : "";
        UpdateControls();
    }

    private void UpdateControls()
    {
        bool hasRoom = !_busy && Room is not null;
        SchoolStudyEditGrade.IsEnabled = !_busy && _catalog?.TermId is not null && _gradeId is not null && _calendar is not null;
        StudyRoomOpenButton.IsEnabled = StudyEditClassButton.IsEnabled = StudySeatingButton.IsEnabled = StudyReportsButton.IsEnabled = hasRoom;
        SchoolStudyRefresh.IsEnabled = !_busy;
    }

    private async void GradeChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_filling || _client is null || SchoolStudyGrade.SelectedItem is not StudyTargetLabel grade) return;
        _gradeId = grade.Id;
        _roomId = null;
        _compactDetail = false;
        ArrangeStudyWindow(force: true);
        await LoadAsync();
    }

    private void DayChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_filling || StudyDays.SelectedItem is not SchoolStudyDayOption day) return;
        _date = day.Date;
        _filling = true;
        StudyJumpDate.SelectedDate = _date.ToDateTime(TimeOnly.MinValue);
        _filling = false;
        BindRooms();
    }

    private void RoomChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_filling || _client is null) return;
        if (Room is { } selected) _roomId = selected.Room.ClassId;
        BindRoom();
    }

    private async Task BrowseAsync(DateOnly date)
    {
        if (date < Earliest || date > Latest) { SchoolStudyStatus.Text = "请选择学校日历支持的 2000 至 9998 年日期。"; return; }
        _date = date;
        var week = Monday(date);
        bool changed = _week != week;
        _week = week;
        BindWeek();
        if (changed) await LoadAsync();
        else BindRooms();
    }

    private async void PriorWeek(object? sender, RoutedEventArgs args) => await BrowseAsync(_date.AddDays(-7));
    private async void NextWeek(object? sender, RoutedEventArgs args) => await BrowseAsync(_date.AddDays(7));
    private async void ThisWeek(object? sender, RoutedEventArgs args) => await BrowseAsync(SchoolToday());
    private async void JumpDateChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!_filling && _client is not null && StudyJumpDate.SelectedDate is { } date) await BrowseAsync(DateOnly.FromDateTime(date));
    }

    private async void EditGrade(object? sender, RoutedEventArgs args)
    {
        if (_gradeId is not null && SchoolStudyGrade.SelectedItem is StudyTargetLabel grade)
            await OpenEditor("GRADE", grade.Id, grade.Name, SchoolStudyEditGrade);
    }

    private async void EditClass(object? sender, RoutedEventArgs args)
    {
        if (Room is { } row) await OpenEditor("CLASS", row.Room.ClassId, row.Name, StudyEditClassButton);
    }

    private async Task OpenEditor(string type, string id, string name, Control returnTo)
    {
        if (_child is not null || _busy || _catalog is not { } catalog) return;
        var editor = new SchoolStudyEditWindow(_client, _school, catalog, type, id, name, _date);
        await OpenChild(editor);
        if (_closed) return;
        if (editor.Published) await LoadAsync(readCatalog: true);
        if (!_closed) returnTo.Focus();
    }

    private async void OpenSeating(object? sender, RoutedEventArgs args)
    {
        if (_child is not null || _busy || Room is not { } row || _catalog?.TermId is not { } term) return;
        var window = new SchoolStudySeatingWindow(_client, _school.Name, row.Name, new(_school.Id, term, row.Room.ClassId));
        await OpenChild(window);
        if (!_closed) StudySeatingButton.Focus();
    }

    private async void OpenReports(object? sender, RoutedEventArgs args)
    {
        if (_child is not null || _busy || Room is not { } row || _catalog?.TermId is not { } term) return;
        var scope = new StudyEvidenceScope(_school.Id, term, row.Room.ClassId);
        var view = new StudyEvidenceView();
        var title = new TextBlock { Text = $"{_school.Name} · {row.Name} · {_date:yyyy/M/d}", FontSize = 20 };
        var content = new StackPanel { Spacing = 12, Margin = new(15), Children = { title, view } };
        var window = new Window { Title = "本日场次与回执", Width = 820, Height = 620, MinHeight = 440, MinWidth = 650,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ScrollViewer { Content = content } };
        var date = _date;
        window.Closed += (_, _) => view.SetScope(null, null, null, null);
        window.Opened += async (_, _) =>
        {
            BatchWorkspaceWindow.FitSchoolWindow(window);
            Classroom room = new(row.Room.ClassId, row.Name, null, []);
            try
            {
                var seating = await _client.ReadSchoolSeatingAsync(scope, _lifetime.Token);
                room = room with { Screens = seating.Bindings.Select(binding => new Screen(binding.BindingId, binding.Name,
                    binding.DeviceId is { } device ? [new(device, "教室终端", "UNKNOWN", null)] : [])).ToArray() };
            }
            catch (ConsoleApiException error) { if (window.IsVisible) title.Text += "\n设备列表待核对 · " + error.Message; }
            catch (OperationCanceledException) { return; }
            if (window.IsVisible) view.SetScope(_client, _school.Id, term, room, date);
        };
        await OpenChild(window);
        if (!_closed) StudyReportsButton.Focus();
    }

    private async Task OpenChild(Window window)
    {
        _child = window;
        window.Closed += (_, _) => { if (_child == window) _child = null; };
        try { await window.ShowDialog(this); }
        finally { if (_child == window) _child = null; }
    }

    private async void RefreshStudy(object? sender, RoutedEventArgs args) => await LoadAsync(readCatalog: true);
    private void CloseStudy(object? sender, RoutedEventArgs args) => Close();

    internal static string SourceText(string source, bool legacy = false) => source switch
    {
        "Grade" => "继承年级", "Class" => "本班独立安排", "Disabled" => "已关闭",
        _ when legacy => "沿用旧定时噪音安排", _ => "暂无学校自习策略"
    };

    internal static string WindowsText(IEnumerable<StudyWindow> windows) => string.Join("、", windows.Select(span =>
        span.Start.Date == span.End.Date ? $"{span.Start:HH:mm}–{span.End:HH:mm}" : $"{span.Start:M/d HH:mm}–{span.End:M/d HH:mm}"));

    private static string DescribeDay(SchoolStudyDayMode mode, DateOnly date)
    {
        string caption = mode.Exception switch
        {
            "UseWeekday" => "按周" + "一二三四五六日"[mode.Weekday - 1] + "安排 · ",
            "Skip" when mode.Windows.Length > 0 => "当天停排，保留前日跨夜场次 · ",
            "Skip" => "当天停排",
            _ => ""
        };
        if (mode.Exception == "Skip" && mode.Windows.Length == 0) return caption;
        return caption + (mode.Windows.Length == 0 ? "无时段" : WindowsText(mode.Windows));
    }
}
