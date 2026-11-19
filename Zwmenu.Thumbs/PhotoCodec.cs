using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;
using Svg.Skia;

namespace Zwmenu.Thumbs;

/// <summary>Один файл картинки: кадры, размер и есть ли в нём пустые пиксели.</summary>
public sealed class PhotoDocument : IDisposable
{
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }
    public bool HasAlpha { get; init; }
    public bool Animated { get; init; }
    public bool FramesCut { get; init; }
    public string Kind { get; init; } = "";
    public SKBitmap[] Frames { get; init; } = Array.Empty<SKBitmap>();
    public int[] DelaysMs { get; init; } = Array.Empty<int>();

    public int Width => Frames.Length > 0 ? Frames[0].Width : 0;
    public int Height => Frames.Length > 0 ? Frames[0].Height : 0;

    public void Dispose()
    {
        foreach (var frame in Frames)
            frame.Dispose();
    }
}

/// <summary>Читает распространённые картинки, TGA и QOI. Файл на диске не держит.</summary>
public static class PhotoCodec
{
    public const long MaxBytes = 80L * 1024 * 1024;

    static readonly string[] Extensions =
    {
        ".png", ".apng", ".jpg", ".jpeg", ".jpe", ".jfif", ".pjp", ".pjpeg",
        ".gif", ".bmp", ".dib", ".rle", ".webp", ".wbmp",
        ".svg", ".svgz", ".ico", ".cur", ".tif", ".tiff",
        ".tga", ".targa", ".qoi",
        ".jxr", ".wdp", ".hdp", ".wmp", ".dds",
        ".heic", ".heif", ".hif", ".avif", ".avifs",
        ".dng", ".cr2", ".nef", ".arw", ".orf", ".rw2", ".raf", ".pef", ".srw", ".raw", ".nrw"
    };

    public static string DialogFilter
    {
        get
        {
            var parts = new string[Extensions.Length];
            for (int i = 0; i < Extensions.Length; i++)
                parts[i] = "*" + Extensions[i];
            return "Картинки|" + string.Join(';', parts) + "|Все файлы|*.*";
        }
    }

    public static IReadOnlyList<string> KnownExtensions => Extensions;

    public static bool IsImagePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        string ext = Path.GetExtension(path);
        for (int i = 0; i < Extensions.Length; i++)
        {
            if (ext.Equals(Extensions[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static PhotoDocument Open(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("Файла нет.", path);
        if (info.Length > MaxBytes)
            throw new InvalidDataException("Файл слишком большой.");
        byte[] bytes = File.ReadAllBytes(path);
        return Open(bytes, Path.GetExtension(path));
    }

    public static PhotoDocument Open(byte[] bytes, string extension)
    {
        if (bytes.Length == 0)
            throw new InvalidDataException("Файл пустой.");
        if (IsSvg(bytes, extension))
            return OpenSvg(bytes, extension, ViewEdge);
        if (extension.Equals(".qoi", StringComparison.OrdinalIgnoreCase) || IsQoiMagic(bytes))
            return OpenQoi(bytes);
        if (extension.Equals(".tga", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".targa", StringComparison.OrdinalIgnoreCase))
            return OpenTga(bytes);
        return OpenRaster(bytes, extension);
    }

    public const int ViewEdge = 2048;

    public static SKBitmap? Thumbnail(string path, int edge)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxBytes || edge < 8)
                return null;
            string ext = Path.GetExtension(path);
            if (ext.Equals(".svg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".svgz", StringComparison.OrdinalIgnoreCase))
            {
                byte[] bytes = File.ReadAllBytes(path);
                using var doc = OpenSvg(bytes, ext, edge);
                return doc.Frames.Length == 0 ? null : doc.Frames[0].Copy();
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var codec = SKCodec.Create(stream);
            if (codec == null)
                return null;
            using var still = DecodeStill(codec, edge);
            if (Math.Max(still.Width, still.Height) <= edge)
                return still.Copy();
            return Fit(still, edge);
        }
        catch
        {
            return null;
        }
    }

    public static void SelfCheck()
    {
        using (var png = new SKBitmap(new SKImageInfo(4, 4, SKColorType.Bgra8888, SKAlphaType.Unpremul)))
        {
            png.Erase(SKColors.Transparent);
            png.SetPixel(0, 0, SKColors.Red);
            png.SetPixel(1, 0, new SKColor(0, 0, 255, 0));
            using var data = png.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException("png");
            using var doc = Open(data.ToArray(), ".png");
            if (!doc.HasAlpha || doc.Width != 4 || doc.Kind != "PNG")
                throw new InvalidOperationException("прозрачность png");
        }

        using (var solid = new SKBitmap(new SKImageInfo(4, 4, SKColorType.Bgra8888, SKAlphaType.Opaque)))
        {
            solid.Erase(SKColors.White);
            using var data = solid.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException("png2");
            using var doc = Open(data.ToArray(), ".png");
            if (doc.HasAlpha)
                throw new InvalidOperationException("сплошной png");
        }

        string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"10\"><rect width=\"20\" height=\"10\" fill=\"#ff0000\"/></svg>";
        using (var doc = Open(Encoding.UTF8.GetBytes(svg), ".svg"))
        {
            if (doc.Width < 16 || doc.Height < 8 || doc.Kind != "SVG")
                throw new InvalidOperationException("svg");
        }

        if (!IsImagePath("a.WEBP") || !IsImagePath("a.tga") || !IsImagePath("a.HEIC") || !IsImagePath("a.qoi") || IsImagePath("a.txt"))
            throw new InvalidOperationException("форматы");

        byte[] qoi =
        {
            (byte)'q', (byte)'o', (byte)'i', (byte)'f',
            0, 0, 0, 1, 0, 0, 0, 1, 4, 0,
            0xFF, 255, 0, 0, 255,
            0, 0, 0, 0, 0, 0, 0, 1
        };
        using (var doc = Open(qoi, ".qoi"))
        {
            if (doc.Width != 1 || doc.Height != 1 || doc.Kind != "QOI" || doc.Frames[0].GetPixel(0, 0).Red < 200)
                throw new InvalidOperationException("qoi");
        }

        byte[] tga =
        {
            0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            2, 0, 1, 0, 24, 0x20,
            255, 0, 0,
            0, 0, 255
        };
        using (var doc = Open(tga, ".tga"))
        {
            if (doc.Width != 2 || doc.Height != 1 || doc.Kind != "TGA")
                throw new InvalidOperationException("tga");
            if (doc.Frames[0].GetPixel(0, 0).Blue < 200 || doc.Frames[0].GetPixel(1, 0).Red < 200)
                throw new InvalidOperationException("tga пиксель");
        }

        byte[] gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
        using (var doc = Open(gif, ".gif"))
        {
            if (doc.Width != 1 || doc.Height != 1 || doc.Kind != "GIF")
                throw new InvalidOperationException("gif");
        }

        using (var jpeg = new SKBitmap(new SKImageInfo(32, 16, SKColorType.Bgra8888, SKAlphaType.Opaque)))
        {
            jpeg.Erase(SKColors.White);
            using var data = jpeg.Encode(SKEncodedImageFormat.Jpeg, 80)
                ?? throw new InvalidOperationException("jpeg");
            using var doc = Open(data.ToArray(), ".jpg");
            if (doc.HasAlpha || doc.Kind != "JPEG" || doc.Animated)
                throw new InvalidOperationException("jpeg");
        }

        static void ExpectStrip(SKEncodedOrigin origin, int dw, int dh, bool redFirst)
        {
            var bmp = new SKBitmap(new SKImageInfo(2, 1, SKColorType.Bgra8888, SKAlphaType.Premul));
            bmp.SetPixel(0, 0, SKColors.Red);
            bmp.SetPixel(1, 0, SKColors.Blue);
            using var turned = ApplyOrigin(bmp, origin);
            if (turned.Width != dw || turned.Height != dh)
                throw new InvalidOperationException("размер " + origin);
            SKColor first = turned.GetPixel(0, 0);
            SKColor second = dw == 1 ? turned.GetPixel(0, 1) : turned.GetPixel(1, 0);
            bool ok = redFirst
                ? first.Red > 200 && second.Blue > 200
                : first.Blue > 200 && second.Red > 200;
            if (!ok)
                throw new InvalidOperationException("поворот " + origin);
        }

        ExpectStrip(SKEncodedOrigin.RightTop, 1, 2, true);
        ExpectStrip(SKEncodedOrigin.LeftBottom, 1, 2, false);
        ExpectStrip(SKEncodedOrigin.BottomRight, 2, 1, false);
        ExpectStrip(SKEncodedOrigin.TopRight, 2, 1, false);
        ExpectStrip(SKEncodedOrigin.LeftTop, 1, 2, false);
        ExpectStrip(SKEncodedOrigin.RightBottom, 1, 2, true);

        var square = new SKBitmap(new SKImageInfo(2, 2, SKColorType.Bgra8888, SKAlphaType.Premul));
        square.SetPixel(0, 0, SKColors.Red);
        square.SetPixel(1, 0, SKColors.Lime);
        square.SetPixel(0, 1, SKColors.Blue);
        square.SetPixel(1, 1, SKColors.White);
        var again = square.Copy();
        using (var flipped = ApplyOrigin(square, SKEncodedOrigin.BottomLeft))
        {
            if (flipped.GetPixel(0, 0).Blue < 200 || flipped.GetPixel(0, 1).Red < 200)
                throw new InvalidOperationException("зеркало");
        }

        using (var turned = ApplyOrigin(again, SKEncodedOrigin.RightBottom))
        {
            if (turned.GetPixel(0, 0).Red < 200 || turned.GetPixel(1, 0).Blue < 200 || turned.GetPixel(0, 1).Green < 200)
                throw new InvalidOperationException("поворот зеркала");
        }
    }

    static PhotoDocument OpenSvg(byte[] bytes, string extension, int maxEdge)
    {
        byte[] xml = bytes;
        if (extension.Equals(".svgz", StringComparison.OrdinalIgnoreCase) || LooksGzip(bytes))
        {
            using var input = new MemoryStream(bytes);
            using var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            xml = output.ToArray();
        }

        using var svg = new SKSvg();
        using var stream = new MemoryStream(xml, writable: false);
        SKPicture? picture = svg.Load(stream);
        if (picture == null)
            throw new InvalidDataException("SVG не прочитался.");

        SKRect cull = picture.CullRect;
        float w = cull.Width;
        float h = cull.Height;
        if (w < 1 || h < 1)
            throw new InvalidDataException("В SVG нет размера.");

        float scale = Math.Min(maxEdge / w, maxEdge / h);
        if (scale > 8)
            scale = 8;
        if (scale < 1)
            scale = Math.Min(maxEdge / w, maxEdge / h);
        int dw = Math.Max(1, (int)Math.Round(w * scale));
        int dh = Math.Max(1, (int)Math.Round(h * scale));
        var info = new SKImageInfo(dw, dh, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(scale);
            canvas.Translate(-cull.Left, -cull.Top);
            canvas.DrawPicture(picture);
        }

        return new PhotoDocument
        {
            SourceWidth = Math.Max(1, (int)Math.Round(w)),
            SourceHeight = Math.Max(1, (int)Math.Round(h)),
            HasAlpha = SampleAlpha(bitmap),
            Animated = false,
            Kind = "SVG",
            Frames = new[] { bitmap },
            DelaysMs = new[] { 0 }
        };
    }

    static PhotoDocument OpenRaster(byte[] bytes, string extension)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var codec = SKCodec.Create(stream)
            ?? throw new InvalidDataException("Формат не прочитался.");
        SKImageInfo origin = codec.Info;
        if (origin.Width < 1 || origin.Height < 1)
            throw new InvalidDataException("В файле нет картинки.");

        var format = codec.EncodedFormat;
        if ((format == SKEncodedImageFormat.Gif || format == SKEncodedImageFormat.Webp) && codec.FrameCount > 1)
            return OpenAnimated(codec, origin, extension);

        var bitmap = DecodeStill(codec, ViewEdge);
        int srcW = origin.Width;
        int srcH = origin.Height;
        if (SwapsAxes(codec.EncodedOrigin))
            (srcW, srcH) = (srcH, srcW);

        return new PhotoDocument
        {
            SourceWidth = srcW,
            SourceHeight = srcH,
            HasAlpha = bitmap.AlphaType != SKAlphaType.Opaque && SampleAlpha(bitmap),
            Animated = false,
            Kind = KindName(format, extension),
            Frames = new[] { bitmap },
            DelaysMs = new[] { 0 }
        };
    }

    static PhotoDocument OpenAnimated(SKCodec codec, SKImageInfo origin, string extension)
    {
        int total = Math.Max(1, codec.FrameCount);
        var frameInfo = codec.FrameInfo;
        long one = (long)origin.Width * origin.Height;
        int count = total;
        bool cut = false;
        if (one > 0 && one * count > 12_000_000)
        {
            count = Math.Max(1, (int)(12_000_000 / one));
            cut = count < total;
        }
        count = Math.Min(count, 36);
        if (count < total)
            cut = true;

        var info = new SKImageInfo(origin.Width, origin.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKBitmap(info);
        var frames = new List<SKBitmap>(count);
        var delays = new List<int>(count);
        try
        {
            for (int i = 0; i < count; i++)
            {
                int required = frameInfo.Length > i ? frameInfo[i].RequiredFrame : -1;
                bool prior = false;
                if (required >= 0 && required < frames.Count)
                {
                    canvas.Erase(SKColors.Transparent);
                    using var paint = new SKCanvas(canvas);
                    paint.DrawBitmap(frames[required], 0, 0);
                    prior = true;
                }
                else
                {
                    canvas.Erase(SKColors.Transparent);
                }

                SKCodecResult result = codec.GetPixels(
                    info,
                    canvas.GetPixels(),
                    canvas.RowBytes,
                    new SKCodecOptions(i, prior ? required : -1));
                if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
                {
                    if (frames.Count == 0)
                        throw new InvalidDataException("Кадр не прочитался.");
                    cut = true;
                    break;
                }

                frames.Add(canvas.Copy());
                int delay = frameInfo.Length > i ? frameInfo[i].Duration : 0;
                delays.Add(delay < 20 ? 100 : delay);
            }
        }
        catch
        {
            foreach (var frame in frames)
                frame.Dispose();
            throw;
        }

        return new PhotoDocument
        {
            SourceWidth = origin.Width,
            SourceHeight = origin.Height,
            HasAlpha = frames.Count > 0 && SampleAlpha(frames[0]),
            Animated = frames.Count > 1,
            FramesCut = cut,
            Kind = KindName(codec.EncodedFormat, extension),
            Frames = frames.ToArray(),
            DelaysMs = delays.ToArray()
        };
    }

    static SKBitmap DecodeStill(SKCodec codec, int maxEdge)
    {
        SKImageInfo origin = codec.Info;
        int w = origin.Width;
        int h = origin.Height;
        if (Math.Max(w, h) > maxEdge)
        {
            float desired = maxEdge / (float)Math.Max(w, h);
            SKSizeI dims = codec.GetScaledDimensions(desired);
            if (dims.Width > 0 && dims.Height > 0 && dims.Width <= w && dims.Height <= h)
            {
                w = dims.Width;
                h = dims.Height;
            }
        }

        var alpha = codec.EncodedFormat == SKEncodedImageFormat.Jpeg
            ? SKAlphaType.Opaque
            : SKAlphaType.Premul;
        SKBitmap? bitmap = TryPixels(codec, w, h, alpha, SKCodecOptions.Default);
        if (bitmap == null && alpha == SKAlphaType.Opaque)
            bitmap = TryPixels(codec, w, h, SKAlphaType.Premul, SKCodecOptions.Default);
        if (bitmap == null && (w != origin.Width || h != origin.Height))
            bitmap = TryPixels(codec, origin.Width, origin.Height, SKAlphaType.Premul, SKCodecOptions.Default);
        if (bitmap == null)
            throw new InvalidDataException("Кадр не прочитался.");

        if (Math.Max(bitmap.Width, bitmap.Height) > maxEdge)
        {
            SKBitmap fitted = Fit(bitmap, maxEdge);
            bitmap.Dispose();
            bitmap = fitted;
        }

        return ApplyOrigin(bitmap, codec.EncodedOrigin);
    }

    static SKBitmap? TryPixels(SKCodec codec, int width, int height, SKAlphaType alpha, SKCodecOptions options)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, alpha);
        var bitmap = new SKBitmap(info);
        SKCodecResult result = codec.GetPixels(info, bitmap.GetPixels(), bitmap.RowBytes, options);
        if (result == SKCodecResult.Success || result == SKCodecResult.IncompleteInput)
            return bitmap;
        bitmap.Dispose();
        return null;
    }

    static bool SwapsAxes(SKEncodedOrigin origin) =>
        origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    static SKBitmap ApplyOrigin(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
            return bitmap;

        bool swap = SwapsAxes(origin);
        int dw = swap ? bitmap.Height : bitmap.Width;
        int dh = swap ? bitmap.Width : bitmap.Height;
        var turned = new SKBitmap(dw, dh, bitmap.ColorType, bitmap.AlphaType);
        using (var canvas = new SKCanvas(turned))
        {
            canvas.Clear(SKColors.Transparent);
            switch (origin)
            {
                case SKEncodedOrigin.TopRight:
                    canvas.Translate(bitmap.Width, 0);
                    canvas.Scale(-1, 1);
                    break;
                case SKEncodedOrigin.BottomRight:
                    canvas.Translate(bitmap.Width, bitmap.Height);
                    canvas.RotateDegrees(180);
                    break;
                case SKEncodedOrigin.BottomLeft:
                    canvas.Translate(0, bitmap.Height);
                    canvas.Scale(1, -1);
                    break;
                case SKEncodedOrigin.LeftTop:
                    canvas.Translate(bitmap.Height, bitmap.Width);
                    canvas.RotateDegrees(90);
                    canvas.Scale(-1, 1);
                    break;
                case SKEncodedOrigin.RightTop:
                    canvas.Translate(bitmap.Height, 0);
                    canvas.RotateDegrees(90);
                    break;
                case SKEncodedOrigin.RightBottom:
                    canvas.SetMatrix(new SKMatrix
                    {
                        ScaleX = 0,
                        SkewX = 1,
                        TransX = 0,
                        SkewY = 1,
                        ScaleY = 0,
                        TransY = 0,
                        Persp2 = 1
                    });
                    break;
                case SKEncodedOrigin.LeftBottom:
                    canvas.Translate(0, bitmap.Width);
                    canvas.RotateDegrees(-90);
                    break;
            }
            canvas.DrawBitmap(bitmap, 0, 0);
        }

        bitmap.Dispose();
        return turned;
    }

    public static SKBitmap Fit(SKBitmap source, int edge)
    {
        float scale = Math.Min(edge / (float)source.Width, edge / (float)source.Height);
        if (scale > 1)
            scale = 1;
        int dw = Math.Max(1, (int)Math.Round(source.Width * scale));
        int dh = Math.Max(1, (int)Math.Round(source.Height * scale));
        var info = new SKImageInfo(dw, dh, SKColorType.Bgra8888, SKAlphaType.Premul);
        var scaled = source.Resize(info, SKFilterQuality.Medium);
        return scaled ?? throw new InvalidDataException("Эскиз не собрался.");
    }

    public static SKBitmap Rotate(SKBitmap source)
    {
        var info = new SKImageInfo(source.Height, source.Width, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(bitmap.Width, 0);
        canvas.RotateDegrees(90);
        canvas.DrawBitmap(source, 0, 0);
        return bitmap;
    }

    public static void SavePng(SKBitmap source, string path)
    {
        using var image = SKImage.FromBitmap(source);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidDataException("Не сохранилось.");
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    static bool SampleAlpha(SKBitmap bitmap)
    {
        if (bitmap.AlphaType == SKAlphaType.Opaque)
            return false;
        int w = bitmap.Width;
        int h = bitmap.Height;
        int stride = bitmap.RowBytes;
        IntPtr pixels = bitmap.GetPixels();
        if (pixels == IntPtr.Zero || stride < 4)
            return false;
        int stepX = Math.Max(1, w / 80);
        int stepY = Math.Max(1, h / 80);
        for (int y = 0; y < h; y += stepY)
        {
            IntPtr row = pixels + y * stride;
            for (int x = 0; x < w; x += stepX)
            {
                if (Marshal.ReadByte(row, x * 4 + 3) < 250)
                    return true;
            }
        }
        return false;
    }

    static bool IsSvg(byte[] bytes, string extension)
    {
        if (extension.Equals(".svg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".svgz", StringComparison.OrdinalIgnoreCase))
            return true;
        if (LooksGzip(bytes))
            return false;
        int n = Math.Min(bytes.Length, 400);
        string head = Encoding.UTF8.GetString(bytes, 0, n).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
            || (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && head.Contains("<svg", StringComparison.OrdinalIgnoreCase));
    }

    static bool LooksGzip(byte[] bytes) => bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B;

    static bool IsQoiMagic(byte[] bytes) =>
        bytes.Length > 4 && bytes[0] == (byte)'q' && bytes[1] == (byte)'o' && bytes[2] == (byte)'i' && bytes[3] == (byte)'f';

    static PhotoDocument OpenQoi(byte[] bytes)
    {
        if (!IsQoiMagic(bytes) || bytes.Length < 14)
            throw new InvalidDataException("Это не QOI.");
        int width = (bytes[4] << 24) | (bytes[5] << 16) | (bytes[6] << 8) | bytes[7];
        int height = (bytes[8] << 24) | (bytes[9] << 16) | (bytes[10] << 8) | bytes[11];
        if (width < 1 || height < 1 || (long)width * height > 40_000_000)
            throw new InvalidDataException("QOI странного размера.");

        var seen = new (byte R, byte G, byte B, byte A)[64];
        byte r = 0, g = 0, b = 0, a = 255;
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        int stride = bitmap.RowBytes;
        byte[] row = new byte[stride];
        int p = 14;
        int run = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (run > 0)
                {
                    run--;
                }
                else
                {
                    if (p >= bytes.Length)
                    {
                        bitmap.Dispose();
                        throw new InvalidDataException("QOI обрезан.");
                    }

                    byte tag = bytes[p++];
                    if (tag == 0xFE)
                    {
                        if (p + 3 > bytes.Length)
                        {
                            bitmap.Dispose();
                            throw new InvalidDataException("QOI обрезан.");
                        }
                        r = bytes[p++];
                        g = bytes[p++];
                        b = bytes[p++];
                    }
                    else if (tag == 0xFF)
                    {
                        if (p + 4 > bytes.Length)
                        {
                            bitmap.Dispose();
                            throw new InvalidDataException("QOI обрезан.");
                        }
                        r = bytes[p++];
                        g = bytes[p++];
                        b = bytes[p++];
                        a = bytes[p++];
                    }
                    else if ((tag & 0xC0) == 0x00)
                    {
                        var color = seen[tag & 0x3F];
                        r = color.R;
                        g = color.G;
                        b = color.B;
                        a = color.A;
                    }
                    else if ((tag & 0xC0) == 0x40)
                    {
                        r = (byte)(r + ((tag >> 4) & 3) - 2);
                        g = (byte)(g + ((tag >> 2) & 3) - 2);
                        b = (byte)(b + (tag & 3) - 2);
                    }
                    else if ((tag & 0xC0) == 0x80)
                    {
                        int dg = (tag & 0x3F) - 32;
                        if (p >= bytes.Length)
                        {
                            bitmap.Dispose();
                            throw new InvalidDataException("QOI обрезан.");
                        }
                        byte next = bytes[p++];
                        r = (byte)(r + dg + ((next >> 4) & 0xF) - 8);
                        g = (byte)(g + dg);
                        b = (byte)(b + dg + (next & 0xF) - 8);
                    }
                    else
                    {
                        run = tag & 0x3F;
                    }

                    seen[(r * 3 + g * 5 + b * 7 + a * 11) % 64] = (r, g, b, a);
                }

                int o = x * 4;
                row[o] = (byte)(b * a / 255);
                row[o + 1] = (byte)(g * a / 255);
                row[o + 2] = (byte)(r * a / 255);
                row[o + 3] = a;
            }

            Marshal.Copy(row, 0, bitmap.GetPixels() + y * stride, stride);
        }

        return Still("QOI", bitmap, width, height);
    }

    static PhotoDocument OpenTga(byte[] bytes)
    {
        if (bytes.Length < 18)
            throw new InvalidDataException("TGA слишком короткий.");
        int idLen = bytes[0];
        int mapType = bytes[1];
        int imageType = bytes[2];
        int mapLen = bytes[5] | (bytes[6] << 8);
        int mapBits = bytes[7];
        int width = bytes[12] | (bytes[13] << 8);
        int height = bytes[14] | (bytes[15] << 8);
        int depth = bytes[16];
        int desc = bytes[17];
        bool top = (desc & 0x20) != 0;
        bool rle = imageType is 9 or 10 or 11;
        int kind = imageType switch
        {
            9 => 1,
            10 => 2,
            11 => 3,
            _ => imageType
        };
        if (width < 1 || height < 1 || width > 16000 || height > 16000)
            throw new InvalidDataException("TGA странного размера.");
        if (kind is not (1 or 2 or 3))
            throw new InvalidDataException("Этот вид TGA не читается.");
        if (kind == 1 && (mapType != 1 || mapLen < 1))
            throw new InvalidDataException("В TGA нет палитры.");
        int pixelBytes = Math.Max(1, depth / 8);
        if (pixelBytes > 4)
            throw new InvalidDataException("Глубина TGA не та.");

        int pos = 18 + idLen;
        byte[] palette = Array.Empty<byte>();
        int mapEntry = Math.Max(1, (mapBits + 7) / 8);
        if (mapType == 1)
        {
            int mapBytes = mapLen * mapEntry;
            if (pos < 0 || pos + mapBytes > bytes.Length)
                throw new InvalidDataException("Палитра TGA обрезана.");
            palette = new byte[mapBytes];
            Buffer.BlockCopy(bytes, pos, palette, 0, mapBytes);
            pos += mapBytes;
        }

        var pix = new byte[width * height * 4];
        int written = 0;
        int total = width * height;
        byte[] hold = new byte[4];
        while (written < total)
        {
            int run = 1;
            bool repeat = false;
            if (rle)
            {
                if (pos >= bytes.Length)
                    break;
                byte pack = bytes[pos++];
                run = (pack & 0x7F) + 1;
                repeat = (pack & 0x80) != 0;
            }

            if (repeat && !TakePixel(bytes, ref pos, pixelBytes, hold))
                break;

            for (int n = 0; n < run && written < total; n++)
            {
                if (!repeat && !TakePixel(bytes, ref pos, pixelBytes, hold))
                    goto done;
                int x = written % width;
                int y = written / width;
                if (!top)
                    y = height - 1 - y;
                DecodeTgaPixel(hold, depth, kind, palette, mapBits, out byte pb, out byte pg, out byte pr, out byte pa);
                int o = (y * width + x) * 4;
                pix[o] = (byte)(pb * pa / 255);
                pix[o + 1] = (byte)(pg * pa / 255);
                pix[o + 2] = (byte)(pr * pa / 255);
                pix[o + 3] = pa;
                written++;
            }
        }

        done:
        if (written < total)
            throw new InvalidDataException("TGA обрезан.");

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        int stride = bitmap.RowBytes;
        for (int y = 0; y < height; y++)
            Marshal.Copy(pix, y * width * 4, bitmap.GetPixels() + y * stride, width * 4);

        return Still("TGA", bitmap, width, height);
    }

    static bool TakePixel(byte[] bytes, ref int pos, int pixelBytes, byte[] hold)
    {
        if (pos + pixelBytes > bytes.Length)
            return false;
        for (int i = 0; i < pixelBytes; i++)
            hold[i] = bytes[pos++];
        for (int i = pixelBytes; i < hold.Length; i++)
            hold[i] = 0;
        return true;
    }

    static void DecodeTgaPixel(byte[] px, int depth, int kind, byte[] palette, int mapBits, out byte b, out byte g, out byte r, out byte a)
    {
        a = 255;
        if (kind == 3)
        {
            b = g = r = px[0];
            return;
        }

        if (kind == 1)
        {
            int index = depth >= 16 ? px[0] | (px[1] << 8) : px[0];
            int entry = Math.Max(1, (mapBits + 7) / 8);
            int at = index * entry;
            if (at < 0 || at + Math.Min(entry, 3) > palette.Length)
            {
                b = g = r = 0;
                a = 0;
                return;
            }
            b = palette[at];
            g = entry > 1 ? palette[at + 1] : b;
            r = entry > 2 ? palette[at + 2] : g;
            if (entry > 3)
                a = palette[at + 3];
            return;
        }

        if (depth == 16)
        {
            int v = px[0] | (px[1] << 8);
            b = (byte)((v & 0x1F) << 3);
            g = (byte)(((v >> 5) & 0x1F) << 3);
            r = (byte)(((v >> 10) & 0x1F) << 3);
            return;
        }

        b = px[0];
        g = px[1];
        r = depth >= 24 ? px[2] : px[1];
        if (depth == 32)
            a = px[3];
    }

    static PhotoDocument Still(string kind, SKBitmap bitmap, int width, int height) => new()
    {
        SourceWidth = width,
        SourceHeight = height,
        HasAlpha = bitmap.AlphaType != SKAlphaType.Opaque && SampleAlpha(bitmap),
        Animated = false,
        Kind = kind,
        Frames = new[] { bitmap },
        DelaysMs = new[] { 0 }
    };

    static string KindName(SKEncodedImageFormat format, string extension)
    {
        return format switch
        {
            SKEncodedImageFormat.Png => "PNG",
            SKEncodedImageFormat.Jpeg => "JPEG",
            SKEncodedImageFormat.Gif => "GIF",
            SKEncodedImageFormat.Webp => "WEBP",
            SKEncodedImageFormat.Bmp => "BMP",
            SKEncodedImageFormat.Ico => "ICO",
            SKEncodedImageFormat.Wbmp => "WBMP",
            SKEncodedImageFormat.Heif => "HEIF",
            SKEncodedImageFormat.Avif => "AVIF",
            SKEncodedImageFormat.Dng => "DNG",
            _ => extension.TrimStart('.').ToUpperInvariant() switch
            {
                "TIF" or "TIFF" => "TIFF",
                "SVG" or "SVGZ" => "SVG",
                "TGA" or "TARGA" => "TGA",
                "QOI" => "QOI",
                "JXR" or "WDP" or "HDP" or "WMP" => "JPEG XR",
                "DDS" => "DDS",
                "HEIC" or "HEIF" or "HIF" => "HEIF",
                "AVIF" or "AVIFS" => "AVIF",
                "CUR" => "CUR",
                "" => "КАДР",
                var other => other
            }
        };
    }
}
