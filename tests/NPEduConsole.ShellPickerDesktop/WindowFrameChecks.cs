using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using NPEduConsole.App;

internal static class WindowFrameChecks
{
    public static async Task Run(IClassicDesktopStyleApplicationLifetime desktop, string output)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("此回归需要 Windows。");
            var main = (MainWindow)desktop.MainWindow!;
            main.RequestedThemeVariant = ThemeVariant.Light;
            await Task.Delay(350);
            Require(main.IsExtendedIntoWindowDecorations, "主窗启用自绘标题区");
            var handle = main.TryGetPlatformHandle()!.Handle;
            var status = main.FindControl<Button>("OpenServerStatusButton")!;
            var buttonPosition = status.TranslatePoint(new Point(status.Bounds.Width / 2, status.Bounds.Height / 2), main)!.Value;
            Require(Hit(main, buttonPosition) == 1, "标题栏运维按钮仍接收普通鼠标输入");
            Require(Hit(main, new Point(main.ClientSize.Width / 2, 22)) == 2, "标题留白由 Windows 识别为拖动区");
            var chrome = main.GetVisualAncestors().Last();
            var captionButtons = chrome.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible &&
                b.Name is "PART_MinimizeButton" or "PART_MaximizeButton" or "PART_CloseButton" or "PART_FullScreenButton").ToArray();
            Require(captionButtons.Length == 3 && captionButtons.All(b => b.Name != "PART_FullScreenButton"), "顶栏正好显示三枚系统按钮");
            var titleText = chrome.GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "PART_TitleTextPanel");
            Require(!titleText.IsEffectivelyVisible || titleText.Opacity == 0, "默认窗口标题不会覆盖品牌与运维按钮");
            var maximize = captionButtons.Single(b => b.Name == "PART_MaximizeButton");
            Require(Hit(main, maximize.TranslatePoint(new Point(maximize.Bounds.Width / 2, maximize.Bounds.Height / 2), main)!.Value) == 9,
                "Windows 将最大化按钮识别为 HTMAXBUTTON");
            Require(GetWindowRect(handle, out var outer), "读取 Windows 外框");
            Require(SendMessage(handle, 0x0084, 0, ((outer.Left + 1) & 65535) | ((outer.Top + 100) << 16)) == 10,
                "窗口左边缘保留系统缩放");
            Console.WriteLine($"显示缩放 {main.RenderScaling:P0}；系统 {Environment.OSVersion.Version}");
            Capture(main, output, "01-login-light");

            main.RequestedThemeVariant = ThemeVariant.Dark;
            main.Width = 840;
            main.Height = 480;
            var account = main.FindControl<TextBox>("Username")!;
            account.Text = "001";
            account.Focus();
            await Task.Delay(180);
            Require(account.Text == "001" && account.IsFocused, "紧凑窗保留输入焦点及前导零");
            var field = main.FindControl<FormField>("AccountField")!;
            field.ReportError("请输入短账号。");
            account.Text = "教师001";
            await Task.Delay(70);
            Require(string.IsNullOrEmpty(field.ErrorMessage) && account.Text == "教师001", "更正中文与数字后清除字段错误");
            account.Text = "001";
            Capture(main, output, "02-login-dark-compact");
            var password = main.FindControl<TextBox>("Password")!;
            Require(password.PasswordChar != default, "密码输入沿用遮盖模式");

            var child = new ServerStatusWindow { Width = 840, Height = 480, RequestedThemeVariant = ThemeVariant.Dark };
            child.Show(main);
            await Task.Delay(150);
            Require(child.IsExtendedIntoWindowDecorations && child.Padding.Top >= 40, "过渡子窗正文避让统一标题栏");
            Require(Hit(child, new Point(190, 22)) == 2, "子窗口标题区保留拖动");
            Capture(child, output, "03-operations-dark");
            child.Close();

            var temporary = new Window { Width = 390, Height = 200, Title = "确认操作", CanResize = false };
            temporary.Show(main);
            await Task.Delay(120);
            Require(temporary.IsExtendedIntoWindowDecorations && temporary.Padding.Top >= 40, "代码创建的对话框使用同一窗框");
            temporary.Close();

            var titlePoint = main.PointToScreen(new Point(main.ClientSize.Width / 2, 22));
            PostMessage(handle, 0x00A3, 2, (titlePoint.X & 65535) | (titlePoint.Y << 16));
            await WaitState(main, WindowState.Maximized);
            PostMessage(handle, 0x0112, 0xF120, 0);
            await WaitState(main, WindowState.Normal);
            foreach (var (command, expected) in new[] { (0xF030, WindowState.Maximized), (0xF120, WindowState.Normal),
                         (0xF020, WindowState.Minimized), (0xF120, WindowState.Normal) })
            {
                PostMessage(handle, 0x0112, command, 0);
                await WaitState(main, expected);
            }
            main.RequestedThemeVariant = ConsoleHighContrast.Dark;
            await Task.Delay(150);
            Require(main.FindControl<TextBox>("Username")!.Foreground is ISolidColorBrush ink && ink.Color == Colors.White,
                "高对比字段采用系统文字色");
            Capture(main, output, "04-login-high-contrast");
            Console.WriteLine("PASS Window frame and input checks");
            desktop.Shutdown(0);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            desktop.Shutdown(1);
        }
    }

    private static async Task WaitState(Window window, WindowState expected)
    {
        for (int attempt = 0; window.WindowState != expected && attempt < 40; attempt++) await Task.Delay(25);
        Require(window.WindowState == expected, $"Windows 系统命令到达 {expected}");
    }

    private static nint Hit(Window window, Point local)
    {
        var screen = window.PointToScreen(local);
        return SendMessage(window.TryGetPlatformHandle()!.Handle, 0x0084, 0, (screen.X & 65535) | (screen.Y << 16));
    }

    private static void Capture(Window window, string output, string name)
    {
        var root = window.GetVisualAncestors().LastOrDefault() ?? window;
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(root.Bounds.Width), (int)Math.Ceiling(root.Bounds.Height)));
        bitmap.Render(root);
        bitmap.Save(Path.Combine(output, name + ".png"), new PngBitmapEncoderOptions());
    }

    private static void Require(bool value, string check)
    {
        if (!value) throw new InvalidDataException(check);
        Console.WriteLine("PASS " + check);
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out WindowRect bounds);
}
