using System.Collections;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using YamlDotNet.Serialization;

namespace Zwmenu.Services;

public sealed record TextTool(string Id, string Title, string Hint, string Tag);

public sealed record TextOpResult(bool Ok, string Message, string? OutputPath = null);

/// <summary>Операции над текстовыми / конфиг-файлами.</summary>
public static class TextFileService
{
    private static readonly HashSet<string> Ext = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".json", ".yml", ".yaml", ".xml", ".properties", ".props",
        ".ini", ".cfg", ".conf", ".config", ".env", ".toml", ".csv", ".tsv",
        ".md", ".markdown", ".html", ".htm", ".css", ".js", ".ts", ".jsx", ".tsx",
        ".sql", ".svg", ".gitignore", ".editorconfig", ".dockerignore",
        ".csproj", ".sln", ".http", ".rest", ".plist", ".strings"
    };

    /// <summary>Внешнее кольцо: проверка и форматы.</summary>
    public static IReadOnlyList<TextTool> OuterTools { get; } =
    [
        new("valid", "Валид.", "Проверить JSON / YAML / XML / properties", "act:valid"),
        new("pretty", "Красив.", "Красиво отформатировать", "act:pretty"),
        new("mini", "Мини", "Сжать JSON / убрать лишние пробелы", "act:mini"),
        new("tojson", "→JSON", "Сохранить как .json", "act:tojson"),
        new("toyaml", "→YAML", "Сохранить как .yml", "act:toyaml"),
        new("toxml", "→XML", "Сохранить как .xml", "act:toxml"),
        new("totxt", "→TXT", "Обычный текст .txt", "act:totxt"),
        new("utf8", "UTF-8", "Перекодировать в UTF-8 без BOM", "act:utf8"),
        new("lf", "LF", "Переводы строк Unix (\\n)", "act:lf"),
        new("crlf", "CRLF", "Переводы строк Windows (\\r\\n)", "act:crlf")
    ];

    /// <summary>Внутреннее кольцо: правка и утилиты.</summary>
    public static IReadOnlyList<TextTool> InnerTools { get; } =
    [
        new("sort", "Сорт", "Сортировать строки A→Z", "act:sort"),
        new("uniq", "Уник.", "Убрать дубликаты строк", "act:uniq"),
        new("empty", "Пустые−", "Удалить пустые строки", "act:empty"),
        new("trim", "Обрез.", "Обрезать пробелы по краям строк", "act:trim"),
        new("upper", "a→A", "ВЕРХНИЙ РЕГИСТР", "act:upper"),
        new("lower", "A→a", "нижний регистр", "act:lower"),
        new("b64", "Base64", "Кодировать или декодировать Base64", "act:b64"),
        new("hash", "SHA256", "Хеш файла → .sha256", "act:hash"),
        new("copy", "Копир.", "Скопировать текст в буфер", "act:copy"),
        new("stat", "Стат.", "Строки / слова / символы / размер", "act:stat")
    ];

    public static bool IsTextFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;
        string ext = Path.GetExtension(path);
        if (Ext.Contains(ext))
            return true;
        // Файлы без расширения вроде Dockerfile / Makefile / LICENSE
        string name = Path.GetFileName(path);
        return name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Makefile", StringComparison.OrdinalIgnoreCase)
               || name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase)
               || name.Equals("README", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> FilterText(IEnumerable<string> paths) =>
        paths.Where(IsTextFile).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static TextOpResult Run(string sourcePath, string tag)
    {
        try
        {
            if (!IsTextFile(sourcePath))
                return new(false, "Это не текстовый файл.");

            return tag switch
            {
                "act:valid" => Validate(sourcePath),
                "act:pretty" => Pretty(sourcePath),
                "act:mini" => Minify(sourcePath),
                "act:tojson" => ConvertTo(sourcePath, ".json"),
                "act:toyaml" => ConvertTo(sourcePath, ".yml"),
                "act:toxml" => ConvertTo(sourcePath, ".xml"),
                "act:totxt" => ConvertTo(sourcePath, ".txt"),
                "act:utf8" => RewriteEncoding(sourcePath, withBom: false),
                "act:lf" => RewriteLineEnds(sourcePath, "\n"),
                "act:crlf" => RewriteLineEnds(sourcePath, "\r\n"),
                "act:sort" => TransformLines(sourcePath, " сорт", lines => lines.OrderBy(l => l, StringComparer.Ordinal).ToList()),
                "act:uniq" => TransformLines(sourcePath, " уник", lines => lines.Distinct(StringComparer.Ordinal).ToList()),
                "act:empty" => TransformLines(sourcePath, " без пустых", lines => lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList()),
                "act:trim" => TransformLines(sourcePath, " обрез", lines => lines.Select(l => l.Trim()).ToList()),
                "act:upper" => TransformText(sourcePath, " UPPER", t => t.ToUpperInvariant()),
                "act:lower" => TransformText(sourcePath, " lower", t => t.ToLowerInvariant()),
                "act:b64" => Base64Smart(sourcePath),
                "act:hash" => WriteHash(sourcePath),
                "act:copy" => CopyToClipboard(sourcePath),
                "act:stat" => Stats(sourcePath),
                _ => new(false, "Неизвестное действие.")
            };
        }
        catch (Exception ex)
        {
            return new(false, ex.Message);
        }
    }

    // ─── Validate ─────────────────────────────────────────────

    private static TextOpResult Validate(string path)
    {
        string text = ReadAll(path);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        string name = Path.GetFileName(path);

        if (LooksBinary(text))
            return new(false, $"{name}: это не текст (бинарный файл).");

        if (string.IsNullOrWhiteSpace(text))
            return new(false, $"{name}: файл пустой — проверять нечего.");

        return ext switch
        {
            ".json" => ValidateJson(text, name),
            ".yml" or ".yaml" => ValidateYaml(text, name),
            ".xml" or ".svg" or ".csproj" or ".config" => ValidateXml(text, name),
            ".properties" or ".props" or ".ini" or ".env" => ValidateProperties(text, name),
            ".toml" => new(true, $"{name}: TOML · базовая проверка ок · {CountLines(text)} стр."),
            ".csv" or ".tsv" => ValidateCsv(text, name, ext == ".tsv" ? '\t' : ','),
            _ => new(true, $"{name}: текст ок · {CountLines(text)} стр. · {text.Length} симв.")
        };
    }

    private static TextOpResult ValidateJson(string text, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            string kind = doc.RootElement.ValueKind switch
            {
                JsonValueKind.Object => "объект",
                JsonValueKind.Array => "массив",
                _ => "значение"
            };
            int n = doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement.EnumerateObject().Count()
                : doc.RootElement.ValueKind == JsonValueKind.Array
                    ? doc.RootElement.GetArrayLength()
                    : 1;
            return new(true, $"{name}: JSON ок ✓ · {kind} · {n} элем.");
        }
        catch (JsonException ex)
        {
            // JsonException.LineNumber / BytePositionInLine — 0-based.
            int line = (int)(ex.LineNumber ?? 0) + 1;
            int col = (int)(ex.BytePositionInLine ?? 0) + 1;
            string tip = ExplainJson(ex.Message);
            string snip = Snippet(text, line, col);
            return new(false, $"{name}: JSON битый\nстрока {line}, позиция {col}\n{tip}{snip}");
        }
    }

    private static TextOpResult ValidateYaml(string text, string name)
    {
        // Сначала ловим очевидные косяки по строкам оригинала (номер строки = как в редакторе).
        var obvious = FindYamlObviousMistake(text);
        if (obvious != null)
            return obvious with { Message = name + ": " + obvious.Message };

        if (StripYamlNoise(text).Length == 0)
            return new(true, $"{name}: YAML ок ✓ · только комментарии/пусто");

        try
        {
            // Парсим ИСХОДНЫЙ текст — иначе номера строк от YamlDotNet съезжают
            // (раньше выкидывали комментарии и указывали не туда).
            var deser = new DeserializerBuilder()
                .IgnoreUnmatchedProperties()
                .Build();
            object? obj = deser.Deserialize<object>(new StringReader(text));
            string shape = DescribeYamlRoot(obj);
            return new(true, $"{name}: YAML ок ✓ · {shape} · {CountLines(text)} стр.");
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            int line = Math.Max(1, (int)ex.Start.Line);
            int col = Math.Max(1, (int)ex.Start.Column);
            // Если парсер ткнул в комментарий/пустое — ищем реальную кривую строку рядом.
            var better = RefineYamlError(text, line, col, ex.Message);
            if (better != null)
                return new(false, $"{name}: YAML битый\n{better}");

            string tip = ExplainYaml(ex.Message);
            string snip = Snippet(text, line, col);
            return new(false, $"{name}: YAML битый\nстрока {line}, позиция {col}\n{tip}{snip}");
        }
        catch (Exception ex)
        {
            string tip = ExplainYaml(ex.Message);
            return new(false, $"{name}: YAML битый\n{tip}");
        }
    }

    /// <summary>
    /// Частые ручные ошибки: «key value» без двоеточия, таб в отступе.
    /// Номера строк — 1-based, как в Notepad++.
    /// </summary>
    private static TextOpResult? FindYamlObviousMistake(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        bool inBlockScalar = false;
        int blockKeyIndent = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i];
            string trim = raw.Trim();
            int indent = 0;
            while (indent < raw.Length && raw[indent] == ' ') indent++;

            // Содержимое | / > не проверяем на «нет двоеточия».
            if (inBlockScalar)
            {
                if (trim.Length == 0 || trim.StartsWith('#'))
                    continue;
                if (indent > blockKeyIndent)
                    continue;
                inBlockScalar = false;
            }

            if (trim.Length == 0 || trim.StartsWith('#'))
                continue;

            if (indent < raw.Length && raw[indent] == '\t')
            {
                return new(false,
                    $"YAML битый\nстрока {i + 1}\nВ отступе таб — в YAML нужны только пробелы." +
                    Snippet(text, i + 1, indent + 1));
            }

            if (StartsYamlBlockScalar(trim))
            {
                inBlockScalar = true;
                blockKeyIndent = indent;
                continue;
            }

            if (LooksLikeMissingColon(trim))
            {
                int col = raw.IndexOf(trim, StringComparison.Ordinal) + 1;
                return new(false,
                    $"YAML битый\nстрока {i + 1}\nПосле ключа нет двоеточия. Нужно: «ключ: значение»." +
                    Snippet(text, i + 1, Math.Max(1, col)));
            }
        }
        return null;
    }

    /// <summary>Строка объявляет block scalar: «key: |», «key: >-», «|» и т.п.</summary>
    private static bool StartsYamlBlockScalar(string trim)
    {
        // value tail after optional "key:"
        string value = trim;
        int colon = trim.IndexOf(':');
        if (colon >= 0)
            value = trim[(colon + 1)..].Trim();
        // "|", ">", "|+", ">-", "|2", ">1 #" …
        return Regex.IsMatch(value, @"^[|>][+-]?\d*\s*(#.*)?$");
    }

    private static bool LooksLikeMissingColon(string trim)
    {
        // Уже есть ключ: значение / список / документ
        if (trim.StartsWith("- ") || trim.StartsWith("---") || trim.StartsWith("..."))
            return false;
        if (trim.Contains(':'))
            return false;
        // flow / спец
        if (trim.StartsWith('{') || trim.StartsWith('[') || trim.StartsWith('|') || trim.StartsWith('>'))
            return false;

        // Два «слова» через пробел: language en
        var m = Regex.Match(trim, @"^([A-Za-z_][\w\-]*)\s+(\S.+)$");
        return m.Success;
    }

    private static string? RefineYamlError(string text, int line, int col, string msg)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (line < 1 || line > lines.Length)
            return null;

        string pointed = lines[line - 1].Trim();
        // Парсер часто указывает на комментарий/пустую — ищем ближайшую кривую строку ниже/выше.
        if (pointed.Length == 0 || pointed.StartsWith('#'))
        {
            for (int i = line - 1; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                if (t.Length == 0 || t.StartsWith('#'))
                    continue;
                if (LooksLikeMissingColon(t))
                {
                    return $"строка {i + 1}\nПосле ключа нет двоеточия. Нужно: «ключ: значение»." +
                           Snippet(text, i + 1, 1);
                }
                // Первая содержательная строка после ложного указателя
                return $"строка {i + 1}\n{ExplainYaml(msg)}" + Snippet(text, i + 1, 1);
            }
        }

        if (LooksLikeMissingColon(pointed))
        {
            return $"строка {line}\nПосле ключа нет двоеточия. Нужно: «ключ: значение»." +
                   Snippet(text, line, col);
        }
        return null;
    }

    private static TextOpResult ValidateXml(string text, string name)
    {
        try
        {
            var doc = XDocument.Parse(text, LoadOptions.SetLineInfo);
            string root = doc.Root?.Name.LocalName ?? "?";
            int kids = doc.Root?.Elements().Count() ?? 0;
            return new(true, $"{name}: XML ок ✓ · <{root}> · {kids} детей");
        }
        catch (System.Xml.XmlException ex)
        {
            int line = Math.Max(1, ex.LineNumber);
            int col = Math.Max(1, ex.LinePosition);
            string tip = ExplainXml(ex.Message);
            string snip = Snippet(text, line, col);
            return new(false, $"{name}: XML битый\nстрока {line}, позиция {col}\n{tip}{snip}");
        }
    }

    private static TextOpResult ValidateProperties(string text, string name)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int keys = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';') || line.StartsWith('['))
                continue;
            if (!line.Contains('=') && !line.Contains(':'))
            {
                string snip = "\n→ «" + Trunc(lines[i].TrimEnd(), 48) + "»";
                return new(false,
                    $"{name}: properties битый\nстрока {i + 1}\nНужен вид ключ=значение{snip}");
            }
            keys++;
        }
        return new(true, $"{name}: properties ок ✓ · {keys} ключей");
    }

    private static TextOpResult ValidateCsv(string text, string name, char sep)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return new(false, $"{name}: CSV пустой.");

        int cols = SplitCsvLine(lines[0], sep).Length;
        for (int i = 1; i < lines.Length; i++)
        {
            int n = SplitCsvLine(lines[i], sep).Length;
            if (n != cols)
            {
                return new(false,
                    $"{name}: CSV битый\nстрока {i + 1}: {n} колонок, а в шапке {cols}\n→ «{Trunc(lines[i], 48)}»");
            }
        }
        return new(true, $"{name}: CSV ок ✓ · {lines.Length} стр. · {cols} кол.");
    }

    private static string[] SplitCsvLine(string line, char sep)
    {
        // Простой сплит без полного CSV-парсера — для проверки числа колонок хватает.
        return line.Split(sep);
    }

    private static string StripYamlNoise(string text)
    {
        var sb = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            string t = raw.Trim();
            if (t.Length == 0 || t.StartsWith('#'))
                continue;
            sb.AppendLine(raw);
        }
        return sb.ToString().Trim();
    }

    private static string DescribeYamlRoot(object? obj) => obj switch
    {
        null => "пустой документ",
        IDictionary d => $"словарь · {d.Count} ключей",
        IList list => $"список · {list.Count} эл.",
        string => "строка",
        _ => obj.GetType().Name
    };

    private static string Snippet(string text, int line1Based, int col1Based)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (line1Based < 1 || line1Based > lines.Length)
            return "";
        string line = lines[line1Based - 1];
        string show = Trunc(line, 56);
        // Курсор-приближение под проблемным местом
        int mark = Math.Clamp(col1Based - 1, 0, Math.Min(show.Length, 56));
        string pointer = new string(' ', Math.Min(mark, 40)) + "↑";
        return $"\n«{show}»\n{pointer}";
    }

    private static string ExplainJson(string msg)
    {
        msg = msg ?? "";
        if (ContainsAny(msg, "invalid property", "duplicate"))
            return "Повторяется одно и то же имя поля.";
        if (ContainsAny(msg, "expected", "','", "comma"))
            return "Пропущена запятая или лишняя запятая.";
        if (ContainsAny(msg, "quote", "'\"'"))
            return "Проблема с кавычками — строка не закрыта.";
        if (ContainsAny(msg, "']',", "end of object", "'}'"))
            return "Не закрыта скобка } у объекта.";
        if (ContainsAny(msg, "']',", "end of array", "']'"))
            return "Не закрыта скобка ] у массива.";
        if (ContainsAny(msg, "true", "false", "null", "invalid"))
            return "Сломано значение (true/false/null/число/строка).";
        if (ContainsAny(msg, "depth"))
            return "Слишком глубокая вложенность.";
        return "Синтаксис JSON сломан. " + SoftEn(msg);
    }

    private static string ExplainYaml(string msg)
    {
        msg = msg ?? "";
        if (ContainsAny(msg, "simple key", "could not find expected", "incomplete explicit mapping", "mapping value"))
            return "После ключа нет двоеточия или значение записано криво. Нужно: «ключ: значение».";
        if (ContainsAny(msg, "document start"))
            return "С начала файла YAML не читается. Смотри первую не-комментарий строку: часто забыли «:».";
        if (ContainsAny(msg, "found character that cannot start", "tab", "\\t"))
            return "В отступах таб. В YAML отступ только пробелами.";
        if (ContainsAny(msg, "while parsing a block mapping", "expected <block end>", "bad indentation"))
            return "Сломаны отступы или рядом ключ без «:». Отступ — только пробелы.";
        if (ContainsAny(msg, "duplicate key"))
            return "Одинаковый ключ дважды в одном блоке.";
        if (ContainsAny(msg, "anchor", "alias"))
            return "Проблема с якорем/ссылкой (& / *).";
        return "Синтаксис YAML сломан. Проверь «ключ: значение» и отступы.";
    }

    private static string ExplainXml(string msg)
    {
        msg = msg ?? "";
        if (ContainsAny(msg, "end tag", "matching"))
            return "Не совпадают открывающий и закрывающий теги.";
        if (ContainsAny(msg, "not closed", "unclosed"))
            return "Тег не закрыт.";
        if (ContainsAny(msg, "invalid character", "name"))
            return "Недопустимый символ в имени тега или тексте.";
        if (ContainsAny(msg, "root"))
            return "Должен быть один корневой тег.";
        return "Синтаксис XML сломан. " + SoftEn(msg);
    }

    private static bool ContainsAny(string hay, params string[] needles)
    {
        foreach (var n in needles)
            if (hay.Contains(n, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Короткий хвост оригинала, если хочется деталей — без простыни.</summary>
    private static string SoftEn(string msg)
    {
        string t = TrimMsg(msg.Replace("\r", " ").Replace("\n", " "));
        return t.Length == 0 ? "" : $"({t})";
    }

    // ─── Pretty / Mini ────────────────────────────────────────

    private static TextOpResult Pretty(string path)
    {
        string text = ReadAll(path);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        string pretty = ext switch
        {
            ".json" => PrettyJson(text),
            ".yml" or ".yaml" => PrettyYaml(text),
            ".xml" or ".svg" or ".csproj" or ".config" => PrettyXml(text),
            _ => text // для прочего просто нормализуем переводы строк
                .Replace("\r\n", "\n").Replace('\r', '\n')
        };
        string dest = FileOutPath.NextFree(FileOutPath.WithSuffix(path, " красив"));
        WriteAll(dest, pretty);
        return new(true, "Готово: " + Path.GetFileName(dest), dest);
    }

    private static TextOpResult Minify(string path)
    {
        string text = ReadAll(path);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        string mini = ext switch
        {
            ".json" => MinifyJson(text),
            ".xml" or ".svg" => Regex.Replace(PrettyXml(text), @">\s+<", "><").Trim(),
            _ => Regex.Replace(text, @"[ \t]+\r?\n", "\n").Trim() + "\n"
        };
        string dest = FileOutPath.NextFree(FileOutPath.WithSuffix(path, " мини"));
        WriteAll(dest, mini);
        return new(true, "Готово: " + Path.GetFileName(dest), dest);
    }

    private static string PrettyJson(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string MinifyJson(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return JsonSerializer.Serialize(doc.RootElement);
    }

    private static string PrettyYaml(string text)
    {
        var obj = new DeserializerBuilder().Build().Deserialize<object>(text);
        return new SerializerBuilder().Build().Serialize(obj);
    }

    private static string PrettyXml(string text)
    {
        var doc = XDocument.Parse(text);
        return doc.Declaration != null
            ? doc.Declaration + Environment.NewLine + doc.ToString()
            : doc.ToString();
    }

    // ─── Convert ──────────────────────────────────────────────

    private static TextOpResult ConvertTo(string path, string newExt)
    {
        string text = ReadAll(path);
        string srcExt = Path.GetExtension(path).ToLowerInvariant();
        object? data = ParseLoose(text, srcExt);

        string outText = newExt.ToLowerInvariant() switch
        {
            ".json" => ToJson(data, text, srcExt),
            ".yml" or ".yaml" => ToYaml(data, text, srcExt),
            ".xml" => ToXml(data, text, srcExt),
            ".txt" => text.Replace("\r\n", "\n").Replace('\r', '\n'),
            _ => text
        };

        string dir = Path.GetDirectoryName(path) ?? "";
        string name = Path.GetFileNameWithoutExtension(path);
        string dest = FileOutPath.NextFree(Path.Combine(dir, name + newExt));
        WriteAll(dest, outText);
        return new(true, "Готово: " + Path.GetFileName(dest), dest);
    }

    private static object? ParseLoose(string text, string ext)
    {
        try
        {
            return ext switch
            {
                ".json" => JsonSerializer.Deserialize<JsonElement>(text),
                ".yml" or ".yaml" => new DeserializerBuilder().Build().Deserialize<object>(text),
                ".xml" or ".svg" or ".csproj" or ".config" => XDocument.Parse(text),
                ".properties" or ".props" or ".ini" or ".env" => ParseProps(text),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> ParseProps(string text)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';') || line.StartsWith('['))
                continue;
            int i = line.IndexOfAny(['=', ':']);
            if (i <= 0) continue;
            dict[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        return dict;
    }

    private static string ToJson(object? data, string raw, string srcExt)
    {
        if (data is JsonElement je)
            return JsonSerializer.Serialize(je, new JsonSerializerOptions { WriteIndented = true });
        if (data is IDictionary dict)
        {
            var map = new Dictionary<string, string>();
            foreach (DictionaryEntry e in dict)
                map[e.Key?.ToString() ?? ""] = e.Value?.ToString() ?? "";
            return JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true });
        }
        if (data is XDocument xd)
            return JsonSerializer.Serialize(XmlToMap(xd.Root), new JsonSerializerOptions { WriteIndented = true });
        if (data != null)
        {
            // YAML object → JSON через промежуточный YAML/JSON-совместимый дамп.
            string yaml = new SerializerBuilder().Build().Serialize(data);
            try
            {
                var again = new DeserializerBuilder().Build().Deserialize<object>(yaml);
                return JsonSerializer.Serialize(again, new JsonSerializerOptions { WriteIndented = true });
            }
            catch
            {
                return JsonSerializer.Serialize(data.ToString(), new JsonSerializerOptions { WriteIndented = true });
            }
        }

        // Fallback: lines as array
        var lines = raw.Replace("\r\n", "\n").Split('\n');
        return JsonSerializer.Serialize(lines, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string ToYaml(object? data, string raw, string srcExt)
    {
        if (data is JsonElement je)
        {
            using var doc = JsonDocument.Parse(je.GetRawText());
            object? obj = JsonElementToObject(doc.RootElement);
            return new SerializerBuilder().Build().Serialize(obj);
        }
        if (data is IDictionary dict)
        {
            var map = new Dictionary<string, string>();
            foreach (DictionaryEntry e in dict)
                map[e.Key?.ToString() ?? ""] = e.Value?.ToString() ?? "";
            return new SerializerBuilder().Build().Serialize(map);
        }
        if (data != null)
            return new SerializerBuilder().Build().Serialize(data);

        return new SerializerBuilder().Build().Serialize(new { text = raw });
    }

    private static string ToXml(object? data, string raw, string srcExt)
    {
        if (data is XDocument xd)
            return xd.ToString();
        if (data is JsonElement je)
            return JsonToXml(je).ToString();
        if (data is IDictionary dict)
        {
            var root = new XElement("root");
            foreach (DictionaryEntry e in dict)
                root.Add(new XElement(SafeXmlName(e.Key?.ToString() ?? "item"), e.Value?.ToString() ?? ""));
            return root.ToString();
        }
        return new XElement("root", new XElement("text", raw)).ToString();
    }

    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Object => el.EnumerateObject()
            .ToDictionary(p => p.Name, p => JsonElementToObject(p.Value)),
        JsonValueKind.Array => el.EnumerateArray().Select(JsonElementToObject).ToList(),
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out long l) ? l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    private static Dictionary<string, object?> XmlToMap(XElement? el)
    {
        var map = new Dictionary<string, object?>();
        if (el == null) return map;
        foreach (var child in el.Elements())
            map[child.Name.LocalName] = child.HasElements
                ? XmlToMap(child)
                : child.Value;
        return map;
    }

    private static XElement JsonToXml(JsonElement el, string name = "root")
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            var x = new XElement(SafeXmlName(name));
            foreach (var p in el.EnumerateObject())
                x.Add(JsonToXml(p.Value, p.Name));
            return x;
        }
        if (el.ValueKind == JsonValueKind.Array)
        {
            var x = new XElement(SafeXmlName(name));
            foreach (var item in el.EnumerateArray())
                x.Add(JsonToXml(item, "item"));
            return x;
        }
        return new XElement(SafeXmlName(name), el.ToString());
    }

    private static string SafeXmlName(string name)
    {
        name = Regex.Replace(name, @"[^a-zA-Z0-9_\-\.]", "_");
        if (name.Length == 0 || char.IsDigit(name[0]))
            name = "n_" + name;
        return name;
    }

    // ─── Line / text transforms ───────────────────────────────

    private static TextOpResult TransformLines(string path, string suffix, Func<List<string>, List<string>> fn)
    {
        string text = ReadAll(path);
        bool crlf = text.Contains("\r\n");
        string eol = crlf ? "\r\n" : "\n";
        bool endsWithNl = text.EndsWith('\n') || text.EndsWith("\r\n");
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        // Split сохраняет хвостовой "" при EndsWith('\n') — убираем, иначе Join+append дадут двойной EOL.
        if (endsWithNl && lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        var result = fn(lines);
        string joined = string.Join(eol, result);
        if (endsWithNl)
            joined += eol;
        string dest = FileOutPath.NextFree(FileOutPath.WithSuffix(path, suffix));
        WriteAll(dest, joined);
        return new(true, "Готово: " + Path.GetFileName(dest), dest);
    }

    private static TextOpResult TransformText(string path, string suffix, Func<string, string> fn)
    {
        string text = ReadAll(path);
        string dest = FileOutPath.NextFree(FileOutPath.WithSuffix(path, suffix));
        WriteAll(dest, fn(text));
        return new(true, "Готово: " + Path.GetFileName(dest), dest);
    }

    private static TextOpResult RewriteLineEnds(string path, string eol)
    {
        string text = ReadAll(path).Replace("\r\n", "\n").Replace('\r', '\n');
        string outText = text.Replace("\n", eol);
        string dest = FileOutPath.NextFree(FileOutPath.WithSuffix(path, eol == "\n" ? " LF" : " CRLF"));
        WriteAll(dest, outText);
        return new(true, "Готово: " + Path.GetFileName(dest), dest);
    }

    private static TextOpResult RewriteEncoding(string path, bool withBom)
    {
        string text = ReadAll(path);
        string dest = FileOutPath.NextFree(FileOutPath.WithSuffix(path, withBom ? " BOM" : " UTF8"));
        var enc = new UTF8Encoding(encoderShouldEmitUTF8Identifier: withBom);
        File.WriteAllText(dest, text, enc);
        return new(true, "Готово: " + Path.GetFileName(dest), dest);
    }

    private static TextOpResult Base64Smart(string path)
    {
        string text = ReadAll(path).Trim();
        string dest;
        string outText;
        if (IsProbablyBase64(text))
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(Regex.Replace(text, @"\s+", ""));
                outText = Encoding.UTF8.GetString(bytes);
                dest = FileOutPath.NextFree(FileOutPath.WithSuffix(path, " decode"));
            }
            catch
            {
                outText = Convert.ToBase64String(Encoding.UTF8.GetBytes(ReadAll(path)));
                dest = FileOutPath.NextFree(FileOutPath.WithSuffix(path, " b64"));
            }
        }
        else
        {
            outText = Convert.ToBase64String(Encoding.UTF8.GetBytes(ReadAll(path)));
            dest = FileOutPath.NextFree(FileOutPath.WithSuffix(path, " b64"));
        }
        WriteAll(dest, outText);
        return new(true, "Готово: " + Path.GetFileName(dest), dest);
    }

    private static bool IsProbablyBase64(string text)
    {
        string t = Regex.Replace(text, @"\s+", "");
        return t.Length >= 8 && t.Length % 4 == 0 && Regex.IsMatch(t, @"^[A-Za-z0-9+/]*={0,2}$");
    }

    private static TextOpResult WriteHash(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string dest = FileOutPath.NextFree(path + ".sha256");
        File.WriteAllText(dest, hash + "  " + Path.GetFileName(path) + Environment.NewLine, Encoding.UTF8);
        return new(true, "SHA256: " + hash[..16] + "…", dest);
    }

    /// <summary>Только с UI/STA-потока (TextMenuWindow вызывает copy на Dispatcher).</summary>
    private static TextOpResult CopyToClipboard(string path)
    {
        string text = ReadAll(path);
        System.Windows.Clipboard.SetText(text);
        return new(true, $"Скопировано · {text.Length} символов");
    }

    private static TextOpResult Stats(string path)
    {
        string text = ReadAll(path);
        long size = new FileInfo(path).Length;
        int lines = CountLines(text);
        int chars = text.Length;
        int words = Regex.Matches(text, @"\S+").Count;
        string sizeStr = size < 1024 ? $"{size} Б"
            : size < 1024 * 1024 ? $"{size / 1024.0:0.#} КБ"
            : $"{size / (1024.0 * 1024):0.##} МБ";
        return new(true, $"{lines} стр · {words} сл · {chars} симв · {sizeStr}");
    }

    // ─── IO helpers ───────────────────────────────────────────

    private static string ReadAll(string path)
    {
        // Автоопределение: UTF-8 / UTF-8 BOM / UTF-16 / default
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }

    private static void WriteAll(string path, string text) =>
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private static int CountLines(string text)
    {
        if (text.Length == 0) return 0;
        int n = 1;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n') n++;
        return n;
    }

    private static bool LooksBinary(string text)
    {
        int check = Math.Min(text.Length, 2000);
        int nul = 0;
        for (int i = 0; i < check; i++)
            if (text[i] == '\0') nul++;
        return nul > 0;
    }

    private static string TrimMsg(string m) => m.Length <= 120 ? m : m[..117] + "…";
    private static string Trunc(string m, int n) => m.Length <= n ? m : m[..n] + "…";
}
