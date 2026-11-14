using System.Buffers.Binary;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Zwmenu.Overlay;

namespace Zwmenu.Services;

/// <summary>
/// Текст и картинка уходят сразу. Файлы ждут, пока их сами отправят или перетащат через край.
/// </summary>
sealed class EdgeFiles : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private const int ChunkSize = EdgeWire.FileChunkSize;
    private const int MaxManifest = 60_000;
    private const int MaxText = 1_000_000;
    private const int MaxPng = 32 * 1024 * 1024;

    private readonly Func<byte[], bool> _sendBulk;
    private readonly Func<int, byte[]> _rent;
    private readonly SemaphoreSlim _oneSend = new(1, 1);
    private HwndSource? _source;
    private CancellationTokenSource? _alive;
    private readonly object _echoGate = new();
    private Incoming? _incoming;
    private ClipIncoming? _clip;
    private string _echoSig = "";
    private long _echoUntil;
    private bool _leftDown;
    private bool _dropReady;
    private bool _dropWanted;
    private string[] _readyPaths = Array.Empty<string>();
    private List<string> _armed = new();
    private long _armedUntil;
    private readonly object _carryGate = new();
    private List<string>? _carry;
    private long _carryUntil;
    private int _armTicket;
    private int _carryTicket;
    private int _releaseTicket;
    private int _carryCancelTicket;
    private int _armBusy;
    private int _generation;
    private int _fileGen;
    private int _cancelGen;
    private int _sending;
    private int _stageGen;
    private readonly object _stageGate = new();
    private List<string>? _afterSend;
    private readonly object _inGate = new();
    private uint _ignoreId;
    private Channel<byte[]>? _disk;
    private Task? _diskTask;
    private int _diskEpoch;
    private long _sendUiAt;
    private long _recvUiAt;
    private int _suppressNext;
    private uint _ownSequence;
    private bool _disposed;

    public EdgeFiles(Func<byte[], bool> sendBulk, Func<int, byte[]>? rent = null)
    {
        _sendBulk = sendBulk;
        _rent = rent ?? (n => new byte[n]);
    }

    public Action? PasteIntoWindow { get; set; }

    /// <summary>Подпись у курсора на том ноутбуке. Пустая строка убирает плюс.</summary>
    public Action<string?>? CarryVisual { get; set; }

    public void Start()
    {
        Stop();
        _alive = new CancellationTokenSource();
        int epoch = Interlocked.Increment(ref _diskEpoch);
        var disk = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(48)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        _disk = disk;
        _diskTask = Task.Run(() => DiskLoop(disk, epoch));
        var parameters = new HwndSourceParameters("ZwmenuEdgeClip")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3)
        };
        _source = new HwndSource(parameters);
        _source.AddHook(OnMessage);
        AddClipboardFormatListener(_source.Handle);
    }

    public void Stop()
    {
        bool live = _incoming != null || Volatile.Read(ref _sending) != 0;
        try { _alive?.Cancel(); } catch { /* уже остановлен */ }
        _alive = null;
        Interlocked.Increment(ref _diskEpoch);
        try { _disk?.Writer.TryComplete(); } catch { /* уже закрыт */ }
        try { _diskTask?.Wait(400); } catch { /* запись ещё дожимает кадр */ }
        _disk = null;
        _diskTask = null;
        CloseIncoming(delete: true);
        if (live)
            EdgeTransferHud.Finish("Связь оборвалась", "Не все файлы дошли", false);
        CloseClip();
        _dropReady = false;
        _dropWanted = false;
        _leftDown = false;
        lock (_carryGate)
            _carry = null;
        if (_source != null)
        {
            RemoveClipboardFormatListener(_source.Handle);
            _source.RemoveHook(OnMessage);
            _source.Dispose();
            _source = null;
        }
    }

    public void NoteLeft(bool down)
    {
        _leftDown = down;
        if (down)
        {
            // Клик пришёл с другого ноутбука. Значок выделяется чуть позже самого нажатия.
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;
            _ = Task.Run(() =>
            {
                Thread.Sleep(70);
                try
                {
                    dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { RememberSelection(); }
                        catch { /* оболочка занята */ }
                    }));
                }
                catch { /* окно уже закрыто */ }
            });
            return;
        }

        var ui = System.Windows.Application.Current?.Dispatcher;
        if (ui == null || ui.CheckAccess())
            TryDrop();
        else
            ui.BeginInvoke(TryDrop);
    }

    public void RememberSelection()
    {
        if (Interlocked.Exchange(ref _armBusy, 1) == 1)
            return;
        try
        {
            var paths = SelectedFiles();
            if (paths.Count == 0)
                return;
            _armed = paths;
            _armedUntil = Environment.TickCount64 + 60_000;
        }
        finally
        {
            Interlocked.Exchange(ref _armBusy, 0);
        }
    }

    /// <summary>Жест начался. Вызов с крючка, до того как файлы прочитаны.</summary>
    public void BeginGesture() => Interlocked.Increment(ref _armTicket);

    /// <summary>Кнопка ещё зажата. Файлы только показываем у курсора, байты не шлём.</summary>
    public bool ArmCarry()
    {
        int ticket = Volatile.Read(ref _armTicket);
        if (ticket == 0 || ticket == Volatile.Read(ref _carryCancelTicket))
            return false;
        var paths = PickPaths();
        if (paths.Count == 0)
            return false;
        bool sendNow;
        lock (_carryGate)
        {
            if (ticket != Volatile.Read(ref _armTicket) || ticket == Volatile.Read(ref _carryCancelTicket))
                return false;
            sendNow = ticket == Volatile.Read(ref _releaseTicket);
            if (sendNow)
            {
                _carry = null;
                _carryUntil = 0;
            }
            else
            {
                _carry = paths;
                _carryTicket = ticket;
                _carryUntil = Environment.TickCount64 + 60_000;
            }
        }

        Interlocked.Increment(ref _stageGen);
        EdgeTransferHud.DismissOffer();
        if (sendNow)
            return DispatchCarry(paths);
        string label = paths.Count == 1
            ? System.IO.Path.GetFileName(paths[0].TrimEnd('\\', '/'))
            : CountFiles(paths.Count);
        if (string.IsNullOrWhiteSpace(label))
            label = CountFiles(paths.Count);
        try { CarryVisual?.Invoke(label + " · отпусти"); }
        catch { /* подсказка не обязательна */ }
        return true;
    }

    /// <summary>Кнопку отпустили. Теперь можно отправлять.</summary>
    public bool CommitCarry()
    {
        int ticket = Volatile.Read(ref _armTicket);
        List<string>? paths;
        lock (_carryGate)
        {
            bool fresh = _carry != null
                && _carryTicket == ticket
                && Environment.TickCount64 <= _carryUntil;
            paths = fresh ? _carry : null;
            _carry = null;
            _carryUntil = 0;
            if (paths == null)
                _releaseTicket = ticket;
        }

        if (paths == null || paths.Count == 0)
            return false;
        return DispatchCarry(paths);
    }

    /// <summary>Кнопку так и не отпустили: увели мышь обратно или прервали жест.</summary>
    public void CancelCarry()
    {
        int ticket = Volatile.Read(ref _armTicket);
        bool had;
        lock (_carryGate)
        {
            had = _carry != null;
            _carry = null;
            _carryUntil = 0;
            _carryCancelTicket = ticket;
        }

        if (!had)
            return;
        try { CarryVisual?.Invoke(null); }
        catch { /* подсказка не обязательна */ }
    }

    private bool DispatchCarry(List<string> paths)
    {
        try { CarryVisual?.Invoke(null); }
        catch { /* подсказку снимем вторым кадром */ }
        EdgeTransferHud.Progress("Перетаскиваю", "Отпустил, собираю файлы…", 0, force: true);
        Send(paths, drop: true);
        return true;
    }

    private List<string> PickPaths()
    {
        var dragging = ShelfFiles.Dragging;
        if (dragging is { Length: > 0 })
            return dragging.ToList();
        // Свежий список с клика. Повторный опрос проводника на потоке мыши её подвешивает.
        if (Environment.TickCount64 < _armedUntil && _armed.Count > 0)
            return _armed.ToList();
        return SelectedFiles();
    }

    public void CancelActive()
    {
        int gen = Volatile.Read(ref _fileGen);
        if (gen != 0 && Volatile.Read(ref _sending) == gen)
        {
            Interlocked.Exchange(ref _cancelGen, gen);
            Interlocked.Increment(ref _fileGen);
            return;
        }

        uint id;
        lock (_inGate)
            id = _incoming?.Id ?? 0;
        if (id == 0)
            return;
        _ignoreId = id;
        CloseIncoming(delete: true);
        EdgeTransferHud.Finish("Остановлено", "Приём прерван, недошедшие файлы убраны", false);
    }

    public bool IsFileFrame(byte[] body) =>
        body.Length > 0 && body[0] is EdgeWire.FileListCode or EdgeWire.FileChunkCode or EdgeWire.FileEndCode;

    public ValueTask EnqueueDisk(byte[] body)
    {
        var disk = _disk;
        if (disk == null)
        {
            Handle(body);
            return ValueTask.CompletedTask;
        }

        return disk.Writer.WriteAsync(body);
    }

    private async Task DiskLoop(Channel<byte[]> disk, int epoch)
    {
        try
        {
            await foreach (var body in disk.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (epoch != Volatile.Read(ref _diskEpoch))
                    return;
                try
                {
                    HandleCore(body);
                }
                catch
                {
                    bool files = _incoming != null;
                    CloseIncoming(delete: true);
                    if (files)
                        EdgeTransferHud.Finish("Перенос оборвался", "Не все файлы дошли", false);
                }
            }
        }
        catch (ChannelClosedException)
        {
            /* связь закрыла очередь */
        }
    }

    private static string CountFiles(int count)
    {
        int n10 = count % 10;
        int n100 = count % 100;
        string word = n10 == 1 && n100 != 11
            ? "файл"
            : n10 is >= 2 and <= 4 && (n100 < 12 || n100 > 14)
                ? "файла"
                : "файлов";
        return count + " " + word;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return bytes + " Б";
        if (bytes < 1024 * 1024)
            return (bytes / 1024d).ToString("0.#") + " КБ";
        if (bytes < 1024L * 1024 * 1024)
            return (bytes / 1024d / 1024d).ToString("0.#") + " МБ";
        return (bytes / 1024d / 1024d / 1024d).ToString("0.##") + " ГБ";
    }

    private void Report(string title, int index, int count, string name, long got, long total, long started, bool force, bool send)
    {
        long now = Environment.TickCount64;
        ref long gate = ref (send ? ref _sendUiAt : ref _recvUiAt);
        if (!force && now < Volatile.Read(ref gate))
            return;
        Volatile.Write(ref gate, now + 80);
        long span = now - started;
        string speed = span > 300 && got > 0 ? " · " + FormatBytes(got * 1000 / Math.Max(span, 1)) + "/с" : "";
        string line = index < 0
            ? CountFiles(count) + " · " + FormatBytes(total)
            : (index + 1) + " из " + count + " · " + name + "\n" + FormatBytes(got) + " из " + FormatBytes(total) + speed;
        EdgeTransferHud.Progress(title, line, total == 0 ? 1 : (double)got / total, force);
    }

    public bool Handle(byte[] body)
    {
        if (body.Length == 0)
            return false;
        try
        {
            return HandleCore(body);
        }
        catch
        {
            bool files = _incoming != null;
            CloseIncoming(delete: true);
            CloseClip();
            if (files)
                EdgeTransferHud.Finish("Перенос оборвался", "Не все файлы дошли", false);
            return true;
        }
    }

    private bool HandleCore(byte[] body)
    {
        switch (body[0])
        {
            case EdgeWire.FileListCode:
                BeginIncoming(body);
                return true;
            case EdgeWire.FileChunkCode:
                WriteChunk(body);
                return true;
            case EdgeWire.FileEndCode:
                FinishIncoming(body);
                return true;
            case EdgeWire.ClipOpenCode:
                BeginClip(body);
                return true;
            case EdgeWire.ClipChunkCode:
                WriteClipChunk(body);
                return true;
            case EdgeWire.ClipEndCode:
                FinishClip(body);
                return true;
            default:
                return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate)
        {
            handled = true;
            try { ReadClipboard(); }
            catch { /* чужой буфер не роняет окно */ }
        }

        return IntPtr.Zero;
    }

    private void ReadClipboard()
    {
        if (Volatile.Read(ref _suppressNext) == 1)
            return;
        if (_ownSequence != 0 && GetClipboardSequenceNumber() == _ownSequence)
            return;
        try
        {
            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                var list = System.Windows.Clipboard.GetFileDropList();
                var paths = new List<string>(list.Count);
                foreach (string? path in list)
                {
                    if (!string.IsNullOrWhiteSpace(path))
                        paths.Add(path);
                }

                if (paths.Count == 0)
                    return;
                if (IsEcho(Signature(paths)))
                    return;
                // Копирование и вырезание только запоминаем. Байты уходят по кнопке или через край экрана.
                Stage(paths);
                return;
            }

            lock (_stageGate)
                _afterSend = null;
            Interlocked.Increment(ref _stageGen);
            EdgeTransferHud.DismissOffer();
        }
        catch
        {
            return;
        }

        string? text = null;
        byte[]? png = null;
        BitmapSource? image = null;
        try
        {
            if (System.Windows.Clipboard.ContainsText())
            {
                string value = System.Windows.Clipboard.GetText();
                if (value.Length > 0 && value.Length <= MaxText)
                    text = value;
            }
        }
        catch
        {
            text = null;
        }

        try
        {
            if (System.Windows.Clipboard.ContainsData("PNG") && System.Windows.Clipboard.GetData("PNG") is Stream stream)
            {
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                if (copy.Length > 0 && copy.Length <= MaxPng)
                    png = copy.ToArray();
            }
            else if (System.Windows.Clipboard.ContainsImage())
            {
                image = System.Windows.Clipboard.GetImage();
                if (image != null && image.CanFreeze && !image.IsFrozen)
                    image.Freeze();
                if (image != null && !image.IsFrozen)
                    image = null;
            }
        }
        catch
        {
            png = null;
            image = null;
        }

        if (text == null && png == null && image == null)
            return;
        int generation = Interlocked.Increment(ref _generation);
        var token = _alive?.Token ?? CancellationToken.None;
        var textCopy = text;
        var pngCopy = png;
        var imageCopy = image;
        _ = Task.Run(() => SendClip(textCopy, pngCopy, imageCopy, token, generation));
    }

    private void Stage(List<string> roots)
    {
        int gen = Interlocked.Increment(ref _stageGen);
        string title = roots.Count == 1
            ? Path.GetFileName(roots[0].TrimEnd('\\', '/'))
            : CountFiles(roots.Count);
        if (string.IsNullOrWhiteSpace(title))
            title = "Файлы";
        lock (_stageGate)
        {
            if (Volatile.Read(ref _sending) != 0)
            {
                _afterSend = roots;
                return;
            }
        }

        int armed = 0;
        EdgeTransferHud.Offer(
            title,
            "Считаю, сколько внутри. На второй компьютер ещё ничего не ушло.",
            () => Interlocked.Exchange(ref armed, 1),
            () => Interlocked.Increment(ref _stageGen));
        _ = Task.Run(() =>
        {
            List<(string Full, string Rel, long Size)>? files = null;
            bool capped = false;
            try { files = Expand(roots, out capped); }
            catch { files = new List<(string, string, long)>(); }
            if (gen != Volatile.Read(ref _stageGen))
                return;
            long bytes = 0;
            int count = files?.Count ?? 0;
            if (files != null)
            {
                foreach (var file in files)
                    bytes += file.Size;
            }

            string detail = files == null
                ? "Больше 4 ГБ, целиком не отправляю."
                : count == 0
                    ? "Внутри нет файлов."
                    : FormatBytes(bytes) + " · " + CountFiles(count)
                        + (capped ? ". Беру первые 500." : ".")
                        + " Уйдёт только по кнопке или если перетащить через край.";
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;
            dispatcher.BeginInvoke(() =>
            {
                if (gen != Volatile.Read(ref _stageGen))
                    return;
                if (Volatile.Read(ref _sending) != 0)
                {
                    lock (_stageGate)
                        _afterSend = roots;
                    return;
                }

                if (Volatile.Read(ref armed) == 1)
                {
                    Interlocked.Increment(ref _stageGen);
                    if (files == null || count == 0)
                        EdgeTransferHud.Finish("Не отправилось", files == null ? "Больше 4 ГБ" : "Внутри нет файлов", false);
                    else
                        SendReady(files, drop: false);
                    return;
                }

                EdgeTransferHud.Offer(title, detail, () =>
                {
                    if (gen != Volatile.Read(ref _stageGen))
                        return;
                    Interlocked.Increment(ref _stageGen);
                    if (files == null || count == 0)
                    {
                        EdgeTransferHud.Finish("Не отправилось", files == null ? "Больше 4 ГБ" : "Внутри нет файлов", false);
                        return;
                    }

                    SendReady(files, drop: false);
                }, () => Interlocked.Increment(ref _stageGen));
            });
        });
    }

    private void Send(List<string> roots, bool drop)
    {
        int generation = Interlocked.Increment(ref _fileGen);
        Volatile.Write(ref _sending, generation);
        var token = _alive?.Token ?? CancellationToken.None;
        _ = Task.Run(() =>
        {
            List<(string Full, string Rel, long Size)>? files;
            try
            {
                files = Expand(roots, out _);
            }
            catch
            {
                files = new List<(string, string, long)>();
            }

            SendCore(files, drop, token, generation);
        });
    }

    private void SendReady(List<(string Full, string Rel, long Size)> files, bool drop)
    {
        int generation = Interlocked.Increment(ref _fileGen);
        Volatile.Write(ref _sending, generation);
        var token = _alive?.Token ?? CancellationToken.None;
        _ = Task.Run(() => SendCore(files, drop, token, generation));
    }

    private void SendCore(List<(string Full, string Rel, long Size)>? files, bool drop, CancellationToken token, int generation)
    {
        var held = false;
        try
        {
            _oneSend.Wait(token);
            held = true;
        }
        catch
        {
            EndSending(generation);
            return;
        }

        try
        {
            long sent = 0;
            long total = 0;
            var started = Environment.TickCount64;
            if (generation != Volatile.Read(ref _fileGen))
                return;
            if (token.IsCancellationRequested)
            {
                EdgeTransferHud.Finish("Связь оборвалась", "Не все файлы дошли", false);
                return;
            }
            if (files == null)
            {
                EdgeTransferHud.Finish("Не отправилось", "Перенос больше 4 ГБ", false);
                return;
            }

            if (files.Count == 0)
            {
                EdgeTransferHud.Finish("Не отправилось", "Файлы не прочитались", false);
                return;
            }

            uint id = (uint)Random.Shared.Next(1, int.MaxValue);
            byte[]? manifest = BuildManifest(id, drop, files);
            if (manifest == null)
            {
                EdgeTransferHud.Finish("Не отправилось", "Слишком много файлов для одного переноса", false);
                return;
            }

            if (!_sendBulk(manifest))
            {
                EdgeTransferHud.Finish("Не отправилось", "Нет связи со вторым компьютером", false);
                return;
            }

            foreach (var file in files)
                total += file.Size;
            var missed = new List<string>();
            var buffer = new byte[ChunkSize];
            string head = drop ? "Перетаскиваю" : "На второй компьютер";
            bool closed = false;
            Report(head, -1, files.Count, "", 0, total, started, force: true, send: true);
            for (int i = 0; i < files.Count; i++)
            {
                if (Halt(generation, token, opened: true, ref closed, id, ref sent, total))
                    return;
                string name = Path.GetFileName(files[i].Rel);
                try
                {
                    // Delete: пока шлём, этот файл можно переименовать или перенести здесь.
                    using var stream = new FileStream(
                        files[i].Full,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        ChunkSize,
                        FileOptions.SequentialScan);
                    long offset = 0;
                    while (true)
                    {
                        int read = stream.Read(buffer, 0, buffer.Length);
                        if (read <= 0)
                            break;
                        var packet = read == ChunkSize ? _rent(EdgeWire.FilePacketSize) : new byte[17 + read];
                        FillChunk(packet, id, (ushort)i, offset, buffer.AsSpan(0, read));
                        if (!_sendBulk(packet))
                        {
                            EdgeTransferHud.Finish("Связь оборвалась", "Дошло " + i + " из " + files.Count + " файлов", false);
                            return;
                        }

                        offset += read;
                        sent += read;
                        Report(head, i, files.Count, name, sent, total, started, force: false, send: true);
                        if (Halt(generation, token, opened: true, ref closed, id, ref sent, total))
                            return;
                    }

                    if (offset != files[i].Size)
                        missed.Add(name);
                }
                catch
                {
                    missed.Add(name);
                }
            }

            if (!closed && !_sendBulk(FileEnd(id)))
            {
                EdgeTransferHud.Finish("Связь оборвалась", "Второй компьютер мог получить не всё", false);
                return;
            }

            if (missed.Count == 0)
            {
                EdgeTransferHud.Finish("Отправлено", CountFiles(files.Count) + " · " + FormatBytes(sent), true);
                return;
            }

            string who = string.Join(", ", missed.Take(3));
            if (missed.Count > 3)
                who += " и ещё " + (missed.Count - 3);
            EdgeTransferHud.Finish(
                "Не все файлы ушли",
                "Дошло " + (files.Count - missed.Count) + " из " + files.Count + ". Не ушли: " + who,
                false);
        }
        finally
        {
            if (held)
                _oneSend.Release();
            EndSending(generation);
        }
    }

    private bool Halt(int generation, CancellationToken token, bool opened, ref bool closed, uint id, ref long sent, long total)
    {
        if (generation == Volatile.Read(ref _cancelGen))
        {
            if (opened && !closed)
            {
                closed = true;
                _sendBulk(FileEnd(id));
            }

            EdgeTransferHud.Finish(
                "Остановлено",
                sent == 0 ? "Ничего не ушло" : "Ушло " + FormatBytes(sent) + " из " + FormatBytes(total),
                false);
            return true;
        }

        if (token.IsCancellationRequested)
        {
            EdgeTransferHud.Finish("Связь оборвалась", "Не все файлы дошли", false);
            return true;
        }

        return generation != Volatile.Read(ref _fileGen);
    }

    private void EndSending(int generation)
    {
        if (Interlocked.CompareExchange(ref _sending, 0, generation) != generation)
            return;
        List<string>? next;
        lock (_stageGate)
        {
            next = _afterSend;
            _afterSend = null;
        }

        if (next == null)
            return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
            return;
        dispatcher.BeginInvoke(() => Stage(next));
    }

    private void SendClip(string? text, byte[]? png, BitmapSource? image, CancellationToken token, int generation)
    {
        if (png == null && image != null)
            png = EncodePng(image);
        if ((text == null || text.Length == 0) && (png == null || png.Length == 0))
            return;
        if (png != null && png.Length > MaxPng)
            png = null;
        if (text == null && png == null)
            return;
        if (IsEcho(ClipSignature(text, png)))
            return;

        try
        {
            _oneSend.Wait(token);
        }
        catch
        {
            return;
        }

        try
        {
            if (generation != Volatile.Read(ref _generation) || token.IsCancellationRequested)
                return;
            uint id = (uint)Random.Shared.Next(1, int.MaxValue);
            int textLen = text == null ? 0 : Encoding.UTF8.GetByteCount(text);
            int pngLen = png?.Length ?? 0;
            if (textLen > MaxText)
                return;
            if (!_sendBulk(ClipOpen(id, textLen, pngLen)))
                return;
            if (textLen > 0 && !WriteParts(id, 0, Encoding.UTF8.GetBytes(text!), token, generation))
                return;
            if (pngLen > 0 && png != null && !WriteParts(id, 1, png, token, generation))
                return;
            _sendBulk(ClipEnd(id));
        }
        finally
        {
            _oneSend.Release();
        }
    }

    private bool WriteParts(uint id, byte part, byte[] data, CancellationToken token, int generation)
    {
        for (int offset = 0; offset < data.Length;)
        {
            if (token.IsCancellationRequested || generation != Volatile.Read(ref _generation))
                return false;
            int count = Math.Min(ChunkSize, data.Length - offset);
            if (!_sendBulk(ClipChunk(id, part, offset, data.AsSpan(offset, count))))
                return false;
            offset += count;
        }

        return true;
    }

    private void CloseClip()
    {
        if (_clip == null)
            return;
        try { _clip.Text.Dispose(); } catch { /* уже закрыт */ }
        try { _clip.Png.Dispose(); } catch { /* уже закрыт */ }
        _clip = null;
    }

    private void BeginClip(byte[] body)
    {
        CloseClip();
        if (body.Length < 13)
            return;
        int textLen = BitConverter.ToInt32(body, 5);
        int pngLen = BitConverter.ToInt32(body, 9);
        if (textLen < 0 || pngLen < 0 || textLen > MaxText || pngLen > MaxPng)
            return;
        if (textLen == 0 && pngLen == 0)
            return;
        _clip = new ClipIncoming
        {
            Id = BitConverter.ToUInt32(body, 1),
            TextLen = textLen,
            PngLen = pngLen
        };
    }

    private void WriteClipChunk(byte[] body)
    {
        var clip = _clip;
        if (clip == null || body.Length < 12)
            return;
        uint id = BitConverter.ToUInt32(body, 1);
        if (id != clip.Id)
            return;
        byte part = body[5];
        int offset = BitConverter.ToInt32(body, 6);
        int len = BitConverter.ToUInt16(body, 10);
        if (12 + len > body.Length)
            return;
        var stream = part == 0 ? clip.Text : part == 1 ? clip.Png : null;
        int cap = part == 0 ? clip.TextLen : part == 1 ? clip.PngLen : -1;
        if (stream == null || offset < 0 || offset > cap || len > cap - offset)
            return;
        stream.Position = offset;
        stream.Write(body, 12, len);
    }

    private void FinishClip(byte[] body)
    {
        var clip = _clip;
        if (clip == null || body.Length < 5)
            return;
        uint id = BitConverter.ToUInt32(body, 1);
        if (id != clip.Id)
            return;
        _clip = null;
        try
        {
            if (clip.Text.Length != clip.TextLen || clip.Png.Length != clip.PngLen)
                return;
            string? text = clip.TextLen == 0 ? null : Encoding.UTF8.GetString(clip.Text.ToArray());
            byte[]? png = clip.PngLen == 0 ? null : clip.Png.ToArray();
            PublishClip(text, png);
        }
        finally
        {
            try { clip.Text.Dispose(); } catch { /* уже закрыт */ }
            try { clip.Png.Dispose(); } catch { /* уже закрыт */ }
        }
    }

    private void PublishClip(string? text, byte[]? png)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
            return;
        dispatcher.BeginInvoke(() =>
        {
            try
            {
                var data = new System.Windows.DataObject();
                if (!string.IsNullOrEmpty(text))
                    data.SetText(text);
                if (png != null && png.Length > 0)
                {
                    var image = LoadPng(png);
                    if (image != null)
                        data.SetImage(image);
                    data.SetData("PNG", new MemoryStream(png));
                }

                MarkEcho(ClipSignature(text, png));
                Volatile.Write(ref _suppressNext, 1);
                try
                {
                    System.Windows.Clipboard.SetDataObject(data, true);
                    _ownSequence = GetClipboardSequenceNumber();
                }
                finally
                {
                    Volatile.Write(ref _suppressNext, 0);
                }
            }
            catch
            {
                /* буфер занят, копия останется на том ноутбуке */
            }
        });
    }

    private static byte[]? EncodePng(BitmapSource image)
    {
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            if (stream.Length == 0 || stream.Length > MaxPng)
                return null;
            return stream.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? LoadPng(byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static byte[] ClipOpen(uint id, int textLen, int pngLen)
    {
        var body = new byte[13];
        body[0] = EdgeWire.ClipOpenCode;
        BitConverter.GetBytes(id).CopyTo(body, 1);
        BitConverter.GetBytes(textLen).CopyTo(body, 5);
        BitConverter.GetBytes(pngLen).CopyTo(body, 9);
        return body;
    }

    private static byte[] ClipChunk(uint id, byte part, int offset, ReadOnlySpan<byte> data)
    {
        var body = new byte[12 + data.Length];
        body[0] = EdgeWire.ClipChunkCode;
        BitConverter.GetBytes(id).CopyTo(body, 1);
        body[5] = part;
        BitConverter.GetBytes(offset).CopyTo(body, 6);
        BitConverter.GetBytes((ushort)data.Length).CopyTo(body, 10);
        data.CopyTo(body.AsSpan(12));
        return body;
    }

    private static byte[] ClipEnd(uint id)
    {
        var body = new byte[5];
        body[0] = EdgeWire.ClipEndCode;
        BitConverter.GetBytes(id).CopyTo(body, 1);
        return body;
    }

    private static string ClipSignature(string? text, byte[]? png)
    {
        using var sha = SHA256.Create();
        if (!string.IsNullOrEmpty(text))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        if (png != null && png.Length > 0)
            sha.TransformBlock(png, 0, png.Length, null, 0);
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    private void MarkEcho(string signature)
    {
        lock (_echoGate)
        {
            _echoSig = signature;
            _echoUntil = Environment.TickCount64 + 4000;
        }
    }

    /// <summary>Ctrl+X кладёт в буфер Preferred DropEffect = Move. Тогда файлы не шлём — их сейчас переносят здесь.</summary>
    private static bool ClipboardIsCut()
    {
        try
        {
            var data = System.Windows.Clipboard.GetDataObject();
            if (data == null || !data.GetDataPresent("Preferred DropEffect"))
                return false;
            object? raw = data.GetData("Preferred DropEffect");
            byte[] bytes = raw switch
            {
                MemoryStream memory => memory.ToArray(),
                byte[] direct => direct,
                Stream stream => ReadAll(stream),
                _ => Array.Empty<byte>()
            };
            if (bytes.Length < 4)
                return false;
            int effect = BitConverter.ToInt32(bytes, 0);
            return (effect & 2) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] ReadAll(Stream stream)
    {
        if (stream.CanSeek)
            stream.Position = 0;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private bool IsEcho(string signature)
    {
        lock (_echoGate)
            return signature == _echoSig && Environment.TickCount64 < _echoUntil;
    }

    private void BeginIncoming(byte[] body)
    {
        _ignoreId = 0;
        CloseIncoming(delete: true);
        if (body.Length < 8)
            return;
        var incoming = new Incoming
        {
            Id = BitConverter.ToUInt32(body, 1),
            Drop = body[5] != 0,
            Started = Environment.TickCount64
        };
        int count = BitConverter.ToUInt16(body, 6);
        if (count > 500 || count == 0)
            return;
        int pos = 8;
        var names = new string[count];
        var sizes = new long[count];
        string dir = Path.Combine(Path.GetTempPath(), "Zwmenu", "inbox", incoming.Id.ToString());
        Directory.CreateDirectory(dir);
        string root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        for (int i = 0; i < count; i++)
        {
            if (pos + 2 > body.Length)
            {
                TryDelete(dir);
                return;
            }

            int nameLen = BitConverter.ToUInt16(body, pos);
            pos += 2;
            if (nameLen < 1 || pos + nameLen + 8 > body.Length)
            {
                TryDelete(dir);
                return;
            }

            string rel = Encoding.UTF8.GetString(body, pos, nameLen);
            pos += nameLen;
            long size = BitConverter.ToInt64(body, pos);
            pos += 8;
            if (size < 0)
            {
                TryDelete(dir);
                return;
            }

            string dest = SafePath(root, rel);
            if (dest.Length == 0)
            {
                TryDelete(dir);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            names[i] = dest;
            sizes[i] = size;
        }

        incoming.Root = dir;
        incoming.Names = names;
        incoming.Sizes = sizes;
        incoming.Streams = new FileStream?[count];
        incoming.Cursor = new long[count];
        long total = 0;
        for (int i = 0; i < count; i++)
            total += sizes[i];
        incoming.Total = total;
        lock (_inGate)
            _incoming = incoming;
        Report("Со второго компьютера", -1, count, "", 0, total, incoming.Started, force: true, send: false);
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, true); } catch { /* временная папка уже пуста */ }
    }

    private void WriteChunk(byte[] body)
    {
        if (body.Length < 17)
            return;
        uint id = BitConverter.ToUInt32(body, 1);
        if (id == _ignoreId)
            return;
        int index = BitConverter.ToUInt16(body, 5);
        long offset = BitConverter.ToInt64(body, 7);
        int len = BitConverter.ToUInt16(body, 15);
        string name;
        int count;
        long got;
        long total;
        long started;
        lock (_inGate)
        {
            var incoming = _incoming;
            if (incoming == null || id != incoming.Id)
                return;
            if (index < 0 || index >= incoming.Streams.Length || len < 0 || 17 + len > body.Length)
                return;
            var stream = incoming.Streams[index];
            if (stream == null)
            {
                stream = new FileStream(
                    incoming.Names[index],
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete,
                    65536,
                    FileOptions.SequentialScan);
                incoming.Streams[index] = stream;
            }

            if (incoming.Cursor[index] != offset)
                stream.Position = offset;
            stream.Write(body, 17, len);
            incoming.Cursor[index] = offset + len;
            incoming.Got += len;
            name = Path.GetFileName(incoming.Names[index]);
            count = incoming.Names.Length;
            got = incoming.Got;
            total = incoming.Total;
            started = incoming.Started;
        }

        Report("Со второго компьютера", index, count, name, got, total, started, force: false, send: false);
    }

    private void FinishIncoming(byte[] body)
    {
        if (body.Length < 5)
            return;
        uint id = BitConverter.ToUInt32(body, 1);
        if (id == _ignoreId)
        {
            _ignoreId = 0;
            return;
        }

        Incoming? incoming;
        lock (_inGate)
        {
            incoming = _incoming;
            if (incoming == null || id != incoming.Id)
                return;
            _incoming = null;
        }

        var good = new List<string>();
        var missed = new List<string>();
        for (int i = 0; i < incoming.Streams.Length; i++)
        {
            try { incoming.Streams[i]?.Dispose(); } catch { /* уже закрыт */ }
            incoming.Streams[i] = null;
            if (incoming.Sizes[i] == 0 && !File.Exists(incoming.Names[i]))
            {
                try { File.WriteAllBytes(incoming.Names[i], Array.Empty<byte>()); }
                catch { /* пустой файл не обязателен */ }
            }

            bool whole = File.Exists(incoming.Names[i]) && new FileInfo(incoming.Names[i]).Length == incoming.Sizes[i];
            if (whole)
                good.Add(incoming.Names[i]);
            else
            {
                missed.Add(Path.GetFileName(incoming.Names[i]));
                try { File.Delete(incoming.Names[i]); } catch { /* кусок уже не нужен */ }
            }
        }

        if (good.Count == 0)
        {
            TryDelete(incoming.Root);
            EdgeTransferHud.Finish("Ничего не дошло", "Связь оборвалась до конца передачи", false);
            return;
        }

        var paths = TopLevel(incoming.Root, good.ToArray());
        bool drop = incoming.Drop;
        string missedText = "";
        if (missed.Count > 0)
        {
            missedText = string.Join(", ", missed.Take(3));
            if (missed.Count > 3)
                missedText += " и ещё " + (missed.Count - 3);
        }

        Publish(paths, drop, missed.Count, missedText);
    }

    private void Publish(string[] paths, bool drop, int missed, string missedText)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
            return;
        dispatcher.BeginInvoke(() =>
        {
            string[] landed = paths;
            if (drop)
            {
                landed = PlaceOnDesktop(paths);
                if (landed.Length == 0)
                {
                    EdgeTransferHud.Finish("Не легло на стол", "Файл дошёл, но на рабочий стол его положить не вышло", false);
                    return;
                }
            }

            try
            {
                var bag = new StringCollection();
                bag.AddRange(landed);
                MarkEcho(Signature(landed));
                Volatile.Write(ref _suppressNext, 1);
                try
                {
                    System.Windows.Clipboard.SetFileDropList(bag);
                    _ownSequence = GetClipboardSequenceNumber();
                }
                finally
                {
                    Volatile.Write(ref _suppressNext, 0);
                }
            }
            catch
            {
                if (!drop)
                {
                    EdgeTransferHud.Finish("Буфер занят", "Файл уже лежит во временной папке", false);
                    return;
                }
            }

            _readyPaths = landed;
            _dropWanted = false;
            _dropReady = false;
            if (missed > 0)
            {
                EdgeTransferHud.Finish(
                    "Не все файлы дошли",
                    "Есть " + CountFiles(landed.Length) + ". Нет: " + missedText,
                    false);
                return;
            }

            EdgeTransferHud.Finish(
                drop ? "На рабочем столе" : "Можно вставлять",
                drop
                    ? CountFiles(landed.Length)
                    : "Жми Ctrl+V · " + CountFiles(landed.Length),
                true);
        });
    }

    private void TryDrop()
    {
        if (!_dropReady || !_dropWanted || _leftDown)
            return;
        _dropReady = false;
        _dropWanted = false;
        if (CopyIntoFolder(_readyPaths))
        {
            Toast(_readyPaths.Length == 1 ? "Файл переносится в эту папку" : "Файлы переносятся в эту папку");
            return;
        }

        if (IsShellWindow(GetForegroundWindow()))
        {
            PasteIntoWindow?.Invoke();
            Toast(_readyPaths.Length == 1 ? "Файл вставлен" : "Файлы вставлены");
            return;
        }

        Toast(_readyPaths.Length == 1 ? "Файл здесь. Жми Ctrl+V" : "Файлы здесь. Жми Ctrl+V");
    }

    private void CloseIncoming(bool delete)
    {
        Incoming? incoming;
        lock (_inGate)
        {
            incoming = _incoming;
            _incoming = null;
        }

        if (incoming == null)
            return;
        foreach (var stream in incoming.Streams)
        {
            try { stream?.Dispose(); } catch { /* уже закрыт */ }
        }

        if (!delete || incoming.Root.Length == 0)
            return;
        try { Directory.Delete(incoming.Root, true); } catch { /* папка ещё занята */ }
    }

    private List<(string Full, string Rel, long Size)>? Expand(List<string> roots, out bool capped)
    {
        var files = new List<string>();
        var folders = new List<string>();
        capped = false;
        foreach (string root in roots)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(root))
                    continue;
                if (File.Exists(root))
                {
                    if (files.Count >= 500)
                    {
                        capped = true;
                        break;
                    }

                    files.Add(Path.GetFullPath(root));
                }
                else if (Directory.Exists(root))
                {
                    string folder = Path.GetFullPath(root);
                    folders.Add(folder);
                    foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                    {
                        if (files.Count >= 500)
                        {
                            capped = true;
                            break;
                        }

                        files.Add(Path.GetFullPath(file));
                    }
                }
            }
            catch
            {
                /* битый путь пропускаем */
            }

            if (capped)
                break;
        }

        files = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0)
            return new List<(string, string, long)>();

        string baseDir = folders.Count == 1 && roots.Count == 1
            ? Path.GetDirectoryName(folders[0]) ?? folders[0]
            : CommonDirectory(files.Select(Path.GetDirectoryName).Where(d => d != null).Cast<string>().ToList());
        long total = 0;
        var list = new List<(string, string, long)>(files.Count);
        foreach (string file in files)
        {
            long size = new FileInfo(file).Length;
            total += size;
            if (total > 4L * 1024 * 1024 * 1024)
                return null;
            string rel = Path.GetRelativePath(baseDir, file);
            if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
                rel = Path.GetFileName(file);
            list.Add((file, rel, size));
        }

        return list;
    }

    private static string CommonDirectory(List<string> dirs)
    {
        if (dirs.Count == 0)
            return "";
        string prefix = dirs[0];
        foreach (string dir in dirs.Skip(1))
        {
            while (prefix.Length > 0
                && !dir.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string? parent = Path.GetDirectoryName(prefix.TrimEnd('\\'));
                if (string.IsNullOrEmpty(parent) || parent == prefix)
                {
                    prefix = "";
                    break;
                }

                prefix = parent;
            }
        }

        return prefix.Length == 0 ? Path.GetPathRoot(dirs[0]) ?? "" : prefix;
    }

    private static byte[]? BuildManifest(uint id, bool drop, List<(string Full, string Rel, long Size)> files)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(EdgeWire.FileListCode);
        ms.Write(BitConverter.GetBytes(id));
        ms.WriteByte(drop ? (byte)1 : (byte)0);
        ms.Write(BitConverter.GetBytes((ushort)files.Count));
        foreach (var file in files)
        {
            byte[] name = Encoding.UTF8.GetBytes(file.Rel.Replace('/', '\\'));
            if (name.Length == 0 || name.Length > 1024)
                return null;
            ms.Write(BitConverter.GetBytes((ushort)name.Length));
            ms.Write(name);
            ms.Write(BitConverter.GetBytes(file.Size));
        }

        if (ms.Length > MaxManifest)
            return null;
        return ms.ToArray();
    }

    private static void FillChunk(byte[] body, uint id, ushort index, long offset, ReadOnlySpan<byte> data)
    {
        body[0] = EdgeWire.FileChunkCode;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1), id);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(5), index);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(7), offset);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(15), (ushort)data.Length);
        data.CopyTo(body.AsSpan(17));
    }

    private static byte[] FileEnd(uint id)
    {
        var body = new byte[5];
        body[0] = EdgeWire.FileEndCode;
        BitConverter.GetBytes(id).CopyTo(body, 1);
        return body;
    }

    private static string SafePath(string rootWithSlash, string relative)
    {
        string cleaned = relative.Replace('/', '\\').TrimStart('\\');
        if (cleaned.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(cleaned))
            cleaned = Path.GetFileName(cleaned);
        if (cleaned.Length == 0)
            return "";
        string full = Path.GetFullPath(Path.Combine(rootWithSlash, cleaned));
        return full.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase) ? full : "";
    }

    private static string Signature(IEnumerable<string> paths) =>
        string.Join("|", paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));

    private static string[] TopLevel(string root, string[] files)
    {
        var tops = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            string rel = Path.GetRelativePath(root, file);
            int cut = rel.IndexOfAny(new[] { '\\', '/' });
            string top = cut < 0 ? rel : rel[..cut];
            if (top.Length == 0 || top is "." or "..")
                continue;
            string full = Path.GetFullPath(Path.Combine(root, top));
            if (seen.Add(full))
                tops.Add(full);
        }

        return tops.Count == 0 ? files : tops.ToArray();
    }

    private static bool CopyIntoFolder(string[] paths)
    {
        string? folderPath = FolderUnderCursor();
        if (string.IsNullOrWhiteSpace(folderPath))
            return false;
        try
        {
            Type? kind = Type.GetTypeFromProgID("Shell.Application");
            if (kind == null)
                return false;
            dynamic shell = Activator.CreateInstance(kind)!;
            dynamic folder = shell.NameSpace(folderPath);
            if (folder == null)
                return false;
            const int flags = 4 | 16 | 512;
            foreach (string path in paths)
            {
                if (File.Exists(path) || Directory.Exists(path))
                    folder.CopyHere(path, flags);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FolderUnderCursor()
    {
        if (!GetCursorPos(out var point))
            return null;
        IntPtr window = WindowFromPoint(point);
        if (window == IntPtr.Zero)
            return null;
        IntPtr root = GetAncestor(window, 2);
        if (root == IntPtr.Zero)
            root = window;
        if (IsDesktopClass(ClassOf(root)) || IsDesktopClass(ClassOf(window)))
            return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return ExplorerFolder(root);
    }

    private static string? ExplorerFolder(IntPtr root)
    {
        try
        {
            Type? kind = Type.GetTypeFromProgID("Shell.Application");
            if (kind == null)
                return null;
            dynamic shell = Activator.CreateInstance(kind)!;
            foreach (dynamic window in shell.Windows())
            {
                try
                {
                    if (Convert.ToInt64(window.HWND) != root.ToInt64())
                        continue;
                    string path = window.Document.Folder.Self.Path;
                    if (Directory.Exists(path))
                        return path;
                }
                catch
                {
                    /* это окно не папка */
                }
            }
        }
        catch
        {
            /* оболочка занята */
        }

        return null;
    }

    private static string[] PlaceOnDesktop(string[] paths)
    {
        string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desk))
            return Array.Empty<string>();
        Directory.CreateDirectory(desk);
        var placed = new List<string>(paths.Length);
        foreach (string path in paths)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;
                string name = Path.GetFileName(path.TrimEnd('\\', '/'));
                if (name.Length == 0)
                    continue;
                string dest = UniqueDesktopPath(Path.Combine(desk, name));
                if (File.Exists(path))
                    File.Copy(path, dest);
                else if (Directory.Exists(path))
                    CopyTree(path, dest);
                else
                    continue;
                placed.Add(dest);
            }
            catch
            {
                /* один битый файл не отменяет остальные */
            }
        }

        return placed.ToArray();
    }

    private static string UniqueDesktopPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return path;
        string? dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
            return path;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 2; i < 1000; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }

        return Path.Combine(dir, name + " " + Guid.NewGuid().ToString("N")[..6] + ext);
    }

    private static void CopyTree(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (string file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        foreach (string dir in Directory.EnumerateDirectories(source))
            CopyTree(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    private static List<string> SelectedFiles()
    {
        var found = new List<string>();
        try
        {
            foreach (string path in ShellSelectionService.GetSelectedFiles())
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;
                if (File.Exists(path) || Directory.Exists(path))
                    found.Add(path);
            }
        }
        catch
        {
            /* рабочий стол через автоматизацию иногда недоступен */
        }

        if (found.Count > 0)
            return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        try
        {
            IntPtr foreground = GetForegroundWindow();
            bool desktop = IsDesktopClass(ClassOf(foreground));
            string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            Type? kind = Type.GetTypeFromProgID("Shell.Application");
            if (kind == null)
                return found;
            dynamic shell = Activator.CreateInstance(kind)!;
            foreach (dynamic window in shell.Windows())
            {
                try
                {
                    bool match = false;
                    try
                    {
                        match = Convert.ToInt64(window.HWND) == foreground.ToInt64();
                    }
                    catch
                    {
                        /* у окна нет номера */
                    }

                    if (!match && desktop)
                    {
                        string path = "";
                        try { path = (string)window.Document.Folder.Self.Path; } catch { /* не папка */ }
                        match = path.Length > 0 && string.Equals(path, desk, StringComparison.OrdinalIgnoreCase);
                        if (!match)
                        {
                            string loc = "";
                            try { loc = (string)window.LocationName; } catch { /* нет имени */ }
                            match = loc.Equals("Desktop", StringComparison.OrdinalIgnoreCase)
                                || loc.Equals("Рабочий стол", StringComparison.OrdinalIgnoreCase);
                        }
                    }

                    if (!match)
                        continue;
                    foreach (dynamic item in window.Document.SelectedItems())
                    {
                        string path = item.Path;
                        if (!string.IsNullOrWhiteSpace(path))
                            found.Add(path);
                    }

                    if (found.Count > 0)
                        break;
                }
                catch
                {
                    /* это окно не проводник */
                }
            }
        }
        catch
        {
            /* оболочка занята */
        }

        return found;
    }

    private static bool IsDesktopClass(string name) =>
        name is "Progman" or "WorkerW" or "SysListView32" or "SHELLDLL_DefView";

    private static string ClassOf(IntPtr window)
    {
        if (window == IntPtr.Zero)
            return "";
        var name = new StringBuilder(64);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }

    private static bool IsShellWindow(IntPtr window)
    {
        string name = ClassOf(window);
        return name is "CabinetWClass" or "ExploreWClass" or "Progman" or "WorkerW";
    }

    private static void Toast(string text)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
            return;
        dispatcher.BeginInvoke(() =>
        {
            var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).Bounds;
            ScreenshotToast.Show(text, screen, 2.2);
        });
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private sealed class Incoming
    {
        public uint Id;
        public bool Drop;
        public string Root = "";
        public string[] Names = Array.Empty<string>();
        public long[] Sizes = Array.Empty<long>();
        public FileStream?[] Streams = Array.Empty<FileStream?>();
        public long[] Cursor = Array.Empty<long>();
        public long Total;
        public long Got;
        public long Started;
    }

    private sealed class ClipIncoming
    {
        public uint Id;
        public int TextLen;
        public int PngLen;
        public MemoryStream Text { get; } = new();
        public MemoryStream Png { get; } = new();
    }
}
