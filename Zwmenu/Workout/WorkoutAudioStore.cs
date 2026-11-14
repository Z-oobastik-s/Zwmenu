using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Zwmenu.Workout;

public sealed class WorkoutAudioSettings
{
    public string? MusicPath { get; set; }
    public int DefaultWorkSeconds { get; set; } = 30;
    public float MusicVolume { get; set; } = 0.35f;
    public float VideoVolume { get; set; } = 0.8f;
    public bool ReminderEnabled { get; set; } = true;
    public int ReminderHour { get; set; } = 20;
    public int ReminderMinute { get; set; } = 0;
    public int ReminderJitterMinutes { get; set; } = 30;
    public Dictionary<string, string> Phrases { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<(string Key, string Title, string Hint)> PhraseSlots { get; } =
    [
        ("start", "Старт подхода", "Когда сказал «начать» / «старт»"),
        ("set_done", "Подход сделан", "После «выполнил» / «завершил»"),
        ("rest_over", "Отдых конец", "Когда таймер отдыха закончился"),
        ("next", "Следующее упражнение", "Переход к следующему пункту"),
        ("day_done", "День готов", "Все подходы дня закрыты"),
        ("keep_going", "Продолжай", "Мотивация между подходами (опционально)"),
        ("work_time_up", "Время подхода вышло", "Таймер работы дошёл до нуля")
    ];
}

public static class WorkoutAudioStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zwmenu", "Workout");

    public static string FilePath => Path.Combine(DirectoryPath, "audio.json");
    public static string PhrasesFolder => Path.Combine(DirectoryPath, "Phrases");

    public static WorkoutAudioSettings Load()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            Directory.CreateDirectory(PhrasesFolder);
            if (!File.Exists(FilePath))
                return new WorkoutAudioSettings();
            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<WorkoutAudioSettings>(json, Options) ?? new WorkoutAudioSettings();
        }
        catch
        {
            return new WorkoutAudioSettings();
        }
    }

    public static void Save(WorkoutAudioSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        Directory.CreateDirectory(PhrasesFolder);
        string json = JsonSerializer.Serialize(settings, Options);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Copy(tmp, FilePath, overwrite: true);
        try { File.Delete(tmp); } catch { /* ignore */ }
    }

    public static string? ResolvePhrase(WorkoutAudioSettings s, string key)
    {
        if (s.Phrases.TryGetValue(key, out var p) && !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            return p;
        string auto = Path.Combine(PhrasesFolder, key + ".mp3");
        if (File.Exists(auto)) return auto;
        auto = Path.Combine(PhrasesFolder, key + ".wav");
        if (File.Exists(auto)) return auto;
        return null;
    }

    public static string? ResolveMusic(WorkoutAudioSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.MusicPath) && File.Exists(s.MusicPath))
            return s.MusicPath;
        return null;
    }
}