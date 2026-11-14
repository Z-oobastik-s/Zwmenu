using System.Diagnostics;

namespace Zwmenu.Services;

public sealed record ProcessEntry(int Pid, string Name, string Title, long MemoryBytes, IReadOnlyList<int> AllPids);

/// <summary>Список процессов для быстрого завершения с колеса.</summary>
public static class ProcessKillService
{
    private static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle", "System", "Registry", "smss", "csrss", "wininit", "services", "lsass",
        "svchost", "fontdrvhost", "dwm", "winlogon", "Memory Compression", "Secure System",
        "Zwmenu"
    };

    /// <summary>Шум Windows: не критичны, но забивают список вместо утилит вроде ShareMouse/Hamachi.</summary>
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationFrameHost", "StartMenuExperienceHost", "SearchHost", "SearchApp", "SearchIndexer",
        "TextInputHost", "ShellExperienceHost", "RuntimeBroker", "backgroundTaskHost", "BackgroundTransferHost",
        "SystemSettings", "SystemSettingsBroker", "UserOOBEBroker", "LockApp", "LogonUI",
        "sihost", "taskhostw", "ctfmon", "conhost", "dllhost", "WmiPrvSE", "WmiApSrv",
        "CompPkgSrv", "CrossDeviceService", "CrossDeviceResume", "PhoneExperienceHost",
        "SecurityHealthSystray", "SecurityHealthService", "MsMpEng", "NisSrv",
        "MicrosoftEdgeUpdate", "edgeupdate", "edgeupdatem", "Widgets", "WidgetService",
        "GameBar", "GameBarFTServer", "GameInputSvc", "Video.UI", "Music.UI",
        "ShellHost", "PickerHost", "OpenWith", "FileCoAuth", "FileSyncHelper",
        "smartscreen", "SgrmBroker", "AggregatorHost",
        "ChsIME", "spoolsv", "PrintIsolationHost",
        "OfficeClickToRun", "AppVShNotify",
        "RtkAudUService64", "AUDIODG", "jhi_service"
    };

    private static IReadOnlyList<ProcessEntry>? _snapshotCache;
    private static DateTime _snapshotAt = DateTime.MinValue;
    private static int _snapshotMax;

    /// <summary>Кэш для мгновенного наведения. null, если старше maxAge.</summary>
    public static IReadOnlyList<ProcessEntry>? PeekFresh(int max, double maxAgeSeconds)
    {
        if (_snapshotCache == null)
            return null;
        if ((DateTime.UtcNow - _snapshotAt).TotalSeconds > maxAgeSeconds)
            return null;
        return _snapshotMax == max ? _snapshotCache : _snapshotCache.Take(max).ToList();
    }

    public static IReadOnlyList<ProcessEntry> Snapshot(int max = 42)
    {
        // Короткий кэш: повторное наведение на «Процессы» не гоняет GetProcesses снова.
        if (_snapshotCache != null
            && _snapshotMax >= max
            && (DateTime.UtcNow - _snapshotAt).TotalSeconds < 1.5)
            return _snapshotMax == max ? _snapshotCache : _snapshotCache.Take(max).ToList();

        int mySession = Process.GetCurrentProcess().SessionId;
        var groups = new Dictionary<string, Acc>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string name = p.ProcessName;
                if (Critical.Contains(name) || Noise.Contains(name))
                    continue;

                string title = "";
                try { title = p.MainWindowTitle?.Trim() ?? ""; }
                catch { /* нет доступа */ }

                long mem = 0;
                try { mem = p.WorkingSet64; }
                catch { /* ignore */ }

                bool sameSession = false;
                try { sameSession = p.SessionId == mySession; }
                catch { /* ignore */ }

                if (!groups.TryGetValue(name, out var acc))
                {
                    acc = new Acc(name);
                    groups[name] = acc;
                }

                acc.Pids.Add(p.Id);
                acc.Memory += mem;
                if (title.Length > 0 && (acc.Title.Length == 0 || title.Length > acc.Title.Length))
                    acc.Title = title;
                if (sameSession) acc.SameSession = true;
            }
            catch
            {
                // процесс исчез
            }
            finally
            {
                try { p.Dispose(); } catch { /* ignore */ }
            }
        }

        var result = groups.Values
            .OrderByDescending(Score)
            .Take(max)
            .Select(a => new ProcessEntry(a.Pids[0], a.Name, a.Title, a.Memory, a.Pids.ToList()))
            .ToList();
        _snapshotCache = result;
        _snapshotAt = DateTime.UtcNow;
        _snapshotMax = max;
        return result;
    }

    private static long Score(Acc a)
    {
        // Окно / путь вне Windows / своя сессия важнее голой памяти:
        // иначе Chrome забивает мелкие утилиты вроде ShareMouse.
        long score = a.Memory / 1024;
        if (a.Title.Length > 0) score += 2_000_000L;
        if (a.SameSession) score += 400_000L;
        return score;
    }

    public static string TryKill(int pid) => TryKillMany(new[] { pid });

    public static string TryKillMany(IReadOnlyList<int> pids)
    {
        if (pids == null || pids.Count == 0)
            return "Нечего завершать.";

        var errors = new List<string>();
        int killed = 0;

        foreach (int pid in pids.Distinct())
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                string name = p.ProcessName;
                if (Critical.Contains(name))
                {
                    errors.Add($"«{name}» системный: лучше не трогать.");
                    continue;
                }

                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero && p.CloseMainWindow())
                    {
                        if (p.WaitForExit(600))
                        {
                            killed++;
                            continue;
                        }
                    }
                }
                catch { /* fallback Kill */ }

                p.Kill(entireProcessTree: true);
                if (!p.WaitForExit(2000))
                {
                    errors.Add($"«{name}» (PID {pid}): не подтвердил выход.");
                    continue;
                }

                killed++;
            }
            catch (ArgumentException)
            {
                killed++; // уже нет
            }
            catch (Exception ex)
            {
                errors.Add($"PID {pid}: {Short(ex.Message)}");
            }
        }

        var stillAlive = new List<int>();
        foreach (int pid in pids.Distinct())
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.HasExited)
                    stillAlive.Add(pid);
            }
            catch (ArgumentException)
            {
                // умер
            }
        }

        if (stillAlive.Count == 0)
            return errors.Count == 0 || killed > 0 ? "" : string.Join(" ", errors);

        string alive = "Ещё живы PID: " + string.Join(", ", stillAlive)
                       + ". Часто так у процессов от администратора или если программа сама себя поднимает.";
        if (errors.Count > 0)
            alive += " " + string.Join(" ", errors);
        return alive;
    }

    public static string FormatLabel(ProcessEntry e)
    {
        string mem = e.MemoryBytes >= 1024L * 1024 * 1024
            ? $"{e.MemoryBytes / (1024.0 * 1024 * 1024):0.#} ГБ"
            : $"{e.MemoryBytes / (1024.0 * 1024):0} МБ";
        int n = e.AllPids?.Count ?? 1;
        return n > 1 ? $"{e.Name}: {mem} ×{n}" : $"{e.Name}: {mem}";
    }

    /// <summary>Убить текущие explorer.exe (если есть) и запустить снова: и перезапуск, и подъём после убийства.</summary>
    public static string TryRestartExplorer()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(2500);
                }
                catch
                {
                    // уже умер или нет прав: всё равно попробуем старт
                }
                finally
                {
                    try { p.Dispose(); } catch { /* ignore */ }
                }
            }

            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true })?.Dispose();
            return "";
        }
        catch (Exception ex)
        {
            return "Не удалось перезапустить проводник: " + Short(ex.Message);
        }
    }

    private static string Short(string msg)
    {
        if (string.IsNullOrWhiteSpace(msg)) return "ошибка";
        msg = msg.Trim();
        return msg.Length <= 120 ? msg : msg[..117] + "...";
    }

    private sealed class Acc
    {
        public Acc(string name) => Name = name;
        public string Name { get; }
        public string Title { get; set; } = "";
        public long Memory { get; set; }
        public bool SameSession { get; set; }
        public bool UserPath { get; set; }
        public List<int> Pids { get; } = new();
    }
}
