using System.Windows.Media;

namespace Zwmenu.Services;

/// <summary>Одна визуальная тема интерфейса: цвета, радиусы, шрифт. Не только перекраска.</summary>
public sealed class UiSkin
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Hint { get; init; }
    public required Color BgDeep { get; init; }
    public required Color BgPanel { get; init; }
    public required Color BgTitle { get; init; }
    public required Color Fg { get; init; }
    public required Color FgDim { get; init; }
    public required Color Border { get; init; }
    public required Color ListBg { get; init; }
    public required Color ListSel { get; init; }
    public required Color BtnBg { get; init; }
    public required Color BtnHover { get; init; }
    public required Color Accent { get; init; }
    public required Color AccentHover { get; init; }
    public required Color AccentSoft { get; init; }
    public required Color AccentText { get; init; }
    public required Color Danger { get; init; }
    public required Color DangerHover { get; init; }
    public required Color StatusOk { get; init; }
    public required Color StatusWarn { get; init; }
    public double CornerPanel { get; init; } = 14;
    public double CornerPill { get; init; } = 10;
    public double CornerChip { get; init; } = 8;
    public string FontUi { get; init; } = "Segoe UI Variable Text, Segoe UI";
}

/// <summary>
/// Живая тема окон и панелей. Колесо по прежнему берёт RingLooks.
/// Переключение без перезапуска: новые окна и перерисовка слушателей.
/// </summary>
public static class UiTheme
{
    public const string DefaultId = "signal-forge";

    static readonly UiSkin[] All =
    [
        new()
        {
            Id = "signal-forge",
            Title = "Signal Forge",
            Hint = "Ночной графит и мятный сигнал. Точный инструмент, без неона.",
            BgDeep = C(0x08, 0x09, 0x0B),
            BgPanel = C(0x11, 0x13, 0x17),
            BgTitle = C(0x0C, 0x0E, 0x12),
            Fg = C(0xEC, 0xEF, 0xF3),
            FgDim = C(0x8A, 0x92, 0x9C),
            Border = C(0x2A, 0x31, 0x3A),
            ListBg = C(0x0D, 0x0F, 0x13),
            ListSel = C(0x2A, 0x3A, 0x42),
            BtnBg = C(0x1C, 0x21, 0x28),
            BtnHover = C(0x28, 0x30, 0x3A),
            Accent = C(0x1E, 0x3A, 0x36),
            AccentHover = C(0x2A, 0x4C, 0x46),
            AccentSoft = C(0x14, 0x28, 0x26),
            AccentText = C(0x6E, 0xE0, 0xC4),
            Danger = C(0x3A, 0x1C, 0x20),
            DangerHover = C(0x52, 0x28, 0x2E),
            StatusOk = C(0x1A, 0x3A, 0x2C),
            StatusWarn = C(0x2A, 0x1C, 0x1E),
            CornerPanel = 14,
            CornerPill = 10
        },
        new()
        {
            Id = "cold-obsidian",
            Title = "Cold Obsidian",
            Hint = "Чёрное стекло и серебряная кромка. Холодно и строго.",
            BgDeep = C(0x05, 0x06, 0x08),
            BgPanel = C(0x0E, 0x10, 0x14),
            BgTitle = C(0x0A, 0x0B, 0x0E),
            Fg = C(0xEE, 0xF2, 0xF6),
            FgDim = C(0x8A, 0x94, 0xA0),
            Border = C(0x34, 0x3C, 0x48),
            ListBg = C(0x0A, 0x0C, 0x10),
            ListSel = C(0x2C, 0x36, 0x44),
            BtnBg = C(0x18, 0x1C, 0x24),
            BtnHover = C(0x26, 0x2C, 0x36),
            Accent = C(0x2A, 0x34, 0x42),
            AccentHover = C(0x3A, 0x48, 0x58),
            AccentSoft = C(0x16, 0x1C, 0x24),
            AccentText = C(0xC0, 0xD0, 0xE0),
            Danger = C(0x3A, 0x1A, 0x22),
            DangerHover = C(0x50, 0x26, 0x30),
            StatusOk = C(0x18, 0x32, 0x2A),
            StatusWarn = C(0x36, 0x2A, 0x16),
            CornerPanel = 12,
            CornerPill = 8
        },
        new()
        {
            Id = "ember-line",
            Title = "Ember Line",
            Hint = "Тёплый уголь и янтарная линия. Без кремовой «газетной» эстетики.",
            BgDeep = C(0x0C, 0x09, 0x08),
            BgPanel = C(0x16, 0x12, 0x10),
            BgTitle = C(0x10, 0x0C, 0x0A),
            Fg = C(0xF2, 0xEC, 0xE4),
            FgDim = C(0xA0, 0x92, 0x84),
            Border = C(0x3A, 0x30, 0x28),
            ListBg = C(0x12, 0x0E, 0x0C),
            ListSel = C(0x3A, 0x2E, 0x22),
            BtnBg = C(0x24, 0x1C, 0x16),
            BtnHover = C(0x34, 0x28, 0x1E),
            Accent = C(0x3A, 0x2A, 0x18),
            AccentHover = C(0x4C, 0x38, 0x20),
            AccentSoft = C(0x28, 0x1C, 0x12),
            AccentText = C(0xE8, 0xB8, 0x6A),
            Danger = C(0x42, 0x1A, 0x16),
            DangerHover = C(0x58, 0x26, 0x20),
            StatusOk = C(0x1E, 0x32, 0x22),
            StatusWarn = C(0x3A, 0x28, 0x12),
            CornerPanel = 16,
            CornerPill = 12
        },
        new()
        {
            Id = "frost-signal",
            Title = "Frost Signal",
            Hint = "Холодный сланец и ледяной акцент. Чистая схема для долгих сессий.",
            BgDeep = C(0x07, 0x0C, 0x12),
            BgPanel = C(0x0E, 0x16, 0x1E),
            BgTitle = C(0x0A, 0x12, 0x18),
            Fg = C(0xE6, 0xF0, 0xF6),
            FgDim = C(0x7E, 0x96, 0xA8),
            Border = C(0x28, 0x3C, 0x4C),
            ListBg = C(0x0A, 0x12, 0x18),
            ListSel = C(0x24, 0x3A, 0x4C),
            BtnBg = C(0x16, 0x22, 0x2E),
            BtnHover = C(0x22, 0x32, 0x42),
            Accent = C(0x1A, 0x32, 0x42),
            AccentHover = C(0x26, 0x44, 0x58),
            AccentSoft = C(0x12, 0x24, 0x30),
            AccentText = C(0x7A, 0xC8, 0xE8),
            Danger = C(0x3A, 0x1C, 0x28),
            DangerHover = C(0x50, 0x28, 0x34),
            StatusOk = C(0x14, 0x34, 0x30),
            StatusWarn = C(0x34, 0x2C, 0x14),
            CornerPanel = 14,
            CornerPill = 10
        }
    ];

    static UiSkin _current = All[0];

    public static event Action? Changed;

    public static UiSkin Current => _current;

    public static IReadOnlyList<UiSkin> Catalog => All;

    public static UiSkin Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return All[0];
        foreach (var skin in All)
        {
            if (string.Equals(skin.Id, id, StringComparison.OrdinalIgnoreCase))
                return skin;
        }

        return All[0];
    }

    public static void Apply(string? id, bool raise = true)
    {
        var next = Get(id);
        if (ReferenceEquals(_current, next) && string.Equals(_current.Id, next.Id, StringComparison.Ordinal))
            return;
        _current = next;
        if (raise)
            Changed?.Invoke();
    }

    /// <summary>Подбирает UI тему к виду кольца, если пользователь не зафиксировал отдельно.</summary>
    public static string SuggestForRingLook(int ringLook) => ringLook switch
    {
        1 => "cold-obsidian",
        2 => "ember-line",
        3 => "frost-signal",
        _ => DefaultId
    };

    static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}
