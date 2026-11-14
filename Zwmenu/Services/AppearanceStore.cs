using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Zwmenu.Services;

public sealed class AppearanceSettings
{
    /// <summary>Показывать PNG-скин рамки вокруг колеса.</summary>
    public bool UseRingSkin { get; set; }

    /// <summary>Имя файла в Assets/Skins, например skin-01.png.</summary>
    public string? RingSkinFile { get; set; }

    /// <summary>0..4 — вид диска/орбиты/подсказки. См. RingLooks.</summary>
    public int RingLook { get; set; }

    /// <summary>hold | instant | alt — см. MiddleButtonMode.</summary>
    public string MiddleClickMode { get; set; } = "hold";

    /// <summary>Сколько мс держать СКМ, чтобы открыть колесо (режим hold).</summary>
    public int MiddleHoldMs { get; set; } = 220;
}

public sealed record RingSkinOption(string FileName, string Title, string FullPath);

/// <summary>
/// Доли радиуса PNG (от центра до края картинки):
/// Inner — внутренняя кромка орнамента, Band — середина декоративного кольца, Outer — внешний край.
/// </summary>
public sealed record RingSkinMetrics(double Inner, double Band, double Outer);

/// <summary>Как подогнать колесо под выбранный скин.</summary>
public sealed record SkinFit(
    string Path,
    string FileName,
    double SkinRadius,
    double OrbitRadius,
    double ClearRadius,
    RingSkinMetrics Metrics);

/// <summary>Внешний вид колеса: %LOCALAPPDATA%\Zwmenu\appearance.json.</summary>
public static class AppearanceStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly Dictionary<string, string> Titles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["skin-01.png"] = "Алая броня",
        ["skin-02.png"] = "Лунная вода",
        ["skin-03.png"] = "Алый венок",
        ["skin-04.png"] = "Фиолетовая звезда",
        ["skin-05.png"] = "Сакура и фонари",
        ["crimson-ring.png"] = "Алая броня"
    };

    /// <summary>Замерено по альфа-каналу PNG (доля от половины стороны).</summary>
    private static readonly Dictionary<string, RingSkinMetrics> MetricsMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["skin-01.png"] = new(0.421, 0.674, 0.893),
        ["skin-02.png"] = new(0.592, 0.795, 0.999),
        ["skin-03.png"] = new(0.543, 0.778, 0.998),
        ["skin-04.png"] = new(0.552, 0.759, 0.948),
        ["skin-05.png"] = new(0.571, 0.784, 0.959),
        ["crimson-ring.png"] = new(0.421, 0.674, 0.893)
    };

    private static readonly RingSkinMetrics DefaultMetrics = new(0.55, 0.76, 0.96);

    private static AppearanceSettings _cache = new();
    private static bool _loaded;
    private static IReadOnlyList<RingSkinOption>? _skinsCache;

    /// <summary>Растёт только при Save: колесо не трогает диск, пока ревизия та же.</summary>
    public static int Revision { get; private set; }

    public static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zwmenu");

    public static string FilePath => Path.Combine(DirectoryPath, "appearance.json");

    public static string SkinsFolder =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Skins");

    public static AppearanceSettings Current
    {
        get
        {
            if (!_loaded)
                Reload();
            return _cache;
        }
    }

    public static AppearanceSettings Reload()
    {
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath))
            {
                _cache = new AppearanceSettings();
                return _cache;
            }

            string json = File.ReadAllText(FilePath);
            _cache = JsonSerializer.Deserialize<AppearanceSettings>(json, Options) ?? new AppearanceSettings();
            Normalize(_cache);
            return _cache;
        }
        catch
        {
            _cache = new AppearanceSettings();
            return _cache;
        }
    }

    public static void Save(AppearanceSettings settings)
    {
        Normalize(settings);
        Directory.CreateDirectory(DirectoryPath);
        string json = JsonSerializer.Serialize(settings, Options);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, FilePath, overwrite: true);
        _cache = settings;
        _loaded = true;
        Revision++;
        // Скин мог смениться: сбросим только список (картинки кэширует RingImageCache).
        _skinsCache = null;
        RingImageCache.WarmActiveSkin();
    }

    public static IReadOnlyList<RingSkinOption> ListSkins()
    {
        if (_skinsCache != null)
            return _skinsCache;

        try
        {
            if (!Directory.Exists(SkinsFolder))
                return _skinsCache = Array.Empty<RingSkinOption>();

            _skinsCache = Directory.EnumerateFiles(SkinsFolder, "*.png")
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p =>
                {
                    string name = Path.GetFileName(p);
                    string title = Titles.TryGetValue(name, out var t)
                        ? t
                        : Path.GetFileNameWithoutExtension(name);
                    return new RingSkinOption(name, title, p);
                })
                .ToList();
            return _skinsCache;
        }
        catch
        {
            return _skinsCache = Array.Empty<RingSkinOption>();
        }
    }

    public static bool RingSkinAvailable() => ListSkins().Count > 0;

    public static string? ResolveSelectedPath(AppearanceSettings? settings = null)
    {
        settings ??= Current;
        if (!settings.UseRingSkin)
            return null;

        // Normalize только если файл ещё не выбран: не сканируем папку на каждый СКМ.
        if (string.IsNullOrWhiteSpace(settings.RingSkinFile))
            Normalize(settings);
        if (string.IsNullOrWhiteSpace(settings.RingSkinFile))
            return null;

        string path = Path.Combine(SkinsFolder, settings.RingSkinFile);
        return File.Exists(path) ? path : null;
    }

    public static RingSkinMetrics GetMetrics(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return DefaultMetrics;
        return MetricsMap.TryGetValue(fileName, out var m) ? m : DefaultMetrics;
    }

    /// <summary>
    /// Подгонка: декоративное кольцо скина проходит через центры кнопок,
    /// внутренний просвет остаётся под центр, clear учитывает внешние шипы/луну.
    /// </summary>
    public static SkinFit? TryFitActiveSkin(double baseOrbit, double slotSize, double centerSize)
    {
        string? path = ResolveSelectedPath();
        if (path == null)
            return null;

        string file = Path.GetFileName(path);
        var m = GetMetrics(file);

        double band = Math.Clamp(m.Band, 0.35, 0.95);
        double inner = Math.Clamp(m.Inner, 0.2, band - 0.05);
        double outer = Math.Clamp(m.Outer, band + 0.02, 1.05);

        // Сначала сажаем кольцо орнамента на текущую орбиту.
        double skinR = baseOrbit / band;

        // Центр и внутренний край кнопок не должны врезаться в орнамент.
        double minInnerClear = centerSize * 0.5 + 14;
        if (skinR * inner < minInnerClear)
            skinR = minInnerClear / inner;

        double orbit = skinR * band;

        // Орбита не меньше расчётной для числа слотов.
        if (orbit < baseOrbit)
        {
            orbit = baseOrbit;
            skinR = orbit / band;
        }

        // Кнопки чуть внутрь середины ленты: иконки читаются, орнамент обрамляет.
        orbit = skinR * (band * 0.97 + inner * 0.03);

        double clear = skinR * outer + Math.Max(24, slotSize * 0.35);
        return new SkinFit(path, file, skinR, orbit, clear, new RingSkinMetrics(inner, band, outer));
    }

    public static MiddleButtonMode GetMiddleClickMode(AppearanceSettings? s = null)
    {
        s ??= Current;
        return (s.MiddleClickMode ?? "").Trim().ToLowerInvariant() switch
        {
            "instant" => MiddleButtonMode.Instant,
            "alt" => MiddleButtonMode.Alt,
            _ => MiddleButtonMode.Hold
        };
    }

    public static int GetMiddleHoldMs(AppearanceSettings? s = null)
    {
        s ??= Current;
        return Math.Clamp(s.MiddleHoldMs <= 0 ? 220 : s.MiddleHoldMs, 120, 600);
    }

    public static int GetRingLook(AppearanceSettings? s = null)
    {
        s ??= Current;
        return Math.Clamp(s.RingLook, 0, RingLooks.Count - 1);
    }

    public static RingLookPalette ActiveLook(AppearanceSettings? s = null)
        => RingLooks.Get(GetRingLook(s));

    private static void Normalize(AppearanceSettings s)
    {
        string mode = (s.MiddleClickMode ?? "").Trim().ToLowerInvariant();
        s.MiddleClickMode = mode is "instant" or "alt" or "hold" ? mode : "hold";
        s.MiddleHoldMs = Math.Clamp(s.MiddleHoldMs <= 0 ? 220 : s.MiddleHoldMs, 120, 600);
        s.RingLook = Math.Clamp(s.RingLook, 0, RingLooks.Count - 1);

        var skins = ListSkins();
        if (skins.Count == 0)
        {
            s.UseRingSkin = false;
            s.RingSkinFile = null;
            return;
        }

        if (string.Equals(s.RingSkinFile, "crimson-ring.png", StringComparison.OrdinalIgnoreCase))
            s.RingSkinFile = "skin-01.png";

        if (string.IsNullOrWhiteSpace(s.RingSkinFile)
            || skins.All(x => !string.Equals(x.FileName, s.RingSkinFile, StringComparison.OrdinalIgnoreCase)))
        {
            s.RingSkinFile = skins[0].FileName;
        }
    }
}
