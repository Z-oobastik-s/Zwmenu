using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;

namespace Zwmenu.Services;

/// <summary>Поиск / однократная установка ffmpeg. Без зависаний и ложных «нет файла».</summary>
public static class FfmpegHost
{
    private static readonly object Gate = new();
    private static string? _exe;
    private static Task<string?>? _ensureTask;
    private static string? _lastError;

    private static string ToolsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Zwmenu", "ffmpeg");

    public static string? LastError => _lastError;

    public static async Task<string?> EnsureAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // ct не отменяет общую установку: один отменивший не ломает FFmpeg остальным.
        _ = ct;

        // Доверенный путь после однократного probe — без -version на каждый RunAsync.
        if (IsPresent(_exe))
            return _exe;

        Task<string?> task;
        lock (Gate)
        {
            if (IsPresent(_exe))
                return _exe;

            _ensureTask ??= EnsureCoreAsync(progress, CancellationToken.None);
            task = _ensureTask;
        }

        return await task.ConfigureAwait(false);
    }

    private static async Task<string?> EnsureCoreAsync(IProgress<string>? progress, CancellationToken ct)
    {
        try
        {
            string? found = FindExisting();
            if (found != null)
            {
                _exe = found;
                _lastError = null;
                return _exe;
            }

            Directory.CreateDirectory(ToolsDir);
            string destExe = Path.Combine(ToolsDir, "ffmpeg.exe");
            if (ProbeRunnable(destExe))
            {
                _exe = destExe;
                _lastError = null;
                return _exe;
            }

            progress?.Report("Скачиваю FFmpeg (один раз)…");
            const string url =
                "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl-shared.zip";
            string zipPath = Path.Combine(ToolsDir, "ffmpeg-download.zip");
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { /* ignore */ }

            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Zwmenu/1.0");
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                long? total = resp.Content.Headers.ContentLength;
                await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = File.Create(zipPath);
                var buffer = new byte[65_536];
                long readTotal = 0;
                int read;
                var lastReport = DateTime.UtcNow;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    readTotal += read;
                    if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 400)
                    {
                        lastReport = DateTime.UtcNow;
                        if (total is > 0)
                            progress?.Report($"Скачиваю FFmpeg… {(int)(readTotal * 100 / total.Value)}%");
                        else
                            progress?.Report($"Скачиваю FFmpeg… {readTotal / (1024 * 1024)} МБ");
                    }
                }
            }

            if (!File.Exists(zipPath) || new FileInfo(zipPath).Length < 1_000_000)
                throw new InvalidOperationException("Архив FFmpeg скачался битый. Проверь интернет.");

            progress?.Report("Распаковываю FFmpeg…");
            string extractDir = Path.Combine(ToolsDir, "_extract");
            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, recursive: true);
            ZipFile.ExtractToDirectory(zipPath, extractDir);

            string? ff = Directory.EnumerateFiles(extractDir, "ffmpeg.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (ff == null)
                throw new InvalidOperationException("В архиве нет ffmpeg.exe");

            string bin = Path.GetDirectoryName(ff)!;
            File.Copy(ff, destExe, overwrite: true);
            foreach (var dll in Directory.EnumerateFiles(bin, "*.dll"))
                File.Copy(dll, Path.Combine(ToolsDir, Path.GetFileName(dll)), overwrite: true);

            try { Directory.Delete(extractDir, recursive: true); } catch { /* ignore */ }
            try { File.Delete(zipPath); } catch { /* ignore */ }

            if (!ProbeRunnable(destExe))
                throw new InvalidOperationException("Не удалось установить ffmpeg.exe");

            _exe = destExe;
            _lastError = null;
            progress?.Report("FFmpeg готов");
            return _exe;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _exe = null;
            lock (Gate) { _ensureTask = null; }
            return null;
        }
    }

    private static string? FindExisting()
    {
        string[] candidates =
        [
            Path.Combine(ToolsDir, "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "Tools", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")
        ];
        foreach (var c in candidates)
        {
            if (ProbeRunnable(c))
                return c;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where",
                Arguments = "ffmpeg",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            string? line = p?.StandardOutput.ReadLine()?.Trim();
            p?.WaitForExit(3000);
            if (ProbeRunnable(line))
                return line;
        }
        catch { /* ignore */ }

        return null;
    }

    private static bool IsPresent(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try { return File.Exists(path) && new FileInfo(path).Length >= 50_000; }
        catch { return false; }
    }

    /// <summary>
    /// Однократный probe при поиске/установке. Без редиректа stdout/stderr —
    /// иначе труба может забиться; -version пишет мало, но редирект всё равно лишний.
    /// </summary>
    private static bool ProbeRunnable(string? path)
    {
        if (!IsPresent(path))
            return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path!,
                Arguments = "-version",
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };
            using var p = Process.Start(psi);
            if (p == null)
                return false;
            if (!p.WaitForExit(4000))
            {
                try { p.Kill(true); } catch { /* ignore */ }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Invalidate()
    {
        lock (Gate)
        {
            _exe = null;
            _ensureTask = null;
        }
    }

    public static async Task<(bool Ok, string Log)> RunAsync(
        IReadOnlyList<string> args, CancellationToken ct = default, int timeoutMs = 180_000)
    {
        string? exe = await EnsureAsync(ct: ct).ConfigureAwait(false);
        if (exe == null)
            return (false, _lastError ?? "FFmpeg не найден. Нет интернета для скачивания?");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? ToolsDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-y");
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Не удалось запустить FFmpeg.");

        try { p.StandardInput.Close(); } catch { /* ignore */ }

        var stdoutTask = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = p.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        try
        {
            await p.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            try
            {
                await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(2000))
                    .ConfigureAwait(false);
            }
            catch { /* ignore */ }
            _lastError = "FFmpeg завис или слишком долго работает.";
            return (false, _lastError);
        }

        string log = (await stderrTask.ConfigureAwait(false)) + "\n" + (await stdoutTask.ConfigureAwait(false));
        if (p.ExitCode != 0 && LooksLikeMissingBinary(log))
            Invalidate();
        return (p.ExitCode == 0, log);
    }

    private static bool LooksLikeMissingBinary(string log) =>
        log.Contains("not found", StringComparison.OrdinalIgnoreCase)
        || log.Contains("No such file", StringComparison.OrdinalIgnoreCase)
        || log.Contains("cannot load", StringComparison.OrdinalIgnoreCase);
}
