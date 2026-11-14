using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Zwmenu.Services;

/// <summary>Снимок одного монитора в физических пикселях, без окна выделения.</summary>
public static class ScreenCapture
{
    private const int CursorShowing = 0x00000001;
    private const int DiNormal = 0x0003;

    public static Bitmap CaptureMonitor(Rectangle bounds, bool cursor)
    {
        if (bounds.Width < 1 || bounds.Height < 1)
            throw new InvalidOperationException("Пустой экран.");

        var bmp = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            if (cursor)
                DrawCursor(g, bounds);
            return bmp;
        }
        catch
        {
            bmp.Dispose();
            throw;
        }
    }

    public static BitmapSource ToFrozen(Bitmap bmp)
    {
        var data = bmp.LockBits(
            new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            if (data.Stride <= 0)
                throw new InvalidOperationException("Не удалось прочитать снимок.");

            int bytes = data.Stride * data.Height;
            var buffer = new byte[bytes];
            Marshal.Copy(data.Scan0, buffer, 0, bytes);
            var src = BitmapSource.Create(
                bmp.Width,
                bmp.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                buffer,
                data.Stride);
            src.Freeze();
            return src;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    public static void CopyToClipboard(Bitmap bmp)
    {
        var src = ToFrozen(bmp);
        Exception? last = null;
        for (int i = 0; i < 5; i++)
        {
            try
            {
                System.Windows.Clipboard.SetImage(src);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                Thread.Sleep(30);
            }
        }

        throw last ?? new InvalidOperationException("Буфер обмена занят.");
    }

    public static string SaveToDesktop(Bitmap bmp, ScreenshotSettings settings)
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (string.IsNullOrWhiteSpace(desktop))
            throw new InvalidOperationException("Рабочий стол не найден.");

        bool jpeg = string.Equals(settings.Format, "jpeg", StringComparison.OrdinalIgnoreCase);
        string ext = jpeg ? ".jpg" : ".png";
        string name = $"Zwmenu {DateTime.Now:yyyy-MM-dd HH-mm-ss}{ext}";
        string path = FileOutPath.NextFree(Path.Combine(desktop, name));

        if (jpeg)
        {
            var enc = Array.Find(
                ImageCodecInfo.GetImageEncoders(),
                c => c.FormatID == ImageFormat.Jpeg.Guid);
            if (enc == null)
                throw new InvalidOperationException("JPEG-кодек не найден.");

            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(settings.Quality, 40, 100));
            bmp.Save(path, enc, ep);
        }
        else
        {
            bmp.Save(path, ImageFormat.Png);
        }

        return path;
    }

    private static void DrawCursor(Graphics g, Rectangle bounds)
    {
        var info = new CursorInfo { cbSize = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info) || (info.flags & CursorShowing) == 0 || info.hCursor == IntPtr.Zero)
            return;

        if (!GetIconInfo(info.hCursor, out var icon))
            return;

        try
        {
            int x = info.x - bounds.X - icon.xHotspot;
            int y = info.y - bounds.Y - icon.yHotspot;
            if (x >= bounds.Width || y >= bounds.Height || x + 256 < 0 || y + 256 < 0)
                return;

            IntPtr hdc = g.GetHdc();
            try
            {
                DrawIconEx(hdc, x, y, info.hCursor, 0, 0, 0, IntPtr.Zero, DiNormal);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }
        finally
        {
            if (icon.hbmMask != IntPtr.Zero)
                DeleteObject(icon.hbmMask);
            if (icon.hbmColor != IntPtr.Zero)
                DeleteObject(icon.hbmColor);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CursorInfo pci);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out IconInfo piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(
        IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, int istepIfAniCur, IntPtr hbrFlickerFreeDraw, int diFlags);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
