using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Zwmenu.Overlay;

/// <summary>Короткий взгляд на экран второго компьютера. Само закрывается, если на него не навести мышь.</summary>
public sealed class GlanceWindow : Window
{
    static GlanceWindow? _open;
    readonly DispatcherTimer _timer;
    bool _held;

    GlanceWindow(byte[] jpeg)
    {
        Title = "Экран второго";
        Width = 860;
        Height = 540;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        TemplateTheme.StyleWindow(this);
        var image = new System.Windows.Controls.Image
        {
            Stretch = System.Windows.Media.Stretch.Uniform,
            Margin = new Thickness(8)
        };
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(jpeg);
            bmp.EndInit();
            bmp.Freeze();
            image.Source = bmp;
        }
        catch
        {
            Content = new TextBlock
            {
                Text = "Снимок пришёл битый.",
                Foreground = TemplateTheme.Brush(TemplateTheme.Fg),
                Margin = new Thickness(16)
            };
        }

        if (Content == null)
            Content = image;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            if (!_held)
                Close();
        };
        MouseEnter += (_, _) =>
        {
            _held = true;
            _timer.Stop();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            if (_open == this)
                _open = null;
        };
        Loaded += (_, _) => _timer.Start();
    }

    public static void ShowJpeg(byte[] jpeg)
    {
        if (jpeg == null || jpeg.Length < 16)
            return;
        var app = Application.Current;
        if (app == null)
            return;
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(() => ShowJpeg(jpeg));
            return;
        }

        _open?.Close();
        var window = new GlanceWindow(jpeg);
        _open = window;
        window.Show();
    }
}
