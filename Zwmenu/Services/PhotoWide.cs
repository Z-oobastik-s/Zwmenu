using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using Zwmenu.Thumbs;

namespace Zwmenu.Services;

/// <summary>Форматы, которые Skia не знает, но может открыть сама Windows: JPEG XR, HEIC, AVIF, DDS, RAW.</summary>
static class PhotoWide
{
    static readonly HashSet<string> Extra = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jxr", ".wdp", ".hdp", ".wmp", ".dds", ".cur", ".rle",
        ".heic", ".heif", ".hif", ".avif", ".avifs",
        ".dng", ".cr2", ".nef", ".arw", ".orf", ".rw2", ".raf", ".pef", ".srw", ".raw", ".nrw"
    };

    public static bool IsExtra(string path) => Extra.Contains(Path.GetExtension(path ?? ""));

    public static PhotoDocument Open(string path) => OnUi(() => Decode(path, PhotoCodec.ViewEdge));

    public static SKBitmap? Thumbnail(string path, int edge)
    {
        try
        {
            using var doc = OnUi(() => Decode(path, edge));
            return doc.Frames.Length == 0 ? null : doc.Frames[0].Copy();
        }
        catch
        {
            return null;
        }
    }

    public static string Why(string path)
    {
        string ext = Path.GetExtension(path);
        if (ext.Equals(".heic", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".heif", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".hif", StringComparison.OrdinalIgnoreCase))
            return "Windows не открыл HEIC. Из магазина Microsoft ставится «Расширения для изображений HEIF».";
        if (ext.Equals(".avif", StringComparison.OrdinalIgnoreCase) || ext.Equals(".avifs", StringComparison.OrdinalIgnoreCase))
            return "Windows не открыл AVIF. Из магазина Microsoft ставится расширение AV1.";
        if (ext.Equals(".dng", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cr2", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".nef", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".arw", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".raw", StringComparison.OrdinalIgnoreCase))
            return "Windows не открыл этот RAW. Из магазина Microsoft ставятся «Расширения для изображений RAW».";
        return "Windows тоже не открыл этот файл.";
    }

    public static void SelfCheck()
    {
        string path = Path.Combine(Path.GetTempPath(), "zwmenu-wide.png");
        using (var bmp = new SKBitmap(new SKImageInfo(6, 4, SKColorType.Bgra8888, SKAlphaType.Opaque)))
        {
            bmp.Erase(SKColors.Orange);
            using var data = bmp.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidOperationException("wide png");
            File.WriteAllBytes(path, data.ToArray());
        }

        using var doc = Decode(path, 64);
        if (doc.Width != 6 || doc.Height != 4)
            throw new InvalidOperationException("wide");
    }

    static T OnUi<T>(Func<T> work)
    {
        var app = Application.Current;
        if (app == null || app.Dispatcher.CheckAccess())
            return work();
        return app.Dispatcher.Invoke(work);
    }

    static PhotoDocument Decode(string path, int maxEdge)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("Файла нет.", path);
        if (info.Length > PhotoCodec.MaxBytes)
            throw new InvalidDataException("Файл слишком большой.");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
            throw new InvalidDataException("В файле нет картинки.");

        BitmapFrame frame = decoder.Frames[0];
        int srcW = Math.Max(1, frame.PixelWidth);
        int srcH = Math.Max(1, frame.PixelHeight);
        BitmapSource source = frame;
        int longEdge = Math.Max(srcW, srcH);
        if (maxEdge > 16 && longEdge > maxEdge)
        {
            double scale = maxEdge / (double)longEdge;
            var scaled = new TransformedBitmap();
            scaled.BeginInit();
            scaled.Source = frame;
            scaled.Transform = new ScaleTransform(scale, scale);
            scaled.EndInit();
            source = scaled;
        }

        var converted = new FormatConvertedBitmap();
        converted.BeginInit();
        converted.Source = source;
        converted.DestinationFormat = PixelFormats.Pbgra32;
        converted.EndInit();
        converted.Freeze();

        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        int row = bitmap.RowBytes;
        for (int y = 0; y < height; y++)
            Marshal.Copy(pixels, y * stride, bitmap.GetPixels() + y * row, stride);

        return new PhotoDocument
        {
            SourceWidth = srcW,
            SourceHeight = srcH,
            HasAlpha = AnyAlpha(pixels, stride, width, height),
            Animated = false,
            Kind = Kind(path),
            Frames = new[] { bitmap },
            DelaysMs = new[] { 0 }
        };
    }

    static bool AnyAlpha(byte[] pixels, int stride, int width, int height)
    {
        int stepX = Math.Max(1, width / 48);
        int stepY = Math.Max(1, height / 48);
        for (int y = 0; y < height; y += stepY)
        {
            int row = y * stride;
            for (int x = 0; x < width; x += stepX)
            {
                if (pixels[row + x * 4 + 3] < 250)
                    return true;
            }
        }
        return false;
    }

    static string Kind(string path)
    {
        string ext = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        return ext switch
        {
            "JXR" or "WDP" or "HDP" or "WMP" => "JPEG XR",
            "HEIC" or "HEIF" or "HIF" => "HEIF",
            "AVIF" or "AVIFS" => "AVIF",
            "TIF" or "TIFF" => "TIFF",
            "" => "КАДР",
            _ => ext
        };
    }
}

static class PhotoFiles
{
    public static PhotoDocument Open(string path)
    {
        Exception? first = null;
        try
        {
            return PhotoCodec.Open(path);
        }
        catch (Exception ex) when (ex is not FileNotFoundException && ex.Message != "Файл слишком большой.")
        {
            first = ex;
        }

        try
        {
            return PhotoWide.Open(path);
        }
        catch
        {
            if (PhotoWide.IsExtra(path))
                throw new InvalidDataException(PhotoWide.Why(path));
            if (first != null)
                throw first;
            throw;
        }
    }
}
