using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Zwmenu.Services;

/// <summary>Второй щелчок по видео отдаёт путь уже открытому Zwmenu и пишет программу в «Открыть с помощью».</summary>
public static class VideoOpen
{
    const string PipeName = "Zwmenu.VideoView";
    const string ProgId = "Zwmenu.Video";

    static readonly string[] Extensions =
    {
        ".mp4", ".m4v", ".mp4v", ".mov", ".qt", ".mkv", ".webm",
        ".avi", ".wmv", ".asf", ".flv", ".f4v",
        ".mpg", ".mpeg", ".mpe", ".m2v", ".ts", ".mts", ".m2ts", ".m2t", ".vob",
        ".3gp", ".3g2", ".ogv", ".ogm", ".divx", ".rm", ".rmvb", ".wtv"
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
            return "Видео|" + string.Join(';', parts) + "|Все файлы|*.*";
        }
    }

    public static bool IsVideoPath(string? path)
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
        if (!TryVideoArg(args, out string path))
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
        if (!TryVideoArg(args, out string path))
            return;
        Zwmenu.Overlay.VideoWindow.ShowFile(path);
    }

    public static void SelfCheck()
    {
        if (!IsVideoPath("a.MKV") || IsVideoPath("a.png"))
            throw new InvalidOperationException("видео форматы");
        if (!Command("C:\\Zwmenu\\Zwmenu.exe").Contains("--video", StringComparison.Ordinal))
            throw new InvalidOperationException("видео команда");
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
                changed |= Set(prog, "", "Видео Zwmenu");
                changed |= Set(prog, "DefaultIcon", FileKindIcons.Quoted(FileKindIcons.Video));
                using var open = prog?.CreateSubKey(@"shell\open\command");
                changed |= Set(open, "", command);
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

    static string Command(string exe) => "\"" + exe + "\" --video \"%1\"";

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
                if (IsVideoPath(path))
                    Zwmenu.Overlay.VideoWindow.ShowFile(path);
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

    public static bool TryVideoArg(string[] args, out string path)
    {
        path = "";
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--video", StringComparison.OrdinalIgnoreCase))
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
        if (!IsVideoPath(candidate) || !File.Exists(candidate))
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
