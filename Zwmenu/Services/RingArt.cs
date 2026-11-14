using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Zwmenu.Services;

/// <summary>Три слоя рисунка на канве: свечение, кольцо, центр, и кружки слотов поверх.</summary>
public static class RingArt
{
    public static bool Place(
        Canvas canvas,
        RingLookPalette look,
        double cx,
        double cy,
        double orbit,
        double slot,
        double center)
    {
        if (!RingLooks.TryLayout(look, orbit, out var lay))
            return false;

        var ink = new SolidColorBrush(look.IconColor);
        ink.Freeze();
        var plate = new SolidColorBrush(look.PlateColor);
        plate.Freeze();

        AddImage(canvas, RingLooks.LayerPath(look, "aura"), cx, cy, lay.AuraRadius * 2, lay.AuraOpacity, 1);
        AddImage(canvas, RingLooks.LayerPath(look, "ring"), cx, cy, lay.RingRadius * 2, 1, 2);
        AddImage(canvas, RingLooks.LayerPath(look, "core"), cx, cy, lay.CoreSize, 1, 8);

        for (int i = 0; i < 8; i++)
        {
            double angle = -Math.PI / 2 + i * Math.PI * 2 / 8;
            AddSlot(canvas, cx + Math.Cos(angle) * orbit, cy + Math.Sin(angle) * orbit, slot, ink, plate, centerSlot: false);
        }

        AddSlot(canvas, cx, cy, center, ink, plate, centerSlot: true);
        return true;
    }

    private static void AddSlot(
        Canvas canvas,
        double x,
        double y,
        double size,
        SolidColorBrush ink,
        SolidColorBrush plate,
        bool centerSlot)
    {
        double plateSize = size * (centerSlot ? 0.50 : 0.58);
        var holder = new Grid
        {
            Width = size,
            Height = size,
            IsHitTestVisible = false
        };
        holder.Children.Add(new Ellipse
        {
            Width = plateSize,
            Height = plateSize,
            Fill = plate,
            Stroke = ink,
            StrokeThickness = 1.6,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        });
        holder.Children.Add(new Ellipse
        {
            Width = plateSize * 0.72,
            Height = plateSize * 0.72,
            Stroke = ink,
            StrokeThickness = 1,
            Opacity = 0.55,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        });
        holder.Children.Add(new TextBlock
        {
            Text = "\uE700",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = size * (centerSlot ? 0.26 : 0.26),
            Foreground = ink,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            TextAlignment = System.Windows.TextAlignment.Center
        });
        Canvas.SetLeft(holder, x - size / 2);
        Canvas.SetTop(holder, y - size / 2);
        Panel.SetZIndex(holder, centerSlot ? 20 : 10);
        canvas.Children.Add(holder);
    }

    private static void AddImage(Canvas canvas, string? path, double cx, double cy, double size, double opacity, int z)
    {
        if (path == null || size < 2)
            return;

        var source = RingImageCache.GetSkin(path, size > 180 ? 900 : 420);
        if (source == null)
            return;

        var image = new System.Windows.Controls.Image
        {
            Width = size,
            Height = size,
            Source = source,
            Stretch = Stretch.Uniform,
            Opacity = opacity,
            IsHitTestVisible = false
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Fant);
        Canvas.SetLeft(image, Math.Round(cx - size / 2));
        Canvas.SetTop(image, Math.Round(cy - size / 2));
        Panel.SetZIndex(image, z);
        canvas.Children.Add(image);
    }
}
