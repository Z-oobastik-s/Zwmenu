using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Zwmenu.Services;

namespace Zwmenu.Overlay;

/// <summary>Предложение поставить новую версию. Скачивание начинается только по кнопке.</summary>
public sealed class UpdateWindow : Window
{
    public static Action? ExitForUpdate;

    static UpdateWindow? _open;

    readonly TextBlock _text;
    readonly System.Windows.Controls.Border _track;
    readonly System.Windows.Controls.Border _fill;
    readonly StackPanel _buttons;
    UpdateOffer? _offer;
    CancellationTokenSource? _cancel;
    bool _busy;

    UpdateWindow()
    {
        Title = "Обновление";
        Width = 400;
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
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 12)
        };
        _fill = new System.Windows.Controls.Border
        {
            Height = 8,
            Width = 0,
            Background = TemplateTheme.Brush(TemplateTheme.AccentText),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        _track = new System.Windows.Controls.Border
        {
            Height = 8,
            Background = TemplateTheme.Brush(TemplateTheme.ListBg),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 0, 14),
            Visibility = Visibility.Collapsed,
            Child = _fill
        };
        _buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };

        var root = new StackPanel { Margin = new Thickness(16, 14, 16, 10) };
        root.Children.Add(_text);
        root.Children.Add(_track);
        root.Children.Add(_buttons);
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || _busy)
                return;
            e.Handled = true;
            Close();
        };
        ContentRendered += (_, _) => Place();
        Closed += (_, _) =>
        {
            _cancel?.Cancel();
            if (ReferenceEquals(_open, this))
                _open = null;
        };
    }

    public static void ShowOffer(UpdateOffer offer)
    {
        var window = Ensure();
        window.BindOffer(offer);
        window.Show();
        window.Place();
        window.Activate();
    }

    public static void ShowNote(string text)
    {
        var window = Ensure();
        window.BindNote(text);
        window.Show();
        window.Place();
        window.Activate();
    }

    void BindOffer(UpdateOffer offer)
    {
        _offer = offer;
        _busy = false;
        _cancel?.Cancel();
        _text.Text = "Вышла версия " + offer.Version + ". Сейчас стоит " + AppUpdate.Current
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
        if (_busy || _offer == null)
            return;

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
                if (pair.Total > 0)
                {
                    _fill.Width = 352.0 * pair.Got / pair.Total;
                    _text.Text = "Скачиваю " + Megabytes(pair.Got) + " из " + Megabytes(pair.Total) + " МБ";
                }
                else
                {
                    _text.Text = "Скачиваю " + Megabytes(pair.Got) + " МБ";
                }
            });
            await AppUpdate.DownloadAsync(offer, progress, _cancel.Token);
            _text.Text = "Ставлю. Программа сейчас закроется и откроется снова.";
            AppUpdate.StartInstaller(offer.Version);
            if (ExitForUpdate != null)
                ExitForUpdate();
            else
                Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            _busy = false;
            _buttons.IsEnabled = true;
            BindOffer(offer);
        }
        catch
        {
            _busy = false;
            _buttons.IsEnabled = true;
            _track.Visibility = Visibility.Collapsed;
            _text.Text = "Не скачалось. Проверь интернет и нажми «Поставить» ещё раз.";
            _buttons.Children.Clear();
            _buttons.Children.Add(TemplateTheme.Pill("Поставить", Install, accent: true));
            _buttons.Children.Add(TemplateTheme.Pill("Позже", Later));
        }
    }

    void Later()
    {
        if (_offer != null)
            AppUpdate.RememberSkip(_offer.Version);
        Close();
    }

    void Place()
    {
        var area = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
            ?? System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        Left = area.Right - ActualWidth - 16;
        Top = area.Bottom - ActualHeight - 16;
    }

    static string Megabytes(long bytes) => ((bytes + 524288) / 1048576).ToString();

    static UpdateWindow Ensure()
    {
        if (_open != null)
            return _open;
        _open = new UpdateWindow();
        return _open;
    }
}
