namespace Zwmenu.Settings;

public sealed record GlyphOption(string Glyph, string Name);

public sealed record TintOption(string Hex, string Name);

public static class Catalog
{
    public static IReadOnlyList<GlyphOption> Glyphs { get; } =
    [
        new("\uE8B7", "Папка"),
        new("\uE8B8", "Открытая папка"),
        new("\uE8A5", "Документ"),
        new("\uE8F1", "Диск"),
        new("\uE8F4", "Этот компьютер"),
        new("\uE7F4", "Экран"),
        new("\uE7FC", "Игра"),
        new("\uE768", "Ярлык"),
        new("\uE7B8", "Приложение"),
        new("\uE945", "Код"),
        new("\uE943", "Консоль"),
        new("\uE70B", "Заметка"),
        new("\uE805", "Тренировки"),
        new("\uE8D6", "Музыка"),
        new("\uE714", "Видео"),
        new("\uE91B", "Изображение"),
        new("\uE722", "Камера"),
        new("\uE896", "Загрузки"),
        new("\uE898", "Отправка"),
        new("\uE774", "Фото"),
        new("\uE787", "Почта"),
        new("\uE7BE", "Календарь"),
        new("\uE909", "Сеть"),
        new("\uE756", "Избранное"),
        new("\uE734", "Звезда"),
        new("\uE80F", "Настройки"),
        new("\uE721", "Поиск"),
        new("\uE8BD", "Библиотека"),
        new("\uE711", "Закрыть"),
        new("\uE72E", "Защита"),
        new("\uE7BA", "Клавиатура"),
        new("\uE8A7", "Справка"),
    ];

    public static IReadOnlyList<TintOption> Tints { get; } =
    [
        new("#FF3A3A3E", "Графит"),
        new("#FF3E4E62", "Сланец"),
        new("#FF3E5A6E", "Сталь"),
        new("#FF3E4658", "Ночь"),
        new("#FF3E5E4A", "Мох"),
        new("#FF3E5A52", "Хвоя"),
        new("#FF5A5240", "Песок"),
        new("#FF6A5A3E", "Охра"),
        new("#FF4A3E3A", "Глина"),
        new("#FF5A3E4E", "Вино"),
        new("#FF5E3E5A", "Слива"),
        new("#FF4E3E4A", "Чернила"),
    ];
}
