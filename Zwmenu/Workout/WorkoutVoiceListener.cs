using System.Text.Json;
using NAudio.Wave;
using Vosk;
using Zwmenu.Services;

namespace Zwmenu.Workout;

/// <summary>
/// Офлайн-голос (Vosk, русский). Слушает микрофон только пока живёт окно тренировок.
/// Модель грузится в фоне заранее, чтобы окно не подвисало.
/// </summary>
internal sealed class WorkoutVoiceListener : IDisposable
{
    private const int SampleRate = 16000;

    private readonly object _gate = new();
    private WaveInEvent? _waveIn;
    private VoskRecognizer? _recognizer;
    private bool _disposed;
    private bool _running;
    private string? _lastCmd;
    private DateTime _lastCmdAt = DateTime.MinValue;

    public event Action<string>? CommandRecognized;
    public event Action<string>? StatusChanged;

    public bool IsListening => _running;

    /// <summary>Прогрев русской модели в фоне (можно звать при старте приложения).</summary>
    public static Task<Model> PreloadAsync() => VoskModelStore.PreloadRuAsync();

    public async void Start()
    {
        if (_running || _disposed)
            return;

        try
        {
            StatusChanged?.Invoke("Голос: подключаю микрофон…");
            Model model = await PreloadAsync().ConfigureAwait(false);
            if (_disposed)
                return;

            await Task.Yield();
            if (_disposed)
                return;

            int device = 0;
            int bufferMs = 300;
            try
            {
                device = Services.ScreenshotStore.ResolveMicDevice(Services.ScreenshotStore.Current.DictationMicDevice);
                bufferMs = Math.Clamp(Services.ScreenshotStore.Current.DictationBufferMs, 100, 500);
            }
            catch { /* значения по умолчанию */ }

            var recognizer = new VoskRecognizer(model, SampleRate);
            recognizer.SetWords(false);
            var wave = new WaveInEvent
            {
                DeviceNumber = device,
                WaveFormat = new WaveFormat(SampleRate, 1),
                BufferMilliseconds = bufferMs
            };
            wave.DataAvailable += OnDataAvailable;

            lock (_gate)
            {
                if (_disposed)
                {
                    try { wave.Dispose(); } catch { /* ignore */ }
                    try { recognizer.Dispose(); } catch { /* ignore */ }
                    return;
                }

                _recognizer = recognizer;
                _waveIn = wave;
                _running = true;
            }

            try
            {
                wave.StartRecording();
            }
            catch
            {
                StopInternal();
                throw;
            }

            StatusChanged?.Invoke("Голос: слушаю. План: начать/выполнил. Видео: вперёд/назад/пауза.");
        }
        catch (Exception ex)
        {
            StopInternal();
            StatusChanged?.Invoke("Голос недоступен: " + ex.Message + " Разреши микрофон в параметрах Windows.");
        }
    }

    public void Stop()
    {
        StopInternal();
        if (!_disposed)
            StatusChanged?.Invoke("Голос: выключен (окно закрыто).");
    }

    private void StopInternal()
    {
        WaveInEvent? wave;
        VoskRecognizer? recognizer;
        lock (_gate)
        {
            _running = false;
            wave = _waveIn;
            recognizer = _recognizer;
            _waveIn = null;
            _recognizer = null;
        }

        if (wave != null)
        {
            try { wave.DataAvailable -= OnDataAvailable; } catch { /* ignore */ }
            try { wave.StopRecording(); } catch { /* ignore */ }
            try { Thread.Sleep(40); } catch { /* ignore */ }
            try { wave.Dispose(); } catch { /* ignore */ }
        }

        if (recognizer != null)
        {
            try { recognizer.Dispose(); } catch { /* ignore */ }
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || e.Buffer == null)
            return;

        try
        {
            lock (_gate)
            {
                if (!_running || _recognizer == null)
                    return;
                // AcceptWaveform под локом — StopInternal не Dispose'ит recognizer параллельно.
                if (_recognizer.AcceptWaveform(e.Buffer, e.BytesRecorded))
                    HandleJson(_recognizer.Result());
            }
        }
        catch
        {
            // ignore transient recognizer errors
        }
    }

    private void HandleJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;

        string? text = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("text", out var t))
                text = t.GetString();
        }
        catch
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
            return;

        string? cmd = Classify(text);
        if (cmd == null)
            return;

        if (cmd == _lastCmd && (DateTime.UtcNow - _lastCmdAt).TotalSeconds < 1.6)
            return;
        _lastCmd = cmd;
        _lastCmdAt = DateTime.UtcNow;
        CommandRecognized?.Invoke(cmd);
    }

    private static string? Classify(string raw)
    {
        string text = raw.Trim().ToLowerInvariant();
        if (text.Length < 3)
            return null;

        if (ContainsAny(text, "пропустить отдых", "стоп отдых", "хватит отдыхать", "отдых закончен", "закончил отдых"))
            return "skip_rest";
        if (ContainsAny(text, "режим видео", "открой видео", "покажи видео") || text == "видео")
            return "video_mode";
        if (ContainsAny(text, "режим план", "открой план", "покажи план") || text == "план")
            return "plan_mode";
        if (ContainsAny(text, "видео вперёд", "видео вперед", "видео дальше", "видео следующее", "следующее видео"))
            return "video_next";
        if (ContainsAny(text, "видео назад", "видео предыдущее", "предыдущее видео"))
            return "video_prev";
        if (ContainsAny(text, "пауза", "останови", "стоп видео"))
            return "pause";
        if (ContainsAny(text, "продолжи", "продолжить", "играй"))
            return "resume";
        if (ContainsAny(text, "вперёд", "вперед"))
            return "next";
        if (ContainsAny(text, "начать", "начинай", "начнём", "начнем", "старт", "поехали", "погнали"))
            return "start";
        if (ContainsAny(text, "следующ", "дальше"))
            return "next";
        if (ContainsAny(text, "предыдущ", "назад", "прошлое"))
            return "prev";
        if (ContainsAny(text, "выполнил", "выполнила", "выполнено", "завершил", "завершила", "завершено",
                "готово", "сделал", "сделала", "подход готов", "подход выполнен", "закончил подход"))
            return "done";
        return null;
    }

    private static bool ContainsAny(string text, params string[] parts)
    {
        foreach (var p in parts)
        {
            if (text.Contains(p, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopInternal();
    }
}