using System.Runtime.InteropServices;

namespace Zwmenu.Services;

/// <summary>Окно, которое было на экране в момент открытия колеса.</summary>
public static class ShellHere
{
    static IntPtr _window;

    public static void Note()
    {
        IntPtr now = GetForegroundWindow();
        if (now == IntPtr.Zero)
            return;
        GetWindowThreadProcessId(now, out uint pid);
        if (pid == (uint)Environment.ProcessId)
            return;
        _window = now;
    }

    public static string? Folder()
    {
        IntPtr window = _window;
        if (window == IntPtr.Zero)
            window = GetForegroundWindow();
        return ShellSelectionService.TryCurrentFolder(window);
    }

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
