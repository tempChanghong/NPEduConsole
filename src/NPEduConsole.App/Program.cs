using Avalonia;

namespace NPEduConsole.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        AppBuilder.Configure<ConsoleApplication>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}
