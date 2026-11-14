using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Zwmenu.Models;

namespace Zwmenu.Services;

/// <summary>Кэш PNG для колеса: скин и иконки пунктов не декодируем на каждый СКМ.</summary>
public static class RingImageCache
{
    private static readonly ConcurrentDictionary<string, ImageSource> Skins =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, ImageSource> Icons =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Скин на экране ~300–450 dip: 720 px хватает даже на 150–200% DPI.</summary>
    public static ImageSource? GetSkin(string path, int decodeWidth = 720)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (Skins.TryGetValue(path, out var hit))
            return hit;
        if (!File.Exists(path))
            return null;

        var loaded = Load(path, decodeWidth);
        if (loaded == null)
            return null;
        return Skins.GetOrAdd(path, loaded);
    }

    public static ImageSource? GetIcon(string path, int decodeWidth = 96)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (Icons.TryGetValue(path, out var hit))
            return hit;
        if (!File.Exists(path))
            return null;

        var loaded = Load(path, decodeWidth);
        if (loaded == null)
            return null;
        return Icons.GetOrAdd(path, loaded);
    }

    /// <summary>Декод иконок пунктов до первого СКМ. Вызывать с UI-потока.</summary>
    public static void WarmActions(IEnumerable<MenuAction> items)
    {
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.IconPath))
                GetIcon(item.IconPath);
            foreach (var child in item.Children)
            {
                if (!string.IsNullOrWhiteSpace(child.IconPath))
                    GetIcon(child.IconPath);
            }
        }
    }

    public static void WarmActiveSkin()
    {
        try
        {
            string? path = AppearanceStore.ResolveSelectedPath();
            if (path != null)
                GetSkin(path);
        }
        catch
        {
            // прогрев лучший effort
        }
    }

    public static void InvalidateSkin(string? path = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            Skins.Clear();
        else
            Skins.TryRemove(path, out _);
    }

    private static ImageSource? Load(string path, int decodeWidth)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = fs;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeWidth > 0)
                bmp.DecodePixelWidth = decodeWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
