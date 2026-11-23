using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;

// 与 Program.cs 同处全局命名空间，驱动里直接写 RosterWorkspaceFocus.Assert...。

/// <summary>
/// 学生名单工作区的几何回归：固定表头/操作栏、可滚动名单与预览列表、按真实对象定位、取消预览后的返回焦点。
///
/// 依赖的稳定契约（产品侧提供）：
/// <list type="bullet">
/// <item>编辑区：表头 <c>RosterTableHeader</c> 与操作栏 <c>RosterWorkspaceActions</c> 固定在窗口内不动；
/// 滚动容器是 <c>RosterList</c>（ScrollViewer），行集合是它内部的 <c>RosterRows</c>（ItemsControl）。</item>
/// <item>预览区：表头 <c>RosterPreviewTableHeader</c> 固定；变更列表 <c>RosterChangesList</c> 是<b>虚拟化</b> ListBox，
/// 只能先 <c>ScrollIntoView</c> 让它实现出行容器，再断言几何。</item>
/// <item>取消预览后焦点回到 <c>PreviewRosterButton</c>（沿用产品原入口名），预览区收起。</item>
/// </list>
///
/// 判定一律以「控件自己的滚动视口 ∩ 窗口客户区」为准：只和整窗求交会把滚出视口的行误判为可见。
/// </summary>
internal static class RosterWorkspaceFocus
{
    /// <summary>行/编辑器必须完整落在滚动视口内的容差：滚动偏移可能是小数像素。</summary>
    private const double ViewportTolerance = 1.0;

    /// <summary>固定表头与操作栏必须完整落在客户区内的容差。</summary>
    private const double FixedBarTolerance = 0.5;

    private const uint MouseMove = 0x0200;
    private const uint MouseLeftDown = 0x0201;
    private const uint MouseLeftUp = 0x0202;
    private const uint MouseWheel = 0x020A;
    private const nuint LeftButtonHeld = 0x0001;

    /// <summary>
    /// 编辑区：<paramref name="expectedRows"/> 行名单必须让滚动容器真正溢出、首行在视口内；
    /// 滚到底之后表头与操作栏的矩形不得移动，且最后一行确实进入视口。
    /// 840×480 与 100 行长表都用同一套断言。
    /// </summary>
    internal static async Task AssertRosterWorkspaceAsync(Window window, int expectedRows)
    {
        Require(expectedRows >= 1, "期望行数至少为 1。");

        var header = FindNamed<Control>(window, "RosterTableHeader");
        var actions = FindNamed<Control>(window, "RosterWorkspaceActions");
        var scroll = FindNamed<ScrollViewer>(window, "RosterList");

        await UntilCondition(() => IsRendered(header) && IsRendered(actions) && IsRendered(scroll),
            "名单表头、操作栏与名单滚动区渲染出来");

        var rows = RowsControlOf(scroll);
        int items = rows.ItemCount;
        Require(items >= expectedRows, $"RosterRows 只有 {items} 项，少于期望的 {expectedRows} 项。");

        var client = ClientRect(window);
        var headerBefore = RectOf(window, header, "名单表头");
        var actionsBefore = RectOf(window, actions, "名单操作栏");

        AssertFixed(client, headerBefore, "名单表头");
        AssertFixed(client, actionsBefore, "名单操作栏");

        // 长表：必须溢出，否则后面的“滚出视口”断言没有意义。
        await UntilCondition(() => scroll.Extent.Height > scroll.Viewport.Height + 1, "名单滚动区出现滚动条");
        Require(scroll.Extent.Height > scroll.Viewport.Height + 1,
            $"名单没有溢出（extent {scroll.Extent.Height:0.#} / viewport {scroll.Viewport.Height:0.#}），长表断言无效。");

        var first = await ContainerAsync(rows, 0, "名单首行");
        first.BringIntoView();
        await UntilCondition(() => IsRendered(first), "名单首行渲染出来");
        await AssertInsideViewportAsync(window, scroll, first, "名单首行");

        await ScrollToEndAsync(scroll, "名单");
        var last = await ContainerAsync(rows, items - 1, "名单末行");
        await UntilCondition(() => IsRendered(last), "名单末行滚进视口");
        await AssertInsideViewportAsync(window, scroll, last, "名单末行");

        RequireRectUnchanged(headerBefore, RectOf(window, header, "名单表头"), "名单表头随名单滚动移动了");
        RequireRectUnchanged(actionsBefore, RectOf(window, actions, "名单操作栏"), "名单操作栏随名单滚动移动了");
    }

    internal static async Task AssertNativeScrollAsync(Window window)
    {
        var scroll = FindNamed<ScrollViewer>(window, "RosterList");
        var header = FindNamed<Control>(window, "RosterTableHeader");
        var actions = FindNamed<Control>(window, "RosterWorkspaceActions");
        var firstRow = await ContainerAsync(RowsControlOf(scroll), 0, "名单首行");
        var headerBefore = RectOf(window, header, "名单表头");
        var actionsBefore = RectOf(window, actions, "名单操作栏");
        var vertical = scroll.GetVisualDescendants().OfType<ScrollBar>()
            .Single(bar => bar.Orientation == Orientation.Vertical && ReferenceEquals(bar.TemplatedParent, scroll));
        var track = vertical.GetVisualDescendants().OfType<Track>().Single();
        var thumb = track.Thumb ?? throw new InvalidDataException("名单滚动条缺少 Thumb。");
        if (window.TryGetPlatformHandle() is not { HandleDescriptor: "HWND" } handle)
            throw new InvalidDataException("原生滚动测试需要 Windows 窗口。");
        nint hwnd = handle.Handle;
        await UntilCondition(() => IsRendered(thumb) && thumb.GetVisualDescendants().OfType<Border>()
            .Any(part => part.Name == "ScrollGrip" && IsRendered(part) && part.Bounds.Width < thumb.Bounds.Width),
            "名单滚动条使用带独立命中区域的共享样式");

        double startOffset = scroll.Offset.Y;
        double firstRowBefore = RectOf(window, firstRow, "名单首行").Y;
        bool dragStarted = false;
        void OnDragStarted(object? sender, RoutedEventArgs args) => dragStarted = true;
        thumb.AddHandler(Thumb.DragStartedEvent, OnDragStarted);
        try
        {
            // Queue Win32 input only for this window, then let Avalonia hit-test the real controls.
            var origin = window.PointToScreen(default);
            var grip = ClientPixelsOf(thumb);
            var midpoint = ClientPixelsOf(track);
            PostMouse(MouseMove, 0, grip);
            PostMouse(MouseLeftDown, LeftButtonHeld, grip);
            try
            {
                await UntilCondition(() => dragStarted, "指针按下命中实际 Thumb");
                PostMouse(MouseMove, LeftButtonHeld, midpoint);
            }
            finally
            {
                PostMouse(MouseLeftUp, 0, midpoint);
            }

            await UntilCondition(() => scroll.Offset.Y < startOffset - scroll.Viewport.Height &&
                RectOf(window, firstRow, "名单首行").Y > firstRowBefore + scroll.Viewport.Height, "拖动 Thumb 后名单实际向上滚动");
            AssertFixedBars();
            double draggedOffset = scroll.Offset.Y;
            double draggedRow = RectOf(window, firstRow, "名单首行").Y;

            var viewport = ViewportLimit(window, scroll, "名单");
            var wheelAt = window.PointToScreen(new Point(viewport.X + 12, viewport.Center.Y));
            const int wheelDelta = -3 * 120;
            PostMouse(MouseWheel, (nuint)unchecked((ushort)wheelDelta) << 16, wheelAt);
            await UntilCondition(() => scroll.Offset.Y > draggedOffset + 10 &&
                RectOf(window, firstRow, "名单首行").Y < draggedRow - 10, "原生滚轮使名单实际向下滚动");
            AssertFixedBars();
            Console.WriteLine(FormattableString.Invariant(
                $"PASS: Windows HWND-scoped WM_* Thumb drag and wheel at {window.ClientSize.Width:0}x{window.ClientSize.Height:0}, scale {window.RenderScaling:P0}; offsets {startOffset:0.0} -> {draggedOffset:0.0} -> {scroll.Offset.Y:0.0}; fixed header and bottom actions."));

            PixelPoint ClientPixelsOf(Control control)
            {
                var screen = control.PointToScreen(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2));
                return new PixelPoint(screen.X - origin.X, screen.Y - origin.Y);
            }
        }
        finally
        {
            thumb.RemoveHandler(Thumb.DragStartedEvent, OnDragStarted);
        }

        void PostMouse(uint message, nuint flags, PixelPoint position)
        {
            uint coordinates = unchecked((ushort)position.X) | ((uint)unchecked((ushort)position.Y) << 16);
            Require(PostMessage(hwnd, message, flags, (nint)coordinates), $"Windows 未接收鼠标消息 0x{message:X}。");
        }

        void AssertFixedBars()
        {
            RequireRectUnchanged(headerBefore, RectOf(window, header, "名单表头"), "原生滚动移动了表头");
            RequireRectUnchanged(actionsBefore, RectOf(window, actions, "名单操作栏"), "原生滚动移动了操作栏");
        }
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(nint hwnd, uint message, nuint flags, nint position);

    /// <summary>
    /// 预览区：表头固定；<paramref name="changeRows"/> 逐个滚进视口后完整落在视口内。
    /// 变更列表是虚拟化 ListBox，因此按 <c>Items</c> 里的真实模型对象选行（不是先等视觉树出现），
    /// 传 <c>null</c> 时只核对首行。
    /// </summary>
    internal static async Task AssertRosterPreviewAsync(Window window, IReadOnlyList<object>? changeRows = null)
    {
        var header = FindNamed<Control>(window, "RosterPreviewTableHeader");
        var changes = FindNamed<ItemsControl>(window, "RosterChangesList");

        await UntilCondition(() => IsRendered(header) && IsRendered(changes), "预览表头与变更列表渲染出来");
        Require(changes.ItemCount >= 1, "RosterChangesList 是空的，预览没内容可核对。");

        var scroll = ScrollOf(changes, "RosterChangesList");
        var client = ClientRect(window);
        var headerBefore = RectOf(window, header, "预览表头");
        AssertFixed(client, headerBefore, "预览表头");

        IReadOnlyList<object> targets = changeRows is { Count: > 0 } ? changeRows : new object[] { ItemAt(changes, 0) };
        foreach (object item in targets)
        {
            var container = await RevealAsync(changes, item, "变更行");
            var element = RowElement(container)
                ?? throw new InvalidDataException("变更行里没有任何 AutomationId，无法按对象稳定定位。");
            await AssertInsideViewportAsync(window, scroll, element, "变更行");
        }

        // 把变更列表滚到底，预览表头不能跟着动。
        if (scroll.Extent.Height > scroll.Viewport.Height + 1)
        {
            await ScrollToEndAsync(scroll, "变更列表");
        }

        RequireRectUnchanged(headerBefore, RectOf(window, header, "预览表头"), "预览表头随变更列表滚动移动了");
        AssertFixed(client, RectOf(window, header, "预览表头"), "预览表头");
    }

    /// <summary>
    /// 取消预览之后：焦点回到 <c>PreviewRosterButton</c>，它可见可用、完整在客户区内，
    /// 并且预览区确实收起了（<c>RosterChangesList</c> 不可见）。
    /// </summary>
    internal static async Task AssertRosterPreviewCancelFocusAsync(Window window)
    {
        var button = FindNamed<Control>(window, "PreviewRosterButton");
        await UntilCondition(() => IsRendered(button) && button.IsFocused, "取消预览后焦点回到「预览变更」");

        Require(button.IsFocused, "取消预览后焦点没有回到 PreviewRosterButton。");
        Require(button.IsEffectivelyEnabled, "取消预览后 PreviewRosterButton 不可用。");
        AssertFixed(ClientRect(window), RectOf(window, button, "预览按钮"), "PreviewRosterButton");

        if (window.FindControl<Control>("RosterChangesList") is { } changes)
        {
            Require(!changes.IsEffectivelyVisible, "取消预览后变更列表仍然可见。");
        }
    }

    // ── 虚拟化列表的定位 ────────────────────────────────────────────────────────

    /// <summary>
    /// 先按模型对象在 <c>Items</c> 里找到索引，再 <c>ScrollIntoView</c>；等容器真正实现且渲染后返回。
    /// 这是虚拟化列表唯一可靠的顺序——不能先找视觉树，因为没实现的项根本不在树里。
    /// </summary>
    private static async Task<Control> RevealAsync(ItemsControl list, object item, string description)
    {
        int index = IndexOf(list, item);
        Require(index >= 0, $"{description} 不在 RosterChangesList.Items 里。");

        Control? container = null;
        await UntilCondition(() =>
        {
            if (ContainerAsync(list, item) is { } realized && IsRendered(realized))
            {
                container = realized;
                return true;
            }

            list.ScrollIntoView(index);
            return false;
        }, $"{description} 滚进视口");

        return container!;
    }

    private static Control? ContainerAsync(ItemsControl list, object item) =>
        list.ContainerFromItem(item) as Control;

    /// <summary>变更行里承载 <c>AutomationId</c> 的元素（模板根 Border，而不是 ListBoxItem 容器）。</summary>
    private static Control? RowElement(Control container) =>
        container.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(control => AutomationProperties.GetAutomationId(control) is { Length: > 0 });

    private static int IndexOf(ItemsControl list, object item)
    {
        for (int index = 0; index < list.ItemCount; index++)
        {
            if (ReferenceEquals(ItemAt(list, index), item)) return index;
        }

        return -1;
    }

    private static object ItemAt(ItemsControl list, int index) =>
        list.Items[index] ?? throw new InvalidDataException($"RosterChangesList 第 {index + 1} 项为空。");

    private static async Task<Control> ContainerAsync(ItemsControl rows, int index, string description)
    {
        Control? container = null;
        await UntilCondition(() => (container = rows.ContainerFromIndex(index) as Control) is { IsEffectivelyVisible: true },
            $"{description} 实现出来");
        return container!;
    }

    private static ItemsControl RowsControlOf(ScrollViewer scroll) =>
        scroll.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault()
        ?? throw new InvalidDataException("RosterList 里没有 ItemsControl（预期 RosterRows）。");

    // ── 基础工具 ────────────────────────────────────────────────────────────────

    private static void Require(bool success, string message)
    {
        if (!success) throw new InvalidDataException(message);
    }

    private static async Task UntilCondition(Func<bool> condition, string step)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }

        throw new InvalidDataException($"Timed out: {step}");
    }

    private static T FindNamed<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().SingleOrDefault(control => control.Name == name)
        ?? throw new InvalidDataException($"Window is missing {name}");

    private static bool IsRendered(Control control) =>
        control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0;

    private static ScrollViewer ScrollOf(Control list, string description)
    {
        if (list is ScrollViewer self) return self;
        return list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()
            ?? throw new InvalidDataException($"{description} has no ScrollViewer");
    }

    private static async Task ScrollToEndAsync(ScrollViewer scroll, string description)
    {
        double target = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        scroll.Offset = new Vector(scroll.Offset.X, target);
        await UntilCondition(() => Math.Abs(scroll.Offset.Y - target) < 1, $"{description} 滚动到底");
    }

    private static Rect ClientRect(Window window) => new(0, 0, window.ClientSize.Width, window.ClientSize.Height);

    private static Rect RectOf(Window window, Control control, string description)
    {
        var origin = control.TranslatePoint(default, window);
        Require(origin is not null, $"{description} 不在窗口的可视树里。");
        return new Rect(origin!.Value, control.Bounds.Size);
    }

    /// <summary>
    /// 控件必须完整落在「滚动视口 ∩ 窗口客户区」内。
    /// 滚动是异步布局：<c>Offset</c> 变了不代表这一帧的坐标已经跟着变，
    /// 所以这里每轮都重读 ScrollContentPresenter 与目标的实际矩形，直到真的落进去（必要时补一次 BringIntoView）。
    /// </summary>
    private static async Task AssertInsideViewportAsync(Window window, ScrollViewer scroll, Control control, string description)
    {
        Rect limit = default;
        Rect rect = default;
        bool nudged = false;
        DateTime deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            limit = ViewportLimit(window, scroll, description);
            rect = RectOf(window, control, description);
            if (Contains(limit, rect, ViewportTolerance)) return;

            if (!nudged)
            {
                nudged = true;
                control.BringIntoView();
            }

            await Task.Delay(25);
        }

        throw new InvalidDataException(
            $"{description} 没有落进滚动视口：{Describe(rect)} / 视口 {Describe(limit)}。");
    }

    /// <summary>真正的可视区：ScrollContentPresenter（无它时退回 ScrollViewer 自身）∩ 窗口客户区。</summary>
    private static Rect ViewportLimit(Window window, ScrollViewer scroll, string description)
    {
        var presenter = scroll.GetVisualDescendants().OfType<ScrollContentPresenter>().FirstOrDefault();
        Control viewport = presenter is null ? scroll : presenter;
        var port = RectOf(window, viewport, $"{description} 的滚动视口");
        var client = ClientRect(window);
        Require(client.Intersects(port), $"{description} 的滚动视口不在窗口内：{Describe(port)} / {Describe(client)}。");
        return Overlap(port, client);
    }

    private static void AssertFixed(Rect client, Rect rect, string description) =>
        Require(Contains(client, rect, FixedBarTolerance),
            $"{description} 没有完整留在窗口内：{Describe(rect)} / {Describe(client)}。");

    private static void RequireRectUnchanged(Rect before, Rect after, string message) =>
        Require(Math.Abs(before.X - after.X) <= FixedBarTolerance
            && Math.Abs(before.Y - after.Y) <= FixedBarTolerance
            && Math.Abs(before.Width - after.Width) <= FixedBarTolerance
            && Math.Abs(before.Height - after.Height) <= FixedBarTolerance,
            $"{message}：滚动前 {Describe(before)}，滚动后 {Describe(after)}。");

    private static Rect Overlap(Rect left, Rect right)
    {
        double x = Math.Max(left.X, right.X);
        double y = Math.Max(left.Y, right.Y);
        double width = Math.Min(left.Right, right.Right) - x;
        double height = Math.Min(left.Bottom, right.Bottom) - y;
        return width <= 0 || height <= 0 ? default : new Rect(x, y, width, height);
    }

    private static bool Contains(Rect outer, Rect inner, double tolerance) =>
        inner.X >= outer.X - tolerance
        && inner.Y >= outer.Y - tolerance
        && inner.Right <= outer.Right + tolerance
        && inner.Bottom <= outer.Bottom + tolerance;

    private static string Describe(Rect rect) => $"[{rect.X:0.#},{rect.Y:0.#} {rect.Width:0.#}×{rect.Height:0.#}]";
}
