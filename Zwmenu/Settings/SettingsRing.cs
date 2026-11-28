using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Zwmenu.Overlay;
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
        AppearanceStore.SetRingLook(look);
        RefreshRingLookUi();
        RefreshUiSkinUi();
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

    private string _uiSkin = UiTheme.DefaultId;
    private readonly List<Button> _uiSkinButtons = new();

    private void InitUiSkins()
    {
        if (UiSkinGrid == null)
            return;
        UiSkinGrid.Children.Clear();
        _uiSkinButtons.Clear();
        foreach (var skin in UiTheme.Catalog)
        {
            var button = new Button
            {
                Content = skin.Title,
                Tag = skin.Id,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(8, 8, 8, 8),
                FontSize = 12.5
            };
            button.Click += OnUiSkin;
            UiSkinGrid.Children.Add(button);
            _uiSkinButtons.Add(button);
        }

        _uiSkin = AppearanceStore.Current.UiSkin;
        if (UiSkinFollowBox != null)
        {
            UiSkinFollowBox.IsChecked = AppearanceStore.Current.UiSkinFollowRing;
            UiSkinFollowBox.Checked += OnUiSkinFollow;
            UiSkinFollowBox.Unchecked += OnUiSkinFollow;
        }
        RefreshUiSkinUi();
    }

    private void OnUiSkin(object sender, RoutedEventArgs e)
    {
        if (_suspend || sender is not Button { Tag: string id })
            return;
        id = UiTheme.Get(id).Id;
        if (string.Equals(id, _uiSkin, StringComparison.OrdinalIgnoreCase))
            return;
        _uiSkin = id;
        AppearanceStore.SetUiSkin(id, followRing: false);
        if (UiSkinFollowBox != null)
            UiSkinFollowBox.IsChecked = false;
        RefreshUiSkinUi();
        Note($"Тема окон: «{UiTheme.Get(id).Title}». Новые панели возьмут её сразу.");
    }

    private void OnUiSkinFollow(object sender, RoutedEventArgs e)
    {
        if (_suspend || UiSkinFollowBox == null)
            return;
        bool follow = UiSkinFollowBox.IsChecked == true;
        var s = AppearanceStore.Current;
        s.UiSkinFollowRing = follow;
        if (follow)
            s.UiSkin = UiTheme.SuggestForRingLook(s.RingLook);
        AppearanceStore.Save(s);
        _uiSkin = s.UiSkin;
        RefreshUiSkinUi();
    }

    private void RefreshUiSkinUi()
    {
        if (_uiSkinButtons.Count == 0)
            return;
        _uiSkin = AppearanceStore.Current.UiSkin;
        for (int i = 0; i < _uiSkinButtons.Count; i++)
        {
            var btn = _uiSkinButtons[i];
            bool on = btn.Tag is string id &&
                      string.Equals(id, _uiSkin, StringComparison.OrdinalIgnoreCase);
            PaintRingLook(btn, on);
        }
        var skin = UiTheme.Get(_uiSkin);
        if (UiSkinHint != null)
            UiSkinHint.Text = skin.Hint;
    }

    private static void PaintRingLook(Button button, bool on)
    {
        var accent = TemplateTheme.Accent;
        var idle = TemplateTheme.BtnBg;
        button.Background = new SolidColorBrush(on ? accent : idle);
        button.Foreground = new SolidColorBrush(on ? TemplateTheme.AccentText : TemplateTheme.Fg);
        button.BorderBrush = new SolidColorBrush(on
            ? Color.FromArgb(0x88, TemplateTheme.AccentText.R, TemplateTheme.AccentText.G, TemplateTheme.AccentText.B)
            : TemplateTheme.Border);
        button.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private string _flyoutStyle = FlyoutLooks.DefaultStyleId;
    private string _flyoutMotion = FlyoutLooks.DefaultMotionId;
    private readonly List<Button> _flyoutStyleButtons = new();
    private readonly List<Button> _flyoutMotionButtons = new();

    private void InitFlyoutLooks()
    {
        if (FlyoutStyleGrid == null)
            return;

        FlyoutStyleGrid.Children.Clear();
        _flyoutStyleButtons.Clear();
        foreach (var style in FlyoutLooks.StyleCatalog)
        {
            var button = new Button
            {
                Content = style.Title,
                Tag = style.Id,
                Width = 132,
                Height = 36,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(8, 0, 8, 0),
                FontSize = 13
            };
            button.Click += OnFlyoutStyle;
            FlyoutStyleGrid.Children.Add(button);
            _flyoutStyleButtons.Add(button);
        }

        _flyoutMotionButtons.Clear();
        _flyoutStyle = AppearanceStore.Current.FlyoutStyle;
        _flyoutMotion = AppearanceStore.Current.FlyoutMotion;
        RefreshFlyoutLooksUi();
    }

    private void OnFlyoutStyle(object sender, RoutedEventArgs e)
    {
        if (_suspend || sender is not Button { Tag: string id })
            return;
        id = FlyoutLooks.NormalizeStyle(id);
        if (string.Equals(id, _flyoutStyle, StringComparison.OrdinalIgnoreCase))
            return;
        _flyoutStyle = id;
        AppearanceStore.SetFlyoutStyle(id);
        var style = FlyoutLooks.GetStyle(id);
        AppearanceStore.SetFlyoutMotion(style.MotionId);
        _flyoutMotion = style.MotionId;
        RefreshFlyoutLooksUi();
        Note($"Форма: «{style.Title}». Открой колесо и наведи на группу.");
    }

    private void RefreshFlyoutLooksUi()
    {
        _flyoutStyle = AppearanceStore.Current.FlyoutStyle;
        _flyoutMotion = AppearanceStore.Current.FlyoutMotion;
        foreach (var btn in _flyoutStyleButtons)
        {
            bool on = btn.Tag is string id &&
                      string.Equals(id, _flyoutStyle, StringComparison.OrdinalIgnoreCase);
            PaintRingLook(btn, on);
        }

        if (FlyoutStyleHint != null)
        {
            var style = FlyoutLooks.GetStyle(_flyoutStyle);
            FlyoutStyleHint.Text = $"Выбрано: {style.Title}. {style.Hint}";
        }
    }

    private void DrawRingLookPreview(RingLookPalette look)
    {
        RingLookPreview.Children.Clear();
        if (RingArt.Place(RingLookPreview, look, 134, 78, 46, 18, 26))
            return;

        double cx = 134;
        double cy = 78;
        double glow = 58;
        double disc = 40;
        double orbit = 32;
        double slot = 12;

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
