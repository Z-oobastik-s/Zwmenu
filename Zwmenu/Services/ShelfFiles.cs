using System.Text.Json;

namespace Zwmenu.Services;

/// <summary>
/// Файлы полки лежат в скрытой папке на рабочем столе.
/// На столе их не видно, в стекле они есть. Вытащил обратно, значок снова на месте.
/// </summary>
static class ShelfFiles
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string? _directory;

    public static string DirectoryPath
    {
        get
        {
            if (_directory != null)
                return _directory;
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string dir = Path.Combine(desktop, "Полка");
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                var attrs = File.GetAttributes(dir);
                if ((attrs & FileAttributes.Hidden) == 0)
                    File.SetAttributes(dir, attrs | FileAttributes.Hidden);
            }
            catch
            {
                // папка всё равно работает, даже если значок «Полка» виден
            }

            _directory = dir;
            return dir;
        }
    }

    public static bool IsInside(string path)
    {
        string full = Path.GetFullPath(path).TrimEnd('\\', '/');
        string root = Path.GetFullPath(DirectoryPath).TrimEnd('\\', '/');
        return full.Equals(root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static string[]? Dragging { get; private set; }

    public static void BeginDrag(string[] paths) => Dragging = paths.Length == 0 ? null : paths;

    public static void EndDrag() => Dragging = null;

    public static IReadOnlyList<ShelfEntry> List()
    {
        var list = new List<ShelfEntry>();
        try
        {
            foreach (string path in System.IO.Directory.EnumerateFileSystemEntries(DirectoryPath))
            {
                string name = Path.GetFileName(path);
                if (name.Length == 0 || name.StartsWith('.'))
                    continue;
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                    continue;
                long stamp = 0;
                try
                {
                    var info = new FileInfo(path);
                    stamp = info.LastWriteTimeUtc.Ticks ^ info.Length;
                }
                catch
                {
                    // имя всё равно показываем
                }

                list.Add(new ShelfEntry(path, name, stamp, Ghost: false, path, "", IsImage(path)));
            }
        }
        catch
        {
            // папка занята
        }

        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        var prefs = LoadPrefs();
        int ghostAt = 0;
        foreach (ShelfGhost ghost in prefs.Ghosts)
        {
            if (string.IsNullOrWhiteSpace(ghost.Clsid) || string.IsNullOrWhiteSpace(ghost.Parsing))
                continue;
            try { ShelfShell.Hide(ghost.Clsid); }
            catch { /* значок останется, полка всё равно его показывает */ }
            list.Insert(ghostAt, new ShelfEntry(
                "ghost:" + ghost.Clsid,
                string.IsNullOrWhiteSpace(ghost.Name) ? "Значок" : ghost.Name,
                1,
                Ghost: true,
                ghost.Parsing,
                ghost.Clsid,
                Photo: false));
            ghostAt++;
        }

        return ApplyOrder(list, prefs);
    }

    public static void MoveKey(string key, int index)
    {
        var keys = List().Select(entry => entry.Key).ToList();
        int from = keys.FindIndex(item => item.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (from < 0)
            return;
        keys.RemoveAt(from);
        if (index > from)
            index--;
        if (index < 0)
            index = 0;
        if (index > keys.Count)
            index = keys.Count;
        keys.Insert(index, key);
        var prefs = LoadPrefs();
        prefs.Order = keys;
        SavePrefs(prefs);
    }

    private static IReadOnlyList<ShelfEntry> ApplyOrder(List<ShelfEntry> items, ShelfPrefs prefs)
    {
        var map = new Dictionary<string, ShelfEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in items)
            map[entry.Key] = entry;
        var ordered = new List<ShelfEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (prefs.Order != null)
        {
            foreach (string key in prefs.Order)
            {
                if (map.TryGetValue(key, out var entry) && seen.Add(entry.Key))
                    ordered.Add(entry);
            }
        }

        bool appended = false;
        foreach (var entry in items)
        {
            if (!seen.Add(entry.Key))
                continue;
            ordered.Add(entry);
            appended = true;
        }

        bool same = prefs.Order != null
            && prefs.Order.Count == ordered.Count
            && !appended;
        if (same)
        {
            for (int i = 0; i < ordered.Count; i++)
            {
                if (!string.Equals(prefs.Order![i], ordered[i].Key, StringComparison.OrdinalIgnoreCase))
                {
                    same = false;
                    break;
                }
            }
        }

        if (!same)
        {
            prefs.Order = ordered.Select(entry => entry.Key).ToList();
            SavePrefs(prefs);
        }

        return ordered;
    }

    public static bool TakeGhost(string clsid, string parsing, string name)
    {
        clsid = (clsid ?? "").Trim();
        parsing = (parsing ?? "").Trim();
        if (clsid.Length == 0 || parsing.Length == 0)
            return false;
        var prefs = LoadPrefs();
        if (prefs.Ghosts.Any(g => string.Equals(g.Clsid, clsid, StringComparison.OrdinalIgnoreCase)))
        {
            ShelfShell.Hide(clsid);
            return true;
        }

        ShelfShell.Hide(clsid);
        prefs.Ghosts.Add(new ShelfGhost { Clsid = clsid, Parsing = parsing, Name = name });
        SavePrefs(prefs);
        return true;
    }

    public static bool ReturnGhost(string clsid)
    {
        clsid = (clsid ?? "").Trim();
        if (clsid.Length == 0)
            return false;
        var prefs = LoadPrefs();
        int removed = prefs.Ghosts.RemoveAll(g => string.Equals(g.Clsid, clsid, StringComparison.OrdinalIgnoreCase));
        ShelfShell.Show(clsid);
        if (removed > 0)
            SavePrefs(prefs);
        return removed > 0;
    }

    /// <summary>Забрать файл или папку с рабочего стола в полку. На столе значок пропадает.</summary>
    public static bool TakeIn(string source)
    {
        source = (source ?? "").Trim().Trim('"');
        if (source.Length == 0 || IsInside(source))
            return false;
        if (!File.Exists(source) && !System.IO.Directory.Exists(source))
            return false;

        string name = Path.GetFileName(source.TrimEnd('\\', '/'));
        if (name.Length == 0)
            return false;
        string dest = Unique(Path.Combine(DirectoryPath, name));
        try
        {
            if (System.IO.Directory.Exists(source))
                System.IO.Directory.Move(source, dest);
            else
                File.Move(source, dest);
            return true;
        }
        catch (IOException)
        {
            if (System.IO.Directory.Exists(source))
                return false;
            string part = dest + ".part";
            File.Copy(source, part);
            if (File.Exists(dest))
                File.Delete(dest);
            File.Move(part, dest);
            File.Delete(source);
            return true;
        }
    }

    /// <summary>Вернуть на рабочий стол, рядом с полкой, обычным значком.</summary>
    public static bool ReturnToDesktop(string source)
    {
        if (!IsInside(source) || (!File.Exists(source) && !System.IO.Directory.Exists(source)))
            return false;
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string name = Path.GetFileName(source.TrimEnd('\\', '/'));
        string dest = Unique(Path.Combine(desktop, name));
        if (System.IO.Directory.Exists(source))
            System.IO.Directory.Move(source, dest);
        else
            File.Move(source, dest);
        return true;
    }

    public static ShelfPrefs CurrentLook()
    {
        var prefs = LoadPrefs();
        prefs.ClampLook();
        return prefs;
    }

    public static void EditLook(Action<ShelfPrefs> edit)
    {
        var prefs = LoadPrefs();
        edit(prefs);
        prefs.ClampLook();
        SavePrefs(prefs);
    }

    public static void ResetLook()
    {
        var cur = LoadPrefs();
        SavePrefs(new ShelfPrefs
        {
            Open = cur.Open,
            Placed = cur.Placed,
            Left = cur.Left,
            Top = cur.Top,
            Ghosts = cur.Ghosts,
            Order = cur.Order
        });
    }

    public static ShelfPrefs LoadPrefs()
    {
        try
        {
            string path = PrefsPath();
            if (!File.Exists(path))
                return new ShelfPrefs();
            var prefs = JsonSerializer.Deserialize<ShelfPrefs>(File.ReadAllText(path), Json) ?? new ShelfPrefs();
            prefs.Ghosts ??= new List<ShelfGhost>();
            prefs.Order ??= new List<string>();
            return prefs;
        }
        catch
        {
            return new ShelfPrefs();
        }
    }

    public static void SavePrefs(ShelfPrefs prefs)
    {
        try
        {
            string path = PrefsPath();
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(prefs, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // место на экране вспомним в следующий раз
        }
    }

    private static string PrefsPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zwmenu", "shelf.json");

    private static bool IsImage(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jfif", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path) && !System.IO.Directory.Exists(path))
            return path;
        string? dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
            return path;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 2; i < 1000; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !System.IO.Directory.Exists(candidate))
                return candidate;
        }

        return Path.Combine(dir, name + " " + Guid.NewGuid().ToString("N")[..6] + ext);
    }
}

readonly record struct ShelfEntry(string Key, string Name, long Stamp, bool Ghost, string Launch, string Clsid, bool Photo);

sealed class ShelfGhost
{
    public string Clsid { get; set; } = "";
    public string Parsing { get; set; } = "";
    public string Name { get; set; } = "";
}

sealed class ShelfPrefs
{
    public bool Open { get; set; }
    public bool Placed { get; set; }
    public double Left { get; set; }
    public double Top { get; set; }
    public List<ShelfGhost> Ghosts { get; set; } = new();
    public List<string> Order { get; set; } = new();

    /// <summary>Сторона значка, 32–96.</summary>
    public int Icon { get; set; } = 64;
    public int Rows { get; set; } = 3;
    /// <summary>0 = сколько влезет в ширину.</summary>
    public int Columns { get; set; }
    public bool Names { get; set; } = true;
    public int NameSize { get; set; } = 12;
    public int Radius { get; set; } = 16;
    public int Gap { get; set; } = 8;
    /// <summary>-1 = авто: один ряд выше, несколько ближе к низу.</summary>
    public int Bottom { get; set; } = -1;
    /// <summary>0 слева, 1 центр, 2 справа.</summary>
    public int Align { get; set; } = 1;
    /// <summary>Процент увеличения при наведении.</summary>
    public int Hover { get; set; } = 120;
    public bool Press { get; set; } = true;
    /// <summary>Плотность подложки, проценты.</summary>
    public int Plate { get; set; } = 94;
    /// <summary>Ширина полки в процентах рабочей области.</summary>
    public int ShelfWidth { get; set; } = 100;
    /// <summary>0 ужать, 1 листать вбок, 2 листать вниз.</summary>
    public int Scroll { get; set; }
    public bool HideInGame { get; set; } = true;
    public bool Above { get; set; }
    /// <summary>0 полка … 11 центр, 12 обложка, 13 кино, 14 колода, 15 кольцо, 16 трон.</summary>
    public int Layout { get; set; }
    /// <summary>0 вручную, 1 по типу, 2 по имени, 3 папки сначала, 4 серии.</summary>
    public int Sort { get; set; }
    public string KindOrder { get; set; } = ShelfArrange.DefaultKinds;

    public void ClampLook()
    {
        Icon = Math.Clamp(Icon, 32, 96);
        Rows = Math.Clamp(Rows, 1, 6);
        Columns = Math.Clamp(Columns, 0, 24);
        NameSize = Math.Clamp(NameSize, 9, 18);
        Radius = Math.Clamp(Radius, 0, 36);
        Gap = Math.Clamp(Gap, 0, 32);
        if (Bottom < -1)
            Bottom = -1;
        if (Bottom > 220)
            Bottom = 220;
        Align = Math.Clamp(Align, 0, 2);
        Hover = Math.Clamp(Hover, 100, 160);
        Plate = Math.Clamp(Plate, 15, 100);
        ShelfWidth = Math.Clamp(ShelfWidth, 40, 100);
        Scroll = Math.Clamp(Scroll, 0, 2);
        Layout = Math.Clamp(Layout, 0, 16);
        Sort = Math.Clamp(Sort, 0, 4);
        KindOrder = string.Join(',', ShelfArrange.Kinds(KindOrder));
    }
}
