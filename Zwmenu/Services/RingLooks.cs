using System.Windows.Media;

namespace Zwmenu.Services;

/// <summary>Пять дорогих видов колеса: диск, свечение, орбита, ободок слотов, карточка подсказки.</summary>
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
}

public static class RingLooks
{
    public const int Count = 5;

    private static readonly RingLookPalette[] All =
    [
        Build(
            "Классика",
            "Матовый графит, как сейчас. Чистый и спокойный.",
            soft: Soft(0x99, 0x00, 0x00, 0x00, 0x44, 0x00, 0x00, 0x00),
            disc: Disc(0xF0, 0x28, 0x28, 0x2C, 0xE8, 0x22, 0x22, 0x26, 0xD6, 0x1A, 0x1A, 0x1E),
            orbit: GradientStroke(0x55, 0xF5, 0xF0, 0xE8, 0x18, 0xF5, 0xF0, 0xE8, 0x40, 0xF5, 0xF0, 0xE8),
            orbitW: 1.5,
            rim: Solid(0x40, 0xF5, 0xF0, 0xE8),
            hintBg: Solid(0xF0, 0x16, 0x16, 0x18),
            hintBorder: Solid(0x33, 0xF5, 0xF0, 0xE8),
            hintTitle: Solid(0xFF, 0xF5, 0xF0, 0xE8),
            hintBody: Solid(0xB8, 0xF5, 0xF0, 0xE8),
            core: Color.FromRgb(0x22, 0x22, 0x26),
            rimC: Color.FromRgb(0xC8, 0xC2, 0xB8),
            glow: Color.FromArgb(0x66, 0x00, 0x00, 0x00)),
        Build(
            "Обсидиан",
            "Глубокое чёрное стекло и тонкий серебряный обод. Дорого и строго.",
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
            glow: Color.FromArgb(0x55, 0x20, 0x28, 0x34)),
        Build(
            "Шампань",
            "Тёплый металл и золотая орбита. Как корпус дорогих часов.",
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
            glow: Color.FromArgb(0x55, 0x6A, 0x48, 0x18)),
        Build(
            "Лёд",
            "Холодное стекло и платиновая кромка. Светлый акцент на тёмном.",
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
            glow: Color.FromArgb(0x50, 0x40, 0x70, 0x88)),
        Build(
            "Карбон",
            "Тёмный карбон и тонкая алая линия. Спортивный люкс.",
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
            glow: Color.FromArgb(0x55, 0x50, 0x10, 0x10))
    ];

    public static RingLookPalette Get(int look) => All[Math.Clamp(look, 0, Count - 1)];

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
        Color glow) => new()
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
        PreviewGlow = glow
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
