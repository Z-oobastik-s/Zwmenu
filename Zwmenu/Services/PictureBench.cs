using System.Drawing.Imaging;
using System.IO;
using Draw = System.Drawing;

namespace Zwmenu.Services;

/// <summary>Лист, обрезка, поворот. Гифку и ролик собирает ClipBench через ffmpeg.</summary>
public static class PictureBench
{
    public static (string? Path, string? Error) Rotate(string source, Draw.RotateFlipType flip)
    {
        try
        {
            using var image = Load(source);
            image.RotateFlip(flip);
            string dest = Beside(source, "поворот", ExtOrPng(source));
            Save(image, dest);
            return (dest, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    public static (string? Path, string? Error) Crop(string source, Draw.Rectangle rect)
    {
        try
        {
            using var image = Load(source);
            int x = Math.Clamp(rect.X, 0, Math.Max(0, image.Width - 1));
            int y = Math.Clamp(rect.Y, 0, Math.Max(0, image.Height - 1));
            int w = Math.Clamp(rect.Width, 1, image.Width - x);
            int h = Math.Clamp(rect.Height, 1, image.Height - y);
            if (w < 2 || h < 2)
                return (null, "Рамка слишком маленькая.");
            using var cut = image.Clone(new Draw.Rectangle(x, y, w, h), image.PixelFormat);
            string dest = Beside(source, "обрезка", ".png");
            Save(cut, dest);
            return (dest, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    public static (string? Path, string? Error) Sheet(IReadOnlyList<string> files, int columns)
    {
        var list = files.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToList();
        if (list.Count == 0)
            return (null, "Нет картинок.");
        int cols = Math.Clamp(columns, 1, 8);
        const int cellW = 420;
        const int cellH = 300;
        const int caption = 22;
        const int gap = 16;
        int rows = (int)Math.Ceiling(list.Count / (double)cols);
        int width = cols * cellW + (cols + 1) * gap;
        int height = rows * (cellH + caption) + (rows + 1) * gap;
        try
        {
            using var sheet = new Draw.Bitmap(width, height);
            using var g = Draw.Graphics.FromImage(sheet);
            g.Clear(Draw.Color.White);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            using var font = new Draw.Font("Segoe UI", 11f);
            using var brush = new Draw.SolidBrush(Draw.Color.FromArgb(40, 40, 40));
            int drawn = 0;
            for (int i = 0; i < list.Count; i++)
            {
                int col = i % cols;
                int row = i / cols;
                int x = gap + col * (cellW + gap);
                int y = gap + row * (cellH + caption + gap);
                try
                {
                    using var image = Load(list[i]);
                    var box = Fit(image.Width, image.Height, cellW, cellH);
                    int dx = x + (cellW - box.Width) / 2;
                    int dy = y + (cellH - box.Height) / 2;
                    g.DrawImage(image, new Draw.Rectangle(dx, dy, box.Width, box.Height));
                    drawn++;
                }
                catch
                {
                    g.FillRectangle(Draw.Brushes.Gainsboro, x, y, cellW, cellH);
                }

                g.DrawString(Path.GetFileName(list[i]), font, brush, new Draw.RectangleF(x, y + cellH + 2, cellW, caption));
            }

            if (drawn == 0)
                return (null, "Картинки не открылись.");
            string dest = Beside(list[0], cols == 2 && list.Count == 2 ? "рядом" : "лист", ".png");
            Save(sheet, dest);
            return (dest, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    public static void SelfCheck()
    {
        string dir = Path.Combine(Path.GetTempPath(), "zwpic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "wide.png");
            using (var bmp = new Draw.Bitmap(30, 10))
            {
                bmp.SetPixel(0, 0, Draw.Color.Red);
                bmp.Save(file, ImageFormat.Png);
            }

            var (turned, turnErr) = Rotate(file, Draw.RotateFlipType.Rotate90FlipNone);
            if (turnErr != null || turned == null)
                throw new InvalidOperationException(turnErr ?? "поворот");
            using (var check = new Draw.Bitmap(turned))
            {
                if (check.Width != 10 || check.Height != 30)
                    throw new InvalidOperationException("поворот дал " + check.Width + "x" + check.Height);
            }

            var (cut, cutErr) = Crop(file, new Draw.Rectangle(2, 2, 6, 4));
            if (cutErr != null || cut == null)
                throw new InvalidOperationException(cutErr ?? "обрезка");
            using (var check = new Draw.Bitmap(cut))
            {
                if (check.Width != 6 || check.Height != 4)
                    throw new InvalidOperationException("обрезка дала " + check.Width + "x" + check.Height);
            }

            string second = Path.Combine(dir, "second.png");
            File.Copy(file, second);
            var (sheet, sheetErr) = Sheet(new[] { file, second }, 2);
            if (sheetErr != null || sheet == null)
                throw new InvalidOperationException(sheetErr ?? "лист");
            using (var check = new Draw.Bitmap(sheet))
            {
                if (check.Width < 400)
                    throw new InvalidOperationException("лист узкий");
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* временная папка */ }
        }
    }

    static Draw.Bitmap Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var raw = new Draw.Bitmap(stream);
        if ((long)raw.Width * raw.Height > 40_000_000)
            throw new InvalidOperationException("«" + Path.GetFileName(path) + "» слишком большая.");
        return new Draw.Bitmap(raw);
    }

    static void Save(Draw.Image image, string dest)
    {
        string ext = Path.GetExtension(dest).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg")
        {
            var encoder = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, 92L);
            image.Save(dest, encoder, parameters);
            return;
        }

        image.Save(dest, ImageFormat.Png);
    }

    static string ExtOrPng(string source)
    {
        string ext = Path.GetExtension(source).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".bmp" ? ext : ".png";
    }

    static string Beside(string source, string suffix, string ext)
    {
        string dir = Path.GetDirectoryName(source) ?? "";
        string name = Path.GetFileNameWithoutExtension(source);
        return FileOutPath.NextFree(Path.Combine(dir, name + " " + suffix + ext));
    }

    static Draw.Size Fit(int w, int h, int boxW, int boxH)
    {
        if (w < 1 || h < 1)
            return new Draw.Size(1, 1);
        double scale = Math.Min(boxW / (double)w, boxH / (double)h);
        return new Draw.Size(Math.Max(1, (int)(w * scale)), Math.Max(1, (int)(h * scale)));
    }
}
