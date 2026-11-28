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
        Width = 480;
        SizeToContent = SizeToContent.Height;
        FontFamily = new FontFamily(UiTheme.Current.FontUi);
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

        var scroll = TrayPaint.HiddenScroll(cards);
        scroll.MaxHeight = 420;
        var body = new StackPanel();
        body.Children.Add(ForgeChrome.BrandHead("Zwmenu", "Где что лежит, и что будет, если нажать."));
        body.Children.Add(scroll);

        var shell = ForgeChrome.Shell(body, new Thickness(14, 12, 14, 10));
        Content = shell;
        ContentRendered += (_, _) =>
        {
            double room = TrayPaint.Room(
                System.Windows.Forms.Cursor.Position.X,
                System.Windows.Forms.Cursor.Position.Y);
            scroll.MaxHeight = Math.Max(180, Math.Min(420, room - 120));
            ForgeChrome.FadeIn(shell);
        };
        PreviewMouseWheel += (_, e) =>
        {
            if (scroll.ScrollableHeight <= 0)
                return;
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta * 0.45);
            e.Handled = true;
        };
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

    static UIElement Line(string gesture, string text)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var rail = new System.Windows.Controls.Border
        {
            Width = 3,
            Margin = new Thickness(0, 2, 10, 2),
            Background = TemplateTheme.Brush(TemplateTheme.AccentText),
            CornerRadius = new CornerRadius(1.5),
            Opacity = 0.75,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Grid.SetColumn(rail, 0);
        row.Children.Add(rail);

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = gesture,
            Foreground = TemplateTheme.Brush(TemplateTheme.AccentText),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily(UiTheme.Current.FontUi)
        });
        stack.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            LineHeight = 17,
            FontFamily = new FontFamily(UiTheme.Current.FontUi)
        });
        Grid.SetColumn(stack, 1);
        row.Children.Add(stack);

        return ForgeChrome.Card(row, new Thickness(10, 8, 10, 8));
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