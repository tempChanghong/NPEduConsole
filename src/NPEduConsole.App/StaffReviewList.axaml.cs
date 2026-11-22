using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace NPEduConsole.App;

/// <summary>
/// 一行师资问题：Key 是稳定身份（缺岗也必须有 key），Title 是「谁 + 什么岗位」，
/// Detail 是「年级/班级 + 原因」，Status 是给管理员看的短状态词。
/// 结果由宿主按服务器返回组装，本控件只负责显示、选择与键盘。
/// </summary>
public sealed record StaffReviewLine(string Key, string Title, string Detail, string Status)
{
    public string AutomationName => $"{Title}；{Detail}；{Status}";

    public string ToolTipText => $"{Title}\n{Detail}";
}

/// <summary>
/// 窄窗师资问题列表（纯展示选择控件）。
///
/// 约定：
///   - 行只按 Key 定位，不当成索引；重新读取后宿主用 preferredKey 恢复选中。
///   - 840 宽的窗口里标题与原因都换行，不截断老师、班级和原因。
///   - Enter / 双击交给宿主打开对应页面；本控件不联网、不导航。
///   - 焦点由宿主决定何时要：FocusSelected() 给「刚打开这一页」，
///     SelectIndex() 给「删掉当前行后落到相邻行」。
/// </summary>
public sealed partial class StaffReviewList : UserControl
{
    private StaffReviewLine[] _rows = Array.Empty<StaffReviewLine>();
    private string? _lastKey;
    private bool _suppress;

    public StaffReviewList()
    {
        InitializeComponent();
    }

    /// <summary>选中行变化（含宿主调用 SetRows/SelectIndex/FocusSelected 引起的）。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>用户按 Enter 或双击当前行，请求宿主打开详情。</summary>
    public event EventHandler? OpenRequested;

    public string? SelectedKey => (StaffReviewListBox.SelectedItem as StaffReviewLine)?.Key;

    public int Count => _rows.Length;

    /// <summary>
    /// 换一批行。preferredKey 为空时沿用上一次的选中 Key（例如保存后重读）。
    /// </summary>
    public void SetRows(IReadOnlyList<StaffReviewLine> rows, string? preferredKey = null)
    {
        // ListBox 换 ItemsSource 会先清空选中并触发 SelectionChanged；keep 必须先取。
        var keep = string.IsNullOrEmpty(preferredKey) ? _lastKey : preferredKey;
        var previousKey = SelectedKey;
        var hadFocus = StaffReviewListBox.IsKeyboardFocusWithin;

        _rows = rows is null ? Array.Empty<StaffReviewLine>() : rows.Where(row => row is not null).ToArray();

        _suppress = true;
        try
        {
            StaffReviewListBox.ItemsSource = _rows;
            StaffReviewEmpty.IsVisible = _rows.Length == 0;
            var target = Find(keep) ?? _rows.FirstOrDefault();
            StaffReviewListBox.SelectedItem = target;
        }
        finally
        {
            _suppress = false;
        }

        _lastKey = SelectedKey;
        if (!string.Equals(previousKey, SelectedKey, StringComparison.Ordinal))
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        if (hadFocus && _rows.Length > 0)
        {
            Dispatcher.UIThread.Post(() => FocusRow(StaffReviewListBox.SelectedItem), DispatcherPriority.Loaded);
        }
    }

    /// <summary>宿主按索引给出「相邻回位」：越界会夹到最近一行。</summary>
    public bool SelectIndex(int index)
    {
        if (_rows.Length == 0) return false;
        var target = _rows[Math.Clamp(index, 0, _rows.Length - 1)];
        SelectRow(target);
        Dispatcher.UIThread.Post(() => FocusRow(target), DispatcherPriority.Loaded);
        return true;
    }

    /// <summary>把焦点交给当前选中行；还没有选中时先选第一行。</summary>
    public void FocusSelected()
    {
        if (StaffReviewListBox.SelectedItem is null && _rows.Length > 0)
        {
            SelectRow(_rows[0]);
        }

        var item = StaffReviewListBox.SelectedItem;
        Dispatcher.UIThread.Post(() => FocusRow(item), DispatcherPriority.Loaded);
    }

    private StaffReviewLine? Find(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        return _rows.FirstOrDefault(row => string.Equals(row.Key, key, StringComparison.Ordinal));
    }

    private void SelectRow(StaffReviewLine target)
    {
        var previousKey = SelectedKey;
        _suppress = true;
        try
        {
            StaffReviewListBox.SelectedItem = target;
        }
        finally
        {
            _suppress = false;
        }

        _lastKey = SelectedKey;
        if (!string.Equals(previousKey, SelectedKey, StringComparison.Ordinal))
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>容器还没生成（虚拟化）时退回到列表本身，保证键盘不会掉出这一页。</summary>
    private void FocusRow(object? item)
    {
        var container = item is null ? null : StaffReviewListBox.ContainerFromItem(item) as Control;
        if (container is null)
        {
            StaffReviewListBox.Focus();
            return;
        }

        container.BringIntoView();
        container.Focus();
    }

    private void RowsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        _lastKey = SelectedKey;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RowsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (StaffReviewListBox.SelectedItem is null) return;
        e.Handled = true;
        OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RowsDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (StaffReviewListBox.SelectedItem is null) return;
        OpenRequested?.Invoke(this, EventArgs.Empty);
    }
}
