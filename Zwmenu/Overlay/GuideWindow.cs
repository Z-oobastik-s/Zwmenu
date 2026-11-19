using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Zwmenu.Services;

namespace Zwmenu.Overlay;

/// <summary>Шпаргалка вместо длинной подсказки у значка. Жесты читаются из текущих настроек.</summary>
public sealed class GuideWindow : Window
{
    static GuideWindow? _open;
    bool _inside;

    GuideWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = 390;
        SizeToContent = SizeToContent.Height;
        FontFamily = new FontFamily("Segoe UI");
        MouseEnter += (_, _) => _inside = true;
        MouseLeave += (_, _) => _inside = false;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };

        var cards = new StackPanel();
        try
        {
            foreach (var card in Cards())
                cards.Children.Add(Line(card.Gesture, card.Text));
        }
        catch
        {
            cards.Children.Add(Line("—", "Настройки не прочитались. Меню откроется правым кликом."));
        }

        var root = new Border
        {
            Background = TrayPaint.Brush(Color.FromRgb(0x10, 0x11, 0x13)),
            BorderBrush = TrayPaint.Brush(Color.FromRgb(0x3A, 0x3C, 0x40)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14, 12, 14, 8),
            Effect = TrayPaint.Shadow(),
            Child = new StackPanel
            {
                Children =
                {
                    TitleBlock(),
                    cards
                }
            }
        };
        Content = root;
    }

    public static void ShowNear(int x, int y)
    {
        var app = Application.Current;
        if (app == null)
            return;
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(() => ShowNear(x, y));
            return;
        }

        _open ??= new GuideWindow();
        _open.Left = -4000;
        _open.Top = -4000;
        if (!_open.IsVisible)
            _open.Show();
        _open.UpdateLayout();
        TrayPaint.PlaceInCorner(_open, x, y);
        TrayDismiss.Arm(_open, () => PointerInside(), CloseGuide);
    }

    public static void CloseGuide()
    {
        if (_open == null)
            return;
        var window = _open;
        _open = null;
        TrayDismiss.Disarm(window);
        try { window.Close(); } catch { /* уже закрыто */ }
    }

    public static bool IsShown => _open is { IsVisible: true };

    public static bool PointerInside()
    {
        if (_open is not { IsVisible: true } window)
            return false;
        if (window._inside)
            return true;
        try
        {
            var p = window.PointFromScreen(new Point(
                System.Windows.Forms.Cursor.Position.X,
                System.Windows.Forms.Cursor.Position.Y));
            return p.X >= -8 && p.Y >= -8 && p.X <= window.ActualWidth + 8 && p.Y <= window.ActualHeight + 8;
        }
        catch
        {
            return false;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (ReferenceEquals(_open, this))
            _open = null;
        base.OnClosed(e);
    }

    static UIElement TitleBlock()
    {
        var stack = new StackPanel { Margin = new Thickness(2, 0, 2, 6) };
        stack.Children.Add(new TextBlock
        {
            Text = "Zwmenu",
            Foreground = TrayPaint.Brush(TemplateTheme.Fg),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        });
        return stack;
    }

    static Border Line(string gesture, string text)
    {
        var mark = new TextBlock
        {
            Text = gesture,
            Foreground = TrayPaint.Brush(TemplateTheme.AccentText),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        var body = new TextBlock
        {
            Text = text,
            Foreground = TrayPaint.Brush(Color.FromRgb(0xD2, 0xD3, 0xD4)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(body, 1);
        row.Children.Add(mark);
        row.Children.Add(body);
        return new Border
        {
            Padding = new Thickness(2, 5, 2, 5),
            BorderBrush = TrayPaint.Brush(Color.FromRgb(0x2A, 0x2C, 0x30)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = row
        };
    }

    static IEnumerable<(string Gesture, string Text)> Cards()
    {
        var shot = ScreenshotStore.Current;
        string wheel = AppearanceStore.GetMiddleClickMode() switch
        {
            MiddleButtonMode.Instant => "Колесо сразу по средней кнопке.",
            MiddleButtonMode.Alt => "Колесо только с Alt и средней кнопкой.",
            _ => "Подержи среднюю кнопку — откроется колесо. Короткий щелчок обычный."
        };
        yield return ("Значок", "Наведение — это окно. Правый клик — меню. Двойной — статус.");
        yield return ("Колесо", wheel);
        yield return (ScreenshotStore.HotkeyTitle(shot.Hotkey), "Снимок: рамка и копия. Пять последних лежат в меню значка.");
        yield return (ScreenshotStore.HotkeyTitle(shot.ClipHotkey), "Короткое видео. Esc останавливает раньше.");
        yield return (ScreenshotStore.HotkeyTitle(shot.TranslateHotkey), "Перевод текста, который уже скопирован.");
        yield return (DictationGesture(shot), DictationBody(shot));
        yield return ("Край", "Курсор и файлы переходят на второй ноутбук.");
        string voice = shot.VoiceShiftToggleKey == 0 ? "Голос" : ScreenshotStore.HotkeyTitle(shot.VoiceShiftToggleKey);
        yield return (voice, "Тембр микрофона. Включается в меню значка.");
        yield return ("Ctrl", "На файле долгое удержание открывает действия. Ctrl+C не мешает.");
    }

    static string DictationGesture(ScreenshotSettings shot)
    {
        string side = shot.DictationSideButton switch
        {
            1 => "Боковая",
            2 => "Боковая 2",
            _ => ""
        };
        if (side.Length > 0)
            return side;
        if (shot.DictationHotkey != 0)
            return ScreenshotStore.HotkeyTitle(shot.DictationHotkey);
        return "Нет";
    }

    static string DictationBody(ScreenshotSettings shot)
    {
        string extra = shot.DictationSideButton != 0 && shot.DictationHotkey != 0
            ? " Ещё " + ScreenshotStore.HotkeyTitle(shot.DictationHotkey) + "."
            : "";
        if (shot.DictationSideButton == 0 && shot.DictationHotkey == 0)
            return "Не назначена. Зажал — говоришь, отпустил — текст в окне.";
        return "Зажал — говоришь, отпустил — текст в окне." + extra;
    }
}
