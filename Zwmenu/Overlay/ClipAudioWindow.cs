using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Zwmenu.Services;

namespace Zwmenu.Overlay;

/// <summary>Вопрос перед клипом: без звука, компьютер, микрофон или оба.</summary>
public sealed class ClipAudioWindow : Window
{
    private ClipAudio _choice = ClipAudio.Silent;

    private ClipAudioWindow(string previous, bool systemOk, bool micOk)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = true;
        Content = Build(previous, systemOk, micOk);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape)
                return;
            DialogResult = false;
            e.Handled = true;
        };
        Loaded += (_, _) => PlaceOnCursorMonitor();
    }

    public static bool Ask(string previous, bool systemOk, bool micOk, out ClipAudio audio, Action<Window> shown)
    {
        var window = new ClipAudioWindow(previous, systemOk, micOk);
        shown(window);
        bool ok = window.ShowDialog() == true;
        audio = ok ? window._choice : ClipAudio.Silent;
        return ok;
    }

    private UIElement Build(string previous, bool systemOk, bool micOk)
    {
        var ink = new SolidColorBrush(Color.FromRgb(0xF5, 0xF0, 0xE8));
        ink.Freeze();
        var stack = new System.Windows.Controls.StackPanel();
        stack.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Как записать звук?",
            Foreground = ink,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 15,
            Margin = new Thickness(2, 0, 2, 12)
        });
        stack.Children.Add(Choice("Без звука", ClipAudio.Silent, previous == "silent", true));
        stack.Children.Add(Choice(systemOk ? "Звук компьютера" : "Звук компьютера — нет устройства", ClipAudio.System, previous == "system", systemOk));
        stack.Children.Add(Choice(micOk ? "Микрофон" : "Микрофон — нет устройства", ClipAudio.Microphone, previous == "mic", micOk));
        stack.Children.Add(Choice(
            systemOk && micOk ? "Компьютер и микрофон" : "Компьютер и микрофон — нет устройства",
            ClipAudio.Both,
            previous == "both",
            systemOk && micOk));

        return new System.Windows.Controls.Border
        {
            Width = 340,
            Padding = new Thickness(14, 14, 14, 12),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0xF4, 0x16, 0x16, 0x1A)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xF5, 0xF0, 0xE8)),
            BorderThickness = new Thickness(1),
            Child = stack
        };
    }

    private System.Windows.Controls.Button Choice(string caption, ClipAudio audio, bool selected, bool enabled)
    {
        var button = new System.Windows.Controls.Button
        {
            Height = 34,
            Margin = new Thickness(0, 0, 0, 6),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
            Padding = new Thickness(12, 0, 12, 0),
            IsEnabled = enabled,
            Opacity = enabled ? 1 : 0.4,
            Cursor = enabled ? Cursors.Hand : Cursors.Arrow,
            Background = new SolidColorBrush(selected && enabled
                ? Color.FromRgb(0x3E, 0x5A, 0x6E)
                : Color.FromRgb(0x2A, 0x2A, 0x2E)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF0, 0xE8)),
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Style = ButtonStyle(),
            Content = caption
        };
        button.Click += (_, _) =>
        {
            if (!enabled)
                return;
            _choice = audio;
            DialogResult = true;
        };
        return button;
    }

    private static Style ButtonStyle()
    {
        var border = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
        border.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(System.Windows.Controls.Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
        border.SetValue(System.Windows.Controls.Border.PaddingProperty, new TemplateBindingExtension(System.Windows.Controls.Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter));
        presenter.SetValue(System.Windows.Controls.ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(System.Windows.Controls.Control.HorizontalContentAlignmentProperty));
        presenter.SetValue(System.Windows.Controls.ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        var template = new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.Button)) { VisualTree = border };
        return new Style(typeof(System.Windows.Controls.Button))
        {
            Setters = { new Setter(System.Windows.Controls.Control.TemplateProperty, template) }
        };
    }

    private void PlaceOnCursorMonitor()
    {
        UpdateLayout();
        double width = ActualWidth > 1 ? ActualWidth : 340;
        double height = ActualHeight > 1 ? ActualHeight : 220;
        var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        Point center = RadialLayout.DeviceToDip(this, area.Left + area.Width / 2.0, area.Top + area.Height / 2.0);
        Point tl = RadialLayout.DeviceToDip(this, area.Left, area.Top);
        Point br = RadialLayout.DeviceToDip(this, area.Right, area.Bottom);
        double left = center.X - width / 2;
        double top = center.Y - height / 2;
        double maxLeft = br.X - width - 8;
        double maxTop = br.Y - height - 8;
        if (maxLeft < tl.X + 8)
            maxLeft = tl.X + 8;
        if (maxTop < tl.Y + 8)
            maxTop = tl.Y + 8;
        if (left < tl.X + 8)
            left = tl.X + 8;
        if (left > maxLeft)
            left = maxLeft;
        if (top < tl.Y + 8)
            top = tl.Y + 8;
        if (top > maxTop)
            top = maxTop;
        Left = left;
        Top = top;
    }
}
