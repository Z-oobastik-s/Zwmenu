using System.Windows.Media;

namespace Zwmenu.Services;

/// <summary>Вид колеса. Каждый собран из слоёв: свечение, кольцо, центр.</summary>
public sealed class RingLookPalette
{
    public required string Title { get; init; }
    public required string Hint { get; init; }
    public required Brush SoftGlow { get; init; }
    public required Brush MatteDisc { get; init; }
    public required Brush OrbitStroke { get; init; }
    public double OrbitThickness { get; init; } = 1.5;
    public required Brush SlotRim { get; init; }
    public required Brush HintBg { get; init; }
    public required Brush HintBorder { get; init; }
    public required Brush HintTitle { get; init; }
    public required Brush HintBody { get; init; }
    public Color PreviewCore { get; init; }
    public Color PreviewRim { get; init; }
    public Color PreviewGlow { get; init; }

    /// <summary>Папка Assets/Looks, если вид собран из рисунков, а не из заливок.</summary>
    public string? ArtFolder { get; init; }

    /// <summary>Середина кольца как доля радиуса картинки. Кнопки садятся сюда.</summary>
    public double Band { get; init; } = 0.62;

    /// <summary>Насколько свечение шире кольца.</summary>
    public double AuraScale { get; init; } = 1.15;

    public double AuraOpacity { get; init; } = 0.92;
    public double CoreSize { get; init; } = 140;

    /// <summary>Цвет глифа и ободка тарелки на видах с рисунком.</summary>
    public Color IconColor { get; init; } = Color.FromRgb(0xF5, 0xF0, 0xE8);

    /// <summary>Подложка под иконку, чтобы её было видно на кольце.</summary>
    public Color PlateColor { get; init; } = Color.FromArgb(0xF0, 0x14, 0x14, 0x18);
}

public static class RingLooks
{
    public const int Count = 12;

    private static readonly RingLookPalette[] All =
    [
        Build(
            "Signal Forge",
            "Ночной графит и мятный сигнал. Точный инструмент, без неона.",
            soft: Soft(0x99, 0x00, 0x00, 0x00, 0x44, 0x08, 0x14, 0x12),
            disc: Disc(0xF0, 0x14, 0x16, 0x1A, 0xE8, 0x11, 0x13, 0x17, 0xD6, 0x0C, 0x0E, 0x12),
            orbit: GradientStroke(0x70, 0x6E, 0xE0, 0xC4, 0x22, 0x2A, 0x48, 0x42, 0x55, 0x6E, 0xE0, 0xC4),
            orbitW: 1.6,
            rim: Solid(0x55, 0x6E, 0xE0, 0xC4),
            hintBg: Solid(0xF0, 0x0C, 0x0E, 0x12),
            hintBorder: Solid(0x44, 0x6E, 0xE0, 0xC4),
            hintTitle: Solid(0xFF, 0xEC, 0xEF, 0xF3),
            hintBody: Solid(0xB8, 0x8A, 0x92, 0x9C),
            core: Color.FromRgb(0x11, 0x13, 0x17),
            rimC: Color.FromRgb(0x6E, 0xE0, 0xC4),
            glow: Color.FromArgb(0x55, 0x1E, 0x3A, 0x36),
            art: "classic",
            band: 0.62,
            auraScale: 1.04,
            auraOpacity: 0.7,
            coreSize: 108,
            ir: 0x6E, ig: 0xE0, ib: 0xC4,
            pr: 0x11, pg: 0x13, pb: 0x17),
        Build(
            "Обсидиан",
            "Чёрное стекло с серебряным бликом. Строго и холодно.",
            soft: Soft(0xAA, 0x00, 0x00, 0x00, 0x33, 0x18, 0x1C, 0x22),
            disc: Disc(0xF4, 0x0A, 0x0B, 0x0E, 0xEE, 0x08, 0x09, 0x0C, 0xCC, 0x04, 0x05, 0x08),
            orbit: GradientStroke(0x90, 0xE8, 0xEE, 0xF4, 0x28, 0xA0, 0xA8, 0xB4, 0x70, 0xD8, 0xDE, 0xE8),
            orbitW: 1.8,
            rim: Solid(0x70, 0xD0, 0xD6, 0xE0),
            hintBg: Solid(0xF2, 0x0A, 0x0B, 0x0E),
            hintBorder: Solid(0x55, 0xC8, 0xD0, 0xDC),
            hintTitle: Solid(0xFF, 0xEE, 0xF2, 0xF6),
            hintBody: Solid(0xB0, 0xC0, 0xC8, 0xD0),
            core: Color.FromRgb(0x0A, 0x0B, 0x0E),
            rimC: Color.FromRgb(0xD0, 0xD6, 0xE0),
            glow: Color.FromArgb(0x55, 0x20, 0x28, 0x34),
            art: "obsidian",
            band: 0.62,
            auraScale: 1.04,
            auraOpacity: 0.65,
            coreSize: 108,
            ir: 0xE4, ig: 0xEA, ib: 0xF2,
            pr: 0x0C, pg: 0x0E, pb: 0x12),
        Build(
            "Шампань",
            "Тёплое золото, лучи и бусины между кнопками.",
            soft: Soft(0x88, 0x1A, 0x10, 0x08, 0x40, 0x3A, 0x28, 0x10),
            disc: Disc(0xF2, 0x1C, 0x16, 0x10, 0xE8, 0x18, 0x12, 0x0C, 0xD0, 0x12, 0x0E, 0x0A),
            orbit: GradientStroke(0xB0, 0xE8, 0xC8, 0x7A, 0x40, 0xA8, 0x78, 0x48, 0xC0, 0xF0, 0xD8, 0x9A),
            orbitW: 2.2,
            rim: Solid(0x88, 0xE0, 0xC0, 0x78),
            hintBg: Solid(0xF2, 0x16, 0x12, 0x0C),
            hintBorder: Solid(0x66, 0xD8, 0xB4, 0x6A),
            hintTitle: Solid(0xFF, 0xF4, 0xE6, 0xC8),
            hintBody: Solid(0xB8, 0xD8, 0xBC, 0x8A),
            core: Color.FromRgb(0x1A, 0x14, 0x0E),
            rimC: Color.FromRgb(0xE0, 0xC0, 0x78),
            glow: Color.FromArgb(0x55, 0x6A, 0x48, 0x18),
            art: "champagne",
            band: 0.62,
            auraScale: 1.05,
            auraOpacity: 0.8,
            coreSize: 108,
            ir: 0xF0, ig: 0xD4, ib: 0x9A,
            pr: 0x24, pg: 0x18, pb: 0x0E),
        Build(
            "Лёд",
            "Морозное стекло и короткие грани между кнопками.",
            soft: Soft(0x77, 0x08, 0x14, 0x1C, 0x44, 0x28, 0x48, 0x58),
            disc: Disc(0xF0, 0x12, 0x1A, 0x22, 0xE4, 0x10, 0x16, 0x1E, 0xC8, 0x0C, 0x12, 0x18),
            orbit: GradientStroke(0xA0, 0xC8, 0xE8, 0xF4, 0x38, 0x70, 0x98, 0xB0, 0x88, 0xB0, 0xD8, 0xEC),
            orbitW: 1.9,
            rim: Solid(0x78, 0xB8, 0xD8, 0xEC),
            hintBg: Solid(0xF0, 0x10, 0x16, 0x1C),
            hintBorder: Solid(0x55, 0xA0, 0xC8, 0xDC),
            hintTitle: Solid(0xFF, 0xE8, 0xF4, 0xFA),
            hintBody: Solid(0xB0, 0xB8, 0xD0, 0xE0),
            core: Color.FromRgb(0x12, 0x1A, 0x22),
            rimC: Color.FromRgb(0xB0, 0xD4, 0xE8),
            glow: Color.FromArgb(0x50, 0x40, 0x70, 0x88),
            art: "ice",
            band: 0.62,
            auraScale: 1.05,
            auraOpacity: 0.75,
            coreSize: 108,
            ir: 0xE4, ig: 0xF4, ib: 0xFF,
            pr: 0x10, pg: 0x18, pb: 0x24),
        Build(
            "Карбон",
            "Плетёный карбон и алая нить. Спортивный люкс.",
            soft: Soft(0x90, 0x10, 0x04, 0x04, 0x40, 0x28, 0x08, 0x08),
            disc: Disc(0xF2, 0x12, 0x12, 0x14, 0xE8, 0x0E, 0x0E, 0x10, 0xD0, 0x0A, 0x0A, 0x0C),
            orbit: GradientStroke(0xB0, 0xE0, 0x48, 0x48, 0x30, 0x60, 0x18, 0x18, 0x90, 0xC0, 0x38, 0x38),
            orbitW: 2.0,
            rim: Solid(0x70, 0xC8, 0x40, 0x40),
            hintBg: Solid(0xF2, 0x10, 0x10, 0x12),
            hintBorder: Solid(0x66, 0xC0, 0x38, 0x38),
            hintTitle: Solid(0xFF, 0xF2, 0xE8, 0xE8),
            hintBody: Solid(0xB0, 0xC8, 0x98, 0x98),
            core: Color.FromRgb(0x12, 0x12, 0x14),
            rimC: Color.FromRgb(0xD0, 0x48, 0x48),
            glow: Color.FromArgb(0x55, 0x50, 0x10, 0x10),
            art: "carbon",
            band: 0.62,
            auraScale: 1.04,
            auraOpacity: 0.7,
            coreSize: 108,
            ir: 0xFF, ig: 0x70, ib: 0x70,
            pr: 0x14, pg: 0x14, pb: 0x16),
        Build(
            "Созвездие",
            "Золотые звёзды на ночном небе. Лучи между иконками, в центре звезда.",
            soft: Soft(0x88, 0x18, 0x14, 0x28, 0x40, 0x40, 0x28, 0x10),
            disc: Disc(0xE8, 0x10, 0x0C, 0x18, 0xCC, 0x08, 0x06, 0x12, 0x00, 0x08, 0x06, 0x12),
            orbit: GradientStroke(0xC0, 0xE8, 0xC8, 0x78, 0x50, 0xA0, 0x70, 0x30, 0xD0, 0xF0, 0xD8, 0x90),
            orbitW: 1.6,
            rim: Solid(0xCC, 0xE8, 0xC4, 0x72),
            hintBg: Solid(0xF0, 0x10, 0x0C, 0x16),
            hintBorder: Solid(0x66, 0xE0, 0xC0, 0x70),
            hintTitle: Solid(0xFF, 0xF6, 0xE6, 0xB8),
            hintBody: Solid(0xC0, 0xE0, 0xC8, 0x90),
            core: Color.FromRgb(0x12, 0x0C, 0x18),
            rimC: Color.FromRgb(0xE8, 0xC4, 0x72),
            glow: Color.FromArgb(0x66, 0x40, 0x28, 0x10),
            art: "stars",
            band: 0.54,
            auraScale: 1.02,
            auraOpacity: 0.9,
            coreSize: 104,
            ir: 0xF8, ig: 0xE6, ib: 0xB0,
            pr: 0x22, pg: 0x14, pb: 0x36),
        Build(
            "Кузница",
            "Кованое железо, заклёпки и жар в трещинах. Уголь в центре.",
            soft: Soft(0x99, 0x20, 0x08, 0x04, 0x44, 0x60, 0x18, 0x08),
            disc: Disc(0xF0, 0x14, 0x0C, 0x0A, 0xD0, 0x10, 0x08, 0x08, 0x00, 0x10, 0x08, 0x08),
            orbit: GradientStroke(0xD0, 0xE0, 0x70, 0x30, 0x60, 0x80, 0x28, 0x10, 0xC0, 0xC0, 0x48, 0x20),
            orbitW: 2.0,
            rim: Solid(0xCC, 0xE0, 0x78, 0x38),
            hintBg: Solid(0xF2, 0x12, 0x0A, 0x08),
            hintBorder: Solid(0x66, 0xC0, 0x50, 0x28),
            hintTitle: Solid(0xFF, 0xF4, 0xD8, 0xC0),
            hintBody: Solid(0xC0, 0xD0, 0x98, 0x80),
            core: Color.FromRgb(0x14, 0x0C, 0x0A),
            rimC: Color.FromRgb(0xE0, 0x78, 0x38),
            glow: Color.FromArgb(0x70, 0x80, 0x28, 0x08),
            art: "forge",
            band: 0.64,
            auraScale: 1.06,
            auraOpacity: 0.9,
            coreSize: 104,
            ir: 0xFF, ig: 0xB0, ib: 0x70,
            pr: 0x2A, pg: 0x12, pb: 0x0C),
        Build(
            "Венок",
            "Тёмные листья и белые цветы. Мягкий зелёный свет за венком.",
            soft: Soft(0x77, 0x0C, 0x18, 0x10, 0x40, 0x20, 0x40, 0x28),
            disc: Disc(0xE0, 0x0E, 0x14, 0x10, 0xC0, 0x0A, 0x10, 0x0C, 0x00, 0x0A, 0x10, 0x0C),
            orbit: GradientStroke(0xB0, 0xC8, 0xD8, 0xC0, 0x40, 0x60, 0x80, 0x60, 0x90, 0xE8, 0xF0, 0xE0),
            orbitW: 1.6,
            rim: Solid(0xBB, 0xD0, 0xE0, 0xC8),
            hintBg: Solid(0xF0, 0x0E, 0x14, 0x10),
            hintBorder: Solid(0x55, 0xB0, 0xC8, 0xA8),
            hintTitle: Solid(0xFF, 0xF4, 0xF6, 0xF0),
            hintBody: Solid(0xC0, 0xD4, 0xC8, 0xB8),
            core: Color.FromRgb(0x10, 0x16, 0x12),
            rimC: Color.FromRgb(0xD8, 0xE8, 0xDC),
            glow: Color.FromArgb(0x55, 0x20, 0x50, 0x30),
            art: "garden",
            band: 0.54,
            auraScale: 1.08,
            auraOpacity: 0.85,
            coreSize: 104,
            ir: 0xD4, ig: 0xF0, ib: 0xC8,
            pr: 0x10, pg: 0x24, pb: 0x18),
        Build(
            "Механизм",
            "Латунные шестерни и стальной обод. В центре рубиновый камень.",
            soft: Soft(0x88, 0x18, 0x12, 0x08, 0x40, 0x50, 0x38, 0x14),
            disc: Disc(0xF0, 0x14, 0x10, 0x0A, 0xD8, 0x10, 0x0C, 0x08, 0x00, 0x10, 0x0C, 0x08),
            orbit: GradientStroke(0xD0, 0xE8, 0xC8, 0x78, 0x50, 0xA0, 0x80, 0x40, 0xC0, 0xF0, 0xE0, 0xA0),
            orbitW: 1.8,
            rim: Solid(0xCC, 0xE0, 0xC0, 0x70),
            hintBg: Solid(0xF2, 0x12, 0x0E, 0x08),
            hintBorder: Solid(0x66, 0xD0, 0xB0, 0x60),
            hintTitle: Solid(0xFF, 0xF6, 0xE8, 0xC0),
            hintBody: Solid(0xC8, 0xD0, 0xB0, 0x80),
            core: Color.FromRgb(0x16, 0x10, 0x0A),
            rimC: Color.FromRgb(0xE0, 0xC0, 0x70),
            glow: Color.FromArgb(0x55, 0x60, 0x40, 0x18),
            art: "watch",
            band: 0.64,
            auraScale: 1.05,
            auraOpacity: 0.8,
            coreSize: 108,
            ir: 0xF8, ig: 0xD8, ib: 0x98,
            pr: 0x24, pg: 0x18, pb: 0x0C),
        Build(
            "Призма",
            "Хрустальная корона и алмаз в центре. Холодный перелив.",
            soft: Soft(0x77, 0x08, 0x18, 0x20, 0x44, 0x40, 0x80, 0x90),
            disc: Disc(0xE8, 0x0C, 0x14, 0x18, 0xC8, 0x08, 0x10, 0x14, 0x00, 0x08, 0x10, 0x14),
            orbit: GradientStroke(0xD0, 0xC8, 0xF0, 0xF8, 0x50, 0x70, 0xB0, 0xC8, 0xB0, 0xE8, 0xF8, 0xFF),
            orbitW: 1.6,
            rim: Solid(0xCC, 0xC8, 0xEC, 0xF8),
            hintBg: Solid(0xF0, 0x0C, 0x12, 0x16),
            hintBorder: Solid(0x66, 0xA8, 0xD8, 0xEC),
            hintTitle: Solid(0xFF, 0xE8, 0xF6, 0xFC),
            hintBody: Solid(0xC0, 0xD0, 0xE4, 0xF0),
            core: Color.FromRgb(0x0C, 0x14, 0x18),
            rimC: Color.FromRgb(0xC8, 0xEC, 0xF8),
            glow: Color.FromArgb(0x55, 0x40, 0x90, 0xA0),
            art: "prism",
            band: 0.54,
            auraScale: 0.92,
            auraOpacity: 0.95,
            coreSize: 104,
            ir: 0xD8, ig: 0xF2, ib: 0xFF,
            pr: 0x10, pg: 0x20, pb: 0x2C),
        Build(
            "Даркнет",
            "Почти чёрное кольцо и тусклые красные узлы. Иконки горят алым.",
            soft: Soft(0x88, 0x18, 0x04, 0x08, 0x30, 0x40, 0x08, 0x10),
            disc: Disc(0xF2, 0x08, 0x06, 0x08, 0xE0, 0x06, 0x04, 0x06, 0x00, 0x06, 0x04, 0x06),
            orbit: GradientStroke(0xD0, 0xC4, 0x28, 0x38, 0x40, 0x60, 0x10, 0x18, 0xB0, 0xE0, 0x30, 0x40),
            orbitW: 1.6,
            rim: Solid(0xDD, 0xE0, 0x38, 0x48),
            hintBg: Solid(0xF2, 0x0A, 0x06, 0x08),
            hintBorder: Solid(0x66, 0xC0, 0x30, 0x40),
            hintTitle: Solid(0xFF, 0xF4, 0xD8, 0xDC),
            hintBody: Solid(0xC0, 0xD0, 0x98, 0xA0),
            core: Color.FromRgb(0x0C, 0x08, 0x0A),
            rimC: Color.FromRgb(0xE0, 0x38, 0x48),
            glow: Color.FromArgb(0x55, 0x60, 0x08, 0x10),
            art: "darknet",
            band: 0.62,
            auraScale: 1.06,
            auraOpacity: 0.85,
            coreSize: 104,
            ir: 0xFF, ig: 0x5A, ib: 0x68,
            pr: 0x18, pg: 0x08, pb: 0x0C),
        Build(
            "Терминал",
            "Тёмный контур и зелёные метки, как экран в темноте. Иконки кислотные.",
            soft: Soft(0x77, 0x04, 0x14, 0x08, 0x30, 0x08, 0x30, 0x14),
            disc: Disc(0xF2, 0x06, 0x0C, 0x08, 0xE0, 0x04, 0x0A, 0x06, 0x00, 0x04, 0x0A, 0x06),
            orbit: GradientStroke(0xD0, 0x39, 0xFF, 0x78, 0x40, 0x10, 0x60, 0x28, 0xB0, 0x57, 0xFF, 0x8A),
            orbitW: 1.6,
            rim: Solid(0xDD, 0x39, 0xFF, 0x78),
            hintBg: Solid(0xF2, 0x06, 0x0C, 0x08),
            hintBorder: Solid(0x66, 0x20, 0xC0, 0x50),
            hintTitle: Solid(0xFF, 0xC8, 0xFF, 0xD8),
            hintBody: Solid(0xB0, 0xC8, 0xD0, 0xB8),
            core: Color.FromRgb(0x06, 0x0E, 0x0A),
            rimC: Color.FromRgb(0x39, 0xFF, 0x78),
            glow: Color.FromArgb(0x55, 0x08, 0x40, 0x18),
            art: "hack",
            band: 0.62,
            auraScale: 1.06,
            auraOpacity: 0.85,
            coreSize: 104,
            ir: 0x5C, ig: 0xFF, ib: 0x8A,
            pr: 0x06, pg: 0x16, pb: 0x0C)
    ];

    public static RingLookPalette Get(int look) => All[Math.Clamp(look, 0, Count - 1)];

    public static string? LayerPath(RingLookPalette look, string layer)
    {
        if (string.IsNullOrWhiteSpace(look.ArtFolder))
            return null;

        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Looks", look.ArtFolder, layer + ".png");
        return File.Exists(path) ? path : null;
    }

    public static bool TryLayout(RingLookPalette look, double orbit, out RingArtFrame frame)
    {
        frame = default;
        if (LayerPath(look, "ring") == null || LayerPath(look, "aura") == null || LayerPath(look, "core") == null)
            return false;

        double band = Math.Clamp(look.Band, 0.4, 0.85);
        double ringR = orbit / band;
        double auraR = ringR * Math.Clamp(look.AuraScale, 0.7, 1.8);
        frame = new RingArtFrame(
            ringR,
            auraR,
            look.CoreSize,
            look.AuraOpacity,
            Math.Max(ringR, auraR) + 16);
        return true;
    }

    public static IReadOnlyList<(int Id, string Title, string Hint)> Catalog()
    {
        var list = new (int, string, string)[Count];
        for (int i = 0; i < Count; i++)
            list[i] = (i, All[i].Title, All[i].Hint);
        return list;
    }

    private static RingLookPalette Build(
        string title,
        string hint,
        Brush soft,
        Brush disc,
        Brush orbit,
        double orbitW,
        Brush rim,
        Brush hintBg,
        Brush hintBorder,
        Brush hintTitle,
        Brush hintBody,
        Color core,
        Color rimC,
        Color glow,
        string? art = null,
        double band = 0.62,
        double auraScale = 1.15,
        double auraOpacity = 0.92,
        double coreSize = 140,
        byte ir = 0xF5, byte ig = 0xF0, byte ib = 0xE8,
        byte pr = 0x14, byte pg = 0x14, byte pb = 0x18) => new()
    {
        Title = title,
        Hint = hint,
        SoftGlow = soft,
        MatteDisc = disc,
        OrbitStroke = orbit,
        OrbitThickness = orbitW,
        SlotRim = rim,
        HintBg = hintBg,
        HintBorder = hintBorder,
        HintTitle = hintTitle,
        HintBody = hintBody,
        PreviewCore = core,
        PreviewRim = rimC,
        PreviewGlow = glow,
        ArtFolder = art,
        Band = band,
        AuraScale = auraScale,
        AuraOpacity = auraOpacity,
        CoreSize = coreSize,
        IconColor = Color.FromRgb(ir, ig, ib),
        PlateColor = Color.FromArgb(0xF0, pr, pg, pb)
    };

    private static Brush Soft(byte a0, byte r0, byte g0, byte b0, byte a1, byte r1, byte g1, byte b1)
    {
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new System.Windows.Point(0.5, 0.5),
            Center = new System.Windows.Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(a0, r0, g0, b0), 0.55));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(a1, r1, g1, b1), 0.8));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, r1, g1, b1), 1));
        brush.Freeze();
        return brush;
    }

    private static Brush Disc(
        byte a0, byte r0, byte g0, byte b0,
        byte a1, byte r1, byte g1, byte b1,
        byte a2, byte r2, byte g2, byte b2)
    {
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new System.Windows.Point(0.5, 0.5),
            Center = new System.Windows.Point(0.5, 0.5),
            RadiusX = 0.72,
            RadiusY = 0.72
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(a0, r0, g0, b0), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(a1, r1, g1, b1), 0.5));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(a2, r2, g2, b2), 0.85));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, r2, g2, b2), 1));
        brush.Freeze();
        return brush;
    }

    private static Brush GradientStroke(
        byte a0, byte r0, byte g0, byte b0,
        byte a1, byte r1, byte g1, byte b1,
        byte a2, byte r2, byte g2, byte b2)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1)
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(a0, r0, g0, b0), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(a1, r1, g1, b1), 0.5));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(a2, r2, g2, b2), 1));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush Solid(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Как разложить три рисунка вида относительно орбиты кнопок.</summary>
public readonly struct RingArtFrame
{
    public RingArtFrame(double ringRadius, double auraRadius, double coreSize, double auraOpacity, double clear)
    {
        RingRadius = ringRadius;
        AuraRadius = auraRadius;
        CoreSize = coreSize;
        AuraOpacity = auraOpacity;
        Clear = clear;
    }

    public double RingRadius { get; }
    public double AuraRadius { get; }
    public double CoreSize { get; }
    public double AuraOpacity { get; }
    public double Clear { get; }
}
