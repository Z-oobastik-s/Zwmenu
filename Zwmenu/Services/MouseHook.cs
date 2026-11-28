using System.Runtime.InteropServices;

namespace Zwmenu.Services;

/// <summary>
/// Режим средней кнопки:
/// Hold — короткий клик как в Windows, удержание открывает колесо;
/// Instant — СКМ сразу колесо;
/// Alt — колесо только при Alt+СКМ.
/// </summary>
public enum MiddleButtonMode
{
    Hold,
    Instant,
    Alt
}

/// <summary>Low-level WH_MOUSE_LL: средняя кнопка + клики для закрытия колеса.</summary>
public sealed class MouseHook : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const int WmMouseMove = 0x0200;
    private const uint LlmhfInjected = 0x00000001;
    private const int InputMouse = 0;
    private const uint MouseeventfMiddleDown = 0x0020;
    private const uint MouseeventfMiddleUp = 0x0040;
    private const int VkMenu = 0x12;

    /// <summary>Метка наших SendInput: хук их пропускает, без петли.</summary>
    public static readonly UIntPtr InjectExtra = (UIntPtr)0x5A574D45; // ZWME

    /// <summary>СКМ со второго ноутбука. Это клик пользователя, колесо на этом экране открываем.</summary>
    public static readonly UIntPtr EdgeExtra = (UIntPtr)0x5A574D46; // ZWMF

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    public readonly struct ScreenPoint
    {
        public ScreenPoint(int x, int y) { X = x; Y = y; }
        public int X { get; }
        public int Y { get; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int Type;
        public MOUSEINPUT Mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint DwFlags;
        public uint Time;
        public UIntPtr DwExtraInfo;
    }

    private static readonly IntPtr Handled = (IntPtr)1;

    private readonly LowLevelMouseProc _proc;
    private IntPtr _hookId = IntPtr.Zero;
    private bool _disposed;
    /// <summary>Мы съели MMB down — значит up тоже глотаем.</summary>
    private bool _swallowArmed;
    private bool _xSwallowArmed;
    private byte _xSwallowWhich;

    public event EventHandler<ScreenPoint>? MiddleDown;
    public event EventHandler<ScreenPoint>? MiddleUp;
    public event EventHandler<XButtonEventArgs>? SideDown;
    public event EventHandler<XButtonEventArgs>? SideUp;
    public event EventHandler<ScreenPoint>? AnyButtonDown;
    public event EventHandler<ScreenPoint>? MouseMoved;

    /// <summary>Какую боковую кнопку глотать: 0 — никакую, 1 — назад, 2 — вперёд.</summary>
    public byte CaptureSideButton { get; set; }

    public bool TrackMovement { get; set; }
    public bool ReportOtherButtons { get; set; }
    public MiddleButtonMode Mode { get; set; } = MiddleButtonMode.Hold;
    public long LastCallbackTick { get; private set; }

    public MouseHook() => _proc = HookCallback;

    public void Start()
    {
        if (_hookId != IntPtr.Zero)
            return;

        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule
            ?? throw new InvalidOperationException("Не удалось определить модуль процесса для хука мыши.");

        _hookId = SetWindowsHookEx(WhMouseLl, _proc, GetModuleHandle(curModule.ModuleName), 0);
        if (_hookId == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx (мышь) не удалось");
    }

    public void Stop()
    {
        if (_hookId == IntPtr.Zero)
            return;
        UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
        _swallowArmed = false;
        _xSwallowArmed = false;
        _xSwallowWhich = 0;
    }

    /// <summary>Отдать системе короткий СКМ после перехвата (вкладка в браузере и т.п.).</summary>
    public static void SendMiddleClickPassthrough()
    {
        var inputs = new INPUT[2];
        inputs[0] = new INPUT
        {
            Type = InputMouse,
            Mi = new MOUSEINPUT { DwFlags = MouseeventfMiddleDown, DwExtraInfo = InjectExtra }
        };
        inputs[1] = new INPUT
        {
            Type = InputMouse,
            Mi = new MOUSEINPUT { DwFlags = MouseeventfMiddleUp, DwExtraInfo = InjectExtra }
        };
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        LastCallbackTick = Environment.TickCount64;
        if (nCode < 0)
            return CallNextHookEx(_hookId, nCode, wParam, lParam);

        int msg = wParam.ToInt32();
        if (msg == WmMouseMove && !TrackMovement)
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        if ((msg == WmLButtonDown || msg == WmRButtonDown) && !ReportOtherButtons)
            return CallNextHookEx(_hookId, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<MsllHookStruct>(lParam);

        // Свой SendInput — всегда пропускаем (иначе петля).
        if (data.ExtraInfo == InjectExtra)
            return CallNextHookEx(_hookId, nCode, wParam, lParam);

        // Чужой injected СКМ пропускаем. Событие со второго ноутбука помечено EdgeExtra:
        // это живой клик, колесо открывается здесь.
        // Боковые X1/X2 у кучи мышей помечены injected — их из-за этого не глотаем.
        bool injected = (data.Flags & LlmhfInjected) != 0;
        if (injected && data.ExtraInfo != EdgeExtra && msg is WmMButtonDown or WmMButtonUp)
            return CallNextHookEx(_hookId, nCode, wParam, lParam);

        var pt = new ScreenPoint(data.Point.X, data.Point.Y);

        if (msg == WmMButtonDown)
        {
            if (Mode == MiddleButtonMode.Alt && !IsAltDown())
            {
                _swallowArmed = false;
                return CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            // В игре СКМ нужен самой игре: колесо не открываем.
            if (GameFocus.IsPlaying(fresh: true))
            {
                _swallowArmed = false;
                return CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            _swallowArmed = true;
            SafeInvoke(() => MiddleDown?.Invoke(this, pt));
            return Handled;
        }

        if (msg == WmMButtonUp)
        {
            if (!_swallowArmed)
                return CallNextHookEx(_hookId, nCode, wParam, lParam);

            _swallowArmed = false;
            SafeInvoke(() => MiddleUp?.Invoke(this, pt));
            return Handled;
        }

        if (msg == WmXButtonDown)
        {
            // В режиме назначения в настройках — не глотаем, пусть окно само увидит X1/X2.
            if (ScreenshotStore.SuspendDictationCapture)
                return CallNextHookEx(_hookId, nCode, wParam, lParam);

            byte which = XWhich(data.MouseData);
            if (which != 0 && which == CaptureSideButton)
            {
                // В игре кнопка нужна самой игре, микрофон не открываем.
                if (GameFocus.IsPlaying())
                    return CallNextHookEx(_hookId, nCode, wParam, lParam);

                _xSwallowArmed = true;
                _xSwallowWhich = which;
                SafeInvoke(() => SideDown?.Invoke(this, new XButtonEventArgs(which, pt)));
                return Handled;
            }
        }
        else if (msg == WmXButtonUp)
        {
            if (ScreenshotStore.SuspendDictationCapture)
                return CallNextHookEx(_hookId, nCode, wParam, lParam);

            byte which = XWhich(data.MouseData);
            if (_xSwallowArmed && which == _xSwallowWhich)
            {
                _xSwallowArmed = false;
                _xSwallowWhich = 0;
                SafeInvoke(() => SideUp?.Invoke(this, new XButtonEventArgs(which, pt)));
                return Handled;
            }
        }

        if (msg is WmLButtonDown or WmRButtonDown)
            SafeInvoke(() => AnyButtonDown?.Invoke(this, pt));
        else if (msg == WmMouseMove)
            SafeInvoke(() => MouseMoved?.Invoke(this, pt));

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static byte XWhich(uint mouseData)
    {
        // По MSDN кнопка в старшем слове; на части драйверов бывает в младшем.
        int hi = (int)((mouseData >> 16) & 0xFFFF);
        if (hi is 1 or 2)
            return (byte)hi;
        int lo = (int)(mouseData & 0xFFFF);
        return lo is 1 or 2 ? (byte)lo : (byte)0;
    }

    private static bool IsAltDown() => (GetAsyncKeyState(VkMenu) & 0x8000) != 0;

    /// <summary>Исключение из обработчика хука валит процесс (0xc0000005 / unexpected parameters).</summary>
    private static void SafeInvoke(Action action)
    {
        try { action(); }
        catch { /* never throw out of WH_*_LL */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }

    public sealed class XButtonEventArgs : EventArgs
    {
        public XButtonEventArgs(byte which, ScreenPoint point)
        {
            Which = which;
            Point = point;
        }

        /// <summary>1 — XButton1 (назад), 2 — XButton2 (вперёд).</summary>
        public byte Which { get; }
        public ScreenPoint Point { get; }
    }
}
