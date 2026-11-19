using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Zwmenu.Thumbs;

namespace Zwmenu.Services;

/// <summary>
/// Второй щелчок по картинке отдаёт путь уже открытому Zwmenu.
/// В «Открыть с помощью» программа появляется сама, без прав администратора.
/// </summary>
public static class PhotoOpen
{
    const string PipeName = "Zwmenu.PhotoView";
    const string ProgId = "Zwmenu.Photo";
    const string ThumbClsid = "{6f4c2a91-8e3d-4b7a-9c15-2d6e8f0a4b33}";
    const string ThumbHandlerKey = "{e357fccd-a995-4576-b8a0-c6c0f5d8e1c0}";

    static readonly string[] ThumbExtensions = { ".svg", ".svgz", ".webp" };

    static CancellationTokenSource? _cts;

    public static bool TakeRequest(string[] args)
    {
        if (!TryImageArg(args, out string path))
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
        if (!TryImageArg(args, out string path))
            return;
        Zwmenu.Overlay.PhotoWindow.ShowFile(path);
    }

    public static void SelfCheck()
    {
        if (PhotoCodec.KnownExtensions.Count < 8)
            throw new InvalidOperationException("форматы");
        string command = Command("C:\\Zwmenu\\Zwmenu.exe");
        if (!command.Contains("--photo", StringComparison.Ordinal))
            throw new InvalidOperationException("команда");
        PhotoCodec.SelfCheck();
        PhotoMeta.SelfCheck();
        PhotoWide.SelfCheck();
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
                changed |= Set(prog, "", "Просмотр Zwmenu");
                changed |= Set(prog, "DefaultIcon", FileKindIcons.Quoted(FileKindIcons.Photo));
                using var open = prog?.CreateSubKey(@"shell\open\command");
                changed |= Set(open, "", command);
            }

            using (var app = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\Zwmenu.exe"))
            {
                changed |= Set(app, "FriendlyAppName", "Просмотр Zwmenu");
                changed |= Set(app, "ApplicationIcon", exe + ",0");
                using var open = app?.CreateSubKey(@"shell\open\command");
                changed |= Set(open, "", command);
                using var types = app?.CreateSubKey("SupportedTypes");
                foreach (string ext in PhotoCodec.KnownExtensions)
                    changed |= Set(types, ext, "");
            }

            foreach (string ext in PhotoCodec.KnownExtensions)
            {
                using var openWith = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ext + @"\OpenWithProgids");
                changed |= Set(openWith, ProgId, "");
                using var listed = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + ext + @"\OpenWithProgids");
                changed |= Set(listed, ProgId, "");
            }

            changed |= ForgetThumb();

            if (changed)
                SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // Список «Открыть с помощью» подтянется при следующем запуске.
        }
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

    static string Command(string exe) => "\"" + exe + "\" --photo \"%1\"";

    static bool ForgetThumb()
    {
        bool changed = false;
        try
        {
            if (Registry.CurrentUser.OpenSubKey(@"Software\Classes\CLSID\" + ThumbClsid) != null)
            {
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\CLSID\" + ThumbClsid, false);
                changed = true;
            }
        }
        catch
        {
            // Чужой раздел не трогаем.
        }

        foreach (string ext in ThumbExtensions)
        {
            changed |= DropShell(ext);
            string? prog = ProgOf(ext);
            if (!string.IsNullOrWhiteSpace(prog))
                changed |= DropShell(prog);
        }

        try
        {
            using var approved = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved", true);
            if (approved?.GetValue(ThumbClsid) != null)
            {
                approved.DeleteValue(ThumbClsid, false);
                changed = true;
            }
        }
        catch
        {
            // Список одобренных расширений уже чистый.
        }

        return changed;
    }

    static bool DropShell(string className)
    {
        string sub = @"Software\Classes\" + className + @"\ShellEx\" + ThumbHandlerKey;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(sub);
            if (key?.GetValue("") is not string text || !text.Equals(ThumbClsid, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        catch
        {
            return false;
        }

        try
        {
            Registry.CurrentUser.DeleteSubKey(sub, false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static string? ProgOf(string ext)
    {
        string? user = Read(Registry.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + ext + @"\UserChoice", "ProgId");
        if (!string.IsNullOrWhiteSpace(user))
            return user;
        string? local = Read(Registry.CurrentUser, @"Software\Classes\" + ext, "");
        if (!string.IsNullOrWhiteSpace(local))
            return local;
        return Read(Registry.LocalMachine, @"Software\Classes\" + ext, "");
    }

    static string? Read(RegistryKey root, string sub, string name)
    {
        try
        {
            using var key = root.OpenSubKey(sub);
            return key?.GetValue(name) as string;
        }
        catch
        {
            return null;
        }
    }

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
                if (PhotoCodec.IsImagePath(path))
                    Zwmenu.Overlay.PhotoWindow.ShowFile(path);
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

    public static bool TryImageArg(string[] args, out string path)
    {
        path = "";
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--photo", StringComparison.OrdinalIgnoreCase))
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
        if (!PhotoCodec.IsImagePath(candidate) || !File.Exists(candidate))
            return false;
        path = candidate;
        return true;
    }

    static void WritePacket(Stream stream, string text)
    {
        byte[] data = Encoding.UTF8.GetBytes(text ?? "");
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, data.Length);
        stream.Write(len);
        stream.Write(data);
        stream.Flush();
    }

    static string ReadPacket(Stream stream)
    {
        Span<byte> lenBuf = stackalloc byte[4];
        ReadExact(stream, lenBuf);
        int n = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
        if (n < 0 || n > 32_000)
            throw new InvalidDataException();
        byte[] data = new byte[n];
        ReadExact(stream, data);
        return Encoding.UTF8.GetString(data);
    }

    static void ReadExact(Stream stream, Span<byte> buf)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = stream.Read(buf[got..]);
            if (n == 0)
                throw new EndOfStreamException();
            got += n;
        }
    }

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHOpenWithDialog(IntPtr owner, ref OpenAsInfo info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct OpenAsInfo
    {
        public string File;
        public string Class;
        public int Flags;
    }
}
