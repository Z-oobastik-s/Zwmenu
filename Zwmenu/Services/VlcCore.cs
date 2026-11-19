using LibVLCSharp.Shared;

namespace Zwmenu.Services;

/// <summary>Общая загрузка libvlc для видео и музыки. Не трогать на старте приложения и в --selfcheck.</summary>
public static class VlcCore
{
    static readonly object Gate = new();
    static LibVLC? _lib;
    static bool _failed;
    static int _idleGen;

    public static LibVLC Get()
    {
        Interlocked.Increment(ref _idleGen);
        lock (Gate)
        {
            if (_lib != null)
                return _lib;
            if (_failed)
                throw new InvalidOperationException("Библиотека уже не загрузилась.");
            try
            {
                string dir = Path.Combine(AppContext.BaseDirectory, "libvlc", Environment.Is64BitProcess ? "win-x64" : "win-x86");
                string dll = Path.Combine(dir, "libvlc.dll");
                if (!File.Exists(dll))
                    throw new InvalidOperationException("Рядом с программой нет папки libvlc.");
                Core.Initialize(dir);
                _lib = new LibVLC(
                    "--no-video-title-show",
                    "--quiet",
                    "--avcodec-hw=dxva2",
                    "--avcodec-threads=2",
                    "--file-caching=200",
                    "--network-caching=400",
                    "--drop-late-frames",
                    "--skip-frames",
                    "--no-snapshot-preview");
                return _lib;
            }
            catch
            {
                _failed = true;
                throw;
            }
        }
    }

    /// <summary>Окно видео или музыки закрылось. Если больше никто не играет, библиотеку отпускаем.</summary>
    public static void NoteClosed()
    {
        int gen = Interlocked.Increment(ref _idleGen);
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(20_000).ConfigureAwait(false); }
            catch { return; }

            LibVLC? lib;
            lock (Gate)
            {
                if (gen != Volatile.Read(ref _idleGen) || _lib == null)
                    return;
                if (Zwmenu.Overlay.VideoWindow.IsOpen || Zwmenu.Overlay.MusicWindow.IsOpen)
                    return;
                lib = _lib;
                _lib = null;
            }

            try { lib.Dispose(); } catch { /* уже закрыта */ }
        });
    }
}
