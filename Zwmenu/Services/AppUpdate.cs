using System.Diagnostics;
using System.Net.Http;
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

    public static Version Current
    {
        get
        {
            var version = typeof(AppUpdate).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return Normalize(version);
        }
    }

    public static void CheckSoon(bool manual)
    {
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

            var found = offer;
            var broken = failed;
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (found != null && ShouldOffer(found.Version, Current, Skipped(), manual))
                {
                    UpdateWindow.ShowOffer(found);
                    return;
                }

                if (!manual)
                    return;

                UpdateWindow.ShowNote(broken
                    ? "Не удалось проверить обновление. Нет связи с GitHub."
                    : "Уже стоит последняя версия.");
            });
        });
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
                if (string.IsNullOrWhiteSpace(url))
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
        var partial = SetupPath(offer.Version) + ".part";
        var finalPath = SetupPath(offer.Version);
        try
        {
            using var response = await Http.GetAsync(offer.Url, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? offer.Size;
            await using var input = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                long got = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                    got += read;
                    progress.Report((got, total));
                }

                if (offer.Size > 0 && got != offer.Size)
                    throw new InvalidDataException("size");
                if (got < 1_000_000)
                    throw new InvalidDataException("small");
            }

            await using (var check = File.OpenRead(partial))
            {
                if (check.ReadByte() != 'M' || check.ReadByte() != 'Z')
                    throw new InvalidDataException("exe");
            }

            if (File.Exists(finalPath))
                File.Delete(finalPath);
            File.Move(partial, finalPath);
        }
        catch
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { /* хвост закачки не мешает следующей */ }
            throw;
        }
    }

    public static void StartInstaller(Version version)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = SetupPath(version),
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
            UseShellExecute = true
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
            {"tag_name":"v9.9.9","assets":[{"name":"Zwmenu-Setup.exe","browser_download_url":"https://example.invalid/Zwmenu-Setup.exe","size":123456789}]}
            """;
        if (!TryParseLatest(json, out var offer) || offer.Version != new Version(9, 9, 9) || offer.Size != 123456789)
            throw new InvalidOperationException("разбор релиза");
        if (TryParseLatest("""{"tag_name":"v9.9.9","assets":[{"name":"readme.txt","browser_download_url":"https://example.invalid/readme.txt","size":10}]}""", out _))
            throw new InvalidOperationException("релиз без установщика принят");
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

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Zwmenu");
        return client;
    }
}
