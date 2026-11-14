using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Zwmenu.Services;

namespace Zwmenu.Overlay;

/// <summary>Лист из пяти новых видов: картинки на диск и окно на экран, чтобы их было видно.</summary>
public static class LookSheet
{
    public static void Run()
    {
        string dir = Path.Combine(Path.GetTempPath(), "zwmenu-looks");
        Directory.CreateDirectory(dir);

        var host = new System.Windows.Controls.Primitives.UniformGrid
        {
            Columns = 4,
            Margin = new Thickness(8)
        };

        var backdrop = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        backdrop.GradientStops.Add(new GradientStop(Color.FromRgb(0x2A, 0x34, 0x40), 0));
        backdrop.GradientStops.Add(new GradientStop(Color.FromRgb(0x16, 0x18, 0x1E), 1));
        backdrop.Freeze();

        for (int id = 0; id < RingLooks.Count; id++)
        {
            var look = RingLooks.Get(id);
            var cell = new StackPanel { Width = 200, Margin = new Thickness(4, 2, 4, 6) };
            cell.Children.Add(new TextBlock
            {
                Text = look.Title,
                Foreground = Brushes.White,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6)
            });

            var full = new Grid { Width = 520, Height = 520, Background = backdrop };
            var canvas = new Canvas { Width = 520, Height = 520 };
            RingArt.Place(canvas, look, 260, 260, 116, 64, 70);
            full.Children.Add(canvas);
            full.Measure(new System.Windows.Size(520, 520));
            full.Arrange(new Rect(0, 0, 520, 520));
            full.UpdateLayout();

            var bitmap = new RenderTargetBitmap(520, 520, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(full);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string name = string.IsNullOrWhiteSpace(look.ArtFolder) ? id.ToString() : look.ArtFolder;
            using (var stream = File.Create(Path.Combine(dir, name + ".png")))
                encoder.Save(stream);

            var box = new Viewbox { Width = 168, Height = 168, Stretch = Stretch.Uniform };
            box.Child = full;
            cell.Children.Add(box);
            host.Children.Add(cell);
        }

        var window = new Window
        {
            Title = "Zwmenu",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Width = 880,
            Height = 640,
            Left = 16,
            Top = 20,
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x18)),
            Content = host
        };

        window.Show();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(40) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { window.Close(); } catch { /* уже закрыто */ }
            Application.Current.Shutdown();
        };
        timer.Start();
    }
}
