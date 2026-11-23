using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;

internal static partial class Program
{
    private static async Task CancelOrganizationPickerAsync(Window owner, Button opener)
    {
        var handle = owner.TryGetPlatformHandle()?.Handle ?? throw new InvalidDataException("Expected the Windows main window.");
        // The native dialog may run its own modal message loop; observe and cancel it off the UI thread.
        var cancel = Task.Run(async () =>
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(15))
            {
                nint dialog = 0;
                while ((dialog = OrganizationPickerNative.FindWindowEx(0, dialog, "#32770", "打开旧组织 JSON")) != 0)
                {
                    OrganizationPickerNative.GetWindowThreadProcessId(dialog, out var processId);
                    if (processId != Environment.ProcessId || OrganizationPickerNative.GetWindow(dialog, 4) != handle) continue;
                    if (!OrganizationPickerNative.PostMessage(dialog, 0x0010, 0, 0))
                        throw new InvalidDataException("Windows did not accept the picker close request.");
                    return;
                }
                await Task.Delay(40);
            }
            throw new TimeoutException("The native JSON picker did not appear owned by this main window.");
        });
        Click(opener);
        await cancel;
        await Until(() => opener.IsEffectivelyEnabled && opener.IsEffectivelyVisible && owner.IsActive,
            "native JSON picker cancels back into the same active import page");
    }

    private static class OrganizationPickerNative
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint FindWindowEx(nint parent, nint after, string windowClass, string title);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);

        [DllImport("user32.dll")]
        internal static extern nint GetWindow(nint window, uint command);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
    }
}
