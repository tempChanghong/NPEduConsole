using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;
using NPEduConsole.App;

/// <summary>
/// 行政班任课的预览工作区回归：固定表头/操作栏、可滚动预览列表、按班级 ID 定位变化行。
///
/// 依赖的稳定契约（产品侧提供）：
/// <list type="bullet">
/// <item>预览表头 <c>TeachingPreviewHeader</c> 与操作栏 <c>TeachingWorkspaceActions</c> 固定在窗口内不动；</item>
/// <item>预览列表 <c>TeachingPreviewList</c> 是<b>虚拟化</b> ListBox，自己滚动；</item>
/// <item>每个班一行，模板根元素 <c>AutomationId=TeachingPreviewClass-{classId}</c>，且该元素可聚焦。</item>
/// </list>
///
/// 虚拟化列表的正确顺序是：先按 <c>Items</c> 里的真实模型对象选行 → <c>ScrollIntoView</c> → 等容器实现 →
/// 再断言几何。没实现的项根本不在视觉树里，所以不能先找树。
/// 判定以「列表自己的滚动视口 ∩ 窗口客户区」为准：只和整窗求交会把滚出视口的行误判成可见。
/// 与 Program.cs 同处全局命名空间，故是同名 partial 类型的一部分。
/// </summary>
internal static partial class Program
{
    private const string TeachingRowPrefix = "TeachingPreviewClass-";

    /// <summary>行必须完整落在滚动视口内的容差：滚动偏移可能是小数像素。</summary>
    private const double TeachingViewportTolerance = 1.0;

    /// <summary>固定表头与操作栏必须完整落在客户区内的容差。</summary>
    private const double TeachingFixedBarTolerance = 0.5;

    /// <summary>
    /// 预览工作区：表头与操作栏固定；<paramref name="rows"/> 的每个模型行都能按
    /// <c>TeachingPreviewClass-{classId}</c> 在列表里实现出来，并完整落在列表视口内。
    /// 列表必须真的溢出（长表）。840×480 与长表共用同一套断言。
    /// </summary>
    internal static async Task AssertTeachingPreviewAsync(TeachingView view, IReadOnlyList<object> rows)
    {
        if (rows.Count == 0) throw new InvalidDataException("需要至少一个预览行。");

        var window = (Window)TopLevel.GetTopLevel(view)!;
        var header = Find<Control>(view, "TeachingPreviewHeader");
        var actions = Find<Control>(view, "TeachingWorkspaceActions");
        var list = Find<ItemsControl>(view, "TeachingPreviewList");

        await Until(() => Rendered(header) && Rendered(actions) && Rendered(list),
            "预览表头、操作栏与预览列表渲染出来");
        Check(list.ItemCount >= rows.Count, $"TeachingPreviewList 只有 {list.ItemCount} 项，少于传入的 {rows.Count} 行。");

        var scroll = TeachingScrollOf(list);
        var client = TeachingClient(window);
        var headerBefore = TeachingRect(window, header, "预览表头");
        var actionsBefore = TeachingRect(window, actions, "任课操作栏");

        TeachingAssertFixed(client, headerBefore, "预览表头");
        TeachingAssertFixed(client, actionsBefore, "任课操作栏");

        await Until(() => scroll.Extent.Height > scroll.Viewport.Height + 1, "任课预览列表出现滚动条");
        if (scroll.Extent.Height <= scroll.Viewport.Height + 1)
            throw new InvalidDataException(
                $"任课预览列表没有溢出（extent {scroll.Extent.Height:0.#} / viewport {scroll.Viewport.Height:0.#}），长表断言无效。");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (object model in rows)
        {
            var row = await TeachingRevealAsync(list, model, "预览行");
            if (AutomationProperties.GetAutomationId(row) is not { Length: > 0 } automationId
                || !automationId.StartsWith(TeachingRowPrefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"预览行的 AutomationId 不是 {TeachingRowPrefix}{{classId}}：{AutomationProperties.GetAutomationId(row) ?? "<空>"}。");
            }

            if (!seen.Add(automationId))
                throw new InvalidDataException($"预览列表出现重复的 {automationId}，无法按班级区分行。");

            await TeachingAssertInsideViewportAsync(window, scroll, row, automationId);
        }

        // 滚到底：表头与操作栏不得移动，最后一项仍在视口内。
        await TeachingScrollToEndAsync(scroll, "任课预览");
        if (TeachingContainerOf(list, list.ItemCount - 1) is { } lastRow)
        {
            await Until(() => Rendered(lastRow), "任课预览末行滚进视口");
            await TeachingAssertInsideViewportAsync(window, scroll, lastRow, "任课预览末行");
        }

        TeachingRequireRectUnchanged(headerBefore, TeachingRect(window, header, "预览表头"), "预览表头随列表滚动移动了");
        TeachingRequireRectUnchanged(actionsBefore, TeachingRect(window, actions, "任课操作栏"), "任课操作栏随列表滚动移动了");
    }

    /// <summary>
    /// 单个变化班拿到键盘焦点，并且它所在的行完整落在预览列表视口内。
    /// 严格断言落在行元素本身（<c>TeachingPreviewClass-{classId}</c>）上：产品应聚焦模板根 Border，
    /// 而不是外层 ListBoxItem——焦点在容器上时这里会明确失败，方便定位。
    /// </summary>
    internal static async Task AssertTeachingClassFocusAsync(TeachingView view, object row, string classId)
    {
        if (string.IsNullOrWhiteSpace(classId)) throw new InvalidDataException("需要班级 ID。");

        var window = (Window)TopLevel.GetTopLevel(view)!;
        var list = Find<ItemsControl>(view, "TeachingPreviewList");
        string expected = TeachingRowPrefix + classId;
        var container = await TeachingRevealContainerAsync(list, row, expected);

        var element = TeachingRowElement(container, expected)
            ?? throw new InvalidDataException($"预览行里找不到 AutomationId={expected} 的元素。");

        await Until(() => element.IsFocused, $"变化班 {classId} 的行元素获得键盘焦点");
        if (!element.IsFocused)
        {
            string owner = container.IsFocused ? "外层 ListBoxItem" : "其它控件";
            throw new InvalidDataException(
                $"变化班 {classId} 的行元素没有获得键盘焦点（焦点在{owner}上）；请聚焦 AutomationId={expected} 的 Border。");
        }

        var scroll = TeachingScrollOf(list);
        await TeachingAssertInsideViewportAsync(window, scroll, element, expected);
    }

    // ── 虚拟化列表的定位 ────────────────────────────────────────────────────────

    /// <summary>按模型对象在 <c>Items</c> 里定位 → <c>ScrollIntoView</c> → 等容器实现 → 返回行元素。</summary>
    private static async Task<Control> TeachingRevealAsync(ItemsControl list, object model, string description)
    {
        var container = await TeachingRevealContainerAsync(list, model, description);
        return TeachingRowElement(container, expectedAutomationId: null)
               ?? throw new InvalidDataException($"{description} 里没有带 AutomationId 的行元素。");
    }

    private static async Task<Control> TeachingRevealContainerAsync(ItemsControl list, object model, string description)
    {
        int index = TeachingIndexOf(list, model);
        if (index < 0) throw new InvalidDataException($"{description} 不在 TeachingPreviewList.Items 里。");

        Control? container = null;
        await Until(() =>
        {
            if (list.ContainerFromItem(model) is Control realized && Rendered(realized))
            {
                container = realized;
                return true;
            }

            list.ScrollIntoView(index);
            return false;
        }, $"{description} 实现并进入视口");

        return container!;
    }

    private static Control? TeachingContainerOf(ItemsControl list, int index) =>
        index >= 0 && index < list.ItemCount ? list.ContainerFromIndex(index) as Control : null;

    /// <summary>行元素 = 容器内第一个带 <c>AutomationId</c> 的控件（模板根 Border）。</summary>
    private static Control? TeachingRowElement(Control container, string? expectedAutomationId) =>
        container.GetVisualDescendants().OfType<Control>().FirstOrDefault(control =>
            AutomationProperties.GetAutomationId(control) is { Length: > 0 } id
            && (expectedAutomationId is null || string.Equals(id, expectedAutomationId, StringComparison.Ordinal)));

    private static int TeachingIndexOf(ItemsControl list, object model)
    {
        for (int index = 0; index < list.ItemCount; index++)
        {
            if (ReferenceEquals(list.Items[index], model)) return index;
        }

        return -1;
    }

    // ── 基础工具 ────────────────────────────────────────────────────────────────

    private static bool Rendered(Control control) =>
        control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0;

    private static ScrollViewer TeachingScrollOf(Control list)
    {
        if (list is ScrollViewer self) return self;
        return list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()
            ?? throw new InvalidDataException("TeachingPreviewList has no ScrollViewer");
    }

    private static async Task TeachingScrollToEndAsync(ScrollViewer scroll, string description)
    {
        double target = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        scroll.Offset = new Vector(scroll.Offset.X, target);
        await Until(() => Math.Abs(scroll.Offset.Y - target) < 1, $"{description} 滚动到底");
    }

    private static Rect TeachingClient(Window window) => new(0, 0, window.ClientSize.Width, window.ClientSize.Height);

    private static Rect TeachingRect(Window window, Control control, string description)
    {
        var origin = control.TranslatePoint(default, window);
        if (origin is null) throw new InvalidDataException($"{description} 不在窗口的可视树里。");
        return new Rect(origin.Value, control.Bounds.Size);
    }

    /// <summary>
    /// 控件必须完整落在「滚动视口 ∩ 窗口客户区」内。
    /// 滚动是异步布局：<c>Offset</c> 或 <c>ScrollIntoView</c> 返回时视觉坐标可能还是上一帧的，
    /// 因此每轮都重读 ScrollContentPresenter 与目标矩形，直到真的落进去（必要时补一次 BringIntoView）。
    /// </summary>
    private static async Task TeachingAssertInsideViewportAsync(
        Window window, ScrollViewer scroll, Control control, string description)
    {
        Rect limit = default;
        Rect rect = default;
        bool nudged = false;
        DateTime deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            limit = TeachingViewportLimit(window, scroll, description);
            rect = TeachingRect(window, control, description);
            if (TeachingContains(limit, rect, TeachingViewportTolerance)) return;

            if (!nudged)
            {
                nudged = true;
                control.BringIntoView();
            }

            await Task.Delay(25);
        }

        throw new InvalidDataException(
            $"{description} 没有落进滚动视口：{TeachingDescribe(rect)} / 视口 {TeachingDescribe(limit)}。");
    }

    /// <summary>真正的可视区：ScrollContentPresenter（无它时退回 ScrollViewer 自身）∩ 窗口客户区。</summary>
    private static Rect TeachingViewportLimit(Window window, ScrollViewer scroll, string description)
    {
        var presenter = scroll.GetVisualDescendants().OfType<ScrollContentPresenter>().FirstOrDefault();
        Control viewport = presenter is null ? scroll : presenter;
        var port = TeachingRect(window, viewport, $"{description} 的滚动视口");
        var client = TeachingClient(window);
        if (!client.Intersects(port))
            throw new InvalidDataException(
                $"{description} 的滚动视口不在窗口内：{TeachingDescribe(port)} / {TeachingDescribe(client)}。");
        return TeachingOverlap(port, client);
    }

    private static void TeachingAssertFixed(Rect client, Rect rect, string description)
    {
        if (!TeachingContains(client, rect, TeachingFixedBarTolerance))
            throw new InvalidDataException($"{description} 没有完整留在窗口内：{TeachingDescribe(rect)} / {TeachingDescribe(client)}。");
    }

    private static void TeachingRequireRectUnchanged(Rect before, Rect after, string message)
    {
        bool same = Math.Abs(before.X - after.X) <= TeachingFixedBarTolerance
            && Math.Abs(before.Y - after.Y) <= TeachingFixedBarTolerance
            && Math.Abs(before.Width - after.Width) <= TeachingFixedBarTolerance
            && Math.Abs(before.Height - after.Height) <= TeachingFixedBarTolerance;
        if (!same)
            throw new InvalidDataException($"{message}：滚动前 {TeachingDescribe(before)}，滚动后 {TeachingDescribe(after)}。");
    }

    private static Rect TeachingOverlap(Rect left, Rect right)
    {
        double x = Math.Max(left.X, right.X);
        double y = Math.Max(left.Y, right.Y);
        double width = Math.Min(left.Right, right.Right) - x;
        double height = Math.Min(left.Bottom, right.Bottom) - y;
        return width <= 0 || height <= 0 ? default : new Rect(x, y, width, height);
    }

    private static bool TeachingContains(Rect outer, Rect inner, double tolerance) =>
        inner.X >= outer.X - tolerance
        && inner.Y >= outer.Y - tolerance
        && inner.Right <= outer.Right + tolerance
        && inner.Bottom <= outer.Bottom + tolerance;

    private static string TeachingDescribe(Rect rect) => $"[{rect.X:0.#},{rect.Y:0.#} {rect.Width:0.#}×{rect.Height:0.#}]";
}
