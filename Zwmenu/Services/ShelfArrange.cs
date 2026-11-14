using System.Text.RegularExpressions;

namespace Zwmenu.Services;

readonly record struct ShelfSpot(string Key, string Name, ShelfEntry? Entry, string[]? Stack = null);

/// <summary>Порядок полки: вручную, по типу, по имени, папки сначала, серии рядом.</summary>
static class ShelfArrange
{
    public const string DefaultKinds = "series,folder,app,image,disk,music,video,doc,zip,other,ghost";

    private static readonly string[] Known =
    [
        "series", "folder", "app", "image", "disk", "music", "video", "doc", "zip", "other", "ghost"
    ];

    public static string Title(string kind) => kind switch
    {
        "series" => "Продолжения",
        "app" => "Программы",
        "image" => "Картинки",
        "disk" => "Диски",
        "music" => "Музыка",
        "video" => "Видео",
        "folder" => "Папки",
        "doc" => "Документы",
        "zip" => "Архивы",
        "ghost" => "Значки",
        _ => "Остальное"
    };

    public static List<string> Kinds(string? raw)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(raw))
        {
            foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Array.IndexOf(Known, part) >= 0 && !list.Contains(part))
                    list.Add(part);
            }
        }

        foreach (string kind in Known)
        {
            if (list.Contains(kind))
                continue;
            if (kind == "series")
                list.Insert(0, kind);
            else
                list.Add(kind);
        }

        return list;
    }

    public static List<ShelfSpot> Build(IReadOnlyList<ShelfEntry> items, int sort, int layout, string? kindOrder, string? openStack = null)
    {
        var spots = Collapse(Flat(items, sort, kindOrder), openStack);
        if (layout is not (3 or 9))
            return spots;

        var result = new List<ShelfSpot>();
        foreach (string kind in Kinds(kindOrder))
        {
            var chunk = spots.Where(spot => spot.Entry is { } entry && KindOf(entry) == kind).ToList();
            if (chunk.Count == 0)
                continue;
            result.Add(new ShelfSpot("head:" + kind, Title(kind), null));
            result.AddRange(chunk);
        }

        return result;
    }

    /// <summary>
    /// Файлы одного сериала схлопываются в стопку. Закрытая стопка одна.
    /// Открытая остаётся стопкой и рядом показывает каждую обложку.
    /// </summary>
    private static List<ShelfSpot> Collapse(List<ShelfEntry> flat, string? openStack)
    {
        var groups = new Dictionary<string, List<ShelfEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in flat)
        {
            if (KindOf(entry) != "series")
                continue;
            string stem = Stem(entry.Name);
            if (stem.Length == 0)
                stem = Path.GetFileNameWithoutExtension(entry.Name);
            if (!groups.TryGetValue(stem, out var list))
            {
                list = new List<ShelfEntry>();
                groups[stem] = list;
            }

            list.Add(entry);
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ShelfSpot>();
        foreach (var entry in flat)
        {
            if (used.Contains(entry.Key))
                continue;
            if (KindOf(entry) == "series")
            {
                string stem = Stem(entry.Name);
                if (stem.Length == 0)
                    stem = Path.GetFileNameWithoutExtension(entry.Name);
                if (groups.TryGetValue(stem, out var members) && members.Count >= 2)
                {
                    foreach (var member in members)
                        used.Add(member.Key);
                    var ordered = members
                        .OrderBy(item => Number(item.Name))
                        .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ToList();
                    var cover = ordered[^1];
                    string key = "stack:" + stem.ToLowerInvariant();
                    result.Add(new ShelfSpot(key, stem, cover, ordered.Select(item => item.Launch).ToArray()));
                    if (string.Equals(openStack, key, StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var member in ordered)
                            result.Add(new ShelfSpot(member.Key, member.Name, member));
                    }

                    continue;
                }
            }

            used.Add(entry.Key);
            result.Add(new ShelfSpot(entry.Key, entry.Name, entry));
        }

        return result;
    }

    private static List<ShelfEntry> Flat(IReadOnlyList<ShelfEntry> items, int sort, string? kindOrder)
    {
        var kinds = Kinds(kindOrder);
        return sort switch
        {
            1 => items.OrderBy(entry => kinds.IndexOf(KindOf(entry)))
                .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            2 => items.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            3 => items.OrderBy(entry => KindOf(entry) == "folder" ? 0 : 1)
                .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            4 => items.OrderBy(entry => Stem(entry.Name), StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(entry => Number(entry.Name))
                .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            _ => items.ToList()
        };
    }

    public static string KindOf(ShelfEntry entry)
    {
        if (entry.Ghost)
            return "ghost";
        if (LooksNumbered(entry.Name))
            return "series";
        if (entry.Launch.Length > 0 && System.IO.Directory.Exists(entry.Launch))
            return "folder";
        if (entry.Photo)
            return "image";
        string ext = Path.GetExtension(entry.Name);
        if (Is(ext, ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".jfif", ".ico"))
            return "image";
        if (Is(ext, ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".wma"))
            return "music";
        if (Is(ext, ".mp4", ".mkv", ".avi", ".mov", ".webm", ".wmv"))
            return "video";
        if (Is(ext, ".iso", ".img", ".vhd", ".vhdx", ".dmg", ".wim", ".bin", ".cue"))
            return "disk";
        if (Is(ext, ".exe", ".bat", ".cmd", ".lnk", ".msi", ".ps1", ".com"))
            return "app";
        if (Is(ext, ".zip", ".rar", ".7z", ".tar", ".gz"))
            return "zip";
        if (Is(ext, ".txt", ".pdf", ".doc", ".docx", ".rtf", ".md", ".json", ".xml", ".csv"))
            return "doc";
        return "other";
    }

    private static bool Is(string ext, params string[] options)
    {
        foreach (string option in options)
        {
            if (ext.Equals(option, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool LooksNumbered(string name)
    {
        string bare = Path.GetFileNameWithoutExtension(name);
        return Regex.IsMatch(bare, @"(?:[\(\[]\s*\d+\s*[\)\]]|\s+-\s*\d+|\s+\d+)\s*$");
    }

    private static string Stem(string name)
    {
        string bare = Path.GetFileNameWithoutExtension(name);
        return Regex.Replace(bare, @"\s*[\(\[]?\d+[\)\]]?\s*$", "").Trim();
    }

    private static int Number(string name)
    {
        var match = Regex.Match(Path.GetFileNameWithoutExtension(name), @"(\d+)\s*$");
        return match.Success && int.TryParse(match.Groups[1].Value, out int value) ? value : 0;
    }
}
