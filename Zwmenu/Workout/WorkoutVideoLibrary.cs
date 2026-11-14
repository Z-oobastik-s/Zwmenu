using System.IO;

namespace Zwmenu.Workout;

public sealed record WorkoutVideo(string Path, string Title, int Index);

public static class WorkoutVideoLibrary
{
    public static IReadOnlyList<WorkoutVideo> Load()
    {
        var files = new List<string>();
        foreach (var dir in CandidateDirs())
        {
            if (!Directory.Exists(dir))
                continue;
            files.AddRange(Directory.EnumerateFiles(dir, "*.mp4", SearchOption.TopDirectoryOnly));
            files.AddRange(Directory.EnumerateFiles(dir, "*.webm", SearchOption.TopDirectoryOnly));
            files.AddRange(Directory.EnumerateFiles(dir, "*.mkv", SearchOption.TopDirectoryOnly));
        }

        var distinct = files
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var list = new List<WorkoutVideo>();
        for (int i = 0; i < distinct.Count; i++)
        {
            string path = distinct[i];
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            string title = $"Видео {i + 1}";
            // если имя не video_дата: покажем как есть
            if (!name.StartsWith("video_", StringComparison.OrdinalIgnoreCase))
                title = name;
            list.Add(new WorkoutVideo(path, title, i));
        }
        return list;
    }

    private static IEnumerable<string> CandidateDirs()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "Assets", "Workout", "Videos");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Zwmenu", "Workout", "Videos");
    }
}