using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using System.Runtime.InteropServices;

namespace NPEduConsole.App;

public static class ConsoleHighContrast
{
    public static ThemeVariant Light { get; } = new("ConsoleContrastLight", ThemeVariant.Light);
    public static ThemeVariant Dark { get; } = new("ConsoleContrastDark", ThemeVariant.Dark);

    internal static void FollowPlatform(Application app)
    {
        var themes = app.Resources.ThemeDictionaries;
        themes[Light] = Palette(Colors.White, Colors.Black, Colors.Navy, Colors.White);
        themes[Dark] = Palette(Colors.Black, Colors.White, Colors.Yellow, Colors.Black);
        if (app.PlatformSettings is not { } settings) return;

        Apply(settings.GetColorValues());
        settings.ColorValuesChanged += (_, _) =>
        {
            // Read the current preference on the UI thread, including changes to the active contrast palette.
            if (Dispatcher.UIThread.CheckAccess()) Apply(settings.GetColorValues());
            else Dispatcher.UIThread.Post(() => Apply(settings.GetColorValues()));
        };

        void Apply(PlatformColorValues values)
        {
            if (values.ContrastPreference != ColorContrastPreference.High)
            {
                if (app.RequestedThemeVariant == Light || app.RequestedThemeVariant == Dark)
                    app.RequestedThemeVariant = ThemeVariant.Default;
                return;
            }

            var variant = values.ThemeVariant == PlatformThemeVariant.Dark ? Dark : Light;
            if (OperatingSystem.IsWindows())
            {
                var surface = SystemColor(5); // COLOR_WINDOW
                var ink = SystemColor(8); // COLOR_WINDOWTEXT
                variant = 0.299 * surface.R + 0.587 * surface.G + 0.114 * surface.B < 128 ? Dark : Light;
                themes[variant] = Palette(surface, ink, SystemColor(13), SystemColor(14)); // HIGHLIGHT / HIGHLIGHTTEXT
            }
            app.RequestedThemeVariant = variant;
        }
    }

    private static ResourceDictionary Palette(Color surface, Color ink, Color selected, Color onSelected)
    {
        var result = new ResourceDictionary();
        Set(surface, "LoginCanvasBrush", "FrameBrush", "FieldBrush", "CanvasBrush", "SidebarBrush", "SurfaceBrush", "ScrollRailBrush", "AccentTintBrush", "AccentSoftBrush", "OverviewBannerBrush",
            "ErrorTintBrush", "WarningTintBrush", "SuccessTintBrush", "ComboBoxBackground",
            "ComboBoxDropDownBackground", "ComboBoxBackgroundPointerOver", "ComboBoxBackgroundPressed",
            "ComboBoxItemBackgroundPointerOver", "ComboBoxItemBackgroundPressed", "ComboBoxItemBackgroundSelected",
            "ComboBoxItemBackgroundSelectedPointerOver", "ComboBoxItemBackgroundSelectedPressed");
        Set(ink, "FieldEdgeBrush", "CaptionButtonForeground", "InkBrush", "MutedBrush", "LineBrush", "ScrollGripBrush", "AccentBrush", "AccentStrongBrush", "FocusBrush",
            "ErrorBrush", "WarningBrush", "SuccessBrush", "ComboBoxBorderBrush", "ComboBoxDropDownBorderBrush",
            "ComboBoxBorderBrushPointerOver", "ComboBoxBorderBrushPressed", "ComboBoxBackgroundBorderBrushFocused",
            "ComboBoxItemBorderBrushSelected", "ComboBoxItemBorderBrushSelectedPointerOver",
            "ComboBoxItemBorderBrushSelectedPressed");
        Set(selected, "AccentButtonBackground", "AccentButtonBackgroundPointerOver", "AccentButtonBackgroundPressed");
        Set(onSelected, "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed");
        return result;

        void Set(Color color, params string[] keys)
        {
            var brush = new ImmutableSolidColorBrush(color);
            foreach (var key in keys) result[key] = brush;
        }
    }

    private static Color SystemColor(int index)
    {
        var value = GetSysColor(index);
        return Color.FromRgb((byte)value, (byte)(value >> 8), (byte)(value >> 16));
    }

    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);
}
