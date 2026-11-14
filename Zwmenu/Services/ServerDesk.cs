using System.Text.RegularExpressions;

namespace Zwmenu.Services;

/// <summary>Команды сервера через уже сохранённый RCON. Свободная строка не уходит: только известные действия.</summary>
public static class ServerDesk
{
    static readonly Regex Nick = new(@"^[A-Za-z0-9_\.]{2,16}$", RegexOptions.Compiled);
    static readonly Regex Give = new(@"^(?:minecraft:)?give\s+\S+\s+\S+(?:\s+\d{1,4})?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public const string DefaultKit =
        "minecraft:give @p minecraft:bread 16\n" +
        "minecraft:give @p minecraft:cooked_beef 8\n" +
        "minecraft:give @p minecraft:torch 32\n" +
        "minecraft:give @p minecraft:oak_log 16\n" +
        "minecraft:give @p minecraft:iron_pickaxe 1";

    public static string? NickProblem(string nick)
    {
        nick = (nick ?? "").Trim();
        if (!Nick.IsMatch(nick))
            return "Ник: буквы, цифры, _ и точка, от 2 до 16.";
        return null;
    }

    public static string Whitelist(string nick) => "whitelist add " + nick.Trim();

    public static string Kick(string nick, string reason)
    {
        string why = CleanReason(reason);
        return why.Length == 0 ? "kick " + nick.Trim() : "kick " + nick.Trim() + " " + why;
    }

    public static string Ban(string nick, string reason)
    {
        string why = CleanReason(reason);
        return why.Length == 0 ? "ban " + nick.Trim() : "ban " + nick.Trim() + " " + why;
    }

    public static string? TimeCommand(string which) => which switch
    {
        "day" => "time set day",
        "night" => "time set night",
        _ => null
    };

    public static string? WeatherCommand(string which) => which switch
    {
        "clear" => "weather clear",
        "rain" => "weather rain",
        _ => null
    };

    public static string Say(string text)
    {
        string clean = CleanReason(text);
        if (clean.Length > 200)
            clean = clean[..200];
        return "say " + clean;
    }

    public static string? KitProblem(string text, out List<string> commands)
    {
        commands = new List<string>();
        foreach (string raw in (text ?? "").Split('\n', '\r'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
                continue;
            if (line.Contains(';') || line.Contains('|') || !Give.IsMatch(line))
                return "В наборе только строки give. Эта не подошла: " + line;
            commands.Add(line);
            if (commands.Count > 20)
                return "В наборе больше 20 строк.";
        }

        if (commands.Count == 0)
            return "Набор пустой.";
        return null;
    }

    public static Task<(bool Ok, string Text)> RunAsync(string command) 
    {
        var server = SnippetStore.Server;
        if (string.IsNullOrWhiteSpace(server.Host) || string.IsNullOrWhiteSpace(server.Password))
            return Task.FromResult((false, "В шаблонах не задан адрес или пароль RCON."));
        if (string.IsNullOrWhiteSpace(command))
            return Task.FromResult((false, "Пустая команда."));
        return RconClient.ExecuteAsync(server.Host, server.Port <= 0 ? 25575 : server.Port, server.Password, command.Trim());
    }

    public static async Task<(bool Ok, string Text)> RunAllAsync(IReadOnlyList<string> commands)
    {
        var lines = new List<string>();
        foreach (string command in commands)
        {
            var (ok, text) = await RunAsync(command).ConfigureAwait(false);
            lines.Add(command + ": " + text);
            if (!ok)
                return (false, string.Join("\n", lines));
        }

        return (true, string.Join("\n", lines));
    }

    public static void SelfCheck()
    {
        if (NickProblem("Steve") != null)
            throw new InvalidOperationException("Steve");
        if (NickProblem("bad nick") == null || NickProblem("a;kick") == null || NickProblem("x") == null)
            throw new InvalidOperationException("ник пропустил мусор");
        if (Kick("Steve", "отошёл\nвсё") != "kick Steve отошёл всё")
            throw new InvalidOperationException(Kick("Steve", "отошёл\nвсё"));
        if (Ban("Steve", "") != "ban Steve")
            throw new InvalidOperationException("ban");
        if (TimeCommand("day") != "time set day" || WeatherCommand("rain") != "weather rain")
            throw new InvalidOperationException("погода");
        if (TimeCommand("explode") != null)
            throw new InvalidOperationException("чужое время");
        if (Say("привет \"всем\"").Contains('"') || Say("a\nb").Contains('\n'))
            throw new InvalidOperationException("say");
        string? bad = KitProblem("minecraft:give @p minecraft:stone 1; stop", out _);
        if (bad == null)
            throw new InvalidOperationException("набор пропустил stop");
        string? ok = KitProblem(DefaultKit, out var commands);
        if (ok != null || commands.Count != 5)
            throw new InvalidOperationException(ok ?? "набор");
    }

    static string CleanReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "";
        var chars = reason.Where(c => c != '"' && c != '\\' && c != ';').ToArray();
        string text = new string(chars);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 48 && reason.Length > 80 ? text[..48] : text.Length > 200 ? text[..200] : text;
    }
}
