using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using SkiaSharp;

namespace Zwmenu.Services;

/// <summary>
/// Последние пять снимков на диске. В памяти полный кадр не держится:
/// файл пишется и закрывается, лишний удаляется.
/// </summary>
public static class ShotHistory
{
    public const int Limit = 5;

    private static readonly object Gate = new();

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Zwmenu", "shots");

    private static string ManifestPath => Path.Combine(Dir, "order.json");

    public static string ThumbPath(string pngPath) =>
        Path.Combine(Path.GetDirectoryName(pngPath) ?? "", Path.GetFileNameWithoutExtension(pngPath) + ".thumb.jpg");

    /// <summary>
    /// Пиксели снимаются сразу, сжатие идёт в фоне и без GDI:
    /// фоновый Graphics.Save пересекается с UI и роняет процесс.
    /// </summary>
    public static void RememberLater(Bitmap source)
    {
        byte[]? pixels;
        int width;
        int height;
        try
        {
            pixels = CopyPixels(source, out width, out height);
        }
        catch
        {
            return;
        }

        if (pixels == null || width < 1 || height < 1)
            return;

        _ = Task.Run(() =>
        {
            try { Remember(pixels, width, height); }
            catch { /* история не должна ломать копирование */ }
        });
    }

    public static IReadOnlyList<string> Paths()
    {
        lock (Gate)
            return ExistingLocked();
    }

    public static void Prune()
    {
        lock (Gate)
            ExistingLocked();
    }

    private static void Remember(byte[] pixels, int width, int height)
    {
        Directory.CreateDirectory(Dir);
        string name = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png";
        string pngTmp = Path.Combine(Dir, name + ".tmp");
        string thumbTmp = Path.Combine(Dir, Path.GetFileNameWithoutExtension(name) + ".thumb.jpg.tmp");
        Encode(pixels, width, height, pngTmp, thumbTmp);

        lock (Gate)
        {
            string png = Path.Combine(Dir, name);
            string thumb = ThumbPath(png);
            if (File.Exists(png))
                File.Delete(png);
            File.Move(pngTmp, png);
            if (File.Exists(thumbTmp))
            {
                if (File.Exists(thumb))
                    File.Delete(thumb);
                File.Move(thumbTmp, thumb);
            }

            var names = ReadNames();
            names.Insert(0, name);
            WriteAndTrim(names);
        }
    }

    private static byte[]? CopyPixels(Bitmap source, out int width, out int height)
    {
        width = source.Width;
        height = source.Height;
        if (width < 1 || height < 1)
            return null;

        var data = source.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            int packed = width * 4;
            if (data.Stride < packed)
                return null;
            var pixels = new byte[packed * height];
            for (int y = 0; y < height; y++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * packed, packed);
            return pixels;
        }
        finally
        {
            source.UnlockBits(data);
        }
    }

    private static void Encode(byte[] pixels, int width, int height, string pngTmp, string thumbTmp)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var image = SKImage.FromPixelCopy(info, pixels, width * 4);
        using (var png = image.Encode(SKEncodedImageFormat.Png, 100))
        using (var file = File.Create(pngTmp))
            png.SaveTo(file);

        int tw = Math.Min(160, width);
        int th = Math.Max(1, (int)Math.Round(height * (tw / (double)width)));
        using var surface = SKSurface.Create(new SKImageInfo(tw, th, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        surface.Canvas.DrawImage(image, new SKRect(0, 0, tw, th));
        using var snap = surface.Snapshot();
        using var jpg = snap.Encode(SKEncodedImageFormat.Jpeg, 70);
        using var thumb = File.Create(thumbTmp);
        jpg.SaveTo(thumb);
    }

    private static List<string> ExistingLocked()
    {
        try
        {
            if (!Directory.Exists(Dir))
                return new List<string>();

            var names = ReadNames();
            if (names.Count == 0)
            {
                names = Directory.EnumerateFiles(Dir, "*.png")
                    .Select(Path.GetFileName)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!)
                    .OrderByDescending(n => n, StringComparer.Ordinal)
                    .Take(Limit)
                    .ToList();
            }

            return WriteAndTrim(names);
        }
        catch
        {
            return new List<string>();
        }
    }

    private static List<string> ReadNames()
    {
        try
        {
            if (!File.Exists(ManifestPath))
                return new List<string>();
            var names = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(ManifestPath));
            return names ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static List<string> WriteAndTrim(List<string> names)
    {
        var keep = new List<string>(Limit);
        var keepSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name) || !File.Exists(Path.Combine(Dir, name)))
                continue;
            if (!keepSet.Add(name))
                continue;
            keep.Add(name);
            if (keep.Count == Limit)
                break;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(Dir))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && !keepSet.Contains(name))
                {
                    TryDelete(file);
                    TryDelete(ThumbPath(file));
                    continue;
                }

                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                    && File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddMinutes(-2))
                    TryDelete(file);
            }

            Directory.CreateDirectory(Dir);
            string manifestTmp = ManifestPath + ".tmp";
            File.WriteAllText(manifestTmp, JsonSerializer.Serialize(keep));
            File.Move(manifestTmp, ManifestPath, overwrite: true);
        }
        catch
        {
            // список в памяти всё равно верный
        }

        return keep.Select(n => Path.Combine(Dir, n)).ToList();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* файл ещё читают */ }
    }
}
