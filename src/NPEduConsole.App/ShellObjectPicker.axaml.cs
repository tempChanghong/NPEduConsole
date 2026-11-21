using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace NPEduConsole.App;

/// <summary>
/// 全校检索的候选项。ClassId 必填；ScreenId 为空表示这是班级，非空表示这是该班的一块大屏。
/// Detail 用于同名对象之间的辨认（例如「G1-C07 · 高二（7）班」），SearchText 可补充额外的
/// 可检索文字（学号、旧名称、备注），不参与展示。
/// </summary>
public sealed record ShellSearchItem(string ClassId, string? ScreenId, string Title, string Detail, string? SearchText = null)
{
    public bool IsScreen => !string.IsNullOrEmpty(ScreenId);

    public override string ToString() => string.IsNullOrEmpty(Detail) ? Title : $"{Title}，{Detail}";
}

/// <summary>
/// 顶栏的紧凑检索入口：一个像输入框的按钮，点开或按 Ctrl+K / F4 后展开弹层，按班名、班级代码、
/// 大屏名或所属班级过滤当前学校的班级与大屏。选择后只回传目标 ID，跳转由外壳负责。
/// 本控件不发请求、不持久化任何输入；目录由外壳通过 <see cref="SetCatalog"/> 提供。
/// </summary>
public sealed partial class ShellObjectPicker : UserControl
{
    /// <summary>结果区最多渲染多少行；超过时提示继续输入，避免 200 项以上目录一次性铺开。</summary>
    private const int MaxRendered = 50;

    private IReadOnlyList<ShellSearchItem> _items = Array.Empty<ShellSearchItem>();
    private List<ShellSearchItem> _rendered = [];
    private string _scopeMessage = "";
    private int _matchCount;
    private TopLevel? _topLevel;

    public ShellObjectPicker()
    {
        InitializeComponent();
        AutomationProperties.SetName(this, "全校对象查找");
        PickerPopup.PlacementTarget = TriggerButton;
        // F4 只在本控件内生效，避免抢走其它下拉框的 F4。
        AddHandler(KeyDownEvent, OnPickerKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>选择后回传真实 ClassId / ScreenId，由外壳负责跳转。</summary>
    public event Action<ShellSearchItem>? TargetChosen;

    /// <summary>弹层是否已展开。</summary>
    public bool IsOpen => PickerPopup.IsOpen;

    /// <summary>当前查询文字。设置后会立即重新过滤，但不会自动打开弹层。</summary>
    public string SearchText
    {
        get => SearchBox.Text ?? "";
        set => SearchBox.Text = value ?? "";
    }

    /// <summary>当前实际渲染的结果（最多 <see cref="MaxRendered"/> 项）。</summary>
    public IReadOnlyList<ShellSearchItem> Results => _rendered;

    /// <summary>当前高亮项。</summary>
    public ShellSearchItem? Selection => ResultsList.SelectedItem as ShellSearchItem;

    /// <summary>目录范围说明；为空表示目录被视为完整。</summary>
    public string ScopeMessage => _scopeMessage;

    /// <summary>最近一次展开时给弹层设定的宽度，便于外壳与回归断言窄窗约束。</summary>
    public double PopupWidth => PopupSurface.Width;

    /// <summary>
    /// 替换目录。切校时必须立刻调用空列表，这样旧学校的对象不会留在弹层里，
    /// 旧查询文字也会被清掉；本方法不会自行展开弹层。
    /// 必须在 UI 线程调用。
    /// </summary>
    public void SetCatalog(IReadOnlyList<ShellSearchItem> items, string scopeMessage)
    {
        _items = items?.ToArray() ?? Array.Empty<ShellSearchItem>();
        _scopeMessage = string.IsNullOrWhiteSpace(scopeMessage) ? "" : scopeMessage.Trim();

        bool focusInside = IsKeyboardFocusWithin;
        if (PickerPopup.IsOpen)
        {
            // 切校后旧弹层内容已经失效，不能让它以旧结果继续停留。
            PickerPopup.IsOpen = false;
        }

        SearchBox.Text = "";
        ResultsList.SelectedIndex = -1;
        Refresh(selectFirst: false);

        if (focusInside) TriggerButton.Focus();
    }

    /// <summary>展开弹层并把焦点放到输入框。外壳的 Ctrl+K 可直接调用；重复调用只会重新聚焦。</summary>
    public void Open()
    {
        if (!IsEffectivelyVisible || !IsEffectivelyEnabled) return;

        UpdatePopupSize();
        if (PickerPopup.IsOpen)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            return;
        }

        PickerPopup.IsOpen = true;
    }

    /// <summary>收起弹层。焦点在弹层内时回到触发器。</summary>
    public void Close() => Close(restoreFocus: true);

    private void Close(bool restoreFocus)
    {
        bool wasOpen = PickerPopup.IsOpen;
        if (wasOpen) PickerPopup.IsOpen = false;
        // 只有弹层开着或焦点本来就在控件里时才把焦点拉回触发器，避免切校等场景抢走外部焦点。
        if (restoreFocus && (wasOpen || IsKeyboardFocusWithin)) TriggerButton.Focus();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // 控件可能被移出再挂回同一窗口，先摘掉旧处理器，避免 Ctrl+K 触发两次。
        _topLevel?.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);
        _topLevel = TopLevel.GetTopLevel(this);
        // Ctrl+K 是全校级入口：焦点在班级列表或表格里时也要能打开。
        _topLevel?.AddHandler(KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _topLevel?.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);
        _topLevel = null;
        if (PickerPopup.IsOpen) PickerPopup.IsOpen = false;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.K || (e.KeyModifiers & KeyModifiers.Control) == 0) return;
        if (!IsEffectivelyVisible || !IsEffectivelyEnabled) return;
        e.Handled = true;
        Open();
    }

    private void OnPickerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F4) return;
        e.Handled = true;
        Open();
    }

    private void TriggerClicked(object? sender, RoutedEventArgs e) => Open();

    private void PickerOpened(object? sender, EventArgs e)
    {
        TriggerButton.Classes.Set("open", true);
        UpdatePopupSize();
        Refresh();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void PickerClosed(object? sender, EventArgs e) => TriggerButton.Classes.Set("open", false);

    private void SearchChanged(object? sender, TextChangedEventArgs e) => Refresh();

    private void SearchKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                Move(1);
                e.Handled = true;
                break;
            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                e.Handled = true;
                Choose(ResultsList.SelectedItem as ShellSearchItem ?? _rendered.FirstOrDefault());
                break;
            case Key.Escape:
                e.Handled = true;
                Close(restoreFocus: true);
                break;
        }
    }

    private void ResultsKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                Choose(ResultsList.SelectedItem as ShellSearchItem);
                break;
            case Key.Escape:
                e.Handled = true;
                Close(restoreFocus: true);
                break;
        }
    }

    private void ResultsReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (e.Source is not Visual source) return;
        if (source.FindAncestorOfType<ListBoxItem>() is { DataContext: ShellSearchItem item }) Choose(item);
    }

    private void Move(int delta)
    {
        if (_rendered.Count == 0) return;
        int index = ResultsList.SelectedIndex + delta;
        if (index < 0) index = 0;
        if (index >= _rendered.Count) index = _rendered.Count - 1;
        ResultsList.SelectedIndex = index;
        ResultsList.ScrollIntoView(_rendered[index]);
    }

    private void Choose(ShellSearchItem? item)
    {
        if (item is null) return;
        // 先收起弹层并显式交回焦点，再通知外壳：外壳可能立刻切页，焦点不该留在已关闭的弹层里。
        Close(restoreFocus: true);
        TargetChosen?.Invoke(item);
    }

    private void UpdatePopupSize()
    {
        var top = TopLevel.GetTopLevel(this);
        double availableWidth = (top?.ClientSize.Width ?? 900) - 32;
        double max = Math.Min(560, Math.Max(240, availableWidth));
        double min = Math.Min(360, max);
        PopupSurface.Width = Math.Clamp(Math.Max(TriggerButton.Bounds.Width, min), min, max);
        if (top is not null) PopupSurface.MaxHeight = Math.Min(420, Math.Max(180, top.ClientSize.Height - 96));
    }

    private void Refresh(bool selectFirst = true)
    {
        string query = (SearchBox.Text ?? "").Trim();
        string[] tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var matched = new List<(ShellSearchItem Item, int Rank, int Order)>(_items.Count);
        int order = 0;
        foreach (var item in _items)
        {
            order++;
            if (!Matches(item, tokens)) continue;
            matched.Add((item, Rank(item, tokens), order));
        }

        _matchCount = matched.Count;
        _rendered = matched
            .OrderBy(entry => entry.Rank)
            .ThenBy(entry => entry.Order)
            .Take(MaxRendered)
            .Select(entry => entry.Item)
            .ToList();

        ResultsList.ItemsSource = _rendered;
        ResultsList.IsVisible = _rendered.Count > 0;
        ResultsList.SelectedIndex = selectFirst && _rendered.Count > 0 ? 0 : -1;

        ScopeText.Text = _scopeMessage;
        ScopeText.IsVisible = _scopeMessage.Length > 0;

        string empty = _items.Count == 0 ? "暂无可查找的对象。" : "没有匹配的班级或大屏。";
        EmptyText.Text = empty;
        EmptyText.IsVisible = _rendered.Count == 0;

        FooterHint.Text = _matchCount > MaxRendered
            ? $"仅显示前 {MaxRendered} 项（共 {_matchCount} 项匹配），继续输入可缩小范围。"
            : _matchCount > 0
                ? $"共 {_matchCount} 项 · ↑↓ 选择 · Enter 打开 · Esc 关闭"
                : "↑↓ 选择 · Enter 打开 · Esc 关闭";
    }

    private static bool Matches(ShellSearchItem item, string[] tokens)
    {
        foreach (string token in tokens)
        {
            if (!Contains(item.Title, token) && !Contains(item.Detail, token) && !Contains(item.SearchText, token))
                return false;
        }

        return true;
    }

    private static bool Contains(string? text, string token) =>
        !string.IsNullOrEmpty(text) && text.Contains(token, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>名称以首个词开头优先，其次是名称命中某个词，最后是只在详情里命中；同级保持目录原顺序。</summary>
    private static int Rank(ShellSearchItem item, string[] tokens)
    {
        if (tokens.Length == 0) return 0;
        if (item.Title.StartsWith(tokens[0], StringComparison.CurrentCultureIgnoreCase)) return 0;
        foreach (string token in tokens)
        {
            if (Contains(item.Title, token)) return 1;
        }

        return 2;
    }
}
