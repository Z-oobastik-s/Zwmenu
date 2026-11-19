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
        Width = 460;
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
            cards.Children.Add(Line("Памятка", "Настройки не прочитались. Меню откроется правым кликом по кубику у часов."));
        }

        var root = new Border
        {
            Background = TrayPaint.Brush(Color.FromRgb(0x10, 0x11, 0x13)),
            BorderBrush = TrayPaint.Brush(Color.FromRgb(0x3A, 0x3C, 0x40)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(16, 14, 16, 10),
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
        TrayIconService.HoldTip();
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
        TrayIconService.ReleaseTip();
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
        var stack = new StackPanel { Margin = new Thickness(2, 0, 2, 8) };
        stack.Children.Add(new TextBlock
        {
            Text = "Zwmenu",
            Foreground = TrayPaint.Brush(TemplateTheme.Fg),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        });
        stack.Children.Add(new TextBlock
        {
            Text = "Где что лежит, и что будет, если нажать.",
            Foreground = TrayPaint.Brush(TemplateTheme.FgDim),
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0)
        });
        return stack;
    }

    static Border Line(string gesture, string text)
    {
        var mark = new TextBlock
        {
            Text = gesture,
            Foreground = TrayPaint.Brush(TemplateTheme.AccentText),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold
        };
        var body = new TextBlock
        {
            Text = text,
            Foreground = TrayPaint.Brush(Color.FromRgb(0xD8, 0xD9, 0xDA)),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
            LineHeight = 17
        };
        var row = new StackPanel();
        row.Children.Add(mark);
        row.Children.Add(body);
        return new Border
        {
            Padding = new Thickness(2, 7, 2, 7),
            BorderBrush = TrayPaint.Brush(Color.FromRgb(0x2A, 0x2C, 0x30)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = row
        };
    }

    static IEnumerable<(string Gesture, string Text)> Cards()
    {
        var shot = ScreenshotStore.Current;
        string shotKey = ScreenshotStore.HotkeyTitle(shot.Hotkey);
        string clipKey = ScreenshotStore.HotkeyTitle(shot.ClipHotkey);
        string translateKey = ScreenshotStore.HotkeyTitle(shot.TranslateHotkey);
        string wheel = AppearanceStore.GetMiddleClickMode() switch
        {
            MiddleButtonMode.Instant => "Нажатие сразу открывает круглое меню программ.",
            MiddleButtonMode.Alt => "Круглое меню программ открывается только вместе с Alt.",
            _ => "Подержи её чуть дольше обычного щелчка: откроется круглое меню программ. Короткий щелчок остаётся обычным кликом."
        };
        yield return ("Кубик у часов", "Значок Zwmenu в трее, рядом с часами. Наведение открывает эту памятку. Правый клик открывает меню. Двойной клик открывает статус.");
        yield return ("Средняя кнопка мыши", wheel);
        yield return ("Снимок экрана", "Клавиша " + shotKey + ". На экране рамка, выделенное копируется. Пять последних снимков лежат в меню кубика у часов.");
        yield return ("Короткое видео", "Клавиша " + clipKey + ". Пишет кусок экрана. Esc останавливает запись раньше, чем она кончится сама.");
        yield return ("Перевод", "Клавиша " + translateKey + ". Берёт текст, который уже скопирован, и предлагает перевод.");
        yield return (DictationGesture(shot), DictationBody(shot));
        yield return ("Край монитора", "Уведи курсор за край экрана. Курсор и файлы переходят на второй ноутбук.");
        yield return ("Тембр микрофона", VoiceBody(shot));
        yield return ("Ctrl на файле", "Подержи Ctrl на файле или ярлыке: откроется круг действий. Ctrl+C копирует как обычно и этот круг не открывает.");
    }

    static string DictationGesture(ScreenshotSettings shot)
    {
        if (shot.DictationSideButton == 2)
            return "Вторая боковая кнопка мыши";
        if (shot.DictationSideButton == 1)
            return "Боковая кнопка мыши";
        if (shot.DictationHotkey != 0)
            return "Диктовка, клавиша " + ScreenshotStore.HotkeyTitle(shot.DictationHotkey);
        return "Диктовка";
    }

    static string DictationBody(ScreenshotSettings shot)
    {
        if (shot.DictationSideButton == 0 && shot.DictationHotkey == 0)
            return "Сейчас не назначена. В настройках ставится боковая кнопка мыши или клавиша. Зажал и говоришь, отпустил: текст в окне.";
        string extra = shot.DictationSideButton != 0 && shot.DictationHotkey != 0
            ? " То же делает клавиша " + ScreenshotStore.HotkeyTitle(shot.DictationHotkey) + "."
            : "";
        return "Зажми и говори. Отпусти: текст встанет в окно под курсором." + extra;
    }

    static string VoiceBody(ScreenshotSettings shot)
    {
        if (shot.VoiceShiftToggleKey == 0)
            return "Меняет голос в микрофоне. Включается в меню кубика у часов.";
        return "Клавиша " + ScreenshotStore.HotkeyTitle(shot.VoiceShiftToggleKey) + " включает его. Ещё он есть в меню кубика у часов.";
    }
}
