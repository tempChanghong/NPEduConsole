using Avalonia;
using System.Globalization;
using System.Runtime.InteropServices;

namespace NPEduConsole.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
#if DEBUG
        const string localProfileArgument = "--vs-local-profile";
        if (args.Contains(localProfileArgument, StringComparer.Ordinal))
        {
            var appArgs = args.Where(argument => argument != localProfileArgument).ToArray();
            RunDesktop(appArgs, Path.Combine(AppContext.BaseDirectory, "vs-profile"));
            return;
        }
#endif
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string profile = Path.Combine(localData, "NPEduConsole");
        if (OperatingSystem.IsWindows() &&
            string.Equals(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
                Path.Combine(localData, "Programs", "NPEduConsole"), StringComparison.OrdinalIgnoreCase))
        {
            using var lease = TryOpenInstallation(profile);
            if (lease is not null) RunDesktop(args, profile);
            return;
        }
        RunDesktop(args, profile);
    }

    private static void RunDesktop(string[] args, string profile) =>
        AppBuilder.Configure(() => new ConsoleApplication
        {
            LastServerFile = Path.Combine(profile, "last-server.txt")
        }).UsePlatformDetect().StartWithClassicDesktopLifetime(args);

    private static FileStream? TryOpenInstallation(string profile)
    {
        try
        {
            Directory.CreateDirectory(profile);
            return new FileStream(Path.Combine(profile, "installation.lock"), FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            string message = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh"
                ? "暂时无法打开 NPEduConsole。请等待安装或卸载结束，或检查此 Windows 用户的配置目录。"
                : "NPEduConsole could not open. Wait for setup or uninstall to finish, or check this Windows user's settings directory.";
            MessageBoxW(IntPtr.Zero, message, "NPEduConsole", 0x30);
            return null;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr window, string text, string title, uint type);
}
