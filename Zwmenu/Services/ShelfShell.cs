using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Zwmenu.Services;

/// <summary>
/// «Этот компьютер», «Корзина» и другие значки стола не лежат файлами.
/// Их прячем реестром и держим на полке как ярлыки оболочки.
/// </summary>
static class ShelfShell
{
    public const string IdListFormat = "Shell IDList Array";

    private const uint SigdnNormal = 0;
    private const uint SigdnDesktopParsing = 0x80028000;
    private const uint SigdnFileSysPath = 0x80058000;
    private const uint ShcneUpdateDir = 0x00001000;
    private const uint ShcnfIdList = 0x0000;
    private const uint ShcnfFlush = 0x1000;
    private const int WmKeyDown = 0x0100;
    private const int VkF5 = 0x74;

    private static readonly string[] HideKeys =
    [
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\ClassicStartMenu"
    ];

    private static readonly Guid DesktopFolder = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");

    public readonly record struct Hit(string? FilePath, string? Parsing, string Name, string? Clsid);

    public static List<Hit> Parse(byte[] bytes)
    {
        var list = new List<Hit>();
        if (bytes.Length < 8)
            return list;
        int count = BitConverter.ToInt32(bytes, 0);
        if (count <= 0 || count > 64)
            return list;
        for (int i = 1; i <= count; i++)
        {
            int slot = 4 + i * 4;
            if (slot + 4 > bytes.Length)
                break;
            int offset = BitConverter.ToInt32(bytes, slot);
            if (offset <= 0 || offset >= bytes.Length)
                continue;
            IntPtr pidl = CopyPidl(bytes, offset);
            if (pidl == IntPtr.Zero)
                continue;
            try
            {
                string? parsing = NameOf(pidl, SigdnDesktopParsing);
                string? display = NameOf(pidl, SigdnNormal);
                string? file = NameOf(pidl, SigdnFileSysPath);
                string? clsid = ClsidOf(parsing);
                if (string.IsNullOrWhiteSpace(display))
                    display = clsid ?? "Значок";
                list.Add(new Hit(string.IsNullOrWhiteSpace(file) ? null : file, parsing, display!, clsid));
            }
            finally
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }

        return list;
    }

    public const string RecycleClsid = "{645FF040-5081-101B-9F08-00AA002F954E}";
    public const string ComputerClsid = "{20D04FE0-3AEA-1069-A2D8-08002B30309D}";

    public static Hit FromParsing(string parsing)
    {
        parsing = (parsing ?? "").Trim();
        string? clsid = ClsidOf(parsing);
        string name = NameOfClsid(clsid);
        return new Hit(null, clsid == null ? parsing : "::" + clsid, name, clsid);
    }

    public static string NameOfClsid(string? clsid)
    {
        if (string.Equals(clsid, RecycleClsid, StringComparison.OrdinalIgnoreCase))
            return "Корзина";
        if (string.Equals(clsid, ComputerClsid, StringComparison.OrdinalIgnoreCase))
            return "Этот компьютер";
        return "Значок";
    }

    public static bool IsShelfGhost(string? clsid)
        => string.Equals(clsid, RecycleClsid, StringComparison.OrdinalIgnoreCase)
            || string.Equals(clsid, ComputerClsid, StringComparison.OrdinalIgnoreCase);

    public static bool IsDesktopSpecial(Hit hit)
    {
        if (string.IsNullOrWhiteSpace(hit.Clsid))
            return false;
        if (!string.IsNullOrWhiteSpace(hit.FilePath)
            && !hit.FilePath.StartsWith("::", StringComparison.Ordinal))
            return false;
        if (IsShelfGhost(hit.Clsid))
            return true;
        string parsing = (hit.Parsing ?? "").Trim().TrimEnd('\\');
        if (parsing.Length == 0)
            return false;
        int markers = 0;
        for (int i = 0; i + 1 < parsing.Length; i++)
        {
            if (parsing[i] == ':' && parsing[i + 1] == ':')
                markers++;
        }

        return markers is 1 or 2
            && parsing.Contains("::" + hit.Clsid, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsUnsafeToMove(string path)
    {
        try
        {
            string full = Path.GetFullPath(path).TrimEnd('\\', '/');
            if (full.Length <= 3)
                return true;
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
            if (full.Equals(profile, StringComparison.OrdinalIgnoreCase))
                return true;
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
            if (full.Equals(windows, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch
        {
            return true;
        }

        return false;
    }

    public static bool Hide(string clsid)
    {
        bool changed = false;
        foreach (string path in HideKeys)
        {
            using var key = Registry.CurrentUser.CreateSubKey(path);
            if (key == null)
                continue;
            if (key.GetValue(clsid) is int value && value != 0)
                continue;
            key.SetValue(clsid, 1, RegistryValueKind.DWord);
            changed = true;
        }

        if (changed)
            NudgeDesktop();
        return true;
    }

    public static void Show(string clsid)
    {
        foreach (string path in HideKeys)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(path);
                key?.SetValue(clsid, 0, RegistryValueKind.DWord);
            }
            catch
            {
                // второй ключ всё равно попробуем
            }
        }

        NudgeDesktop();
    }

    private static string? ClsidOf(string? parsing)
    {
        if (string.IsNullOrWhiteSpace(parsing))
            return null;
        int start = parsing.LastIndexOf("::{", StringComparison.Ordinal);
        if (start < 0)
            return null;
        int end = parsing.IndexOf('}', start);
        if (end < 0)
            return null;
        string body = parsing.Substring(start + 2, end - start - 1);
        return Guid.TryParse(body, out Guid id) ? id.ToString("B").ToUpperInvariant() : null;
    }

    private static string? NameOf(IntPtr pidl, uint sigdn)
    {
        if (SHGetNameFromIDList(pidl, sigdn, out IntPtr raw) != 0 || raw == IntPtr.Zero)
            return null;
        try
        {
            return Marshal.PtrToStringUni(raw);
        }
        finally
        {
            Marshal.FreeCoTaskMem(raw);
        }
    }

    private static IntPtr CopyPidl(byte[] bytes, int offset)
    {
        int size = 0;
        int cursor = offset;
        while (cursor + 2 <= bytes.Length)
        {
            int cb = BitConverter.ToUInt16(bytes, cursor);
            if (cb == 0)
            {
                size += 2;
                break;
            }

            if (cb < 2 || cursor + cb > bytes.Length)
                return IntPtr.Zero;
            size += cb;
            cursor += cb;
        }

        if (size < 4)
            return IntPtr.Zero;
        IntPtr mem = Marshal.AllocCoTaskMem(size);
        Marshal.Copy(bytes, offset, mem, size);
        return mem;
    }

    private static void NudgeDesktop()
    {
        IntPtr desktop = IntPtr.Zero;
        try
        {
            Guid folder = DesktopFolder;
            if (SHGetKnownFolderIDList(ref folder, 0, IntPtr.Zero, out desktop) == 0 && desktop != IntPtr.Zero)
                SHChangeNotify(ShcneUpdateDir, ShcnfIdList | ShcnfFlush, desktop, IntPtr.Zero);
        }
        catch
        {
            // ниже всё равно толкнём список
        }
        finally
        {
            if (desktop != IntPtr.Zero)
                ILFree(desktop);
        }

        IntPtr list = FindDesktopList();
        if (list != IntPtr.Zero)
            PostMessage(list, WmKeyDown, (IntPtr)VkF5, IntPtr.Zero);
    }

    private static IntPtr FindDesktopList()
    {
        IntPtr progman = FindWindow("Progman", null);
        IntPtr def = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (def == IntPtr.Zero)
        {
            IntPtr worker = IntPtr.Zero;
            while ((worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
            {
                def = FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (def != IntPtr.Zero)
                    break;
            }
        }

        return def == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(def, IntPtr.Zero, "SysListView32", null);
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetNameFromIDList(IntPtr pidl, uint sigdn, out IntPtr name);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderIDList(ref Guid id, uint flags, IntPtr token, out IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string cls, string? title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
