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

    public static List<ShelfSpot> Build(
        IReadOnlyList<ShelfEntry> items,
        int sort,
        int layout,
        string? kindOrder,
        string? openStack = null,
        IReadOnlyList<ShelfStack>? piles = null,
        IReadOnlyList<string>? loose = null)
    {
        var spots = Collapse(Flat(items, sort, kindOrder), openStack, piles, loose);
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
    /// Свои стопки важнее автомата. Пронумерованный сериал схлопывается сам,
    /// если файл не помечен «держать отдельно» и не лежит в своей стопке.
    /// Открытая стопка остаётся и рядом показывает каждый файл.
    /// </summary>
    private static List<ShelfSpot> Collapse(
        List<ShelfEntry> flat,
        string? openStack,
        IReadOnlyList<ShelfStack>? piles,
        IReadOnlyList<string>? loose)
    {
        var looseSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (loose != null)
        {
            foreach (string key in loose)
            {
                if (!string.IsNullOrWhiteSpace(key))
                    looseSet.Add(key);
            }
        }

        var pileByKey = new Dictionary<string, ShelfStack>(StringComparer.OrdinalIgnoreCase);
        var pileMembers = new Dictionary<string, List<ShelfEntry>>(StringComparer.OrdinalIgnoreCase);
        if (piles != null)
        {
            foreach (var pile in piles)
            {
                if (string.IsNullOrWhiteSpace(pile.Id) || pile.Keys == null || pile.Keys.Count < 2)
                    continue;
                var members = new List<ShelfEntry>();
                foreach (string key in pile.Keys)
                {
                    int at = flat.FindIndex(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
                    if (at < 0 || pileByKey.ContainsKey(flat[at].Key))
                        continue;
                    members.Add(flat[at]);
                }

                if (members.Count < 2)
                    continue;
                pileMembers[pile.Id] = members;
                foreach (var member in members)
                    pileByKey[member.Key] = pile;
            }
        }

        var groups = new Dictionary<string, List<ShelfEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in flat)
        {
            if (pileByKey.ContainsKey(entry.Key) || looseSet.Contains(entry.Key) || KindOf(entry) != "series")
                continue;
            string stem = StemOf(entry.Name);
            if (!groups.TryGetValue(stem, out var list))
            {
                list = new List<ShelfEntry>();
                groups[stem] = list;
            }

            list.Add(entry);
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenPile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ShelfSpot>();
        foreach (var entry in flat)
        {
            if (!used.Add(entry.Key))
                continue;
            if (pileByKey.TryGetValue(entry.Key, out var pile) && pileMembers.TryGetValue(pile.Id, out var piled))
            {
                if (!seenPile.Add(pile.Id))
                    continue;
                foreach (var member in piled)
                    used.Add(member.Key);
                var cover = piled[^1];
                string title = pile.Name;
                if (string.IsNullOrWhiteSpace(title))
                    title = Path.GetFileNameWithoutExtension(cover.Name);
                if (string.IsNullOrWhiteSpace(title))
                    title = "Стопка";
                string key = "pile:" + pile.Id;
                result.Add(new ShelfSpot(key, title, cover, piled.Select(item => item.Key).ToArray()));
                if (string.Equals(openStack, key, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var member in piled)
                        result.Add(new ShelfSpot(member.Key, member.Name, member));
                }

                continue;
            }

            if (KindOf(entry) == "series" && !looseSet.Contains(entry.Key))
            {
                string stem = StemOf(entry.Name);
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
                    result.Add(new ShelfSpot(key, stem, cover, ordered.Select(item => item.Key).ToArray()));
                    if (string.Equals(openStack, key, StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var member in ordered)
                            result.Add(new ShelfSpot(member.Key, member.Name, member));
                    }

                    continue;
                }
            }

            result.Add(new ShelfSpot(entry.Key, entry.Name, entry));
        }

        return result;
    }

    public static string Bucket(ShelfEntry entry)
    {
        if (entry.Ghost)
            return "ghost";
        if (entry.Launch.Length > 0 && System.IO.Directory.Exists(entry.Launch))
            return "folder";
        string ext = Path.GetExtension(entry.Name);
        if (ext.Equals(".gif", StringComparison.OrdinalIgnoreCase))
            return "gif";
        if (Is(ext, ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".tif", ".tiff", ".jfif", ".ico"))
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

    public static string BucketTitle(string bucket) => bucket switch
    {
        "gif" => "Гиф",
        "image" => "Картинки",
        "music" => "Музыка",
        "video" => "Видео",
        "disk" => "Диски",
        "app" => "Программы",
        "folder" => "Папки",
        "zip" => "Архивы",
        "doc" => "Документы",
        "ghost" => "Значки",
        _ => "Файлы"
    };

    public static string GatherTitle(string bucket) => bucket switch
    {
        "gif" => "Собрать все гиф",
        "image" => "Собрать все картинки",
        "music" => "Собрать всю музыку",
        "video" => "Собрать все видео",
        "disk" => "Собрать все диски",
        "app" => "Собрать все программы",
        "folder" => "Собрать все папки",
        "zip" => "Собрать все архивы",
        "doc" => "Собрать все документы",
        _ => "Собрать такие же"
    };

    public static bool Held(IReadOnlyList<ShelfEntry> items, string key, IReadOnlyList<ShelfStack>? piles, IReadOnlyList<string>? loose)
    {
        if (piles != null)
        {
            foreach (var pile in piles)
            {
                if (pile.Keys != null && pile.Keys.Any(item => string.Equals(item, key, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }

        if (loose != null && loose.Any(item => string.Equals(item, key, StringComparison.OrdinalIgnoreCase)))
            return false;
        int mine = -1;
        for (int i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                mine = i;
                break;
            }
        }

        if (mine < 0 || KindOf(items[mine]) != "series")
            return false;
        string stem = StemOf(items[mine].Name);
        int count = 0;
        foreach (var entry in items)
        {
            if (KindOf(entry) != "series" || !string.Equals(StemOf(entry.Name), stem, StringComparison.OrdinalIgnoreCase))
                continue;
            if (loose != null && loose.Any(item => string.Equals(item, entry.Key, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (piles != null && piles.Any(pile => pile.Keys != null && pile.Keys.Any(item => string.Equals(item, entry.Key, StringComparison.OrdinalIgnoreCase))))
                continue;
            count++;
        }

        return count >= 2;
    }

    public static void SelfCheck()
    {
        var a = Entry(@"C:\shelf\a.jpg");
        var b = Entry(@"C:\shelf\b.jpg");
        var c = Entry(@"C:\shelf\c.jpg");
        var gif = Entry(@"C:\shelf\loop.gif");
        var song1 = Entry(@"C:\shelf\song 1.mp3");
        var song2 = Entry(@"C:\shelf\song 2.mp3");
        ShelfEntry[] items = [a, b, c, gif, song1, song2];
        var plain = Build(items, 0, 0, null);
        if (plain.Count(spot => spot.Key.StartsWith("stack:", StringComparison.Ordinal)) != 1)
            throw new InvalidOperationException("сериал не собрался");
        if (!plain.Any(spot => spot.Key == a.Key) || !plain.Any(spot => spot.Key == gif.Key))
            throw new InvalidOperationException("картинка сама легла в стопку");

        var piles = new List<ShelfStack>
        {
            new() { Id = "img", Name = "Картинки", Keys = [a.Key, b.Key] }
        };
        var mixed = Build(items, 0, 0, null, "pile:img", piles, [song1.Key]);
        var pile = mixed.FirstOrDefault(spot => spot.Key == "pile:img");
        if (pile.Stack == null || pile.Stack.Length != 2 || !mixed.Any(spot => spot.Key == a.Key))
            throw new InvalidOperationException("своя стопка не открылась");
        if (!mixed.Any(spot => spot.Key == c.Key) || !mixed.Any(spot => spot.Key == song1.Key))
            throw new InvalidOperationException("отдельный файл пропал");
        if (mixed.Any(spot => spot.Key.StartsWith("stack:", StringComparison.Ordinal)))
            throw new InvalidOperationException("разнятый сериал собрался снова");
        if (Bucket(gif) != "gif" || Bucket(a) != "image" || Bucket(song2) != "music")
            throw new InvalidOperationException("тип стопки");
        if (!Held(items, b.Key, piles, null) || Held(items, c.Key, piles, [c.Key]))
            throw new InvalidOperationException("файл не понял, в стопке ли он");
        var stayed = ShelfFiles.Splice(["a", "b", "c"], ["b"], "c");
        if (stayed.Count != 3 || stayed[0] != "a" || stayed[1] != "b" || stayed[2] != "c")
            throw new InvalidOperationException("значок дёрнулся на месте");
        var shifted = ShelfFiles.Splice(["a", "b", "c"], ["c"], "a");
        if (shifted.Count != 3 || shifted[0] != "c" || shifted[1] != "a" || shifted[2] != "b")
            throw new InvalidOperationException("значок не встал влево");
    }

    private static ShelfEntry Entry(string path) =>
        new(path, Path.GetFileName(path), 1, false, path, "", false);

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
            4 => items.OrderBy(entry => StemOf(entry.Name), StringComparer.CurrentCultureIgnoreCase)
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

    private static string StemOf(string name)
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
