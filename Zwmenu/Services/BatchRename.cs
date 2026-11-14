using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zwmenu.Services;

public enum RenameOrder
{
    Name,
    Modified,
    AsGiven
}

public sealed class RenameRequest
{
    public string Pattern { get; init; } = "";
    public int Start { get; init; } = 1;
    public int Step { get; init; } = 1;
    public int Pad { get; init; }
    public bool KeepExtension { get; init; } = true;
    public RenameOrder Order { get; init; } = RenameOrder.Name;
}

public sealed class RenameRow
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string OldName { get; init; } = "";
    public string NewName { get; init; } = "";
    public string? Problem { get; init; }
    public bool Same { get; init; }
}

public sealed class RenamePreview
{
    public string? Problem { get; init; }
    public List<RenameRow> Rows { get; init; } = new();

    public bool CanApply =>
        Problem == null
        && Rows.Count > 0
        && Rows.All(r => r.Problem == null)
        && Rows.Any(r => !r.Same);
}

/// <summary>
/// Пачечное имя: шаблон, предпросмотр, обмен имён без столкновения, один откат.
/// «Серия 1» без скобок само становится рядом. Скобки дают {n} {n:00} {name} {stem} {date}.
/// </summary>
public static class BatchRename
{
    const int MaxFiles = 500;
    static readonly Regex Token = new(@"\{(n|name|stem|ext|date)(?::(0+))?\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex TrailingNumber = new(@"^(.*?)(\d+)\s*$", RegexOptions.Compiled);
    static readonly Regex StemTail = new(@"[\s._-]*\d+$", RegexOptions.Compiled);
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static List<UndoPair>? _undo;
    static string? _undoOverride;

    public static bool CanUndo
    {
        get
        {
            EnsureUndo();
            return _undo is { Count: > 0 };
        }
    }

    public static RenamePreview Preview(IReadOnlyList<string> files, RenameRequest request)
    {
        var clean = Dedupe(files);
        if (clean.Count == 0)
            return new RenamePreview { Problem = "Нет файлов." };
        if (clean.Count > MaxFiles)
            return new RenamePreview { Problem = "Больше " + MaxFiles + " файлов за раз не беру." };

        string pattern = (request.Pattern ?? "").Trim();
        if (pattern.Length == 0)
            return new RenamePreview { Problem = "Напиши имя или шаблон." };
        if (pattern.Length > 180)
            return new RenamePreview { Problem = "Шаблон длиннее 180 знаков." };
        if (pattern.IndexOfAny(new[] { '/', '\\' }) >= 0)
            return new RenamePreview { Problem = "В шаблоне нельзя папку. Файлы остаются где лежат." };
        if (request.Step == 0)
            return new RenamePreview { Problem = "Шаг не может быть нулём." };
        if (request.Start < 0 || request.Start > 1_000_000)
            return new RenamePreview { Problem = "Начало номера вне диапазона." };

        var ordered = Order(clean, request.Order);
        bool tokens = pattern.Contains('{');
        var rows = new List<RenameRow>(ordered.Count);
        var taken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < ordered.Count; i++)
        {
            string from = ordered[i];
            int number = request.Start + i * request.Step;
            rows.Add(BuildRow(from, pattern, tokens, number, request, ordered, taken));
        }

        return new RenamePreview { Rows = rows };
    }

    public static (int Done, string? Error) Apply(IReadOnlyList<string> files, RenameRequest request)
    {
        var preview = Preview(files, request);
        if (preview.Problem != null)
            return (0, preview.Problem);
        if (!preview.CanApply)
        {
            var bad = preview.Rows.FirstOrDefault(r => r.Problem != null);
            return (0, bad?.Problem ?? "Нечего менять.");
        }

        var work = preview.Rows.Where(r => !r.Same).ToList();
        var temps = new List<Hop>(work.Count);
        string token = Guid.NewGuid().ToString("N")[..8];
        int staged = 0;
        try
        {
            for (int i = 0; i < work.Count; i++)
            {
                var row = work[i];
                string dir = Path.GetDirectoryName(row.From) ?? "";
                string ext = Path.GetExtension(row.From);
                string temp = Path.Combine(dir, ".zwrename-" + token + "-" + i + ext);
                File.Move(row.From, temp);
                temps.Add(new Hop(row.From, temp, row.To));
                staged++;
            }

            for (int i = 0; i < temps.Count; i++)
            {
                File.Move(temps[i].Temp, temps[i].Final);
                staged++;
            }
        }
        catch (Exception ex)
        {
            Rollback(temps, staged);
            return (0, "Не переименовал, вернул как было. " + ex.Message);
        }

        var pairs = work.Select(r => new UndoPair { Now = r.To, Was = r.From }).ToList();
        SaveUndo(pairs);
        return (work.Count, null);
    }

    public static (int Done, string? Error) Undo()
    {
        EnsureUndo();
        if (_undo == null || _undo.Count == 0)
            return (0, "Откатывать нечего.");

        var pairs = _undo
            .Where(p => !string.IsNullOrWhiteSpace(p.Now) && !string.IsNullOrWhiteSpace(p.Was))
            .ToList();
        var temps = new List<Hop>(pairs.Count);
        string token = Guid.NewGuid().ToString("N")[..8];
        int staged = 0;
        var missing = new List<string>();
        try
        {
            for (int i = 0; i < pairs.Count; i++)
            {
                if (!File.Exists(pairs[i].Now))
                {
                    missing.Add(Path.GetFileName(pairs[i].Now));
                    continue;
                }

                string dir = Path.GetDirectoryName(pairs[i].Now) ?? "";
                string ext = Path.GetExtension(pairs[i].Now);
                string temp = Path.Combine(dir, ".zwrename-" + token + "-" + i + ext);
                File.Move(pairs[i].Now, temp);
                temps.Add(new Hop(pairs[i].Now, temp, pairs[i].Was));
                staged++;
            }

            for (int i = 0; i < temps.Count; i++)
            {
                if (File.Exists(temps[i].Final))
                    throw new IOException("Имя «" + Path.GetFileName(temps[i].Final) + "» уже занято.");
                File.Move(temps[i].Temp, temps[i].Final);
                staged++;
            }
        }
        catch (Exception ex)
        {
            Rollback(temps, staged);
            return (0, "Откат остановился. " + ex.Message);
        }

        SaveUndo(new List<UndoPair>());
        string extra = missing.Count == 0 ? "" : " Не нашёл: " + string.Join(", ", missing.Take(4)) + ".";
        return (temps.Count, temps.Count == 0 ? "Файлы уже не на тех именах." + extra : null);
    }

    public static void SelfCheck()
    {
        string dir = Path.Combine(Path.GetTempPath(), "zwrename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string? undoWas = _undoOverride;
        _undoOverride = Path.Combine(dir, "undo.json");
        _undo = new List<UndoPair>();
        try
        {
            string a = Path.Combine(dir, "b.txt");
            string b = Path.Combine(dir, "a.txt");
            File.WriteAllText(a, "b");
            File.WriteAllText(b, "a");
            var series = Preview(new[] { a, b }, new RenameRequest { Pattern = "Серия 1", Start = 1, Step = 1 });
            Expect(series.CanApply, "серия должна применяться");
            Expect(series.Rows[0].NewName == "Серия 1.txt", "первым по имени идёт a: " + series.Rows[0].NewName);
            Expect(series.Rows[1].NewName == "Серия 2.txt", "вторым идёт b: " + series.Rows[1].NewName);

            var padded = Preview(new[] { a }, new RenameRequest { Pattern = "Серия 01", Start = 5, Pad = 0 });
            Expect(padded.Rows[0].NewName == "Серия 05.txt", "нули из примера: " + padded.Rows[0].NewName);

            var one = Preview(new[] { a }, new RenameRequest { Pattern = "Итог" });
            Expect(one.Rows[0].NewName == "Итог.txt", "один файл без номера: " + one.Rows[0].NewName);

            var many = Preview(new[] { a, b }, new RenameRequest { Pattern = "Итог", Start = 1 });
            Expect(many.Rows[0].NewName == "Итог 1.txt", "пачка без номера: " + many.Rows[0].NewName);

            var tokens = Preview(new[] { a, b }, new RenameRequest { Pattern = "{name}-{n:00}", Start = 3, Step = 2 });
            Expect(tokens.Rows[0].NewName == "a-03.txt", "шаблон: " + tokens.Rows[0].NewName);
            Expect(tokens.Rows[1].NewName == "b-05.txt", "шаг 2: " + tokens.Rows[1].NewName);

            string png = Path.Combine(dir, "Серия 3.png");
            File.WriteAllText(png, "img");
            var stem = Preview(new[] { png }, new RenameRequest { Pattern = "{stem}-{n:00}", Start = 1 });
            Expect(stem.Rows[0].NewName == "Серия-01.png", "stem: " + stem.Rows[0].NewName);
            var extName = Preview(new[] { png }, new RenameRequest { Pattern = "{name}-копия.{ext}", KeepExtension = false });
            Expect(extName.Rows[0].NewName == "Серия 3-копия.png", "ext: " + extName.Rows[0].NewName);

            File.WriteAllText(Path.Combine(dir, "занято.txt"), "x");
            var clash = Preview(new[] { a }, new RenameRequest { Pattern = "занято" });
            Expect(clash.Rows[0].Problem != null, "чужое имя должно быть ошибкой");

            string x = Path.Combine(dir, "x.txt");
            string y = Path.Combine(dir, "y.txt");
            File.WriteAllText(x, "x");
            File.WriteAllText(y, "y");
            var (done, err) = Apply(new[] { x, y }, new RenameRequest { Pattern = "пара {n}", Start = 1, Order = RenameOrder.AsGiven });
            Expect(err == null, err ?? "");
            Expect(done == 2, "оба должны переименоваться");
            Expect(File.Exists(Path.Combine(dir, "пара 1.txt")), "пара 1");
            Expect(File.ReadAllText(Path.Combine(dir, "пара 1.txt")) == "x", "содержимое x уехало с именем");
            var (back, undoErr) = Undo();
            Expect(undoErr == null, undoErr ?? "");
            Expect(back == 2, "откат обоих");
            Expect(File.Exists(x) && File.ReadAllText(x) == "x", "x вернулся");
            Expect(!Directory.EnumerateFiles(dir, ".zwrename-*").Any(), "временные имена не остались");

            string leftFile = Path.Combine(dir, "1.txt");
            string rightFile = Path.Combine(dir, "2.txt");
            File.WriteAllText(leftFile, "one");
            File.WriteAllText(rightFile, "two");
            var (swapped, swapErr) = Apply(new[] { leftFile, rightFile }, new RenameRequest
            {
                Pattern = "{n}",
                Start = 2,
                Step = -1,
                Order = RenameOrder.Name
            });
            Expect(swapErr == null, swapErr ?? "");
            Expect(swapped == 2, "обмен");
            Expect(File.ReadAllText(leftFile) == "two", "после обмена в 1 лежит two");
            Expect(File.ReadAllText(rightFile) == "one", "после обмена в 2 лежит one");
            Expect(!Directory.EnumerateFiles(dir, ".zwrename-*").Any(), "после обмена временных нет");

            var bad = Preview(new[] { a }, new RenameRequest { Pattern = "a/b" });
            Expect(bad.Problem != null, "слэш запрещён");
            var zero = Preview(new[] { a }, new RenameRequest { Pattern = "Серия {n}", Step = 0 });
            Expect(zero.Problem != null, "нулевой шаг");
            var folder = Directory.CreateDirectory(Path.Combine(dir, "папка")).FullName;
            var asDir = Preview(new[] { folder }, new RenameRequest { Pattern = "Серия {n}" });
            Expect(asDir.Rows[0].Problem != null, "папка не файл");
        }
        finally
        {
            _undoOverride = undoWas;
            _undo = null;
            try { Directory.Delete(dir, true); } catch { /* проверка закончена */ }
        }
    }

    static void Expect(bool ok, string message)
    {
        if (!ok)
            throw new InvalidOperationException(message);
    }

    static RenameRow BuildRow(
        string from,
        string pattern,
        bool tokens,
        int number,
        RenameRequest request,
        IReadOnlyList<string> sources,
        Dictionary<string, string> taken)
    {
        string oldName = Path.GetFileName(from);
        if (Directory.Exists(from))
            return Problem(from, oldName, "Это папка. Нужны файлы внутри.");
        if (!File.Exists(from))
            return Problem(from, oldName, "Файла уже нет.");

        string stem = Path.GetFileNameWithoutExtension(from);
        string ext = Path.GetExtension(from);
        string produced = tokens
            ? ApplyTokens(pattern, number, request.Pad, stem, ext, File.GetLastWriteTime(from))
            : PlainName(pattern, number, request.Pad, sources.Count);

        produced = produced.Trim();
        bool hasExtToken = tokens && pattern.Contains("{ext}", StringComparison.OrdinalIgnoreCase);
        if (request.KeepExtension)
        {
            string cut = Path.GetFileNameWithoutExtension(produced);
            if (cut.Length == 0)
                cut = produced;
            produced = cut + ext;
        }
        else if (!hasExtToken && Path.GetExtension(produced).Length == 0)
        {
            produced += ext;
        }

        produced = produced.Trim().TrimEnd('.');
        string? problem = NameProblem(produced);
        if (problem != null)
            return Problem(from, oldName, problem);
        if (number < 0)
            return Problem(from, oldName, "Номер ушёл ниже нуля. Подними начало или шаг.");

        string dir = Path.GetDirectoryName(from) ?? "";
        string to = Path.Combine(dir, produced);
        if (to.Length > 240)
            return Problem(from, oldName, "Путь длиннее 240 знаков.");

        string key = to;
        if (taken.TryGetValue(key, out string? other))
            return Problem(from, oldName, "То же имя, что у «" + Path.GetFileName(other) + "».");
        taken[key] = from;

        bool same = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
        bool ours = sources.Any(s => string.Equals(s, to, StringComparison.OrdinalIgnoreCase));
        if (!same && File.Exists(to) && !ours)
            return Problem(from, oldName, "«" + produced + "» уже есть в папке.");

        return new RenameRow
        {
            From = from,
            To = to,
            OldName = oldName,
            NewName = produced,
            Same = same
        };
    }

    static string PlainName(string pattern, int number, int pad, int count)
    {
        string prefix = pattern;
        int width = pad > 0 ? pad : 1;
        var match = TrailingNumber.Match(pattern);
        if (match.Success)
        {
            prefix = match.Groups[1].Value;
            if (pad < 1)
                width = match.Groups[2].Value.Length;
        }

        bool numbered = count > 1 || match.Success;
        if (!numbered)
            return prefix;

        if (!match.Success && prefix.Length > 0 && char.IsLetter(prefix[^1]))
            prefix += " ";
        return prefix + number.ToString(new string('0', Math.Clamp(width, 1, 8)), CultureInfo.InvariantCulture);
    }

    static string ApplyTokens(string pattern, int number, int pad, string name, string ext, DateTime when)
    {
        return Token.Replace(pattern, m =>
        {
            int width = m.Groups[2].Success ? m.Groups[2].Value.Length : Math.Clamp(Math.Max(pad, 1), 1, 8);
            return m.Groups[1].Value.ToLowerInvariant() switch
            {
                "n" => number.ToString(new string('0', width), CultureInfo.InvariantCulture),
                "name" => name,
                "stem" => SeriesStem(name),
                "ext" => ext.TrimStart('.'),
                "date" => when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                _ => m.Value
            };
        });
    }

    public static string SeriesStem(string name)
    {
        string cut = StemTail.Replace(name, "");
        return cut.Length == 0 ? name : cut.Trim();
    }

    static string? NameProblem(string name)
    {
        if (name.Length == 0 || name == "." || name == "..")
            return "Пустое имя.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "В имени нельзя \\ / : * ? \" < > |.";
        string bare = Path.GetFileNameWithoutExtension(name);
        if (Reserved.Contains(bare))
            return "«" + bare + "» зарезервировано Windows.";
        return null;
    }

    static RenameRow Problem(string from, string oldName, string problem) =>
        new() { From = from, OldName = oldName, NewName = "", Problem = problem };

    static List<string> Dedupe(IReadOnlyList<string> files)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in files)
        {
            string path = (raw ?? "").Trim().Trim('"');
            if (path.Length == 0 || !seen.Add(path))
                continue;
            list.Add(path);
        }

        return list;
    }

    static List<string> Order(List<string> files, RenameOrder order)
    {
        return order switch
        {
            RenameOrder.AsGiven => files,
            RenameOrder.Modified => files
                .OrderBy(Stamp)
                .ThenBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _ => files
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    static DateTime Stamp(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    static void Rollback(List<Hop> temps, int staged)
    {
        int finished = Math.Max(0, staged - temps.Count);
        for (int i = finished - 1; i >= 0; i--)
        {
            try
            {
                if (File.Exists(temps[i].Final) && !File.Exists(temps[i].Temp))
                    File.Move(temps[i].Final, temps[i].Temp);
            }
            catch { /* дальше всё равно вернём что сможем */ }
        }

        foreach (var hop in temps)
        {
            try
            {
                if (File.Exists(hop.Temp) && !File.Exists(hop.Original))
                    File.Move(hop.Temp, hop.Original);
            }
            catch { /* имя останется временным, это видно в папке */ }
        }
    }

    static void EnsureUndo()
    {
        if (_undo != null)
            return;
        try
        {
            string path = UndoPath();
            if (!File.Exists(path))
            {
                _undo = new List<UndoPair>();
                return;
            }

            _undo = JsonSerializer.Deserialize<List<UndoPair>>(File.ReadAllText(path)) ?? new List<UndoPair>();
        }
        catch
        {
            _undo = new List<UndoPair>();
        }
    }

    static void SaveUndo(List<UndoPair> pairs)
    {
        _undo = pairs;
        try
        {
            string path = UndoPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(pairs, Json));
        }
        catch { /* откат в этой сессии всё равно в памяти */ }
    }

    static string UndoPath()
    {
        if (!string.IsNullOrWhiteSpace(_undoOverride))
            return _undoOverride;
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zwmenu");
        return Path.Combine(root, "rename-undo.json");
    }

    readonly record struct Hop(string Original, string Temp, string Final);

    sealed class UndoPair
    {
        public string Now { get; set; } = "";
        public string Was { get; set; } = "";
    }
}
