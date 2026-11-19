using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Zwmenu.Services;

namespace Zwmenu.Overlay;

/// <summary>Предлагает поставить то, без чего программа на этом компьютере не заработает.</summary>
public sealed class SetupOfferWindow : Window
{
    static SetupOfferWindow? _open;

    readonly TextBlock _text;
    readonly StackPanel _buttons;
    bool _busy;
    bool _alive = true;

    SetupOfferWindow()
    {
        Title = "Нужна установка";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = true;
        Cursor = Cursors.Arrow;
        TemplateTheme.StyleWindow(this);

        _text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = TemplateTheme.Brush(TemplateTheme.Fg),
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 14),
            Text = "Для голоса нужен виртуальный кабель. Без него микрофон в Telegram и игры не подменится. Могу скачать официальный VB-Cable и открыть установщик."
        };
        _buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        _buttons.Children.Add(TemplateTheme.Pill("Установить", Install, accent: true));
        _buttons.Children.Add(TemplateTheme.Pill("Позже", Later));

        var root = new StackPanel { Margin = new Thickness(16, 14, 16, 12) };
        root.Children.Add(_text);
        root.Children.Add(_buttons);
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || _busy)
                return;
            e.Handled = true;
            Later();
        };
        ContentRendered += (_, _) => Place();
        Closed += (_, _) =>
        {
            _alive = false;
            if (ReferenceEquals(_open, this))
                _open = null;
        };
    }

    public static void ShowOffer(bool stealFocus)
    {
        var window = _open != null && _open._alive ? _open : new SetupOfferWindow();
        _open = window;
        if (window._busy)
        {
            if (!window.IsVisible)
                window.Show();
            return;
        }

        window.ShowActivated = stealFocus && !GameFocus.IsPlaying();
        if (!window.IsVisible)
            window.Show();
        window.Place();
        if (stealFocus && !GameFocus.IsPlaying())
            window.Activate();
    }

    async void Install()
    {
        if (_busy)
            return;
        _busy = true;
        _buttons.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(text =>
            {
                if (_alive)
                    _text.Text = text;
            });
            string result = await SetupOffer.InstallCableAsync(progress, CancellationToken.None);
            if (!_alive)
                return;
            _text.Text = result;
            _buttons.Children.Clear();
            _buttons.Children.Add(TemplateTheme.Pill("Хорошо", Close));
            _buttons.IsEnabled = true;
            _busy = false;
        }
        catch (Exception ex)
        {
            if (!_alive)
                return;
            _busy = false;
            _buttons.IsEnabled = true;
            _text.Text = ex is InvalidDataException && !string.IsNullOrWhiteSpace(ex.Message)
                ? ex.Message
                : "Не скачалось. Проверь интернет и нажми «Установить» ещё раз.";
            _buttons.Children.Clear();
            _buttons.Children.Add(TemplateTheme.Pill("Установить", Install, accent: true));
            _buttons.Children.Add(TemplateTheme.Pill("Позже", Later));
        }
    }

    void Later()
    {
        SetupOffer.RememberLater();
        Close();
    }

    void Place()
    {
        var point = System.Windows.Forms.Cursor.Position;
        var area = System.Windows.Forms.Screen.FromPoint(point).WorkingArea;
        double width = ActualWidth > 1 ? ActualWidth : Width;
        double height = ActualHeight > 1 ? ActualHeight : 180;
        Left = Math.Max(area.Left + 8, area.Right - width - 16);
        Top = Math.Max(area.Top + 8, area.Bottom - height - 16);
    }
}
