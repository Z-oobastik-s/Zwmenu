using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Zwmenu.Overlay;

/// <summary>Цвета и кнопки как в Program Text Buffer: тёмный фон, пилюли, без «дефолтного» WPF.</summary>
static class TemplateTheme
{
    public static readonly Color BgDeep = Color.FromRgb(0x07, 0x07, 0x08);
    public static readonly Color BgPanel = Color.FromRgb(0x12, 0x12, 0x13);
    public static readonly Color BgTitle = Color.FromRgb(0x0D, 0x0D, 0x0E);
    public static readonly Color Fg = Color.FromRgb(0xF0, 0xF0, 0xF0);
    public static readonly Color FgDim = Color.FromRgb(0x7A, 0x7A, 0x7A);
    public static readonly Color Border = Color.FromRgb(0x2C, 0x2C, 0x2E);
    public static readonly Color ListBg = Color.FromRgb(0x0E, 0x0E, 0x10);
    public static readonly Color ListSel = Color.FromRgb(0x3F, 0x3F, 0x42);
    public static readonly Color BtnBg = Color.FromRgb(0x24, 0x24, 0x26);
    public static readonly Color BtnHover = Color.FromRgb(0x32, 0x32, 0x34);
    public static readonly Color Accent = Color.FromRgb(0x3A, 0x3A, 0x40);
    public static readonly Color AccentHover = Color.FromRgb(0x48, 0x48, 0x4F);
    public static readonly Color AccentSoft = Color.FromRgb(0x1A, 0x2E, 0x2A);
    public static readonly Color AccentText = Color.FromRgb(0x7D, 0xD4, 0xC0);
    public static readonly Color GiveAccent = Color.FromRgb(0x3D, 0x9F, 0x88);

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static Border Pill(string text, Action click, bool accent = false, bool danger = false)
    {
        var fill = accent ? Accent : (danger ? Color.FromRgb(0x3A, 0x22, 0x24) : BtnBg);
        var hover = accent ? AccentHover : (danger ? Color.FromRgb(0x52, 0x2C, 0x30) : BtnHover);
        var borderColor = accent || danger ? Color.FromRgb(0xC8, 0xC8, 0xC8) : Border;
        var label = new TextBlock
        {
            Text = text,
            Foreground = Brush(Fg),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            FontWeight = accent ? FontWeights.SemiBold : FontWeights.Normal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        var border = new Border
        {
            Background = Brush(fill),
            BorderBrush = Brush(borderColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
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
            try { click(); } catch { /* кнопка не валит окно */ }
        };
        return border;
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
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13
        };
        if (multi)
        {
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        return box;
    }

    public static System.Windows.Controls.CheckBox Tick(string text)
    {
        var box = new System.Windows.Controls.CheckBox
        {
            Content = text,
            Foreground = Brush(Fg),
            FontFamily = new FontFamily("Segoe UI"),
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
        glyph.SetValue(TextBlock.ForegroundProperty, Brush(Fg));
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
            trigger.Setters.Add(new Setter(System.Windows.Controls.Border.BorderBrushProperty, Brush(Color.FromRgb(0xC8, 0xC8, 0xC8)), "Mark"));
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
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Padding = new Thickness(2)
        };
        list.Resources[System.Windows.SystemColors.HighlightBrushKey] = Brush(ListSel);
        list.Resources[System.Windows.SystemColors.HighlightTextBrushKey] = Brush(Fg);
        list.Resources[System.Windows.SystemColors.InactiveSelectionHighlightBrushKey] = Brush(ListSel);
        list.Resources[System.Windows.SystemColors.InactiveSelectionHighlightTextBrushKey] = Brush(Fg);
        return list;
    }

    public static void StyleWindow(Window window)
    {
        window.Background = Brush(BgDeep);
        window.Foreground = Brush(Fg);
        window.FontFamily = new FontFamily("Segoe UI");
        window.WindowStyle = WindowStyle.SingleBorderWindow;
        window.Effect = null;
    }
}
