using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Windows;
using Draw = System.Drawing;

namespace Zwmenu.Services;

/// <summary>
/// Кадр 23 между двумя Zwmenu: уход, взгляд, печать.
/// Старая сборка такой кадр молча пропускает, поэтому на обоих компьютерах нужна одна версия.
/// </summary>
public static class WorkshopHub
{
    public const byte Away = 1;
    public const byte GlanceAsk = 2;
    public const byte GlancePart = 3;
    public const byte PrintBegin = 4;
    public const byte PrintPart = 5;
    public const byte PrintEnd = 6;
    public const byte Note = 7;

    const int Chunk = 48_000;
    static readonly object Gate = new();
    static byte[]?[]? _parts;
    static int _count;
    static int _got;
    static MemoryStream? _print;
    static string _printName = "file";

    public static event Action<string>? Noted;

    public static bool Send(byte verb, ReadOnlySpan<byte> rest)
    {
        var body = new byte[2 + rest.Length];
        body[0] = EdgeWire.WorkshopCode;
        body[1] = verb;
        rest.CopyTo(body.AsSpan(2));
        return EdgeShareService.Shared.TrySend(body);
    }

    public static void Handle(byte[] body)
    {
        if (body == null || body.Length < 2 || body[0] != EdgeWire.WorkshopCode)
            return;
        switch (body[1])
        {
            case Away:
                MachineDesk.Leave(tellPeer: false);
                break;
            case GlanceAsk:
                Task.Run(CaptureAndSend);
                break;
            case GlancePart:
                TakeGlance(body);
                break;
            case PrintBegin:
                BeginPrint(body);
                break;
            case PrintPart:
                TakePrint(body);
                break;
            case PrintEnd:
                FinishPrint();
                break;
            case Note:
                Tell(Encoding.UTF8.GetString(body, 2, body.Length - 2));
                break;
        }
    }

    public static Task<string> SendFileToPrint(string path)
    {
        return Task.Run(() =>
        {
            if (!File.Exists(path))
                return "Файла нет.";
            var info = new FileInfo(path);
            if (info.Length <= 0)
                return "Файл пустой.";
            if (info.Length > 25_000_000)
                return "Файл больше 25 МБ. Для печати через связь это много.";
            if (!EdgeShareService.Shared.Linked)
                return "Второй не на связи.";

            byte[] data = File.ReadAllBytes(path);
            if (!Send(PrintBegin, Encoding.UTF8.GetBytes(Path.GetFileName(path))))
                return "Связь оборвалась.";
            for (int i = 0; i < data.Length; i += Chunk)
            {
                int n = Math.Min(Chunk, data.Length - i);
                var rest = new byte[n];
                Buffer.BlockCopy(data, i, rest, 0, n);
                if (!Send(PrintPart, rest))
                    return "Связь оборвалась на середине файла.";
            }

            if (!Send(PrintEnd, ReadOnlySpan<byte>.Empty))
                return "Не удалось закончить отправку.";
            return "Файл ушёл на второй компьютер. Печать начнётся там, если принтер у него есть.";
        });
    }

    static void CaptureAndSend()
    {
        try
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            if (screen == null)
            {
                Reply("Экран не найден.");
                return;
            }

            using var shot = ScreenCapture.CaptureMonitor(screen.Bounds, cursor: false);
            byte[] jpeg = ToJpeg(shot);
            int count = Math.Max(1, (jpeg.Length + Chunk - 1) / Chunk);
            if (count > 40)
            {
                Reply("Снимок слишком большой.");
                return;
            }

            for (int i = 0; i < count; i++)
            {
                int n = Math.Min(Chunk, jpeg.Length - i * Chunk);
                var rest = new byte[4 + n];
                BinaryPrimitives.WriteUInt16LittleEndian(rest, (ushort)i);
                BinaryPrimitives.WriteUInt16LittleEndian(rest.AsSpan(2), (ushort)count);
                Buffer.BlockCopy(jpeg, i * Chunk, rest, 4, n);
                if (!Send(GlancePart, rest))
                {
                    Reply("Связь оборвалась, взгляд не дошёл.");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Reply("Взгляд не собрался: " + ex.Message);
        }
    }

    static byte[] ToJpeg(Draw.Bitmap source)
    {
        Draw.Bitmap? scaled = null;
        try
        {
            Draw.Bitmap bmp = source;
            if (source.Width > 800)
            {
                int width = 800;
                int height = Math.Max(1, source.Height * width / source.Width);
                scaled = new Draw.Bitmap(width, height);
                using (var g = Draw.Graphics.FromImage(scaled))
                    g.DrawImage(source, 0, 0, width, height);
                bmp = scaled;
            }

            var encoder = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 35L);
            using var stream = new MemoryStream();
            bmp.Save(stream, encoder, parameters);
            return stream.ToArray();
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    static void TakeGlance(byte[] body)
    {
        if (body.Length < 6)
            return;
        int index = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4));
        if (count < 1 || count > 40 || index >= count)
            return;
        byte[]? jpeg;
        lock (Gate)
        {
            if (index == 0 || _parts == null || _count != count)
            {
                _parts = new byte[count][];
                _count = count;
                _got = 0;
            }

            if (_parts[index] != null)
                return;
            var chunk = new byte[body.Length - 6];
            Buffer.BlockCopy(body, 6, chunk, 0, chunk.Length);
            _parts[index] = chunk;
            _got++;
            if (_got < count || _parts.Any(p => p == null))
                return;
            int total = _parts.Sum(p => p!.Length);
            if (total <= 0 || total > 2_000_000)
            {
                _parts = null;
                return;
            }

            jpeg = new byte[total];
            int offset = 0;
            foreach (byte[]? part in _parts)
            {
                Buffer.BlockCopy(part!, 0, jpeg, offset, part!.Length);
                offset += part.Length;
            }

            _parts = null;
        }

        Zwmenu.Overlay.GlanceWindow.ShowJpeg(jpeg);
    }

    static void BeginPrint(byte[] body)
    {
        lock (Gate)
        {
            _print?.Dispose();
            _print = new MemoryStream();
            string name = body.Length > 2 ? Encoding.UTF8.GetString(body, 2, body.Length - 2) : "file";
            _printName = SafeName(name);
        }
    }

    static void TakePrint(byte[] body)
    {
        lock (Gate)
        {
            if (_print == null || body.Length <= 2 || _print.Length > 25_000_000)
                return;
            _print.Write(body, 2, body.Length - 2);
        }
    }

    static void FinishPrint()
    {
        byte[] data;
        string name;
        lock (Gate)
        {
            if (_print == null || _print.Length == 0)
            {
                _print?.Dispose();
                _print = null;
                Reply("Файл для печати пустой.");
                return;
            }

            data = _print.ToArray();
            _print.Dispose();
            _print = null;
            name = _printName;
        }

        Task.Run(() => PrintLocal(name, data));
    }

    static void PrintLocal(string name, byte[] data)
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "Zwmenu");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name);
            File.WriteAllBytes(path, data);
            var start = new ProcessStartInfo(path)
            {
                Verb = "print",
                UseShellExecute = true,
                CreateNoWindow = true
            };
            Process.Start(start);
            Reply("Печать запущена: " + name);
        }
        catch (Exception ex)
        {
            Reply("Печать не запустилась: " + ex.Message);
        }
    }

    static string SafeName(string name)
    {
        string file = Path.GetFileName(name).Trim();
        foreach (char bad in Path.GetInvalidFileNameChars())
            file = file.Replace(bad, '_');
        if (file.Length == 0 || file is "." or "..")
            file = "file.bin";
        return file.Length > 80 ? file[..80] : file;
    }

    static void Reply(string text)
    {
        try { Send(Note, Encoding.UTF8.GetBytes(text)); }
        catch { /* ответ не обязателен */ }
        Tell(text);
    }

    static void Tell(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        var app = System.Windows.Application.Current;
        if (app == null)
            return;
        app.Dispatcher.BeginInvoke(() => Noted?.Invoke(text.Trim()));
    }
}
