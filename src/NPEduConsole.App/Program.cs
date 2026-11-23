using Avalonia;

namespace NPEduConsole.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        AppBuilder.Configure(() => new ConsoleApplication
        {
            LastServerFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NPEduConsole", "last-server.txt")
        }).UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}
