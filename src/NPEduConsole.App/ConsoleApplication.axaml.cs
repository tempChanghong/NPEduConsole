using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace NPEduConsole.App;

public sealed partial class ConsoleApplication : Application
{
    internal string? LastServerFile { get; init; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        ConsoleHighContrast.FollowPlatform(this);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(LastServerFile);
        base.OnFrameworkInitializationCompleted();
    }
}
