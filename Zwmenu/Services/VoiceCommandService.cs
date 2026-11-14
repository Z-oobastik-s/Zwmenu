using Zwmenu.Models;

namespace Zwmenu.Services;

/// <summary>
/// Голосовые команды: нечёткое сравнение (опечатка/одна буква — ок).
/// «Браузер» = Opera GX (или другой не-Edge из реестра). Edge голосом не трогаем.
/// </summary>
public static class VoiceCommandService
{
    public readonly record struct Match(string Title, MenuAction Action);

    private static readonly string[] OpenVerbs =
    [
        // RU
        "открой", "открыть", "открои", "аткрой", "открй",
        "запусти", "запустить", "запустиь",
        "покажи", "показать",
        // UK / суржик
        "відкрий", "відкрити", "видкрий", "видкрити",
        "запусти", "запустити",
        "покажи", "показати"
    ];

    public static bool TryMatch(string spoken, out Match match)
    {
        match = default;
        string raw = Normalize(spoken);
        if (raw.Length < 3)
            return false;

        // Целые фразы (вкладки / диспетчер) — с допуском опечаток.
        if (FuzzyPhrase(raw, 2, "новая вкладка", "новую вкладку", "открой вкладку", "открыть вкладку",
                "новая вклдка", "новую вкладку браузера",
                "нова вкладка", "нову вкладку", "відкрий вкладку", "відкрити вкладку"))
        {
            match = Keys("Новая вкладка", "Ctrl+T");
            return true;
        }

        if (FuzzyPhrase(raw, 2, "закрой вкладку", "закрыть вкладку", "закрой вкладка", "закрыть вкладка",
                "закрой вкладку браузера",
                "закрий вкладку", "закрити вкладку", "закрий вкладка"))
        {
            match = Keys("Закрыть вкладку", "Ctrl+W");
            return true;
        }

        if (FuzzyPhrase(raw, 2, "новое окно", "новое окно браузера", "открой новое окно", "открыть новое окно",
                "нове вікно", "відкрий нове вікно", "відкрити нове вікно"))
        {
            match = Keys("Новое окно", "Ctrl+N");
            return true;
        }

        if (FuzzyPhrase(raw, 2, "диспетчер задач", "диспетчер", "открой диспетчер", "запусти диспетчер",
                "диспетчер задача", "диспечер задач",
                "диспетчер завдань", "диспетчер задач"))
        {
            match = Launch("Диспетчер задач", "taskmgr.exe");
            return true;
        }

        // Диктофон
        if (FuzzyPhrase(raw, 3,
                "запиши голос", "запись голоса", "начать запись", "начни запись", "открой диктофон",
                "запусти диктофон", "диктофон", "голосовая запись",
                "почни запис", "відкрий диктофон"))
        {
            match = Launch("Диктофон", "zwmenu:voicerec:start");
            return true;
        }

        if (FuzzyPhrase(raw, 2, "пауза записи", "пауза диктофона", "поставь на паузу"))
        {
            match = Launch("Пауза", "zwmenu:voicerec:pause");
            return true;
        }

        if (FuzzyPhrase(raw, 2, "продолжи запись", "продолжить запись", "сними с паузы"))
        {
            match = Launch("Продолжить", "zwmenu:voicerec:resume");
            return true;
        }

        if (FuzzyPhrase(raw, 2, "стоп запись", "останови запись", "закончить запись", "сохрани запись",
                "стоп диктофон", "заверши запись"))
        {
            match = Launch("Стоп", "zwmenu:voicerec:stop");
            return true;
        }

        // Vosk часто слышит «телеграм» как телик/телеканал/грамм — ловим отдельно.
        if (TryMatchTelegramGarble(raw, out match))
            return true;

        if (!TryStripVerb(raw, out string rest) || rest.Length == 0)
            return false;

        if (rest.StartsWith("папку ", StringComparison.Ordinal)
            || rest.StartsWith("папка ", StringComparison.Ordinal)
            || rest.StartsWith("папки ", StringComparison.Ordinal)
            || rest.StartsWith("папку ", StringComparison.Ordinal)
            || rest.StartsWith("теку ", StringComparison.Ordinal)) // «папку»→«теку» бывает
            rest = rest[(rest.IndexOf(' ') + 1)..].Trim();

        return MatchTarget(rest, out match);
    }

    public static bool TryExecute(string spoken, out string title)
    {
        title = "";
        if (!TryMatch(spoken, out var m))
            return false;
        title = m.Title;
        try
        {
            ActionDispatcher.Execute(m.Action);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Пробует несколько гипотез (RU/UK/best) — Vosk на суржике врёт по-разному.</summary>
    public static bool TryExecuteAny(out string title, params string?[] spokenVariants)
    {
        title = "";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? s in spokenVariants)
        {
            if (string.IsNullOrWhiteSpace(s))
                continue;
            string key = s.Trim();
            if (!seen.Add(key))
                continue;
            if (TryExecute(key, out title))
                return true;
        }

        return false;
    }

    /// <summary>
    /// «открой телеграм» → телик / телеканал / тереком / тебе ли грамм…
    /// Короткая фраза почти целиком про «тел*» или «*грамм» = Telegram.
    /// </summary>
    private static bool TryMatchTelegramGarble(string raw, out Match match)
    {
        match = default;

        // Прямые кривые фразы целиком.
        if (FuzzyPhrase(raw, 4,
                "открой телеграм", "открыть телеграм", "запусти телеграм", "відкрий телеграм",
                "открой телеграмм", "открой телик", "открой телеканал", "открой тереком",
                "открой грамм", "открой телега", "открой тг",
                "запусти телик", "запусти телеканал", "відкрий телеграм"))
        {
            return TryBuildTelegram(out match);
        }

        bool hasVerb = TryStripVerb(raw, out string rest);
        if (hasVerb && rest.Length > 0 && LooksLikeTelegramPhrase(rest))
            return TryBuildTelegram(out match);

        // Без нормального глагола: коротыш из «телемусора» (Vosk съел «открой»).
        if (!hasVerb && IsShortTelegramOnlyUtterance(raw))
            return TryBuildTelegram(out match);

        return false;
    }

    private static bool TryBuildTelegram(out Match match)
    {
        match = default;
        string? path = FirstExisting(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Telegram Desktop", "Telegram.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Telegram Desktop", "Telegram.exe"));
        if (path == null)
            return false;
        match = Launch("Telegram", path);
        return true;
    }

    private static bool IsShortTelegramOnlyUtterance(string raw)
    {
        var tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || tokens.Length > 5)
            return false;

        string[] stop =
        [
            "а", "и", "или", "да", "ну", "это", "вот", "мне", "тебе", "ли", "на", "в", "во",
            "the", "to", "a", "і", "та", "це", "ну"
        ];
        var content = tokens
            .Where(t => t.Length > 1 && !stop.Contains(t))
            .ToArray();
        if (content.Length == 0 || content.Length > 3)
            return false;

        return content.All(LooksLikeTelegramToken) || content.Any(LooksLikeTelegramToken) && content.Length <= 2;
    }

    private static bool LooksLikeTelegramPhrase(string phrase)
    {
        if (LooksLikeTelegramToken(phrase.Replace(" ", "", StringComparison.Ordinal)))
            return true;
        foreach (string t in phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (LooksLikeTelegramToken(t))
                return true;
        }

        // «тебе ли грамм» / «или грамм»
        if (phrase.Contains("грамм", StringComparison.Ordinal)
            || phrase.Contains("грам", StringComparison.Ordinal))
            return true;

        string key = ConsonantKey(phrase);
        string tg = ConsonantKey("телеграм");
        return key.Length >= 3 && (key.Contains(tg, StringComparison.Ordinal)
                                   || tg.Contains(key, StringComparison.Ordinal)
                                   || EditDistance(key, tg) <= 2);
    }

    private static bool LooksLikeTelegramToken(string token)
    {
        if (token.Length == 0)
            return false;
        if (token is "тг" or "tg" or "телега" or "телик" or "телек" or "телеканал"
            or "телеграм" or "телеграмм" or "telegram" or "тереком" or "телегр"
            or "грамм" or "грам" or "телеграмме" or "телеграма")
            return true;
        if (token.StartsWith("теле", StringComparison.Ordinal) && token.Length <= 12)
            return true;
        if (token.StartsWith("тел", StringComparison.Ordinal) && token.Length is >= 4 and <= 10)
            return true;
        if (token.EndsWith("грам", StringComparison.Ordinal) || token.EndsWith("грамм", StringComparison.Ordinal))
            return true;
        return EditDistance(token, "телеграм") <= 3
               || EditDistance(token, "telegram") <= 3
               || EditDistance(token, "телик") <= 2;
    }

    private static string ConsonantKey(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char ch in s)
        {
            if (ch is 'а' or 'е' or 'ё' or 'и' or 'і' or 'ї' or 'о' or 'у' or 'ы' or 'э' or 'ю' or 'я'
                or 'ь' or 'ъ' or ' ')
                continue;
            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static bool MatchTarget(string rest, out Match match)
    {
        match = default;
        if (rest.Length == 0)
            return false;

        // Кандидаты: (название, алиасы, путь или null=папка/shell позже)
        var scored = new List<(int Dist, int AliasLen, string Title, Func<string?> Resolve)>();

        void Consider(string title, string[] aliases, Func<string?> resolve)
        {
            int best = int.MaxValue;
            int bestLen = 1;
            foreach (string a in aliases)
            {
                int d = PhraseDistance(rest, a);
                if (d < best)
                {
                    best = d;
                    bestLen = Math.Max(1, a.Length);
                }
            }

            int maxAllowed = MaxEdits(bestLen);
            // Также: цель целиком внутри фразы («телеграм пожалуйста»).
            if (best > maxAllowed)
            {
                foreach (string a in aliases)
                {
                    if (a.Length < 4)
                        continue;
                    if (ContainsFuzzy(rest, a, MaxEdits(a.Length)))
                    {
                        best = 0;
                        bestLen = a.Length;
                        break;
                    }
                }
            }

            if (best <= MaxEdits(bestLen))
                scored.Add((best, bestLen, title, resolve));
        }

        Consider("Telegram",
            [
                "телеграм", "телеграмм", "telegram", "тг", "телега", "телик", "телек",
                "телеканал", "тереком", "телегр", "грамм", "грам", "телеграма"
            ],
            () => FirstExisting(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Telegram Desktop", "Telegram.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Telegram Desktop", "Telegram.exe")));

        // Доп. удар по кривому распознаванию «телеграм».
        if (LooksLikeTelegramPhrase(rest) && TryBuildTelegram(out match))
            return true;

        Consider("Cursor",
            ["курсор", "cursor", "курсар"],
            () => FirstExisting(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "cursor", "Cursor.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "Cursor", "Cursor.exe")));

        Consider("Opera GX",
            ["опера", "opera", "опера gx", "opera gx", "опера джикс", "опера джи икс", "операджикс", "опера джі ікс"],
            FindOperaGx);

        Consider("Chrome",
            ["хром", "chrome", "гугл хром", "google chrome"],
            () => FirstExisting(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Google", "Chrome", "Application", "chrome.exe")));

        Consider("Firefox",
            ["файрфокс", "фаерфокс", "firefox", "огнелис"],
            () => FirstExisting(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Mozilla Firefox", "firefox.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Mozilla Firefox", "firefox.exe")));

        // «браузер» → Opera GX / дефолтный http-клиент, никогда Edge.
        Consider("Браузер",
            ["браузер", "броузер", "інтернет", "интернет"],
            FindPreferredBrowser);

        Consider("Блокнот",
            ["блокнот", "notepad"],
            () => "notepad.exe");

        Consider("Этот компьютер",
            ["этот компьютер", "мой компьютер", "проводник", "эксплорер", "explorer",
                "цей компютер", "цей комп'ютер", "мій компютер", "провідник"],
            () => "shell:MyComputerFolder");

        Consider("Рабочий стол",
            ["рабочий стол", "десктоп", "desktop", "робочий стіл", "стільниця"],
            () => DirOrNull(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)));

        Consider("Документы",
            ["документы", "мои документы", "documents", "документи", "мої документи"],
            () => DirOrNull(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));

        Consider("Загрузки",
            ["загрузки", "загрузка", "downloads", "скачанное", "завантаження", "завантажене"],
            () => DirOrNull(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")));

        Consider("Параметры",
            ["настройки windows", "параметры windows", "параметры системы", "настройки системы",
                "параметри windows", "налаштування windows"],
            () => "ms-settings:");

        if (scored.Count == 0)
            return false;

        // Меньше правок / длиннее алиас → лучше.
        scored.Sort((a, b) =>
        {
            int c = a.Dist.CompareTo(b.Dist);
            return c != 0 ? c : b.AliasLen.CompareTo(a.AliasLen);
        });

        foreach (var s in scored)
        {
            string? path = s.Resolve();
            if (string.IsNullOrWhiteSpace(path))
                continue;
            match = Launch(s.Title, path);
            return true;
        }

        return false;
    }

    private static string? FindOperaGx()
    {
        foreach (var (name, exe) in EnumerateRegisteredBrowsers())
        {
            if (name.Contains("opera", StringComparison.OrdinalIgnoreCase)
                && File.Exists(exe))
                return exe;
        }

        return FirstExisting(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Opera GX", "opera.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Opera", "opera.exe"),
            @"C:\Program Files\Opera GX\opera.exe",
            @"C:\Program Files\Opera\opera.exe");
    }

    private static string? FindPreferredBrowser()
    {
        // 1) Opera GX / Opera
        string? opera = FindOperaGx();
        if (opera != null)
            return opera;

        // 2) Системный браузер по умолчанию (http), если это не Edge
        string? def = TryDefaultHttpBrowser();
        if (def != null && !IsEdgePath(def))
            return def;

        // 3) Любой зарегистрированный кроме Edge
        foreach (var (_, exe) in EnumerateRegisteredBrowsers())
        {
            if (!IsEdgePath(exe) && File.Exists(exe))
                return exe;
        }

        return null;
    }

    private static string? TryDefaultHttpBrowser()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice");
            string? progId = key?.GetValue("ProgId") as string;
            if (string.IsNullOrWhiteSpace(progId))
                return null;

            // ProgId вроде OperaGXStable.Url.h... → ищем Clients\StartMenuInternet\*
            foreach (var (name, exe) in EnumerateRegisteredBrowsers())
            {
                if (progId.Contains("Opera", StringComparison.OrdinalIgnoreCase)
                    && name.Contains("Opera", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(exe))
                    return exe;
                if (progId.StartsWith(name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)
                    && File.Exists(exe))
                    return exe;
            }

            // Прямой ProgId → command
            using var cmd = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command");
            string? line = cmd?.GetValue(null) as string;
            return ExtractExe(line);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<(string Name, string Exe)> EnumerateRegisteredBrowsers()
    {
        string[] roots =
        [
            @"SOFTWARE\Clients\StartMenuInternet",
            @"SOFTWARE\WOW6432Node\Clients\StartMenuInternet"
        ];

        foreach (string root in roots)
        {
            foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            {
                Microsoft.Win32.RegistryKey? baseKey = null;
                try { baseKey = hive.OpenSubKey(root); }
                catch { continue; }
                if (baseKey == null)
                    continue;

                using (baseKey)
                {
                    foreach (string name in baseKey.GetSubKeyNames())
                    {
                        string? exe = null;
                        try
                        {
                            using var cmd = baseKey.OpenSubKey(name + @"\shell\open\command");
                            exe = ExtractExe(cmd?.GetValue(null) as string);
                        }
                        catch { /* skip */ }

                        if (!string.IsNullOrWhiteSpace(exe))
                            yield return (name, exe!);
                    }
                }
            }
        }
    }

    private static string? ExtractExe(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;
        string s = commandLine.Trim();
        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            if (end > 1)
                return s[1..end];
        }

        int sp = s.IndexOf(' ');
        return sp > 0 ? s[..sp] : s;
    }

    private static bool IsEdgePath(string path) =>
        path.Contains("msedge", StringComparison.OrdinalIgnoreCase)
        || path.Contains(@"\Edge\", StringComparison.OrdinalIgnoreCase);

    private static Match Launch(string title, string path) =>
        new(title, new MenuAction
        {
            Label = title,
            Kind = ActionKind.Launch,
            Value = path
        });

    private static Match Keys(string title, string shortcut) =>
        new(title, new MenuAction
        {
            Label = title,
            Kind = ActionKind.Keys,
            Value = shortcut
        });

    private static string? FirstExisting(params string[] candidates)
    {
        foreach (string c in candidates)
        {
            string p = (c ?? "").Trim();
            if (p.Length == 0)
                continue;
            if (p.Equals("notepad.exe", StringComparison.OrdinalIgnoreCase)
                || p.Equals("taskmgr.exe", StringComparison.OrdinalIgnoreCase)
                || File.Exists(p))
                return p;
        }

        return null;
    }

    private static string? DirOrNull(string path) =>
        Directory.Exists(path) ? path : null;

    private static bool TryStripVerb(string raw, out string rest)
    {
        rest = "";
        var tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return false;

        int verbEnd = 0;
        bool ok = false;
        // первое слово ≈ глагол; «открой пожалуйста …»
        foreach (string v in OpenVerbs)
        {
            if (EditDistance(tokens[0], v) <= MaxEdits(v.Length))
            {
                ok = true;
                verbEnd = 1;
                break;
            }
        }

        if (!ok)
            return false;

        if (tokens.Length > 2
            && EditDistance(tokens[1], "будь") <= 1
            && EditDistance(tokens[2], "ласка") <= 2)
            verbEnd = 3;
        else if (tokens.Length > 1
                 && (EditDistance(tokens[1], "пожалуйста") <= 2
                     || EditDistance(tokens[1], "будьласка") <= 2
                     || tokens[1] is "плиз" or "please"))
            verbEnd = 2;

        if (verbEnd >= tokens.Length)
            return false;

        rest = string.Join(' ', tokens.Skip(verbEnd));
        return rest.Length > 0;
    }

    private static bool FuzzyPhrase(string raw, int maxExtra, params string[] phrases)
    {
        foreach (string p in phrases)
        {
            int d = PhraseDistance(raw, p);
            if (d <= Math.Max(maxExtra, MaxEdits(p.Length)))
                return true;
        }

        return false;
    }

    private static int PhraseDistance(string a, string b)
    {
        if (a.Equals(b, StringComparison.Ordinal))
            return 0;
        // Если длины сильно разные — сравниваем как «содержит с опечатками» через токены.
        return EditDistance(a, b);
    }

    private static bool ContainsFuzzy(string hay, string needle, int maxDist)
    {
        if (hay.Length < needle.Length - maxDist)
            return false;
        if (hay.Contains(needle, StringComparison.Ordinal))
            return true;

        // Скользящее окно по словам / по символам для коротких.
        var words = hay.Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            for (int len = 1; len <= words.Length - i; len++)
            {
                string slice = string.Join(' ', words.Skip(i).Take(len));
                if (Math.Abs(slice.Length - needle.Length) > maxDist + 2)
                    continue;
                if (EditDistance(slice, needle) <= maxDist)
                    return true;
            }
        }

        return false;
    }

    private static int MaxEdits(int len) =>
        len <= 4 ? 1 : len <= 8 ? 2 : 3;

    private static int EditDistance(string a, string b)
    {
        if (a.Length == 0)
            return b.Length;
        if (b.Length == 0)
            return a.Length;

        // Ограничим тяжёлые сравнения.
        if (Math.Abs(a.Length - b.Length) > 8 && a.Length + b.Length > 24)
            return Math.Abs(a.Length - b.Length);

        int n = a.Length;
        int m = b.Length;
        var prev = new int[m + 1];
        var cur = new int[m + 1];
        for (int j = 0; j <= m; j++)
            prev[j] = j;

        for (int i = 1; i <= n; i++)
        {
            cur[0] = i;
            char ca = a[i - 1];
            for (int j = 1; j <= m; j++)
            {
                int cost = ca == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(
                    Math.Min(cur[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }

            (prev, cur) = (cur, prev);
        }

        return prev[m];
    }

    private static string Normalize(string spoken)
    {
        if (string.IsNullOrWhiteSpace(spoken))
            return "";
        var sb = new System.Text.StringBuilder(spoken.Length);
        foreach (char ch in spoken.Trim().ToLowerInvariant())
        {
            char c = ch == 'ё' ? 'е' : ch;
            if (char.IsLetterOrDigit(c) || c is ' ' or '-')
                sb.Append(c == '-' ? ' ' : c);
            else if (char.IsWhiteSpace(c))
                sb.Append(' ');
        }

        string s = sb.ToString();
        while (s.Contains("  ", StringComparison.Ordinal))
            s = s.Replace("  ", " ", StringComparison.Ordinal);
        return s.Trim();
    }
}
