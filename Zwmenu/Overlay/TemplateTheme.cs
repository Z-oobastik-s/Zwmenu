using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Zwmenu.Services;

namespace Zwmenu.Overlay;

/// <summary>
/// ????? ?????????? ???? ????. ????? ?? UiTheme.Current (Signal Forge ? ??.).
/// ??? ??????? ?????: i3 ? ?????? GPU ?? ?????? ??????????.
/// </summary>
static class TemplateTheme
{
    public static Color BgDeep => UiTheme.Current.BgDeep;
    public static Color BgPanel => UiTheme.Current.BgPanel;
    public static Color BgTitle => UiTheme.Current.BgTitle;
    public static Color Fg => UiTheme.Current.Fg;
    public static Color FgDim => UiTheme.Current.FgDim;
    public static Color Border => UiTheme.Current.Border;
    public static Color ListBg => UiTheme.Current.ListBg;
    public static Color ListSel => UiTheme.Current.ListSel;
    public static Color BtnBg => UiTheme.Current.BtnBg;
    public static Color BtnHover => UiTheme.Current.BtnHover;
    public static Color Accent => UiTheme.Current.Accent;
    public static Color AccentHover => UiTheme.Current.AccentHover;
    public static Color AccentSoft => UiTheme.Current.AccentSoft;
    public static Color AccentText => UiTheme.Current.AccentText;
    public static Color Danger => UiTheme.Current.Danger;
    public static Color DangerHover => UiTheme.Current.DangerHover;
    public static Color StatusOk => UiTheme.Current.StatusOk;
    public static Color StatusWarn => UiTheme.Current.StatusWarn;
    public static Color GiveAccent => AccentText;

    static FontFamily UiFont => new(UiTheme.Current.FontUi);

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static void Hint(FrameworkElement el, string text)
    {
        ToolTipService.SetInitialShowDelay(el, 2000);
        ToolTipService.SetBetweenShowDelay(el, 1500);
        ToolTipService.SetShowDuration(el, 5000);
        el.ToolTip = new ToolTip
        {
            Content = text,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            Background = Brush(BgPanel),
            Foreground = Brush(Fg),
            BorderBrush = Brush(Border),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 5, 8, 5),
            FontFamily = UiFont,
            FontSize = 12,
            HasDropShadow = false
        };
    }

    public static System.Windows.Controls.Border Pill(string text, Action click, bool accent = false, bool danger = false, string? hint = null)
    {
        var fill = accent ? Accent : (danger ? Danger : BtnBg);
        var hover = accent ? AccentHover : (danger ? DangerHover : BtnHover);
        var fg = accent ? AccentText : Fg;
        var borderColor = accent
            ? Color.FromArgb(0x66, AccentText.R, AccentText.G, AccentText.B)
            : danger
                ? Color.FromArgb(0x88, 0xC8, 0x90, 0x90)
                : Border;
        var label = new TextBlock
        {
            Text = text,
            Foreground = Brush(fg),
            FontFamily = UiFont,
            FontSize = 12,
            FontWeight = accent ? FontWeights.SemiBold : FontWeights.Normal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var border = new System.Windows.Controls.Border
        {
            Background = Brush(fill),
            BorderBrush = Brush(borderColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(UiTheme.Current.CornerPill),
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 6, 6),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = label,
            SnapsToDevicePixels = true
        };
        border.MouseEnter += (_, _) => border.Background = Brush(hover);
        border.MouseLeave += (_, _) => border.Background = Brush(fill);
        border.MouseLeftButtonUp += (_, _) =>
        {
            try { click(); } catch { /* ?????? ?? ????? ???? */ }
        };
        if (!string.IsNullOrWhiteSpace(hint))
            Hint(border, hint);
        return border;
    }

    public static System.Windows.Controls.Border Frame(UIElement content, Thickness? padding = null, double? radius = null)
    {
        return new System.Windows.Controls.Border
        {
            Background = Brush(BgPanel),
            BorderBrush = Brush(Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radius ?? UiTheme.Current.CornerPanel),
            Padding = padding ?? new Thickness(10),
            Child = content,
            SnapsToDevicePixels = true
        };
    }

    public static System.Windows.Controls.Border TitleBar(string title, MouseButtonEventHandler? drag = null)
    {
        var row = new DockPanel { LastChildFill = true };
        var mark = new System.Windows.Controls.Border
        {
            Width = 3,
            Margin = new Thickness(0, 2, 8, 2),
            Background = Brush(AccentText),
            CornerRadius = new CornerRadius(1.5),
            Opacity = 0.9
        };
        DockPanel.SetDock(mark, Dock.Left);
        row.Children.Add(mark);
        row.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Brush(Fg),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = UiFont
        });

        var bar = new System.Windows.Controls.Border
        {
            Background = Brush(BgTitle),
            BorderBrush = Brush(Border),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 8, 10, 8),
            Child = row,
            Cursor = Cursors.SizeAll,
            SnapsToDevicePixels = true
        };
        if (drag != null)
            bar.MouseLeftButtonDown += drag;
        return bar;
    }

    public static System.Windows.Controls.Border Chip(string text, bool soft = false)
    {
        return new System.Windows.Controls.Border
        {
            Background = Brush(soft ? AccentSoft : BtnBg),
            BorderBrush = Brush(Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(UiTheme.Current.CornerChip),
            Padding = new Thickness(8, 3, 8, 3),
            Child = new TextBlock
            {
                Text = text,
                Foreground = Brush(soft ? AccentText : FgDim),
                FontSize = 11,
                FontFamily = UiFont
            },
            SnapsToDevicePixels = true
        };
    }

    public static System.Windows.Controls.TextBox Field(bool multi = false)
    {
        var box = new System.Windows.Controls.TextBox
        {
            Background = Brush(ListBg),
            Foreground = Brush(Fg),
            CaretBrush = Brush(Fg),
            BorderBrush = Brush(Border),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7, 10, 7),
            FontFamily = UiFont,
            FontSize = 13
        };
        if (multi)
        {
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        }
        return box;
    }

    public static System.Windows.Controls.CheckBox Tick(string text)
    {
        var box = new System.Windows.Controls.CheckBox
        {
            Content = text,
            Foreground = Brush(Fg),
            FontFamily = UiFont,
            FontSize = 13,
            Cursor = Cursors.Hand,
            FocusVisualStyle = null
        };

        var mark = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
        mark.SetValue(FrameworkElement.WidthProperty, 15.0);
        mark.SetValue(FrameworkElement.HeightProperty, 15.0);
        mark.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(3));
        mark.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new Thickness(1));
        mark.SetValue(System.Windows.Controls.Border.BorderBrushProperty, Brush(Border));
        mark.SetValue(System.Windows.Controls.Border.BackgroundProperty, Brush(ListBg));
        mark.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        mark.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 1, 8, 0));
        mark.Name = "Mark";

        var glyph = new FrameworkElementFactory(typeof(TextBlock));
        glyph.SetValue(TextBlock.TextProperty, "\u2713");
        glyph.SetValue(TextBlock.FontSizeProperty, 11.0);
        glyph.SetValue(TextBlock.ForegroundProperty, Brush(AccentText));
        glyph.SetValue(TextBlock.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        glyph.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        glyph.SetValue(TextBlock.MarginProperty, new Thickness(0, -1, 0, 0));
        glyph.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        glyph.Name = "Glyph";
        mark.AppendChild(glyph);

        var caption = new FrameworkElementFactory(typeof(ContentPresenter));
        caption.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        caption.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);

        var row = new FrameworkElementFactory(typeof(StackPanel));
        row.SetValue(StackPanel.OrientationProperty, System.Windows.Controls.Orientation.Horizontal);
        row.AppendChild(mark);
        row.AppendChild(caption);

        var template = new ControlTemplate(typeof(System.Windows.Controls.CheckBox)) { VisualTree = row };
        template.Triggers.Add(Checked());
        box.Template = template;
        return box;

        static Trigger Checked()
        {
            var trigger = new Trigger
            {
                Property = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                Value = true
            };
            trigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "Glyph"));
            trigger.Setters.Add(new Setter(System.Windows.Controls.Border.BackgroundProperty, Brush(Accent), "Mark"));
            trigger.Setters.Add(new Setter(System.Windows.Controls.Border.BorderBrushProperty,
                Brush(Color.FromArgb(0xAA, AccentText.R, AccentText.G, AccentText.B)), "Mark"));
            return trigger;
        }
    }

    public static System.Windows.Controls.ListBox DarkList()
    {
        var list = new System.Windows.Controls.ListBox
        {
            Background = Brush(ListBg),
            Foreground = Brush(Fg),
            BorderBrush = Brush(Border),
            BorderThickness = new Thickness(1),
            FontFamily = UiFont,
            FontSize = 13,
            Padding = new Thickness(2)
        };
        list.Resources[System.Windows.SystemColors.HighlightBrushKey] = Brush(ListSel);
        list.Resources[System.Windows.SystemColors.HighlightTextBrushKey] = Brush(Fg);
        list.Resources[System.Windows.SystemColors.InactiveSelectionHighlightBrushKey] = Brush(ListSel);
        list.Resources[System.Windows.SystemColors.InactiveSelectionHighlightTextBrushKey] = Brush(Fg);
        return list;
    }

    public static void StyleWindow(Window window) => ApplyChrome(window);

    public static void ApplyChrome(Window window, bool allowShadow = false)
    {
        window.Background = Brush(BgDeep);
        window.Foreground = Brush(Fg);
        window.FontFamily = UiFont;
        window.WindowStyle = WindowStyle.SingleBorderWindow;
        window.Effect = allowShadow ? SoftShadow() : null;
    }

    public static DropShadowEffect SoftShadow()
    {
        var e = new DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 10,
            ShadowDepth = 2,
            Opacity = 0.28,
            Direction = 270,
            RenderingBias = RenderingBias.Performance
        };
        e.Freeze();
        return e;
    }
}
