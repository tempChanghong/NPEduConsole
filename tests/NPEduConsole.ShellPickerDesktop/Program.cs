using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using NPEduConsole.App;

internal static class Program
{
    private const int ClassCount = 60;

    [STAThread]
    private static int Main(string[] args)
    {
        string? output = args is ["--verify", var directory] ? directory : null;
        bool interactive = args is ["--interactive"] or [];
        if (output is null && !interactive)
        {
            Console.Error.WriteLine("用法：NPEduConsole.ShellPickerDesktop --verify <输出目录> | --interactive");
            return 2;
        }

        if (output is not null) Directory.CreateDirectory(output);
        return AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect()
            .AfterSetup(builder => Dispatcher.UIThread.Post(() =>
            {
                if (output is null) RunInteractive();
                else _ = VerifyAsync(output);
            }))
            .StartWithClassicDesktopLifetime([]);
    }

    private static IClassicDesktopStyleApplicationLifetime Desktop =>
        (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;

    /// <summary>交互模式：真实窗口里手工确认键盘、窄窗与高对比，不做断言。</summary>
    private static void RunInteractive()
    {
        var (shell, picker, _) = BuildShell(1160, 780);
        picker.SetCatalog(Catalog(), "");
        Start(shell);
    }

    private static void Start(Window shell)
    {
        var original = Desktop.MainWindow;
        Desktop.MainWindow = shell;
        shell.Show();
        // ConsoleApplication 总会建一个登录主窗：只隐藏、不关闭，否则默认的
        // 「最后一个窗口关闭即退出」会让回归提前结束。
        if (original is not null && !ReferenceEquals(original, shell)) original.Hide();
    }

    private static async Task VerifyAsync(string output)
    {
        var (shell, picker, probe) = BuildShell(1160, 780);
        var chosen = new List<ShellSearchItem>();
        picker.TargetChosen += item => chosen.Add(item);
        Start(shell);

        try
        {
            Check(Application.Current!.TryFindResource("SurfaceBrush", shell.ActualThemeVariant, out var surface) &&
                surface is IBrush, "使用应用的真实主题画刷。");

            // 1. 注入目录不会自己弹开。
            picker.SetCatalog(Catalog(), "");
            Check(!picker.IsOpen, "SetCatalog 不会自行展开弹层。");
            Check(picker.ScopeMessage.Length == 0, "完整目录没有范围提示。");

            // 2. 展开后按目录顺序列出；180 个对象只渲染 50 行并说明截断。
            picker.Open();
            await Until(() => picker.IsOpen && picker.Results.Count > 0, "首屏结果");
            Check(picker.Results.Count == 50, "空查询只渲染前 50 项。");
            Check(Footer(picker).Text?.Contains("仅显示前 50") == true, "截断时说明还有更多结果。");
            await Capture(shell, output, "01-open-1160");
            await CapturePopup(picker, output, "02-popup-1160");

            // 3. 按代码过滤：小写也能命中，默认选中的是班级行。
            picker.SearchText = "g1-c07";
            await Until(() => picker.Results.Count == 3, "代码过滤");
            Check(picker.Results.All(item => item.Detail.Contains("G1-C07", StringComparison.Ordinal)), "代码过滤只留该班。");
            Check(picker.Selection is { ScreenId: null }, "过滤后默认选中的是班级行。");
            RaiseKey(SearchBox(picker), Key.Down);
            Check(picker.Selection?.ScreenId is not null, "向下键选择第一台大屏。");
            RaiseKey(SearchBox(picker), Key.Up);
            Check(picker.Selection is { ScreenId: null }, "向上键回到班级。");

            // 4. Enter 回传真实 ClassId；弹层收起、焦点回触发器。
            RaiseKey(SearchBox(picker), Key.Enter);
            await Until(() => !picker.IsOpen, "Enter 关闭弹层");
            Check(chosen.Count == 1 && chosen[0].ClassId == "cls-07" && chosen[0].ScreenId is null, "Enter 回传班级 ID。");
            Check(Trigger(picker).IsFocused, "选择后焦点回到触发器。");

            // 5. 多关键字定位大屏，回传 binding ID。
            picker.Open();
            await Until(() => picker.IsOpen, "再次展开");
            picker.SearchText = "G1-C07 后排";
            await Until(() => picker.Results.Count == 1, "多关键字过滤");
            RaiseKey(SearchBox(picker), Key.Enter);
            await Until(() => chosen.Count == 2, "选择大屏");
            Check(chosen[1].ClassId == "cls-07" && chosen[1].ScreenId == "cls-07-back", "大屏行回传 binding ID。");

            // 6. Esc 只关弹层：不回传、不关窗口、焦点回触发器。
            picker.Open();
            await Until(() => picker.IsOpen, "Esc 前展开");
            picker.SearchText = "G1-C07";
            RaiseKey(SearchBox(picker), Key.Escape);
            await Until(() => !picker.IsOpen, "Esc 关闭弹层");
            Check(chosen.Count == 2, "Esc 不产生选择。");
            Check(shell.IsVisible, "Esc 不关闭主窗口。");
            Check(Trigger(picker).IsFocused, "Esc 后焦点回到触发器。");

            // 7. F4 在控件内展开；Ctrl+K 在窗口任意焦点处展开。
            RaiseKey(Trigger(picker), Key.F4);
            await Until(() => picker.IsOpen, "F4 展开");
            RaiseKey(SearchBox(picker), Key.Escape);
            await Until(() => !picker.IsOpen, "F4 后收起");
            probe.Focus();
            Check(probe.IsKeyboardFocusWithin, "焦点已在窗口里的普通控件上。");
            RaiseKey(shell, Key.K, KeyModifiers.Control);
            await Until(() => picker.IsOpen, "Ctrl+K 展开");
            await Until(() => SearchBox(picker).IsFocused, "Ctrl+K 焦点");
            Check(SearchBox(picker).IsFocused, "Ctrl+K 后焦点进入输入框。");

            // 8. 60 个同名「后排大屏」靠 Detail 互相区分。
            picker.SearchText = "后排大屏";
            await Until(() => picker.Results.Count == 50, "同名过滤");
            Check(picker.Results.All(item => item.Title == "后排大屏"), "同名行标题一致。");
            Check(picker.Results.Select(item => item.Detail).Distinct().Count() == 50, "同名行靠详情区分。");
            await Capture(shell, output, "03-same-name-1160");
            RaiseKey(SearchBox(picker), Key.Escape);
            await Until(() => !picker.IsOpen, "关闭同名结果");

            // 9. 目录不完整：范围说明与已加载对象同时出现。
            picker.SetCatalog(Catalog().Take(9).ToArray(), "该学校目录尚未完全加载：只检索到 3 个班。");
            picker.Open();
            await Until(() => picker.IsOpen, "范围提示展开");
            Check(Scope(picker).IsVisible && Scope(picker).Text == "该学校目录尚未完全加载：只检索到 3 个班。",
                "范围说明原样显示。");
            Check(picker.Results.Count == 9, "范围说明不影响已加载对象。");
            await CapturePopup(picker, output, "04-scope-1160");
            RaiseKey(SearchBox(picker), Key.Escape);
            await Until(() => !picker.IsOpen, "关闭范围结果");

            // 10. 切校：空列表立即生效，弹层收起、查询清空，且不会自行展开。
            picker.Open();
            await Until(() => picker.IsOpen, "切校前展开");
            picker.SetCatalog([], "切换到新的学校，目录尚未载入。");
            await Until(() => !picker.IsOpen, "切校收起弹层");
            Check(picker.SearchText.Length == 0, "切校清掉旧查询。");
            Check(picker.Results.Count == 0, "切校清掉旧对象。");
            picker.Open();
            await Until(() => picker.IsOpen, "空目录展开");
            Check(Empty(picker).IsVisible && Empty(picker).Text == "暂无可查找的对象。", "空目录说明原因。");
            Check(Scope(picker).IsVisible, "空目录仍显示范围说明。");
            RaiseKey(SearchBox(picker), Key.Escape);
            await Until(() => !picker.IsOpen, "关闭空目录");

            // 11. 焦点在控件外时，切目录不抢焦点。
            probe.Focus();
            picker.SetCatalog(Catalog(), "");
            await Task.Delay(120);
            Check(probe.IsKeyboardFocusWithin, "目录更新不抢外部焦点。");

            // 12. 窄窗：弹层不超出窗口宽度，触发器仍可用。
            shell.Width = 400;
            shell.Height = 300;
            await Until(() => shell.ClientSize.Width is > 0 and <= 420, "窄窗生效");
            picker.Open();
            await Until(() => picker.IsOpen && picker.PopupWidth > 0, "窄窗展开");
            Check(picker.PopupWidth <= shell.ClientSize.Width + 0.5, "弹层宽度不超过窗口。");
            Check(Trigger(picker).Bounds.Width > 80, "窄窗触发器仍可用。");
            await Capture(shell, output, "05-narrow-400");
            await CapturePopup(picker, output, "06-narrow-popup-400");
            RaiseKey(SearchBox(picker), Key.Escape);
            await Until(() => !picker.IsOpen, "窄窗关闭");

            // The search is unavailable while its workspace is hidden, e.g. before sign-in.
            var toolbar = (Control)picker.Parent!;
            toolbar.IsVisible = false;
            RaiseKey(shell, Key.K, KeyModifiers.Control);
            picker.Open();
            Check(!picker.IsOpen, "工作区隐藏时不能打开查找。");
            toolbar.IsVisible = true;

            // 13. 高对比：不靠硬编码颜色，边框与列表仍在。
            shell.Width = 1160;
            shell.Height = 780;
            shell.RequestedThemeVariant = ConsoleHighContrast.Dark;
            await Until(() => shell.ClientSize.Width > 1000, "宽窗恢复");
            picker.Open();
            await Until(() => picker.IsOpen && picker.Results.Count > 0, "高对比展开");
            Check(Trigger(picker).BorderThickness.Left >= 1, "触发器保留可见边框。");
            Check(Surface(picker).Background is ISolidColorBrush { Color: var background } && background == Colors.Black,
                "弹层实际使用高对比主题的黑色表面。");
            Check(Results(picker).ItemCount == picker.Results.Count, "结果列表与目录一致。");
            await Capture(shell, output, "07-high-contrast");
            await CapturePopup(picker, output, "08-high-contrast-popup");

            End("PASS: 全校检索在 1160×780 与 400×300、高对比与键盘（F4/Ctrl+K/方向键/Enter/Esc）下均可用；"
                + "同名靠详情区分、切校清空且不自动展开、目录不完整有范围说明；未连接任何服务器。", 0);
        }
        catch (Exception error)
        {
            End("FAIL: " + error, 1);
        }

        void End(string message, int code)
        {
            File.WriteAllText(Path.Combine(output, "result.txt"), message + Environment.NewLine);
            Console.WriteLine(message);
            Desktop.Shutdown(code);
        }
    }

    private static (Window Shell, ShellObjectPicker Picker, TextBox Probe) BuildShell(double width, double height)
    {
        var picker = new ShellObjectPicker
        {
            Width = 260,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        // 焦点探针：模拟外壳里获得焦点的普通控件，用来验证 Ctrl+K 不依赖焦点位置、切换目录不抢焦点。
        var probe = new TextBox { PlaceholderText = "焦点探针" };
        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            Margin = new Thickness(16)
        };

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var school = new TextBlock
        {
            Text = "示范中学",
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        top.Children.Add(school);
        top.Children.Add(picker);
        Grid.SetColumn(picker, 1);
        var account = new TextBlock
        {
            Text = "学校管理员",
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        top.Children.Add(account);
        Grid.SetColumn(account, 2);
        content.Children.Add(top);

        var divider = new Border { Height = 1, Margin = new Thickness(0, 10, 0, 12), Background = Brushes.Gainsboro };
        content.Children.Add(divider);
        Grid.SetRow(divider, 1);

        content.Children.Add(probe);
        Grid.SetRow(probe, 2);

        var shell = new Window
        {
            Title = "ShellObjectPicker 回归",
            Width = width,
            Height = height,
            MinWidth = 320,
            MinHeight = 240,
            Content = content
        };
        return (shell, picker, probe);
    }

    /// <summary>60 个班、每班前排与后排各一块大屏，共 180 个对象；同名大屏用来验证详情区分。</summary>
    private static ShellSearchItem[] Catalog()
    {
        var items = new List<ShellSearchItem>(ClassCount * 3);
        for (int index = 1; index <= ClassCount; index++)
        {
            string code = $"G1-C{index:D2}";
            string name = $"高一（{index}）班";
            string classId = $"cls-{index:D2}";
            items.Add(new ShellSearchItem(classId, null, name, $"{code} · 高一年级"));
            items.Add(new ShellSearchItem(classId, $"{classId}-front", "前排大屏", $"{code} · {name} · 网页大屏"));
            items.Add(new ShellSearchItem(classId, $"{classId}-back", "后排大屏", $"{code} · {name} · NPEduTools"));
        }

        return items.ToArray();
    }

    private static TextBox SearchBox(ShellObjectPicker picker) => FindInPicker<TextBox>(picker, "SearchBox");
    private static Button Trigger(ShellObjectPicker picker) => FindInPicker<Button>(picker, "TriggerButton");
    private static ListBox Results(ShellObjectPicker picker) => FindInPicker<ListBox>(picker, "ResultsList");
    private static TextBlock Scope(ShellObjectPicker picker) => FindInPicker<TextBlock>(picker, "ScopeText");
    private static TextBlock Empty(ShellObjectPicker picker) => FindInPicker<TextBlock>(picker, "EmptyText");
    private static TextBlock Footer(ShellObjectPicker picker) => FindInPicker<TextBlock>(picker, "FooterHint");
    private static Border Surface(ShellObjectPicker picker) => FindInPicker<Border>(picker, "PopupSurface");

    private static T FindInPicker<T>(ShellObjectPicker picker, string name) where T : Control =>
        picker.FindControl<T>(name)
        ?? picker.GetLogicalDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? PopupContent(picker).GetLogicalDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new InvalidDataException("找不到控件：" + name);

    /// <summary>弹层内容挂在 Popup.Child 下，不在选择器自身的视觉/逻辑子树里，需要单独进去找。</summary>
    private static Control PopupContent(ShellObjectPicker picker) =>
        picker.GetLogicalDescendants().OfType<Popup>().FirstOrDefault()?.Child as Control
        ?? throw new InvalidDataException("找不到选择器弹层");

    private static void RaiseKey(Control target, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        target.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
            Source = target
        });

    private static async Task Until(Func<bool> condition, string step)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            while (!condition()) await Task.Delay(25, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(step);
        }
    }

    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidDataException(message);
    }

    /// <summary>截图是证据，不是判定条件：失败只记录，不中断回归。</summary>
    private static async Task Capture(Window window, string output, string name)
    {
        try
        {
            await Task.Delay(160);
            if (window.Content is not Control root || root.Bounds.Width < 1) return;
            using var image = new RenderTargetBitmap(new PixelSize((int)root.Bounds.Width, (int)root.Bounds.Height));
            image.Render(root);
            image.Save(Path.Combine(output, name + ".png"), new PngBitmapEncoderOptions());
        }
        catch (Exception error)
        {
            Console.WriteLine($"NOTE: 截图 {name} 失败：{error.Message}");
        }
    }

    /// <summary>弹层在独立的 PopupRoot 里，必须单独渲染才进得了证据。</summary>
    private static async Task CapturePopup(ShellObjectPicker picker, string output, string name)
    {
        try
        {
            await Task.Delay(160);
            if (TopLevel.GetTopLevel(Surface(picker)) is not Control root || root.Bounds.Width < 1) return;
            using var image = new RenderTargetBitmap(new PixelSize((int)root.Bounds.Width, (int)root.Bounds.Height));
            image.Render(root);
            image.Save(Path.Combine(output, name + ".png"), new PngBitmapEncoderOptions());
        }
        catch (Exception error)
        {
            Console.WriteLine($"NOTE: 弹层截图 {name} 失败：{error.Message}");
        }
    }
}
