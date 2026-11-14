using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using Microsoft.Win32;

namespace Zwmenu.Services;

/// <summary>
/// Пункт «Печать на втором пк» в меню файла. Второй процесс только передаёт путь уже запущенному Zwmenu.
/// </summary>
public static class PrintPeer
{
    const string PipeName = "Zwmenu.PrintPeer";
    const string MenuKey = @"Software\Classes\*\shell\ZwmenuPrintPeer";

    static CancellationTokenSource? _cts;

    public static bool TakeRequest(string[] args)
    {
        if (!TryPath(args, out string path))
            return false;
        string? fail = Ask(path);
        if (fail != null)
        {
            System.Windows.Forms.MessageBox.Show(
                fail,
                "Zwmenu",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Information);
        }

        Environment.Exit(fail == null ? 0 : 1);
        return true;
    }

    public static void StartServer()
    {
        InstallMenu();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => Listen(_cts.Token));
    }

    public static void StopServer()
    {
        try { _cts?.Cancel(); }
        catch { /* уже остановлен */ }
    }

    public static void Show(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        var app = System.Windows.Application.Current;
        if (app == null)
            return;
        app.Dispatcher.BeginInvoke(() =>
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            var area = screen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 800, 600);
            Zwmenu.Overlay.ScreenshotToast.Show(text.Trim(), area, 4.5);
        });
    }

    static void InstallMenu()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
                return;
            using var key = Registry.CurrentUser.CreateSubKey(MenuKey);
            if (key == null)
                return;
            key.SetValue("", "Печать на втором пк");
            key.SetValue("MUIVerb", "Печать на втором пк");
            key.SetValue("Icon", "\"" + exe + "\",0");
            key.SetValue("NeverDefault", "");
            using var command = key.CreateSubKey("command");
            command?.SetValue("", "\"" + exe + "\" --print-peer \"%1\"");
        }
        catch
        {
            // Меню не обязательно для уже открытого колеса.
        }
    }

    static void Listen(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                var wait = Task.Run(pipe.WaitForConnection);
                wait.Wait(token);
                if (!pipe.IsConnected)
                    continue;
                string path = ReadPacket(pipe);
                string result = WorkshopHub.SendFileToPrint(path).GetAwaiter().GetResult();
                Show(result);
                WritePacket(pipe, result);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                Thread.Sleep(200);
            }
            finally
            {
                try { pipe?.Dispose(); }
                catch { /* уже закрыта */ }
            }
        }
    }

    static string? Ask(string path)
    {
        NamedPipeClientStream? pipe = null;
        try
        {
            pipe = OpenPipe();
            WritePacket(pipe, path);
            var read = Task.Run(() => ReadPacket(pipe));
            if (!read.Wait(45_000))
                throw new TimeoutException();
            return null;
        }
        catch
        {
            return "Zwmenu не запущен. Открой его на этом ноутбуке, и на втором тоже.";
        }
        finally
        {
            try { pipe?.Dispose(); }
            catch { /* уже закрыта */ }
        }
    }

    static NamedPipeClientStream OpenPipe()
    {
        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None);
        try
        {
            pipe.Connect(2000);
            return pipe;
        }
        catch
        {
            if (!PipeListed())
            {
                pipe.Dispose();
                throw;
            }
        }

        try
        {
            pipe.Connect(50_000);
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    static bool PipeListed()
    {
        try
        {
            foreach (string name in Directory.GetFiles(@"\\.\pipe\"))
            {
                if (name.EndsWith(PipeName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { /* список труб недоступен */ }
        return false;
    }

    static bool TryPath(string[] args, out string path)
    {
        path = "";
        for (int i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], "--print-peer", StringComparison.OrdinalIgnoreCase))
                continue;
            if (i + 1 >= args.Length)
                return false;
            path = string.Join(" ", args.Skip(i + 1)).Trim().Trim('"');
            return path.Length > 0;
        }

        return false;
    }

    static void WritePacket(Stream stream, string text)
    {
        byte[] data = Encoding.UTF8.GetBytes(text ?? "");
        if (data.Length > 32_000)
            data = data.AsSpan(0, 32_000).ToArray();
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
}
