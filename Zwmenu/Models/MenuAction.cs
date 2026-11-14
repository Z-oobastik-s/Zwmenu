using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Zwmenu.Models;

public enum ActionKind
{
    None,
    Keys,
    Launch,
    /// <summary>Только подменю: при наведении/клике выезжают Children, сам пункт ничего не запускает.</summary>
    Group
}

/// <summary>
/// Пункт колеса. Tag 0: центр, дальше по часовой от верха.
/// Tag в файл не пишется: место задаёт порядок в списке.
/// </summary>
public sealed class MenuAction: INotifyPropertyChanged
{
    /// <summary>Сектор колеса: прочитать буфер и спросить, копировать перевод или оригинал.</summary>
    public const string ClipboardTranslateValue = "zwmenu:clipboard";

    /// <summary>Сектор колеса: одна мышь на два ноутбука в этой сети.</summary>
    public const string EdgeShareValue = "zwmenu:edges";

    /// <summary>Окно шаблонов, ссылок и команд сервера.</summary>
    public const string SnippetsValue = "zwmenu:snippets";

    /// <summary>Продвинутая запись голоса на рабочий стол.</summary>
    public const string VoiceRecValue = "zwmenu:voicerec";

    /// <summary>Скрыть или вернуть значки рабочего стола.</summary>
    public const string DesktopIconsValue = "zwmenu:desktopicons";

    /// <summary>Стеклянная полка для файлов, снятых с рабочего стола.</summary>
    public const string ShelfValue = "zwmenu:shelf";
    public const string WorkshopValue = "zwmenu:workshop";
    private int _tag;
    private string _label = "";
    private string _description = "";
    private ActionKind _kind;
    private string _value = "";
    private string _arguments = "";
    private string _glyph = "\uE8B7";
    private string _tint = "#FF3E4E62";
    private string _iconPath = "";
    private string _groupLayout = "";
    private bool _runAsAdmin;

    public MenuAction()
    {
        Children.CollectionChanged += (_, _) => OnChanged(nameof(ExtraLabel));
    }

    /// <summary>Пункты, которые выезжают при наведении. Один уровень, без вложенности дальше.</summary>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public ObservableCollection<MenuAction> Children { get; } = new();

    [JsonIgnore]
    public int Tag
    {
        get => _tag;
        set => Set(ref _tag, value, also: nameof(Role));
    }

    public string Label
    {
        get => _label;
        set => Set(ref _label, value);
    }

    public string Description
    {
        get => _description;
        set => Set(ref _description, value);
    }

    public ActionKind Kind
    {
        get => _kind;
        set => Set(ref _kind, value, also: nameof(PathMissing));
    }

    /// <summary>Путь к папке, файлу, ярлыку, программе или адрес.</summary>
    public string Value
    {
        get => _value;
        set => Set(ref _value, value, also: nameof(PathMissing));
    }

    /// <summary>Аргументы командной строки: только для программ.</summary>
    public string Arguments
    {
        get => _arguments;
        set => Set(ref _arguments, value);
    }

    /// <summary>Символ Segoe MDL2 Assets.</summary>
    public string Glyph
    {
        get => _glyph;
        set => Set(ref _glyph, value);
    }

    /// <summary>Матовый цвет кнопки, #AARRGGBB.</summary>
    public string Tint
    {
        get => _tint;
        set => Set(ref _tint, value);
    }

    /// <summary>Путь к PNG с реальной иконкой программы (извлекается из exe/lnk).</summary>
    public string IconPath
    {
        get => _iconPath;
        set => Set(ref _iconPath, value ?? "");
    }

    /// <summary>
    /// Вид выезжающего меню для Group: "apps" (список игр/программ) или "folders" (сетка папок).
    /// Пусто: угадываем по содержимому.
    /// </summary>
    public string GroupLayout
    {
        get => _groupLayout;
        set => Set(ref _groupLayout, value ?? "");
    }

    /// <summary>Запуск программы с UAC (от имени администратора).</summary>
    public bool RunAsAdmin
    {
        get => _runAsAdmin;
        set => Set(ref _runAsAdmin, value);
    }

    private string? _iconExistsFor;
    private bool _iconExists;

    /// <summary>File.Exists на каждое наведение давал микрофриз. Проверяем один раз на путь.</summary>
    [JsonIgnore]
    public bool HasFileIcon
    {
        get
        {
            if (!string.Equals(_iconExistsFor, IconPath, StringComparison.OrdinalIgnoreCase))
            {
                _iconExistsFor = IconPath;
                _iconExists = !string.IsNullOrWhiteSpace(IconPath) && File.Exists(IconPath);
            }
            return _iconExists;
        }
    }

    private string? _foldersKey;
    private bool _folders;

    /// <summary>Группа папок: сетка со своими иконками. Directory.Exists кэшируется.</summary>

    [JsonIgnore]
    public bool IsFoldersGroup
    {
        get
        {
            string key = GroupLayout + "|" + Kind + "|" + Children.Count;
            if (key == _foldersKey)
                return _folders;
            _foldersKey = key;
            _folders = ComputeFoldersGroup();
            return _folders;
        }
    }

    private bool ComputeFoldersGroup()
    {
        if (Kind != ActionKind.Group)
            return false;
        if (IsProcessGroup)
            return false;
        if (string.Equals(GroupLayout, "folders", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(GroupLayout, "apps", StringComparison.OrdinalIgnoreCase))
            return false;
        if (Children.Count == 0)
            return false;
        int dirs = Children.Count(c => !string.IsNullOrWhiteSpace(c.Value) && Directory.Exists(c.Value));
        return dirs * 2 >= Children.Count;
    }

    private string? _dirKey;
    private bool _dirYes;

    /// <summary>Directory.Exists на каждое наведение по плитке папки.</summary>
    [JsonIgnore]
    public bool ValueIsDirectory
    {
        get
        {
            if (!string.Equals(_dirKey, Value, StringComparison.OrdinalIgnoreCase))
            {
                _dirKey = Value;
                _dirYes = !string.IsNullOrWhiteSpace(Value) && Directory.Exists(Value);
            }
            return _dirYes;
        }
    }

    private string? _missingKey;
    private bool _missing;

    /// <summary>Группа процессов: выезд со списком для быстрого завершения.</summary>
    [JsonIgnore]
    public bool IsProcessGroup =>
        Kind == ActionKind.Group
        && string.Equals(GroupLayout, "processes", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string Role => Tag == 0 ? "Центр": $"По кругу: {Tag}";

    [JsonIgnore]
    public string ExtraLabel => Children.Count == 0 ? "": $"При наведении: {Children.Count}";

    /// <summary>Для пункта «открыть»: путь пустой или локальный файл/папка не найдены.</summary>
    [JsonIgnore]
    public bool PathMissing
    {
        get
        {
            string key = Kind + "|" + Value;
            if (key == _missingKey)
                return _missing;
            _missingKey = key;
            _missing = ComputePathMissing();
            return _missing;
        }
    }

    private bool ComputePathMissing()
    {
        if (Kind is ActionKind.None or ActionKind.Group or ActionKind.Keys)
            return false;
        if (string.IsNullOrWhiteSpace(Value))
            return true;
        if (IsWebAddress(Value) || IsShellAppTarget(Value) || IsProtocolTarget(Value) || !IsLocalPath(Value))
            return false;
        return !File.Exists(Value) && !Directory.Exists(Value);
    }

    /// <summary>Store / AppsFolder: shell:AppsFolder\Family!App или Family!App.</summary>
    public static bool IsShellAppTarget(string value)
    {
        string v = (value ?? "").Trim();
        if (v.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("shell:AppsFolder/", StringComparison.OrdinalIgnoreCase))
            return true;
        // PackageFamilyName!ApplicationId
        int bang = v.IndexOf('!');
        return bang > 0 && bang < v.Length - 1 && !v.Contains('\\') && !v.Contains('/');
    }

    public static string NormalizeShellAppTarget(string value)
    {
        string v = (value ?? "").Trim();
        const string prefixBs = "shell:AppsFolder\\";
        const string prefixFs = "shell:AppsFolder/";
        if (v.StartsWith(prefixBs, StringComparison.OrdinalIgnoreCase))
            return prefixBs + v[prefixBs.Length..].TrimStart('\\', '/');
        if (v.StartsWith(prefixFs, StringComparison.OrdinalIgnoreCase))
            return prefixBs + v[prefixFs.Length..].TrimStart('\\', '/');
        if (IsShellAppTarget(v) && !v.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            return prefixBs + v;
        return v;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MenuAction Clone()
    {
        var copy = new MenuAction
        {
            Label = Label,
            Description = Description,
            Kind = Kind,
            Value = Value,
            Arguments = Arguments,
            Glyph = Glyph,
            Tint = Tint,
            IconPath = IconPath,
            GroupLayout = GroupLayout,
            RunAsAdmin = RunAsAdmin
        };
        foreach (var child in Children)
            copy.Children.Add(child.Clone());
        return copy;
    }

    public static List<MenuAction> CreateDefaults()
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

        var list = new List<MenuAction>
        {
            Item(0, "Закрыть", "Просто закрывает колесо. Ничего не запускает.", ActionKind.None, "", "\uE711", "#FF3A3A3E"),
            Item(1, "Тренировки", "Домашний план: отжимания, пресс, ноги. Без железа.", ActionKind.Launch, "zwmenu:workout", "\uE805", "#FF3E5A6E"),
            GroupItem(2, "Процессы", "Наведи: список процессов. Кликни: открыть диспетчер задач.", "\uE7F4", "#FF6A5A3E", "processes"),
            GroupItem(3, "Игры", "Наведи или кликни: выедут игры и программы.", "\uE7FC", "#FF3E5E4A", "apps",
                Item(-1, "Рабочий стол", "Открывает папку рабочего стола.", ActionKind.Launch, desktop, "\uE7F4", "#FF3E5E4A")),
            GroupItem(4, "Папки", "Наведи: сетка папок. Иконки берутся как в проводнике, в том числе свои.", "\uE8B7", "#FF5E3E5A", "folders",
                Item(-1, "Изображения", "Открывает папку «Изображения».", ActionKind.Launch, pictures, "\uE91B", "#FF5E3E5A")),
            Item(5, "Проводник", "Открывает «Этот компьютер».", ActionKind.Launch, "explorer.exe", "\uE8B7", "#FF3E4E62"),
        };

        var panel = Item(6, "Панель управления", "Классическая панель управления: все элементы крупными значками.", ActionKind.Launch, "explorer.exe", "\uE713", "#FF5A3E4E");
        panel.Arguments = @"shell:::{21EC2020-3AEA-1069-A2DD-08002B30309D}";
        list.Add(panel);

        var cmd = Item(7, "Командная строка", "Командная строка в папке, которая сейчас открыта. От имени администратора.", ActionKind.Launch, "cmd.exe", "\uE756", "#FF3E5A52");
        cmd.RunAsAdmin = true;
        list.Add(cmd);

        list.Add(Item(8, "Блокнот", "Запускает Блокнот Windows.", ActionKind.Launch, "notepad.exe", "\uE70B", "#FF5A5240"));
        list.Add(Item(9, "Перевод",
            "Читает буфер. Можно скопировать перевод или оставить как есть. Обычное копирование буфер не меняет.",
            ActionKind.Launch, ClipboardTranslateValue, "\uE8C1", "#FF3E4E62"));
        list.Add(Item(10, "Экраны",
            "Одна мышь на два ноутбука в одной сети. Курсор переходит через край экрана.",
            ActionKind.Launch, EdgeShareValue, "\uE7F3", "#FF3E5A52"));
        list.Add(Item(11, "Шаблоны",
            "Текстовые заготовки. Своя клавиша копирует или сразу вставляет.",
            ActionKind.Launch, SnippetsValue, "\uE70F", "#FF4E4668"));
        list.Add(Item(12, "Диктофон",
            "Запись голоса с обработкой. Пауза, маркеры — файл на рабочий стол.",
            ActionKind.Launch, VoiceRecValue, "\uE720", "#FF5A3E4E"));
        list.Add(Item(13, "Скрыть иконки",
            "Убрать значки и оставить одни обои. Повторный клик вернёт.",
            ActionKind.Launch, DesktopIconsValue, "\uE73E", "#FF4A5560"));
        list.Add(Item(14, "Полка",
            "Стекло у края экрана. Перетащи файл с рабочего стола сюда, обои останутся чистыми. Обратно вытащи его на стол.",
            ActionKind.Launch, ShelfValue, "\uE7B8", "#FF5A4638"));
        list.Add(Item(15, "Мастерская",
            "Имена пачкой, картинки, видео, сервер, микрофон, место и второй компьютер.",
            ActionKind.Launch, WorkshopValue, "\uE90F", "#FF4A5560"));
        return list;
    }

    public static bool IsLocalPath(string value) =>
        value.Contains('\\') || value.Contains('/') || (value.Length >= 2 && value[1] == ':');

    /// <summary>URI вроде ms-settings: или mailto: не файл на диске.</summary>
    public static bool IsProtocolTarget(string value)
    {
        string v = (value ?? "").Trim();
        if (v.Length < 3)
            return false;
        // Обычный диск C:\...
        if (char.IsLetter(v[0]) && v[1] == ':' && (v.Length == 2 || v[2] is '\\' or '/'))
            return false;
        int colon = v.IndexOf(':');
        return colon > 1;
    }

    public static bool IsWebAddress(string value)
    {
        string v = value.Trim();
        return v.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    private static MenuAction Item(int tag, string label, string description, ActionKind kind, string value, string glyph, string tint)
        => new()
        {
            Tag = tag,
            Label = label,
            Description = description,
            Kind = kind,
            Value = value,
            Glyph = glyph,
            Tint = tint
        };

    private static MenuAction GroupItem(int tag, string label, string description, string glyph, string tint, string layout, params MenuAction[] children)
    {
        var item = Item(tag, label, description, ActionKind.Group, "", glyph, tint);
        item.GroupLayout = layout;
        foreach (var child in children)
            item.Children.Add(child);
        return item;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null, string? also = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        OnChanged(name);
        if (also != null)
            OnChanged(also);
    }

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
