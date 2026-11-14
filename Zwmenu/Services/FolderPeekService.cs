using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Zwmenu.Services;

public sealed record FolderPeekEntry(string Path, string Name, bool IsDirectory, bool IsImage);

/// <summary>Содержимое папки для выезжающей панели: список с кэшем и лёгкими превью.</summary>
public static class FolderPeekService
{
    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".gif", ".bmp", ".tif", ".tiff", ".ico", ".jfif"
    };

    private static readonly ConcurrentDictionary<string, CacheList> ListCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, ImageSource> VisualCache = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxVisualCache = 180;

    /// <summary>Только память: наведение не ждёт диск, если список уже снят.</summary>
    public static bool TryTakeCached(string folder, int max, out IReadOnlyList<FolderPeekEntry> items)
    {
        items = Array.Empty<FolderPeekEntry>();
        if (string.IsNullOrWhiteSpace(folder))
            return false;
        string key;
        try { key = Path.GetFullPath(folder); }
        catch { return false; }
        if (!ListCache.TryGetValue(key, out var hit) || (DateTime.UtcNow - hit.At).TotalSeconds >= 8)
            return false;
        items = hit.Items.Count <= max ? hit.Items : hit.Items.Take(max).ToList();
        return true;
    }

    public static IReadOnlyList<FolderPeekEntry> List(string folder, int max = 36)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return Array.Empty<FolderPeekEntry>();

        string key = Path.GetFullPath(folder);
        if (ListCache.TryGetValue(key, out var hit) && (DateTime.UtcNow - hit.At).TotalSeconds < 8)
            return hit.Items.Count <= max ? hit.Items : hit.Items.Take(max).ToList();

        // Не сканируем всю папку: останавливаемся после лимита (сетевые/огромные каталоги).
        int hardCap = Math.Max(max * 2, 64);
        var dirs = new List<FolderPeekEntry>();
        var files = new List<FolderPeekEntry>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(folder))
            {
                if (dirs.Count >= hardCap)
                    break;
                try
                {
                    string name = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(name) || name.StartsWith('.'))
                        continue;
                    dirs.Add(new FolderPeekEntry(dir, name, true, false));
                }
                catch { /* нет доступа */ }
            }

            foreach (var file in Directory.EnumerateFiles(folder))
            {
                if (dirs.Count + files.Count >= hardCap)
                    break;
                try
                {
                    string name = Path.GetFileName(file);
                    if (string.IsNullOrEmpty(name) || name.StartsWith('.'))
                        continue;
                    string ext = Path.GetExtension(name);
                    files.Add(new FolderPeekEntry(file, name, false, ImageExt.Contains(ext)));
                }
                catch { /* нет доступа */ }
            }
        }
        catch
        {
            // частичный список лучше, чем зависание UI
        }

        var ranked = dirs
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .Concat(files.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
            .Take(Math.Max(max, 64))
            .ToList();

        ListCache[key] = new CacheList(DateTime.UtcNow, ranked);
        return ranked.Count <= max ? ranked : ranked.Take(max).ToList();
    }

    public static ImageSource? LoadVisual(FolderPeekEntry entry, int px = 48)
    {
        try
        {
            string cacheKey = entry.Path + "|" + px;
            if (VisualCache.TryGetValue(cacheKey, out var cached))
                return cached;

            ImageSource? src = null;
            if (entry.IsImage && File.Exists(entry.Path))
                src = TryLoadImageThumb(entry.Path, px);

            src ??= AppIconService.CreateLiveIcon(entry.Path, px);
            if (src == null)
                return null;

            if (VisualCache.Count >= MaxVisualCache)
            {
                foreach (var drop in VisualCache.Keys.Take(40).ToList())
                    VisualCache.TryRemove(drop, out _);
            }

            VisualCache[cacheKey] = src;
            return src;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? TryLoadImageThumb(string path, int px)
    {
        try
        {
            // Stream надёжнее Uri на путях с пробелами/кириллицей; OnLoad закрывает файл сразу.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.StreamSource = fs;
            bi.DecodePixelWidth = Math.Max(px * 2, 96);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch
        {
            return null;
        }
    }

    public static string FormatHint(FolderPeekEntry e)
    {
        if (e.IsDirectory)
            return "Папка. Повиси около 3 сек: заглянуть внутрь. Кликни: открыть в проводнике.";
        if (e.IsImage)
            return "Картинка. Кликни: открыть.";
        string ext = Path.GetExtension(e.Name);
        if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            return "Ярлык. Кликни: запустить.";
        return "Файл. Кликни: открыть.";
    }

    private sealed record CacheList(DateTime At, IReadOnlyList<FolderPeekEntry> Items);
}
