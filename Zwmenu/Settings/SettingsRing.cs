using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Zwmenu.Services;

namespace Zwmenu.Settings;

public partial class SettingsWindow
{
    private int _ringLook;
    private readonly List<Button> _lookButtons = new();

    private void InitRingLooks()
    {
        RingLookGrid.Children.Clear();
        _lookButtons.Clear();
        foreach (var (id, title, _) in RingLooks.Catalog())
        {
            var button = new Button
            {
                Content = title,
                Tag = id.ToString(),
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(4, 8, 4, 8),
                FontSize = 12.5
            };
            button.Click += OnRingLook;
            RingLookGrid.Children.Add(button);
            _lookButtons.Add(button);
        }

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
        s.UseRingSkin = false;
        AppearanceStore.Save(s);
        RefreshRingLookUi();
        Note($"Вид круга: «{RingLooks.Get(look).Title}». Открой колесо колёсиком.");
    }

    private void RefreshRingLookUi()
    {
        for (int i = 0; i < _lookButtons.Count; i++)
            PaintRingLook(_lookButtons[i], i == _ringLook);
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
        if (RingArt.Place(RingLookPreview, look, 152, 110, 56, 22, 30))
            return;

        double cx = 152;
        double cy = 110;
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
