using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Zwmenu.Overlay;

static class TrayPaint
{
    public static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static DropShadowEffect Shadow() => new()
    {
        Color = Colors.Black,
        BlurRadius = 28,
        ShadowDepth = 0,
        Opacity = 0.55
    };

    public static double Scale() => ScaleAt(0, 0);

    public static double ScaleAt(int pixelX, int pixelY)
    {
        try
        {
            var point = new PointI { X = pixelX, Y = pixelY };
            IntPtr monitor = MonitorFromPoint(point, 2);
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 && dpiX >= 96)
                return dpiX / 96.0;
        }
        catch
        {
            // запасной замер ниже
        }

        try
        {
            using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
            return Math.Max(1, g.DpiX / 96.0);
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>Колёсико крутит список, стандартную полосу не рисуем.</summary>
    public static void HideBars(FrameworkElement host)
    {
        var style = new Style(typeof(System.Windows.Controls.Primitives.ScrollBar));
        style.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed));
        style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 0.0));
        style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 0.0));
        host.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = style;
    }

    public static ScrollViewer HiddenScroll(UIElement content)
    {
        var scroll = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0)
        };
        HideBars(scroll);
        return scroll;
    }

    /// <summary>Сколько места по высоте между краем экрана и панелью задач.</summary>
    public static double Room(int pixelX, int pixelY)
    {
        var limit = Limits(pixelX, pixelY);
        return Math.Max(320, (limit.Bottom - limit.Top) / limit.Scale - 52);
    }

    /// <summary>Нижний или верхний угол той стороны, где значок. Не посреди экрана.</summary>
    public static void PlaceInCorner(Window window, int pixelX, int pixelY)
    {
        var limit = Limits(pixelX, pixelY);
        double room = Math.Max(320, (limit.Bottom - limit.Top) / limit.Scale - 52);
        if (window.MaxHeight > room)
            window.MaxHeight = room;
        window.UpdateLayout();
        double width = window.ActualWidth > 1 ? window.ActualWidth : window.Width;
        double height = window.ActualHeight > 1 ? window.ActualHeight : 240;
        double scale = limit.Scale;
        var area = limit.Area;
        int bottomPx = limit.Bottom;
        const double margin = 12;
        const double tipGap = 40;
        bool toRight = pixelX >= area.Left + area.Width / 2.0;
        bool toBottom = pixelY >= area.Top + area.Height / 2.0;
        double minLeft = area.Left / scale + margin;
        double maxLeft = area.Right / scale - width - margin;
        double minTop = area.Top / scale + (toBottom ? margin : tipGap);
        double maxTop = bottomPx / scale - height - (toBottom ? tipGap : margin);
        double left = toRight ? maxLeft : minLeft;
        double top = toBottom ? maxTop : minTop;
        if (left < minLeft)
            left = minLeft;
        if (top < minTop)
            top = minTop;
        window.Left = left;
        window.Top = top;
    }

    readonly struct ScreenLimit
    {
        public ScreenLimit(double scale, System.Drawing.Rectangle area, int bottom)
        {
            Scale = scale;
            Area = area;
            Top = area.Top;
            Bottom = bottom;
        }

        public double Scale { get; }
        public System.Drawing.Rectangle Area { get; }
        public int Top { get; }
        public int Bottom { get; }
    }

    static ScreenLimit Limits(int pixelX, int pixelY)
    {
        double scale = Math.Max(1, ScaleAt(pixelX, pixelY));
        var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(pixelX, pixelY)).WorkingArea;
        int bottomPx = area.Bottom;
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero && GetWindowRect(tray, out WinRect trayRect))
        {
            int screenBottom = GetSystemMetrics(1);
            int trayTop = trayRect.Top;
            int trayHeight = trayRect.Bottom - trayRect.Top;
            if (trayHeight < 28 || trayHeight > 120)
                trayHeight = 40;
            if (trayTop > screenBottom - 48)
                trayTop = screenBottom - trayHeight;
            if (trayTop > area.Top + 80)
                bottomPx = Math.Min(bottomPx, trayTop);
        }
        return new ScreenLimit(scale, area, bottomPx);
    }

    public static bool ContainsPoint(Window window, int pixelX, int pixelY)
    {
        if (!window.IsVisible || window.ActualWidth < 1)
            return false;
        try
        {
            double scale = Math.Max(1, ScaleAt(pixelX, pixelY));
            double left = window.Left * scale;
            double top = window.Top * scale;
            double right = left + window.ActualWidth * scale;
            double bottom = top + window.ActualHeight * scale;
            return pixelX >= left - 4 && pixelY >= top - 4 && pixelX <= right + 4 && pixelY <= bottom + 4;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hwnd, out WinRect rect);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(PointI point, uint flags);

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    struct PointI
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WinRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

/// <summary>Клик мимо открытого окна трея его закрывает. Полоса над экраном для этого не нужна.</summary>
static class TrayDismiss
{
    const int WhMouseLl = 14;
    const int WmLButtonDown = 0x0201;
    const int WmRButtonDown = 0x0204;
    const int WmMButtonDown = 0x0207;

    static IntPtr _hook;
    static HookProc? _proc;
    static object? _owner;
    static Func<bool>? _inside;
    static Action? _close;

    public static void Arm(object owner, Func<bool> inside, Action close)
    {
        Disarm(_owner);
        _owner = owner;
        _inside = inside;
        _close = close;
        _proc = Callback;
        _hook = SetWindowsHookEx(WhMouseLl, _proc, IntPtr.Zero, 0);
    }

    public static void Disarm(object? owner)
    {
        if (owner != null && !ReferenceEquals(_owner, owner))
            return;
        _owner = null;
        _inside = null;
        _close = null;
        if (_hook == IntPtr.Zero)
            return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _close != null && _inside != null)
        {
            int msg = wParam.ToInt32();
            if (msg is WmLButtonDown or WmRButtonDown or WmMButtonDown)
            {
                var data = Marshal.PtrToStructure<HookPoint>(lParam);
                bool inside = false;
                try { inside = _inside(); } catch { /* окно уже закрывается */ }
                if (!inside)
                {
                    var close = _close;
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(close);
                }
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct HookPoint
    {
        public int X;
        public int Y;
        public int MouseData;
        public int Flags;
        public int Time;
        public IntPtr Extra;
    }

    [DllImport("user32.dll")]
    static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);

    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
}
