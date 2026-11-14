namespace Zwmenu.Services;

/// <summary>Общий хелпер имён выходных файлов без перезаписи.</summary>
public static class FileOutPath
{
    public static string NextFree(string path)
    {
        if (!File.Exists(path))
            return path;

        string dir = Path.GetDirectoryName(path) ?? "";
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 2; i < 1000; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(dir, $"{name}_{Guid.NewGuid():N}{ext}");
    }

    public static string WithSuffix(string path, string suffix)
    {
        string dir = Path.GetDirectoryName(path) ?? "";
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        return Path.Combine(dir, name + suffix + ext);
    }
}
