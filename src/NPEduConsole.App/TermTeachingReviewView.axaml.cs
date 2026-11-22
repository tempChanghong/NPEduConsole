using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;

namespace NPEduConsole.App;

/// <summary>一行「这个对象从什么变成什么」。<paramref name="Issue"/> 非空表示这一行需要人先处理。</summary>
public sealed record TermTeachingReviewLine(string Key, string Title, string Before, string After, string? Issue)
{
    /// <summary>宽/窄两种排版都要能一眼认出冲突行。</summary>
    public bool HasIssue => !string.IsNullOrWhiteSpace(Issue);
}

/// <summary>
/// 指定学期教学安排的逐行前后对照。只做显示与键盘定位：
/// 数据、提交、范围版本都由宿主窗口负责，这里不读网络、不写文件。
/// </summary>
public sealed partial class TermTeachingReviewView : UserControl
{
    /// <summary>
    /// 预览页占满整个窗口，840 窗的可用宽度约 814 DIP：四列表格在这个宽度里
    /// 「变更前/变更后」会被压到一两个汉字宽，所以 900 以下改纵向卡片；
    /// 1160 这类宽窗仍是横表。同一份行数据、同一个 ListBox，只换模板，
    /// 选中与滚动位置都不丢。
    /// </summary>
    private const double CompactWidth = 900;

    private IReadOnlyList<TermTeachingReviewLine> _rows = Array.Empty<TermTeachingReviewLine>();
    private bool? _compact;
    /// <summary>上一次选中的行 Key，用来在宿主重新读取同一批行时保持选中不跳。</summary>
    private string? _lastKey;

    public TermTeachingReviewView()
    {
        InitializeComponent();
        ApplyLayout(false);
        TermTeachingReviewList.SelectionChanged += (_, _) =>
            _lastKey = (TermTeachingReviewList.SelectedItem as TermTeachingReviewLine)?.Key;
        PropertyChanged += (_, change) =>
        {
            if (change.Property == BoundsProperty)
                ApplyLayout(Bounds.Width > 0 && Bounds.Width < CompactWidth);
        };
    }

    /// <summary>当前选中行的 Key；没有选中时为 null。</summary>
    public string? SelectedKey => (TermTeachingReviewList.SelectedItem as TermTeachingReviewLine)?.Key;

    /// <summary>
    /// 换一批对照行。给了 <paramref name="selectedKey"/> 就选中它；没给时先按
    /// 上一次选中的 Key 对齐（宿主重新读取同一批行不该跳行），都没有才落到
    /// 第一条有冲突的行——打开页面就能看见要先处理的事。
    /// </summary>
    public void SetRows(IReadOnlyList<TermTeachingReviewLine> rows, string? selectedKey = null)
    {
        var hadFocus = TermTeachingReviewList.IsKeyboardFocusWithin;
        // 换 ItemsSource 会让 ListBox 先清空选中（SelectionChanged 写回 null），
        // 所以「上一行的 Key」必须在赋值前取。
        var keep = string.IsNullOrEmpty(selectedKey) ? _lastKey : selectedKey;
        _rows = rows ?? Array.Empty<TermTeachingReviewLine>();
        TermTeachingReviewList.ItemsSource = _rows;
        TermTeachingReviewList.IsVisible = _rows.Count > 0;
        TermTeachingReviewEmpty.IsVisible = _rows.Count == 0;
        if (_rows.Count == 0)
        {
            TermTeachingReviewList.SelectedItem = null;
            _lastKey = null;
            return;
        }
        var target = Find(keep) ?? _rows.FirstOrDefault(row => row.HasIssue) ?? _rows[0];
        TermTeachingReviewList.SelectedItem = target;
        _lastKey = target.Key;
        if (hadFocus) RestoreFocusToSelection();
    }

    /// <summary>
    /// 按 Key 定位一条冲突并让键盘抵达它：选中、滚进视口、把焦点交给该行。
    /// 不是冲突行（或 Key 不在当前列表）返回 false，由宿主决定提示什么。
    /// </summary>
    public bool FocusIssue(string key)
    {
        var target = Find(key);
        if (target is null || !target.HasIssue) return false;
        TermTeachingReviewList.SelectedItem = target;
        TermTeachingReviewList.ScrollIntoView(target);
        Dispatcher.UIThread.Post(() =>
        {
            if (TermTeachingReviewList.ContainerFromItem(target) is Control container)
            {
                container.BringIntoView();
                container.Focus();
            }
            else
            {
                TermTeachingReviewList.Focus();
            }
        }, DispatcherPriority.Loaded);
        return true;
    }

    private TermTeachingReviewLine? Find(string? key) => string.IsNullOrEmpty(key)
        ? null
        : _rows.FirstOrDefault(row => string.Equals(row.Key, key, StringComparison.Ordinal));

    private void ApplyLayout(bool compact)
    {
        if (_compact == compact) return;
        var hadFocus = TermTeachingReviewList.IsKeyboardFocusWithin;
        _compact = compact;
        TermTeachingReviewHeader.IsVisible = !compact;
        var key = compact ? "CompactRow" : "WideRow";
        if (this.TryFindResource(key, out var resource) && resource is IDataTemplate template)
            TermTeachingReviewList.ItemTemplate = template;
        // 换模板会重建行容器；键盘还在列表里时把焦点交回同一 Key 的新容器，
        // 不能让缩放把用户正在看的那一行「弄丢」。
        if (hadFocus) RestoreFocusToSelection();
    }

    /// <summary>把键盘焦点交回当前选中的行；行容器还没生成就退回到列表本身。</summary>
    private void RestoreFocusToSelection()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (TermTeachingReviewList.SelectedItem is TermTeachingReviewLine selected
                && TermTeachingReviewList.ContainerFromItem(selected) is Control container)
                container.Focus();
            else
                TermTeachingReviewList.Focus();
        }, DispatcherPriority.Loaded);
    }
}
