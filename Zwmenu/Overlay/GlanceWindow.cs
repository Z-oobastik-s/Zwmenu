using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Zwmenu.Services;

namespace Zwmenu.Overlay;

/// <summary>Взгляд на экран второго компьютера. Может обновляться, пока идёт трансляция.</summary>
public sealed class GlanceWindow : Window
{
    static GlanceWindow? _open;
    readonly DispatcherTimer _idle;
    readonly System.Windows.Controls.Image _image;
    readonly TextBlock _status;
    bool _held;
    bool _live;

    GlanceWindow()
    {
        Title = "Экран второго";
        Width = 960;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        TemplateTheme.StyleWindow(this);
        _image = new System.Windows.Controls.Image
        {
            Stretch = System.Windows.Media.Stretch.Uniform,
            Margin = new Thickness(8, 8, 8, 4)
        };
        _status = new TextBlock
        {
            Text = "Кадр с второго компьютера",
            Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
            FontSize = 12,
            Margin = new Thickness(12, 0, 12, 10),
            TextWrapping = TextWrapping.Wrap
        };
        var stop = TemplateTheme.Pill("Остановить трансляцию", () =>
        {
            WorkshopHub.StopPeerLive();
            _live = false;
            _status.Text = "Трансляция остановлена.";
        });
        stop.Margin = new Thickness(12, 0, 12, 12);
        var root = new DockPanel();
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(stop, Dock.Bottom);
        root.Children.Add(_status);
        root.Children.Add(stop);
        root.Children.Add(_image);
        Content = root;

        _idle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(14) };
        _idle.Tick += (_, _) =>
        {
            _idle.Stop();
            if (!_held && !_live)
                Close();
        };
        MouseEnter += (_, _) =>
        {
            _held = true;
            _idle.Stop();
        };
        MouseLeave += (_, _) =>
        {
            _held = false;
            if (!_live)
                _idle.Start();
        };
        Closed += (_, _) =>
        {
            _idle.Stop();
            if (_live)
                WorkshopHub.StopPeerLive();
            if (_open == this)
                _open = null;
        };
    }

    public static void ShowJpeg(byte[] jpeg, bool live = false)
    {
        if (jpeg == null || jpeg.Length < 16)
            return;
        var app = Application.Current;
        if (app == null)
            return;
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(() => ShowJpeg(jpeg, live));
            return;
        }

        var window = _open ?? new GlanceWindow();
        _open = window;
        window._live = live || window._live;
        window.ApplyJpeg(jpeg);
        window._status.Text = window._live
            ? "Идёт трансляция. Кадр обновляется сам."
            : "Один кадр. Наведи мышь, чтобы не закрылось сразу.";
        if (!window.IsVisible)
            window.Show();
        if (!window._live)
        {
            window._idle.Stop();
            window._idle.Start();
        }
        else
            window._idle.Stop();
    }

    public static void MarkLive()
    {
        var app = Application.Current;
        if (app == null)
            return;
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(MarkLive);
            return;
        }

        var window = _open ?? new GlanceWindow();
        _open = window;
        window._live = true;
        window._idle.Stop();
        window._status.Text = "Жду кадры со второго компьютера...";
        if (!window.IsVisible)
            window.Show();
    }

    void ApplyJpeg(byte[] jpeg)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(jpeg);
            bmp.EndInit();
            bmp.Freeze();
            _image.Source = bmp;
        }
        catch
        {
            _status.Text = "Снимок пришёл битый.";
        }
    }
}
