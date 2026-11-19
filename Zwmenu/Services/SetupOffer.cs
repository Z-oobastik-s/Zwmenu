using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using NAudio.CoreAudioApi;

namespace Zwmenu.Services;

/// <summary>Чего не хватает на этом компьютере, и установка по кнопке.</summary>
public static class SetupOffer
{
    public const string ZipUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip";
    public const string CableNote = "Кабеля нет. Нажми включить, и я предложу его поставить.";

    static readonly HttpClient Http = CreateClient();
    static int _checking;

    public static bool IsCableUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        return uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && uri.Host.Equals("download.vb-audio.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.EndsWith("/VBCABLE_Driver_Pack46.zip", StringComparison.OrdinalIgnoreCase);
    }

    public static bool CableInstalled()
    {
        MMDeviceEnumerator? en = null;
        try
        {
            en = new MMDeviceEnumerator();
            foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active | DeviceState.Disabled))
            {
                try
                {
                    if (VoiceShiftLive.IsCablePlay(dev.FriendlyName))
                        return true;
                }
                finally { try { dev.Dispose(); } catch { /* устройство уже закрыто */ } }
            }

            return false;
        }
        catch
        {
            return true;
        }
        finally
        {
            try { en?.Dispose(); } catch { /* ignore */ }
        }
    }

    public static void CheckSoon()
    {
        if (ScreenshotStore.Current.SetupCableLater)
            return;
        if (Interlocked.CompareExchange(ref _checking, 1, 0) != 0)
            return;
        _ = Task.Run(() =>
        {
            bool missing = false;
            try { missing = !CableInstalled(); }
            catch { missing = false; }
            finally { Interlocked.Exchange(ref _checking, 0); }

            if (!missing)
                return;
            var app = System.Windows.Application.Current;
            if (app == null || app.Dispatcher.HasShutdownStarted)
                return;
            app.Dispatcher.BeginInvoke(() =>
            {
                if (ScreenshotStore.Current.SetupCableLater || CableInstalled())
                    return;
                Overlay.SetupOfferWindow.ShowOffer(stealFocus: false);
            });
        });
    }

    public static void ShowNow()
    {
        var app = System.Windows.Application.Current;
        if (app == null)
            return;
        if (app.Dispatcher.CheckAccess())
            Overlay.SetupOfferWindow.ShowOffer(stealFocus: true);
        else
            app.Dispatcher.BeginInvoke(() => Overlay.SetupOfferWindow.ShowOffer(stealFocus: true));
    }

    public static void RememberLater()
    {
        var s = ScreenshotStore.Current;
        s.SetupCableLater = true;
        ScreenshotStore.Save(s);
    }

    public static async Task<string> InstallCableAsync(IProgress<string> progress, CancellationToken cancel)
    {
        if (!IsCableUrl(ZipUrl))
            throw new InvalidDataException("адрес кабеля");

        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Zwmenu", "setup", "vbcable");
        Directory.CreateDirectory(dir);
        string zip = Path.Combine(dir, "VBCABLE_Driver_Pack46.zip");
        progress.Report("Скачиваю кабель…");
        using (var resp = await Http.GetAsync(ZipUrl, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;
            await using var input = await resp.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            await using var output = File.Create(zip);
            var buffer = new byte[65536];
            long got = 0;
            int read;
            var last = DateTime.UtcNow;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancel).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                got += read;
                if ((DateTime.UtcNow - last).TotalMilliseconds < 300)
                    continue;
                last = DateTime.UtcNow;
                progress.Report(total is > 0
                    ? "Скачиваю кабель… " + (int)(got * 100 / total.Value) + "%"
                    : "Скачиваю кабель… " + (got / 1024) + " КБ");
            }
        }

        if (!File.Exists(zip) || new FileInfo(zip).Length < 100_000)
            throw new InvalidDataException("Архив кабеля скачался битый.");

        progress.Report("Распаковываю…");
        string unpack = Path.Combine(dir, "pack");
        if (Directory.Exists(unpack))
            Directory.Delete(unpack, true);
        Directory.CreateDirectory(unpack);
        ExtractSafe(zip, unpack);
        string? exe = Directory.EnumerateFiles(unpack, "VBCABLE_Setup_x64.exe", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (exe == null)
            throw new InvalidDataException("В архиве нет установщика кабеля.");

        progress.Report("Открываю установщик…");
        Process? setup;
        try
        {
            setup = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "Установка отменена. Без кабеля голос в другие программы не уйдёт.";
        }

        if (setup == null)
            return "Установщик не открылся.";
        await Task.Run(() => setup.WaitForExit(180000), cancel).ConfigureAwait(false);
        if (CableInstalled())
            return "Кабель появился. Можно включать голос.";
        return "Установщик закрылся. Нажми в нём Install Driver, если ещё не нажал. Потом перезагрузи компьютер, иначе кабель может не появиться.";
    }

    public static void SelfCheck()
    {
        if (!IsCableUrl(ZipUrl))
            throw new InvalidOperationException("адрес кабеля");
        if (IsCableUrl("https://example.com/VBCABLE_Driver_Pack46.zip"))
            throw new InvalidOperationException("чужой адрес принят");
        _ = CableInstalled();
    }

    static void ExtractSafe(string zipPath, string dest)
    {
        string root = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;
            string name = entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (name.Contains(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(name))
                continue;
            string target = Path.GetFullPath(Path.Combine(dest, name));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Zwmenu");
        return http;
    }
}
