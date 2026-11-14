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
    static TaskCompletionSource<string>? _printWait;

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
                NoteFromPeer(Encoding.UTF8.GetString(body, 2, body.Length - 2));
                break;
        }
    }

    public static async Task<string> SendFileToPrint(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "Файла нет.";
        if (Directory.Exists(path))
            return "Это папка. На печать уходит файл.";
        if (!File.Exists(path))
            return "Файла нет.";
        var info = new FileInfo(path);
        if (info.Length <= 0)
            return "Файл пустой.";
        if (info.Length > 25_000_000)
            return "Файл больше 25 МБ. Для печати через связь это много.";
        if (!EdgeShareService.Shared.Linked)
            return "Второй компьютер не на связи. Zwmenu должен быть открыт на обоих в одной сети.";

        var wait = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Gate)
            _printWait = wait;
        try
        {
            string? broken = await Task.Run(() => SendPrintBytes(path));
            if (broken != null)
                return broken;
            var done = await Task.WhenAny(wait.Task, Task.Delay(40_000));
            if (done != wait.Task)
                return "Файл ушёл, но второй компьютер не ответил. Проверь, что Zwmenu там открыт и принтер включён.";
            return await wait.Task;
        }
        finally
        {
            lock (Gate)
            {
                if (ReferenceEquals(_printWait, wait))
                    _printWait = null;
            }
        }
    }

    static string? SendPrintBytes(string path)
    {
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
        return null;
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
        string path = "";
        try
        {
            string? printer = DefaultPrinter();
            if (printer == null)
            {
                Reply("Печать. На этом компьютере нет принтера.");
                return;
            }

            string dir = Path.Combine(Path.GetTempPath(), "Zwmenu");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, name);
            File.WriteAllBytes(path, data);
            string ext = Path.GetExtension(path);
            bool own = false;
            if (IsImage(ext))
            {
                try
                {
                    RunSta(() => PrintImage(path, printer, name));
                    own = true;
                }
                catch
                {
                    RunSta(() => PrintShell(path, printer));
                }
            }
            else if (IsPlainText(ext) && LooksLikeText(data))
            {
                RunSta(() => PrintText(data, printer, name));
                own = true;
            }
            else if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                own = PrintPdfOrShell(path, printer, name);
            else
                RunSta(() => PrintShell(path, printer));
            Reply("Печать. Запустил на «" + printer + "»: " + name);
            if (own)
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch { /* файл ещё занят */ }
            }
        }
        catch (Exception ex)
        {
            Reply("Печать. " + CleanPrintError(ex.Message));
        }
    }

    static string? DefaultPrinter()
    {
        try
        {
            var settings = new System.Drawing.Printing.PrinterSettings();
            if (!settings.IsValid)
                return null;
            string name = settings.PrinterName ?? "";
            return name.Length == 0 ? null : name;
        }
        catch
        {
            return null;
        }
    }

    static void RunSta(Action work)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { work(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (error != null)
            throw error;
    }

    static void PrintImage(string path, string printer, string title)
    {
        using var image = System.Drawing.Image.FromFile(path);
        using var doc = NewPrint(printer, title);
        doc.PrintPage += (_, e) =>
        {
            if (e.Graphics == null)
                return;
            var bounds = e.MarginBounds;
            float scale = Math.Min(bounds.Width / (float)image.Width, bounds.Height / (float)image.Height);
            float w = image.Width * scale;
            float h = image.Height * scale;
            float x = bounds.Left + (bounds.Width - w) / 2f;
            float y = bounds.Top + (bounds.Height - h) / 2f;
            e.Graphics.DrawImage(image, x, y, w, h);
            e.HasMorePages = false;
        };
        doc.Print();
    }

    static void PrintText(byte[] data, string printer, string title)
    {
        string text = DecodeText(data);
        var lines = WrapText(text);
        int index = 0;
        using var font = new System.Drawing.Font("Segoe UI", 11f);
        using var doc = NewPrint(printer, title);
        doc.PrintPage += (_, e) =>
        {
            if (e.Graphics == null)
                return;
            float y = e.MarginBounds.Top;
            float limit = e.MarginBounds.Bottom;
            while (index < lines.Count && y + font.Height <= limit)
            {
                e.Graphics.DrawString(lines[index], font, System.Drawing.Brushes.Black, e.MarginBounds.Left, y);
                y += font.Height + 2;
                index++;
            }

            e.HasMorePages = index < lines.Count && index < 2400;
        };
        doc.Print();
    }

    static List<string> WrapText(string text)
    {
        var lines = new List<string>();
        string flat = text.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (string raw in flat.Split('\n'))
        {
            if (raw.Length <= 110)
            {
                lines.Add(raw);
                continue;
            }

            for (int i = 0; i < raw.Length; i += 110)
                lines.Add(raw.Substring(i, Math.Min(110, raw.Length - i)));
        }

        if (lines.Count == 0)
            lines.Add("");
        return lines;
    }

    static bool PrintPdfOrShell(string path, string printer, string title)
    {
        List<System.Drawing.Bitmap>? pages = null;
        try
        {
            pages = RenderPdf(path).GetAwaiter().GetResult();
        }
        catch
        {
            pages = null;
        }

        if (pages == null || pages.Count == 0)
        {
            RunSta(() => PrintShell(path, printer));
            return false;
        }

        try
        {
            RunSta(() => PrintBitmaps(pages, printer, title));
            return true;
        }
        finally
        {
            foreach (var page in pages)
                page.Dispose();
        }
    }

    static async Task<List<System.Drawing.Bitmap>> RenderPdf(string path)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        var doc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
        if (doc.PageCount == 0 || doc.PageCount > 30)
            throw new InvalidOperationException("pdf");
        var pages = new List<System.Drawing.Bitmap>();
        for (uint i = 0; i < doc.PageCount; i++)
        {
            using var page = doc.GetPage(i);
            using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var opt = new Windows.Data.Pdf.PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Clamp(page.Size.Width * 140.0 / 96.0, 200, 1600)
            };
            await page.RenderToStreamAsync(ras, opt);
            ras.Seek(0);
            uint size = (uint)Math.Min(ras.Size, int.MaxValue);
            var bytes = new byte[size];
            var reader = new Windows.Storage.Streams.DataReader(ras.GetInputStreamAt(0));
            await reader.LoadAsync(size);
            reader.ReadBytes(bytes);
            reader.DetachStream();
            using var copy = new MemoryStream(bytes);
            using var raw = new System.Drawing.Bitmap(copy);
            pages.Add(new System.Drawing.Bitmap(raw));
        }

        return pages;
    }

    static void PrintBitmaps(IReadOnlyList<System.Drawing.Bitmap> pages, string printer, string title)
    {
        int index = 0;
        using var doc = NewPrint(printer, title);
        doc.PrintPage += (_, e) =>
        {
            if (e.Graphics == null || index >= pages.Count)
                return;
            var image = pages[index];
            var bounds = e.MarginBounds;
            float scale = Math.Min(bounds.Width / (float)image.Width, bounds.Height / (float)image.Height);
            float w = image.Width * scale;
            float h = image.Height * scale;
            float x = bounds.Left + (bounds.Width - w) / 2f;
            float y = bounds.Top + (bounds.Height - h) / 2f;
            e.Graphics.DrawImage(image, x, y, w, h);
            index++;
            e.HasMorePages = index < pages.Count;
        };
        doc.Print();
    }

    static void PrintShell(string path, string printer)
    {
        if (!TryPrintVerb(path, out string verb))
            throw new InvalidOperationException("Этот тип файла на том компьютере не печатается.");
        var info = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
            Verb = verb,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (verb == "printto")
            info.Arguments = "\"" + printer.Replace("\"", "") + "\"";
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Печать не запустилась.");
    }

    static System.Drawing.Printing.PrintDocument NewPrint(string printer, string title)
    {
        var doc = new System.Drawing.Printing.PrintDocument();
        doc.PrinterSettings.PrinterName = printer;
        doc.DocumentName = title;
        doc.PrintController = new System.Drawing.Printing.StandardPrintController();
        if (!doc.PrinterSettings.IsValid)
        {
            doc.Dispose();
            throw new InvalidOperationException("Принтер недоступен: " + printer);
        }

        return doc;
    }

    static bool TryPrintVerb(string path, out string verb)
    {
        verb = "";
        string ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
            return false;
        var ids = new List<string>();
        string? user = RegGet(Microsoft.Win32.Registry.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + ext + @"\UserChoice", "ProgId");
        if (!string.IsNullOrEmpty(user))
            ids.Add(user);
        string? cls = RegGet(Microsoft.Win32.Registry.ClassesRoot, ext, "");
        if (!string.IsNullOrEmpty(cls))
            ids.Add(cls);
        ids.Add(@"SystemFileAssociations\" + ext);
        foreach (string id in ids)
        {
            if (VerbCommand(id, "printto"))
            {
                verb = "printto";
                return true;
            }
        }

        foreach (string id in ids)
        {
            if (VerbCommand(id, "print"))
            {
                verb = "print";
                return true;
            }
        }

        return false;
    }

    static bool VerbCommand(string progId, string verb)
    {
        string sub = progId + @"\shell\" + verb + @"\command";
        if (RegGet(Microsoft.Win32.Registry.ClassesRoot, sub, "") is { Length: > 0 })
            return true;
        return RegGet(Microsoft.Win32.Registry.CurrentUser, @"Software\Classes\" + sub, "") is { Length: > 0 };
    }

    static string? RegGet(Microsoft.Win32.RegistryKey root, string sub, string name)
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

    static bool IsImage(string ext)
    {
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".tif", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsPlainText(string ext)
    {
        return ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".log", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".csv", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ini", StringComparison.OrdinalIgnoreCase);
    }

    static bool LooksLikeText(byte[] data)
    {
        int n = Math.Min(data.Length, 4096);
        int nul = 0;
        for (int i = 0; i < n; i++)
        {
            if (data[i] == 0)
                nul++;
        }

        return nul < 2;
    }

    static string DecodeText(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            return Encoding.UTF8.GetString(data, 3, data.Length - 3);
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
        return Encoding.UTF8.GetString(data);
    }

    static string CleanPrintError(string message)
    {
        string text = (message ?? "").Trim();
        if (text.Length == 0)
            return "Печать не запустилась.";
        return text.Length > 180 ? text[..180] : text;
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

    static void NoteFromPeer(string text)
    {
        if (text.StartsWith("Печать.", StringComparison.Ordinal))
        {
            TaskCompletionSource<string>? wait;
            lock (Gate)
                wait = _printWait;
            wait?.TrySetResult(text);
        }

        Tell(text);
    }

    static void Reply(string text)
    {
        try { Send(Note, Encoding.UTF8.GetBytes(text)); }
        catch { /* ответ не обязателен */ }
        Tell(text);
        PrintPeer.Show(text);
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
