using System.IO.Compression;
using System.Net.Http;
using Vosk;

namespace Zwmenu.Services;

/// <summary>
/// Общие модели Vosk: русский + украинский (для суржика — оба сразу).
/// </summary>
internal static class VoskModelStore
{
    private const string RuUrl = "https://alphacephei.com/vosk/models/vosk-model-small-ru-0.22.zip";
    private const string RuFolder = "vosk-model-small-ru-0.22";
    private const string UkUrl = "https://alphacephei.com/vosk/models/vosk-model-small-uk-v3-small.zip";
    private const string UkFolder = "vosk-model-small-uk-v3-small";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };
    private static readonly object Gate = new();

    private static Model? _ru;
    private static Model? _uk;
    private static Task<Model>? _ruTask;
    private static Task<Model?>? _ukTask;

    public static string VoiceRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Zwmenu", "Voice");

    public static Task<Model> PreloadRuAsync()
    {
        lock (Gate)
        {
            if (_ru != null)
                return Task.FromResult(_ru);
            return _ruTask ??= Task.Run(LoadRu);
        }
    }

    /// <summary>Украинская модель; null если скачать не удалось (диктовка продолжит на RU).</summary>
    public static Task<Model?> PreloadUkAsync()
    {
        lock (Gate)
        {
            if (_uk != null)
                return Task.FromResult<Model?>(_uk);
            return _ukTask ??= Task.Run(LoadUkSoft);
        }
    }

    public static async Task PreloadSurzhykAsync()
    {
        var ru = PreloadRuAsync();
        var uk = PreloadUkAsync();
        await ru.ConfigureAwait(false);
        await uk.ConfigureAwait(false);
    }

    private static Model LoadRu()
    {
        lock (Gate)
        {
            if (_ru != null)
                return _ru;
        }

        Vosk.Vosk.SetLogLevel(-1);
        string path = EnsureModel(RuFolder, RuUrl);
        var model = new Model(path);
        lock (Gate)
        {
            _ru ??= model;
            return _ru;
        }
    }

    private static Model? LoadUkSoft()
    {
        try
        {
            lock (Gate)
            {
                if (_uk != null)
                    return _uk;
            }

            Vosk.Vosk.SetLogLevel(-1);
            string path = EnsureModel(UkFolder, UkUrl);
            var model = new Model(path);
            lock (Gate)
            {
                _uk ??= model;
                return _uk;
            }
        }
        catch
        {
            return null;
        }
    }

    private static string EnsureModel(string folderName, string url)
    {
        Directory.CreateDirectory(VoiceRoot);
        string modelDir = Path.Combine(VoiceRoot, folderName);
        if (LooksReady(modelDir))
            return modelDir;

        string zipPath = Path.Combine(VoiceRoot, folderName + ".zip");
        using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var stream = Http.GetStreamAsync(url).GetAwaiter().GetResult())
            stream.CopyTo(fs);

        if (Directory.Exists(modelDir))
            Directory.Delete(modelDir, true);
        ZipFile.ExtractToDirectory(zipPath, VoiceRoot);
        try { File.Delete(zipPath); } catch { /* ignore */ }

        // Иногда zip кладёт файлы на уровень выше / с другим именем — найдём final.mdl.
        if (!LooksReady(modelDir))
        {
            string? found = Directory.EnumerateDirectories(VoiceRoot)
                .FirstOrDefault(LooksReady);
            if (found != null && !string.Equals(found, modelDir, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (Directory.Exists(modelDir))
                        Directory.Delete(modelDir, true);
                    Directory.Move(found, modelDir);
                }
                catch
                {
                    if (LooksReady(found))
                        return found;
                }
            }
        }

        if (!LooksReady(modelDir))
            throw new InvalidOperationException("Не нашёл папку модели после распаковки: " + folderName);
        return modelDir;
    }

    private static bool LooksReady(string dir) =>
        Directory.Exists(dir) && File.Exists(Path.Combine(dir, "am", "final.mdl"));
}
