using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Zwmenu.Overlay;

namespace Zwmenu.Services;

/// <summary>
/// Удержание Ctrl при выбранном фото или аудио/видео → кольцо инструментов.
/// Ждём дольше короткого Ctrl+C/X/V: любая вторая клавиша сразу отменяет жест.
/// Клик снаружи / Esc закрывают меню.
/// </summary>
public sealed class FileConvertHotkeyService : IDisposable
{
    private const int VkControl = 0x11;
    /// <summary>0.8 с: копирование обычно укладывается в 0.2–0.4 с, а полторы секунды уже тормозят намеренный жест.</summary>
    private const int HoldMs = 800;

    private readonly Dispatcher _dispatcher;
    private readonly KeyboardHook _keyboard;
    private readonly MouseHook _mouse;
    private readonly Func<bool> _isMenuOpen;
    private readonly DispatcherTimer _holdTimer;
    private Window? _popup;
    private ConvertMenuWindow? _imageShell;
    private MediaMenuWindow? _mediaShell;
    private TextMenuWindow? _textShell;
    private bool _disposed;
    private bool _armed;
    private bool _cancelled;
    private bool _reportOwned;
    private int _holdGeneration;

    public FileConvertHotkeyService(
        KeyboardHook keyboard,
        MouseHook mouse,
        Dispatcher dispatcher,
        Func<bool> isMenuOpen)
    {
        _keyboard = keyboard;
        _mouse = mouse;
        _dispatcher = dispatcher;
        _isMenuOpen = isMenuOpen;
        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoldMs) };
        _holdTimer.Tick += OnHoldTick;

        _keyboard.CtrlAlonePressed += OnCtrlPressed;
        _keyboard.CtrlReleased += OnCtrlReleased;
        _keyboard.CtrlChordKey += OnCtrlChord;
        _mouse.AnyButtonDown += OnAnyMouseDown;
        _dispatcher.BeginInvoke(PrimeShells, DispatcherPriority.Background);
    }

    /// <summary>Кольца конвертации собираются до первого Ctrl, не в момент клика.</summary>
    private void PrimeShells()
    {
        if (_disposed)
            return;
        try
        {
            _imageShell ??= Park(new ConvertMenuWindow(Array.Empty<string>()));
            _mediaShell ??= Park(new MediaMenuWindow(Array.Empty<string>()));
            _textShell ??= Park(new TextMenuWindow(Array.Empty<string>()));
        }
        catch
        {
            // первое открытие соберёт окно само
        }
    }

    private static T Park<T>(T window) where T : Window
    {
        window.Left = -4000;
        window.Top = -4000;
        window.Show();
        window.UpdateLayout();
        window.Hide();
        return window;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static bool IsCtrlDown() => (GetAsyncKeyState(VkControl) & 0x8000) != 0;

    private void OnCtrlPressed(object? sender, EventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed || _isMenuOpen())
                return;
            if (_popup != null)
            {
                ClosePopup();
                return;
            }

            _armed = true;
            _cancelled = false;
            _holdTimer.Stop();
            _holdTimer.Start();
        });
    }

    private void OnCtrlReleased(object? sender, EventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            Interlocked.Increment(ref _holdGeneration);
            _armed = false;
            _holdTimer.Stop();
        });
    }

    private void OnCtrlChord(object? sender, EventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            Interlocked.Increment(ref _holdGeneration);
            _cancelled = true;
            _armed = false;
            _holdTimer.Stop();
        });
    }

    private void OnAnyMouseDown(object? sender, MouseHook.ScreenPoint pt)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_popup == null)
                return;

            try
            {
                Point screen = new(pt.X, pt.Y);
                Point local = _popup.PointFromScreen(screen);
                if (local.X < 0 || local.Y < 0
                    || local.X > _popup.ActualWidth
                    || local.Y > _popup.ActualHeight)
                {
                    ClosePopup();
                }
            }
            catch
            {
                ClosePopup();
            }
        });
    }

    private void OnHoldTick(object? sender, EventArgs e)
    {
        _holdTimer.Stop();
        if (!_armed || _cancelled || _disposed || _popup != null || _isMenuOpen())
            return;

        _armed = false;
        int gen = _holdGeneration;

        Task.Run(() =>
        {
            var selected = ShellSelectionService.GetSelectedFiles();
            var images = ImageConvertService.FilterImages(selected);
            var media = MediaConvertService.FilterMedia(selected);
            var texts = TextFileService.FilterText(selected);

            bool openMedia = media.Count > 0;
            bool openImage = !openMedia && images.Count > 0;
            bool openText = !openMedia && !openImage && texts.Count > 0;
            if (!openMedia && !openImage && !openText)
            {
                var plain = selected.Where(File.Exists).Take(8).ToArray();
                if (plain.Length == 0)
                    return;
                _dispatcher.BeginInvoke(() =>
                {
                    if (_disposed || _popup != null || _isMenuOpen())
                        return;
                    if (_cancelled || gen != _holdGeneration || !IsCtrlDown())
                        return;
                    ShowHoldPopup(plain);
                });
                return;
            }

            _dispatcher.BeginInvoke(() =>
            {
                if (_disposed || _popup != null || _isMenuOpen())
                    return;
                if (_cancelled || gen != _holdGeneration || !IsCtrlDown())
                    return;

                if (openMedia)
                    ShowMediaPopup(media);
                else if (openImage)
                    ShowImagePopup(images);
                else
                    ShowTextPopup(texts);
            });
        });
    }

    private void ShowImagePopup(IReadOnlyList<string> images)
    {
        try
        {
            var w = _imageShell ?? new ConvertMenuWindow(images);
            _imageShell = null;
            w.UseFiles(images);
            AttachPopup(w);
            w.Closed += (_, _) => _dispatcher.BeginInvoke(PrimeShells, DispatcherPriority.Background);
            w.ShowNearCursor();
            w.MentionHolders();
        }
        catch
        {
            ReleaseClickWatch();
            _popup = null;
        }
    }

    private void ShowMediaPopup(IReadOnlyList<string> media)
    {
        try
        {
            var w = _mediaShell ?? new MediaMenuWindow(media);
            _mediaShell = null;
            w.UseFiles(media);
            AttachPopup(w);
            w.Closed += (_, _) => _dispatcher.BeginInvoke(PrimeShells, DispatcherPriority.Background);
            w.ShowNearCursor();
            w.MentionHolders();
        }
        catch
        {
            ReleaseClickWatch();
            _popup = null;
        }
    }

    private void ShowTextPopup(IReadOnlyList<string> texts)
    {
        try
        {
            var w = _textShell ?? new TextMenuWindow(texts);
            _textShell = null;
            w.UseFiles(texts);
            AttachPopup(w);
            w.Closed += (_, _) => _dispatcher.BeginInvoke(PrimeShells, DispatcherPriority.Background);
            w.ShowNearCursor();
            w.MentionHolders();
        }
        catch
        {
            ReleaseClickWatch();
            _popup = null;
        }
    }

    private void ShowHoldPopup(IReadOnlyList<string> files)
    {
        try
        {
            var w = new FileHoldWindow(files);
            AttachPopup(w);
            w.ShowNearCursor();
            w.Load();
        }
        catch
        {
            ReleaseClickWatch();
            _popup = null;
        }
    }

    private void AttachPopup(Window w)
    {
        _popup = w;
        w.Closed += (_, _) =>
        {
            if (ReferenceEquals(_popup, w))
                ReleaseClickWatch();
            if (ReferenceEquals(_popup, w))
                _popup = null;
        };

        if (!_mouse.ReportOtherButtons)
        {
            _mouse.ReportOtherButtons = true;
            _reportOwned = true;
        }
    }

    private void ReleaseClickWatch()
    {
        if (!_reportOwned)
            return;
        _reportOwned = false;
        if (!_isMenuOpen())
            _mouse.ReportOtherButtons = false;
    }

    public void ClosePopup()
    {
        var w = _popup;
        _popup = null;
        ReleaseClickWatch();
        if (w == null)
            return;

        try
        {
            if (w is IRequestClose closable)
                closable.RequestClose();
            else
                w.Close();
        }
        catch
        {
            try { w.Close(); } catch { /* ignore */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Increment(ref _holdGeneration);
        _holdTimer.Stop();
        _keyboard.CtrlAlonePressed -= OnCtrlPressed;
        _keyboard.CtrlReleased -= OnCtrlReleased;
        _keyboard.CtrlChordKey -= OnCtrlChord;
        _mouse.AnyButtonDown -= OnAnyMouseDown;
        ClosePopup();
    }
}
