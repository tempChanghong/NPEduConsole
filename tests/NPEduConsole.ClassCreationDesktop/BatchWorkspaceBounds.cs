using Avalonia;
using Avalonia.Controls;
using NPEduConsole.App;

internal static partial class Program
{
    private static void VerifyBatchPlacementMath()
    {
        var workArea = new PixelRect(-1920, 36, 1920, 1044);
        foreach (double scale in new[] { 1.25, 1.5, 2.0 })
        {
            var chrome = new Size(16, 39);
            var placement = BatchWorkspaceWindow.FitAvailable(new Size(1160, 780), new Size(700, 480), chrome,
                new PixelPoint(-270, 840), workArea, scale);
            Check(placement.Position.X >= workArea.X && placement.Position.Y >= workArea.Y &&
                placement.Position.X + Math.Ceiling((placement.Client.Width + chrome.Width) * scale) <= workArea.Right &&
                placement.Position.Y + Math.Ceiling((placement.Client.Height + chrome.Height) * scale) <= workArea.Bottom,
                $"right/bottom work area is preserved at {scale:P0} scaling");
        }
    }

    private static void CheckBatchWindowOnScreen(Window window)
    {
        if (window.Screens.ScreenFromWindow(window) is not { } screen || window.FrameSize is not { } frame)
            throw new InvalidDataException("No real Windows display geometry.");
        var position = window.Position;
        Check(position.X >= screen.WorkingArea.X - 2 && position.Y >= screen.WorkingArea.Y - 2 &&
            position.X + frame.Width * screen.Scaling <= screen.WorkingArea.Right + 2 &&
            position.Y + frame.Height * screen.Scaling <= screen.WorkingArea.Bottom + 2,
            "the editor from a lower-right owner fits the Windows display work area");
    }

    private static bool FitsBatchWindow(Window window, Control control)
    {
        if (!control.IsEffectivelyVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0 ||
            control.TranslatePoint(default, window) is not { } origin) return false;
        var size = window.ClientSize;
        return origin.X >= -1 && origin.Y >= -1 &&
            origin.X + control.Bounds.Width <= size.Width + 1 && origin.Y + control.Bounds.Height <= size.Height + 1;
    }
}
