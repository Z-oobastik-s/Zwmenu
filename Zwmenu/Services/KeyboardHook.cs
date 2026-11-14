using System.Runtime.InteropServices;

namespace Zwmenu.Services;

/// <summary>Low-level WH_KEYBOARD_LL: Esc, Ctrl для конвертации файлов, клавиша снимка.</summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int VkEscape = 0x1B;
    private const int VkLControl = 0xA2;
    private const int VkRControl = 0xA3;
    private const int VkControl = 0x11;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public int VkCode;
        public int ScanCode;
        public int Flags;
        public int Time;
        public UIntPtr DwExtraInfo;
    }

    private readonly LowLevelKeyboardProc _proc;
    private readonly HashSet<int> _swallowed = new();
    private IntPtr _hookId = IntPtr.Zero;
    private bool _disposed;
    private bool _ctrlDown;

    public Action? EscapePressed { get; set; }

    public bool IsCtrlDown => _ctrlDown;

    /// <summary>true — клавиша съедается и не уходит в активное окно. Только key down.</summary>
    public Func<int, bool>? SwallowKeyDown { get; set; }

    /// <summary>Отпускание клавиши, которую мы сами съели. Повтор удержания так не считается новым нажатием.</summary>
    public event Action<int>? SwallowedKeyUp;

    /// <summary>Ctrl нажат (без других клавиш пока). Can fire repeatedly? only transition.</summary>
    public event EventHandler? CtrlAlonePressed;

    /// <summary>Ctrl отпущен.</summary>
    public event EventHandler? CtrlReleased;

    /// <summary>Любая другая клавиша при зажатом Ctrl (Ctrl+C и т.п.) — отмена жеста конвертации.</summary>
    public event EventHandler? CtrlChordKey;

    public KeyboardHook()
    {
        _proc = HookCallback;
    }

    public void Start()
    {
        if (_hookId != IntPtr.Zero)
            return;

        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule
            ?? throw new InvalidOperationException("Не удалось определить модуль процесса для хука клавиатуры.");

        _hookId = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(curModule.ModuleName), 0);
        if (_hookId == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx (клавиатура) не удалось");
    }

    public void Stop()
    {
        if (_hookId == IntPtr.Zero)
            return;
        UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
        _ctrlDown = false;
        _swallowed.Clear();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            int vk = data.VkCode;
            bool isDown = msg is WmKeyDown or WmSysKeyDown;
            bool isUp = msg is WmKeyUp or WmSysKeyUp;

            try
            {
                if (vk is VkControl or VkLControl or VkRControl)
                {
                    if (isDown && !_ctrlDown)
                    {
                        _ctrlDown = true;
                        CtrlAlonePressed?.Invoke(this, EventArgs.Empty);
                    }
                    else if (isUp && _ctrlDown)
                    {
                        _ctrlDown = false;
                        CtrlReleased?.Invoke(this, EventArgs.Empty);
                    }
                }
                else if (isDown)
                {
                    if (SwallowKeyDown?.Invoke(vk) == true)
                    {
                        _swallowed.Add(vk);
                        return (IntPtr)1;
                    }

                    _swallowed.Remove(vk);
                    if (vk == VkEscape)
                        EscapePressed?.Invoke();
                    if (_ctrlDown)
                        CtrlChordKey?.Invoke(this, EventArgs.Empty);
                }
                else if (isUp && _swallowed.Remove(vk))
                {
                    SwallowedKeyUp?.Invoke(vk);
                    return (IntPtr)1;
                }
            }
            catch
            {
                // Исключение из WH_KEYBOARD_LL валит процесс.
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }
}
