using System.Runtime.InteropServices;
using System.Text;

namespace Zwmenu.Services;

public sealed class FileHolder
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Line => Name + "   " + Pid + "   " + Kind;
}

/// <summary>Кто держит файл, через Restart Manager. Процесс не закрывается.</summary>
public static class FileLock
{
    public static (IReadOnlyList<FileHolder> Holders, string? Error) Who(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (Array.Empty<FileHolder>(), "Файл не выбран.");

        string full;
        try { full = Path.GetFullPath(path.Trim()); }
        catch { return (Array.Empty<FileHolder>(), "Такого файла нет."); }

        if (Directory.Exists(full))
            return (Array.Empty<FileHolder>(), "Это папка. Нужен файл.");
        if (!File.Exists(full))
            return (Array.Empty<FileHolder>(), "Такого файла нет.");

        uint session = 0;
        bool open = false;
        try
        {
            int started = RmStartSession(out session, 0, new StringBuilder(64));
            if (started != 0)
                return (Array.Empty<FileHolder>(), Fail(started));
            open = true;

            int registered = RmRegisterResources(session, 1, new[] { full }, 0, IntPtr.Zero, 0, IntPtr.Zero);
            if (registered != 0)
                return (Array.Empty<FileHolder>(), Fail(registered));

            RmProcessInfo[]? list = null;
            uint count = 0;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                uint needed = 0;
                count = (uint)(list?.Length ?? 0);
                uint reason = 0;
                int listed = RmGetList(session, out needed, ref count, list, ref reason);
                if (listed == 0)
                    return (Read(list, count), null);
                if (listed != 234)
                    return (Array.Empty<FileHolder>(), Fail(listed));
                if (needed == 0)
                    return (Array.Empty<FileHolder>(), null);
                list = new RmProcessInfo[needed];
            }

            return (Array.Empty<FileHolder>(), "Windows не успела ответить. Попробуй ещё раз.");
        }
        catch (Exception ex)
        {
            return (Array.Empty<FileHolder>(), "Не получилось посмотреть, кто держит файл: " + ex.Message);
        }
        finally
        {
            if (open)
                RmEndSession(session);
        }
    }

    public static void SelfCheck()
    {
        string dir = Path.Combine(Path.GetTempPath(), "zwmenu-hold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "занят.txt");
        try
        {
            File.WriteAllText(file, "x");
            var (free, freeError) = Who(file);
            if (freeError != null)
                throw new InvalidOperationException(freeError);
            if (free.Any(h => h.Pid == Environment.ProcessId))
                throw new InvalidOperationException("свободный файл уже числится за процессом");

            using (var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                _ = stream.Length;
                var (held, heldError) = Who(file);
                if (heldError != null)
                    throw new InvalidOperationException(heldError);
                if (!held.Any(h => h.Pid == Environment.ProcessId))
                    throw new InvalidOperationException("не увидел свой процесс: " + string.Join("; ", held.Select(h => h.Line)));
            }

            var (folder, folderError) = Who(dir);
            if (folderError == null)
                throw new InvalidOperationException("папка принята как файл");
            var (missing, missingError) = Who(Path.Combine(dir, "нет.txt"));
            if (missingError == null)
                throw new InvalidOperationException("пропавший файл принят");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* проверка закончена */ }
        }
    }

    static IReadOnlyList<FileHolder> Read(RmProcessInfo[]? list, uint count)
    {
        var found = new List<FileHolder>();
        if (list == null || count == 0)
            return found;
        int n = (int)Math.Min(count, (uint)list.Length);
        var seen = new HashSet<int>();
        for (int i = 0; i < n; i++)
        {
            int pid = list[i].Process.ProcessId;
            if (pid <= 0 || !seen.Add(pid))
                continue;
            string name = (list[i].AppName ?? "").Trim();
            string service = (list[i].Service ?? "").Trim();
            if (name.Length == 0)
                name = service.Length == 0 ? ProcessName(pid) : service;
            found.Add(new FileHolder
            {
                Pid = pid,
                Name = name,
                Kind = KindName(list[i].ApplicationType, service)
            });
        }

        found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return found;
    }

    public static string? Brief(IReadOnlyList<string> paths)
    {
        var parts = new List<string>();
        int files = 0;
        foreach (string path in paths)
        {
            if (!File.Exists(path))
                continue;
            if (files++ >= 6)
                break;
            var (holders, error) = Who(path);
            if (error != null)
                return Path.GetFileName(path) + ": " + error;
            bool many = paths.Count > 1;
            foreach (FileHolder holder in holders)
            {
                string name = many ? Path.GetFileName(path) + " " + holder.Name : holder.Name;
                if (!parts.Contains(name))
                    parts.Add(name);
            }
        }

        return parts.Count == 0 ? null : "Держит: " + string.Join(", ", parts);
    }

    static string ProcessName(int pid)
    {
        try { return System.Diagnostics.Process.GetProcessById(pid).ProcessName; }
        catch { return "процесс"; }
    }

    static string KindName(int type, string service) => type switch
    {
        1 or 2 => "окно",
        3 => service.Length == 0 ? "служба" : "служба " + service,
        4 => "проводник",
        5 => "консоль",
        1000 => "системное",
        _ => "программа"
    };

    static string Fail(int code) => code switch
    {
        5 => "Windows не дала посмотреть, кто держит файл.",
        121 => "Windows не успела ответить. Попробуй ещё раз.",
        _ => "Не получилось посмотреть, кто держит файл."
    };

    const int MaxAppName = 255;
    const int MaxService = 63;

    [StructLayout(LayoutKind.Sequential)]
    struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME StartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxAppName + 1)]
        public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxService + 1)]
        public string Service;
        public int ApplicationType;
        public uint AppStatus;
        public uint SessionId;
        public int Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmStartSession(out uint session, int flags, StringBuilder key);

    [DllImport("rstrtmgr.dll")]
    static extern int RmEndSession(uint session);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmRegisterResources(
        uint session,
        uint fileCount,
        string[] fileNames,
        uint appCount,
        IntPtr apps,
        uint serviceCount,
        IntPtr services);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmGetList(
        uint session,
        out uint needed,
        ref uint count,
        [In, Out] RmProcessInfo[]? list,
        ref uint rebootReasons);
}
