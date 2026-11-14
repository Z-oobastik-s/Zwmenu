namespace Zwmenu.Services;

public sealed record MediaFormat(string Id, string Title, string Extension, bool Video);

public sealed record MediaQualityPreset(
    string Id,
    string Title,
    string Hint,
    string AudioBitrate,
    int VideoCrf);

public sealed record MediaTool(string Id, string Title, string Hint, string Tag);

/// <summary>Конвертация / сжатие аудио и видео через FFmpeg.</summary>
public static class MediaConvertService
{
    private static readonly HashSet<string> AudioExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".ogg", ".flac", ".m4a", ".aac", ".wma", ".opus", ".aiff", ".aif", ".oga", ".mka"
    };

    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".mov", ".avi", ".wmv", ".m4v", ".mpeg", ".mpg", ".ts", ".flv", ".3gp"
    };

    public static IReadOnlyList<MediaFormat> AudioFormats { get; } =
    [
        new("mp3", "MP3", ".mp3", false),
        new("wav", "WAV", ".wav", false),
        new("ogg", "OGG", ".ogg", false),
        new("flac", "FLAC", ".flac", false),
        new("m4a", "M4A", ".m4a", false),
        new("opus", "OPUS", ".opus", false),
        new("aac", "AAC", ".aac", false),
        new("wma", "WMA", ".wma", false)
    ];

    public static IReadOnlyList<MediaFormat> VideoFormats { get; } =
    [
        new("mp4", "MP4", ".mp4", true),
        new("webm", "WebM", ".webm", true),
        new("mkv", "MKV", ".mkv", true),
        new("mov", "MOV", ".mov", true),
        new("avi", "AVI", ".avi", true),
        new("mp3", "MP3", ".mp3", false),
        new("wav", "WAV", ".wav", false),
        new("m4a", "M4A", ".m4a", false)
    ];

    public static IReadOnlyList<MediaQualityPreset> QualityPresets { get; } =
    [
        new("high", "Высокое", "Почти без потерь", "320k", 18),
        new("normal", "Норма", "Для телефона и чатов", "192k", 23),
        new("light", "Лёгкое", "Малый вес", "96k", 28),
        new("mini", "Мини", "Очень лёгкий файл", "64k", 32)
    ];

    /// <summary>Внутреннее кольцо: 8 инструментов с равным шагом 45°.</summary>
    public static IReadOnlyList<MediaTool> Tools { get; } =
    [
        new("high", "Высок.", "Сжать в высоком качестве", "zip:high"),
        new("normal", "Норма", "Сжать нормально", "zip:normal"),
        new("light", "Лёгкое", "Сильно облегчить", "zip:light"),
        new("fadein", "Нараст.", "Плавное нарастание ~2 сек", "act:fadein"),
        new("fadeout", "Затух.", "Плавное затухание ~2 сек", "act:fadeout"),
        new("gain", "Громче", "Усилить на +6 дБ", "act:gain"),
        new("mono", "Моно", "Один канал — легче файл", "act:mono"),
        new("extract", "Звук", "Вытащить / сохранить как MP3", "act:extract")
    ];

    public static bool IsAudioFile(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && File.Exists(path)
        && AudioExt.Contains(Path.GetExtension(path));

    public static bool IsVideoFile(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && File.Exists(path)
        && VideoExt.Contains(Path.GetExtension(path));

    public static bool IsMediaFile(string path) => IsAudioFile(path) || IsVideoFile(path);

    public static IReadOnlyList<string> FilterMedia(IEnumerable<string> paths) =>
        paths.Where(IsMediaFile).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static bool HasVideo(IEnumerable<string> paths) => paths.Any(IsVideoFile);

    public static IReadOnlyList<MediaFormat> FormatsFor(IEnumerable<string> paths) =>
        HasVideo(paths) ? VideoFormats : AudioFormats;

    public static async Task<(string? OutputPath, string? Error)> ConvertAsync(
        string sourcePath, string formatId, string qualityId = "normal", CancellationToken ct = default)
    {
        if (!IsMediaFile(sourcePath))
            return (null, "Это не аудио/видео.");

        var fmt = AudioFormats.Concat(VideoFormats)
            .FirstOrDefault(f => string.Equals(f.Id, formatId, StringComparison.OrdinalIgnoreCase));
        if (fmt == null)
            return (null, "Неизвестный формат.");

        var quality = QualityPresets.FirstOrDefault(q =>
            string.Equals(q.Id, qualityId, StringComparison.OrdinalIgnoreCase))
            ?? QualityPresets[1];

        string dir = Path.GetDirectoryName(sourcePath) ?? "";
        string name = Path.GetFileNameWithoutExtension(sourcePath);
        string dest = FileOutPath.NextFree(Path.Combine(dir, name + fmt.Extension));

        var args = BuildConvertArgs(sourcePath, dest, fmt, quality);
        var (ok, log) = await FfmpegHost.RunAsync(args, ct).ConfigureAwait(false);
        if (!ok || !File.Exists(dest))
            return (null, ShortErr(log) ?? FfmpegHost.LastError ?? "Не удалось конвертировать.");

        return (dest, null);
    }

    public static async Task<(string? OutputPath, string? Error)> CompressAsync(
        string sourcePath, string qualityId, CancellationToken ct = default)
    {
        if (!IsMediaFile(sourcePath))
            return (null, "Это не аудио/видео.");

        var quality = QualityPresets.FirstOrDefault(q =>
            string.Equals(q.Id, qualityId, StringComparison.OrdinalIgnoreCase));
        if (quality == null)
            return (null, "Неизвестный режим.");

        string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        string formatId = ext switch
        {
            ".mp3" => "mp3",
            ".wav" => "wav",
            ".ogg" or ".oga" => "ogg",
            ".flac" => "flac",
            ".m4a" => "m4a",
            ".aac" => "aac",
            ".opus" => "opus",
            ".wma" => "wma",
            ".webm" => "webm",
            ".mkv" => "mkv",
            ".mov" => "mov",
            ".avi" => "avi",
            _ => IsVideoFile(sourcePath) ? "mp4" : "mp3"
        };

        var fmt = AudioFormats.Concat(VideoFormats)
            .First(f => f.Id == formatId);

        string dir = Path.GetDirectoryName(sourcePath) ?? "";
        string name = Path.GetFileNameWithoutExtension(sourcePath);
        string dest = FileOutPath.NextFree(Path.Combine(dir, name + " сжато" + fmt.Extension));

        var args = BuildConvertArgs(sourcePath, dest, fmt, quality);
        var (ok, log) = await FfmpegHost.RunAsync(args, ct).ConfigureAwait(false);
        if (!ok || !File.Exists(dest))
            return (null, ShortErr(log) ?? FfmpegHost.LastError ?? "Не удалось сжать.");

        return (dest, null);
    }

    public static async Task<(string? OutputPath, string? Error)> ExtractAudioAsync(
        string sourcePath, CancellationToken ct = default)
    {
        if (!IsVideoFile(sourcePath) && !IsAudioFile(sourcePath))
            return (null, "Нужен видео или аудио файл.");

        string dir = Path.GetDirectoryName(sourcePath) ?? "";
        string name = Path.GetFileNameWithoutExtension(sourcePath);
        string dest = FileOutPath.NextFree(Path.Combine(dir, name + " звук.mp3"));

        var args = new[] { "-i", sourcePath, "-vn", "-c:a", "libmp3lame", "-b:a", "192k", dest };
        var (ok, log) = await FfmpegHost.RunAsync(args, ct).ConfigureAwait(false);
        if (!ok || !File.Exists(dest))
            return (null, ShortErr(log) ?? "Не удалось извлечь звук.");

        return (dest, null);
    }

    public static async Task<(string? OutputPath, string? Error)> ToMonoAsync(
        string sourcePath, CancellationToken ct = default)
    {
        if (!IsMediaFile(sourcePath))
            return (null, "Это не аудио/видео.");

        string ext = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(ext))
            ext = ".mp3";

        string dir = Path.GetDirectoryName(sourcePath) ?? "";
        string name = Path.GetFileNameWithoutExtension(sourcePath);
        string dest = FileOutPath.NextFree(Path.Combine(dir, name + " моно" + ext.ToLowerInvariant()));

        string audioCodec = CodecForExt(ext);
        string[] args = IsVideoFile(sourcePath)
            ? ["-i", sourcePath, "-c:v", "copy", "-ac", "1", "-c:a", audioCodec, "-b:a", "160k", dest]
            : ["-i", sourcePath, "-ac", "1", "-c:a", audioCodec, "-b:a", "160k", dest];

        var (ok, log) = await FfmpegHost.RunAsync(args, ct).ConfigureAwait(false);
        if (!ok || !File.Exists(dest))
            return (null, ShortErr(log) ?? "Не удалось сделать моно.");

        return (dest, null);
    }

    public static async Task<(string? OutputPath, string? Error)> FadeInAsync(
        string sourcePath, double seconds = 2.0, CancellationToken ct = default)
    {
        if (!IsMediaFile(sourcePath))
            return (null, "Это не аудио/видео.");

        seconds = Math.Clamp(seconds, 0.3, 15);
        string s = seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        return await ApplyAudioFilterAsync(
            sourcePath, $"afade=t=in:st=0:d={s}", " нараст", ct).ConfigureAwait(false);
    }

    public static async Task<(string? OutputPath, string? Error)> FadeOutAsync(
        string sourcePath, double seconds = 2.0, CancellationToken ct = default)
    {
        if (!IsMediaFile(sourcePath))
            return (null, "Это не аудио/видео.");

        seconds = Math.Clamp(seconds, 0.3, 15);
        double? dur = await ProbeDurationAsync(sourcePath, ct).ConfigureAwait(false);
        if (dur is null or <= 0.5)
            return (null, "Не удалось узнать длину трека.");

        double fade = Math.Min(seconds, dur.Value * 0.45);
        double start = Math.Max(0, dur.Value - fade);
        string d = fade.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        string st = start.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        return await ApplyAudioFilterAsync(
            sourcePath, $"afade=t=out:st={st}:d={d}", " затух", ct).ConfigureAwait(false);
    }

    public static async Task<(string? OutputPath, string? Error)> GainAsync(
        string sourcePath, double db = 6, CancellationToken ct = default)
    {
        if (!IsMediaFile(sourcePath))
            return (null, "Это не аудио/видео.");

        db = Math.Clamp(db, -24, 24);
        string v = db.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        string suffix = db >= 0 ? $" +{v}дБ" : $" {v}дБ";
        return await ApplyAudioFilterAsync(
            sourcePath, $"volume={v}dB", suffix.Replace(',', '.'), ct).ConfigureAwait(false);
    }

    private static async Task<(string? OutputPath, string? Error)> ApplyAudioFilterAsync(
        string sourcePath, string audioFilter, string nameSuffix, CancellationToken ct)
    {
        string ext = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(ext))
            ext = ".mp3";

        string dir = Path.GetDirectoryName(sourcePath) ?? "";
        string name = Path.GetFileNameWithoutExtension(sourcePath);
        string dest = FileOutPath.NextFree(Path.Combine(dir, name + nameSuffix + ext.ToLowerInvariant()));
        string audioCodec = CodecForExt(ext);

        string[] args = IsVideoFile(sourcePath)
            ? ["-i", sourcePath, "-c:v", "copy", "-af", audioFilter, "-c:a", audioCodec, "-b:a", "192k", dest]
            : ["-i", sourcePath, "-af", audioFilter, "-c:a", audioCodec, "-b:a", "192k", dest];

        var (ok, log) = await FfmpegHost.RunAsync(args, ct).ConfigureAwait(false);
        if (!ok || !File.Exists(dest))
            return (null, ShortErr(log) ?? "Не удалось обработать аудио.");

        return (dest, null);
    }

    private static async Task<double?> ProbeDurationAsync(string path, CancellationToken ct)
    {
        // Только probe: без выходного файла ffmpeg сразу пишет Duration и выходит.
        var (_, log) = await FfmpegHost.RunAsync(["-i", path], ct, timeoutMs: 20_000)
            .ConfigureAwait(false);
        var m = System.Text.RegularExpressions.Regex.Match(
            log, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        if (!m.Success)
            return null;
        int h = int.Parse(m.Groups[1].Value);
        int min = int.Parse(m.Groups[2].Value);
        double sec = double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        return h * 3600 + min * 60 + sec;
    }

    private static List<string> BuildConvertArgs(
        string source, string dest, MediaFormat fmt, MediaQualityPreset quality)
    {
        var a = new List<string> { "-i", source };

        if (fmt.Video)
        {
            switch (fmt.Id)
            {
                case "webm":
                    a.AddRange(["-c:v", "libvpx-vp9", "-crf", quality.VideoCrf.ToString(), "-b:v", "0",
                        "-c:a", "libopus", "-b:a", quality.AudioBitrate, dest]);
                    break;
                case "avi":
                    a.AddRange(["-c:v", "mpeg4", "-q:v", Math.Clamp(quality.VideoCrf / 3, 3, 8).ToString(),
                        "-c:a", "libmp3lame", "-b:a", quality.AudioBitrate, dest]);
                    break;
                default:
                    a.AddRange(["-c:v", "libx264", "-preset", "medium", "-crf", quality.VideoCrf.ToString(),
                        "-c:a", "aac", "-b:a", quality.AudioBitrate, "-movflags", "+faststart", dest]);
                    break;
            }
            return a;
        }

        a.Add("-vn");
        switch (fmt.Id)
        {
            case "wav":
                a.AddRange(["-c:a", "pcm_s16le", dest]);
                break;
            case "flac":
                a.AddRange(["-c:a", "flac", dest]);
                break;
            case "ogg":
                a.AddRange(["-c:a", "libvorbis", "-q:a", VorbisQ(quality.Id).ToString(), dest]);
                break;
            case "opus":
                a.AddRange(["-c:a", "libopus", "-b:a", quality.AudioBitrate, dest]);
                break;
            case "m4a":
            case "aac":
                a.AddRange(["-c:a", "aac", "-b:a", quality.AudioBitrate, dest]);
                break;
            case "wma":
                a.AddRange(["-c:a", "wmav2", "-b:a", quality.AudioBitrate, dest]);
                break;
            default:
                a.AddRange(["-c:a", "libmp3lame", "-b:a", quality.AudioBitrate, dest]);
                break;
        }
        return a;
    }

    private static int VorbisQ(string qualityId) => qualityId switch
    {
        "high" => 6,
        "light" => 2,
        _ => 4
    };

    private static string CodecForExt(string ext)
    {
        ext = ext.ToLowerInvariant();
        return ext switch
        {
            ".wav" => "pcm_s16le",
            ".flac" => "flac",
            ".ogg" or ".oga" => "libvorbis",
            ".opus" => "libopus",
            ".m4a" or ".aac" or ".mp4" or ".mov" or ".mkv" => "aac",
            ".wma" => "wmav2",
            ".webm" => "libopus",
            _ => "libmp3lame"
        };
    }

    private static string? ShortErr(string log)
    {
        if (string.IsNullOrWhiteSpace(log))
            return null;

        if (ContainsAny(log, "does not contain any stream", "matches no streams", "Stream map '", "Output with label 'a'"))
            return "В файле нет нужной дорожки (звук/видео).";
        if (ContainsAny(log, "Invalid data found", "could not find codec", "Unknown encoder", "Encoder not found"))
            return "Формат или кодек не поддерживается.";
        if (ContainsAny(log, "No such file", "Error opening input", "Permission denied"))
            return "Не удалось открыть файл.";
        if (ContainsAny(log, "Conversion failed", "Error while"))
            return "FFmpeg не смог обработать файл.";

        var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? last = lines.LastOrDefault(l =>
            l.Contains("Error", StringComparison.OrdinalIgnoreCase)
            || l.Contains("Invalid", StringComparison.OrdinalIgnoreCase)
            || l.Contains("Unknown", StringComparison.OrdinalIgnoreCase));
        if (last == null)
            return null;
        // Не светим сырой английский лог в UI — общее сообщение.
        return "Ошибка обработки медиа.";
    }

    private static bool ContainsAny(string hay, params string[] needles)
    {
        foreach (var n in needles)
            if (hay.Contains(n, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

}
