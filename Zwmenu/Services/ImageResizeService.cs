using SkiaSharp;

namespace Zwmenu.Services;

public sealed record ImageSizePreset(
    string Id,
    string Title,
    string Hint,
    int Width,
    int Height);

/// <summary>Быстрая смена разрешения / кадра (cover: заполняет кадр, лишнее обрезает по центру).</summary>
public static class ImageResizeService
{
    public static IReadOnlyList<ImageSizePreset> Presets { get; } =
    [
        new("256", "256", "Квадрат 256×256", 256, 256),
        new("512", "512", "Квадрат 512×512", 512, 512),
        new("1024", "1024", "Квадрат 1024×1024", 1024, 1024),
        new("16x9", "16:9", "Горизонталь 1920×1080", 1920, 1080),
        new("9x16", "9:16", "Вертикаль 1080×1920", 1080, 1920),
        new("720p", "720p", "1280×720", 1280, 720),
        new("4x3", "4:3", "Классика 1600×1200", 1600, 1200)
    ];

    public static (string? OutputPath, string? Error) ResizeToPreset(string sourcePath, string presetId)
    {
        var preset = Presets.FirstOrDefault(p =>
            string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase));
        if (preset == null)
            return (null, "Неизвестный размер.");
        return ResizeExact(sourcePath, preset.Width, preset.Height);
    }

    public static (string? OutputPath, string? Error) ResizeExact(string sourcePath, int width, int height)
    {
        if (!ImageConvertService.IsImageFile(sourcePath))
            return (null, "Это не изображение.");
        if (width < 16 || height < 16 || width > 8192 || height > 8192)
            return (null, "Размер от 16 до 8192.");

        try
        {
            using var src = Decode(sourcePath);
            using var dst = CoverResize(src, width, height);

            string dir = Path.GetDirectoryName(sourcePath) ?? "";
            string name = Path.GetFileNameWithoutExtension(sourcePath);
            string ext = PreferSaveExtension(Path.GetExtension(sourcePath));
            string dest = FileOutPath.NextFree(Path.Combine(dir, $"{name} {width}x{height}{ext}"));

            SaveKeepingFormat(dst, dest, ext);
            return (dest, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private static string PreferSaveExtension(string ext)
    {
        ext = (ext ?? "").ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" or ".jpe" or ".jfif" => ".jpg",
            ".webp" => ".webp",
            ".png" => ".png",
            ".gif" => ".png", // после ресайза лучше PNG
            ".bmp" => ".png",
            ".tif" or ".tiff" => ".png",
            ".tga" => ".png",
            ".ico" => ".png",
            _ => ".png"
        };
    }

    private static void SaveKeepingFormat(SKBitmap bmp, string dest, string ext)
    {
        using var image = SKImage.FromBitmap(bmp);
        SKData? data = ext switch
        {
            ".jpg" => EncodeJpegFlat(bmp, 92),
            ".webp" => image.Encode(SKEncodedImageFormat.Webp, 90),
            _ => image.Encode(SKEncodedImageFormat.Png, 100)
        };
        using (data)
        {
            if (data == null)
                throw new InvalidOperationException("Не удалось сохранить.");
            using var fs = File.Create(dest);
            data.SaveTo(fs);
        }
    }

    private static SKData EncodeJpegFlat(SKBitmap src, int quality)
    {
        // JPEG без альфы: белый фон.
        using var flat = new SKBitmap(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(flat))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(src, 0, 0);
        }
        using var image = SKImage.FromBitmap(flat);
        return image.Encode(SKEncodedImageFormat.Jpeg, quality)
               ?? throw new InvalidOperationException("Не удалось сохранить JPEG.");
    }

    /// <summary>Заполнить кадр целиком (cover), обрезка по центру.</summary>
    private static SKBitmap CoverResize(SKBitmap src, int tw, int th)
    {
        float scale = Math.Max((float)tw / src.Width, (float)th / src.Height);
        float sw = src.Width * scale;
        float sh = src.Height * scale;
        float dx = (tw - sw) / 2f;
        float dy = (th - sh) / 2f;

        var dst = new SKBitmap(tw, th, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        canvas.DrawBitmap(src, new SKRect(dx, dy, dx + sw, dy + sh), paint);
        return dst;
    }

    private static SKBitmap Decode(string path)
    {
        var sk = SKBitmap.Decode(path);
        if (sk != null)
            return sk;
        throw new InvalidOperationException("Не удалось прочитать изображение.");
    }

}
