using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Zwmenu.Services;

public sealed record ImageConvertFormat(string Id, string Title, string Extension);

/// <param name="Scale">Множитель сторон (0.7 = 70% ширины/высоты).</param>
/// <param name="MaxEdge">Жёсткий потолок длинной стороны в пикселях.</param>
public sealed record ImageCompressPreset(
    string Id,
    string Title,
    string Hint,
    int WebpQuality,
    int JpegQuality,
    double Scale,
    double MaxEdge);

/// <summary>Конвертация и умное сжатие изображений.</summary>
public static class ImageConvertService
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".gif", ".bmp", ".tif", ".tiff",
        ".webp", ".tga", ".ico", ".heic", ".heif", ".avif", ".wdp", ".hdp"
    };

    public static IReadOnlyList<ImageConvertFormat> Formats { get; } =
    [
        new("jpg", "JPG", ".jpg"),
        new("png", "PNG", ".png"),
        new("webp", "WebP", ".webp"),
        new("gif", "GIF", ".gif"),
        new("bmp", "BMP", ".bmp"),
        new("tiff", "TIFF", ".tiff"),
        new("tga", "TGA", ".tga"),
        new("ico", "ICO", ".ico")
    ];

    /// <summary>
    /// Умное сжатие: уменьшает пиксели + качество кодека.
    /// Режимы заметно отличаются по весу (не просто «пересохранить»).
    /// </summary>
    public static IReadOnlyList<ImageCompressPreset> CompressPresets { get; } =
    [
        new("soft", "Лёгкое", "Чуть легче, почти как оригинал", 88, 88, 0.88, 2200),
        new("normal", "Норма", "Для сайтов и чатов", 72, 74, 0.68, 1600),
        new("strong", "Сильное", "Сильно легче, для жёстких лимитов", 55, 58, 0.48, 1280)
    ];

    public static bool IsImageFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;
        return ImageExtensions.Contains(Path.GetExtension(path));
    }

    public static IReadOnlyList<string> FilterImages(IEnumerable<string> paths) =>
        paths.Where(IsImageFile).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static (string? OutputPath, string? Error) Convert(string sourcePath, string formatId)
    {
        var fmt = Formats.FirstOrDefault(f =>
            string.Equals(f.Id, formatId, StringComparison.OrdinalIgnoreCase));
        if (fmt == null)
            return (null, "Неизвестный формат.");
        if (!IsImageFile(sourcePath))
            return (null, "Это не изображение.");

        try
        {
            string dir = Path.GetDirectoryName(sourcePath) ?? "";
            string name = Path.GetFileNameWithoutExtension(sourcePath);
            string dest = FileOutPath.NextFree(Path.Combine(dir, name + fmt.Extension));

            switch (fmt.Id)
            {
                case "webp":
                    EncodeWebp(sourcePath, dest, 90, maxEdge: null);
                    break;
                case "ico":
                    WriteIco(sourcePath, dest);
                    break;
                case "tga":
                    using (var bmp = LoadBitmap(sourcePath))
                        WriteTga(bmp, dest);
                    break;
                case "jpg":
                    using (var bmp = LoadBitmap(sourcePath))
                    using (var flat = FlattenOnWhite(bmp))
                        SaveJpeg(flat, dest, 92L);
                    break;
                default:
                {
                    using var bmp = LoadBitmap(sourcePath);
                    ImageFormat gdi = fmt.Id switch
                    {
                        "png" => ImageFormat.Png,
                        "gif" => ImageFormat.Gif,
                        "bmp" => ImageFormat.Bmp,
                        "tiff" => ImageFormat.Tiff,
                        _ => ImageFormat.Png
                    };
                    bmp.Save(dest, gdi);
                    break;
                }
            }

            return (dest, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    public static (string? OutputPath, string? Error) Compress(string sourcePath, string presetId)
    {
        var preset = CompressPresets.FirstOrDefault(p =>
            string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase));
        if (preset == null)
            return (null, "Неизвестный режим сжатия.");
        if (!IsImageFile(sourcePath))
            return (null, "Это не изображение.");

        try
        {
            string dir = Path.GetDirectoryName(sourcePath) ?? "";
            string name = Path.GetFileNameWithoutExtension(sourcePath);
            // Тот же формат, что у исходника: PNG → PNG, JPG → JPG и т.д.
            string ext = NormalizeCompressExtension(Path.GetExtension(sourcePath));
            string dest = FileOutPath.NextFree(Path.Combine(dir, name + " сжато" + ext));
            long before = new FileInfo(sourcePath).Length;
            EncodeCompressed(sourcePath, dest, ext, preset);

            // Если почти не похудело — дожимаем сильнее (частый кейс: уже сжатый JPG/PNG).
            if (File.Exists(dest) && new FileInfo(dest).Length >= before * 0.92)
            {
                var harder = HarderPreset(preset);
                EncodeCompressed(sourcePath, dest, ext, harder);
            }

            return (dest, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private static ImageCompressPreset HarderPreset(ImageCompressPreset p) =>
        p with
        {
            WebpQuality = Math.Max(40, p.WebpQuality - 18),
            JpegQuality = Math.Max(42, p.JpegQuality - 18),
            Scale = Math.Clamp(p.Scale * 0.78, 0.35, 0.95),
            MaxEdge = Math.Max(720, p.MaxEdge * 0.75)
        };

    /// <summary>Сжатие без смены формата. Неизвестные/тяжёлые (HEIC…) → PNG.</summary>
    private static string NormalizeCompressExtension(string ext)
    {
        ext = (ext ?? "").ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" or ".jpe" or ".jfif" => ".jpg",
            ".png" => ".png",
            ".webp" => ".webp",
            ".gif" => ".gif",
            ".bmp" => ".bmp",
            ".tif" or ".tiff" => ".tiff",
            ".tga" => ".tga",
            ".ico" => ".ico",
            _ => ".png"
        };
    }

    private static void EncodeCompressed(string sourcePath, string dest, string ext, ImageCompressPreset preset)
    {
        switch (ext)
        {
            case ".jpg":
                using (var bmp = LoadBitmap(sourcePath))
                using (var sized = ResizeGdi(bmp, preset.Scale, preset.MaxEdge))
                using (var flat = FlattenOnWhite(sized))
                    SaveJpeg(flat, dest, preset.JpegQuality);
                break;
            case ".webp":
                EncodeSkiaSized(sourcePath, dest, SKEncodedImageFormat.Webp, preset.WebpQuality, preset);
                break;
            case ".png":
                // PNG без потерь сам по себе почти не худеет — режем пиксели + для «Норма/Сильное»
                // слегка «смягчаем» (это даёт заметно меньший файл при том же расширении).
                EncodePngCompressed(sourcePath, dest, preset);
                break;
            case ".gif":
                using (var bmp = LoadBitmap(sourcePath))
                using (var sized = ResizeGdi(bmp, preset.Scale, preset.MaxEdge))
                    sized.Save(dest, ImageFormat.Gif);
                break;
            case ".bmp":
                using (var bmp = LoadBitmap(sourcePath))
                using (var sized = ResizeGdi(bmp, preset.Scale, preset.MaxEdge))
                    sized.Save(dest, ImageFormat.Bmp);
                break;
            case ".tiff":
                using (var bmp = LoadBitmap(sourcePath))
                using (var sized = ResizeGdi(bmp, preset.Scale, preset.MaxEdge))
                    sized.Save(dest, ImageFormat.Tiff);
                break;
            case ".tga":
                using (var bmp = LoadBitmap(sourcePath))
                using (var sized = ResizeGdi(bmp, preset.Scale, preset.MaxEdge))
                    WriteTga(sized, dest);
                break;
            case ".ico":
                WriteIco(sourcePath, dest, preset);
                break;
            default:
                EncodePngCompressed(sourcePath, dest, preset);
                break;
        }
    }

    private static void EncodePngCompressed(string sourcePath, string dest, ImageCompressPreset preset)
    {
        using var sk = DecodeSkia(sourcePath);
        using var sized = ResizeSkiaTo(sk, preset.Scale, preset.MaxEdge);

        // JPEG-промежуток убивает альфу — только для полностью непрозрачных PNG.
        // С прозрачностью: только ресайз (вес всё равно падает за счёт пикселей).
        if ((preset.Id is "normal" or "strong") && !HasTransparency(sized))
        {
            int q = preset.Id == "strong" ? preset.JpegQuality : Math.Min(82, preset.JpegQuality + 6);
            using var image = SKImage.FromBitmap(sized);
            using var jpg = image.Encode(SKEncodedImageFormat.Jpeg, q)
                ?? throw new InvalidOperationException("Не удалось сжать PNG.");
            using var decoded = SKBitmap.Decode(jpg)
                ?? throw new InvalidOperationException("Не удалось сжать PNG.");
            using var outImg = SKImage.FromBitmap(decoded);
            using var png = outImg.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException("Не удалось сохранить PNG.");
            using var fs = File.Create(dest);
            png.SaveTo(fs);
            return;
        }

        using (var image = SKImage.FromBitmap(sized))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100)
               ?? throw new InvalidOperationException("Не удалось сохранить PNG."))
        using (var fs = File.Create(dest))
            data.SaveTo(fs);
    }

    private static unsafe bool HasTransparency(SKBitmap bmp)
    {
        if (bmp.AlphaType == SKAlphaType.Opaque)
            return false;

        IntPtr ptr = bmp.GetPixels();
        if (ptr == IntPtr.Zero)
            return false;

        int w = bmp.Width;
        int h = bmp.Height;
        int stride = bmp.RowBytes;
        // Bgra8888 / Rgba8888: alpha на +3
        byte* basePtr = (byte*)ptr;
        int step = Math.Max(1, Math.Min(w, h) / 64); // достаточно сэмпла
        for (int y = 0; y < h; y += step)
        {
            byte* row = basePtr + y * stride;
            for (int x = 0; x < w; x += step)
            {
                if (row[x * 4 + 3] < 250)
                    return true;
            }
        }
        return false;
    }

    private static void EncodeSkiaSized(
        string sourcePath, string dest, SKEncodedImageFormat format, int quality, ImageCompressPreset preset)
    {
        using var sk = DecodeSkia(sourcePath);
        using var sized = ResizeSkiaTo(sk, preset.Scale, preset.MaxEdge);
        using var image = SKImage.FromBitmap(sized);
        using var data = image.Encode(format, quality)
            ?? throw new InvalidOperationException("Не удалось сохранить изображение.");
        using var fs = File.Create(dest);
        data.SaveTo(fs);
    }

    private static void EncodeWebp(string sourcePath, string dest, int quality, double? maxEdge)
    {
        using var sk = DecodeSkia(sourcePath);
        double scale = 1.0;
        double edge = maxEdge ?? Math.Max(sk.Width, sk.Height);
        using var sized = ResizeSkiaTo(sk, scale, edge);
        using var image = SKImage.FromBitmap(sized);
        using var data = image.Encode(SKEncodedImageFormat.Webp, quality)
            ?? throw new InvalidOperationException("Не удалось закодировать WebP.");
        using var fs = File.Create(dest);
        data.SaveTo(fs);
    }

    private static SKBitmap ResizeSkiaTo(SKBitmap src, double scale, double maxEdge)
    {
        double edge = Math.Max(src.Width, src.Height);
        double targetEdge = Math.Min(edge * scale, maxEdge);
        if (targetEdge >= edge - 0.5)
            return src.Copy();

        double s = targetEdge / edge;
        int w = Math.Max(1, (int)Math.Round(src.Width * s));
        int h = Math.Max(1, (int)Math.Round(src.Height * s));
        var dst = new SKBitmap(w, h, src.ColorType, src.AlphaType);
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        canvas.DrawBitmap(src, new SKRect(0, 0, w, h), paint);
        return dst;
    }

    private static Bitmap ResizeGdi(Bitmap src, double scale, double maxEdge)
    {
        double edge = Math.Max(src.Width, src.Height);
        double targetEdge = Math.Min(edge * scale, maxEdge);
        if (targetEdge >= edge - 0.5)
            return (Bitmap)src.Clone();

        double s = targetEdge / edge;
        int w = Math.Max(1, (int)Math.Round(src.Width * s));
        int h = Math.Max(1, (int)Math.Round(src.Height * s));
        var dst = new Bitmap(w, h, src.PixelFormat == PixelFormat.Format24bppRgb
            ? PixelFormat.Format24bppRgb
            : PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.DrawImage(src, 0, 0, w, h);
        return dst;
    }

    private static void WriteIco(string sourcePath, string dest, ImageCompressPreset? compress = null)
    {
        // PNG-compressed ICO (Vista+): 16 / 32 / 48 / 256.
        int[] sizes = [16, 32, 48, 256];
        using var raw = DecodeSkia(sourcePath);
        SKBitmap? scaled = compress != null
            ? ResizeSkiaTo(raw, compress.Scale, compress.MaxEdge)
            : null;
        try
        {
            SKBitmap src = scaled ?? raw;
            var pngs = new List<byte[]>();
            foreach (int size in sizes)
            {
                using var square = MakeSquare(src, size);
                using var img = SKImage.FromBitmap(square);
                using var data = img.Encode(SKEncodedImageFormat.Png, 100)
                    ?? throw new InvalidOperationException("Не удалось сохранить ICO.");
                pngs.Add(data.ToArray());
            }

            using var fs = File.Create(dest);
            using var bw = new BinaryWriter(fs);
            bw.Write((ushort)0); // reserved
            bw.Write((ushort)1); // icon
            bw.Write((ushort)pngs.Count);

            int offset = 6 + 16 * pngs.Count;
            for (int i = 0; i < pngs.Count; i++)
            {
                int size = sizes[i];
                bw.Write((byte)(size >= 256 ? 0 : size));
                bw.Write((byte)(size >= 256 ? 0 : size));
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((ushort)1);
                bw.Write((ushort)32);
                bw.Write(pngs[i].Length);
                bw.Write(offset);
                offset += pngs[i].Length;
            }

            foreach (var png in pngs)
                bw.Write(png);
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    private static SKBitmap MakeSquare(SKBitmap src, int size)
    {
        var dst = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.Transparent);
        float scale = Math.Min((float)size / src.Width, (float)size / src.Height);
        float w = src.Width * scale;
        float h = src.Height * scale;
        float x = (size - w) / 2f;
        float y = (size - h) / 2f;
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        canvas.DrawBitmap(src, new SKRect(x, y, x + w, y + h), paint);
        return dst;
    }

    private static SKBitmap DecodeSkia(string path)
    {
        var sk = SKBitmap.Decode(path);
        if (sk != null)
            return sk;
        // GDI fallback → skia
        using var bmp = LoadBitmap(path);
        return BitmapToSkia(bmp);
    }

    private static SKBitmap BitmapToSkia(Bitmap bmp)
    {
        using var clone = bmp.PixelFormat == PixelFormat.Format32bppArgb
            ? (Bitmap)bmp.Clone()
            : bmp.Clone(new Rectangle(0, 0, bmp.Width, bmp.Height), PixelFormat.Format32bppArgb);
        var data = clone.LockBits(
            new Rectangle(0, 0, clone.Width, clone.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            var sk = new SKBitmap(clone.Width, clone.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            int bytes = Math.Abs(data.Stride) * clone.Height;
            byte[] buf = new byte[bytes];
            Marshal.Copy(data.Scan0, buf, 0, bytes);
            Marshal.Copy(buf, 0, sk.GetPixels(), Math.Min(bytes, sk.ByteCount));
            return sk;
        }
        finally
        {
            clone.UnlockBits(data);
        }
    }

    private static Bitmap LoadBitmap(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        try
        {
            using var ms = new MemoryStream(bytes);
            using var temp = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false);
            return new Bitmap(temp);
        }
        catch
        {
            using var sk = SKBitmap.Decode(bytes)
                ?? throw new InvalidOperationException("Не удалось прочитать изображение.");
            return SkiaToBitmap(sk);
        }
    }

    private static Bitmap SkiaToBitmap(SKBitmap sk)
    {
        var bmp = new Bitmap(sk.Width, sk.Height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(
            new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            IntPtr src = sk.GetPixels();
            int bytes = Math.Abs(data.Stride) * bmp.Height;
            byte[] buf = new byte[bytes];
            Marshal.Copy(src, buf, 0, Math.Min(bytes, sk.ByteCount));
            Marshal.Copy(buf, 0, data.Scan0, bytes);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }

    private static Bitmap FlattenOnWhite(Bitmap src)
    {
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.Clear(System.Drawing.Color.White);
        g.DrawImage(src, 0, 0, src.Width, src.Height);
        return dst;
    }

    private static void SaveJpeg(Bitmap bmp, string path, long quality)
    {
        var encoder = ImageCodecInfo.GetImageEncoders()
            .First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        bmp.Save(path, encoder, ep);
    }

    private static void WriteTga(Bitmap bmp, string path)
    {
        int w = bmp.Width;
        int h = bmp.Height;
        using var clone = bmp.PixelFormat == PixelFormat.Format32bppArgb
            ? (Bitmap)bmp.Clone()
            : bmp.Clone(new Rectangle(0, 0, w, h), PixelFormat.Format32bppArgb);

        var data = clone.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            using var fs = File.Create(path);
            using var bw = new BinaryWriter(fs);
            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write((byte)2);
            bw.Write((ushort)0);
            bw.Write((ushort)0);
            bw.Write((byte)0);
            bw.Write((ushort)0);
            bw.Write((ushort)0);
            bw.Write((ushort)w);
            bw.Write((ushort)h);
            bw.Write((byte)32);
            bw.Write((byte)0x28);

            int stride = Math.Abs(data.Stride);
            byte[] row = new byte[stride];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, stride);
                bw.Write(row, 0, w * 4);
            }
        }
        finally
        {
            clone.UnlockBits(data);
        }
    }
}
