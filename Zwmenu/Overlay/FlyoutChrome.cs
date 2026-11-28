using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Zwmenu.Services;

namespace Zwmenu.Overlay;

/// <summary>
/// Общие кисти и чип для форм выезда. Сами формы живут в FlyoutForms.
/// </summary>
public static class FlyoutChrome
{
    public static Border Chip(UIElement content, double width, double height)
    {
        return new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(height / 2),
            Background = Solid(Color.FromArgb(0xEE, TemplateTheme.BgPanel.R, TemplateTheme.BgPanel.G, TemplateTheme.BgPanel.B)),
            BorderBrush = Solid(Color.FromArgb(0x77, TemplateTheme.AccentText.R, TemplateTheme.AccentText.G, TemplateTheme.AccentText.B)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4, 10, 4),
            Child = content,
            SnapsToDevicePixels = true,
            Effect = SoftGlow()
        };
    }

    public static Border Petal(UIElement content, double width, double height)
    {
        return new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(12),
            Background = Solid(Color.FromArgb(0xF2, TemplateTheme.BgPanel.R, TemplateTheme.BgPanel.G, TemplateTheme.BgPanel.B)),
            BorderBrush = Solid(Color.FromArgb(0x66, TemplateTheme.AccentText.R, TemplateTheme.AccentText.G, TemplateTheme.AccentText.B)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4, 8, 4),
            Child = content,
            SnapsToDevicePixels = true
        };
    }

    /// <summary>Плотная оболочка для контента стыка / дуги (пульт, процессы).</summary>
    public static Border ContentShell(UIElement child, double width, Thickness? padding = null)
    {
        return new Border
        {
            Width = width,
            Background = Brushes.Transparent,
            Child = child,
            Padding = padding ?? new Thickness(0),
            SnapsToDevicePixels = true
        };
    }

    static DropShadowEffect SoftGlow()
    {
        var fx = new DropShadowEffect
        {
            Color = Color.FromArgb(0x40, TemplateTheme.AccentText.R, TemplateTheme.AccentText.G, TemplateTheme.AccentText.B),
            BlurRadius = 12,
            ShadowDepth = 0,
            Opacity = 0.45
        };
        fx.Freeze();
        return fx;
    }

    static SolidColorBrush Solid(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
