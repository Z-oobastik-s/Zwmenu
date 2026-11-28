using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Zwmenu.Services;

namespace Zwmenu.Overlay;

/// <summary>Рендер ключевых панелей в PNG для проверки после редизайна.</summary>
public static class UiSurfacePreview
{
    public static void Run()
    {
        AppearanceStore.Reload();
        string dir = Path.Combine(Path.GetTempPath(), "zwmenu-ui-preview");
        Directory.CreateDirectory(dir);

        Save(Path.Combine(dir, "desk.png"), DeskFlyout.Build(_ => { }, () => { }), unlockScroll: true);
        Save(Path.Combine(dir, "guide.png"), BuildGuideBody(), unlockScroll: true);
        Save(Path.Combine(dir, "update.png"), BuildUpdateBody());
        Save(Path.Combine(dir, "tray-head.png"), BuildTraySample());

        var note = Path.Combine(dir, "readme.txt");
        File.WriteAllText(note, "Превью панелей Zwmenu.\nПапка: " + dir + "\n");

        var list = new StackPanel { Margin = new Thickness(16) };
        list.Children.Add(new TextBlock
        {
            Text = "Превью UI",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = TemplateTheme.Brush(TemplateTheme.Fg),
            Margin = new Thickness(0, 0, 0, 8)
        });
        list.Children.Add(new TextBlock
        {
            Text = dir,
            FontSize = 12,
            Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
            Margin = new Thickness(0, 0, 0, 12),
            TextWrapping = TextWrapping.Wrap
        });
        foreach (string file in Directory.EnumerateFiles(dir, "*.png"))
        {
            list.Children.Add(new TextBlock
            {
                Text = Path.GetFileName(file),
                Foreground = TemplateTheme.Brush(TemplateTheme.AccentText),
                Margin = new Thickness(0, 8, 0, 4)
            });
            list.Children.Add(new System.Windows.Controls.Image
            {
                Source = Load(file),
                Stretch = Stretch.None,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 8)
            });
        }

        var window = new Window
        {
            Title = "Превью UI",
            Width = 520,
            Height = 720,
            Background = TemplateTheme.Brush(TemplateTheme.BgDeep),
            Content = TrayPaint.HiddenScroll(list),
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        window.Closed += (_, _) => Application.Current.Shutdown();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            window.Close();
        };
        window.Show();
        timer.Start();
    }

    static UIElement BuildGuideBody()
    {
        var cards = new StackPanel();
        cards.Children.Add(ForgeChrome.Card(new TextBlock
        {
            Text = "Средняя кнопка: колесо",
            Foreground = TemplateTheme.Brush(TemplateTheme.Fg),
            TextWrapping = TextWrapping.Wrap
        }));
        cards.Children.Add(ForgeChrome.Card(new TextBlock
        {
            Text = "Правый клик по кубику: это меню",
            Foreground = TemplateTheme.Brush(TemplateTheme.Fg),
            TextWrapping = TextWrapping.Wrap
        }));
        var body = new StackPanel();
        body.Children.Add(ForgeChrome.BrandHead("Zwmenu", "Где что лежит."));
        body.Children.Add(cards);
        return ForgeChrome.Shell(body);
    }

    static UIElement BuildUpdateBody()
    {
        var body = new StackPanel();
        body.Children.Add(ForgeChrome.BrandHead("Обновление", "Новая версия Zwmenu"));
        body.Children.Add(ForgeChrome.Card(new TextBlock
        {
            Text = "Вышла версия 1.4.0. Сейчас стоит 1.3.80.",
            Foreground = TemplateTheme.Brush(TemplateTheme.Fg),
            TextWrapping = TextWrapping.Wrap
        }));
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        buttons.Children.Add(TemplateTheme.Pill("Поставить", () => { }, accent: true));
        buttons.Children.Add(TemplateTheme.Pill("Позже", () => { }));
        body.Children.Add(buttons);
        return ForgeChrome.Shell(body);
    }

    static UIElement BuildTraySample()
    {
        var main = new StackPanel { Width = 300 };
        main.Children.Add(ForgeChrome.BrandHead("Zwmenu", "Правый клик открыл это меню.", 32));
        main.Children.Add(ForgeChrome.Card(new TextBlock
        {
            Text = "Недавние снимки",
            Foreground = TemplateTheme.Brush(TemplateTheme.Fg)
        }));
        main.Children.Add(ForgeChrome.Section("Медиа"));
        main.Children.Add(ForgeChrome.MenuRow("Открыть статус", () => { }));
        main.Children.Add(ForgeChrome.MenuRow("Просмотр фото", () => { }));
        main.Children.Add(ForgeChrome.Section("Справка"));
        main.Children.Add(ForgeChrome.MenuRow("Выход", () => { }, danger: true));
        return ForgeChrome.Shell(main);
    }

    static void Save(string path, UIElement panel, bool unlockScroll = false)
    {
        if (unlockScroll)
        {
            foreach (var scroll in FindScrolls(panel))
                scroll.MaxHeight = double.PositiveInfinity;
        }

        panel.Measure(new System.Windows.Size(480, 2400));
        double pw = Math.Max(panel.DesiredSize.Width, 320);
        double ph = Math.Max(panel.DesiredSize.Height, 80);
        panel.Arrange(new Rect(0, 0, pw, ph));
        panel.UpdateLayout();

        int w = Math.Max(1, (int)Math.Ceiling(pw));
        int h = Math.Max(1, (int)Math.Ceiling(ph));
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(panel);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    static BitmapImage Load(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(path, UriKind.Absolute);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
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
