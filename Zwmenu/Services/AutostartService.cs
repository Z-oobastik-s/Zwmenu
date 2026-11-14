using System.IO;
using Microsoft.Win32;

namespace Zwmenu.Services;

/// <summary>Автозапуск Zwmenu вместе с Windows (HKCU Run).</summary>
public static class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Zwmenu";

    /// <summary>
    /// Автозапуск включён, но в реестре лежит exe от прошлой сборки.
    /// Иначе после входа в Windows стартует старая программа и не знает новые пункты колеса.
    /// </summary>
    public static void RepairStoredPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) is not string value || string.IsNullOrWhiteSpace(value))
                return;

            string exe = Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "Zwmenu.exe");
            if (!File.Exists(exe))
                return;

            string stored = value.Trim().Trim('"');
            if (string.Equals(stored, exe, StringComparison.OrdinalIgnoreCase))
                return;

            key.SetValue(ValueName, QuoteIfNeeded(exe));
        }
        catch
        {
            // Реестр недоступен: этот запуск всё равно работает.
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
            if (key == null)
                return false;

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }

            string exe = Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "Zwmenu.exe");
            if (!File.Exists(exe))
                return false;

            key.SetValue(ValueName, QuoteIfNeeded(exe));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string QuoteIfNeeded(string path) =>
        path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;
}
