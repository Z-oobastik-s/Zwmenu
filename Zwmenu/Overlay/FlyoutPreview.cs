using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Zwmenu.Services;
using IoPath = System.IO.Path;

namespace Zwmenu.Overlay;

/// <summary>Кадры форм выезда рядом с диском + прогон появлений.</summary>
public static class FlyoutPreview
{
    public static void Run()
    {
        AppearanceStore.Reload();
        string dir = IoPath.Combine(IoPath.GetTempPath(), "zwmenu-flyout-preview");
        Directory.CreateDirectory(dir);

        string prevStyle = AppearanceStore.Current.FlyoutStyle;
        string prevMotion = AppearanceStore.Current.FlyoutMotion;

        try
        {
            foreach (var style in FlyoutLooks.StyleCatalog)
            {
                AppearanceStore.Current.FlyoutStyle = style.Id;
                AppearanceStore.Current.FlyoutMotion = style.MotionId;
                var canvas = BuildScene(style.Id, style.Title);
                Save(IoPath.Combine(dir, "form-" + style.Id + ".png"), canvas);
            }

            File.WriteAllText(IoPath.Combine(dir, "audit.txt"), FlyoutForms.Audit());
            File.WriteAllText(
                IoPath.Combine(dir, "readme.txt"),
                "Формы выезда Zwmenu.\nВеер, ореол, лента, корона, всплеск, стык.\nПапка: " + dir + "\n");
        }
        finally
        {
            AppearanceStore.Current.FlyoutStyle = prevStyle;
            AppearanceStore.Current.FlyoutMotion = prevMotion;
        }

        Application.Current.Shutdown();
    }

    static void RunMotionPass(string dir, string restoreStyle, string restoreMotion)
    {
        var host = new Window
        {
            Title = "Проверка появления форм",
            Width = 760,
            Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = TemplateTheme.Brush(TemplateTheme.BgDeep),
            Topmost = true,
            ShowActivated = false
        };

        var stage = new Canvas { Width = 720, Height = 480 };
        host.Content = new Border { Padding = new Thickness(12), Child = stage };

        int index = 0;
        var motions = FlyoutLooks.MotionCatalog;

        void Next()
        {
            if (index >= motions.Count)
            {
                AppearanceStore.Current.FlyoutStyle = restoreStyle;
                AppearanceStore.Current.FlyoutMotion = restoreMotion;
                host.Close();
                Application.Current.Shutdown();
                return;
            }

            var motion = motions[index++];
            AppearanceStore.Current.FlyoutMotion = motion.Id;
            AppearanceStore.Current.FlyoutStyle = motion.Id switch
            {
                "unfurl" => "ribbon",
                "cascade" => "fan",
                "dockslide" => "dock",
                _ => "halo"
            };

            stage.Children.Clear();
            var scene = BuildScene(AppearanceStore.Current.FlyoutStyle, motion.Title);
            stage.Children.Add(scene);
            Canvas.SetLeft(scene, 0);
            Canvas.SetTop(scene, 0);

            stage.Children.Add(new TextBlock
            {
                Text = motion.Title + " / " + motion.Hint,
                Foreground = TemplateTheme.Brush(TemplateTheme.AccentText),
                FontSize = 13,
                Width = 680
            });
            Canvas.SetLeft(stage.Children[^1], 12);
            Canvas.SetTop(stage.Children[^1], 8);

            var wait = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(320, motion.Ms + 140))
            };
            wait.Tick += (_, _) =>
            {
                wait.Stop();
                try { Save(IoPath.Combine(dir, "motion-" + motion.Id + ".png"), stage); }
                catch { /* кадр не обязателен */ }
                Next();
            };
            wait.Start();
        }

        host.Show();
        host.Dispatcher.BeginInvoke(Next, DispatcherPriority.Loaded);
    }

    static Canvas BuildScene(string formId, string title)
    {
        var canvas = new Canvas
        {
            Width = 980,
            Height = 640,
            Background = TemplateTheme.Brush(TemplateTheme.BgDeep)
        };

        double cx = 360;
        double cy = 300;
        // Как FlyoutKeepOut: сразу за слотами (~95+18), не огромный clear скина.
        double keep = 120;
        PaintDisc(canvas, cx, cy, keep);

        var items = SampleItems(formId);
        var mount = FlyoutForms.Build(formId, cx, cy, keep, -Math.PI / 2, title, items, 980, 640);
        // Сразу финальный кадр: сбросить анимации появления.
        FinalizeVisual(mount.Root);
        foreach (var p in mount.Parts)
            FinalizeVisual(p);
        canvas.Children.Add(mount.Root);

        // Контроль keep out.
        canvas.Children.Add(new Ellipse
        {
            Width = keep * 2,
            Height = keep * 2,
            Stroke = new SolidColorBrush(Color.FromArgb(0x55, 0xE0, 0x60, 0x60)),
            StrokeThickness = 1,
            IsHitTestVisible = false
        });
        Canvas.SetLeft(canvas.Children[^1], cx - keep);
        Canvas.SetTop(canvas.Children[^1], cy - keep);

        return canvas;
    }

    static List<FrameworkElement> SampleItems(string formId)
    {
        string[] names =
        [
            "Steam", "Photoshop", "Cursor", "Discord", "Spotify", "Notion",
            "Chrome", "Telegram", "FileZilla", "Dead Frontier", "TLauncher", "Process Hacker"
        ];
        var list = new List<FrameworkElement>();
        foreach (string name in names)
        {
            if (formId is "halo" or "crown")
            {
                list.Add(new Border
                {
                    Width = 44,
                    Height = 44,
                    CornerRadius = new CornerRadius(22),
                    Background = TemplateTheme.Brush(TemplateTheme.BtnBg),
                    BorderBrush = TemplateTheme.Brush(TemplateTheme.AccentText),
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = name[..1],
                        Foreground = TemplateTheme.Brush(TemplateTheme.AccentText),
                        FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                });
                continue;
            }

            double w = formId switch
            {
                "fan" => 150,
                "burst" => 128,
                "ribbon" or "arc" => 118,
                _ => 168
            };
            list.Add(new Border
            {
                Width = w,
                Height = 34,
                CornerRadius = new CornerRadius(10),
                Background = TemplateTheme.Brush(TemplateTheme.BtnBg),
                Padding = new Thickness(10, 0, 10, 0),
                Child = new TextBlock
                {
                    Text = name,
                    Foreground = TemplateTheme.Brush(TemplateTheme.Fg),
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
        }

        return list;
    }

    static void PaintDisc(Canvas canvas, double cx, double cy, double keep)
    {
        var glow = new Ellipse
        {
            Width = keep * 2,
            Height = keep * 2,
            Fill = new RadialGradientBrush(
                Color.FromArgb(0x35, TemplateTheme.AccentText.R, TemplateTheme.AccentText.G, TemplateTheme.AccentText.B),
                Colors.Transparent)
        };
        Canvas.SetLeft(glow, cx - keep);
        Canvas.SetTop(glow, cy - keep);
        canvas.Children.Add(glow);

        var disc = new Ellipse
        {
            Width = 220,
            Height = 220,
            Fill = TemplateTheme.Brush(TemplateTheme.BgPanel),
            Stroke = TemplateTheme.Brush(TemplateTheme.AccentText),
            StrokeThickness = 2
        };
        Canvas.SetLeft(disc, cx - 110);
        Canvas.SetTop(disc, cy - 110);
        canvas.Children.Add(disc);

        for (int i = 0; i < 8; i++)
        {
            double a = -Math.PI / 2 + i * (Math.PI / 4);
            var slot = new Ellipse
            {
                Width = 36,
                Height = 36,
                Fill = TemplateTheme.Brush(TemplateTheme.BtnBg),
                Stroke = TemplateTheme.Brush(TemplateTheme.AccentText),
                StrokeThickness = 1
            };
            Canvas.SetLeft(slot, cx + Math.Cos(a) * 95 - 18);
            Canvas.SetTop(slot, cy + Math.Sin(a) * 95 - 18);
            canvas.Children.Add(slot);
        }
    }

    static void FinalizeVisual(FrameworkElement el)
    {
        el.BeginAnimation(UIElement.OpacityProperty, null);
        el.Opacity = 1;
        if (el.RenderTransform is TransformGroup g)
        {
            foreach (var t in g.Children)
            {
                if (t is ScaleTransform s)
                {
                    s.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    s.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    s.ScaleX = 1;
                    s.ScaleY = 1;
                }
                else if (t is TranslateTransform m)
                {
                    m.BeginAnimation(TranslateTransform.XProperty, null);
                    m.BeginAnimation(TranslateTransform.YProperty, null);
                    m.X = 0;
                    m.Y = 0;
                }
            }
        }
        else if (el.RenderTransform is RotateTransform)
        {
            // угол лепестка/дуги оставляем
        }
    }

    static void Save(string path, FrameworkElement panel)
    {
        panel.Measure(new System.Windows.Size(panel.Width > 0 ? panel.Width : 720, panel.Height > 0 ? panel.Height : 460));
        double pw = Math.Max(panel.DesiredSize.Width, panel.Width > 0 ? panel.Width : 320);
        double ph = Math.Max(panel.DesiredSize.Height, panel.Height > 0 ? panel.Height : 200);
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
}
