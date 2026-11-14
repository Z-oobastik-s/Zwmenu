using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Zwmenu.Services;

namespace Zwmenu.Overlay;

/// <summary>
/// Буфер не меняется, пока не нажата «По-русски» или «Как есть».
/// Уже русский текст кнопки перевода не получает.
/// </summary>
public sealed class ClipboardTranslateWindow : Window
{
    private const int DisplayCap = 20000;

    private readonly System.Windows.Controls.TextBlock _heading;
    private readonly System.Windows.Controls.TextBlock _body;
    private readonly System.Windows.Controls.Button _russian;
    private readonly System.Windows.Controls.Button _originalButton;
    private readonly System.Windows.Controls.StackPanel _actions;

    private string _original = "";
    private string _translation = "";
    private int _generation;
    private CancellationTokenSource? _request;
    private bool _placed;
    private bool _drag;
    private Point _dragScreen;
    private double _dragLeft;
    private double _dragTop;
    private DispatcherTimer? _noteTimer;
    private System.Windows.Controls.Button? _noted;
    private string _notedCaption = "";

    private ClipboardTranslateWindow()
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
        FontFamily = new FontFamily("Segoe UI");

        var ink = Frozen(0xFF, 0xF5, 0xF0, 0xE8);
        var muted = Frozen(0xFF, 0xC8, 0xC2, 0xB8);
        _heading = new System.Windows.Controls.TextBlock
        {
            Foreground = muted,
            FontSize = 12.5,
            Margin = new Thickness(0, 2, 28, 8)
        };
        _body = new System.Windows.Controls.TextBlock
        {
            Foreground = ink,
            FontSize = 14.5,
            TextWrapping = TextWrapping.Wrap
        };
        _russian = ActionButton("По-русски", CopyTranslation);
        _originalButton = ActionButton("Как есть", CopyOriginal);
        _actions = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0)
        };
        _actions.Children.Add(_russian);
        _actions.Children.Add(_originalButton);

        var close = new System.Windows.Controls.Button
        {
            Content = "×",
            FontSize = 16,
            Foreground = ink,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 0, 6, 0),
            Cursor = System.Windows.Input.Cursors.Arrow,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = System.Windows.VerticalAlignment.Top
        };
        close.Click += (_, _) => Close();
        System.Windows.Controls.Panel.SetZIndex(close, 2);

        var scroll = new System.Windows.Controls.ScrollViewer
        {
            MaxHeight = 240,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
            Content = _body
        };
        var stack = new System.Windows.Controls.StackPanel();
        stack.Children.Add(_heading);
        stack.Children.Add(scroll);
        stack.Children.Add(_actions);

        var card = new System.Windows.Controls.Grid { Width = 420 };
        card.Children.Add(stack);
        card.Children.Add(close);
        var shell = new System.Windows.Controls.Border
        {
            Padding = new Thickness(14, 10, 14, 12),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0xF4, 0x16, 0x16, 0x1A)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xF5, 0xF0, 0xE8)),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.SizeAll,
            Child = card
        };
        shell.MouseLeftButtonDown += OnDragDown;
        shell.MouseMove += OnDragMove;
        shell.MouseLeftButtonUp += OnDragUp;
        Content = shell;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape)
                return;
            e.Handled = true;
            Close();
        };
        Closed += (_, _) =>
        {
            CancelRequest();
            if (ReferenceEquals(Current, this))
                Current = null;
        };
    }

    public static ClipboardTranslateWindow? Current { get; private set; }

    public static void ShowCurrent()
    {
        string text;
        try
        {
            if (!System.Windows.Clipboard.ContainsText())
            {
                Toast("В буфере нет текста.");
                return;
            }

            text = System.Windows.Clipboard.GetText() ?? "";
        }
        catch
        {
            Toast("Буфер занят.");
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            Toast("В буфере нет текста.");
            return;
        }

        if (Current == null)
        {
            Current = new ClipboardTranslateWindow();
            Current.Show();
        }
        else
        {
            Current.Activate();
        }

        Current.Present(text);
    }

    private void Present(string text)
    {
        CancelRequest();
        _original = text;
        _translation = "";
        int generation = ++_generation;
        string shown = text.Length > DisplayCap ? text[..DisplayCap] : text;

        if (ScreenText.IsMostlyCyrillic(text))
        {
            ShowState("Уже по-русски", shown, offerCopy: false);
            return;
        }

        ShowState("Перевожу…", shown, offerCopy: false);
        _request = new CancellationTokenSource();
        var token = _request.Token;
        _ = Translate(text, shown, generation, token);
    }

    private async Task Translate(string text, string shown, int generation, CancellationToken token)
    {
        string translated = "";
        try
        {
            translated = await ScreenText.TranslateAsync(text, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            translated = "";
        }

        if (generation != _generation || token.IsCancellationRequested)
            return;

        await Dispatcher.InvokeAsync(() =>
        {
            if (generation != _generation)
                return;
            string result = (translated ?? "").Trim();
            if (result.Length == 0 || string.Equals(result, text.Trim(), StringComparison.Ordinal))
            {
                ShowState("Перевод не получился", shown, offerCopy: false);
                return;
            }

            _translation = result;
            ShowState("", result, offerCopy: true);
        });
    }

    private void ShowState(string heading, string body, bool offerCopy)
    {
        _heading.Text = heading;
        _heading.Visibility = heading.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _body.Text = body;
        _actions.Visibility = offerCopy ? Visibility.Visible : Visibility.Collapsed;
        if (_noted != null)
        {
            _noteTimer?.Stop();
            _noted.Content = _notedCaption;
            _noted = null;
        }

        KeepInside();
    }

    private void CopyTranslation(object sender, RoutedEventArgs e)
    {
        if (_translation.Length == 0)
            return;
        NoteButton(_russian, "По-русски", TrySetText(_translation));
    }

    private void CopyOriginal(object sender, RoutedEventArgs e) =>
        NoteButton(_originalButton, "Как есть", TrySetText(_original));

    private void NoteButton(System.Windows.Controls.Button button, string caption, bool ok)
    {
        _noteTimer?.Stop();
        if (_noted != null && !ReferenceEquals(_noted, button))
            _noted.Content = _notedCaption;
        _noted = button;
        _notedCaption = caption;
        button.Content = ok ? "Скопировано" : "Буфер занят";
        _noteTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _noteTimer.Tick += (_, _) =>
        {
            _noteTimer.Stop();
            if (_noted != null)
                _noted.Content = _notedCaption;
            _noted = null;
        };
        _noteTimer.Start();
    }

    private static bool TrySetText(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                return true;
            }
            catch
            {
                Thread.Sleep(30);
            }
        }

        return false;
    }

    private void CancelRequest()
    {
        _generation++;
        try
        {
            _request?.Cancel();
        }
        catch
        {
            /* уже снят */
        }

        _request = null;
    }

    private void KeepInside()
    {
        UpdateLayout();
        double width = ActualWidth > 1 ? ActualWidth : 420;
        double height = ActualHeight > 1 ? ActualHeight : 160;
        var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        Point tl = RadialLayout.DeviceToDip(this, area.Left, area.Top);
        Point br = RadialLayout.DeviceToDip(this, area.Right, area.Bottom);
        double maxLeft = br.X - width - 8;
        double maxTop = br.Y - height - 8;
        if (maxLeft < tl.X + 8)
            maxLeft = tl.X + 8;
        if (maxTop < tl.Y + 8)
            maxTop = tl.Y + 8;

        double left;
        double top;
        if (!_placed)
        {
            Point center = RadialLayout.DeviceToDip(this, area.Left + area.Width / 2.0, area.Top + area.Height / 2.0);
            left = center.X - width / 2;
            top = center.Y - height / 2;
            _placed = true;
        }
        else
        {
            left = Left;
            top = Top;
        }

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

    private void OnDragDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractive(e.OriginalSource as DependencyObject))
            return;
        _drag = true;
        _dragScreen = PointToScreen(e.GetPosition(this));
        _dragLeft = Left;
        _dragTop = Top;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnDragMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_drag)
            return;
        Point now = PointToScreen(e.GetPosition(this));
        Vector delta = new(now.X - _dragScreen.X, now.Y - _dragScreen.Y);
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
            delta = target.TransformFromDevice.Transform(delta);
        Left = _dragLeft + delta.X;
        Top = _dragTop + delta.Y;
    }

    private void OnDragUp(object sender, MouseButtonEventArgs e)
    {
        if (!_drag)
            return;
        _drag = false;
        ReleaseMouseCapture();
    }

    private static bool IsInteractive(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is System.Windows.Controls.Button
                or System.Windows.Controls.Primitives.ScrollBar
                or System.Windows.Controls.Primitives.Thumb)
                return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private System.Windows.Controls.Button ActionButton(string caption, RoutedEventHandler click)
    {
        var button = new System.Windows.Controls.Button
        {
            Content = caption,
            MinWidth = 140,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(12, 4, 12, 4),
            Foreground = Frozen(0xFF, 0xF5, 0xF0, 0xE8),
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x30)),
            BorderThickness = new Thickness(0),
            FontSize = 13,
            Cursor = System.Windows.Input.Cursors.Arrow,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
            Style = ButtonStyle()
        };
        button.Click += click;
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

    private static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static void Toast(string text)
    {
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).Bounds;
        ScreenshotToast.Show(text, screen, 1.6);
    }
}
