using System.Buffers.Binary;
using SkiaSharp;

namespace Zwmenu.Services;

/// <summary>Отдельные минималистичные иконки для фото, видео и музыки в проводнике.</summary>
public static class FileKindIcons
{
    const int Ver = 2;

    public static string Photo => Ensure("photo", DrawPhoto);
    public static string Video => Ensure("video", DrawVideo);
    public static string Music => Ensure("music", DrawMusic);

    public static string Quoted(string path) => "\"" + path + "\"";

    public static void SelfCheck()
    {
        byte[] head = new byte[6];
        foreach (string path in new[] { Photo, Video, Music })
        {
            if (!File.Exists(path) || new FileInfo(path).Length < 200)
                throw new InvalidOperationException("иконки файлов");
            using var fs = File.OpenRead(path);
            if (fs.Read(head, 0, 6) < 6)
                throw new InvalidOperationException("иконки заголовок");
            if (BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(0, 2)) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(2, 2)) != 1)
                throw new InvalidOperationException("иконки формат");
        }
        if (Photo == Video || Video == Music || Photo == Music)
            throw new InvalidOperationException("иконки разные");
    }

    static string Ensure(string name, Action<SKCanvas, float> draw)
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Zwmenu",
            "icons");
        Directory.CreateDirectory(dir);
        string stamp = Path.Combine(dir, "v" + Ver + ".ok");
        string path = Path.Combine(dir, name + ".ico");
        if (File.Exists(stamp) && File.Exists(path) && new FileInfo(path).Length > 200)
            return path;

        WriteIco(path, draw);
        try { File.WriteAllText(stamp, Ver.ToString()); }
        catch { /* штамп не обязателен */ }
        return path;
    }

    static void WriteIco(string path, Action<SKCanvas, float> draw)
    {
        int[] sizes = { 16, 32, 48, 256 };
        var pngs = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
            pngs[i] = RenderPng(sizes[i], draw);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((ushort)0);
        bw.Write((ushort)1);
        bw.Write((ushort)sizes.Length);

        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
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
        for (int i = 0; i < pngs.Length; i++)
            bw.Write(pngs[i]);

        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, ms.ToArray());
        File.Copy(tmp, path, overwrite: true);
        try { File.Delete(tmp); } catch { /* ок */ }
    }

    static byte[] RenderPng(int size, Action<SKCanvas, float> draw)
    {
        using var bmp = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);
        draw(canvas, size);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    static void DrawPhoto(SKCanvas canvas, float s)
    {
        float r = s * 0.18f;
        using (var paint = Fill(0x12, 0x12, 0x14))
            Round(canvas, s * 0.08f, s * 0.08f, s * 0.84f, s * 0.84f, r, paint);
        using (var paint = Stroke(0x7D, 0xD4, 0xC0, Math.Max(1f, s * 0.05f)))
            Round(canvas, s * 0.18f, s * 0.22f, s * 0.64f, s * 0.52f, s * 0.08f, paint);
        using (var paint = Fill(0x7D, 0xD4, 0xC0))
        {
            canvas.DrawCircle(s * 0.68f, s * 0.34f, s * 0.06f, paint);
            using var path = new SKPath();
            path.MoveTo(s * 0.22f, s * 0.66f);
            path.LineTo(s * 0.40f, s * 0.46f);
            path.LineTo(s * 0.52f, s * 0.58f);
            path.LineTo(s * 0.64f, s * 0.42f);
            path.LineTo(s * 0.78f, s * 0.66f);
            path.Close();
            canvas.DrawPath(path, paint);
        }
    }

    static void DrawVideo(SKCanvas canvas, float s)
    {
        float r = s * 0.18f;
        using (var paint = Fill(0x12, 0x12, 0x14))
            Round(canvas, s * 0.08f, s * 0.08f, s * 0.84f, s * 0.84f, r, paint);
        using (var paint = Stroke(0x7D, 0xD4, 0xC0, Math.Max(1f, s * 0.05f)))
            Round(canvas, s * 0.18f, s * 0.24f, s * 0.64f, s * 0.48f, s * 0.08f, paint);
        using (var paint = Fill(0x7D, 0xD4, 0xC0))
        {
            using var path = new SKPath();
            path.MoveTo(s * 0.40f, s * 0.34f);
            path.LineTo(s * 0.40f, s * 0.62f);
            path.LineTo(s * 0.64f, s * 0.48f);
            path.Close();
            canvas.DrawPath(path, paint);
        }
    }

    static void DrawMusic(SKCanvas canvas, float s)
    {
        float r = s * 0.18f;
        using (var paint = Fill(0x12, 0x12, 0x14))
            Round(canvas, s * 0.08f, s * 0.08f, s * 0.84f, s * 0.84f, r, paint);
        using (var paint = Fill(0x7D, 0xD4, 0xC0))
        {
            float stem = Math.Max(1.5f, s * 0.07f);
            canvas.DrawRoundRect(s * 0.58f, s * 0.22f, stem, s * 0.42f, stem, stem, paint);
            canvas.DrawCircle(s * 0.38f, s * 0.66f, s * 0.12f, paint);
            canvas.DrawCircle(s * 0.62f, s * 0.58f, s * 0.12f, paint);
            using var path = new SKPath();
            path.MoveTo(s * 0.58f + stem, s * 0.22f);
            path.QuadTo(s * 0.78f, s * 0.28f, s * 0.72f, s * 0.42f);
            path.LineTo(s * 0.58f + stem, s * 0.38f);
            path.Close();
            canvas.DrawPath(path, paint);
        }
    }

    static void Round(SKCanvas canvas, float x, float y, float w, float h, float r, SKPaint paint)
        => canvas.DrawRoundRect(x, y, w, h, r, r, paint);

    static SKPaint Fill(byte r, byte g, byte b) => new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Fill,
        Color = new SKColor(r, g, b)
    };

    static SKPaint Stroke(byte r, byte g, byte b, float width) => new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = width,
        StrokeCap = SKStrokeCap.Round,
        StrokeJoin = SKStrokeJoin.Round,
        Color = new SKColor(r, g, b)
    };
}
