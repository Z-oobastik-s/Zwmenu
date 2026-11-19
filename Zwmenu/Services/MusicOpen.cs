using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Zwmenu.Services;

/// <summary>Второй щелчок по треку отдаёт путь уже открытому Zwmenu и пишет программу в «Открыть с помощью».</summary>
public static class MusicOpen
{
    const string PipeName = "Zwmenu.MusicView";
    const string ProgId = "Zwmenu.Music";

    static readonly string[] Extensions =
    {
        ".mp3", ".flac", ".wav", ".wave", ".m4a", ".aac", ".ogg", ".oga", ".opus",
        ".wma", ".aif", ".aiff", ".aifc", ".ape", ".wv", ".mpc", ".mp2", ".mpa",
        ".ac3", ".mka", ".tak", ".dsf", ".dff", ".caf", ".mid", ".midi"
    };

    static CancellationTokenSource? _cts;

    public static IReadOnlyList<string> KnownExtensions => Extensions;

    public static string DialogFilter
    {
        get
        {
            var parts = new string[Extensions.Length];
            for (int i = 0; i < Extensions.Length; i++)
                parts[i] = "*" + Extensions[i];
            return "Музыка|" + string.Join(';', parts) + "|Все файлы|*.*";
        }
    }

    public static bool IsMusicPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        string ext = Path.GetExtension(path);
        for (int i = 0; i < Extensions.Length; i++)
        {
            if (ext.Equals(Extensions[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static bool TakeRequest(string[] args)
    {
        if (!TryMusicArg(args, out string path))
            return false;
        WindowFront.AllowOthers();
        if (!TrySend(path))
            return false;
        Environment.Exit(0);
        return true;
    }

    public static void StartServer()
    {
        Install();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => Listen(_cts.Token));
    }

    public static void StopServer()
    {
        try { _cts?.Cancel(); }
        catch { /* уже остановлен */ }
    }

    public static void OpenFromArgs(string[] args)
    {
        if (!TryMusicArg(args, out string path))
            return;
        Zwmenu.Overlay.MusicWindow.ShowFile(path);
    }

    public static void SelfCheck()
    {
        if (!IsMusicPath("a.MP3") || IsMusicPath("a.mp4") || IsMusicPath("a.png"))
            throw new InvalidOperationException("музыка форматы");
        if (!Command("C:\\Zwmenu\\Zwmenu.exe").Contains("--music", StringComparison.Ordinal))
            throw new InvalidOperationException("музыка команда");
        MusicMeta.SelfCheck();
    }

    public static void OfferDefault(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;
        var info = new OpenAsInfo
        {
            File = path,
            Class = "",
            Flags = 1 | 2 | 4
        };
        SHOpenWithDialog(IntPtr.Zero, ref info);
    }

    public static void Install()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
                return;
            string command = Command(exe);
            bool changed = false;
            using (var prog = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
            {
                changed |= Set(prog, "", "Музыка Zwmenu");
                changed |= Set(prog, "DefaultIcon", FileKindIcons.Quoted(FileKindIcons.Music));
                using var open = prog?.CreateSubKey(@"shell\open\command");
                changed |= Set(open, "", command);
            }

            using (var types = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\Zwmenu.exe\SupportedTypes"))
            {
                foreach (string ext in Extensions)
                    changed |= Set(types, ext, "");
            }

            foreach (string ext in Extensions)
            {
                using var openWith = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ext + @"\OpenWithProgids");
                changed |= Set(openWith, ProgId, "");
                using var listed = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + ext + @"\OpenWithProgids");
                changed |= Set(listed, ProgId, "");
            }

            if (changed)
                SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // Список «Открыть с помощью» подтянется при следующем запуске.
        }
    }

    static string Command(string exe) => "\"" + exe + "\" --music \"%1\"";

    static bool Set(RegistryKey? key, string name, string value)
    {
        if (key == null)
            return false;
        object? current = key.GetValue(name);
        if (current is string text && text == value)
            return false;
        key.SetValue(name, value);
        return true;
    }

    static void Listen(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                var wait = Task.Run(pipe.WaitForConnection);
                wait.Wait(token);
                if (!pipe.IsConnected)
                    continue;
                string path = ReadPacket(pipe);
                if (IsMusicPath(path))
                    Zwmenu.Overlay.MusicWindow.ShowFile(path);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Следующий щелчок попробует ещё раз.
            }
            finally
            {
                try { pipe?.Dispose(); } catch { /* труба уже закрыта */ }
            }
        }
    }

    static bool TrySend(string path)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.None);
            pipe.Connect(700);
            WritePacket(pipe, path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryMusicArg(string[] args, out string path)
    {
        path = "";
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--music", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                    return false;
                path = string.Join(" ", args.Skip(i + 1)).Trim().Trim('"');
                return path.Length > 0;
            }
        }

        var loose = args.Where(a => a.Length > 0 && !a.StartsWith('-')).ToList();
        if (loose.Count != 1)
            return false;
        string candidate = loose[0].Trim().Trim('"');
        if (!IsMusicPath(candidate) || !File.Exists(candidate))
            return false;
        path = candidate;
        return true;
    }

    static void WritePacket(Stream stream, string text)
    {
        byte[] body = Encoding.UTF8.GetBytes(text);
        Span<byte> head = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(head, body.Length);
        stream.Write(head);
        stream.Write(body);
        stream.Flush();
    }

    static string ReadPacket(Stream stream)
    {
        byte[] head = new byte[4];
        ReadExact(stream, head);
        int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(head);
        if (length < 0 || length > 32000)
            return "";
        byte[] body = new byte[length];
        ReadExact(stream, body);
        return Encoding.UTF8.GetString(body);
    }

    static void ReadExact(Stream stream, byte[] buffer)
    {
        int got = 0;
        while (got < buffer.Length)
        {
            int n = stream.Read(buffer, got, buffer.Length - got);
            if (n <= 0)
                throw new EndOfStreamException();
            got += n;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHOpenWithDialog(IntPtr owner, ref OpenAsInfo info);

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct OpenAsInfo
    {
        public string? File;
        public string? Class;
        public int Flags;
    }
}
