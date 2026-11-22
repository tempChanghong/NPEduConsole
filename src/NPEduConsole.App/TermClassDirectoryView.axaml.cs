using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace NPEduConsole.App;

/// <summary>指定学期的行政班选项；编号与名称都来自服务端目录。</summary>
public sealed record TermClassChoice(string Id, string Name, string Code, string? GradeName, bool IsActive);

/// <summary>列表行：显示文本一次算好，模板只负责摆位置。</summary>
public sealed record TermClassRow(TermClassChoice Choice, string Name, string Meta)
{
    public string AutomationName => $"{Name}，{Meta}";

    public override string ToString() => Name;
}

/// <summary>
/// 指定学期的行政班目录：宿主负责学期/年级筛选与详情页，这里只管筛选、选中与打开。
/// 对外一律按班 id 定位，不缓存行对象——学校重新读回目录后行对象会整体重建。
/// </summary>
public sealed partial class TermClassDirectoryView : UserControl
{
    private TermClassRow[] _rows = Array.Empty<TermClassRow>();
    private TermClassRow[] _visible = Array.Empty<TermClassRow>();
    private bool _syncing;

    public TermClassDirectoryView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => RefreshCounts();
    }

    /// <summary>当前选中的班；没有选中时为 null。</summary>
    public TermClassChoice? Selected => (TermClassList.SelectedItem as TermClassRow)?.Choice;

    /// <summary>选中项变化（含被程序清空）。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>用户要求打开当前班（回车、双击或按钮）。</summary>
    public event EventHandler? OpenRequested;

    /// <summary>
    /// 替换整张目录。<paramref name="selectedId"/> 是唯一依据：为 null 表示清空选中，找不到也清空。
    /// </summary>
    public void SetRows(IReadOnlyList<TermClassChoice> rows, string? selectedId)
    {
        var source = rows ?? Array.Empty<TermClassChoice>();
        _rows = source.Select(CreateRow).ToArray();
        Rebuild(selectedId);
    }

    /// <summary>把焦点放回选中行（没有选中就放到第一行）。</summary>
    public void RestoreFocus()
    {
        if (_visible.Length == 0)
        {
            TermClassFilter.Focus();
            return;
        }

        var target = (Selected?.Id is { } id ? Find(id) : null) ?? _visible[0];
        if (!ReferenceEquals(TermClassList.SelectedItem, target))
        {
            // 焦点落点会连带改变选中，宿主要跟着刷新详情，所以这里主动通知一次。
            Select(target);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        TermClassList.ScrollIntoView(target);
        Dispatcher.UIThread.Post(() =>
        {
            if (Selected?.Id != target.Choice.Id || !TermClassList.IsEffectivelyVisible) return;
            if (Find(target.Choice.Id) is { } current && TermClassList.ContainerFromItem(current) is ListBoxItem item)
            {
                item.BringIntoView();
                item.Focus(NavigationMethod.Tab);
            }
            else
            {
                TermClassList.Focus(NavigationMethod.Tab);
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>按 id 选中，不改筛选；供宿主从名单页返回时定位。</summary>
    public void Select(string? id)
    {
        if (id is not null && Find(id) is { } target)
        {
            var previous = Selected?.Id;
            Select(target);
            if (Selected?.Id != previous) SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private TermClassRow? Find(string id) => _visible.FirstOrDefault(item => item.Choice.Id == id);

    private void Select(TermClassRow target)
    {
        if (ReferenceEquals(TermClassList.SelectedItem, target))
        {
            return;
        }

        _syncing = true;
        try
        {
            TermClassList.SelectedItem = target;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void Rebuild(string? keepId)
    {
        var needle = TermClassFilter.Text?.Trim();
        _visible = string.IsNullOrEmpty(needle)
            ? _rows
            : _rows.Where(item =>
                item.Choice.Code.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || item.Choice.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToArray();

        _syncing = true;
        try
        {
            TermClassEmpty.IsVisible = _visible.Length == 0;
            TermClassList.ItemsSource = _visible;
            TermClassList.SelectedItem = keepId is null ? null : Find(keepId);
        }
        finally
        {
            _syncing = false;
        }

        RefreshCounts();
    }

    private static TermClassRow CreateRow(TermClassChoice choice)
    {
        var meta = string.IsNullOrWhiteSpace(choice.GradeName)
            ? choice.Code
            : $"{choice.GradeName} · {choice.Code}";
        if (!choice.IsActive)
        {
            meta += " · 已停用";
        }

        return new TermClassRow(choice, choice.Name, meta);
    }

    private void RefreshCounts()
    {
        TermClassCount.Text = _rows.Length == 0
            ? string.Empty
            : _visible.Length == _rows.Length ? $"共 {_rows.Length} 个班" : $"{_visible.Length} / {_rows.Length} 个班";
    }

    private void FilterTextChanged(object? sender, TextChangedEventArgs e)
    {
        var previous = Selected?.Id;
        Rebuild(previous);
        if (Selected?.Id != previous) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RowsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RowsDoubleTapped(object? sender, TappedEventArgs e) => RaiseOpen();

    private void RowsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            RaiseOpen();
        }
    }

    private void RaiseOpen()
    {
        if (Selected is not null)
        {
            OpenRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
