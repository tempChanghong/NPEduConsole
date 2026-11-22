using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace NPEduConsole.App;

/// <summary>可选为自习展示来源的一个班（学校管理里的一节行政班）。</summary>
public sealed record StudyDisplaySourceOption(string Id, string Name, string Code, string Grade);

/// <summary>列表行：把选项和「是否已选」放在一起，供模板双向绑定。</summary>
public sealed class StudyDisplaySourceRow : INotifyPropertyChanged
{
    private bool _selected;

    public StudyDisplaySourceRow(StudyDisplaySourceOption option, bool selected)
    {
        Option = option;
        _selected = selected;
    }

    public StudyDisplaySourceOption Option { get; }

    public string Name => Option.Name;

    public string Meta => string.Join(" · ", new[] {Option.Code, Option.Grade}
        .Where(part => !string.IsNullOrWhiteSpace(part)));

    public string AutomationName => string.Join(" ", new[] {Option.Grade, Option.Name, Option.Code}
        .Where(part => !string.IsNullOrWhiteSpace(part)));

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 自习展示的来源班级多选：可搜索、可空格切换、Shift 连续选择。
/// 选中集合是唯一的真实状态，列表选中只用于键盘游标。
/// </summary>
public sealed partial class StudyDisplaySourcesView : UserControl
{
    private readonly List<StudyDisplaySourceOption> _options = new();
    private readonly List<StudyDisplaySourceRow> _rows = new();
    private readonly List<StudyDisplaySourceRow> _visible = new();
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private StudyDisplaySourceRow? _anchor;
    private bool _suppress;

    public StudyDisplaySourcesView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, PointerPressedOnRow, RoutingStrategies.Tunnel);
    }

    /// <summary>用户改变勾选时触发；宿主重新读列表不算。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>已选班级的编号，按传入选项的顺序排列。</summary>
    public IReadOnlyList<string> SelectedIds =>
        _options.Where(option => _selected.Contains(option.Id)).Select(option => option.Id).ToArray();

    /// <summary>设置候选班与初始选中集合；不改宿主控件，只重建本控件。</summary>
    public void SetOptions(IReadOnlyList<StudyDisplaySourceOption> options, IEnumerable<string> selected)
    {
        _suppress = true;
        try
        {
            _selected.Clear();
            foreach (var id in selected ?? Enumerable.Empty<string>())
                if (!string.IsNullOrWhiteSpace(id)) _selected.Add(id);

            _options.Clear();
            foreach (var option in options ?? Array.Empty<StudyDisplaySourceOption>())
            {
                if (option is null || string.IsNullOrWhiteSpace(option.Id)) continue;
                if (_options.Any(known => known.Id == option.Id)) continue;
                _options.Add(option);
            }
            _selected.IntersectWith(_options.Select(option => option.Id));

            _rows.Clear();
            foreach (var option in _options)
            {
                var row = new StudyDisplaySourceRow(option, _selected.Contains(option.Id));
                row.PropertyChanged += RowSelectionToggled;
                _rows.Add(row);
            }

            _anchor = null;
            Rebuild();
        }
        finally
        {
            _suppress = false;
        }
    }

    private void RowSelectionToggled(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppress || e.PropertyName != nameof(StudyDisplaySourceRow.Selected)) return;
        if (sender is not StudyDisplaySourceRow row) return;
        if (row.Selected) _selected.Add(row.Option.Id);
        else _selected.Remove(row.Option.Id);
        _anchor = row;
        UpdateSummary();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Rebuild()
    {
        var filter = SourceSearchBox.Text?.Trim() ?? string.Empty;
        _visible.Clear();
        foreach (var row in _rows)
        {
            if (filter.Length == 0 || Matches(row, filter)) _visible.Add(row);
        }

        var cursor = StudySourceListBox.SelectedItem as StudyDisplaySourceRow;
        if (cursor is null || !_visible.Contains(cursor)) cursor = _visible.FirstOrDefault();

        StudySourceListBox.ItemsSource = _visible.ToArray();
        StudySourceListBox.SelectedItem = cursor;
        SourceEmpty.IsVisible = _visible.Count == 0;
        UpdateSummary();
    }

    private static bool Matches(StudyDisplaySourceRow row, string filter) =>
        row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || row.Option.Code.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || row.Option.Grade.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private void UpdateSummary()
    {
        SourceCount.Text = $"已选 {_selected.Count} 个";
        SourceSummary.Text = _visible.Count == _rows.Count
            ? "空格切换选中，Shift+空格或 Shift+单击连续选择。"
            : $"筛选后显示 {_visible.Count} / {_rows.Count} 个班。空格切换选中，Shift+空格或 Shift+单击连续选择。";
    }

    private void SearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        Rebuild();
    }

    private void SelectAllClicked(object? sender, RoutedEventArgs e) => SetVisible(true);

    private void ClearClicked(object? sender, RoutedEventArgs e) => SetVisible(false);

    private void SetVisible(bool selected)
    {
        if (_visible.Count == 0) return;
        _suppress = true;
        try
        {
            foreach (var row in _visible)
            {
                row.Selected = selected;
                if (selected) _selected.Add(row.Option.Id);
                else _selected.Remove(row.Option.Id);
            }
        }
        finally
        {
            _suppress = false;
        }
        UpdateSummary();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RowsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        if (StudySourceListBox.SelectedItem is not StudyDisplaySourceRow row) return;
        Toggle(row, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        e.Handled = true;
    }

    private void PointerPressedOnRow(object? sender, PointerPressedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        var row = FindRow(e.Source as Control);
        if (row is null) return;
        StudySourceListBox.SelectedItem = row;
        Toggle(row, range: true);
        e.Handled = true;
    }

    private StudyDisplaySourceRow? FindRow(Control? control)
    {
        for (var current = control; current is not null; current = current.Parent as Control)
        {
            if (current is ListBoxItem item && item.DataContext is StudyDisplaySourceRow row) return row;
            if (ReferenceEquals(current, StudySourceListBox)) break;
        }
        return null;
    }

    private void Toggle(StudyDisplaySourceRow row, bool range)
    {
        if (!range) { row.Selected = !row.Selected; return; }
        int end = _visible.IndexOf(row);
        int start = _anchor is null ? -1 : _visible.IndexOf(_anchor);
        if (start < 0) _anchor = row;
        SelectRange(start < 0 ? end : start, end);
    }

    private void SelectRange(int from, int to)
    {
        if (from < 0 || to < 0 || _visible.Count == 0) return;
        if (from > to) (from, to) = (to, from);
        _suppress = true;
        try
        {
            for (var index = from; index <= to && index < _visible.Count; index++)
            {
                var row = _visible[index];
                row.Selected = true;
                _selected.Add(row.Option.Id);
            }
        }
        finally
        {
            _suppress = false;
        }
        UpdateSummary();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}
