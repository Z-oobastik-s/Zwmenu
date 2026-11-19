using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Zwmenu.Services;

/// <summary>Вытаскивает окно поверх того, что уже открыто. Иначе Windows только мигает значком.</summary>
static class WindowFront
{
    const int SwRestore = 9;
    const int SwShow = 5;
    const uint SwpNoMove = 0x0002;
    const uint SwpNoSize = 0x0001;
    const uint SwpShowWindow = 0x0040;

    public static void AllowOthers() => AllowSetForegroundWindow(-1);

    public static void Bring(Window window)
    {
        if (!window.Dispatcher.CheckAccess())
        {
            window.Dispatcher.BeginInvoke(() => Bring(window));
            return;
        }

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        if (!window.IsVisible)
            window.Show();

        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        IntPtr fore = GetForegroundWindow();
        uint foreThread = GetWindowThreadProcessId(fore, out _);
        uint appThread = GetCurrentThreadId();
        bool attached = false;
        if (foreThread != 0 && foreThread != appThread)
            attached = AttachThreadInput(foreThread, appThread, true);

        ShowWindow(hwnd, window.WindowState == WindowState.Minimized ? SwRestore : SwShow);
        SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
        SetWindowPos(hwnd, new IntPtr(-2), 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
        SetForegroundWindow(hwnd);
        if (attached)
            AttachThreadInput(foreThread, appThread, false);
        window.Activate();
        window.Focus();
    }

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();
}
