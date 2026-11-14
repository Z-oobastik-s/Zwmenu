using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Zwmenu.Services;

/// <summary>
/// На переднем плане игра: боковая кнопка и клавиша диктовки не включают микрофон.
/// Узнаём CS, Dota, Warhammer по имени, остальные игры по полноэкранному окну
/// и по списку, который Windows уже ведёт для игровой панели.
/// </summary>
static class GameFocus
{
    private const int GwlStyle = -16;
    private const int WsCaption = 0x00C00000;
    private const uint ProcessQueryLimited = 0x1000;
    private const uint MonitorDefaultToNearest = 2;

    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "zwmenu", "searchhost", "startmenuexperiencehost", "shellexperiencehost",
        "applicationframehost", "systemsettings", "textinputhost", "lockapp",
        "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "browser",
        "discord", "telegram", "slack", "teams", "zoom", "steam", "steamwebhelper",
        "cursor", "code", "devenv", "windowsterminal", "wt", "notepad",
        "vlc", "mpc-hc", "mpc-hc64", "potplayer", "potplayer64", "mpv",
        "obs", "obs64", "powerpnt", "winword", "excel", "acrord32"
    };

    private static HashSet<string> _catalog = new(StringComparer.OrdinalIgnoreCase);
    private static int _warm;
    private static IntPtr _hwnd;
    private static long _until;
    private static bool _playing;

    public static void Warm()
    {
        if (Interlocked.Exchange(ref _warm, 1) == 1)
            return;
        var thread = new Thread(CatalogLoop)
        {
            IsBackground = true,
            Name = "ZwmenuGameFocus"
        };
        thread.Start();
    }

    /// <summary>Вызов с крючка мыши: только пара системных запросов, без реестра.</summary>
    public static bool IsPlaying()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            long now = Environment.TickCount64;
            if (hwnd == _hwnd && now < _until)
                return _playing;
            _hwnd = hwnd;
            _until = now + 350;
            _playing = Resolve(hwnd);
            return _playing;
        }
        catch
        {
            return false;
        }
    }

    private static bool Resolve(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return false;
        string name = ExeName(hwnd);
        if (name.Length == 0)
            return CoversScreen(hwnd) && !HasCaption(hwnd);
        if (Skip.Contains(name))
            return false;
        if (KnownGame(name) || _catalog.Contains(name))
            return true;
        return CoversScreen(hwnd) && !HasCaption(hwnd);
    }

    private static bool KnownGame(string name)
    {
        if (name.Equals("cs2", StringComparison.OrdinalIgnoreCase)
            || name.Equals("csgo", StringComparison.OrdinalIgnoreCase)
            || name.Equals("dota2", StringComparison.OrdinalIgnoreCase)
            || name.Equals("darktide", StringComparison.OrdinalIgnoreCase)
            || name.Equals("vermintide", StringComparison.OrdinalIgnoreCase)
            || name.Equals("vermintide2", StringComparison.OrdinalIgnoreCase))
            return true;
        return name.Contains("warhammer", StringComparison.OrdinalIgnoreCase)
            || name.Contains("spacemarine", StringComparison.OrdinalIgnoreCase)
            || name.Contains("dawnofwar", StringComparison.OrdinalIgnoreCase)
            || name.Contains("wh40k", StringComparison.OrdinalIgnoreCase);
    }

    private static void CatalogLoop()
    {
        while (true)
        {
            try { RefreshCatalog(); }
            catch { /* реестр занят, список игр остаётся прежним */ }
            Thread.Sleep(60_000);
        }
    }

    private static void RefreshCatalog()
    {
        var next = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var root = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
        if (root != null)
        {
            foreach (string childName in root.GetSubKeyNames())
            {
                try
                {
                    using var child = root.OpenSubKey(childName);
                    if (child == null)
                        continue;
                    string? path = ExeFromKey(child);
                    if (string.IsNullOrWhiteSpace(path))
                        continue;
                    string file = Path.GetFileNameWithoutExtension(path);
                    if (file.Length == 0 || Skip.Contains(file))
                        continue;
                    next.Add(file);
                }
                catch
                {
                    // битая запись не портит остальные
                }
            }
        }

        _catalog = next;
    }

    private static string? ExeFromKey(RegistryKey key)
    {
        if (key.GetValue("MatchedExeFullPath") is byte[] bytes && bytes.Length >= 4)
        {
            string text = Encoding.Unicode.GetString(bytes).Trim('\0', ' ', '\uFEFF');
            int cut = text.IndexOf('\0');
            if (cut >= 0)
                text = text[..cut];
            if (text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return text;
        }

        foreach (string valueName in key.GetValueNames())
        {
            if (key.GetValue(valueName) is string text
                && text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && text.Contains('\\'))
                return text;
        }

        return null;
    }

    private static string ExeName(IntPtr hwnd)
    {
        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0)
            return "";
        IntPtr process = OpenProcess(ProcessQueryLimited, false, pid);
        if (process == IntPtr.Zero)
            return "";
        try
        {
            var buffer = new StringBuilder(520);
            int size = buffer.Capacity;
            if (!QueryFullProcessImageName(process, 0, buffer, ref size))
                return "";
            return Path.GetFileNameWithoutExtension(buffer.ToString());
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static bool CoversScreen(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var window))
            return false;
        IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
            return false;
        var screen = info.Monitor;
        return window.Left <= screen.Left + 2
            && window.Top <= screen.Top + 2
            && window.Right >= screen.Right - 2
            && window.Bottom >= screen.Bottom - 2;
    }

    private static bool HasCaption(IntPtr hwnd)
    {
        int style = (int)GetWindowLongPtr(hwnd, GwlStyle);
        return (style & WsCaption) == WsCaption;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }
}
