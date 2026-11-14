using System.Diagnostics;

namespace Zwmenu.Services;

/// <summary>Командная строка в папке, которая уже открыта. Права те же, что у пункта на колесе.</summary>
public static class FolderConsole
{
    public static string Quoted(string directory)
    {
        string full = Path.GetFullPath(directory);
        if (full.EndsWith('\\'))
            full += "\\";
        return "\"" + full + "\"";
    }

    public static string CdArguments(string directory) => "/d /k cd /d " + Quoted(directory);

    public static void Launch(string? directory, bool admin)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = true
        };
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            info.Arguments = CdArguments(directory);
        if (admin)
            info.Verb = "runas";
        Process.Start(info)?.Dispose();
    }

    public static void SelfCheck()
    {
        string root = CdArguments(@"C:\");
        if (root != "/d /k cd /d \"C:\\\\\"")
            throw new InvalidOperationException("корень: " + root);
        string spaced = CdArguments(@"C:\папка с пробелом");
        if (!spaced.Contains("\"C:\\папка с пробелом\"", StringComparison.Ordinal))
            throw new InvalidOperationException("пробел: " + spaced);

        string dir = Path.Combine(Path.GetTempPath(), "zwmenu cmd " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = "/d /c cd /d " + Quoted(dir) + " & cd",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("консоль не запустилась");
            string printed = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* проверка закончена */ }
                throw new InvalidOperationException("консоль не завершилась");
            }

            if (!string.Equals(printed, dir, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("консоль открылась не в папке: " + printed);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* проверка закончена */ }
        }
    }
}
