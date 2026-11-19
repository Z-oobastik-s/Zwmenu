using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace Zwmenu.Overlay;

/// <summary>
/// На части видеокарт стрелка пропадает над окном WPF, хотя клики доходят.
/// Пока окно открыто, просим Windows нарисовать курсор ещё раз.
/// </summary>
static class CursorKeep
{
    const int IdcArrow = 32512;
    static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    static readonly HashSet<Window> Open = new();
    static readonly HashSet<Window> Watched = new();
    static IntPtr _arrow;
    static bool _installed;

    public static void Install()
    {
        if (_installed)
            return;
        _installed = true;
        Timer.Tick += (_, _) => Paint();
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnLoaded));
    }

    static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window || sender is ScreenshotOverlayWindow)
            return;
        if (!Watched.Add(window))
            return;
        if (window.Cursor == null)
            window.Cursor = System.Windows.Input.Cursors.Arrow;
        window.IsVisibleChanged += (_, _) => Sync(window);
        window.Closed += (_, _) =>
        {
            Watched.Remove(window);
            Open.Remove(window);
            SyncTimer();
        };
        Sync(window);
    }

    static void Sync(Window window)
    {
        if (window.IsVisible)
        {
            if (Open.Add(window))
                EnsureShown();
        }
        else
        {
            Open.Remove(window);
        }

        SyncTimer();
    }

    static void SyncTimer()
    {
        if (Open.Count > 0)
        {
            if (!Timer.IsEnabled)
                Timer.Start();
            Paint();
        }
        else if (Timer.IsEnabled)
        {
            Timer.Stop();
        }
    }

    static int _suspended;

    public static void Suspend() => Interlocked.Increment(ref _suspended);

    public static void Resume()
    {
        int now;
        do
        {
            now = Volatile.Read(ref _suspended);
            if (now <= 0)
                return;
        }
        while (Interlocked.CompareExchange(ref _suspended, now - 1, now) != now);
    }

    static void Paint()
    {
        if (Volatile.Read(ref _suspended) > 0)
            return;
        IntPtr cursor = GetCursor();
        if (cursor == IntPtr.Zero)
        {
            if (_arrow == IntPtr.Zero)
                _arrow = LoadCursor(IntPtr.Zero, IdcArrow);
            cursor = _arrow;
        }

        if (cursor != IntPtr.Zero)
            SetCursor(cursor);
    }

    /// <summary>Счётчик ShowCursor общий. Если ушёл ниже нуля, стрелка есть, но её не рисуют.</summary>
    public static void EnsureShown()
    {
        if (Volatile.Read(ref _suspended) > 0)
            return;
        int count = ShowCursor(true);
        int guard = 0;
        while (count < 0 && guard++ < 16)
            count = ShowCursor(true);
        if (count > 0)
            ShowCursor(false);
    }

    [DllImport("user32.dll")]
    static extern IntPtr GetCursor();

    [DllImport("user32.dll")]
    static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr LoadCursor(IntPtr instance, int name);

    [DllImport("user32.dll")]
    static extern int ShowCursor(bool show);
}
