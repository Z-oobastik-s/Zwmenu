using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Zwmenu.Overlay;

/// <summary>Короткое уведомление у нижнего края монитора, где делали снимок.</summary>
public static class ScreenshotToast
{
    public static void Show(string text, System.Drawing.Rectangle screenPx, double seconds = 1.8)
    {
        var toast = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.Manual,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Content = BuildCard(text)
        };

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Clamp(seconds, 0.8, 6)) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { toast.Close(); } catch { /* уже закрыт */ }
        };
        toast.Closed += (_, _) => timer.Stop();
        toast.MouseLeftButtonDown += (_, _) =>
        {
            timer.Stop();
            try { toast.Close(); } catch { /* уже закрыт */ }
        };
        toast.Loaded += (_, _) =>
        {
            Place(toast, screenPx);
            timer.Start();
        };
        toast.Show();
    }

    private static UIElement BuildCard(string text)
    {
        var bg = new SolidColorBrush(Color.FromRgb(0x1C, 0x18, 0x14));
        bg.Freeze();
        var border = new SolidColorBrush(Color.FromArgb(0xCC, 0xC4, 0xA4, 0x84));
        border.Freeze();
        var fg = new SolidColorBrush(Color.FromRgb(0xF5, 0xF0, 0xE8));
        fg.Freeze();

        return new System.Windows.Controls.Border
        {
            Background = bg,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12, 16, 12),
            Child = new System.Windows.Controls.TextBlock
            {
                Text = text,
                Foreground = fg,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13.5,
                TextWrapping = TextWrapping.Wrap
            }
        };
    }

    private static void Place(Window toast, System.Drawing.Rectangle screenPx)
    {
        toast.UpdateLayout();
        double w = toast.ActualWidth > 1 ? toast.ActualWidth : toast.Width;
        double h = toast.ActualHeight > 1 ? toast.ActualHeight : 48;
        double cx = screenPx.X + screenPx.Width / 2.0;
        double bottom = screenPx.Bottom - 36;
        Point dip = RadialLayout.DeviceToDip(toast, cx, bottom);
        toast.Left = dip.X - w / 2;
        toast.Top = dip.Y - h;
    }
}
