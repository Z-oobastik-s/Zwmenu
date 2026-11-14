using System.IO;
using NAudio.Wave;

namespace Zwmenu.Workout;

/// <summary>Фоновый трек из настроек пользователя. Сам не стартует: только по «начать».</summary>
internal sealed class WorkoutMusicPlayer : IDisposable
{
    private WaveOutEvent? _out;
    private AudioFileReader? _reader;
    private LoopStream? _loop;
    private bool _disposed;
    private float _baseVolume = 0.35f;
    private bool _ducked;
    private string? _path;

    public bool IsPlaying => _out?.PlaybackState == PlaybackState.Playing;
    public string Status { get; private set; } = "Музыка: ждёт команду «начать» (укажи трек в Аудио)";

    public void Configure(WorkoutAudioSettings settings)
    {
        _baseVolume = Math.Clamp(settings.MusicVolume, 0.05f, 1f);
        _path = WorkoutAudioStore.ResolveMusic(settings);
        Status = _path == null
            ? "Музыка: трек не задан. Открой «Аудио» и укажи свой mp3."
            : "Музыка: готова, стартует по «начать» / «старт».";
    }

    public void Start()
    {
        if (_disposed)
            return;
        StopInternal();
        if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path))
        {
            Status = "Музыка: трек не задан. Открой «Аудио».";
            return;
        }

        try
        {
            _reader = new AudioFileReader(_path) { Volume = _baseVolume };
            _loop = new LoopStream(_reader);
            _out = new WaveOutEvent();
            _out.Init(_loop);
            _out.Play();
            Status = "Музыка: играет";
        }
        catch (Exception ex)
        {
            StopInternal();
            Status = "Музыка: ошибка " + ex.Message;
        }
    }

    public void Stop()
    {
        StopInternal();
        Status = _path == null
            ? "Музыка: трек не задан"
            : "Музыка: стоп";
    }

    public void Duck(bool duck)
    {
        if (_reader == null || _ducked == duck)
            return;
        _ducked = duck;
        _reader.Volume = duck ? _baseVolume * 0.2f : _baseVolume;
    }

    private void StopInternal()
    {
        try { _out?.Stop(); } catch { /* ignore */ }
        try { _out?.Dispose(); } catch { /* ignore */ }
        try { _loop?.Dispose(); } catch { /* ignore */ }
        try { _reader?.Dispose(); } catch { /* ignore */ }
        _out = null;
        _loop = null;
        _reader = null;
        _ducked = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopInternal();
    }

    private sealed class LoopStream : WaveStream
    {
        private readonly WaveStream _source;
        public LoopStream(WaveStream source) => _source = source;
        public override WaveFormat WaveFormat => _source.WaveFormat;
        public override long Length => _source.Length;
        public override long Position
        {
            get => _source.Position;
            set => _source.Position = value;
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = _source.Read(buffer, offset + total, count - total);
                if (read == 0)
                {
                    _source.Position = 0;
                    read = _source.Read(buffer, offset + total, count - total);
                    if (read == 0) break;
                }
                total += read;
            }
            return total;
        }
    }
}