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
    long _leftAt;
    int _anchorX;
    int _anchorY;
    int _clipPhase;
    bool _leftPending;
    bool _disposed;

    public event EventHandler? ShowRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;

    public TrayIconService()
    {
        _status = () => ShowRequested?.Invoke(this, EventArgs.Empty);
        _settings = () => SettingsRequested?.Invoke(this, EventArgs.Empty);
        _exit = () => ExitRequested?.Invoke(this, EventArgs.Empty);
        TrayMenuWindow.StatusRequested += _status;
        TrayMenuWindow.SettingsRequested += _settings;
        TrayMenuWindow.ExitRequested += _exit;

        var dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _watch = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, Watch, dispatcher);
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
            GuideWindow.CloseGuide();
            TrayMenuWindow.CloseMenu();
            ShowRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    public void Show() => _icon.Visible = true;

    public void SetClipPhase(int phase)
    {
        _clipPhase = phase;
        try
        {
            _icon.Text = phase switch
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
            GuideWindow.CloseGuide();
            return;
        }

        long now = Environment.TickCount64;
        if (_leftPending && now - _leftAt >= 260)
        {
            _leftPending = false;
            OpenGuide();
        }
        else if (!_leftPending && !GuideWindow.IsShown && NearAnchor() && now - _seen is >= 450 and <= 900)
            OpenGuide();

        if (GuideWindow.IsShown && !NearAnchor() && !GuideWindow.PointerInside())
            GuideWindow.CloseGuide();

        if (!GuideWindow.IsShown && !_leftPending && !NearAnchor())
            _watch.Stop();
    }

    void NoteCursor()
    {
        var point = Cursor.Position;
        _anchorX = point.X;
        _anchorY = point.Y;
        _seen = Environment.TickCount64;
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

    bool NearAnchor()
    {
        if (_anchorX == 0 && _anchorY == 0)
            return false;
        var point = Cursor.Position;
        return Math.Abs(point.X - _anchorX) <= 64 && Math.Abs(point.Y - _anchorY) <= 48;
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
