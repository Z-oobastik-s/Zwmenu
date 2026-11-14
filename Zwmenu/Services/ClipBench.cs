using System.Globalization;
using System.IO;
using System.Text;

namespace Zwmenu.Services;

/// <summary>Кадр, кусок, гифка, ролик из картинок, тихий звук и склейка. ffmpeg качается один раз, как для конвертера.</summary>
public static class ClipBench
{
    public static string? TrimProblem(double start, double end)
    {
        if (double.IsNaN(start) || double.IsNaN(end) || double.IsInfinity(start) || double.IsInfinity(end))
            return "Время не число.";
        if (start < 0)
            return "Начало не может быть меньше нуля.";
        if (end <= start)
            return "Конец должен быть позже начала.";
        if (end - start < 0.2)
            return "Кусок короче 0,2 сек.";
        if (end - start > 6 * 3600)
            return "Кусок длиннее 6 часов.";
        return null;
    }

    public static string? FrameProblem(double second)
    {
        if (double.IsNaN(second) || double.IsInfinity(second))
            return "Секунда не число.";
        if (second < 0)
            return "Секунда не может быть меньше нуля.";
        if (second > 24 * 3600)
            return "Слишком далеко от начала ролика.";
        return null;
    }

    public static string? PaceProblem(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0.2 || seconds > 30)
            return "Длительность кадра: от 0,2 до 30 сек.";
        return null;
    }

    public static string ConcatEscape(string path) =>
        path.Replace("\\", "/").Replace("'", "'\\''");

    public static async Task<(string? Path, string? Error)> FrameAsync(string source, double second, CancellationToken ct = default)
    {
        string? problem = FrameProblem(second);
        if (problem != null)
            return (null, problem);
        if (!File.Exists(source))
            return (null, "Файла нет.");
        string dest = Beside(source, "кадр", ".png");
        var args = new List<string>();
        if (second > 1)
        {
            args.Add("-ss");
            args.Add(Num(second - 1));
            args.Add("-i");
            args.Add(source);
            args.Add("-ss");
            args.Add("1");
        }
        else
        {
            args.Add("-ss");
            args.Add(Num(second));
            args.Add("-i");
            args.Add(source);
        }

        args.Add("-frames:v");
        args.Add("1");
        args.Add(dest);
        return await Run(args, dest, "Кадр не сохранился.", ct).ConfigureAwait(false);
    }

    public static async Task<(string? Path, string? Error)> TrimAsync(string source, double start, double end, CancellationToken ct = default)
    {
        string? problem = TrimProblem(start, end);
        if (problem != null)
            return (null, problem);
        if (!File.Exists(source))
            return (null, "Файла нет.");
        string dest = Beside(source, "кусок", ".mp4");
        string[] args =
        {
            "-ss", Num(start),
            "-to", Num(end),
            "-i", source,
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-crf", "20",
            "-c:a", "aac",
            "-movflags", "+faststart",
            dest
        };
        return await Run(args, dest, "Кусок не собрался.", ct, 300_000).ConfigureAwait(false);
    }

    public static Task<(string? Path, string? Error)> GifAsync(IReadOnlyList<string> files, double seconds, CancellationToken ct = default) =>
        StillsAsync(files, seconds, "гифка", ".gif", GifFilter, ct);

    public static Task<(string? Path, string? Error)> SlideshowAsync(IReadOnlyList<string> files, double seconds, CancellationToken ct = default) =>
        StillsAsync(files, seconds, "ролик", ".mp4", SlideFilter, ct);

    public static async Task<(string? Path, string? Error)> DenoiseAsync(string source, CancellationToken ct = default)
    {
        if (!File.Exists(source))
            return (null, "Файла нет.");
        string dest = Beside(source, "без гула", ".wav");
        string[] deep =
        {
            "-i", source,
            "-vn",
            "-af", "highpass=f=90,afftdn=nr=12:nf=-30",
            "-c:a", "pcm_s16le",
            dest
        };
        var (ok, log) = await FfmpegHost.RunAsync(deep, ct).ConfigureAwait(false);
        if (ok && File.Exists(dest) && new FileInfo(dest).Length > 0)
            return (dest, null);
        if ((log ?? "").Contains("No such filter", StringComparison.OrdinalIgnoreCase))
        {
            try { if (File.Exists(dest)) File.Delete(dest); } catch { /* перезапишем */ }
            string[] mild = { "-i", source, "-vn", "-af", "highpass=f=90", "-c:a", "pcm_s16le", dest };
            var (ok2, log2) = await FfmpegHost.RunAsync(mild, ct).ConfigureAwait(false);
            if (ok2 && File.Exists(dest))
                return (dest, "Глубокого фильтра в этом ffmpeg нет. Срезал только низкий гул.");
            return (null, Tail(log2));
        }

        return (null, Tail(log));
    }

    public static async Task<(string? Path, string? Error)> JoinAsync(IReadOnlyList<string> files, CancellationToken ct = default)
    {
        var list = files.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();
        if (list.Count < 2)
            return (null, "Нужны хотя бы две записи.");
        string dest = Beside(list[0], "склейка", ".wav");
        var args = new List<string>();
        foreach (string file in list)
        {
            args.Add("-i");
            args.Add(file);
        }

        string inputs = string.Concat(Enumerable.Range(0, list.Count).Select(i => "[" + i + ":a]"));
        args.Add("-filter_complex");
        args.Add(inputs + "concat=n=" + list.Count + ":v=0:a=1[a]");
        args.Add("-map");
        args.Add("[a]");
        args.Add("-c:a");
        args.Add("pcm_s16le");
        args.Add(dest);
        return await Run(args, dest, "Склейка не собралась.", ct, 300_000).ConfigureAwait(false);
    }

    public static void SelfCheck()
    {
        if (TrimProblem(1, 1) == null)
            throw new InvalidOperationException("равные границы должны быть ошибкой");
        if (TrimProblem(0, 2) != null)
            throw new InvalidOperationException("нормальный кусок");
        if (TrimProblem(-1, 3) == null)
            throw new InvalidOperationException("минус");
        if (FrameProblem(-0.1) == null)
            throw new InvalidOperationException("кадр");
        if (PaceProblem(0) == null || PaceProblem(0.4) != null)
            throw new InvalidOperationException("темп");
        string escaped = ConcatEscape(@"C:\мой фильм\it's.mp4");
        string quote = "'";
        string want = "C:/мой фильм/it" + quote + "\\" + quote + quote + "s.mp4";
        if (escaped != want)
            throw new InvalidOperationException("путь для склейки: " + escaped);
    }

    static async Task<(string? Path, string? Error)> StillsAsync(
        IReadOnlyList<string> files, double seconds, string suffix, string ext, string filter, CancellationToken ct)
    {
        string? problem = PaceProblem(seconds);
        if (problem != null)
            return (null, problem);
        var list = files.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToList();
        if (list.Count < 2)
            return (null, "Нужны хотя бы две картинки.");
        string listPath = WriteConcat(list, seconds);
        try
        {
            string dest = Beside(list[0], suffix, ext);
            var args = new List<string>
            {
                "-f", "concat", "-safe", "0", "-i", listPath,
                "-vf", filter
            };
            if (ext == ".mp4")
            {
                args.Add("-c:v");
                args.Add("libx264");
                args.Add("-preset");
                args.Add("veryfast");
                args.Add("-crf");
                args.Add("20");
                args.Add("-pix_fmt");
                args.Add("yuv420p");
            }

            args.Add(dest);
            var made = await Run(args, dest, "Не собралось.", ct, 300_000).ConfigureAwait(false);
            if (made.Path != null || ext != ".gif")
                return made;
            try { if (File.Exists(dest)) File.Delete(dest); } catch { /* повтор */ }
            args[args.IndexOf(filter)] = PlainStill;
            return await Run(args, dest, "Гифка не собралась.", ct, 300_000).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(listPath); } catch { /* временный список */ }
        }
    }

    const string PlainStill = "scale=960:540:force_original_aspect_ratio=decrease,pad=960:540:(ow-iw)/2:(oh-ih)/2:color=black";
    const string SlideFilter = "scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2:color=black,format=yuv420p,fps=30";
    const string GifFilter = "scale=960:540:force_original_aspect_ratio=decrease,pad=960:540:(ow-iw)/2:(oh-ih)/2:color=black,split[s0][s1];[s0]palettegen=stats_mode=single[p];[s1][p]paletteuse";

    static string WriteConcat(IReadOnlyList<string> files, double seconds)
    {
        var text = new StringBuilder();
        text.AppendLine("ffconcat version 1.0");
        foreach (string file in files)
        {
            text.Append("file '").Append(ConcatEscape(file)).AppendLine("'");
            text.Append("duration ").Append(seconds.ToString("0.###", CultureInfo.InvariantCulture)).AppendLine();
        }

        text.Append("file '").Append(ConcatEscape(files[^1])).AppendLine("'");
        string path = Path.Combine(Path.GetTempPath(), "zwmenu-" + Guid.NewGuid().ToString("N") + ".ffconcat");
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    static async Task<(string? Path, string? Error)> Run(IReadOnlyList<string> args, string dest, string fallback, CancellationToken ct, int timeout = 180_000)
    {
        var (ok, log) = await FfmpegHost.RunAsync(args, ct, timeout).ConfigureAwait(false);
        if (!ok || !File.Exists(dest) || new FileInfo(dest).Length == 0)
            return (null, Tail(log) is { Length: > 0 } tail ? tail : fallback);
        return (dest, null);
    }

    static string Tail(string? log)
    {
        if (string.IsNullOrWhiteSpace(log))
            return FfmpegHost.LastError ?? "";
        var lines = log.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(3);
        return string.Join(" ", lines);
    }

    static string Beside(string source, string suffix, string ext)
    {
        string dir = Path.GetDirectoryName(source) ?? "";
        string name = Path.GetFileNameWithoutExtension(source);
        return FileOutPath.NextFree(Path.Combine(dir, name + " " + suffix + ext));
    }

    static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
