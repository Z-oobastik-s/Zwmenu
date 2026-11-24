using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Zwmenu.Overlay;

/// <summary>Рендер пульта в PNG и окно на экран: смотрим вид без колеса.</summary>
public static class DeskFlyoutPreview
{
    public static void Run()
    {
        string path = Path.Combine(Path.GetTempPath(), "zwmenu-desk-preview.png");
        var panel = DeskFlyout.Build(_ => { }, () => { });
        // На превью снимаем MaxHeight, чтобы в PNG был весь пульт, не обрезок.
        if (panel.Child is ScrollViewer scroll)
            scroll.MaxHeight = double.PositiveInfinity;
        panel.Measure(new System.Windows.Size(400, 2000));
        double pw = Math.Max(panel.DesiredSize.Width, 364);
        double ph = Math.Max(panel.DesiredSize.Height, 100);
        panel.Arrange(new Rect(0, 0, pw, ph));
        panel.UpdateLayout();

        int w = Math.Max(1, (int)Math.Ceiling(pw));
        int h = Math.Max(1, (int)Math.Ceiling(ph));
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(panel);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path))
            encoder.Save(stream);

        var image = new System.Windows.Controls.Image
        {
            Source = bitmap,
            Stretch = Stretch.None,
            Margin = new Thickness(16)
        };
        var window = new Window
        {
            Title = "Пульт",
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1C, 0x20)),
            SizeToContent = SizeToContent.WidthAndHeight,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = image
        };
        window.Closed += (_, _) => Application.Current.Shutdown();
        window.Show();
    }
}
