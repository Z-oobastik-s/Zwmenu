using System.Globalization;
using System.Windows.Threading;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace Zwmenu.Services;

public enum VoiceRecState
{
    Idle,
    Recording,
    Paused,
    Saving
}

/// <summary>
/// Запись голоса: WaveIn 48 kHz, DSP, пауза, мониторинг («слушать себя»).
/// </summary>
public sealed class VoiceRecService : IDisposable
{
    public const int SampleRate = 48000;

    public static VoiceRecService Shared { get; } = new();

    private readonly object _gate = new();
    private readonly List<float> _samples = new(SampleRate * 60);
    private readonly DispatcherTimer _tick;

    private WaveInEvent? _waveIn;
    private WaveOutEvent? _monitorOut;
    private BufferedWaveProvider? _monitorBuf;
    private VoiceRecDsp? _dsp;        // снимок на запись
    private VoiceRecDsp? _previewDsp; // живой превью в настройках / мониторе
    private VoiceRecFx? _fx;
    private bool _paused;
    private bool _monitor;
    private bool _metering; // уровень без обязательного монитора
    private bool _monitorBypass; // сырой вход в наушники (A/B)
    private bool _disposed;
    private long _startedTick;
    private long _pausedAccumMs;
    private long _pauseBeganTick;
    private float _levelPeak;
    private float _levelRms;
    private float _levelPeakRaw;
    private float _sessionPeakRaw; // пик до DSP за текущую запись (тишина)
    private string? _lastSavedPath;
    private long _recordArmTick; // первые мс после старта не пишем (клик UI)
    private float[] _processBuf = Array.Empty<float>();
    private byte[] _monitorPcm = Array.Empty<byte>();
    private int _saveEpoch; // отмена закрытием не должна сорвать уже идущий save

    private VoiceRecService()
    {
        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _tick.Tick += (_, _) => Changed?.Invoke();
    }

    public VoiceRecState State { get; private set; } = VoiceRecState.Idle;
    public event Action? Changed;
    public event Action<string>? Saved;
    public event Action<string>? Failed;

    public TimeSpan Elapsed
    {
        get
        {
            if (State is VoiceRecState.Idle or VoiceRecState.Saving)
                return TimeSpan.Zero;
            long now = Environment.TickCount64;
            long ms = now - _startedTick - _pausedAccumMs;
            if (_paused && _pauseBeganTick > 0)
                ms -= now - _pauseBeganTick;
            return TimeSpan.FromMilliseconds(Math.Max(0, ms));
        }
    }

    public float LevelPeak => _levelPeak;
    public float LevelRms => _levelRms;
    /// <summary>Пик до эффектов (для клиппинга входа).</summary>
    public float LevelPeakRaw => _levelPeakRaw;
    public bool IsClipping => _levelPeakRaw >= 0.98f || _levelPeak >= 0.98f;
    public bool Enhance { get; set; } = true;
    public bool IsMonitoring => _monitor;
    public bool IsMetering => _metering;
    public bool MonitorBypass
    {
        get => _monitorBypass;
        set
        {
            _monitorBypass = value;
            Changed?.Invoke();
        }
    }
    public string? LastSavedPath => _lastSavedPath;

    /// <summary>Уровень микрофона без обязательного «слушать себя».</summary>
    public void StartMetering()
    {
        if (_disposed)
            return;
        _metering = true;
        try
        {
            ScreenshotStore.SuspendDictationCapture = true;
            EnsurePreviewDsp();
            EnsureMicRunning();
            _tick.Start();
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _metering = false;
            Failed?.Invoke("Микрофон: " + ex.Message);
        }
    }

    public void StopMetering()
    {
        _metering = false;
        if (!_monitor && State == VoiceRecState.Idle)
        {
            StopRecordingCapture(keepMonitor: false);
            ReleaseDictationHold();
            _tick.Stop();
            _levelPeak = 0;
            _levelRms = 0;
            _levelPeakRaw = 0;
        }

        Changed?.Invoke();
    }

    /// <summary>Подтянуть живые эффекты/громкость монитора после смены настроек.</summary>
    public void RefreshLiveSettings()
    {
        EnsurePreviewDsp();
        float monVol = Math.Clamp(ScreenshotStore.Current.VoiceRecMonitorVolume, 10, 100) / 100f;
        lock (_gate)
        {
            if (_fx != null)
                _fx.MonitorVolume = monVol;
            try
            {
                if (_monitorOut != null)
                    _monitorOut.Volume = monVol;
            }
            catch { /* ignore */ }
        }

        Changed?.Invoke();
    }

    /// <summary>Сменить устройство захвата на лету (если не идёт запись).</summary>
    public void RestartMicDevice()
    {
        if (State is VoiceRecState.Recording or VoiceRecState.Paused or VoiceRecState.Saving)
            return;

        bool needMic = _monitor || _metering;
        StopRecordingCapture(keepMonitor: false);
        if (!needMic)
            return;

        try
        {
            EnsureMicRunning();
        }
        catch (Exception ex)
        {
            Failed?.Invoke("Микрофон: " + ex.Message);
        }
    }

    public void Start()
    {
        if (_disposed)
            return;
        lock (_gate)
        {
            if (State is VoiceRecState.Recording or VoiceRecState.Paused or VoiceRecState.Saving)
                return;
        }

        try
        {
            ScreenshotStore.SuspendDictationCapture = true;
            // Не рвём мониторинг — только чистим буфер записи.
            StopRecordingCapture(keepMonitor: true);

            EnsureMicRunning();

            var fx = VoiceRecFx.FromSettings(ScreenshotStore.Current);
            int maxMin = Math.Clamp(ScreenshotStore.Current.VoiceRecMaxMinutes, 1, 180);
            lock (_gate)
            {
                _samples.Clear();
                if (_samples.Capacity < SampleRate * 60)
                    _samples.Capacity = SampleRate * 60;
                // Запас под длинную запись, без гигантского резерва сразу.
                int hint = SampleRate * Math.Min(maxMin, 3) * 60;
                if (_samples.Capacity < hint)
                    _samples.Capacity = hint;
                _fx = fx;
                _dsp = new VoiceRecDsp(fx, SampleRate);
                _paused = false;
                _pausedAccumMs = 0;
                _pauseBeganTick = 0;
                _startedTick = Environment.TickCount64;
                _recordArmTick = Environment.TickCount64 + Math.Max(0, fx.PreRollMs);
                _levelPeak = 0;
                _levelRms = 0;
                _sessionPeakRaw = 0;
                State = VoiceRecState.Recording;
            }

            UiSounds.Suspended = true;
            _tick.Start();
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            ReleaseDictationHold();
            UiSounds.Suspended = false;
            Failed?.Invoke(ex.Message);
            CancelInternal(keepSuspend: false);
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (State != VoiceRecState.Recording)
                return;
            _paused = true;
            _pauseBeganTick = Environment.TickCount64;
            State = VoiceRecState.Paused;
        }

        Changed?.Invoke();
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (State != VoiceRecState.Paused)
                return;
            if (_pauseBeganTick > 0)
                _pausedAccumMs += Environment.TickCount64 - _pauseBeganTick;
            _pauseBeganTick = 0;
            _paused = false;
            State = VoiceRecState.Recording;
        }

        Changed?.Invoke();
    }

    public void TogglePause()
    {
        if (State == VoiceRecState.Recording)
            Pause();
        else if (State == VoiceRecState.Paused)
            Resume();
    }

    /// <summary>Вкл/выкл «слушать себя» в наушниках/колонках.</summary>
    public void ToggleMonitor()
    {
        if (_monitor)
            StopMonitor();
        else
            StartMonitor();
        Changed?.Invoke();
    }

    public void StartMonitor()
    {
        if (_disposed || _monitor)
            return;
        try
        {
            ScreenshotStore.SuspendDictationCapture = true;
            EnsurePreviewDsp();
            EnsureMicRunning();
            float monVol = Math.Clamp(ScreenshotStore.Current.VoiceRecMonitorVolume, 10, 100) / 100f;
            var format = new WaveFormat(SampleRate, 16, 1);
            var buf = new BufferedWaveProvider(format)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromMilliseconds(200)
            };
            var output = new WaveOutEvent { DesiredLatency = 60, Volume = monVol };
            output.Init(buf);
            output.Play();

            lock (_gate)
            {
                _monitorBuf = buf;
                _monitorOut = output;
                _monitor = true;
                if (_fx == null)
                    _fx = VoiceRecFx.FromSettings(ScreenshotStore.Current);
            }

            _tick.Start();
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            StopMonitor();
            Failed?.Invoke("Монитор: " + ex.Message);
        }
    }

    public void StopMonitor()
    {
        WaveOutEvent? output;
        lock (_gate)
        {
            output = _monitorOut;
            _monitorOut = null;
            _monitorBuf = null;
            _monitor = false;
        }

        try { output?.Stop(); } catch { /* ignore */ }
        try { output?.Dispose(); } catch { /* ignore */ }

        // Микрофон оставляем, если идёт запись или нужен уровень в настройках.
        if (State == VoiceRecState.Idle && !_metering)
        {
            StopRecordingCapture(keepMonitor: false);
            ReleaseDictationHold();
            _tick.Stop();
            _levelPeak = 0;
            _levelRms = 0;
            _levelPeakRaw = 0;
        }

        Changed?.Invoke();
    }

    private void EnsurePreviewDsp()
    {
        var fx = VoiceRecFx.FromSettings(ScreenshotStore.Current);
        lock (_gate)
        {
            _previewDsp = new VoiceRecDsp(fx, SampleRate);
            if (State == VoiceRecState.Idle)
                _fx = fx;
        }
    }

    /// <summary>Останавливает запись и сохраняет в фоне (UI не блокируем на FFmpeg/кодирование).</summary>
    public void StopAndSave()
    {
        float[] snapshot;
        VoiceRecFx fx;
        float sessionPeak;

        lock (_gate)
        {
            if (State is VoiceRecState.Idle or VoiceRecState.Saving)
                return;
            State = VoiceRecState.Saving;
            fx = _fx ?? VoiceRecFx.FromSettings(ScreenshotStore.Current);
            snapshot = _samples.ToArray();
            _samples.Clear();
            sessionPeak = _sessionPeakRaw;
        }

        Changed?.Invoke();
        // Не рвём mic, если открыты настройки (metering) или монитор.
        StopRecordingCapture(keepMonitor: _monitor || _metering);

        if (snapshot.Length < SampleRate / 5) // < 200 мс
        {
            Failed?.Invoke("Слишком коротко, скажи хоть что-нибудь.");
            FinishIdleKeepMonitor();
            return;
        }

        if (sessionPeak < 0.0008f)
        {
            Failed?.Invoke("Микрофон молчит. Проверь устройство в настройках Windows.");
            FinishIdleKeepMonitor();
            return;
        }

        int epoch = Interlocked.Increment(ref _saveEpoch);
        bool copyPath = ScreenshotStore.Current.VoiceRecCopyPath;
        string dir = ResolveOutputDir();

        Task.Run(() =>
        {
            try
            {
                float[] final = VoiceRecDsp.Finalize(snapshot, SampleRate, fx);
                if (final.Length < SampleRate / 10)
                    final = snapshot;

                Directory.CreateDirectory(dir);
                string stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
                string format = ScreenshotStore.NormalizeVoiceRecFormat(fx.Format, fx.BitDepth);
                string ext = ScreenshotStore.VoiceRecExtension(format);
                string path = UniquePath(Path.Combine(dir, $"Голос {stamp}{ext}"));
                var (written, fallback) = WriteAudioFile(path, final, SampleRate, format);

                var disp = System.Windows.Application.Current?.Dispatcher;
                if (disp == null)
                    return;

                disp.BeginInvoke(new Action(() =>
                {
                    if (epoch != _saveEpoch)
                        return;

                    _lastSavedPath = written;
                    if (copyPath)
                    {
                        try { System.Windows.Clipboard.SetText(written); } catch { /* ignore */ }
                    }

                    try { UiSounds.Notify(); } catch { /* ignore */ }

                    FinishIdleKeepMonitor();
                    Saved?.Invoke(written);
                    if (fallback)
                    {
                        Failed?.Invoke(
                            "Сохранил WAV: формат " + format.ToUpperInvariant()
                            + " недоступен (нужен FFmpeg).");
                    }
                }));
            }
            catch (Exception ex)
            {
                var disp = System.Windows.Application.Current?.Dispatcher;
                disp?.BeginInvoke(new Action(() =>
                {
                    if (epoch != _saveEpoch)
                        return;
                    Failed?.Invoke(ex.Message);
                    FinishIdleKeepMonitor();
                }));
            }
        });
    }

    public void Cancel()
    {
        Interlocked.Increment(ref _saveEpoch); // сорвать устаревший async-save callback
        CancelInternal(keepSuspend: false);
        Changed?.Invoke();
    }

    private void FinishIdleKeepMonitor()
    {
        lock (_gate)
            State = VoiceRecState.Idle;
        UiSounds.Suspended = false;
        if (!_monitor && !_metering)
        {
            ReleaseDictationHold();
            _tick.Stop();
        }
        else
            EnsurePreviewDsp();

        Changed?.Invoke();
    }

    private void CancelInternal(bool keepSuspend)
    {
        bool keepListen = _monitor || _metering;
        StopRecordingCapture(keepMonitor: keepListen);

        if (!keepListen)
        {
            WaveOutEvent? output;
            lock (_gate)
            {
                output = _monitorOut;
                _monitorOut = null;
                _monitorBuf = null;
                _monitor = false;
            }

            try { output?.Stop(); } catch { /* ignore */ }
            try { output?.Dispose(); } catch { /* ignore */ }
        }

        lock (_gate)
        {
            _samples.Clear();
            _dsp = null;
            _paused = false;
            _pausedAccumMs = 0;
            _pauseBeganTick = 0;
            State = VoiceRecState.Idle;
            if (!keepListen)
            {
                _fx = null;
                _previewDsp = null;
                _levelPeak = 0;
                _levelRms = 0;
                _levelPeakRaw = 0;
            }
        }

        UiSounds.Suspended = false;
        if (keepListen)
        {
            EnsurePreviewDsp();
            EnsureMicRunning();
        }
        else
        {
            _tick.Stop();
            if (!keepSuspend)
                ReleaseDictationHold();
        }
    }

    private void ReleaseDictationHold()
    {
        if (VoiceShiftLive.Shared.Running)
            return;
        ScreenshotStore.SuspendDictationCapture = false;
    }

    /// <summary>Изменитель голоса забирает микрофон. Запись, если она идёт, продолжает писать уже обработанный звук.</summary>
    public void YieldMicToShift()
    {
        WaveInEvent? wave;
        lock (_gate)
        {
            wave = _waveIn;
            _waveIn = null;
        }

        if (wave == null)
            return;
        try { wave.DataAvailable -= OnMicData; } catch { /* ignore */ }
        try { wave.StopRecording(); } catch { /* ignore */ }
        try { Thread.Sleep(30); } catch { /* ignore */ }
        try { wave.Dispose(); } catch { /* ignore */ }
    }

    /// <summary>Уже изменённый звук с живого выхода: в запись и в монитор.</summary>
    public void TakeShifted(float[] data, int count, float rawPeak)
    {
        if (count < 0)
            return;
        _levelPeakRaw = rawPeak;
        Feed(data, count, rawPeak);
    }

    /// <summary>После выключения живого выхода вернуть микрофон записи, если он ещё нужен.</summary>
    public void ShiftStopped()
    {
        if (State is VoiceRecState.Recording or VoiceRecState.Paused || _monitor || _metering)
        {
            try { EnsureMicRunning(); }
            catch (Exception ex) { Failed?.Invoke("Микрофон: " + ex.Message); }
        }
        else
            ReleaseDictationHold();
    }

    private void EnsureMicRunning()
    {
        if (VoiceShiftLive.Shared.Running)
            return;

        WaveInEvent wave;
        lock (_gate)
        {
            if (_waveIn != null)
                return;

            int device = 0;
            try
            {
                device = ScreenshotStore.ResolveMicDevice(ScreenshotStore.Current.DictationMicDevice);
            }
            catch { /* default */ }

            wave = new WaveInEvent
            {
                DeviceNumber = device,
                WaveFormat = new WaveFormat(SampleRate, 16, 1),
                BufferMilliseconds = 40,
                NumberOfBuffers = 3
            };
            wave.DataAvailable += OnMicData;
            _waveIn = wave;
        }

        wave.StartRecording();
    }

    private void StopRecordingCapture(bool keepMonitor)
    {
        WaveInEvent? wave;
        lock (_gate)
        {
            // Микрофон оставляем, если нужен монитор или уровень в настройках.
            if (keepMonitor && (_monitor || _metering))
            {
                _dsp = null;
                return;
            }

            wave = _waveIn;
            _waveIn = null;
            _dsp = null;
        }

        if (wave == null)
            return;
        try { wave.DataAvailable -= OnMicData; } catch { /* ignore */ }
        try { wave.StopRecording(); } catch { /* ignore */ }
        try { Thread.Sleep(30); } catch { /* ignore */ }
        try { wave.Dispose(); } catch { /* ignore */ }
    }

    private void OnMicData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded < 2 || e.Buffer == null)
            return;

        int samples = e.BytesRecorded / 2;
        if (_processBuf.Length < samples)
            _processBuf = new float[samples];
        var floats = _processBuf;

        float rawPeak = 0;
        for (int i = 0; i < samples; i++)
        {
            short s = (short)(e.Buffer[i * 2] | (e.Buffer[i * 2 + 1] << 8));
            float f = s / 32768f;
            floats[i] = f;
            float a = Math.Abs(f);
            if (a > rawPeak)
                rawPeak = a;
        }

        _levelPeakRaw = rawPeak;

        bool recording;
        bool bypass;
        VoiceRecDsp? recordDsp;
        VoiceRecDsp? previewDsp;
        lock (_gate)
        {
            recording = State == VoiceRecState.Recording;
            bypass = _monitorBypass;
            recordDsp = _dsp;
            previewDsp = _previewDsp;
        }

        // Цепочка: при записи — снимок; иначе живой превью из настроек.
        VoiceRecDsp? chain = null;
        if (recording && recordDsp != null)
            chain = recordDsp;
        else if (!bypass)
            chain = previewDsp;

        if (chain != null)
            chain.ProcessBuffer(floats, samples);

        if (!VoiceShiftLive.Shared.Running && VoiceShift.Shared.Active)
        {
            int shaped = VoiceShift.Shared.Process(floats, samples);
            floats = VoiceShift.Shared.Ready;
            samples = shaped;
        }

        Feed(floats, samples, rawPeak);
    }

    private void Feed(float[] floats, int samples, float rawPeak)
    {
        _levelPeakRaw = rawPeak;
        if (samples <= 0 || floats == null)
            return;

        bool paused;
        bool recording;
        VoiceRecFx? fx;
        BufferedWaveProvider? mon;
        lock (_gate)
        {
            paused = _paused;
            recording = State == VoiceRecState.Recording;
            fx = _fx;
            mon = _monitor && !VoiceShiftLive.Shared.Running ? _monitorBuf : null;
            if (recording && rawPeak > _sessionPeakRaw)
                _sessionPeakRaw = rawPeak;
        }

        float peak = 0;
        double sum = 0;
        for (int i = 0; i < samples; i++)
        {
            float a = Math.Abs(floats[i]);
            if (a > peak)
                peak = a;
            sum += a * a;
        }

        _levelPeak = peak;
        _levelRms = (float)Math.Sqrt(sum / Math.Max(1, samples));

        if (mon != null)
        {
            float monGain = Math.Clamp(fx?.MonitorVolume ?? 0.55f, 0.1f, 1f);
            int bytes = samples * 2;
            if (_monitorPcm.Length < bytes)
                _monitorPcm = new byte[bytes];
            var pcm = _monitorPcm;
            for (int i = 0; i < samples; i++)
            {
                float m = Math.Clamp(floats[i] * monGain, -1f, 1f);
                short v = (short)Math.Round(m * short.MaxValue);
                pcm[i * 2] = (byte)(v & 0xff);
                pcm[i * 2 + 1] = (byte)((v >> 8) & 0xff);
            }

            try { mon.AddSamples(pcm, 0, bytes); } catch { /* overflow */ }
        }

        if (!recording || paused)
            return;

        if (Environment.TickCount64 < _recordArmTick)
            return;

        lock (_gate)
        {
            if (State != VoiceRecState.Recording)
                return;

            int maxMin = Math.Clamp(ScreenshotStore.Current.VoiceRecMaxMinutes, 1, 180);
            if (_samples.Count >= (long)SampleRate * maxMin * 60)
            {
                var disp = System.Windows.Application.Current?.Dispatcher;
                disp?.BeginInvoke(new Action(StopAndSave));
                return;
            }

            for (int i = 0; i < samples; i++)
                _samples.Add(floats[i]);
        }
    }

    public static string ResolveOutputDir()
    {
        string custom = (ScreenshotStore.Current.VoiceRecFolder ?? "").Trim();
        if (custom.Length > 0)
        {
            try
            {
                string full = Path.GetFullPath(custom);
                if (Directory.Exists(full) || custom.IndexOfAny(Path.GetInvalidPathChars()) < 0)
                    return full;
            }
            catch { /* fall back */ }
        }

        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return string.IsNullOrWhiteSpace(desktop)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : desktop;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
            return path;
        string dir = Path.GetDirectoryName(path) ?? "";
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 2; i < 1000; i++)
        {
            string p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p))
                return p;
        }

        return Path.Combine(dir, $"{name} {Guid.NewGuid():N}{ext}");
    }

    /// <returns>Путь и флаг, что вместо выбранного формата сохранили WAV.</returns>
    private static (string Path, bool Fallback) WriteAudioFile(
        string path, float[] samples, int sampleRate, string format)
    {
        format = ScreenshotStore.NormalizeVoiceRecFormat(format);
        if (format is "wav16" or "wav24")
        {
            if (format == "wav24")
                WriteWav24(path, samples, sampleRate);
            else
                WriteWav16(path, samples, sampleRate);
            return (path, false);
        }

        // Сжатые: Media Foundation (mp3/m4a), иначе FFmpeg, иначе WAV.
        if ((format is "mp3" or "m4a") && TryEncodeMediaFoundation(samples, sampleRate, path, format))
            return (path, false);

        if (TryEncodeFfmpeg(samples, sampleRate, path, format))
            return (path, false);

        string fallback = UniquePath(Path.ChangeExtension(path, ".wav")!);
        WriteWav16(fallback, samples, sampleRate);
        try
        {
            if (File.Exists(path) && !string.Equals(path, fallback, StringComparison.OrdinalIgnoreCase))
                File.Delete(path);
        }
        catch { /* ignore */ }
        return (fallback, true);
    }

    private static bool TryEncodeMediaFoundation(float[] samples, int sampleRate, string path, string format)
    {
        try
        {
            MediaFoundationApi.Startup();
            byte[] pcm = FloatsToPcm16(samples);
            using var ms = new MemoryStream(pcm);
            using var raw = new RawSourceWaveStream(ms, new WaveFormat(sampleRate, 16, 1));
            if (format == "mp3")
                MediaFoundationEncoder.EncodeToMp3(raw, path, 192000);
            else if (format == "m4a")
                MediaFoundationEncoder.EncodeToAac(raw, path, 192000);
            else
                return false;

            return File.Exists(path) && new FileInfo(path).Length > 64;
        }
        catch
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
            return false;
        }
    }

    private static bool TryEncodeFfmpeg(float[] samples, int sampleRate, string path, string format)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "zwmenu-voicerec-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            WriteWav16(tmp, samples, sampleRate);
            var args = new List<string> { "-i", tmp };
            switch (format)
            {
                case "mp3":
                    args.AddRange(["-c:a", "libmp3lame", "-b:a", "192k", path]);
                    break;
                case "m4a":
                    args.AddRange(["-c:a", "aac", "-b:a", "192k", path]);
                    break;
                case "flac":
                    args.AddRange(["-c:a", "flac", path]);
                    break;
                case "ogg":
                    args.AddRange(["-c:a", "libvorbis", "-q:a", "5", path]);
                    break;
                default:
                    return false;
            }

            var (ok, _) = FfmpegHost.RunAsync(args).GetAwaiter().GetResult();
            return ok && File.Exists(path) && new FileInfo(path).Length > 64;
        }
        catch
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
            return false;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    private static byte[] FloatsToPcm16(float[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            float s = Math.Clamp(samples[i], -1f, 1f);
            short v = (short)Math.Round(s * short.MaxValue);
            bytes[i * 2] = (byte)(v & 0xff);
            bytes[i * 2 + 1] = (byte)((v >> 8) & 0xff);
        }

        return bytes;
    }

    private static void WriteWav16(string path, float[] samples, int sampleRate)
    {
        var format = new WaveFormat(sampleRate, 16, 1);
        using var writer = new WaveFileWriter(path, format);
        writer.Write(FloatsToPcm16(samples), 0, samples.Length * 2);
    }

    private static void WriteWav24(string path, float[] samples, int sampleRate)
    {
        var format = new WaveFormat(sampleRate, 24, 1);
        using var writer = new WaveFileWriter(path, format);
        var bytes = new byte[samples.Length * 3];
        for (int i = 0; i < samples.Length; i++)
        {
            float s = Math.Clamp(samples[i], -1f, 1f);
            int v = (int)Math.Round(s * 8388607.0);
            v = Math.Clamp(v, -8388608, 8388607);
            int o = i * 3;
            bytes[o] = (byte)(v & 0xff);
            bytes[o + 1] = (byte)((v >> 8) & 0xff);
            bytes[o + 2] = (byte)((v >> 16) & 0xff);
        }

        writer.Write(bytes, 0, bytes.Length);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Cancel();
    }
}
