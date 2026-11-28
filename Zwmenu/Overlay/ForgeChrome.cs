using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Zwmenu.Services;

namespace Zwmenu.Overlay;

/// <summary>
/// Визуальный каркас Signal Forge: оболочка, бренд, секции, строки, лёгкий fade.
/// Без BlurEffect и бесконечных анимаций.
/// </summary>
public static class ForgeChrome
{
    static ImageSource? _mark;
    static ImageBrush? _wash;

    public static ImageSource? Mark()
    {
        if (_mark != null)
            return _mark;
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Brand", "forge-mark.jpg");
        if (!System.IO.File.Exists(path))
            return null;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(path, UriKind.Absolute);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.DecodePixelWidth = 96;
        bmp.EndInit();
        bmp.Freeze();
        _mark = bmp;
        return _mark;
    }

    public static Brush PanelWash()
    {
        if (_wash != null)
            return _wash;
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Brand", "forge-panel-wash.jpg");
        if (!System.IO.File.Exists(path))
            return TemplateTheme.Brush(TemplateTheme.BgPanel);

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(path, UriKind.Absolute);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.DecodePixelWidth = 640;
        bmp.EndInit();
        bmp.Freeze();
        _wash = new ImageBrush(bmp)
        {
            Stretch = Stretch.UniformToFill,
            Opacity = 0.22,
            AlignmentX = AlignmentX.Center,
            AlignmentY = AlignmentY.Top
        };
        _wash.Freeze();
        return _wash;
    }

    public static System.Windows.Controls.Border Shell(UIElement child, Thickness? padding = null, double? radius = null)
    {
        var body = new Grid();
        body.Children.Add(new System.Windows.Controls.Border
        {
            Background = TemplateTheme.Brush(TemplateTheme.BgPanel),
            CornerRadius = new CornerRadius(radius ?? UiTheme.Current.CornerPanel)
        });
        body.Children.Add(new System.Windows.Controls.Border
        {
            Background = PanelWash(),
            CornerRadius = new CornerRadius(radius ?? UiTheme.Current.CornerPanel),
            IsHitTestVisible = false
        });
        body.Children.Add(new System.Windows.Controls.Border
        {
            BorderBrush = TemplateTheme.Brush(Color.FromArgb(0x55, TemplateTheme.AccentText.R, TemplateTheme.AccentText.G, TemplateTheme.AccentText.B)),
            BorderThickness = new Thickness(0, 2, 0, 0),
            CornerRadius = new CornerRadius(radius ?? UiTheme.Current.CornerPanel),
            Margin = new Thickness(1),
            IsHitTestVisible = false
        });
        body.Children.Add(new System.Windows.Controls.Border
        {
            Padding = padding ?? new Thickness(12, 12, 12, 10),
            Child = child
        });

        return new System.Windows.Controls.Border
        {
            Background = Brushes.Transparent,
            BorderBrush = TemplateTheme.Brush(TemplateTheme.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radius ?? UiTheme.Current.CornerPanel),
            Effect = TrayPaint.Shadow(),
            Child = body,
            SnapsToDevicePixels = true
        };
    }

    public static UIElement BrandHead(string title, string? subtitle = null, double markSize = 28)
    {
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(2, 0, 2, 10) };
        var mark = Mark();
        if (mark != null)
        {
            var badge = new System.Windows.Controls.Border
            {
                Width = markSize,
                Height = markSize,
                CornerRadius = new CornerRadius(markSize / 2),
                ClipToBounds = true,
                Margin = new Thickness(0, 0, 10, 0),
                BorderBrush = TemplateTheme.Brush(Color.FromArgb(0x66, TemplateTheme.AccentText.R, TemplateTheme.AccentText.G, TemplateTheme.AccentText.B)),
                BorderThickness = new Thickness(1),
                Child = new System.Windows.Controls.Image
                {
                    Source = mark,
                    Stretch = Stretch.UniformToFill
                }
            };
            DockPanel.SetDock(badge, Dock.Left);
            row.Children.Add(badge);
        }
        else
        {
            var rail = new System.Windows.Controls.Border
            {
                Width = 4,
                Height = markSize,
                CornerRadius = new CornerRadius(2),
                Background = TemplateTheme.Brush(TemplateTheme.AccentText),
                Margin = new Thickness(0, 0, 10, 0),
                Opacity = 0.85
            };
            DockPanel.SetDock(rail, Dock.Left);
            row.Children.Add(rail);
        }

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = TemplateTheme.Brush(TemplateTheme.Fg),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily(UiTheme.Current.FontUi)
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            text.Children.Add(new TextBlock
            {
                Text = subtitle,
                Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
                FontSize = 11.5,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily(UiTheme.Current.FontUi)
            });
        }

        row.Children.Add(text);
        return row;
    }

    public static UIElement Section(string title)
    {
        var row = new DockPanel { Margin = new Thickness(4, 10, 4, 4) };
        var mark = new System.Windows.Controls.Border
        {
            Width = 3,
            Height = 11,
            CornerRadius = new CornerRadius(1.5),
            Background = TemplateTheme.Brush(TemplateTheme.AccentText),
            Opacity = 0.8,
            Margin = new Thickness(0, 1, 7, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(mark, Dock.Left);
        row.Children.Add(mark);
        row.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(),
            Foreground = TemplateTheme.Brush(TemplateTheme.AccentText),
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Opacity = 0.9,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily(UiTheme.Current.FontUi)
        });
        return row;
    }

    public static System.Windows.Controls.Border MenuRow(string text, Action click, bool danger = false)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = TemplateTheme.Brush(danger ? Color.FromRgb(0xE7, 0xB4, 0xB0) : TemplateTheme.Fg),
            FontSize = 13,
            FontFamily = new FontFamily(UiTheme.Current.FontUi),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 8, 0)
        };
        var rail = new System.Windows.Controls.Border
        {
            Width = 3,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(1.5),
            Margin = new Thickness(2, 6, 0, 6),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        var grid = new Grid();
        grid.Children.Add(rail);
        grid.Children.Add(label);

        var row = new System.Windows.Controls.Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(UiTheme.Current.CornerChip),
            Padding = new Thickness(4, 9, 4, 9),
            Margin = new Thickness(0, 1, 0, 1),
            Cursor = Cursors.Hand,
            Child = grid,
            SnapsToDevicePixels = true
        };
        row.MouseEnter += (_, _) =>
        {
            row.Background = TemplateTheme.Brush(TemplateTheme.BtnHover);
            rail.Background = TemplateTheme.Brush(danger
                ? Color.FromRgb(0xE7, 0xB4, 0xB0)
                : TemplateTheme.AccentText);
        };
        row.MouseLeave += (_, _) =>
        {
            row.Background = Brushes.Transparent;
            rail.Background = Brushes.Transparent;
        };
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            try { click(); } catch { /* строка не валит меню */ }
        };
        row.Tag = label;
        return row;
    }

    public static TextBlock? RowLabel(System.Windows.Controls.Border row) =>
        row.Tag as TextBlock
        ?? FindLabel(row);

    static TextBlock? FindLabel(DependencyObject root)
    {
        if (root is TextBlock tb)
            return tb;
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var hit = FindLabel(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
            if (hit != null)
                return hit;
        }

        if (root is System.Windows.Controls.Panel panel)
        {
            foreach (UIElement child in panel.Children)
            {
                var hit = FindLabel(child);
                if (hit != null)
                    return hit;
            }
        }
        else if (root is System.Windows.Controls.Border { Child: DependencyObject content })
        {
            return FindLabel(content);
        }

        return null;
    }

    public static System.Windows.Controls.Border Card(UIElement child, Thickness? padding = null)
    {
        return new System.Windows.Controls.Border
        {
            Background = TemplateTheme.Brush(TemplateTheme.ListBg),
            BorderBrush = TemplateTheme.Brush(TemplateTheme.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(UiTheme.Current.CornerChip + 2),
            Padding = padding ?? new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Child = child,
            SnapsToDevicePixels = true
        };
    }

    public static void FadeIn(UIElement target, double ms = 140)
    {
        target.Opacity = 0;
        var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        target.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    public static System.Windows.Controls.Border ProgressTrack(System.Windows.Controls.Border fill)
    {
        return new System.Windows.Controls.Border
        {
            Height = 6,
            Background = TemplateTheme.Brush(TemplateTheme.ListBg),
            BorderBrush = TemplateTheme.Brush(TemplateTheme.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 0, 0, 14),
            Child = fill,
            ClipToBounds = true
        };
    }
}
