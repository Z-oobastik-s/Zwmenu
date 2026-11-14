using System.IO;
using System.Windows;
using NAudio.Wave;
using WpfButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using WpfToggleButton = System.Windows.Controls.Primitives.ToggleButton;

namespace Zwmenu.Services;

public enum UiSoundKind
{
    Click,
    Switch,
    Notify
}

/// <summary>Короткие UI-звуки: клик, переключатель, уведомление.</summary>
public static class UiSounds
{
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim PlayLock = new(1, 1);
    private static DateTime _lastPlay = DateTime.MinValue;
    private static float _volume = 0.55f;
    private static bool _enabled = true;
    private static bool _hooked;
    private static readonly Dictionary<UiSoundKind, Ready> _ready = new();

    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public static void HookGlobal()
    {
        if (_hooked) return;
        _hooked = true;

        EventManager.RegisterClassHandler(
            typeof(WpfButtonBase),
            WpfButtonBase.ClickEvent,
            new RoutedEventHandler(OnButtonBaseClick),
            handledEventsToo: true);
        _ = Task.Run(Warm);
    }

    /// <summary>Глушим клики UI, пока идёт запись голоса (иначе слышно в файле).</summary>
    public static bool Suspended { get; set; }

    private static void OnButtonBaseClick(object sender, RoutedEventArgs e)
    {
        if (!_enabled || Suspended) return;
        // Панель диктофона без кликов: кнопки и так ясные, звук не нужен.
        if (sender is DependencyObject d)
        {
            var host = Window.GetWindow(d);
            if (host is Overlay.VoiceRecWindow or Overlay.VoiceRecSettingsWindow)
                return;
        }
        if (sender is WpfToggleButton)
            Play(UiSoundKind.Switch);
        else
            Play(UiSoundKind.Click);
    }

    public static void Play(UiSoundKind kind)
    {
        if (!_enabled || Suspended) return;

        lock (Gate)
        {
            if ((DateTime.UtcNow - _lastPlay).TotalMilliseconds < 40)
                return;
            _lastPlay = DateTime.UtcNow;
        }

        string? file = kind switch
        {
            UiSoundKind.Click => "click.mp3",
            UiSoundKind.Switch => "switch.mp3",
            UiSoundKind.Notify => "notify.mp3",
            _ => null
        };
        if (file == null) return;

        if (!_ready.TryGetValue(kind, out Ready? clip) || clip == null)
            return;
        float vol = _volume;
        _ = Task.Run(() =>
        {
            if (!PlayLock.Wait(0))
                return;
            try
            {
                lock (clip)
                {
                    clip.Reader.Volume = vol;
                    clip.Reader.Position = 0;
                    clip.Output.Play();
                }
            }
            catch
            {
                // звук необязателен
            }
            finally
            {
                try { PlayLock.Release(); } catch { /* ignore */ }
            }
        });
    }

    private static void Warm()
    {
        foreach (UiSoundKind kind in new[] { UiSoundKind.Click, UiSoundKind.Switch, UiSoundKind.Notify })
        {
            string? file = kind switch
            {
                UiSoundKind.Click => "click.mp3",
                UiSoundKind.Switch => "switch.mp3",
                UiSoundKind.Notify => "notify.mp3",
                _ => null
            };
            if (file == null)
                continue;
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", file);
            if (!File.Exists(path))
                continue;
            try
            {
                var reader = new AudioFileReader(path) { Volume = _volume };
                var output = new WaveOutEvent { DesiredLatency = 40, NumberOfBuffers = 2 };
                output.Init(reader);
                _ready[kind] = new Ready(reader, output);
            }
            catch
            {
                // без звука интерфейс всё равно работает
            }
        }
    }

    private sealed class Ready(AudioFileReader reader, WaveOutEvent output)
    {
        public AudioFileReader Reader { get; } = reader;
        public WaveOutEvent Output { get; } = output;
    }

    public static void Click() => Play(UiSoundKind.Click);
    public static void Switch() => Play(UiSoundKind.Switch);
    public static void Notify() => Play(UiSoundKind.Notify);
}