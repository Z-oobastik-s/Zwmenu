using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Zwmenu.Services;

public enum ClipAudio
{
    Silent,
    System,
    Microphone,
    Both
}

/// <summary>Имена устройств по умолчанию. Пустая строка значит, что устройства нет.</summary>
public static class ClipAudioDevices
{
    public static (string? System, string? Microphone) Current()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return (Name(enumerator, DataFlow.Render), Name(enumerator, DataFlow.Capture));
        }
        catch
        {
            return (null, null);
        }
    }

    private static string? Name(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        try
        {
            if (!enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia))
                return null;
            using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            string name = device.FriendlyName?.Trim() ?? "";
            return name.Length == 0 ? null : name;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Пишет звук компьютера и микрофон в wav, пока идёт клип.</summary>
public sealed class ClipAudioCapture : IDisposable
{
    private readonly object _gate = new();
    private WasapiLoopbackCapture? _system;
    private WasapiCapture? _mic;
    private WaveFileWriter? _systemWriter;
    private WaveFileWriter? _micWriter;
    private bool _wantSystem;
    private bool _wantMic;

    public string? SystemPath { get; private set; }
    public string? MicPath { get; private set; }
    public bool AnyFile => Usable(SystemPath) || Usable(MicPath);
    public bool Partial => _wantSystem && _wantMic && Usable(SystemPath) != Usable(MicPath);

    public void Start(ClipAudio mode, string directory)
    {
        Directory.CreateDirectory(directory);
        _wantSystem = mode is ClipAudio.System or ClipAudio.Both;
        _wantMic = mode is ClipAudio.Microphone or ClipAudio.Both;
        if (_wantSystem)
            SystemPath = BeginLoopback(directory);
        if (_wantMic)
            MicPath = BeginMic(directory);
    }

    public void Stop()
    {
        try { _system?.StopRecording(); } catch { /* уже остановлен */ }
        try { _mic?.StopRecording(); } catch { /* уже остановлен */ }
        lock (_gate)
        {
            _systemWriter?.Dispose();
            _micWriter?.Dispose();
            _systemWriter = null;
            _micWriter = null;
        }

        if (!Usable(SystemPath))
            SystemPath = null;
        if (!Usable(MicPath))
            MicPath = null;
    }

    public void Dispose()
    {
        Stop();
        try { _system?.Dispose(); } catch { /* ignore */ }
        try { _mic?.Dispose(); } catch { /* ignore */ }
        _system = null;
        _mic = null;
    }

    private string? BeginLoopback(string directory)
    {
        try
        {
            var capture = new WasapiLoopbackCapture();
            string path = Path.Combine(directory, "system.wav");
            var writer = new WaveFileWriter(path, capture.WaveFormat);
            capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0)
                    return;
                lock (_gate)
                    _systemWriter?.Write(e.Buffer, 0, e.BytesRecorded);
            };
            _system = capture;
            _systemWriter = writer;
            capture.StartRecording();
            return path;
        }
        catch
        {
            return null;
        }
    }

    private string? BeginMic(string directory)
    {
        try
        {
            var capture = new WasapiCapture();
            string path = Path.Combine(directory, "mic.wav");
            var writer = new WaveFileWriter(path, capture.WaveFormat);
            capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0)
                    return;
                lock (_gate)
                    _micWriter?.Write(e.Buffer, 0, e.BytesRecorded);
            };
            _mic = capture;
            _micWriter = writer;
            capture.StartRecording();
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static bool Usable(string? path)
    {
        try
        {
            return path != null && File.Exists(path) && new FileInfo(path).Length > 1000;
        }
        catch
        {
            return false;
        }
    }
}
