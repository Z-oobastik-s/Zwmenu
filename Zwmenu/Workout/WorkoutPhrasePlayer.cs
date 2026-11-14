using System.IO;
using NAudio.Wave;

namespace Zwmenu.Workout;

/// <summary>Проигрывает скачанные фразы (mp3/wav). Без Windows Speech.</summary>
internal sealed class WorkoutPhrasePlayer : IDisposable
{
    private WaveOutEvent? _out;
    private AudioFileReader? _reader;
    private bool _disposed;

    public event Action? Started;
    public event Action? Ended;

    public bool IsPlaying => _out?.PlaybackState == PlaybackState.Playing;

    public void Play(string? path)
    {
        if (_disposed || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        try
        {
            StopInternal();
            _reader = new AudioFileReader(path) { Volume = 1f };
            _out = new WaveOutEvent();
            _out.PlaybackStopped += OnStopped;
            _out.Init(_reader);
            Started?.Invoke();
            _out.Play();
        }
        catch
        {
            StopInternal();
        }
    }

    public void PlayKey(WorkoutAudioSettings settings, string key) =>
        Play(WorkoutAudioStore.ResolvePhrase(settings, key));

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        Ended?.Invoke();
        StopInternal();
    }

    private void StopInternal()
    {
        if (_out != null)
        {
            try { _out.PlaybackStopped -= OnStopped; } catch { /* ignore */ }
            try { _out.Stop(); } catch { /* ignore */ }
            try { _out.Dispose(); } catch { /* ignore */ }
            _out = null;
        }
        try { _reader?.Dispose(); } catch { /* ignore */ }
        _reader = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopInternal();
    }
}