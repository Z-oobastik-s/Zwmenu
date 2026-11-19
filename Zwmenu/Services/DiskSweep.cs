namespace Zwmenu.Services;

/// <summary>
/// Старые установщики и обрывки загрузок. Настройки, снимки и модели не трогает.
/// </summary>
public static class DiskSweep
{
    public static void Soon() => _ = Task.Run(Run);

    public static void Run()
    {
        DropOldSetups(Path.GetTempPath(), keepNewest: true);
        DropLooseTemp(Path.GetTempPath(), DateTime.UtcNow.AddHours(-2));
        DropAppTemp(Path.Combine(Path.GetTempPath(), "Zwmenu"));
        DropToolLeftovers();
    }

    /// <summary>Перед новой загрузкой оставляем только тот установщик, который сейчас качаем.</summary>
    public static void KeepOnly(Version version) =>
        DropOldSetups(Path.GetTempPath(), keepNewest: false, keep: version);

    public static void SelfCheck()
    {
        string dir = Path.Combine(Path.GetTempPath(), "zwmenu-sweep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Zwmenu-Setup-1.0.0.exe"), "old");
            File.WriteAllText(Path.Combine(dir, "Zwmenu-Setup-1.0.1.exe"), "new");
            File.WriteAllText(Path.Combine(dir, "Zwmenu-Setup-1.0.0.exe.part"), "part");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "keep");
            DropOldSetups(dir, keepNewest: true);
            if (File.Exists(Path.Combine(dir, "Zwmenu-Setup-1.0.0.exe")))
                throw new InvalidOperationException("старый установщик остался");
            if (File.Exists(Path.Combine(dir, "Zwmenu-Setup-1.0.0.exe.part")))
                throw new InvalidOperationException("оборванная загрузка осталась");
            if (!File.Exists(Path.Combine(dir, "Zwmenu-Setup-1.0.1.exe")))
                throw new InvalidOperationException("новый установщик удалён");
            if (!File.Exists(Path.Combine(dir, "notes.txt")))
                throw new InvalidOperationException("чужой файл удалён");

            File.WriteAllText(Path.Combine(dir, "Zwmenu-Setup-9.0.0.exe"), "later");
            DropOldSetups(dir, keepNewest: false, keep: new Version(1, 0, 1));
            if (File.Exists(Path.Combine(dir, "Zwmenu-Setup-9.0.0.exe")))
                throw new InvalidOperationException("чужая версия осталась");
            if (!File.Exists(Path.Combine(dir, "Zwmenu-Setup-1.0.1.exe")))
                throw new InvalidOperationException("нужная версия удалена");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* проверка уже кончилась */ }
        }
    }

    static void DropOldSetups(string dir, bool keepNewest, Version? keep = null)
    {
        string[] files;
        try
        {
            files = Directory.Exists(dir) ? Directory.GetFiles(dir, "Zwmenu-Setup-*") : Array.Empty<string>();
        }
        catch
        {
            return;
        }

        Version? newest = keep;
        if (keepNewest)
        {
            foreach (string file in files)
            {
                if (!TrySetupVersion(Path.GetFileName(file), out var version, out bool partial) || partial)
                    continue;
                if (newest == null || version > newest)
                    newest = version;
            }
        }

        foreach (string file in files)
        {
            if (!TrySetupVersion(Path.GetFileName(file), out var version, out bool partial))
                continue;
            if (keep != null && version == keep)
                continue;
            if (keepNewest && newest != null && version == newest && !partial)
                continue;
            TryDeleteFile(file);
        }
    }

    static bool TrySetupVersion(string name, out Version version, out bool partial)
    {
        version = new Version(0, 0);
        partial = false;
        const string head = "Zwmenu-Setup-";
        if (!name.StartsWith(head, StringComparison.OrdinalIgnoreCase))
            return false;
        string tail = name[head.Length..];
        if (tail.EndsWith(".exe.part", StringComparison.OrdinalIgnoreCase))
        {
            partial = true;
            tail = tail[..^".exe.part".Length];
        }
        else if (tail.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            tail = tail[..^".exe".Length];
        }
        else
        {
            return false;
        }

        return Version.TryParse(tail, out version!);
    }

    static void DropLooseTemp(string temp, DateTime olderThanUtc)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(temp, "zwmenu-*"))
            {
                string name = Path.GetFileName(file);
                bool junk = name.StartsWith("zwmenu-voicerec-", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".ffconcat", StringComparison.OrdinalIgnoreCase);
                if (!junk || !OlderThan(file, olderThanUtc))
                    continue;
                TryDeleteFile(file);
            }

            foreach (string child in Directory.EnumerateDirectories(temp))
            {
                string name = Path.GetFileName(child);
                bool junk = name.StartsWith("zwpic-", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("zwrename-", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("zwmenu-hold-", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("zwmenu-bg-", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("zwmenu-sweep-", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("zwmenu cmd ", StringComparison.OrdinalIgnoreCase);
                if (!junk || !OlderThan(child, olderThanUtc))
                    continue;
                TryDeleteDir(child);
            }
        }
        catch
        {
            // временная папка занята
        }
    }

    static void DropAppTemp(string root)
    {
        if (!Directory.Exists(root))
            return;
        try
        {
            foreach (string child in Directory.EnumerateDirectories(root))
            {
                string name = Path.GetFileName(child);
                if (name.Equals("inbox", StringComparison.OrdinalIgnoreCase) || name.StartsWith("clip-", StringComparison.OrdinalIgnoreCase))
                    TryDeleteDir(child);
            }

            foreach (string file in Directory.EnumerateFiles(root))
            {
                if (Path.GetFileName(file).Equals("cursor-carry", StringComparison.OrdinalIgnoreCase))
                    continue;
                TryDeleteFile(file);
            }
        }
        catch
        {
            // каталог уже исчез
        }
    }

    static void DropToolLeftovers()
    {
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zwmenu");
        string ffmpeg = Path.Combine(local, "ffmpeg");
        if (File.Exists(Path.Combine(ffmpeg, "ffmpeg.exe")))
        {
            TryDeleteFile(Path.Combine(ffmpeg, "ffmpeg-download.zip"));
            TryDeleteDir(Path.Combine(ffmpeg, "_extract"));
        }

        string voice = Path.Combine(local, "Voice");
        if (Directory.Exists(voice))
        {
            try
            {
                foreach (string zip in Directory.EnumerateFiles(voice, "*.zip"))
                {
                    string folder = Path.Combine(voice, Path.GetFileNameWithoutExtension(zip));
                    if (File.Exists(Path.Combine(folder, "am", "final.mdl")))
                        TryDeleteFile(zip);
                }
            }
            catch { /* модели на месте */ }
        }

        string voices = Path.Combine(local, "voices");
        if (Directory.Exists(voices))
        {
            try
            {
                foreach (string part in Directory.EnumerateFiles(voices, "*.part"))
                    TryDeleteFile(part);
            }
            catch { /* папка голосов занята */ }
        }

        try
        {
            if (SetupOffer.CableInstalled())
                TryDeleteDir(Path.Combine(local, "setup", "vbcable"));
        }
        catch { /* кабель не проверяется */ }
    }

    static bool OlderThan(string path, DateTime olderThanUtc)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path) < olderThanUtc;
        }
        catch
        {
            return false;
        }
    }

    static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* файл ещё открыт */ }
    }

    static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch { /* каталог ещё нужен */ }
    }
}
