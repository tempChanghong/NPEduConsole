using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;

namespace NPEduConsole.App;

/// <summary>一行「这次状态变更会影响哪台大屏」。由宿主从 status-preview / 回执里映射。</summary>
public sealed record TermStatusScreenLine(string BindingId, string Name, string Original, string Destination, string Outcome);

/// <summary>
/// 学期状态变更影响的大屏清单。数据与筛选归宿主；这里只负责显示、宽窄自适应和选中项。
/// 宿主用 <see cref="SelectedBindingId"/> / <see cref="SelectionChanged"/> 去打开准确大屏或未匹配安排。
/// </summary>
public sealed partial class TermStatusScreensView : UserControl
{
    // 四列表头是 2*,1.6*,1.6*,1.6*、列间距 8 + 行内边距 10：内容宽 < 820 时「结果」列只剩约 170 DIP，
    // 中文班名与结果文本会挤成两行。840 宽的宿主窗口内容区约 770 DIP，按业务列取 820 作为分界，
    // 让 840 及更窄一律走逐台卡片，1160 一类宽窗继续用四列。
    private const double CompactWidth = 820;

    private IReadOnlyList<TermStatusScreenLine> _rows = Array.Empty<TermStatusScreenLine>();
    private IDataTemplate? _wideTemplate;
    private IDataTemplate? _compactTemplate;
    private bool _compact;
    private bool _arranged;
    private bool _syncingSelection;
    private double _lastWidth = double.NaN;

    public TermStatusScreensView()
    {
        InitializeComponent();
        AutomationProperties.SetName(this, "这次状态变更影响的大屏");
        AutomationProperties.SetName(TermStatusScreensList, "这次状态变更影响的大屏");
        _wideTemplate = new FuncDataTemplate<TermStatusScreenLine>((row, _) => Line(row), true);
        _compactTemplate = new FuncDataTemplate<TermStatusScreenLine>((row, _) => Card(row), true);
        TermStatusScreensList.ItemTemplate = _wideTemplate;
        SizeChanged += (_, _) => Arrange();
    }

    /// <summary>当前选中行的绑定编号；未选中时为 null。</summary>
    public string? SelectedBindingId
    {
        get => (TermStatusScreensList.SelectedItem as TermStatusScreenLine)?.BindingId;
        set => SelectBinding(value);
    }

    /// <summary>选中项变化（含被程序清空）。宿主据此打开大屏或未匹配安排。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>替换整张清单；调用方给的顺序就是显示顺序。仍存在的选中项会保留。</summary>
    public void SetRows(IReadOnlyList<TermStatusScreenLine> rows)
    {
        var keep = SelectedBindingId;
        _rows = rows ?? Array.Empty<TermStatusScreenLine>();
        _syncingSelection = true;
        try
        {
            TermStatusScreensList.ItemsSource = _rows;
            TermStatusScreensList.SelectedItem = FindRow(keep);
        }
        finally
        {
            _syncingSelection = false;
        }
        Rebuild();
    }

    /// <summary>按绑定编号选中；找不到或传 null 时清空选中。</summary>
    public void SelectBinding(string? bindingId)
    {
        var row = FindRow(bindingId);
        if (ReferenceEquals(row, TermStatusScreensList.SelectedItem))
        {
            return;
        }
        _syncingSelection = true;
        try
        {
            TermStatusScreensList.SelectedItem = row;
        }
        finally
        {
            _syncingSelection = false;
        }
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把焦点放到当前选中行（没有选中就放到第一行）。</summary>
    public bool FocusSelected()
    {
        if (_rows.Count == 0)
        {
            return false;
        }
        var row = FindRow(SelectedBindingId) ?? _rows[0];
        if (!ReferenceEquals(row, TermStatusScreensList.SelectedItem))
        {
            SelectBinding(row.BindingId);
        }
        if (TermStatusScreensList.ContainerFromItem(row) is ListBoxItem item)
        {
            item.BringIntoView();
            return item.Focus(NavigationMethod.Tab);
        }
        return TermStatusScreensList.Focus(NavigationMethod.Tab);
    }

    /// <summary>窄窗/宽窗切换；宿主改过宽度后也可以直接调。</summary>
    public void Arrange(bool force = false)
    {
        var width = Bounds.Width;
        if (double.IsNaN(width) || width <= 0)
        {
            return;
        }
        if (!force && _arranged && Math.Abs(width - _lastWidth) < 0.5)
        {
            return;
        }
        _arranged = true;
        _lastWidth = width;
        var compact = width < CompactWidth;
        if (compact == _compact && !force)
        {
            return;
        }
        _compact = compact;
        Rebuild();
    }

    private TermStatusScreenLine? FindRow(string? bindingId)
    {
        if (string.IsNullOrEmpty(bindingId))
        {
            return null;
        }
        foreach (var row in _rows)
        {
            if (row.BindingId == bindingId)
            {
                return row;
            }
        }
        return null;
    }

    private void Rebuild()
    {
        var hasRows = _rows.Count > 0;
        TermStatusScreensEmpty.IsVisible = !hasRows;
        TermStatusScreensList.IsVisible = hasRows;
        TermStatusScreensHeader.IsVisible = hasRows && !_compact;
        TermStatusScreensList.ItemTemplate = _compact ? _compactTemplate : _wideTemplate;
    }

    private void RowsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_syncingSelection)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private Control Line(TermStatusScreenLine row)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("2*,1.6*,1.6*,1.6*"),
            ColumnSpacing = 8,
        };
        Place(grid, 0, NameCell(row));
        Place(grid, 1, Text(Safe(row.Original), "screen-text"));
        Place(grid, 2, Text(Safe(row.Destination), "screen-text"));
        Place(grid, 3, Text(Safe(row.Outcome), "screen-outcome"));
        return Shell(grid, row);
    }

    private Control Card(TermStatusScreenLine row)
    {
        var stack = new StackPanel {Spacing = 2};
        stack.Children.Add(Text(row.Name, "screen-name"));
        stack.Children.Add(Id(row.BindingId));
        stack.Children.Add(Text($"原归属：{Safe(row.Original)}", "screen-text"));
        stack.Children.Add(Text($"变更后：{Safe(row.Destination)}", "screen-text"));
        stack.Children.Add(Text(Safe(row.Outcome), "screen-outcome"));
        return Shell(stack, row);
    }

    private static Control NameCell(TermStatusScreenLine row)
    {
        var stack = new StackPanel {Spacing = 1};
        stack.Children.Add(Text(row.Name, "screen-name"));
        stack.Children.Add(Id(row.BindingId));
        return stack;
    }

    private static TextBlock Id(string bindingId) => Text($"绑定编号 {bindingId}", "screen-id");

    private static Control Shell(Control content, TermStatusScreenLine row)
    {
        var border = new Border {Child = content};
        border.Classes.Add("screen-row");
        AutomationProperties.SetName(border, Describe(row));
        return border;
    }

    private static void Place(Grid grid, int column, Control child)
    {
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }

    private static TextBlock Text(string value, string style)
    {
        var block = new TextBlock {Text = value};
        block.Classes.Add(style);
        return block;
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private static string Describe(TermStatusScreenLine row) =>
        $"{row.Name}，绑定编号 {row.BindingId}，原归属 {Safe(row.Original)}，变更后 {Safe(row.Destination)}，{Safe(row.Outcome)}";
}
