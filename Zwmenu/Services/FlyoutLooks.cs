namespace Zwmenu.Services;

/// <summary>Форма выезда вокруг круга.</summary>
public sealed class FlyoutStyle
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Hint { get; init; }
    public required string MotionId { get; init; }
}

/// <summary>Появление формы (идёт вместе с формой, отдельно в UI не крутим).</summary>
public sealed class FlyoutMotion
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Hint { get; init; }
    public int Ms { get; init; }
}

/// <summary>Каталог форм выезда. Одна выбранная форма для всех групп колеса.</summary>
public static class FlyoutLooks
{
    public const string DefaultStyleId = "fan";
    public const string DefaultMotionId = "cascade";

    static readonly FlyoutStyle[] Styles =
    [
        new()
        {
            Id = "fan",
            Title = "Веер",
            Hint = "Ровная колонка у кнопки. Имена целиком, без наезда.",
            MotionId = "cascade"
        },
        new()
        {
            Id = "halo",
            Title = "Ореол",
            Hint = "Круглые значки с равным зазором. Имя видно при наведении.",
            MotionId = "orbit"
        },
        new()
        {
            Id = "ribbon",
            Title = "Лента",
            Hint = "Колонка с мягким выгибом. Подписи не встают на круг.",
            MotionId = "unfurl"
        },
        new()
        {
            Id = "crown",
            Title = "Корона",
            Hint = "Короткая дуга значков у кнопки. Имя при наведении.",
            MotionId = "orbit"
        },
        new()
        {
            Id = "burst",
            Title = "Всплеск",
            Hint = "Колонка и тонкий луч от кнопки к каждой строке.",
            MotionId = "cascade"
        },
        new()
        {
            Id = "dock",
            Title = "Стык",
            Hint = "Обычная панель у кромки. Для пульта и длинных списков.",
            MotionId = "dockslide"
        }
    ];

    static readonly FlyoutMotion[] Motions =
    [
        new() { Id = "orbit", Title = "Орбита", Hint = "Лёгкий рост из диска.", Ms = 160 },
        new() { Id = "unfurl", Title = "Разворот", Hint = "Проявление ленты.", Ms = 150 },
        new() { Id = "cascade", Title = "Каскад", Hint = "Появление по очереди.", Ms = 180 },
        new() { Id = "dockslide", Title = "Сдвиг", Hint = "Короткий выезд.", Ms = 120 }
    ];

    public static IReadOnlyList<FlyoutStyle> StyleCatalog => Styles;
    public static IReadOnlyList<FlyoutMotion> MotionCatalog => Motions;

    public static FlyoutStyle GetStyle(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return Styles[0];
        foreach (var s in Styles)
        {
            if (string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))
                return s;
        }

        // Старые id.
        return id switch
        {
            "arc" => GetStyle("ribbon"),
            "plate" or "forge" or "glass" or "blade" => Styles[0],
            _ => Styles[0]
        };
    }

    public static FlyoutMotion GetMotion(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return Motions[0];
        foreach (var m in Motions)
        {
            if (string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
                return m;
        }

        return Motions[0];
    }

    public static string NormalizeStyle(string? id) => GetStyle(id).Id;
    public static string NormalizeMotion(string? id) => GetMotion(id).Id;

    /// <summary>
    /// Форма пользователя для всех групп. Только пульт (deskUi) остаётся стыком:
    /// там ползунки, ореол/веер не подходят.
    /// </summary>
    public static string EffectiveForm(string? requested, int itemCount, bool deskUi)
    {
        string id = NormalizeStyle(requested);
        if (deskUi)
            return "dock";
        return id;
    }
}
