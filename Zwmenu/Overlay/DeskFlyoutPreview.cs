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
        foreach (var scroll in FindScrolls(panel))
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
            Background = TemplateTheme.Brush(TemplateTheme.BgDeep),
            SizeToContent = SizeToContent.WidthAndHeight,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = image
        };
        window.Closed += (_, _) => Application.Current.Shutdown();
        window.Show();
        // Автозакрытие: скрипты сборки не должны ждать клика.
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            window.Close();
        };
        timer.Start();
    }

    static List<ScrollViewer> FindScrolls(DependencyObject? root)
    {
        var list = new List<ScrollViewer>();
        Walk(root, list);
        return list;
    }

    static void Walk(DependencyObject? node, List<ScrollViewer> list)
    {
        if (node == null)
            return;
        if (node is ScrollViewer scroll)
            list.Add(scroll);

        switch (node)
        {
            case System.Windows.Controls.Border { Child: DependencyObject child }:
                Walk(child, list);
                break;
            case ContentControl { Content: DependencyObject content }:
                Walk(content, list);
                break;
            case Panel panel:
                foreach (UIElement el in panel.Children)
                    Walk(el, list);
                break;
            case Decorator { Child: DependencyObject dec }:
                Walk(dec, list);
                break;
        }
    }
}
