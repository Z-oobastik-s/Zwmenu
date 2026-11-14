using System.IO;
using System.Text.Json;

namespace Zwmenu.Services;

/// <summary>Сборка minecraft:give для Java 1.21 и каталоги предметов/зачарований.</summary>
public static class GiveCommand
{
    public static readonly (string Id, string Ru)[] AttrSlots =
    {
        ("mainhand", "главная рука"),
        ("offhand", "вторая рука"),
        ("head", "голова"),
        ("chest", "грудь"),
        ("legs", "ноги"),
        ("feet", "ступни"),
        ("body", "тело"),
    };

    public static readonly (string Id, string Ru)[] AttrOps =
    {
        ("add_value", "к базовому значению"),
        ("add_multiplied_base", "× база"),
        ("add_multiplied_total", "× итог"),
    };

    public static readonly (string Id, string Ru)[] AttrTypes =
    {
        ("minecraft:generic.armor", "Броня"),
        ("minecraft:generic.armor_toughness", "Твёрдость брони"),
        ("minecraft:generic.attack_damage", "Урон атаки"),
        ("minecraft:generic.attack_knockback", "Отбрасывание атаки"),
        ("minecraft:generic.attack_speed", "Скорость атаки"),
        ("minecraft:player.block_break_speed", "Скорость ломания"),
        ("minecraft:player.block_interaction_range", "Дистанция до блока"),
        ("minecraft:generic.burning_time", "Время горения"),
        ("minecraft:player.entity_interaction_range", "Дистанция до сущности"),
        ("minecraft:generic.explosion_knockback_resistance", "Сопротивление взрыву"),
        ("minecraft:generic.fall_damage_multiplier", "Множитель падения"),
        ("minecraft:generic.flying_speed", "Скорость полёта"),
        ("minecraft:generic.gravity", "Гравитация"),
        ("minecraft:horse.jump_strength", "Прыжок лошади"),
        ("minecraft:generic.knockback_resistance", "Сопротивление отбрасыванию"),
        ("minecraft:generic.luck", "Удача"),
        ("minecraft:generic.max_absorption", "Макс. поглощение"),
        ("minecraft:generic.max_health", "Макс. здоровье"),
        ("minecraft:player.mining_efficiency", "Эффективность добычи"),
        ("minecraft:generic.follow_range", "Радиус преследования"),
        ("minecraft:generic.movement_efficiency", "Эффективность ходьбы"),
        ("minecraft:generic.oxygen_bonus", "Бонус кислорода"),
        ("minecraft:generic.safe_fall_distance", "Безопасное падение"),
        ("minecraft:generic.scale", "Масштаб"),
        ("minecraft:player.sneaking_speed", "Скорость крадучись"),
        ("minecraft:generic.movement_speed", "Скорость"),
        ("minecraft:generic.step_height", "Высота шага"),
        ("minecraft:player.submerged_mining_speed", "Добыча под водой"),
        ("minecraft:player.sweeping_damage_ratio", "Размах"),
        ("minecraft:generic.water_movement_efficiency", "Движение в воде"),
        ("minecraft:zombie.spawn_reinforcements", "Подкрепление зомби"),
    };

    private static List<ItemRow>? _items;
    private static List<EnchRow>? _ench;
    private static Dictionary<string, string>? _itemRu;
    private static Dictionary<string, string>? _enchRu;

    public sealed record ItemRow(string Id, string Line, string Blob);
    public sealed record EnchRow(string Id, string Line, int MaxLevel);
    public sealed record AttrMod(string Type, string Amount, string Slot, string Operation, string Id);

    public static IReadOnlyList<ItemRow> Items()
    {
        Ensure();
        return _items!;
    }

    public static IReadOnlyList<EnchRow> Enchantments()
    {
        Ensure();
        return _ench!;
    }

    public static string ItemRu(string id)
    {
        Ensure();
        return _itemRu!.TryGetValue(id, out var ru) ? ru : "";
    }

    public static string Build(
        string target,
        string itemId,
        int count,
        IEnumerable<(string Id, int Level)> enchantments,
        IEnumerable<AttrMod> attributes,
        string customName,
        IEnumerable<string> loreLines)
    {
        target = string.IsNullOrWhiteSpace(target) ? "@p" : target.Trim();
        itemId = (itemId ?? "").Trim().Replace(" ", "", StringComparison.Ordinal);
        if (itemId.Length == 0)
            throw new InvalidOperationException("Укажи ID предмета.");
        if (!itemId.Contains(':', StringComparison.Ordinal))
            itemId = "minecraft:" + itemId;
        count = Math.Clamp(count, 1, 64);

        var comp = new List<string>();
        var ench = enchantments.Where(e => !string.IsNullOrWhiteSpace(e.Id)).ToList();
        if (ench.Count > 0)
            comp.Add("enchantments={" + string.Join(",", ench.Select(e => e.Id + ":" + e.Level)) + "}");

        var attrs = attributes.ToList();
        if (attrs.Count > 0)
        {
            var parts = attrs.Select(m =>
                "{type:" + m.Type + ",amount:" + FormatAmount(m.Amount) + ",slot:" + m.Slot
                + ",operation:" + m.Operation + ",id:\"" + m.Id.Replace("\"", "", StringComparison.Ordinal) + "\"}");
            comp.Add("attribute_modifiers=[" + string.Join(",", parts) + "]");
        }

        string name = (customName ?? "").Trim();
        if (name.Length > 0)
            comp.Add("custom_name=" + QuoteText(name));

        var lore = loreLines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lore.Count > 0)
            comp.Add("lore=[" + string.Join(",", lore.Select(QuoteText)) + "]");

        string stack = comp.Count == 0 ? itemId : itemId + "[" + string.Join(",", comp) + "]";
        return "minecraft:give " + target + " " + stack + " " + count;
    }

    private static string FormatAmount(string raw)
    {
        if (!double.TryParse(raw.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double a))
            return "0";
        if (Math.Abs(a - Math.Round(a)) < 1e-9)
            return ((int)Math.Round(a)).ToString();
        return a.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string QuoteText(string text)
    {
        string inner = JsonSerializer.Serialize(new { text });
        return "'" + inner.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
    }

    private static void Ensure()
    {
        if (_items != null)
            return;
        string dir = Path.Combine(AppContext.BaseDirectory, "Data", "Give");
        _itemRu = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _enchRu = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string langPath = Path.Combine(dir, "lang_ru.json");
            if (File.Exists(langPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(langPath));
                if (doc.RootElement.TryGetProperty("items", out var items))
                {
                    foreach (var p in items.EnumerateObject())
                        _itemRu[p.Name] = p.Value.GetString() ?? "";
                }

                if (doc.RootElement.TryGetProperty("enchantments", out var ench))
                {
                    foreach (var p in ench.EnumerateObject())
                        _enchRu[p.Name] = p.Value.GetString() ?? "";
                }
            }
        }
        catch
        {
            // Каталог всё равно соберём по английским именам.
        }

        _items = new List<ItemRow>();
        try
        {
            string path = Path.Combine(dir, "items.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    string? name = el.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name) || name == "air")
                        continue;
                    string ru = _itemRu.TryGetValue(name, out var r) ? r.Trim() : "";
                    string line = ru.Length > 0 ? name + " — " + ru : name;
                    _items.Add(new ItemRow(name, line, (name + " " + ru).ToLowerInvariant()));
                }
            }
        }
        catch
        {
            // Пустой список: можно ввести ID вручную.
        }

        _items.Sort((a, b) => string.Compare(a.Line, b.Line, StringComparison.OrdinalIgnoreCase));

        _ench = new List<EnchRow>();
        try
        {
            string path = Path.Combine(dir, "enchantments.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    string? name = el.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name))
                        continue;
                    int max = el.TryGetProperty("maxLevel", out var m) && m.TryGetInt32(out int lvl) ? Math.Max(1, lvl) : 1;
                    string ru = _enchRu.TryGetValue(name, out var r) ? r.Trim() : "";
                    string line = ru.Length > 0 ? name + " — " + ru : name;
                    _ench.Add(new EnchRow(name, line, max));
                }
            }
        }
        catch
        {
            // Зачарования можно не трогать.
        }

        _ench.Sort((a, b) => string.Compare(a.Line, b.Line, StringComparison.OrdinalIgnoreCase));
    }
}
