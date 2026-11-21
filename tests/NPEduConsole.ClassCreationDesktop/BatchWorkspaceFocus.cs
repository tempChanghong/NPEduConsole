using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using NPEduConsole.App;

// 与 Program.cs 同处全局命名空间，才能成为同一个 partial 类型。

/// <summary>
/// 第二轮交互的局部回归：批量开班 / 配置授课科目的「冲突定位到具体编辑器」与「办理完返回原入口」。
///
/// 依赖的稳定契约（产品侧提供）：
/// <list type="bullet">
/// <item>批量开班：冲突代码框 <c>AutomationId=BatchClassCode-{1 基服务器行号}</c>；
/// 表头 <c>BatchColumnLabels</c>；表格滚动区 <c>BatchRowScroll</c>；固定操作栏 <c>BatchWorkspaceActions</c>；
/// 列表 <c>BatchRows</c>；单班入口 <c>OpenClassBatchButton</c>。</item>
/// <item>配置授课科目：冲突「包含」框 <c>AutomationId=SubjectSetupPair-{classId}-{subjectId}</c>；
/// 表头 <c>SubjectSetupColumnLabels</c>；列表 <c>SubjectSetupRows</c>；固定操作栏 <c>SubjectSetupActions</c>；
/// 待核对入口 <c>SubjectSetupReadButton</c>。</item>
/// </list>
///
/// 视口判断一律以「表格自己的滚动视口」为准，而不是整窗：行被滚出视口时它的 Bounds 仍然存在，
/// 只和窗口求交会给出假通过。
/// </summary>
internal static partial class Program
{
    /// <summary>行/编辑器必须完整落在滚动视口内的容差：滚动偏移可能是小数像素。</summary>
    private const double ViewportTolerance = 1.0;

    /// <summary>固定表头与操作栏必须完整落在客户区内的容差。</summary>
    private const double FixedBarTolerance = 0.5;

    /// <summary>
    /// 批量开班：第 <paramref name="row"/> 行（服务器原行序，1 基）的班级代码框必须存在、内容匹配、
    /// 拿到键盘焦点，并且真正滚进表格视口；表头与操作栏仍完整可见（840×480、100 行长表同样适用）。
    /// </summary>
    internal static async Task AssertBatchConflictFocusAsync(ClassCreationWindow window, int row, string code)
    {
        Check(row >= 1, "服务器行号必须从 1 开始。");
        Check(!string.IsNullOrWhiteSpace(code), "需要提供服务器指出的班级代码。");

        var codeBox = await UntilElementAsync<TextBox>(window, $"BatchClassCode-{row}", $"第 {row} 行的班级代码框");

        // 行序契约：这一行的代码框里必须是服务器指出的那个代码；错行会在此立刻暴露。
        // 只比内容不比大小写：外壳展示统一大写，服务器回的是提交时的写法。
        Check(SameCode(codeBox.Text, code), $"第 {row} 行的班级代码是「{codeBox.Text ?? ""}」，服务器指的是「{code}」。");

        await Until(() => codeBox.IsFocused, $"第 {row} 行的班级代码框获得键盘焦点");
        Check(codeBox.IsFocused, $"第 {row} 行的班级代码框没有拿到键盘焦点。");

        var scroll = Find<ScrollViewer>(window, "BatchRowScroll");
        Check(IsRendered(scroll), "批量开班的表格滚动区不可见。");
        Check(scroll.GetVisualDescendants().Contains(codeBox), "班级代码框不在 BatchRowScroll 里。");
        AssertInsideViewport(window, scroll, codeBox, $"第 {row} 行的班级代码框");

        AssertFixedBarsInViewport(window, "批量开班", Find<Control>(window, "BatchColumnLabels"), Find<Control>(window, "BatchWorkspaceActions"));
    }

    /// <summary>
    /// 配置授课科目：<paramref name="classId"/> + <paramref name="subjectId"/> 那一格的「包含」框必须存在、
    /// 拿到键盘焦点，并且真正滚进表格视口；表头与操作栏仍完整可见。
    /// </summary>
    internal static async Task AssertSubjectConflictFocusAsync(SubjectSetupWindow window, string classId, string subjectId)
    {
        Check(!string.IsNullOrWhiteSpace(classId), "需要提供班级 ID。");
        Check(!string.IsNullOrWhiteSpace(subjectId), "需要提供学科 ID。");

        string automationId = $"SubjectSetupPair-{classId}-{subjectId}";
        var toggle = await UntilElementAsync<Control>(window, automationId, "冲突行的「包含」框");

        await Until(() => toggle.IsFocused, "冲突行的「包含」框获得键盘焦点");
        Check(toggle.IsFocused, "冲突行的「包含」框没有拿到键盘焦点。");

        var rows = Find<Control>(window, "SubjectSetupRows");
        Check(IsRendered(rows), "授课科目列表不可见。");
        Check(rows.GetVisualDescendants().Contains(toggle), "「包含」框不在 SubjectSetupRows 里。");
        AssertInsideViewport(window, rows, toggle, "冲突行的「包含」框");

        AssertFixedBarsInViewport(window, "配置授课科目", Find<Control>(window, "SubjectSetupColumnLabels"), Find<Control>(window, "SubjectSetupActions"));
    }

    /// <summary>
    /// 办理完返回：<paramref name="expectedName"/> 指定的入口控件重新拿到键盘焦点并在视口内。
    /// 批量开班返回单班时传 <c>OpenClassBatchButton</c>（此时批量工作区必须已经收起）。
    /// </summary>
    internal static async Task AssertBatchFocusReturnAsync(ClassCreationWindow window, string expectedName)
    {
        var target = await UntilReturnTargetAsync(window, expectedName);
        if (expectedName == "OpenClassBatchButton" && window.FindControl<Control>("BatchFields") is { } batch)
            Check(!batch.IsVisible, "返回单班后批量工作区仍然可见。");
        AssertReturned(window, target, expectedName);
    }

    /// <summary>
    /// 重新打开待核对预览后，<paramref name="expectedName"/>（约定为 <c>SubjectSetupReadButton</c>）
    /// 拿到键盘焦点并在视口内。
    /// </summary>
    internal static async Task AssertSubjectFocusReturnAsync(SubjectSetupWindow window, string expectedName)
    {
        var target = await UntilReturnTargetAsync(window, expectedName);
        AssertReturned(window, target, expectedName);
    }

    // ── 共用断言 ────────────────────────────────────────────────────────────────

    private static async Task<Control> UntilReturnTargetAsync(Window window, string expectedName)
    {
        Check(!string.IsNullOrWhiteSpace(expectedName), "需要提供应重新获得焦点的控件名。");
        var target = Find<Control>(window, expectedName);
        await Until(() => target.IsEffectivelyVisible && target.Bounds.Width > 0 && target.Bounds.Height > 0,
            $"返回后的入口 {expectedName} 重新显示");
        await Until(() => target.IsFocused, $"返回后的入口 {expectedName} 获得键盘焦点");
        return target;
    }

    private static void AssertReturned(Window window, Control target, string expectedName)
    {
        Check(target.IsFocused, $"返回后焦点没有回到 {expectedName}。");
        Check(target.IsEffectivelyEnabled, $"返回后的入口 {expectedName} 不可用。");
        AssertInClient(window, target, $"返回后的入口 {expectedName}", FixedBarTolerance);
    }

    /// <summary>控件必须完整落在「滚动视口 ∩ 窗口客户区」内；只和整窗相交不算通过。</summary>
    private static void AssertInsideViewport(Window window, Control viewport, Control control, string description)
    {
        var client = ClientRect(window);
        var port = ViewportRect(window, viewport, $"{description} 的滚动视口");
        Check(client.Intersects(port), $"{description} 的滚动视口不在窗口内：{Describe(port)} / {Describe(client)}。");

        var limit = Overlap(port, client);
        var rect = ViewportRect(window, control, description);
        Check(Contains(limit, rect, ViewportTolerance),
            $"{description} 没有完整落在表格视口内：{Describe(rect)} 超出 {Describe(limit)}。");
    }

    private static void AssertFixedBarsInViewport(Window window, string page, Control header, Control actions)
    {
        Check(IsRendered(header), $"{page} 的表头不可见。");
        Check(IsRendered(actions), $"{page} 的固定操作栏不可见。");

        var client = ClientRect(window);
        var headerRect = ViewportRect(window, header, $"{page} 表头");
        var actionsRect = ViewportRect(window, actions, $"{page} 操作栏");
        Check(Contains(client, headerRect, FixedBarTolerance),
            $"{page} 的表头没有完整留在视口内：{Describe(headerRect)} / {Describe(client)}。");
        Check(Contains(client, actionsRect, FixedBarTolerance),
            $"{page} 的操作栏没有完整留在视口内：{Describe(actionsRect)} / {Describe(client)}。");
    }

    private static void AssertInClient(Window window, Control control, string description, double tolerance)
    {
        var client = ClientRect(window);
        var rect = ViewportRect(window, control, description);
        Check(Contains(client, rect, tolerance), $"{description} 不在窗口内：{Describe(rect)} / {Describe(client)}。");
    }

    // ── 基础工具 ────────────────────────────────────────────────────────────────

    /// <summary>在可视树里按 AutomationId 找控件；等到它真正渲染出尺寸为止（列表可能还在生成行）。</summary>
    private static async Task<T> UntilElementAsync<T>(Visual root, string automationId, string description) where T : Control
    {
        T? found = null;
        await Until(() => (found = root.GetVisualDescendants().OfType<T>()
                .FirstOrDefault(control => AutomationProperties.GetAutomationId(control) == automationId)) is not null
            && IsRendered(found!), description);
        return found!;
    }

    private static bool IsRendered(Control control) =>
        control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0;

    private static bool SameCode(string? left, string right) =>
        string.Equals((left ?? "").Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static Rect ClientRect(Window window) => new(0, 0, window.ClientSize.Width, window.ClientSize.Height);

    private static Rect ViewportRect(Window window, Control control, string description)
    {
        var origin = control.TranslatePoint(default, window);
        Check(origin is not null, $"{description} 不在窗口的可视树里。");
        return new Rect(origin!.Value, control.Bounds.Size);
    }

    /// <summary>两个矩形的交叠区域；不相交时返回空矩形。</summary>
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

    private static string Describe(Rect rect) =>
        $"[{rect.X:0.#},{rect.Y:0.#} {rect.Width:0.#}×{rect.Height:0.#}]";
}
