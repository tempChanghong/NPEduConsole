using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("NPEduConsole.ClassCreationDesktop")]

namespace NPEduConsole.App;

internal static class BatchWorkspaceWindow
{
    internal static void FitSchoolWindow(Window editor)
    {
        if (editor.Owner is not Window workspace || editor.WindowState == WindowState.Maximized) return;
        while (workspace.Owner is Window parent) workspace = parent;

        var target = workspace.ClientSize;
        if (target.Width <= 0 || target.Height <= 0 || workspace.Screens.ScreenFromWindow(workspace) is not { } screen) return;
        Size chrome = editor.FrameSize is { } frame ? new Size(
            Math.Max(0, frame.Width - editor.ClientSize.Width), Math.Max(0, frame.Height - editor.ClientSize.Height)) : default;
        var placement = FitAvailable(target, new Size(editor.MinWidth, editor.MinHeight), chrome,
            workspace.Position, screen.WorkingArea, screen.Scaling);
        editor.Position = placement.Position;
        editor.MinWidth = Math.Min(editor.MinWidth, placement.Client.Width);
        editor.MinHeight = Math.Min(editor.MinHeight, placement.Client.Height);
        editor.Width = placement.Client.Width;
        editor.Height = placement.Client.Height;
        // Windows can report a larger decorated frame after the initial client resize.
        Dispatcher.UIThread.Post(() => FitMeasuredFrame(editor, screen.WorkingArea, screen.Scaling), DispatcherPriority.Background);
    }

    private static void FitMeasuredFrame(Window editor, PixelRect workingArea, double scale)
    {
        if (!editor.IsVisible || editor.WindowState != WindowState.Normal || editor.FrameSize is not { } frame) return;
        int width = (int)Math.Ceiling(frame.Width * scale);
        int height = (int)Math.Ceiling(frame.Height * scale);
        if (width > workingArea.Width)
        {
            editor.Width = Math.Max(1, editor.Width - Math.Ceiling((width - workingArea.Width) / scale));
            editor.MinWidth = Math.Min(editor.MinWidth, editor.Width);
        }
        if (height > workingArea.Height)
        {
            editor.Height = Math.Max(1, editor.Height - Math.Ceiling((height - workingArea.Height) / scale));
            editor.MinHeight = Math.Min(editor.MinHeight, editor.Height);
        }
        var position = FitPosition(editor.Position, workingArea, width, height);
        if (editor.Position != position) editor.Position = position;
    }

    internal static (Size Client, PixelPoint Position) FitAvailable(Size requested, Size minimum, Size chrome,
        PixelPoint origin, PixelRect workingArea, double scale)
    {
        var client = new Size(
            Math.Min(Math.Max(requested.Width, minimum.Width), Math.Max(1, Math.Floor(workingArea.Width / scale - chrome.Width))),
            Math.Min(Math.Max(requested.Height, minimum.Height), Math.Max(1, Math.Floor(workingArea.Height / scale - chrome.Height))));
        int width = (int)Math.Ceiling((client.Width + chrome.Width) * scale);
        int height = (int)Math.Ceiling((client.Height + chrome.Height) * scale);
        return (client, FitPosition(origin, workingArea, width, height));
    }

    private static PixelPoint FitPosition(PixelPoint origin, PixelRect workingArea, int width, int height) => new(
        Math.Clamp(origin.X, workingArea.X, Math.Max(workingArea.X, workingArea.Right - width)),
        Math.Clamp(origin.Y, workingArea.Y, Math.Max(workingArea.Y, workingArea.Bottom - height)));
}
