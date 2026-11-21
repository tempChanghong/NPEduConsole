using Avalonia;
using Avalonia.Controls;
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
    }

    internal static (Size Client, PixelPoint Position) FitAvailable(Size requested, Size minimum, Size chrome,
        PixelPoint origin, PixelRect workingArea, double scale)
    {
        var client = new Size(
            Math.Min(Math.Max(requested.Width, minimum.Width), Math.Max(1, Math.Floor(workingArea.Width / scale - chrome.Width))),
            Math.Min(Math.Max(requested.Height, minimum.Height), Math.Max(1, Math.Floor(workingArea.Height / scale - chrome.Height))));
        int right = workingArea.Right - (int)Math.Ceiling((client.Width + chrome.Width) * scale);
        int bottom = workingArea.Bottom - (int)Math.Ceiling((client.Height + chrome.Height) * scale);
        return (client, new PixelPoint(
            Math.Clamp(origin.X, workingArea.X, Math.Max(workingArea.X, right)),
            Math.Clamp(origin.Y, workingArea.Y, Math.Max(workingArea.Y, bottom))));
    }
}
