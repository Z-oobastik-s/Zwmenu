using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zwmenu.Models;

namespace Zwmenu.Services;

public sealed record MenuLoadResult(IReadOnlyList<MenuAction> Items, string? Warning);

/// <summary>
/// Настройки колеса: %LOCALAPPDATA%\Zwmenu\menu.json.
/// Запись атомарная, предыдущий файл остаётся как menu.json.bak.
/// </summary>
public static class MenuStore
{
    public const int MaxItems = 17;

    /// <summary>Подпись сектора иконок записана. Колесо нужно пересобрать.</summary>
    public static event Action? Changed;
    public const int MaxChildren = 24;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Без Populate get-only ObservableCollection Children остаётся пустой при Load: 
        // в файле дети есть, на колесе после сохранения их как будто нет.
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zwmenu");

    public static string FilePath => Path.Combine(DirectoryPath, "menu.json");

    /// <param name="prepareIcons">false: только JSON, иконки потом в фоне (старт UI не ждёт Shell).</param>
    public static MenuLoadResult Load(bool prepareIcons = true)
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                var created = MenuAction.CreateDefaults();
                Save(created, ensureIcons: prepareIcons);
                EnsureClipboardTranslate(created);
                EnsureEdgeShare(created);
                EnsureSnippets(created);
                EnsureVoiceRec(created);
                EnsureDesktopIcons(created);
                EnsureShelf(created);
                return new MenuLoadResult(created, null);
            }

            string json = File.ReadAllText(FilePath);
            var file = JsonSerializer.Deserialize<MenuFile>(json, Options);
            var (items, warning) = Sanitize(file?.Items);
            if (items.Count == 0)
            {
                return new MenuLoadResult(
                    MenuAction.CreateDefaults(),
                    "Файл настроек пустой: на колесе стандартные пункты. Сам файл не перезаписан.");
            }

            if (prepareIcons)
                PrepareIcons(items);
            return new MenuLoadResult(items, warning);
        }
        catch (Exception ex)
        {
            return new MenuLoadResult(
                MenuAction.CreateDefaults(),
                $"Не удалось прочитать настройки ({ex.Message}). На колесе стандартные пункты, файл не тронут.");
        }
    }

    /// <summary>Достать иконки и записать menu.json, только если пути изменились. Можно с фона.</summary>
    public static void PrepareIcons(IReadOnlyList<MenuAction> items)
    {
        var before = CaptureIconPaths(items);
        AppIconService.EnsureIcons(items);
        if (!IconPathsEqual(before, items))
        {
            try { Save(items, ensureIcons: false); }
            catch { /* диск занят: в памяти иконки уже есть */ }
        }
    }

    public static void Save(IReadOnlyList<MenuAction> items, bool ensureIcons = true)
    {
        if (ensureIcons)
            AppIconService.EnsureIcons(items);

        var file = new MenuFile
        {
            Version = 1,
            Items = items.Take(MaxItems).Select(item => CloneClean(item, allowChildren: true)).ToList()
        };

        Directory.CreateDirectory(DirectoryPath);
        string json = JsonSerializer.Serialize(file, Options);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);

        if (File.Exists(FilePath))
            File.Copy(FilePath, FilePath + ".bak", overwrite: true);

        File.Move(tmp, FilePath, overwrite: true);
    }

    private static List<(string Self, List<string> Kids)> CaptureIconPaths(IReadOnlyList<MenuAction> items)
    {
        var list = new List<(string, List<string>)>(items.Count);
        foreach (var item in items)
        {
            var kids = new List<string>(item.Children.Count);
            foreach (var child in item.Children)
                kids.Add(child.IconPath ?? "");
            list.Add((item.IconPath ?? "", kids));
        }
        return list;
    }

    private static bool IconPathsEqual(
        IReadOnlyList<(string Self, List<string> Kids)> before,
        IReadOnlyList<MenuAction> items)
    {
        if (before.Count != items.Count)
            return false;
        for (int i = 0; i < items.Count; i++)
        {
            if (!string.Equals(before[i].Self, items[i].IconPath ?? "", StringComparison.OrdinalIgnoreCase))
                return false;
            if (before[i].Kids.Count != items[i].Children.Count)
                return false;
            for (int c = 0; c < items[i].Children.Count; c++)
            {
                if (!string.Equals(before[i].Kids[c], items[i].Children[c].IconPath ?? "", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }
        return true;
    }

    public static void RevealFile()
    {
        Directory.CreateDirectory(DirectoryPath);
        if (!File.Exists(FilePath))
            Save(MenuAction.CreateDefaults());

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{FilePath}\"",
            UseShellExecute = true
        })?.Dispose();
    }

    private static (List<MenuAction> Items, string? Warning) Sanitize(List<MenuAction>? raw)
    {
        var list = new List<MenuAction>();
        if (raw == null)
            return (list, null);

        foreach (var src in raw)
        {
            if (src == null)
                continue;
            list.Add(CloneClean(src, allowChildren: true));
            if (list.Count == MaxItems)
                break;
        }

        Renumber(list);
        MigratePicturesToFoldersGroup(list);
        MigrateLegacyProfileFolders(list);
        EnsureClipboardTranslate(list);
        EnsureEdgeShare(list);
        EnsureSnippets(list);
        EnsureVoiceRec(list);
        EnsureDesktopIcons(list);
        EnsureShelf(list);
        EnsureWorkshop(list);
        StripMistakenQuickLaunch(list);

        string? warning = raw.Count > MaxItems
            ? $"В файле было {raw.Count} пунктов: на колесе оставлены первые {MaxItems} (центр и {MaxItems - 1} по кругу)."
           : null;

        return (list, warning);
    }

    /// <summary>
    /// Старые «Загрузки / Документы / Музыка / Видео» на кольце → калькулятор, диспетчер, параметры, терминал.
    /// Папки по-прежнему кладутся в группу «Папки».
    /// </summary>
    private static void MigrateLegacyProfileFolders(List<MenuAction> list)
    {
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string downloads = Path.Combine(user, "Downloads");
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

        var replacements = new (string Label, Func<string, bool> Match, MenuAction NewItem)[]
        {
            ("Загрузки", p => EndsWithFolder(p, "Downloads", "Загрузки") || SamePath(p, downloads),
                ItemLaunch("Тренировки", "Домашний план: отжимания, пресс, ноги. Без железа.", "zwmenu:workout", "\uE805", "#FF3E5A6E")),
            ("Документы", p => EndsWithFolder(p, "Documents", "Документы") || SamePath(p, docs),
                ItemLaunch("Диспетчер задач", "Открывает диспетчер задач.", "taskmgr.exe", "\uE7F4", "#FF6A5A3E")),
            ("Музыка", p => EndsWithFolder(p, "Music", "Музыка") || SamePath(p, music),
                ItemLaunchCmd()),
            ("Видео", p => EndsWithFolder(p, "Videos", "Видео") || SamePath(p, videos),
                ItemLaunchControlPanel()),
        };

        foreach (var item in list)
        {
            if (item.Kind != ActionKind.Launch)
                continue;
            foreach (var (label, match, neu) in replacements)
            {
                if (!string.Equals(item.Label, label, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!match(item.Value ?? ""))
                    continue;
                ApplyLaunchDefaults(item, neu);
                break;
            }
        }

        // «Параметры / Терминал / Диспетчер» и уже созданные пункты → актуальные варианты.
        foreach (var item in list)
        {
            if (item.Kind == ActionKind.Launch
                && (string.Equals(item.Label, "Параметры", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.Label, "Панель управления", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.Value, "ms-settings:", StringComparison.OrdinalIgnoreCase)
                    || LooksLikeLegacySettings(item)))
            {
                ApplyLaunchDefaults(item, ItemLaunchControlPanel());
            }

            if (item.Kind == ActionKind.Launch
                && (string.Equals(item.Label, "Терминал", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.Label, "Командная строка", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.Value, "wt.exe", StringComparison.OrdinalIgnoreCase)
                    || IsCmdPath(item.Value)))
            {
                ApplyLaunchDefaults(item, ItemLaunchCmd());
            }

            if (item.Kind == ActionKind.Launch
                && (string.Equals(item.Label, "Калькулятор", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.Value, "calc.exe", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.Value, "calc", StringComparison.OrdinalIgnoreCase)))
            {
                ApplyLaunchDefaults(item, ItemLaunch(
                    "Тренировки",
                    "Домашний план: отжимания, пресс, ноги. Без железа.",
                    "zwmenu:workout",
                    "\uE805",
                    "#FF3E5A6E"));
            }

            if ((item.Kind == ActionKind.Launch || item.Kind == ActionKind.Group)
                && (string.Equals(item.Label, "Диспетчер задач", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.Label, "Процессы", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.Value, "taskmgr.exe", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.GroupLayout, "processes", StringComparison.OrdinalIgnoreCase)))
            {
                item.Kind = ActionKind.Group;
                item.Label = "Процессы";
                item.Description = "Наведи: список процессов. Кликни: открыть диспетчер задач.";
                item.Value = "";
                item.Arguments = "";
                item.Glyph = "\uE7F4";
                item.GroupLayout = "processes";
                item.IconPath = "";
                item.RunAsAdmin = false;
                item.Children.Clear();
            }
        }
    }

    private static MenuAction ItemLaunchControlPanel()
    {
        var a = ItemLaunch(
            "Панель управления",
            "Классическая панель управления: все элементы крупными значками.",
            "explorer.exe",
            "\uE713",
            "#FF5A3E4E");
        a.Arguments = @"shell:::{21EC2020-3AEA-1069-A2DD-08002B30309D}";
        return a;
    }

    private static MenuAction ItemLaunchCmd()
    {
        var a = ItemLaunch(
            "Командная строка",
            "Командная строка в папке, которая сейчас открыта. От имени администратора.",
            "cmd.exe",
            "\uE756",
            "#FF3E5A52");
        a.RunAsAdmin = true;
        return a;
    }

    private static void ApplyLaunchDefaults(MenuAction item, MenuAction neu)
    {
        item.Kind = ActionKind.Launch;
        item.Label = neu.Label;
        item.Description = neu.Description;
        item.Value = neu.Value;
        item.Glyph = neu.Glyph;
        item.Tint = neu.Tint;
        item.IconPath = "";
        item.Arguments = neu.Arguments ?? "";
        item.RunAsAdmin = neu.RunAsAdmin;
        item.GroupLayout = "apps";
        item.Children.Clear();
    }

    private static bool LooksLikeLegacySettings(MenuAction item)
    {
        string v = (item.Value ?? "").Trim();
        string args = (item.Arguments ?? "").Trim();
        if (v.StartsWith("ms-settings", StringComparison.OrdinalIgnoreCase))
            return true;
        // «Панель» без CLSID или со старым control.exe без All Control Panel Items.
        if (string.Equals(item.Label, "Панель управления", StringComparison.OrdinalIgnoreCase)
            && !args.Contains("21EC2020", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static bool IsCmdPath(string? value)
    {
        string v = (value ?? "").Trim();
        if (v.Length == 0) return false;
        return string.Equals(v, "cmd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(v, "cmd.exe", StringComparison.OrdinalIgnoreCase)
            || v.EndsWith("\\cmd.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Один раз добавляет сектор «Перевод», если на колесе есть место.
    /// Если пункт потом удалили, обратно его не возвращаем.
    /// </summary>
    private static void EnsureClipboardTranslate(List<MenuAction> list)
    {
        try
        {
            var settings = ScreenshotStore.Current;
            if (settings.TranslateWheelSeeded)
                return;

            if (!list.Any(HasClipboardTranslate) && list.Count < MaxItems)
            {
                list.Add(ItemLaunch(
                    "Перевод",
                    "Читает буфер. Можно скопировать перевод или оставить как есть. Обычное копирование буфер не меняет.",
                    MenuAction.ClipboardTranslateValue,
                    "\uE8C1",
                    "#FF3E4E62"));
                Renumber(list);
                Save(list, ensureIcons: false);
            }

            settings.TranslateWheelSeeded = true;
            ScreenshotStore.Save(settings);
        }
        catch
        {
            // Следующий запуск попробует снова. Чужое меню из-за этой ошибки не подменяем.
        }
    }

    private static void EnsureEdgeShare(List<MenuAction> list)
    {
        try
        {
            var settings = ScreenshotStore.Current;
            if (settings.EdgeWheelSeeded)
                return;

            if (!list.Any(HasEdgeShare) && list.Count < MaxItems)
            {
                list.Add(ItemLaunch(
                    "Экраны",
                    "Одна мышь на два ноутбука в одной сети. Курсор переходит через край экрана.",
                    MenuAction.EdgeShareValue,
                    "\uE7F3",
                    "#FF3E5A52"));
                Renumber(list);
                Save(list, ensureIcons: false);
            }

            settings.EdgeWheelSeeded = true;
            ScreenshotStore.Save(settings);
        }
        catch
        {
            // Следующий запуск попробует снова.
        }
    }

    private static void EnsureSnippets(List<MenuAction> list)
    {
        try
        {
            var settings = ScreenshotStore.Current;
            if (settings.SnippetWheelSeeded)
                return;

            if (!list.Any(HasSnippets) && list.Count < MaxItems)
            {
                list.Add(ItemLaunch(
                    "Шаблоны",
                    "Текстовые заготовки. Своя клавиша копирует или сразу вставляет.",
                    MenuAction.SnippetsValue,
                    "\uE70F",
                    "#FF4E4668"));
                Renumber(list);
                Save(list, ensureIcons: false);
            }

            settings.SnippetWheelSeeded = true;
            ScreenshotStore.Save(settings);
        }
        catch
        {
            // Следующий запуск попробует снова.
        }
    }

    private static bool HasSnippets(MenuAction item)
    {
        if (string.Equals((item.Value ?? "").Trim(), MenuAction.SnippetsValue, StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var child in item.Children)
        {
            if (HasSnippets(child))
                return true;
        }

        return false;
    }

    private static void EnsureVoiceRec(List<MenuAction> list)
    {
        try
        {
            var settings = ScreenshotStore.Current;
            if (settings.VoiceRecWheelSeeded)
                return;

            if (!list.Any(HasVoiceRec) && list.Count < MaxItems)
            {
                list.Add(ItemLaunch(
                    "Диктофон",
                    "Запись голоса. Можно сменить тон и отдать его в звонок или игру.",
                    MenuAction.VoiceRecValue,
                    "\uE720",
                    "#FF5A3E4E"));
                Renumber(list);
                Save(list, ensureIcons: false);
            }

            settings.VoiceRecWheelSeeded = true;
            ScreenshotStore.Save(settings);
        }
        catch
        {
            // Следующий запуск попробует снова.
        }
    }

    private static void EnsureDesktopIcons(List<MenuAction> list)
    {
        try
        {
            bool hidden = DesktopIcons.IconsHidden;
            if (FindDesktopIcons(list) is MenuAction existing)
            {
                if (CaptionDiffers(existing, hidden))
                {
                    DesktopIcons.ApplyCaption(existing, hidden);
                    Save(list, ensureIcons: false);
                }

                MarkDesktopIconsSeeded();
                return;
            }

            var settings = ScreenshotStore.Current;
            if (settings.DesktopIconsWheelSeeded || list.Count >= MaxItems)
                return;

            var item = ItemLaunch(
                "Скрыть иконки",
                "Убрать значки и оставить одни обои. Повторный клик вернёт.",
                MenuAction.DesktopIconsValue,
                "\uE73E",
                "#FF4A5560");
            DesktopIcons.ApplyCaption(item, hidden);
            list.Add(item);
            Renumber(list);
            Save(list, ensureIcons: false);
            MarkDesktopIconsSeeded();
        }
        catch
        {
            // Следующий запуск попробует снова.
        }
    }

    /// <summary>После клика по сектору: подпись показывает, что сделает следующий.</summary>
    public static void RememberDesktopIcons(bool hidden)
    {
        try
        {
            var result = Load(prepareIcons: false);
            bool changed = false;
            void Walk(System.Collections.Generic.IEnumerable<MenuAction> items)
            {
                foreach (var item in items)
                {
                    if (string.Equals((item.Value ?? "").Trim(), MenuAction.DesktopIconsValue, StringComparison.OrdinalIgnoreCase))
                    {
                        DesktopIcons.ApplyCaption(item, hidden);
                        changed = true;
                    }

                    Walk(item.Children);
                }
            }

            Walk(result.Items);
            if (!changed)
                return;
            Save(result.Items, ensureIcons: false);
            Changed?.Invoke();
        }
        catch
        {
            // Значки уже переключились. Подпись догонится при следующем запуске.
        }
    }

    private static bool CaptionDiffers(MenuAction item, bool hidden)
    {
        string expect = hidden ? "Показать иконки" : "Скрыть иконки";
        return !string.Equals(item.Label, expect, StringComparison.Ordinal);
    }

    private static MenuAction? FindDesktopIcons(List<MenuAction> list)
    {
        foreach (var item in list)
        {
            var found = FindDesktopIconsItem(item);
            if (found != null)
                return found;
        }

        return null;
    }

    private static MenuAction? FindDesktopIconsItem(MenuAction item)
    {
        if (string.Equals((item.Value ?? "").Trim(), MenuAction.DesktopIconsValue, StringComparison.OrdinalIgnoreCase))
            return item;
        foreach (var child in item.Children)
        {
            var found = FindDesktopIconsItem(child);
            if (found != null)
                return found;
        }

        return null;
    }

    private static void EnsureWorkshop(List<MenuAction> list)
    {
        try
        {
            if (FindWorkshop(list) != null)
            {
                MarkWorkshopSeeded();
                return;
            }

            var settings = ScreenshotStore.Current;
            if (settings.WorkshopWheelSeeded || list.Count >= MaxItems)
                return;

            list.Add(ItemLaunch(
                "Мастерская",
                "Имена пачкой, картинки, видео, сервер, микрофон, место и второй компьютер.",
                MenuAction.WorkshopValue,
                "\uE90F",
                "#FF4A5560"));
            Renumber(list);
            Save(list, ensureIcons: false);
            MarkWorkshopSeeded();
        }
        catch
        {
            // Следующий запуск попробует снова.
        }
    }

    private static MenuAction? FindWorkshop(List<MenuAction> list)
    {
        foreach (var item in list)
        {
            var found = FindWorkshopItem(item);
            if (found != null)
                return found;
        }

        return null;
    }

    private static MenuAction? FindWorkshopItem(MenuAction item)
    {
        if (string.Equals((item.Value ?? "").Trim(), MenuAction.WorkshopValue, StringComparison.OrdinalIgnoreCase))
            return item;
        foreach (var child in item.Children)
        {
            var found = FindWorkshopItem(child);
            if (found != null)
                return found;
        }

        return null;
    }

    private static void MarkWorkshopSeeded()
    {
        var settings = ScreenshotStore.Current;
        if (settings.WorkshopWheelSeeded)
            return;
        settings.WorkshopWheelSeeded = true;
        ScreenshotStore.Save(settings);
    }

    private static void EnsureShelf(List<MenuAction> list)
    {
        try
        {
            if (FindShelf(list) != null)
            {
                MarkShelfSeeded();
                return;
            }

            var settings = ScreenshotStore.Current;
            if (settings.ShelfWheelSeeded || list.Count >= MaxItems)
                return;

            list.Add(ItemLaunch(
                "Полка",
                "Стекло у края экрана. Перетащи файл с рабочего стола сюда, обои останутся чистыми. Обратно вытащи его на стол.",
                MenuAction.ShelfValue,
                "\uE7B8",
                "#FF5A4638"));
            Renumber(list);
            Save(list, ensureIcons: false);
            MarkShelfSeeded();
        }
        catch
        {
            // Следующий запуск попробует снова.
        }
    }

    private static MenuAction? FindShelf(List<MenuAction> list)
    {
        foreach (var item in list)
        {
            var found = FindShelfItem(item);
            if (found != null)
                return found;
        }

        return null;
    }

    private static MenuAction? FindShelfItem(MenuAction item)
    {
        if (string.Equals((item.Value ?? "").Trim(), MenuAction.ShelfValue, StringComparison.OrdinalIgnoreCase))
            return item;
        foreach (var child in item.Children)
        {
            var found = FindShelfItem(child);
            if (found != null)
                return found;
        }

        return null;
    }

    private static void MarkShelfSeeded()
    {
        var settings = ScreenshotStore.Current;
        if (settings.ShelfWheelSeeded)
            return;
        settings.ShelfWheelSeeded = true;
        ScreenshotStore.Save(settings);
    }

    private static void MarkDesktopIconsSeeded()
    {
        var settings = ScreenshotStore.Current;
        if (settings.DesktopIconsWheelSeeded)
            return;
        settings.DesktopIconsWheelSeeded = true;
        ScreenshotStore.Save(settings);
    }

    private static bool HasVoiceRec(MenuAction item)
    {
        if (string.Equals((item.Value ?? "").Trim(), MenuAction.VoiceRecValue, StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var child in item.Children)
        {
            if (HasVoiceRec(child))
                return true;
        }

        return false;
    }

    /// <summary>Убирает ошибочно добавленную группу «Запуск» с колеса (запуск — голосом).</summary>
    private static void StripMistakenQuickLaunch(List<MenuAction> list)
    {
        try
        {
            int n = list.RemoveAll(i =>
                i.Kind == ActionKind.Group
                && string.Equals(i.Label?.Trim(), "Запуск", StringComparison.OrdinalIgnoreCase));
            if (n == 0)
                return;
            Renumber(list);
            Save(list, ensureIcons: false);
        }
        catch
        {
            // ignore
        }
    }

    private static bool HasEdgeShare(MenuAction item)
    {
        if (string.Equals((item.Value ?? "").Trim(), MenuAction.EdgeShareValue, StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var child in item.Children)
        {
            if (HasEdgeShare(child))
                return true;
        }

        return false;
    }

    private static bool HasClipboardTranslate(MenuAction item)
    {
        if (string.Equals((item.Value ?? "").Trim(), MenuAction.ClipboardTranslateValue, StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var child in item.Children)
        {
            if (HasClipboardTranslate(child))
                return true;
        }

        return false;
    }

    private static MenuAction ItemLaunch(string label, string description, string value, string glyph, string tint) =>
        new()
        {
            Label = label,
            Description = description,
            Kind = ActionKind.Launch,
            Value = value,
            Glyph = glyph,
            Tint = tint
        };

    private static bool SamePath(string a, string b) =>
        string.Equals((a ?? "").TrimEnd('\\', '/'), (b ?? "").TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static bool EndsWithFolder(string path, string en, string ru) =>
        path.EndsWith("\\" + en, StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("/" + en, StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("\\" + ru, StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("/" + ru, StringComparison.OrdinalIgnoreCase);

    /// <summary>«Изображения» как одиночная кнопка → группа «Папки» с сеткой (туда добавляют остальные).</summary>
    private static void MigratePicturesToFoldersGroup(List<MenuAction> list)
    {
        string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        for (int i = 0; i < list.Count; i++)
        {
            var item = list[i];
            if (item.Kind != ActionKind.Launch)
                continue;
            if (!string.Equals(item.Label, "Изображения", StringComparison.OrdinalIgnoreCase))
                continue;

            string path = (item.Value ?? "").Trim();
            bool looksPictures = path.Length == 0
                || string.Equals(path, pictures, StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("\\Pictures", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("\\Изображения", StringComparison.OrdinalIgnoreCase);
            if (!looksPictures)
                continue;

            var child = new MenuAction
            {
                Label = "Изображения",
                Description = "Открывает папку «Изображения».",
                Kind = ActionKind.Launch,
                Value = string.IsNullOrWhiteSpace(path) ? pictures: path,
                Glyph = "\uE91B",
                Tint = item.Tint,
                IconPath = item.IconPath
            };

            item.Kind = ActionKind.Group;
            item.Label = "Папки";
            item.Description = "Наведи: сетка папок. Иконки как в проводнике, в том числе свои.";
            item.Value = "";
            item.Arguments = "";
            item.Glyph = "\uE8B7";
            item.GroupLayout = "folders";
            item.IconPath = "";
            item.Children.Clear();
            item.Children.Add(child);
            break;
        }

        // Старая группа игр без layout: явно apps, чтобы не перепуталась с папками.
        foreach (var item in list)
        {
            if (item.Kind != ActionKind.Group)
                continue;
            if (!string.IsNullOrWhiteSpace(item.GroupLayout))
                continue;
            if (item.IsFoldersGroup)
                item.GroupLayout = "folders";
            else
                item.GroupLayout = "apps";
        }
    }

    private static MenuAction CloneClean(MenuAction src, bool allowChildren)
    {
        string label = (src.Label ?? "").Trim();
        if (label.Length == 0)
            label = "Без названия";

        string glyph = string.IsNullOrWhiteSpace(src.Glyph) ? "\uE8B7": src.Glyph.Trim();
        string tint = IsTint(src.Tint) ? src.Tint.Trim(): "#FF3E4E62";
        var kind = src.Kind is ActionKind.None or ActionKind.Launch or ActionKind.Keys or ActionKind.Group
            ? src.Kind
           : ActionKind.Launch;

        var copy = new MenuAction
        {
            Label = ScrubDashes(label),
            Description = ScrubDashes((src.Description ?? "").Trim()),
            Kind = kind,
            Value = (src.Value ?? "").Trim(),
            Arguments = (src.Arguments ?? "").Trim(),
            Glyph = glyph,
            Tint = tint,
            IconPath = (src.IconPath ?? "").Trim(),
            GroupLayout = (src.GroupLayout ?? "").Trim(),
            RunAsAdmin = src.RunAsAdmin
        };

        if (allowChildren)
        {
            foreach (var child in src.Children)
            {
                if (copy.Children.Count >= MaxChildren)
                    break;
                copy.Children.Add(CloneClean(child, allowChildren: false));
            }
        }

        return copy;
    }

    public static void Renumber(IList<MenuAction> items)
    {
        for (int i = 0; i < items.Count; i++)
            items[i].Tag = i;
    }

    /// <summary>В текстах UI запрещены дефис и длинное тире: меняем на двоеточие.</summary>
    private static string ScrubDashes(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        return text
            .Replace('—', ':')
            .Replace('–', ':')
            .Replace(" - ", ": ")
            .Replace('-', ':');
    }

    private static bool IsTint(string? tint)
    {
        if (string.IsNullOrWhiteSpace(tint))
            return false;
        try
        {
            return System.Windows.Media.ColorConverter.ConvertFromString(tint) != null;
        }
        catch
        {
            return false;
        }
    }

    private sealed class MenuFile
    {
        public int Version { get; set; } = 1;
        public List<MenuAction> Items { get; set; } = new();
    }
}
