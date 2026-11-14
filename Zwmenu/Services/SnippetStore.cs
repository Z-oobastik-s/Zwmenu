using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using Zwmenu.Overlay;

namespace Zwmenu.Services;

public sealed class Snippet
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("hotkey")] public string Hotkey { get; set; } = "";
    [JsonPropertyName("hotkey_action")] public string HotkeyAction { get; set; } = "copy";
}

public sealed class LinkItem
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("hotkey")] public string Hotkey { get; set; } = "";
}

public sealed class ServerCommand
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("command")] public string Command { get; set; } = "";
}

public sealed class ServerSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 25575;
    public string Password { get; set; } = "";
    public string WebUrl { get; set; } = "";
    public string WindowHotkey { get; set; } = "numdel";
    public List<ServerCommand> Commands { get; set; } = new();
}

public readonly record struct HotkeyHit(string Kind, int Id);

/// <summary>
/// Шаблоны, ссылки и команды сервера из прежней программы.
/// Файлы лежат в профиле и не затираются установщиком.
/// </summary>
public static class SnippetStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true
    };

    private static List<Snippet> _snippets = new();
    private static List<LinkItem> _links = new();
    private static ServerSettings _server = new();
    private static bool _loaded;

    public static bool SuspendHotkeys { get; set; }

    public static IReadOnlyList<Snippet> Snippets
    {
        get { lock (Gate) return _snippets.ToList(); }
    }

    public static IReadOnlyList<LinkItem> Links
    {
        get { lock (Gate) return _links.ToList(); }
    }

    public static ServerSettings Server
    {
        get { lock (Gate) return Clone(_server); }
    }

    public static void Load()
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Dir());
            _snippets = LoadList<Snippet>(Path.Combine(Dir(), "snippets.json"), "Zwmenu.snippets.seed.json", "snippets.json");
            _links = LoadList<LinkItem>(Path.Combine(Dir(), "links.json"), "Zwmenu.links.seed.json", "ai_portal.json");
            _server = LoadServer();
            _loaded = true;
        }
    }

    public static void SaveSnippets(IEnumerable<Snippet> items)
    {
        lock (Gate)
        {
            Ensure();
            _snippets = items.Select(Clone).ToList();
            Write(Path.Combine(Dir(), "snippets.json"), _snippets);
        }
    }

    public static void SaveLinks(IEnumerable<LinkItem> items)
    {
        lock (Gate)
        {
            Ensure();
            _links = items.Select(Clone).ToList();
            Write(Path.Combine(Dir(), "links.json"), _links);
        }
    }

    public static void SaveServer(ServerSettings settings)
    {
        lock (Gate)
        {
            Ensure();
            _server = Clone(settings);
            Write(Path.Combine(Dir(), "server.json"), _server);
        }
    }

    public static bool TryHit(int vk, out HotkeyHit hit)
    {
        hit = default;
        if (vk <= 0)
            return false;
        lock (Gate)
        {
            Ensure();
            foreach (var snippet in _snippets)
            {
                if (!ChordMatches(snippet.Hotkey, vk))
                    continue;
                hit = new HotkeyHit("snippet", snippet.Id);
                return true;
            }

            foreach (var link in _links)
            {
                if (!ChordMatches(link.Hotkey, vk))
                    continue;
                hit = new HotkeyHit("link", link.Id);
                return true;
            }

            if (ChordMatches(_server.WindowHotkey, vk))
            {
                hit = new HotkeyHit("window", 0);
                return true;
            }
        }

        return false;
    }

    public static void Perform(HotkeyHit hit)
    {
        if (hit.Kind == "window")
        {
            SnippetWindow.Toggle();
            return;
        }

        if (hit.Kind == "link")
        {
            LinkItem? link;
            lock (Gate)
                link = _links.FirstOrDefault(item => item.Id == hit.Id);
            if (link == null || string.IsNullOrWhiteSpace(link.Url))
                return;
            OpenUrl(link.Url);
            Toast("Открыто: " + link.Title);
            return;
        }

        Snippet? snippet;
        lock (Gate)
            snippet = _snippets.FirstOrDefault(item => item.Id == hit.Id);
        if (snippet == null)
            return;
        bool paste = string.Equals(snippet.HotkeyAction, "paste", StringComparison.OrdinalIgnoreCase)
            && !SnippetWindow.IsEditing;
        PutClipboard(Expand(snippet.Text), paste);
        Toast(paste ? "Вставлено: " + snippet.Title : "Скопировано: " + snippet.Title);
    }

    public static void PutClipboard(string text, bool paste, int pasteDelayMs = 50)
    {
        try
        {
            System.Windows.Clipboard.SetText(text ?? "");
        }
        catch
        {
            return;
        }

        if (!paste)
            return;
        int delay = Math.Clamp(pasteDelayMs, 0, 2000);
        _ = Task.Run(async () =>
        {
            if (delay > 0)
                await Task.Delay(delay).ConfigureAwait(false);
            SendCtrlV();
        });
    }

    public static string Expand(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        return text.Replace("\\r\\n", "\n", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\r", "\n", StringComparison.Ordinal)
            .Replace("\\t", "\t", StringComparison.Ordinal);
    }

    public static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch
        {
            // Браузер не открылся. Окно покажет это само, если вызывали оттуда.
        }
    }

    public static bool TryParseChord(string? raw, out bool ctrl, out bool alt, out bool shift, out bool win, out int vk)
    {
        ctrl = alt = shift = win = false;
        vk = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        foreach (string part in raw.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string token = part.ToLowerInvariant();
            if (token is "ctrl" or "control")
                ctrl = true;
            else if (token == "alt")
                alt = true;
            else if (token == "shift")
                shift = true;
            else if (token is "win" or "windows")
                win = true;
            else if (!TryToken(token, out vk))
                return false;
        }

        return vk != 0;
    }

    public static string FormatChord(bool ctrl, bool alt, bool shift, bool win, int vk)
    {
        var parts = new List<string>();
        if (ctrl) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        if (win) parts.Add("Win");
        parts.Add(TokenOf(vk));
        return string.Join("+", parts);
    }

    public static string Canonical(bool ctrl, bool alt, bool shift, bool win, int vk)
    {
        var parts = new List<string>();
        if (ctrl) parts.Add("ctrl");
        if (alt) parts.Add("alt");
        if (shift) parts.Add("shift");
        if (win) parts.Add("win");
        parts.Add(TokenOf(vk).ToLowerInvariant());
        return string.Join("+", parts);
    }

    private static bool ChordMatches(string? raw, int vk)
    {
        if (!TryParseChord(raw, out bool ctrl, out bool alt, out bool shift, out bool win, out int want))
            return false;
        if (want != vk)
            return false;
        return Down(0x11) == ctrl && Down(0x12) == alt && Down(0x10) == shift && (Down(0x5B) || Down(0x5C)) == win;
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static void Ensure()
    {
        if (!_loaded)
            Load();
    }

    private static List<T> LoadList<T>(string path, string seedName, string legacyName)
    {
        if (File.Exists(path))
        {
            var existing = Read<List<T>>(path);
            if (existing != null)
                return existing;
        }

        string? live = LegacyFile(legacyName);
        if (live != null)
        {
            var imported = Read<List<T>>(live);
            if (imported != null)
            {
                Write(path, imported);
                return imported;
            }
        }

        var seed = ReadSeed<List<T>>(seedName) ?? new List<T>();
        Write(path, seed);
        return seed;
    }

    private static ServerSettings LoadServer()
    {
        string path = Path.Combine(Dir(), "server.json");
        if (File.Exists(path))
        {
            var existing = Read<ServerSettings>(path);
            if (existing != null)
            {
                existing.Commands ??= new List<ServerCommand>();
                return existing;
            }
        }

        var settings = new ServerSettings
        {
            Commands = ReadSeed<List<ServerCommand>>("Zwmenu.commands.seed.json") ?? new List<ServerCommand>()
        };
        ImportLegacyServer(settings);
        if (settings.Commands.Count == 0)
            settings.Commands = ReadSeed<List<ServerCommand>>("Zwmenu.commands.seed.json") ?? new List<ServerCommand>();
        Write(path, settings);
        return settings;
    }

    private static void ImportLegacyServer(ServerSettings settings)
    {
        foreach (string dir in LegacyDirs())
        {
            string file = Path.Combine(dir, "settings.json");
            if (!File.Exists(file))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                if (root.TryGetProperty("rcon_host", out var host) && host.GetString() is string h && h.Length > 0)
                    settings.Host = h;
                if (root.TryGetProperty("rcon_port", out var port) && port.TryGetInt32(out int p) && p > 0)
                    settings.Port = p;
                if (root.TryGetProperty("rcon_password", out var password) && password.GetString() is string pass)
                    settings.Password = pass;
                if (root.TryGetProperty("web_console_url", out var web) && web.GetString() is string url && url.Length > 0)
                    settings.WebUrl = url;
                if (root.TryGetProperty("window_toggle_hotkey", out var hotkey) && hotkey.GetString() is string hk && hk.Length > 0)
                    settings.WindowHotkey = hk;
                string commands = Path.Combine(dir, "rcon_commands.json");
                if (File.Exists(commands))
                {
                    var rows = Read<List<ServerCommand>>(commands);
                    if (rows is { Count: > 0 })
                        settings.Commands = rows;
                }

                return;
            }
            catch
            {
                // Битый файл старой программы не мешает семени.
            }
        }
    }

    private static string? LegacyFile(string name)
    {
        foreach (string dir in LegacyDirs())
        {
            string path = Path.Combine(dir, name);
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    private static IEnumerable<string> LegacyDirs()
    {
        yield return @"E:\рабочий стол\Program Text Buffer\dist";
        yield return @"E:\рабочий стол\Program Text Buffer";
    }

    private static T? ReadSeed<T>(string name)
    {
        var stream = typeof(SnippetStore).Assembly.GetManifestResourceStream(name);
        if (stream == null)
            return default;
        using (stream)
            return JsonSerializer.Deserialize<T>(stream, Json);
    }

    private static T? Read<T>(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
        }
        catch
        {
            return default;
        }
    }

    private static void Write<T>(string path, T value)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, path, overwrite: true);
    }

    private static string Dir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zwmenu");

    private static Snippet Clone(Snippet item) => new()
    {
        Id = item.Id,
        Title = item.Title,
        Text = item.Text,
        Hotkey = item.Hotkey,
        HotkeyAction = item.HotkeyAction
    };

    private static LinkItem Clone(LinkItem item) => new()
    {
        Id = item.Id,
        Category = item.Category,
        Title = item.Title,
        Url = item.Url,
        Hotkey = item.Hotkey
    };

    private static ServerSettings Clone(ServerSettings item) => new()
    {
        Host = item.Host,
        Port = item.Port,
        Password = item.Password,
        WebUrl = item.WebUrl,
        WindowHotkey = item.WindowHotkey,
        Commands = item.Commands.Select(command => new ServerCommand
        {
            Id = command.Id,
            Title = command.Title,
            Command = command.Command
        }).ToList()
    };

    private static bool TryToken(string token, out int vk)
    {
        vk = token switch
        {
            "numdel" or "decimal" => 0x6E,
            "num0" => 0x60,
            "num1" => 0x61,
            "num2" => 0x62,
            "num3" => 0x63,
            "num4" => 0x64,
            "num5" => 0x65,
            "num6" => 0x66,
            "num7" => 0x67,
            "num8" => 0x68,
            "num9" => 0x69,
            "space" => 0x20,
            "enter" or "return" => 0x0D,
            "tab" => 0x09,
            "esc" or "escape" => 0x1B,
            "back" or "backspace" => 0x08,
            "del" or "delete" => 0x2E,
            "ins" or "insert" => 0x2D,
            "home" => 0x24,
            "end" => 0x23,
            "pgup" => 0x21,
            "pgdn" => 0x22,
            "left" => 0x25,
            "up" => 0x26,
            "right" => 0x27,
            "down" => 0x28,
            _ when token.Length == 1 && token[0] is >= 'a' and <= 'z' => token[0] - 'a' + 0x41,
            _ when token.Length == 1 && token[0] is >= '0' and <= '9' => token[0],
            _ when token.Length is >= 2 and <= 3 && token[0] == 'f' && int.TryParse(token.AsSpan(1), out int fn) && fn is >= 1 and <= 12 => 0x6F + fn,
            _ => 0
        };
        return vk != 0;
    }

    private static string TokenOf(int vk) => vk switch
    {
        0x6E => "NumDel",
        >= 0x60 and <= 0x69 => "Num" + (vk - 0x60),
        0x20 => "Space",
        0x0D => "Enter",
        0x09 => "Tab",
        0x1B => "Esc",
        0x08 => "Back",
        0x2E => "Del",
        0x2D => "Ins",
        >= 0x70 and <= 0x7B => "F" + (vk - 0x6F),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        _ => "0x" + vk.ToString("X")
    };

    private static void Toast(string text)
    {
        try
        {
            var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            ScreenshotToast.Show(text, screen.Bounds, 1.3);
        }
        catch
        {
            // Уведомление не обязательно.
        }
    }

    /// <summary>Ctrl+A и Delete — стереть всё в активном поле ввода.</summary>
    public static void ClearActiveField()
    {
        try
        {
            var inputs = new INPUT[6];
            inputs[0] = Key(0x11, false); // Ctrl
            inputs[1] = Key(0x41, false); // A
            inputs[2] = Key(0x41, true);
            inputs[3] = Key(0x11, true);
            inputs[4] = Key(0x2E, false); // Delete
            inputs[5] = Key(0x2E, true);
            SendInput(6, inputs, Marshal.SizeOf<INPUT>());
        }
        catch
        {
            // Поле могло не принять ввод.
        }
    }

    private static void SendCtrlV()
    {
        var inputs = new INPUT[4];
        inputs[0] = Key(0x11, false);
        inputs[1] = Key(0x56, false);
        inputs[2] = Key(0x56, true);
        inputs[3] = Key(0x11, true);
        SendInput(4, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        Type = 1,
        U = new InputUnion
        {
            Ki = new KEYBDINPUT
            {
                WVk = vk,
                DwFlags = up ? 2u : 0u,
                DwExtraInfo = MouseHook.InjectExtra
            }
        }
    };

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] inputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mi;
        [FieldOffset(0)] public KEYBDINPUT Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint DwFlags;
        public uint Time;
        public UIntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort WVk;
        public ushort WScan;
        public uint DwFlags;
        public uint Time;
        public UIntPtr DwExtraInfo;
    }
}
