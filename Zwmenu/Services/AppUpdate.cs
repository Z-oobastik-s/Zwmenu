using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Zwmenu.Overlay;

namespace Zwmenu.Services;

public sealed class UpdateOffer
{
    public required Version Version { get; init; }
    public required string Url { get; init; }
    public required long Size { get; init; }
}

/// <summary>Смотрит последний релиз на GitHub и скачивает установщик.</summary>
public static class AppUpdate
{
    public const string AssetName = "Zwmenu-Setup.exe";
    const string LatestUrl = "https://api.github.com/repos/Z-oobastik-s/Zwmenu/releases/latest";

    static readonly HttpClient Http = CreateClient();
    static int _checking;
    static int _manual;
    static int _stopped;

    public static Version Current
    {
        get
        {
            Version best = new(0, 0, 0, 0);
            try
            {
                var version = typeof(AppUpdate).Assembly.GetName().Version;
                if (version != null)
                    best = Newer(best, Normalize(version));
            }
            catch { /* сборка без версии */ }

            try
            {
                string path = typeof(AppUpdate).Assembly.Location;
                if (path.Length > 0)
                {
                    var info = FileVersionInfo.GetVersionInfo(path);
                    if (TryParseVersion(info.FileVersion ?? "", out var file))
                        best = Newer(best, file);
                    if (TryParseVersion(info.ProductVersion ?? "", out var product))
                        best = Newer(best, product);
                }
            }
            catch { /* файл версии недоступен */ }

            return best;
        }
    }

    public static void Stop() => Volatile.Write(ref _stopped, 1);

    public static void CheckSoon(bool manual)
    {
        if (Volatile.Read(ref _stopped) != 0)
            return;
        if (manual)
            Volatile.Write(ref _manual, 1);
        if (Interlocked.CompareExchange(ref _checking, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            UpdateOffer? offer = null;
            var failed = false;
            try
            {
                offer = await LookAsync().ConfigureAwait(false);
            }
            catch
            {
                failed = true;
            }
            finally
            {
                Interlocked.Exchange(ref _checking, 0);
            }

            var found = offer;
            var broken = failed;
            var asManual = Interlocked.Exchange(ref _manual, 0) == 1;
            var app = Application.Current;
            if (Volatile.Read(ref _stopped) != 0 || app == null || app.Dispatcher.HasShutdownStarted)
                return;
            _ = app.Dispatcher.BeginInvoke(() =>
            {
                if (Volatile.Read(ref _stopped) != 0)
                    return;
                if (found != null && ShouldOffer(found.Version, Current, Skipped(), asManual))
                {
                    UpdateWindow.ShowOffer(found, asManual);
                    return;
                }

                if (!asManual || UpdateWindow.IsBusy)
                    return;

                UpdateWindow.ShowNote(broken
                    ? "Не удалось проверить обновление. Нет связи с GitHub."
                    : "Уже стоит последняя версия.");
            });
        });
    }

    public static string? BusyWork()
    {
        try
        {
            if (EdgeShareService.Shared.Transferring)
                return "Идёт передача файлов. Дождись её и нажми «Поставить» ещё раз.";
        }
        catch { /* связь уже закрыта */ }

        try
        {
            if (ScreenClipRecorder.AnyActive)
                return "Идёт запись экрана. Дождись её и нажми «Поставить» ещё раз.";
        }
        catch { /* запись уже кончилась */ }

        try
        {
            if (VoiceRecService.Shared.State is VoiceRecState.Recording or VoiceRecState.Paused or VoiceRecState.Saving)
                return "Идёт запись голоса. Дождись её и нажми «Поставить» ещё раз.";
        }
        catch { /* запись уже кончилась */ }

        return null;
    }

    public static bool IsTrusted(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return false;
        var path = uri.AbsolutePath;
        return path.StartsWith("/Z-oobastik-s/Zwmenu/releases/download/", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith("/" + AssetName, StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldOffer(Version latest, Version current, Version? skipped, bool manual)
    {
        if (latest <= current)
            return false;
        if (manual)
            return true;
        return skipped == null || skipped != latest;
    }

    public static bool TryParseVersion(string text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        text = text.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];
        var dash = text.IndexOf('-');
        if (dash >= 0)
            text = text[..dash];
        if (!Version.TryParse(text, out var parsed))
            return false;

        version = Normalize(parsed);
        return true;
    }

    public static bool TryParseLatest(string json, out UpdateOffer offer)
    {
        offer = null!;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("tag_name", out var tagNode))
                return false;
            if (!TryParseVersion(tagNode.GetString() ?? "", out var version))
                return false;
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
                if (!string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
                    continue;
                var url = asset.TryGetProperty("browser_download_url", out var urlNode) ? urlNode.GetString() : null;
                if (string.IsNullOrWhiteSpace(url) || !IsTrusted(url))
                    return false;
                long size = 0;
                if (asset.TryGetProperty("size", out var sizeNode) && sizeNode.TryGetInt64(out var parsed))
                    size = parsed;
                offer = new UpdateOffer { Version = version, Url = url, Size = size };
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    public static async Task DownloadAsync(UpdateOffer offer, IProgress<(long Got, long Total)> progress, CancellationToken cancel)
    {
        if (!IsTrusted(offer.Url))
            throw new InvalidDataException("url");

        DiskSweep.KeepOnly(offer.Version);
        var finalPath = SetupPath(offer.Version);
        if (Ready(finalPath, offer.Size))
        {
            progress.Report((Math.Max(offer.Size, 1), Math.Max(offer.Size, 1)));
            return;
        }

        var partial = finalPath + ".part";
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await DownloadOnce(offer, partial, finalPath, progress, cancel).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                last = new IOException("stall");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InvalidDataException ex)
            {
                last = ex;
            }
            catch (Exception ex)
            {
                last = ex;
            }

            await Task.Delay(700, cancel).ConfigureAwait(false);
        }

        throw last ?? new IOException("download");
    }

    public static void StartInstaller(Version version)
    {
        var path = SetupPath(version);
        if (path.Contains('"'))
            throw new InvalidOperationException("path");
        Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = "/c ping 127.0.0.1 -n 4 >nul & start \"\" \"" + path + "\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    public static void RememberSkip(Version version)
    {
        var path = SkipPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, version.ToString());
    }

    public static string SetupPath(Version version) =>
        Path.Combine(Path.GetTempPath(), "Zwmenu-Setup-" + version + ".exe");

    public static void SelfCheck()
    {
        if (!TryParseVersion("v1.3.36", out var parsed) || parsed != new Version(1, 3, 36))
            throw new InvalidOperationException("метка версии");
        if (!TryParseVersion("1.10.0-beta", out var beta) || beta != new Version(1, 10, 0))
            throw new InvalidOperationException("хвост метки");
        if (TryParseVersion("не версия", out _))
            throw new InvalidOperationException("мусор принят как версия");

        var older = new Version(1, 3, 35);
        var same = new Version(1, 3, 36);
        var newer = new Version(1, 3, 37);
        if (!ShouldOffer(newer, same, null, manual: false))
            throw new InvalidOperationException("новая версия не предложена");
        if (ShouldOffer(same, same, null, manual: false))
            throw new InvalidOperationException("та же версия предложена");
        if (ShouldOffer(older, same, null, manual: true))
            throw new InvalidOperationException("старая версия предложена");
        if (ShouldOffer(newer, same, newer, manual: false))
            throw new InvalidOperationException("отложенная версия снова предложена");
        if (!ShouldOffer(newer, same, newer, manual: true))
            throw new InvalidOperationException("ручная проверка не показала отложенную версию");

        const string json = """
            {"tag_name":"v9.9.9","assets":[{"name":"Zwmenu-Setup.exe","browser_download_url":"https://github.com/Z-oobastik-s/Zwmenu/releases/download/v9.9.9/Zwmenu-Setup.exe","size":123456789}]}
            """;
        if (!TryParseLatest(json, out var offer) || offer.Version != new Version(9, 9, 9) || offer.Size != 123456789)
            throw new InvalidOperationException("разбор релиза");
        if (TryParseLatest("""{"tag_name":"v9.9.9","assets":[{"name":"readme.txt","browser_download_url":"https://example.invalid/readme.txt","size":10}]}""", out _))
            throw new InvalidOperationException("релиз без установщика принят");
        if (TryParseLatest("""{"tag_name":"v9.9.9","assets":[{"name":"Zwmenu-Setup.exe","browser_download_url":"https://evil.example/Zwmenu-Setup.exe","size":123456789}]}""", out _))
            throw new InvalidOperationException("чужой адрес принят");
        if (!IsTrusted("https://github.com/Z-oobastik-s/Zwmenu/releases/download/v1.3.37/Zwmenu-Setup.exe"))
            throw new InvalidOperationException("свой адрес отвергнут");
        if (IsTrusted("http://github.com/Z-oobastik-s/Zwmenu/releases/download/v1.3.37/Zwmenu-Setup.exe"))
            throw new InvalidOperationException("http принят");
    }

    static async Task DownloadOnce(UpdateOffer offer, string partial, string finalPath, IProgress<(long Got, long Total)> progress, CancellationToken cancel)
    {
        long have = 0;
        if (File.Exists(partial))
            have = new FileInfo(partial).Length;
        if (offer.Size > 0 && have > offer.Size)
        {
            File.Delete(partial);
            have = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, offer.Url);
        if (have > 0)
            request.Headers.Range = new RangeHeaderValue(have, null);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            TryDelete(partial);
            throw new IOException("range");
        }

        response.EnsureSuccessStatusCode();
        var append = response.StatusCode == HttpStatusCode.PartialContent && have > 0;
        if (!append)
            have = 0;

        var total = offer.Size > 0
            ? offer.Size
            : have + (response.Content.Headers.ContentLength ?? 0);
        await using var input = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        var mode = append ? FileMode.Append : FileMode.Create;
        long got = have;
        long lastReport = 0;
        await using (var output = new FileStream(partial, mode, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
        {
            var buffer = new byte[81920];
            while (true)
            {
                int read;
                using (var stall = new CancellationTokenSource(TimeSpan.FromSeconds(40)))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, stall.Token))
                    read = await input.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                if (read == 0)
                    break;
                await output.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                got += read;
                var now = Environment.TickCount64;
                if (total > 0 && (got == total || now - lastReport >= 200))
                {
                    lastReport = now;
                    progress.Report((got, total));
                }
                else if (total <= 0 && now - lastReport >= 200)
                {
                    lastReport = now;
                    progress.Report((got, 0));
                }
            }
        }

        if (total > 0)
            progress.Report((got, total));
        try
        {
            Finish(partial, finalPath, offer.Size);
        }
        catch (InvalidDataException)
        {
            TryDelete(partial);
            throw;
        }
    }

    static void Finish(string partial, string finalPath, long expected)
    {
        using (var check = File.OpenRead(partial))
        {
            if (check.Length < 1_000_000)
                throw new InvalidDataException("small");
            if (expected > 0 && check.Length != expected)
                throw new InvalidDataException("size");
            if (check.ReadByte() != 'M' || check.ReadByte() != 'Z')
                throw new InvalidDataException("exe");
        }

        if (File.Exists(finalPath))
            File.Delete(finalPath);
        File.Move(partial, finalPath);
    }

    static bool Ready(string path, long expected)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            using var check = File.OpenRead(path);
            if (check.Length < 1_000_000)
                return false;
            if (expected > 0 && check.Length != expected)
                return false;
            return check.ReadByte() == 'M' && check.ReadByte() == 'Z';
        }
        catch
        {
            return false;
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* файл ещё занят */ }
    }

    static async Task<UpdateOffer?> LookAsync()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, cancel.Token).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancel.Token).ConfigureAwait(false);
        return TryParseLatest(json, out var offer) ? offer : null;
    }

    static Version? Skipped()
    {
        try
        {
            var path = SkipPath();
            if (!File.Exists(path))
                return null;
            return TryParseVersion(File.ReadAllText(path), out var version) ? version : null;
        }
        catch
        {
            return null;
        }
    }

    static string SkipPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zwmenu", "update-skip.txt");

    static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0));

    static Version Newer(Version left, Version right) => left >= right ? left : right;

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Zwmenu");
        return client;
    }
}
