using System.Runtime.InteropServices;

namespace Zwmenu.Services;

/// <summary>Раскладка клавиатуры активного окна. Её и синхронизируем со вторым ноутбуком.</summary>
static class EdgeLayout
{
    private const int WmInputLangChangeRequest = 0x0050;
    private const uint KlfActivate = 0x00000001;
    private const uint KlfSubstituteOk = 0x00000002;
    private const int SmtoAbortIfHung = 0x0002;

    public static uint Foreground()
    {
        IntPtr window = GetForegroundWindow();
        uint thread = 0;
        if (window != IntPtr.Zero)
            thread = GetWindowThreadProcessId(window, out _);
        IntPtr hkl = GetKeyboardLayout(thread);
        return unchecked((uint)hkl.ToInt64());
    }

    /// <summary>Русский и английский совпадают по языку, даже если полный код раскладки на двух ноутбуках разный.</summary>
    public static bool Same(uint a, uint b) => a != 0 && b != 0 && (a & 0xFFFF) == (b & 0xFFFF);

    public static bool Apply(uint hkl)
    {
        if (hkl == 0 || Same(Foreground(), hkl))
            return true;

        string klid = (hkl & 0xFFFF).ToString("X8");
        IntPtr loaded = LoadKeyboardLayout(klid, KlfActivate | KlfSubstituteOk);
        if (loaded == IntPtr.Zero)
            return false;

        IntPtr target = FocusOfForeground();
        if (target == IntPtr.Zero)
            target = GetForegroundWindow();
        if (target == IntPtr.Zero)
            return false;

        IntPtr sent = SendMessageTimeout(
            target, WmInputLangChangeRequest, IntPtr.Zero, loaded, SmtoAbortIfHung, 200, out _);
        if (sent == IntPtr.Zero)
            PostMessage(target, WmInputLangChangeRequest, IntPtr.Zero, loaded);
        return true;
    }

    private static IntPtr FocusOfForeground()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero)
            return IntPtr.Zero;
        uint thread = GetWindowThreadProcessId(window, out _);
        var info = new GuiThreadInfo { CbSize = Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(thread, ref info) || info.HwndFocus == IntPtr.Zero)
            return window;
        return info.HwndFocus;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadKeyboardLayout(string pwszKLID, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, int flags, int timeout, out IntPtr result);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GuiThreadInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int CbSize;
        public int Flags;
        public IntPtr HwndActive;
        public IntPtr HwndFocus;
        public IntPtr HwndCapture;
        public IntPtr HwndMenuOwner;
        public IntPtr HwndMoveSize;
        public IntPtr HwndCaret;
        public RECT Caret;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }
}
