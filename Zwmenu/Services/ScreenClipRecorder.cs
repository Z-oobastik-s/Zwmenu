using System.Diagnostics;
using System.Text;

namespace Zwmenu.Services;

/// <summary>
/// Короткий клип одного монитора через FFmpeg gdigrab.
/// На экране ничего не рисуем, чтобы рамка не попала в кадр.
/// Аппаратный кодировщик выбирается один раз, иначе ultrafast x264.
/// </summary>
public sealed class ScreenClipRecorder
{
    private readonly object _gate = new();
    private Process? _proc;
    private int _token;
    private int _state;
    private string? _encoder;
    private ClipAudio _audio;
    private string _audioNote = "";

    public bool IsRecording => Volatile.Read(ref _state) == 2;

    public bool IsActive => Volatile.Read(ref _state) != 0;

    /// <summary>0 — покой, 1 — готовится, 2 — пишется.</summary>
    public event Action<int>? StateChanged;

    public event Action<string, System.Drawing.Rectangle, bool>? Finished;

    public void Start(ClipAudio audio)
    {
        if (Volatile.Read(ref _state) != 0)
        {
            Stop();
            return;
        }

        _audio = audio;
        _audioNote = "";
        int token = Interlocked.Increment(ref _token);
        Volatile.Write(ref _state, 1);
        _ = StartAsync(token);
    }

    public void Stop()
    {
        Interlocked.Increment(ref _token);
        Process? proc;
        lock (_gate)
            proc = _proc;
        if (proc == null)
            return;

        try
        {
            proc.StandardInput.WriteLine("q");
            proc.StandardInput.Flush();
        }
        catch
        {
            Kill(proc);
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(2500).ConfigureAwait(false);
            Kill(proc);
        });
    }

    public Task PrepareEncoderAsync() => Task.Run(async () =>
    {
        string? exe = await FfmpegHost.EnsureAsync().ConfigureAwait(false);
        if (exe != null)
            await DetectEncoderAsync(exe).ConfigureAwait(false);
    });

    private async Task StartAsync(int token)
    {
        Notify(1);
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        var bounds = screen.Bounds;
        string path = "";
        try
        {
            int width = bounds.Width & ~1;
            int height = bounds.Height & ~1;
            if (width < 2 || height < 2)
            {
                if (token == Volatile.Read(ref _token))
                    Finished?.Invoke("Пустой экран.", bounds, false);
                return;
            }

            string? exe = await FfmpegHost.EnsureAsync().ConfigureAwait(false);
            if (token != Volatile.Read(ref _token))
                return;
            if (exe == null)
            {
                Finished?.Invoke(FfmpegHost.LastError ?? "FFmpeg не найден.", bounds, false);
                return;
            }

            string encoder = _encoder ?? await DetectEncoderAsync(exe).ConfigureAwait(false);
            if (token != Volatile.Read(ref _token))
                return;

            int seconds = ScreenshotStore.Current.ClipSeconds;
            path = ClipPath();
            int code = await RecordOnce(exe, encoder, bounds.X, bounds.Y, width, height, seconds, path, token)
                .ConfigureAwait(false);
            if (code == 0 && encoder != "x264" && token == Volatile.Read(ref _token))
            {
                _encoder = "x264";
                TryDelete(path);
                path = ClipPath();
                code = await RecordOnce(exe, "x264", bounds.X, bounds.Y, width, height, seconds, path, token)
                    .ConfigureAwait(false);
            }

            if (code == 1 && File.Exists(path) && new FileInfo(path).Length > 20_000)
            {
                Finished?.Invoke("Клип на рабочем столе: " + Path.GetFileName(path) + _audioNote, bounds, true);
                return;
            }

            TryDelete(path);
            if (token == Volatile.Read(ref _token))
                Finished?.Invoke("Клип не записался.", bounds, false);
        }
        catch
        {
            TryDelete(path);
            if (token == Volatile.Read(ref _token))
            {
                try { Finished?.Invoke("Клип не записался.", bounds, false); } catch { /* ignore */ }
            }
        }
        finally
        {
            Volatile.Write(ref _state, 0);
            Notify(0);
        }
    }

    /// <summary>1 — файл готов, 0 — кодировщик отказал, -1 — отмена.</summary>
    private async Task<int> RunAsync(
        string exe, string encoder, int x, int y, int width, int height, int seconds, string path, int token)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("gdigrab");
        psi.ArgumentList.Add("-framerate");
        psi.ArgumentList.Add("30");
        psi.ArgumentList.Add("-offset_x");
        psi.ArgumentList.Add(x.ToString());
        psi.ArgumentList.Add("-offset_y");
        psi.ArgumentList.Add(y.ToString());
        psi.ArgumentList.Add("-video_size");
        psi.ArgumentList.Add(width + "x" + height);
        psi.ArgumentList.Add("-draw_mouse");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-rtbufsize");
        psi.ArgumentList.Add(BufferSize(width, height));
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add("desktop");
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(seconds.ToString());
        psi.ArgumentList.Add("-an");
        AddEncoder(psi, encoder);
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("yuv420p");
        psi.ArgumentList.Add("-movflags");
        psi.ArgumentList.Add("+faststart");
        psi.ArgumentList.Add(path);

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch
        {
            return 0;
        }

        if (proc == null)
            return 0;

        lock (_gate)
        {
            if (token != Volatile.Read(ref _token))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                proc.Dispose();
                return -1;
            }

            _proc = proc;
        }

        Volatile.Write(ref _state, 2);
        Notify(2);
        var stderr = Pump(proc);
        try
        {
            await proc.WaitForExitAsync().ConfigureAwait(false);
        }
        catch
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
        }

        try { await stderr.ConfigureAwait(false); } catch { /* ignore */ }
        bool fileOk = File.Exists(path) && new FileInfo(path).Length > 20_000;
        bool stopped = token != Volatile.Read(ref _token);
        int code = stopped
            ? (fileOk ? 1 : -1)
            : proc.ExitCode != 0 ? 0 : fileOk ? 1 : -1;
        lock (_gate)
        {
            if (ReferenceEquals(_proc, proc))
                _proc = null;
        }

        proc.Dispose();
        return code;
    }

    private static Task Pump(Process proc) => Task.Run(async () =>
    {
        var buffer = new char[2048];
        try
        {
            while (await proc.StandardError.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false) > 0)
            {
                // трубу надо осушать, иначе FFmpeg встанет. Текст ошибки не копим.
            }
        }
        catch
        {
            // процесс уже закрыт
        }
    });

    private async Task<int> RecordOnce(
        string exe, string encoder, int x, int y, int width, int height, int seconds, string path, int token)
    {
        string dir = Path.Combine(Path.GetTempPath(), "Zwmenu", "clip-" + Guid.NewGuid().ToString("N"));
        var audio = new ClipAudioCapture();
        try
        {
            if (_audio != ClipAudio.Silent)
                audio.Start(_audio, dir);
            int code = await RunAsync(exe, encoder, x, y, width, height, seconds, path, token).ConfigureAwait(false);
            audio.Stop();
            if (code != 1 || _audio == ClipAudio.Silent)
                return code;
            if (audio.Partial)
                _audioNote = " Записан не весь звук.";
            if (!audio.AnyFile)
            {
                _audioNote = " Звук не записался.";
                return code;
            }

            if (!await MuxAsync(exe, path, audio).ConfigureAwait(false))
                _audioNote = " Звук не записался.";
            return code;
        }
        finally
        {
            audio.Dispose();
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch
            {
                // временные wav уже не нужны
            }
        }
    }

    private static async Task<bool> MuxAsync(string exe, string video, ClipAudioCapture audio)
    {
        string mixed = video + ".mix.mp4";
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(video);
        int inputs = 0;
        if (audio.SystemPath != null)
        {
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(audio.SystemPath);
            inputs++;
        }

        if (audio.MicPath != null)
        {
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(audio.MicPath);
            inputs++;
        }

        if (inputs == 0)
            return false;
        psi.ArgumentList.Add("-filter_complex");
        psi.ArgumentList.Add(inputs == 2
            ? "[1:a]aresample=48000[a1];[2:a]aresample=48000[a2];[a1][a2]amix=inputs=2:duration=longest:dropout_transition=0,apad[a]"
            : "[1:a]aresample=48000,apad[a]");
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("0:v:0");
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("[a]");
        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("aac");
        psi.ArgumentList.Add("-b:a");
        psi.ArgumentList.Add("160k");
        psi.ArgumentList.Add("-shortest");
        psi.ArgumentList.Add("-movflags");
        psi.ArgumentList.Add("+faststart");
        psi.ArgumentList.Add(mixed);

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null)
                return false;
            var stderr = Pump(proc);
            await proc.WaitForExitAsync().ConfigureAwait(false);
            try { await stderr.ConfigureAwait(false); } catch { /* ignore */ }
            if (proc.ExitCode != 0 || !File.Exists(mixed) || new FileInfo(mixed).Length < 20_000)
            {
                TryDelete(mixed);
                return false;
            }

            string bare = video + ".silent.mp4";
            File.Move(video, bare, overwrite: true);
            try
            {
                File.Move(mixed, video, overwrite: true);
                TryDelete(bare);
                return true;
            }
            catch
            {
                if (!File.Exists(video) && File.Exists(bare))
                    File.Move(bare, video);
                TryDelete(mixed);
                return false;
            }
        }
        catch
        {
            TryDelete(mixed);
            return false;
        }
    }

    private static void AddEncoder(ProcessStartInfo psi, string encoder)
    {
        psi.ArgumentList.Add("-c:v");
        switch (encoder)
        {
            case "nvenc":
                psi.ArgumentList.Add("h264_nvenc");
                psi.ArgumentList.Add("-preset");
                psi.ArgumentList.Add("p1");
                psi.ArgumentList.Add("-tune");
                psi.ArgumentList.Add("ll");
                psi.ArgumentList.Add("-rc");
                psi.ArgumentList.Add("vbr");
                psi.ArgumentList.Add("-cq");
                psi.ArgumentList.Add("23");
                psi.ArgumentList.Add("-b:v");
                psi.ArgumentList.Add("0");
                break;
            case "qsv":
                psi.ArgumentList.Add("h264_qsv");
                psi.ArgumentList.Add("-preset");
                psi.ArgumentList.Add("veryfast");
                psi.ArgumentList.Add("-global_quality");
                psi.ArgumentList.Add("23");
                break;
            case "amf":
                psi.ArgumentList.Add("h264_amf");
                psi.ArgumentList.Add("-quality");
                psi.ArgumentList.Add("speed");
                psi.ArgumentList.Add("-rc");
                psi.ArgumentList.Add("cqp");
                psi.ArgumentList.Add("-qp_i");
                psi.ArgumentList.Add("22");
                psi.ArgumentList.Add("-qp_p");
                psi.ArgumentList.Add("22");
                break;
            default:
                psi.ArgumentList.Add("libx264");
                psi.ArgumentList.Add("-preset");
                psi.ArgumentList.Add("ultrafast");
                psi.ArgumentList.Add("-tune");
                psi.ArgumentList.Add("zerolatency");
                psi.ArgumentList.Add("-crf");
                psi.ArgumentList.Add("23");
                break;
        }
    }

    private async Task<string> DetectEncoderAsync(string exe)
    {
        if (_encoder != null)
            return _encoder;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                Arguments = "-hide_banner -encoders",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using var proc = Process.Start(psi);
            if (proc == null)
                return _encoder = "x264";

            string text = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync().ConfigureAwait(false);
            if (text.Contains("h264_nvenc", StringComparison.Ordinal))
                _encoder = "nvenc";
            else if (text.Contains("h264_qsv", StringComparison.Ordinal))
                _encoder = "qsv";
            else if (text.Contains("h264_amf", StringComparison.Ordinal))
                _encoder = "amf";
            else
                _encoder = "x264";
        }
        catch
        {
            _encoder = "x264";
        }

        return _encoder;
    }

    private void Notify(int phase)
    {
        try { StateChanged?.Invoke(phase); }
        catch { /* подписчик не должен ронять запись */ }
    }

    private static void Kill(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* уже вышел */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    private static string BufferSize(int width, int height)
    {
        long bytes = (long)width * height * 4 * 3;
        if (bytes < 12L * 1024 * 1024)
            bytes = 12L * 1024 * 1024;
        if (bytes > 48L * 1024 * 1024)
            bytes = 48L * 1024 * 1024;
        return (bytes / (1024 * 1024)) + "M";
    }

    private static string ClipPath()
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        string name = $"Zwmenu clip {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4";
        return FileOutPath.NextFree(Path.Combine(desktop, name));
    }
}
