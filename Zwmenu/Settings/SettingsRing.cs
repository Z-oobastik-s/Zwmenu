using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Zwmenu.Services;

namespace Zwmenu.Settings;

public partial class SettingsWindow
{
    private int _ringLook;

    private void InitRingLooks()
    {
        _ringLook = AppearanceStore.GetRingLook();
        RefreshRingLookUi();
    }

    private void OnRingLook(object sender, RoutedEventArgs e)
    {
        if (_suspend || sender is not Button { Tag: string tag } || !int.TryParse(tag, out int look))
            return;
        look = Math.Clamp(look, 0, RingLooks.Count - 1);
        if (look == _ringLook)
            return;
        _ringLook = look;
        var s = AppearanceStore.Current;
        s.RingLook = look;
        // Чтобы новый вид был сразу виден на диске, а не спрятан под PNG.
        if (s.UseRingSkin)
        {
            s.UseRingSkin = false;
            FillRingSkinCombo();
        }

        AppearanceStore.Save(s);
        RefreshRingLookUi();
        Note($"Вид круга: «{RingLooks.Get(look).Title}». Открой колесо колёсиком.");
    }

    private void RefreshRingLookUi()
    {
        Button[] buttons = [RingLook0, RingLook1, RingLook2, RingLook3, RingLook4];
        for (int i = 0; i < buttons.Length; i++)
            PaintRingLook(buttons[i], i == _ringLook);
        var look = RingLooks.Get(_ringLook);
        RingLookHint.Text = look.Hint;
        DrawRingLookPreview(look);
    }

    private static void PaintRingLook(Button button, bool on)
    {
        button.Background = new SolidColorBrush(on
            ? Color.FromRgb(0x3E, 0x5A, 0x6E)
            : Color.FromRgb(0x2A, 0x2A, 0x2E));
        button.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void DrawRingLookPreview(RingLookPalette look)
    {
        RingLookPreview.Children.Clear();
        double cx = 130;
        double cy = 80;
        double glow = 74;
        double disc = 52;
        double orbit = 40;
        double slot = 14;

        var glowEl = new Ellipse
        {
            Width = glow * 2,
            Height = glow * 2,
            Fill = new SolidColorBrush(look.PreviewGlow),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(glowEl, cx - glow);
        Canvas.SetTop(glowEl, cy - glow);
        RingLookPreview.Children.Add(glowEl);

        var discEl = new Ellipse
        {
            Width = disc * 2,
            Height = disc * 2,
            Fill = new SolidColorBrush(look.PreviewCore),
            Stroke = new SolidColorBrush(Color.FromArgb(0x40, look.PreviewRim.R, look.PreviewRim.G, look.PreviewRim.B)),
            StrokeThickness = 1,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(discEl, cx - disc);
        Canvas.SetTop(discEl, cy - disc);
        RingLookPreview.Children.Add(discEl);

        var orbitEl = new Ellipse
        {
            Width = orbit * 2,
            Height = orbit * 2,
            Stroke = new SolidColorBrush(look.PreviewRim),
            StrokeThickness = Math.Max(1.5, look.OrbitThickness),
            Fill = Brushes.Transparent,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(orbitEl, cx - orbit);
        Canvas.SetTop(orbitEl, cy - orbit);
        RingLookPreview.Children.Add(orbitEl);

        for (int i = 0; i < 6; i++)
        {
            double a = -Math.PI / 2 + i * Math.PI * 2 / 6;
            double x = cx + Math.Cos(a) * orbit - slot / 2;
            double y = cy + Math.Sin(a) * orbit - slot / 2;
            var dot = new Ellipse
            {
                Width = slot,
                Height = slot,
                Fill = new SolidColorBrush(Color.FromArgb(0xEE, 0x2A, 0x2E, 0x34)),
                Stroke = new SolidColorBrush(look.PreviewRim),
                StrokeThickness = 1,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(dot, x);
            Canvas.SetTop(dot, y);
            RingLookPreview.Children.Add(dot);
        }

        var center = new Ellipse
        {
            Width = 22,
            Height = 22,
            Fill = new SolidColorBrush(Color.FromArgb(0xF0, look.PreviewCore.R, look.PreviewCore.G, look.PreviewCore.B)),
            Stroke = new SolidColorBrush(look.PreviewRim),
            StrokeThickness = 1.2,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(center, cx - 11);
        Canvas.SetTop(center, cy - 11);
        RingLookPreview.Children.Add(center);
    }
}
