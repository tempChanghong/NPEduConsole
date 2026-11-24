using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// Drive the same Windows file name edit as a keyboard user. The owner, PID,
// active dialog and focused edit must all match before any input is sent.
public static class ReleaseFilePicker
{
    public static string LastCheck { get; private set; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    {
        public int x, y;
        public uint data, flags, time;
        public UIntPtr extra;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    {
        public ushort key, scan;
        public uint flags, time;
        public UIntPtr extra;
    }
    [StructLayout(LayoutKind.Explicit)] private struct InputData
    {
        [FieldOffset(0)] public MouseInput mouse;
        [FieldOffset(0)] public KeyboardInput keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Input
    {
        public uint type;
        public InputData data;
    }
    [StructLayout(LayoutKind.Sequential)] private struct GuiInfo
    {
        public uint size, flags;
        public IntPtr active, focus, capture, menuOwner, moveSize, caret;
        public int left, top, right, bottom;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr previous, string cls, string title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint mode);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int x, y; }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder value, int capacity);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);

    public static bool Choose(uint process, IntPtr owner, string title, string path, bool modernFileName, bool enter)
    {
        LastCheck = "waiting for the named dialog";
        IntPtr dialog = IntPtr.Zero;
        while ((dialog = FindWindowEx(IntPtr.Zero, dialog, "#32770", title)) != IntPtr.Zero)
        {
            uint pid;
            uint thread = GetWindowThreadProcessId(dialog, out pid);
            if (pid != process) continue;
            LastCheck = "waiting for the visible dialog owned by the main window";
            if (GetWindow(dialog, 4) != owner || !IsWindowVisible(dialog)) continue;
            Thread.Sleep(450);
            if (GetForegroundWindow() != dialog) SetForegroundWindow(dialog);
            LastCheck = "waiting for the owned dialog to take foreground";
            if (GetForegroundWindow() != dialog) return false;
            if (!FilenameFocused(dialog, thread, modernFileName))
            {
                if (!Send(dialog, new[] { Key(0x12), Key(0x4E), Key(0x4E, 2), Key(0x12, 2) })) return false;
                Thread.Sleep(100);
            }
            if (!FilenameFocused(dialog, thread, modernFileName)) return false;
            var input = new List<Input> { Key(0x11), Key(0x41), Key(0x41, 2), Key(0x11, 2) };
            foreach (char c in path) { input.Add(Key(0, 4, c)); input.Add(Key(0, 6, c)); }
            if (!Send(dialog, input.ToArray())) return false;
            Thread.Sleep(400);
            return !enter || Send(dialog, new[] { Key(0x0D), Key(0x0D, 2) });
        }
        return false;
    }

    public static IntPtr VisibleDialog(uint process, string title)
    {
        IntPtr dialog = IntPtr.Zero;
        while ((dialog = FindWindowEx(IntPtr.Zero, dialog, "#32770", title)) != IntPtr.Zero)
        {
            uint pid;
            GetWindowThreadProcessId(dialog, out pid);
            if (pid == process && IsWindowVisible(dialog)) return dialog;
        }
        return IntPtr.Zero;
    }

    public static bool ClickSave(uint process, string title, int x, int y)
    {
        var dialog = VisibleDialog(process, title);
        if (dialog == IntPtr.Zero || GetForegroundWindow() != dialog) return false;
        var hit = WindowFromPoint(new Point { x = x, y = y });
        var cls = new StringBuilder(32);
        GetClassName(hit, cls, cls.Capacity);
        if (GetAncestor(hit, 2) != dialog || cls.ToString() != "Button" || GetDlgCtrlID(hit) != 1) return false;
        Point previous;
        if (!GetCursorPos(out previous) || !SetCursorPos(x, y)) return false;
        try
        {
            return Send(dialog, new[] { Mouse(2), Mouse(4) });
        }
        finally { Thread.Sleep(60); SetCursorPos(previous.x, previous.y); }
    }

    private static Input Mouse(uint flags) =>
        new Input { type = 0, data = new InputData { mouse = new MouseInput { flags = flags } } };

    private static bool FilenameFocused(IntPtr dialog, uint thread, bool modernFileName)
    {
        var info = new GuiInfo { size = (uint)Marshal.SizeOf<GuiInfo>() };
        LastCheck = "waiting for keyboard focus inside the dialog";
        if (!GetGUIThreadInfo(thread, ref info) || GetAncestor(info.focus, 2) != dialog)
            return false;
        var type = new StringBuilder(32);
        GetClassName(info.focus, type, type.Capacity);
        LastCheck = "focused control " + type + ", id " + GetDlgCtrlID(info.focus);
        if (type.ToString() != "Edit") return false;
        if (modernFileName && GetDlgCtrlID(info.focus) == 1001) return true;
        for (IntPtr control = info.focus; control != IntPtr.Zero && control != dialog; control = GetParent(control))
            if (GetDlgCtrlID(control) == 1148) return true;
        return false;
    }

    private static Input Key(ushort key, uint flags = 0, ushort scan = 0) =>
        new Input { type = 1, data = new InputData { keyboard = new KeyboardInput { key = key, flags = flags, scan = scan } } };

    private static bool Send(IntPtr dialog, Input[] input) => GetForegroundWindow() == dialog &&
        SendInput((uint)input.Length, input, Marshal.SizeOf<Input>()) == input.Length;
}
