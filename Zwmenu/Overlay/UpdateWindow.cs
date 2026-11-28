using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Zwmenu.Services;

namespace Zwmenu.Overlay;

/// <summary>Предложение поставить новую версию. Скачивание начинается только по кнопке.</summary>
public sealed class UpdateWindow : Window
{
    public static Action? ExitForUpdate;

    static UpdateWindow? _open;

    public static bool IsBusy => _open != null && _open._busy;

    readonly TextBlock _text;
    readonly System.Windows.Controls.Border _track;
    readonly System.Windows.Controls.Border _fill;
    readonly StackPanel _buttons;
    UpdateOffer? _offer;
    CancellationTokenSource? _cancel;
    bool _busy;
    bool _alive = true;

    UpdateWindow()
    {
        Title = "Обновление";
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
            FontFamily = new FontFamily(UiTheme.Current.FontUi),
            FontSize = 13.5,
            LineHeight = 19
        };
        _fill = new System.Windows.Controls.Border
        {
            Height = 6,
            Width = 0,
            Background = TemplateTheme.Brush(TemplateTheme.AccentText),
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        _track = ForgeChrome.ProgressTrack(_fill);
        _track.Visibility = Visibility.Collapsed;

        _buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };

        var body = new StackPanel();
        body.Children.Add(ForgeChrome.BrandHead("Обновление", "Новая версия Zwmenu"));
        body.Children.Add(ForgeChrome.Card(_text, new Thickness(12, 10, 12, 10)));
        body.Children.Add(_track);
        body.Children.Add(_buttons);

        var shell = ForgeChrome.Shell(body, new Thickness(14, 12, 14, 10));
        Content = shell;

        ContentRendered += (_, _) =>
        {
            Place();
            ForgeChrome.FadeIn(shell);
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || _busy)
                return;
            e.Handled = true;
            Close();
        };
        Closed += (_, _) =>
        {
            _alive = false;
            _cancel?.Cancel();
            if (ReferenceEquals(_open, this))
                _open = null;
        };
    }

    public static void ShowOffer(UpdateOffer offer, bool stealFocus)
    {
        var window = Ensure();
        if (window._busy)
            return;
        window.BindOffer(offer);
        window.ShowActivated = stealFocus && !GameFocus.IsPlaying();
        window.Show();
        window.Place();
        if (stealFocus && !GameFocus.IsPlaying())
            window.Activate();
    }

    public static void ShowNote(string text)
    {
        var window = Ensure();
        if (window._busy)
            return;
        window.BindNote(text);
        window.ShowActivated = !GameFocus.IsPlaying();
        window.Show();
        window.Place();
        if (!GameFocus.IsPlaying())
            window.Activate();
    }

    void BindOffer(UpdateOffer offer)
    {
        _offer = offer;
        _busy = false;
        _cancel?.Cancel();
        _text.Text = offer.Kind == UpdateKind.Patch
            ? "Вышла версия " + offer.Version + ". Сейчас стоит " + AppUpdate.Current
                + ". Скачаю только программу, без всего установщика. Zwmenu закроется и откроется снова."
            : "Вышла версия " + offer.Version + ". Сейчас стоит " + AppUpdate.Current
                + ". Zwmenu закроется, поставит её и откроется снова.";
        _track.Visibility = Visibility.Collapsed;
        _fill.Width = 0;
        _buttons.Children.Clear();
        _buttons.Children.Add(TemplateTheme.Pill("Поставить", Install, accent: true));
        _buttons.Children.Add(TemplateTheme.Pill("Позже", Later));
    }

    void BindNote(string text)
    {
        _offer = null;
        _busy = false;
        _cancel?.Cancel();
        _text.Text = text;
        _track.Visibility = Visibility.Collapsed;
        _buttons.Children.Clear();
        _buttons.Children.Add(TemplateTheme.Pill("Хорошо", Close));
    }

    async void Install()
    {
        if (_busy || _offer == null || !_alive)
            return;

        var waiting = AppUpdate.BusyWork();
        if (waiting != null)
        {
            _text.Text = waiting;
            return;
        }

        var offer = _offer;
        _busy = true;
        _buttons.IsEnabled = false;
        _track.Visibility = Visibility.Visible;
        _text.Text = "Скачиваю...";
        _cancel = new CancellationTokenSource();
        try
        {
            var progress = new Progress<(long Got, long Total)>(pair =>
            {
                if (!_alive)
                    return;
                if (pair.Total > 0)
                {
                    _fill.Width = TrackWidth() * Math.Min(pair.Got, pair.Total) / pair.Total;
                    _text.Text = "Скачиваю " + Megabytes(pair.Got) + " из " + Megabytes(pair.Total) + " МБ";
                }
                else
                {
                    _text.Text = "Скачиваю " + Megabytes(pair.Got) + " МБ";
                }
            });
            await AppUpdate.DownloadAsync(offer, progress, _cancel.Token);
            if (!_alive)
                return;
            waiting = AppUpdate.BusyWork();
            if (waiting != null)
            {
                Hold(waiting);
                return;
            }

            _text.Text = "Ставлю. Откроюсь снова и покажу, что обновление прошло.";
            try
            {
                AppUpdate.Start(offer);
            }
            catch (InvalidDataException)
            {
                Hold("Файл обновления повреждён или неполный. Нажми «Поставить» ещё раз, или поставь полный установщик с GitHub.");
                return;
            }
            catch (InvalidOperationException)
            {
                Hold("Не удалось начать установку. Путь к программе недоступен.");
                return;
            }

            if (ExitForUpdate != null)
                ExitForUpdate();
            else
                Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            if (!_alive)
                return;
            _busy = false;
            _buttons.IsEnabled = true;
            BindOffer(offer);
        }
        catch
        {
            if (!_alive)
                return;
            Hold("Не скачалось. Проверь интернет и нажми «Поставить» ещё раз.");
        }
    }

    void Hold(string text)
    {
        _busy = false;
        _buttons.IsEnabled = true;
        _track.Visibility = Visibility.Collapsed;
        _text.Text = text;
        _buttons.Children.Clear();
        _buttons.Children.Add(TemplateTheme.Pill("Поставить", Install, accent: true));
        _buttons.Children.Add(TemplateTheme.Pill("Позже", Later));
    }

    void Later()
    {
        if (_offer != null)
            AppUpdate.RememberSkip(_offer.Version);
        Close();
    }

    void Place()
    {
        var point = System.Windows.Forms.Cursor.Position;
        var area = System.Windows.Forms.Screen.FromPoint(point).WorkingArea;
        var width = ActualWidth > 1 ? ActualWidth : Width;
        var height = ActualHeight > 1 ? ActualHeight : 170;
        Left = Math.Max(area.Left + 8, area.Right - width - 16);
        Top = Math.Max(area.Top + 8, area.Bottom - height - 16);
    }

    double TrackWidth()
    {
        var w = _track.ActualWidth;
        return w > 1 ? w : 336;
    }

    static string Megabytes(long bytes) => ((bytes + 524288) / 1048576).ToString();

    static UpdateWindow Ensure()
    {
        if (_open != null && _open._alive)
            return _open;
        _open = new UpdateWindow();
        return _open;
    }
}