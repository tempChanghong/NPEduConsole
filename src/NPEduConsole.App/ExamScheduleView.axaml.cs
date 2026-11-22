using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using NPEduConsole.Core;

namespace NPEduConsole.App;

/// <summary>
/// Read-only preview of the schedule the screen reported. Display formatting preserves
/// the date, time and offset supplied by the device.
/// </summary>
public sealed partial class ExamScheduleView : UserControl
{
    private const double CompactWidth = 870;
    private readonly List<Control> _rowTargets = [];
    private IReadOnlyList<ExamScheduleEntry>? _entries;
    private bool _compact;
    private bool _arranged;

    public ExamScheduleView()
    {
        InitializeComponent();
        AutomationProperties.SetName(this, "考试考程预览");
        SizeChanged += (_, _) => Arrange();
    }

    /// <summary>Replaces the preview. A null or empty list shows the "not reported" note.</summary>
    public void SetSchedule(IReadOnlyList<ExamScheduleEntry>? entries)
    {
        _entries = entries is { Count: > 0 } ? entries : null;
        Rebuild();
        Arrange(force: true);
    }

    /// <summary>Moves keyboard focus onto the first exam, when the preview has one.</summary>
    public void FocusFirstEntry()
    {
        if (_entries is not { Count: > 0 }) return;
        Dispatcher.UIThread.Post(() =>
        {
            var target = _rowTargets.FirstOrDefault(Usable);
            if (target is null) return;
            target.BringIntoView();
            target.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private void Rebuild()
    {
        ExamScheduleRows.Children.Clear();
        ExamScheduleCards.Children.Clear();
        _rowTargets.Clear();
        if (_entries is null) return;
        foreach (var entry in _entries)
        {
            var row = WideRow(entry);
            ExamScheduleRows.Children.Add(row);
            _rowTargets.Add(row);
            var card = CompactCard(entry);
            ExamScheduleCards.Children.Add(card);
            _rowTargets.Add(card);
        }
    }

    private void Arrange(bool force = false)
    {
        var width = Bounds.Width;
        var compact = width > 0 && width < CompactWidth;
        if (!force && _arranged && compact == _compact) return;
        _compact = compact;
        _arranged = true;
        Layout();
    }

    private void Layout()
    {
        var hasEntries = _entries is { Count: > 0 };
        ExamScheduleEmpty.IsVisible = !hasEntries;
        ExamScheduleWide.IsVisible = hasEntries && !_compact;
        ExamScheduleCompact.IsVisible = hasEntries && _compact;
        AutomationProperties.SetName(ExamScheduleEmpty,
            hasEntries ? "考试考程预览" : "设备尚未回传考程。");
        if (hasEntries) KeepFocus();
    }

    /// <summary>Restores focus when the layout switch hides the row that had it.</summary>
    private void KeepFocus()
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not Control focused) return;
        if (!_rowTargets.Contains(focused) || focused.IsEffectivelyVisible) return;
        _rowTargets.FirstOrDefault(Usable)?.Focus(NavigationMethod.Tab);
    }

    private static bool Usable(Control control) => control.IsEffectivelyVisible && control.IsEffectivelyEnabled;

    private Control WideRow(ExamScheduleEntry entry)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,1.4*,1.4*,1*"), ColumnSpacing = 8 };
        Add(grid, Cell(Text(entry.Name), "exam-cell"), 0);
        Add(grid, Cell(Text(entry.Start), "exam-cell"), 1);
        Add(grid, Cell(Text(entry.End), "exam-cell"), 2);
        Add(grid, Cell(AlertText(entry.AlertTime), "exam-cell"), 3);
        var row = new Border { Child = grid, Focusable = true };
        row.Classes.Add("exam-row");
        AutomationProperties.SetName(row, RowName(entry));
        return row;
    }

    private Control CompactCard(ExamScheduleEntry entry)
    {
        var lines = new StackPanel { Spacing = 2 };
        lines.Children.Add(Cell(Text(entry.Name), "exam-name"));
        var timing = Cell(CompactTime(entry.Start, entry.End), "exam-note");
        ToolTip.SetTip(timing, $"{Text(entry.Start)} — {Text(entry.End)}");
        lines.Children.Add(timing);
        lines.Children.Add(Cell(AlertText(entry.AlertTime), "exam-note"));
        var card = new Border { Child = lines, Focusable = true };
        card.Classes.Add("card");
        card.Classes.Add("exam-row");
        AutomationProperties.SetName(card, RowName(entry));
        return card;
    }

    private static TextBlock Cell(string value, string styleClass)
    {
        var block = new TextBlock { Text = value };
        block.Classes.Add(styleClass);
        return block;
    }

    private static void Add(Grid grid, Control child, int column)
    {
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }

    private static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    private static string CompactTime(string start, string end)
    {
        if (!ReadTimestamp(start, out var startDate, out var startTime) ||
            !ReadTimestamp(end, out var endDate, out var endTime))
            return $"{Text(start)} — {Text(end)}";
        if (startDate == endDate) return $"{startDate:M 月 d 日} {startTime}–{endTime}";
        string dateFormat = startDate.Year == endDate.Year ? "M 月 d 日" : "yyyy 年 M 月 d 日";
        return $"{startDate.ToString(dateFormat, CultureInfo.InvariantCulture)} {startTime} — " +
            $"{endDate.ToString(dateFormat, CultureInfo.InvariantCulture)} {endTime}";
    }

    private static bool ReadTimestamp(string value, out DateOnly date, out string time)
    {
        date = default;
        time = "";
        var match = Timestamp().Match(Text(value));
        if (!match.Success || !DateOnly.TryParseExact(match.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out date)) return false;
        string seconds = match.Groups["seconds"].Value;
        string fraction = match.Groups["fraction"].Value;
        string offset = match.Groups["offset"].Value;
        time = match.Groups["clock"].Value + (seconds == ":00" && fraction.Length == 0 ? "" : seconds) + fraction;
        if (offset.Length > 0) time += " " + offset;
        return true;
    }

    [GeneratedRegex(@"\A(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})[T ](?<clock>(?:[01][0-9]|2[0-3]):[0-5][0-9])(?:(?<seconds>:[0-5][0-9])(?<fraction>\.[0-9]+)?)?(?<offset>Z|[+-](?:0[0-9]|1[0-4]):[0-5][0-9])?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Timestamp();

    /// <summary>Matches the old page: no reminder for non-positive values, otherwise "结束前 N 分钟".</summary>
    private static string AlertText(double minutes) =>
        minutes <= 0 ? "不提醒" : $"结束前 {minutes:0.##} 分钟";

    private static string RowName(ExamScheduleEntry entry) =>
        $"{Text(entry.Name)}，{Text(entry.Start)} 至 {Text(entry.End)}，{AlertText(entry.AlertTime)}";
}
