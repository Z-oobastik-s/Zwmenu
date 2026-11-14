using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Zwmenu.Overlay;

/// <summary>
/// Маленькая карточка переноса у края экрана. Не забирает фокус и не рисует тени.
/// </summary>
public static class EdgeTransferHud
{
    private static readonly SolidColorBrush Ink = Freeze(Color.FromRgb(0xF4, 0xF1, 0xEA));
    private static readonly SolidColorBrush Mute = Freeze(Color.FromRgb(0xC8, 0xC2, 0xB6));
    private static readonly SolidColorBrush Card = Freeze(Color.FromRgb(0x16, 0x18, 0x1E));
    private static readonly SolidColorBrush Line = Freeze(Color.FromRgb(0x3A, 0x34, 0x2C));
    private static readonly SolidColorBrush Track = Freeze(Color.FromRgb(0x2C, 0x30, 0x38));
    private static readonly SolidColorBrush Bar = Freeze(Color.FromRgb(0xD7, 0xC4, 0xA3));
    private static readonly SolidColorBrush Ok = Freeze(Color.FromRgb(0x7D, 0xC8, 0x8A));
    private static readonly SolidColorBrush Bad = Freeze(Color.FromRgb(0xE0, 0x6A, 0x5A));
    private static readonly SolidColorBrush ButtonFace = Freeze(Color.FromRgb(0x2A, 0x2E, 0x36));

    private static Window? _window;
    private static TextBlock? _title;
    private static TextBlock? _detail;
    private static Border? _fill;
    private static StackPanel? _actions;
    private static long _lastPost;
    private static int _queued;
    private static int _epoch;
    private static string _pendTitle = "";
    private static string _pendDetail = "";
    private static double _pendFrac;
    private static Action? _onSend;
    private static bool _busy;
    private static bool _placed;
    private static double _placedHeight;
    private static int _placeTick;

    public static Action? Cancel { get; set; }

    public static bool Busy => _busy;

    public static void DismissOffer()
    {
        Post(true, () =>
        {
            if (_onSend != null)
                Close();
        });
    }

    public static void Offer(string title, string detail, Action send, Action? dismiss = null)
    {
        _onSend = send;
        _busy = false;
        Interlocked.Increment(ref _epoch);
        Post(true, () =>
        {
            Ensure();
            StopTimer();
            _title!.Text = title;
            _detail!.Text = detail;
            SetBar(0, Bar);
            _actions!.Margin = new Thickness(0, 10, 0, 0);
            _actions.Children.Clear();
            _actions.Children.Add(Button("Отправить", () =>
            {
                var go = _onSend;
                _onSend = null;
                _busy = true;
                _actions.Children.Clear();
                _actions.Margin = new Thickness(0);
                _title!.Text = "Собираю файлы…";
                _detail!.Text = "На второй компьютер ещё ничего не ушло";
                go?.Invoke();
            }));
            _actions.Children.Add(Button("Не надо", () =>
            {
                dismiss?.Invoke();
                Close();
            }));
            Place(force: true);
        });
    }

    public static void Progress(string title, string detail, double fraction, bool force = false)
    {
        _pendTitle = title;
        _pendDetail = detail;
        _pendFrac = fraction;
        long now = Environment.TickCount64;
        if (!force && now - Volatile.Read(ref _lastPost) < 80)
            return;
        if (Interlocked.Exchange(ref _queued, 1) == 1)
            return;
        Volatile.Write(ref _lastPost, now);
        int epoch = Volatile.Read(ref _epoch);
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            Volatile.Write(ref _queued, 0);
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            Volatile.Write(ref _queued, 0);
            if (epoch != Volatile.Read(ref _epoch))
                return;
            _busy = true;
            Paint(_pendTitle, _pendDetail, _pendFrac, stop: true, bar: Bar);
        });
    }

    public static void Finish(string title, string detail, bool ok)
    {
        _busy = false;
        _onSend = null;
        Interlocked.Increment(ref _epoch);
        Volatile.Write(ref _lastPost, Environment.TickCount64);
        Post(true, () =>
        {
            Paint(title, detail, 1, stop: false, bar: ok ? Ok : Bad);
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(ok ? 3.2 : 12)
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Close();
            };
            _window!.Tag = timer;
            timer.Start();
        });
    }

    private static void Paint(string title, string detail, double fraction, bool stop, Brush bar)
    {
        Ensure();
        StopTimer();
        if (!stop)
            _onSend = null;
        _title!.Text = title;
        _detail!.Text = detail;
        SetBar(fraction, bar);
        if (stop)
        {
            _actions!.Margin = new Thickness(0, 10, 0, 0);
            if (_actions.Children.Count != 1)
            {
                _actions.Children.Clear();
                _actions.Children.Add(Button("Стоп", () => Cancel?.Invoke()));
            }
        }
        else
        {
            _actions!.Children.Clear();
            _actions.Margin = new Thickness(0);
        }

        Place(force: false);
    }

    private static void SetBar(double fraction, Brush brush)
    {
        if (_fill == null)
            return;
        fraction = Math.Clamp(fraction, 0, 1);
        _fill.Width = Math.Max(0, 276 * fraction);
        _fill.Background = brush;
    }

    private static void Ensure()
    {
        if (_window != null)
            return;
        _title = Text(14, FontWeights.SemiBold, Ink);
        _detail = Text(12, FontWeights.Normal, Mute);
        _detail.TextWrapping = TextWrapping.Wrap;
        _detail.MaxHeight = 58;
        _detail.Margin = new Thickness(0, 4, 0, 8);
        var track = new Border
        {
            Width = 276,
            Height = 4,
            Background = Track,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(2),
            ClipToBounds = true
        };
        _fill = new Border
        {
            Width = 0,
            Height = 4,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Background = Bar
        };
        track.Child = _fill;
        _actions = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0)
        };
        var card = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        card.Children.Add(_title);
        card.Children.Add(_detail);
        card.Children.Add(track);
        card.Children.Add(_actions);
        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Width = 304,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Background = Card,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            Content = card,
            Focusable = false
        };
        _window.MouseLeftButtonDown += (_, _) =>
        {
            if (!_busy && _onSend == null)
                Close();
        };
        _window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(_window).Handle;
            int ex = GetWindowLong(hwnd, -20);
            SetWindowLong(hwnd, -20, ex | 0x00000080 | 0x08000000);
        };
    }

    private static TextBlock Text(double size, FontWeight weight, Brush color) => new()
    {
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = size,
        FontWeight = weight,
        Foreground = color,
        TextWrapping = TextWrapping.NoWrap
    };

    private static Button Button(string caption, Action click)
    {
        var button = new Button
        {
            Content = caption,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(10, 4, 10, 4),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            Foreground = Ink,
            Background = ButtonFace,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            Focusable = false,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        button.Click += (_, _) => click();
        return button;
    }

    private static void Place(bool force)
    {
        if (_window == null)
            return;
        int now = Environment.TickCount;
        bool refresh = force || !_window.IsVisible || !_placed || unchecked(now - _placeTick) > 500;
        if (!_window.IsVisible)
            _window.Show();
        if (refresh)
            _window.UpdateLayout();
        double height = _window.ActualHeight > 1 ? _window.ActualHeight : 96;
        if (!refresh && Math.Abs(height - _placedHeight) < 1)
            return;
        var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        double width = _window.ActualWidth > 1 ? _window.ActualWidth : _window.Width;
        _window.Left = area.Right - width - 14;
        _window.Top = area.Bottom - height - 14;
        _placed = true;
        _placedHeight = height;
        _placeTick = now;
    }

    private static void Close()
    {
        StopTimer();
        _onSend = null;
        _busy = false;
        _placed = false;
        Interlocked.Increment(ref _epoch);
        try { _window?.Hide(); } catch { /* уже закрыто */ }
    }

    private static void StopTimer()
    {
        if (_window?.Tag is System.Windows.Threading.DispatcherTimer timer)
        {
            timer.Stop();
            _window.Tag = null;
        }
    }

    private static void Post(bool force, Action paint)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
            return;
        if (force)
            Volatile.Write(ref _lastPost, Environment.TickCount64);
        if (dispatcher.CheckAccess())
            paint();
        else
            dispatcher.BeginInvoke(paint);
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
