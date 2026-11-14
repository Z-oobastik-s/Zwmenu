using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Zwmenu.Overlay;

/// <summary>
/// Текст диктовки по центру монитора (не у курсора): спокойная плашка, не перекрывает клики.
/// </summary>
public static class DictationHud
{
    private static Window? _window;
    private static TextBlock? _hint;
    private static TextBlock? _body;
    private static Border? _card;
    private static DispatcherTimer? _hideTimer;
    private static string _lastBody = "";

    public static void ShowListening() =>
        Show("слушаю", "", autoHideMs: 0);

    public static void ShowPartial(string text) =>
        Show("слушаю", string.IsNullOrWhiteSpace(text) ? "" : text.Trim(), autoHideMs: 0);

    public static void ShowCleared() =>
        Show("готово", "Текст очищен", autoHideMs: 1500);

    public static void ShowCommand(string title) =>
        Show("команда", string.IsNullOrWhiteSpace(title) ? "Выполняю" : title.Trim(), autoHideMs: 1600);

    public static void ShowError(string text) =>
        Show("ошибка", text.Trim(), autoHideMs: 2200);

    public static void Hide()
    {
        try
        {
            _hideTimer?.Stop();
            _lastBody = "";
            _window?.Hide();
        }
        catch { /* уже закрыто */ }
    }

    private static void Show(string hint, string body, int autoHideMs)
    {
        try
        {
            var app = Application.Current;
            if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(new Action(() => Show(hint, body, autoHideMs)));
                return;
            }

            Ensure();
            if (_window == null || _hint == null || _body == null || _card == null)
                return;

            _hideTimer?.Stop();
            _hint.Text = hint;

            if (!string.Equals(_lastBody, body, StringComparison.Ordinal))
            {
                _body.Text = body;
                _body.Visibility = string.IsNullOrEmpty(body) ? Visibility.Collapsed : Visibility.Visible;
                _lastBody = body;
            }

            PlaceCentered();
            if (!_window.IsVisible)
                _window.Show();

            _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, PlaceCentered);

            if (autoHideMs > 0)
            {
                _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(autoHideMs) };
                _hideTimer.Tick += (_, _) =>
                {
                    try
                    {
                        _hideTimer?.Stop();
                        Hide();
                    }
                    catch { /* ignore */ }
                };
                _hideTimer.Start();
            }
        }
        catch
        {
            // Оверлей не должен валить приложение.
        }
    }

    private static void Ensure()
    {
        if (_window != null)
            return;

        _hint = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Freeze(0x9A, 0xC8, 0xB8),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
            Opacity = 0.9
        };
        _body = new TextBlock
        {
            FontSize = 22,
            FontWeight = FontWeights.Medium,
            Foreground = Freeze(0xF4, 0xF2, 0xEC),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            LineHeight = 30,
            MaxWidth = 720
        };

        var panel = new StackPanel
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Children = { _hint, _body }
        };

        var bg = new SolidColorBrush(Color.FromArgb(0xC8, 0x10, 0x12, 0x16));
        bg.Freeze();
        _card = new Border
        {
            Background = bg,
            BorderBrush = FreezeA(0x55, 0x3A, 0x4A, 0x52),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(28, 18, 28, 20),
            MinWidth = 280,
            MaxWidth = 780,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Child = panel
        };

        // Прозрачный корневой холст на весь экран — контент по центру, клики проходят мимо (IsHitTestVisible=false).
        var root = new Grid { IsHitTestVisible = false };
        root.Children.Add(_card);

        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            Focusable = false,
            IsHitTestVisible = false,
            Content = root
        };
    }

    private static void PlaceCentered()
    {
        if (_window == null || _card == null)
            return;

        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        var wa = screen.WorkingArea;

        _window.Left = wa.Left;
        _window.Top = wa.Top;
        _window.Width = wa.Width;
        _window.Height = wa.Height;

        // Чуть ниже оптического центра — не лезет в глаза и не липнет к курсору.
        _card.Margin = new Thickness(0, 0, 0, Math.Max(48, wa.Height * 0.08));
        _card.VerticalAlignment = VerticalAlignment.Center;
        _card.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;

        double maxText = Math.Clamp(wa.Width * 0.55, 320, 720);
        if (_body != null)
            _body.MaxWidth = maxText;
        _card.MaxWidth = maxText + 56;
    }

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush FreezeA(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
