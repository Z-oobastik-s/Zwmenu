using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Zwmenu.Overlay;

/// <summary>
/// Пока файл едет через край: стрелка с плюсом и подпись у курсора.
/// Плюс виден, пока кнопка зажата. Рамки выделения при этом нет.
/// </summary>
static class EdgeCarryCue
{
    private const uint SpiSetCursors = 0x0057;
    private const uint OcrNormal = 32512;
    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private static Window? _window;
    private static TextBlock? _label;
    private static DispatcherTimer? _timer;
    private static bool _cursorSwapped;
    private static int _want;
    private static long _shownAt;
    private static readonly string MarkerPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Zwmenu", "cursor-carry");

    static EdgeCarryCue()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreCursor();
    }

    /// <summary>Если прошлая копия упала посреди жеста, вернуть обычную стрелку.</summary>
    public static void HealCursor()
    {
        try
        {
            RestoreCursor();
            if (!System.IO.File.Exists(MarkerPath))
                SystemParametersInfo(SpiSetCursors, 0, IntPtr.Zero, 0);
        }
        catch
        {
            // курсор и так системный
        }
    }

    public static void Show(string label)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
            return;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => Show(label));
            return;
        }

        label = string.IsNullOrWhiteSpace(label) ? "Файл" : label.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (label.Length > 48)
            label = label[..48] + "…";
        _shownAt = Environment.TickCount64;
        EnsureWindow();
        if (_label != null)
            _label.Text = "＋  " + label;
        if (_window != null && !_window.IsVisible)
            _window.Show();
        Place();
        if (Volatile.Read(ref _want) != 2)
            Volatile.Write(ref _want, 1);
        QueueCursor();
        _timer?.Start();
    }

    public static void Hide()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(Hide);
            return;
        }

        _timer?.Stop();
        try { _window?.Hide(); } catch { /* уже закрыто */ }
        if (Volatile.Read(ref _want) == 1)
            Volatile.Write(ref _want, 0);
        QueueCursor();
    }

    /// <summary>Курсор уехал на другой компьютер: здесь стрелку не рисуем.</summary>
    public static void Park()
    {
        Volatile.Write(ref _want, 2);
        QueueCursor();
    }

    /// <summary>Курсор вернулся на этот компьютер.</summary>
    public static void Unpark()
    {
        if (Volatile.Read(ref _want) == 2)
            Volatile.Write(ref _want, 0);
        QueueCursor();
    }

    private static void EnsureWindow()
    {
        if (_window != null)
            return;
        _label = new TextBlock
        {
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0xF0, 0xE8)),
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold
        };
        var bg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1C, 0x18, 0x14));
        bg.Freeze();
        var border = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xE6, 0xC4, 0xA4, 0x84));
        border.Freeze();
        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            SizeToContent = SizeToContent.WidthAndHeight,
            IsHitTestVisible = false,
            Content = new Border
            {
                Background = bg,
                BorderBrush = border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 6, 12, 6),
                IsHitTestVisible = false,
                Child = _label
            }
        };
        _window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(_window).Handle;
            int ex = GetWindowLong(hwnd, GwlExstyle);
            SetWindowLong(hwnd, GwlExstyle, ex | WsExTransparent | WsExToolWindow | WsExNoActivate);
        };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _timer.Tick += (_, _) =>
        {
            if (Environment.TickCount64 - _shownAt > 20_000)
            {
                Hide();
                return;
            }

            Place();
        };
    }

    private static void Place()
    {
        if (_window == null)
            return;
        var cursor = System.Windows.Forms.Cursor.Position;
        if (_window.ActualWidth < 2)
            _window.UpdateLayout();
        double w = _window.ActualWidth > 1 ? _window.ActualWidth : 160;
        double h = _window.ActualHeight > 1 ? _window.ActualHeight : 32;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
        int x = cursor.X + 22;
        int y = cursor.Y + 22;
        if (x + w > screen.Right)
            x = cursor.X - (int)w - 8;
        if (y + h > screen.Bottom)
            y = cursor.Y - (int)h - 8;
        x = Math.Max(screen.Left, x);
        y = Math.Max(screen.Top, y);
        var dip = RadialLayout.DeviceToDip(_window, x, y);
        _window.Left = dip.X;
        _window.Top = dip.Y;
    }

    private static int _cursorOp;

    /// <summary>
    /// Смена системной стрелки на потоке мыши подвешивает курсор на сотни миллисекунд.
    /// </summary>
    private static void QueueCursor()
    {
        int op = Interlocked.Increment(ref _cursorOp);
        System.Threading.Tasks.Task.Run(() => ApplyCursor(op));
    }

    private static void ApplyCursor(int op)
    {
        if (op != Volatile.Read(ref _cursorOp))
            return;
        int want = Volatile.Read(ref _want);
        if (want == 2)
            BlankCursor(op);
        else if (want == 1)
            SwapCursor(op);
        else
            RestoreCursor();
    }

    private static readonly uint[] ArrowIds = { 32512, 32513, 32649, 32651, 32515, 32646 };

    private static void BlankCursor(int op)
    {
        if (op != Volatile.Read(ref _cursorOp))
            return;
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(MarkerPath)!);
            System.IO.File.WriteAllText(MarkerPath, "1");
            foreach (uint id in ArrowIds)
            {
                if (op != Volatile.Read(ref _cursorOp) || Volatile.Read(ref _want) != 2)
                    return;
                IntPtr cursor = EmptyCursor();
                if (cursor == IntPtr.Zero)
                    continue;
                if (!SetSystemCursor(cursor, id))
                    DestroyCursor(cursor);
            }

            _cursorSwapped = true;
        }
        catch
        {
            // стрелка останется, связь от этого не рвётся
        }
    }

    private static void SwapCursor(int op)
    {
        if (_cursorSwapped || op != Volatile.Read(ref _cursorOp))
            return;
        IntPtr cursor = IntPtr.Zero;
        try
        {
            cursor = BuildCursor();
            if (cursor == IntPtr.Zero || op != Volatile.Read(ref _cursorOp))
            {
                if (cursor != IntPtr.Zero)
                    DestroyCursor(cursor);
                return;
            }

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(MarkerPath)!);
            System.IO.File.WriteAllText(MarkerPath, "1");
            // SetSystemCursor забирает хэндл себе.
            if (!SetSystemCursor(cursor, OcrNormal))
            {
                DestroyCursor(cursor);
                return;
            }

            cursor = IntPtr.Zero;
            _cursorSwapped = true;
            if (op != Volatile.Read(ref _cursorOp))
                RestoreCursor();
        }
        catch
        {
            if (cursor != IntPtr.Zero)
                DestroyCursor(cursor);
        }
    }

    private static void RestoreCursor()
    {
        if (!_cursorSwapped && !System.IO.File.Exists(MarkerPath))
            return;
        try { SystemParametersInfo(SpiSetCursors, 0, IntPtr.Zero, 0); }
        catch { /* схема курсоров уже обычная */ }
        _cursorSwapped = false;
        try { System.IO.File.Delete(MarkerPath); } catch { /* маркер не обязателен */ }
    }

    private static IntPtr EmptyCursor()
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.Clear(System.Drawing.Color.Transparent);
        return CursorFrom(bmp, 0, 0);
    }

    private static IntPtr BuildCursor()
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            var arrow = new[]
            {
                new PointF(2, 1), new PointF(2, 22), new PointF(7, 17),
                new PointF(11, 25), new PointF(14, 23), new PointF(10, 15), new PointF(17, 15)
            };
            var shadow = arrow.Select(p => new PointF(p.X + 1, p.Y + 1)).ToArray();
            using var black = new SolidBrush(System.Drawing.Color.Black);
            using var white = new SolidBrush(System.Drawing.Color.White);
            using var pen = new System.Drawing.Pen(System.Drawing.Color.Black, 1.4f);
            g.FillPolygon(black, shadow);
            g.FillPolygon(white, arrow);
            g.DrawPolygon(pen, arrow);
            g.FillEllipse(black, 15, 15, 15, 15);
            g.FillEllipse(white, 16, 16, 13, 13);
            using var plus = new System.Drawing.Pen(System.Drawing.Color.Black, 2f);
            g.DrawLine(plus, 22, 18, 22, 28);
            g.DrawLine(plus, 17, 23, 27, 23);
        }

        return CursorFrom(bmp, 1, 1);
    }

    private static IntPtr CursorFrom(Bitmap bmp, int hotX, int hotY)
    {
        int size = bmp.Width;
        var header = new BitmapInfo
        {
            Size = 40,
            Width = size,
            Height = -size,
            Planes = 1,
            BitCount = 32,
            Compression = 0
        };
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr bits;
        IntPtr color = CreateDIBSection(screen, ref header, 0, out bits, IntPtr.Zero, 0);
        ReleaseDC(IntPtr.Zero, screen);
        if (color == IntPtr.Zero || bits == IntPtr.Zero)
            return IntPtr.Zero;
        var raw = bmp.LockBits(new Rectangle(0, 0, size, size), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int stride = size * 4;
            var row = new byte[stride];
            for (int y = 0; y < size; y++)
            {
                Marshal.Copy(raw.Scan0 + y * raw.Stride, row, 0, stride);
                Marshal.Copy(row, 0, bits + y * stride, stride);
            }
        }
        finally
        {
            bmp.UnlockBits(raw);
        }

        IntPtr mask = CreateBitmap(size, size, 1, 1, MaskBits(size));
        var info = new IconInfo
        {
            FIcon = false,
            XHotspot = hotX,
            YHotspot = hotY,
            HbmMask = mask,
            HbmColor = color
        };
        IntPtr cursor = CreateIconIndirect(ref info);
        DeleteObject(color);
        if (mask != IntPtr.Zero)
            DeleteObject(mask);
        return cursor;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetSystemCursor(IntPtr cursor, uint id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint winIni);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, byte[]? bits);

    private static byte[] MaskBits(int size)
    {
        int stride = ((size + 15) / 16) * 2;
        var bits = new byte[stride * size];
        Array.Fill(bits, (byte)0xFF);
        return bits;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref IconInfo info);

    [DllImport("user32.dll")]
    private static extern bool DestroyCursor(IntPtr cursor);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool FIcon;
        public int XHotspot;
        public int YHotspot;
        public IntPtr HbmMask;
        public IntPtr HbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }
}
