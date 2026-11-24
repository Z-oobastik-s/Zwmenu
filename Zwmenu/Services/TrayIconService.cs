using System.Windows.Forms;
using System.Windows.Threading;
using Zwmenu.Overlay;

namespace Zwmenu.Services;

/// <summary>Значок в трее. Меню и памятка: свои окна, не стандартная полоска Windows.</summary>
public sealed class TrayIconService : IDisposable
{
    readonly NotifyIcon _icon;
    readonly DispatcherTimer _watch;
    readonly Action _status;
    readonly Action _settings;
    readonly Action _exit;
    long _seen;
    long _hoverStart;
    long _leftAt;
    int _anchorX;
    int _anchorY;
    int _clipPhase;
    bool _leftPending;
    bool _hovering;
    bool _tipHeld;
    bool _disposed;
    static TrayIconService? _live;

    public event EventHandler? ShowRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;

    public TrayIconService()
    {
        _live = this;
        _status = () => ShowRequested?.Invoke(this, EventArgs.Empty);
        _settings = () => SettingsRequested?.Invoke(this, EventArgs.Empty);
        _exit = () => ExitRequested?.Invoke(this, EventArgs.Empty);
        TrayMenuWindow.StatusRequested += _status;
        TrayMenuWindow.SettingsRequested += _settings;
        TrayMenuWindow.ExitRequested += _exit;

        var dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _watch = new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background, Watch, dispatcher);
        _watch.Stop();

        _icon = new NotifyIcon
        {
            Text = "Zwmenu",
            Visible = false,
            Icon = LoadAppIcon()
        };
        _icon.MouseMove += (_, _) => NoteCursor();
        _icon.MouseUp += (_, e) =>
        {
            NoteCursor();
            if (e.Button == MouseButtons.Right)
            {
                _leftPending = false;
                _hovering = false;
                _seen = Environment.TickCount64 - 10_000;
                GuideWindow.CloseGuide();
                TrayMenuWindow.ShowAt(_anchorX, _anchorY, _clipPhase);
                return;
            }

            if (e.Button == MouseButtons.Left)
            {
                _leftPending = true;
                _leftAt = Environment.TickCount64;
                if (!_watch.IsEnabled)
                    _watch.Start();
            }
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left)
                return;
            _leftPending = false;
            _hovering = false;
            GuideWindow.CloseGuide();
            TrayMenuWindow.CloseMenu();
            ShowRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    public void Show() => _icon.Visible = true;

    public static void HoldTip()
    {
        var live = _live;
        if (live == null || live._disposed)
            return;
        live._tipHeld = true;
        live.ApplyTip();
    }

    public static void ReleaseTip()
    {
        var live = _live;
        if (live == null || live._disposed)
            return;
        if (GuideWindow.IsShown || TrayMenuWindow.IsOpen)
            return;
        live._tipHeld = false;
        live.ApplyTip();
    }

    public void SetClipPhase(int phase)
    {
        _clipPhase = phase;
        ApplyTip();
    }

    void ApplyTip()
    {
        try
        {
            _icon.Text = _tipHeld
                ? ""
                : _clipPhase switch
                {
                    2 => "Zwmenu: идёт запись, Esc: стоп",
                    1 => "Zwmenu: готовлю запись",
                    _ => "Zwmenu"
                };
        }
        catch
        {
            // длина подсказки трея ограничена, без неё программа живёт
        }
    }

    void Watch(object? sender, EventArgs e)
    {
        if (_disposed)
            return;
        if (TrayMenuWindow.IsOpen)
        {
            _leftPending = false;
            _hovering = false;
            GuideWindow.CloseGuide();
            return;
        }

        long now = Environment.TickCount64;

        // MouseMove у значка идёт только пока курсор на нём. Пауза дольше 160 мс: уже ушёл на соседнюю иконку.
        if (_hovering && now - _seen > 160)
            _hovering = false;

        if (_leftPending && now - _leftAt >= 260)
        {
            _leftPending = false;
            if (_hovering)
                OpenGuide();
        }
        else if (_hovering
                 && !_leftPending
                 && !GuideWindow.IsShown
                 && now - _hoverStart >= 520)
        {
            OpenGuide();
        }

        if (GuideWindow.IsShown && !_hovering && !GuideWindow.PointerInside())
            GuideWindow.CloseGuide();

        if (!GuideWindow.IsShown && !TrayMenuWindow.IsOpen && !_leftPending && !_hovering)
        {
            _watch.Stop();
            ReleaseTip();
        }
    }

    void NoteCursor()
    {
        var point = Cursor.Position;
        _anchorX = point.X;
        _anchorY = point.Y;
        long now = Environment.TickCount64;
        if (!_hovering || now - _seen > 400)
            _hoverStart = now;
        _hovering = true;
        _seen = now;
        HoldTip();
        if (!_watch.IsEnabled)
            _watch.Start();
    }

    void OpenGuide()
    {
        if (_anchorX == 0 && _anchorY == 0)
        {
            var point = Cursor.Position;
            _anchorX = point.X;
            _anchorY = point.Y;
        }

        GuideWindow.ShowNear(_anchorX, _anchorY);
    }

    static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "zwmenu.ico");
            if (File.Exists(path))
                return new System.Drawing.Icon(path);
        }
        catch
        {
            // запасная иконка ниже
        }

        return System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (ReferenceEquals(_live, this))
            _live = null;
        _watch.Stop();
        TrayMenuWindow.StatusRequested -= _status;
        TrayMenuWindow.SettingsRequested -= _settings;
        TrayMenuWindow.ExitRequested -= _exit;
        TrayMenuWindow.CloseMenu();
        GuideWindow.CloseGuide();
        _icon.Visible = false;
        _icon.Dispose();
        GC.SuppressFinalize(this);
    }
}
