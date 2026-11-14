using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zwmenu.Services;

public sealed record EdgePeer(string Name, string Host, int Port);

public sealed class EdgePairAsk
{
    public string Name { get; init; } = "";
    public string Code { get; init; } = "";
    internal TaskCompletionSource<bool> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Одна мышь на два своих ноутбука в одной сети.
/// Связь только после согласия на втором компьютере. Через интернет и через чужие сети само не ходит.
/// </summary>
public sealed class EdgeShareService : IDisposable
{
    public const int TcpPort = 47655;
    public const int UdpPort = 47656;

    private static readonly byte[] Magic = "ZWMEDGE1"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private readonly EdgeCapture _capture = new();
    private readonly ConcurrentQueue<byte[]> _outgoing = new();
    private readonly ConcurrentQueue<byte[]> _bulk = new();
    private int _bulkQueued;
    private readonly EdgeFiles _files;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly object _gate = new();
    private readonly Dictionary<string, (EdgePeer Peer, long Seen)> _peers = new(StringComparer.OrdinalIgnoreCase);

    private EdgeSettings _settings = new();
    private CancellationTokenSource? _run;
    private TcpListener? _listener;
    private UdpClient? _udp;
    private Link? _link;
    private EdgePairAsk? _pending;
    private int _gen;
    private bool _running;
    private bool _busy;
    private int _session;
    private bool _disposed;
    private System.Windows.Threading.DispatcherTimer? _layoutTimer;
    private uint _layoutSent;
    private long _layoutQuietUntil;
    private long _layoutHoldOwnUntil;

    public static EdgeShareService Shared { get; } = new();

    private EdgeShareService()
    {
        _files = new EdgeFiles(EnqueueBulk, RentBulk) { PasteIntoWindow = PasteIntoForeground };
        Zwmenu.Overlay.EdgeTransferHud.Cancel = () => _files.CancelActive();
        _capture.Sink = Enqueue;
        Zwmenu.Overlay.EdgeCarryCue.HealCursor();
        _files.CarryVisual = label =>
        {
            if (string.IsNullOrEmpty(label))
                Enqueue(EdgeWire.CarryEnd());
            else
                Enqueue(EdgeWire.Carry(label));
        };
        _capture.OnArmSelection = () =>
        {
            // Крючок раньше, чем проводник выделит значок. Читаем чуть позже, уже после клика.
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
                        try { _files.RememberSelection(); }
                        catch { /* оболочка занята */ }
                    }));
                }
                catch { /* окно уже закрыто */ }
            });
        };
        _capture.OnEdgeCross = () =>
        {
            _files.BeginGesture();
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            dispatcher?.BeginInvoke(new Action(() =>
            {
                try { SendLayout(force: true); }
                catch { /* раскладка не обязательна */ }
                // Кнопка ещё зажата: только запоминаем файлы. Байты уйдут, когда её отпустят.
                try { _files.ArmCarry(); }
                catch { /* оболочка занята */ }
                try { _capture.ReleaseShellDrag(clearSelection: true, releaseButton: false); }
                catch { /* оболочка занята */ }
            }));
        };
        _capture.OnCarryRelease = () =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;
            dispatcher.BeginInvoke(new Action(() =>
            {
                bool sent = false;
                try { sent = _files.CommitCarry(); }
                catch { /* оболочка занята */ }
                Zwmenu.Overlay.EdgeCarryCue.Hide();
                Enqueue(EdgeWire.CarryEnd());
                if (!sent)
                    Enqueue(EdgeWire.CarryCommit());
            }));
        };
        _capture.OnStoppedHosting = () => Zwmenu.Overlay.EdgeCarryCue.Hide();
        _capture.OnForceReturn = () =>
        {
            try { _files.CancelCarry(); }
            catch { /* жест уже сброшен */ }
            if (_capture.Captured)
                Enqueue(EdgeWire.Home());
            else
                Enqueue(EdgeWire.Back(_capture.CurrentYRatio()));
        };
        _capture.PeerAlive = PeerAliveNow;
        _capture.OnEdgeAsleep = TellAsleep;
        Load();
        _capture.PeerOnRight = _settings.PeerOnRight;
    }

    public event Action? Changed;

    public bool Running => _running;
    public bool Linked => _link != null;
    public bool Transferring => _files.Live;

    public bool TrySend(byte[] body)
    {
        if (_link == null || body == null || body.Length == 0 || body.Length > 65_000)
            return false;
        Enqueue(body);
        return true;
    }

    public void PauseTransfers()
    {
        var app = System.Windows.Application.Current;
        if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(PauseTransfers);
            return;
        }

        try { _files.CancelActive(); }
        catch { /* передача уже кончилась */ }
    }
    public bool PeerOnRight => _settings.PeerOnRight;
    public bool IsArmed => _settings.Armed;
    public string MachineName { get; } = Environment.MachineName;
    public string Status { get; private set; } = "Выключено";
    public string? OutgoingCode { get; private set; }
    public string? PeerName => _settings.PeerName;
    public EdgePairAsk? Pending => _pending;
    public IReadOnlyList<string> LocalAddresses { get; private set; } = Array.Empty<string>();

    public IReadOnlyList<EdgePeer> Peers
    {
        get
        {
            lock (_gate)
                return _peers.Values.Select(v => v.Peer).OrderBy(p => p.Name).ToList();
        }
    }

    public void Start()
    {
        if (_disposed || _running)
            return;
        _session++;
        _settings.Armed = true;
        Save();
        _running = true;
        _run = new CancellationTokenSource();
        LocalAddresses = ReadAddresses();
        Status = "Ищем второй ноутбук в этой сети";
        var token = _run.Token;
        try
        {
            _listener = new TcpListener(IPAddress.Any, TcpPort);
            _listener.Start();
            _udp = new UdpClient(UdpPort) { EnableBroadcast = true };
        }
        catch (Exception ex)
        {
            try { _listener?.Stop(); } catch { /* не открылся */ }
            try { _udp?.Dispose(); } catch { /* не открылся */ }
            _listener = null;
            _udp = null;
            _running = false;
            Status = "Не удалось открыть сеть: " + ex.Message;
            Raise();
            return;
        }

        _ = Task.Run(() => AcceptLoop(token));
        _ = Task.Run(() => BeaconLoop(token));
        _ = Task.Run(() => DiscoverLoop(token));
        _ = Task.Run(() => MaintainLoop(token));
        SyncEdgeHook();
        Raise();
    }

    public void Toggle()
    {
        if (Running)
            Stop();
        else
            Start();
    }

    public void Stop() => ShutdownNetwork("Два экрана выключены");

    public void Forget()
    {
        _settings.Token = null;
        _settings.PeerName = null;
        _settings.PeerHost = null;
        _settings.Armed = true;
        Save();
        try { _link?.Client.Dispose(); } catch { /* уже закрыт */ }
        SyncEdgeHook();
        Status = "Второй ноутбук забыт. Ищем снова.";
        Raise();
    }

    public void SetPeerOnRight(bool onRight)
    {
        _settings.PeerOnRight = onRight;
        _capture.PeerOnRight = onRight;
        Save();
        Raise();
    }

    public void ConnectPeer(EdgePeer peer)
    {
        bool same = string.Equals(_settings.PeerName, peer.Name, StringComparison.OrdinalIgnoreCase);
        _settings.PeerName = peer.Name;
        _settings.PeerHost = peer.Host;
        _settings.PeerPort = peer.Port;
        _ = Dial(peer.Host, peer.Port, pairing: !(same && _settings.TokenBytes != null));
    }

    public void ConnectHost(string host)
    {
        host = (host ?? "").Trim();
        if (host.Length == 0)
            return;
        _ = Dial(host, TcpPort, pairing: true);
    }

    public void Respond(bool allow)
    {
        var ask = _pending;
        _pending = null;
        ask?.Done.TrySetResult(allow);
        Raise();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ShutdownNetwork(null);
        _capture.Dispose();
        _files.Dispose();
        _wake.Dispose();
    }

    private async Task Dial(string host, int port, bool pairing)
    {
        if (_link != null || _busy || !_running)
        {
            if (_running)
            {
                Status = "Уже есть соединение";
                Raise();
            }

            return;
        }

        int session = _session;
        TcpClient? client = null;
        bool handed = false;
        try
        {
            _busy = true;
            OutgoingCode = null;
            Status = "Подключаемся к " + host;

            Raise();
            client = new TcpClient { NoDelay = true };
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(wait.Token, _run?.Token ?? CancellationToken.None);
            await client.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
            if (session != _session || !_running)
            {
                client.Dispose();
                return;
            }

            handed = true;
            await RunClient(client, pairing, session).ConfigureAwait(false);
        }
        catch (Exception)
        {
            try { client?.Dispose(); } catch { /* уже закрыт */ }
            ReleaseBusy(session);
            if (session != _session || !_running)
                return;
            OutgoingCode = null;
            if (_link == null)
                Status = "Второй ноутбук не отвечает. Открой на нём Zwmenu и включи «Два экрана».";
            Raise();
        }
        finally
        {
            if (!handed)
                ReleaseBusy(session);
        }
    }

    private void ReleaseBusy(int session)
    {
        if (session == _session && _link == null)
            _busy = false;
    }

    private async Task AcceptLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener != null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            client.NoDelay = true;
            int session = _session;
            _ = Task.Run(() => RunServer(client, session), token);
        }
    }

    private async Task RunServer(TcpClient client, int session)
    {
        try
        {
            if (_link != null || session != _session || !_running)
            {
                client.Dispose();
                return;
            }

            var stream = client.GetStream();
            if (!await ReadMagic(stream).ConfigureAwait(false))
            {
                client.Dispose();
                return;
            }

            await stream.WriteAsync(Magic).ConfigureAwait(false);
            var helloBody = await ReadFrame(stream, CancellationToken.None).ConfigureAwait(false);
            if (helloBody == null || !EdgeWire.TryHello(helloBody, out bool peerOnRight, out string name, out byte[] token, out _))
            {
                client.Dispose();
                return;
            }

            bool known = _settings.TokenBytes is { Length: 16 } saved
                && token.Length == 16
                && saved.AsSpan().SequenceEqual(token);
            if (known)
            {
                RememberRemote(client, name);
                await WriteBody(stream, EdgeWire.Accept(_settings.TokenBytes!)).ConfigureAwait(false);
            }
            else if (CanAdopt(name))
            {
                byte[] fresh = token.Length == 16 ? token : RandomBytes(16);
                _settings.Token = Convert.ToBase64String(fresh);
                _settings.PeerName = name;
                if (string.IsNullOrEmpty(_settings.PeerHost))
                    _settings.PeerOnRight = !peerOnRight;
                RememberRemote(client, name);
                _capture.PeerOnRight = _settings.PeerOnRight;
                Save();
                await WriteBody(stream, EdgeWire.Accept(fresh)).ConfigureAwait(false);
            }
            else
            {
                await WriteBody(stream, EdgeWire.Deny()).ConfigureAwait(false);
                client.Dispose();
                return;
            }

            if (session != _session || !_running)
            {
                client.Dispose();
                return;
            }

            await Pump(client, stream, session).ConfigureAwait(false);
        }
        catch
        {
            try { client.Dispose(); } catch { /* уже закрыт */ }
        }
    }

    private async Task RunClient(TcpClient client, bool pairing, int session)
    {
        if (session != _session || !_running)
        {
            client.Dispose();
            ReleaseBusy(session);
            return;
        }

        var stream = client.GetStream();
        await stream.WriteAsync(Magic).ConfigureAwait(false);
        if (!await ReadMagic(stream).ConfigureAwait(false))
            throw new IOException("чужой ответ");

        byte[]? token = pairing ? null : _settings.TokenBytes;
        await WriteBody(stream, EdgeWire.Hello(_settings.PeerOnRight, MachineName, token, "")).ConfigureAwait(false);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var reply = await ReadFrame(stream, limit.Token).ConfigureAwait(false);
        if (reply == null || reply.Length == 0 || reply[0] == EdgeWire.DenyCode)
        {
            client.Dispose();
            OutgoingCode = null;
            ReleaseBusy(session);
            Status = "Второй ноутбук отклонил связь";
            Raise();
            return;
        }

        if (reply[0] != EdgeWire.AcceptCode || reply.Length != 17)
            throw new IOException("не тот ответ");

        var accepted = reply[1..];
        _settings.Token = Convert.ToBase64String(accepted);
        RememberRemote(client, _settings.PeerName ?? "");
        OutgoingCode = null;
        Save();
        if (session != _session || !_running)
        {
            client.Dispose();
            ReleaseBusy(session);
            return;
        }

        await Pump(client, stream, session).ConfigureAwait(false);
    }

    private async Task Pump(TcpClient client, NetworkStream stream, int session)
    {
        Link? link = null;
        lock (_gate)
        {
            if (_link != null || session != _session || !_running)
            {
                client.Dispose();
                return;
            }

            link = new Link(client, stream, ++_gen);
            _link = link;
            _busy = true;
        }

        try
        {
            while (_outgoing.TryDequeue(out _)) { }
            while (_bulk.TryDequeue(out _)) { }
            _bulkQueued = 0;
            OutgoingCode = null;
            Status = "Связано" + (string.IsNullOrEmpty(_settings.PeerName) ? "" : " с " + _settings.PeerName);
            Hear();
            Ui(() =>
            {
                _capture.PeerOnRight = _settings.PeerOnRight;
                try { _files.Start(); }
                catch (Exception ex) { Status = ex.Message; }
                StartLayoutWatch();
                SyncEdgeHook();
            });
            Raise();

            var token = link.Stop.Token;
            var reading = ReadLoop(link, token);
            var writing = WriteLoop(link, token);
            var ping = PingLoop(link, token);
            await Task.WhenAny(reading, writing).ConfigureAwait(false);
            try { link.Stop.Cancel(); } catch { /* уже остановлен */ }
            try { client.Dispose(); } catch { /* уже закрыт */ }
            await Task.WhenAll(reading, writing, ping).ContinueWith(_ => { }).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (link != null && ReferenceEquals(_link, link))
                    _link = null;
            }

            if (session == _session)
            {
                _busy = false;
                try { _capture.EmergencyRelease(); } catch { /* курсор уже свободен */ }
                Ui(() =>
                {
                    StopLayoutWatch();
                    Zwmenu.Overlay.EdgeCarryCue.Hide();
                    _files.Stop();
                    try { _capture.ReleaseShellDrag(clearSelection: false, releaseButton: _capture.LeftHeld); }
                    catch { /* связь уже нет */ }
                });
                if (_running && _link == null)
                    Status = "Связь прервалась. Ищем второй ноутбук снова.";
                if (_running)
                    SyncEdgeHook();
                Raise();
            }
        }
    }

    private async Task ReadLoop(Link link, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var body = await ReadFrame(link.Stream, token).ConfigureAwait(false);
                if (body == null || body.Length == 0)
                    return;
                link.LastRx = Environment.TickCount64;
                Hear();
                switch (body[0])
                {
                    case EdgeWire.MoveCode when body.Length == 5:
                        int dx = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(1));
                        int dy = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(3));
                        _capture.ApplyMove(dx, dy);
                        if (_capture.ReturnAllowed && _capture.HitReturnEdge(dx))
                        {
                            int ratio = _capture.CurrentYRatio();
                            bool dragging = _capture.LeftHeld;
                            _capture.Hosting = false;
                            // Сначала вернуть курсор. Опрос проводника до Back держал бы
                            // интерфейс, и на первом ноутбуке мышь замирала.
                            Enqueue(EdgeWire.Back(ratio));
                            if (dragging)
                            {
                                _files.BeginGesture();
                                System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                                    System.Windows.Threading.DispatcherPriority.Background,
                                    new Action(() =>
                                    {
                                        try { _files.ArmCarry(); }
                                        catch { /* оболочка занята */ }
                                        try { _capture.ReleaseShellDrag(clearSelection: true, releaseButton: true); }
                                        catch { /* оболочка занята */ }
                                    }));
                            }
                        }
                        break;
                    case EdgeWire.ButtonCode when body.Length == 3:
                        _capture.ApplyButton(body[1], body[2] != 0);
                        if (body[1] == 1)
                        {
                            bool down = body[2] != 0;
                            _files.NoteLeft(down);
                            // Отпустили уже на краю возврата: клик догнал нас после того,
                            // как курсор ушёл. Файлы живут здесь, отправляем их отсюда.
                            if (!down && !_capture.Hosting)
                            {
                                Ui(() =>
                                {
                                    bool sent = false;
                                    try { sent = _files.CommitCarry(); }
                                    catch { /* оболочка занята */ }
                                    if (sent)
                                        Enqueue(EdgeWire.CarryEnd());
                                });
                            }
                        }
                        break;
                    case EdgeWire.WheelCode when body.Length == 3:
                        _capture.ApplyWheel(BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(1)));
                        break;
                    case EdgeWire.KeyCode when body.Length == 7:
                        _capture.ApplyKey(
                            BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(3)),
                            BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(5)),
                            body[1] != 0,
                            body[2] != 0);
                        break;
                    case EdgeWire.EnterCode when body.Length == 3:
                        int enterRatio = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(1));
                        Ui(() =>
                        {
                            // Вернулись, не отпустив кнопку: то, что держали отсюда, уже не отправляем.
                            try { _files.CancelCarry(); }
                            catch { /* жест уже сброшен */ }
                            _capture.PlaceOnEntry(enterRatio);
                        });
                        break;
                    case EdgeWire.BackCode when body.Length == 3:
                        int backRatio = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(1));
                        Ui(() =>
                        {
                            try { _files.CancelCarry(); }
                            catch { /* жест уже сброшен */ }
                            _capture.ReturnHere(backRatio);
                        });
                        break;
                    case EdgeWire.HomeCode:
                        _capture.ForceUnhost();
                        Ui(() =>
                        {
                            try { _files.CancelCarry(); }
                            catch { /* жест уже сброшен */ }
                        });
                        break;
                    case EdgeWire.CarryCommitCode:
                        Ui(() =>
                        {
                            bool sent = false;
                            try { sent = _files.CommitCarry(); }
                            catch { /* оболочка занята */ }
                            if (sent)
                                Enqueue(EdgeWire.CarryEnd());
                        });
                        break;
                    case EdgeWire.CarryCode:
                        Ui(() =>
                        {
                            if (body.Length < 2 || body[1] == 0)
                            {
                                Zwmenu.Overlay.EdgeCarryCue.Hide();
                                return;
                            }

                            string label = Encoding.UTF8.GetString(body, 2, body.Length - 2);
                            // Подпись обратного переноса приехала сюда, а кнопку уже отпустили.
                            if (!_capture.Hosting && !_capture.LeftButtonDown)
                            {
                                Enqueue(EdgeWire.CarryCommit());
                                return;
                            }

                            Zwmenu.Overlay.EdgeCarryCue.Show(label);
                            if (!_capture.Hosting)
                                _capture.HoldForCarryRelease();
                        });
                        break;
                    case EdgeWire.WorkshopCode:
                        try { WorkshopHub.Handle(body); }
                        catch { /* чужой кадр не рвёт связь */ }
                        break;
                    case EdgeWire.PingCode:
                        Enqueue(EdgeWire.Pong());
                        break;
                    case EdgeWire.LayoutCode when body.Length == 5:
                        uint layout = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(1));
                        Ui(() => ApplyRemoteLayout(layout));
                        break;
                    default:
                        if (_files.IsFileFrame(body))
                            await _files.EnqueueDisk(body).ConfigureAwait(false);
                        else
                            _files.Handle(body);
                        break;
                }
            }
        }
        catch
        {
            /* обрыв: Pump закроет сокет */
        }
    }

    private async Task WriteLoop(Link link, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _wake.WaitAsync(token).ConfigureAwait(false);
                while (true)
                {
                    bool wrote = false;
                    while (_outgoing.TryDequeue(out var body))
                    {
                        wrote = true;
                        await WriteBody(link.Stream, body, token, recycle: false).ConfigureAwait(false);
                    }

                    if (_bulk.TryDequeue(out var chunk))
                    {
                        wrote = true;
                        if (Interlocked.Decrement(ref _bulkQueued) < 0)
                            Interlocked.Exchange(ref _bulkQueued, 0);
                        await WriteBody(link.Stream, chunk, token, recycle: chunk.Length == EdgeWire.FilePacketSize).ConfigureAwait(false);
                    }

                    if (!wrote)
                        break;
                }
            }
        }
        catch
        {
            /* обрыв */
        }
    }

    private async Task PingLoop(Link link, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(2000, token).ConfigureAwait(false);
                Enqueue(EdgeWire.Ping());
                if (Environment.TickCount64 - link.LastRx > 7000)
                    link.Stop.Cancel();
            }
        }
        catch
        {
            /* останов */
        }
    }

    private async Task BeaconLoop(CancellationToken token)
    {
        var text = Encoding.UTF8.GetBytes("ZWM1|" + TcpPort + "|" + MachineName.Replace('|', ' '));
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_udp != null)
                {
                    foreach (var address in Broadcasts())
                        _udp.Send(text, text.Length, new IPEndPoint(address, UdpPort));
                }
            }
            catch
            {
                /* этот интерфейс не шлёт */
            }

            try { await Task.Delay(1000, token).ConfigureAwait(false); }
            catch { return; }
        }
    }

    private async Task DiscoverLoop(CancellationToken token)
    {
        if (_udp == null)
            return;
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await _udp.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            string text;
            try { text = Encoding.UTF8.GetString(packet.Buffer); }
            catch { continue; }
            var parts = text.Split('|');
            if (parts.Length != 3 || parts[0] != "ZWM1")
                continue;
            if (!int.TryParse(parts[1], out int port))
                continue;
            string name = parts[2].Trim();
            if (name.Length == 0 || string.Equals(name, MachineName, StringComparison.OrdinalIgnoreCase))
                continue;
            var peer = new EdgePeer(name, packet.RemoteEndPoint.Address.ToString(), port);
            bool changed = false;
            lock (_gate)
            {
                _peers[name] = (peer, Environment.TickCount64);
                if (string.Equals(name, _settings.PeerName, StringComparison.OrdinalIgnoreCase)
                    && _settings.PeerHost != peer.Host)
                {
                    _settings.PeerHost = peer.Host;
                    _settings.PeerPort = port;
                    changed = true;
                }
            }

            if (changed)
                Save();
            Raise();
        }
    }

    private async Task MaintainLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(2000, token).ConfigureAwait(false); }
            catch { return; }

            long now = Environment.TickCount64;
            bool dropped = false;
            lock (_gate)
            {
                foreach (var stale in _peers.Where(p => now - p.Value.Seen > 5000).Select(p => p.Key).ToList())
                {
                    _peers.Remove(stale);
                    dropped = true;
                }
            }

            if (dropped)
                Raise();
            if (_link != null || _busy)
                continue;
            if (!TryPickPeer(out string host, out int port, out string name, out bool haveToken))
                continue;
            if (!ShouldDial(name, host))
                continue;
            if (!string.Equals(_settings.PeerName, name, StringComparison.OrdinalIgnoreCase) || _settings.PeerHost != host)
            {
                _settings.PeerName = name;
                _settings.PeerHost = host;
                _settings.PeerPort = port;
                Save();
            }

            _ = Dial(host, port, pairing: !haveToken);
        }
    }

    private bool CanAdopt(string name)
    {
        if (_settings.TokenBytes == null)
            return true;
        return !string.IsNullOrWhiteSpace(_settings.PeerName)
            && string.Equals(name, _settings.PeerName, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryPickPeer(out string host, out int port, out string name, out bool haveToken)
    {
        host = "";
        port = TcpPort;
        name = "";
        haveToken = _settings.TokenBytes != null;
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(_settings.PeerName))
            {
                name = _settings.PeerName;
                host = _settings.PeerHost ?? "";
                port = _settings.PeerPort == 0 ? TcpPort : _settings.PeerPort;
                foreach (var seen in _peers)
                {
                    if (!string.Equals(seen.Key, name, StringComparison.OrdinalIgnoreCase))
                        continue;
                    host = seen.Value.Peer.Host;
                    port = seen.Value.Peer.Port;
                    break;
                }
            }
            else if (_peers.Count > 0)
            {
                var peer = _peers.Values.Select(v => v.Peer).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).First();
                name = peer.Name;
                host = peer.Host;
                port = peer.Port;
            }
        }

        return !string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(name);
    }

    private bool ShouldDial(string peerName, string peerHost)
    {
        int order = string.Compare(MachineName, peerName ?? "", StringComparison.OrdinalIgnoreCase);
        if (order < 0)
            return true;
        if (order > 0)
            return false;
        string mine = LocalAddresses.FirstOrDefault() ?? "";
        return string.Compare(mine, peerHost ?? "", StringComparison.OrdinalIgnoreCase) < 0;
    }

    private void Enqueue(byte[] body) => Queue(_outgoing, body);

    private bool EnqueueBulk(byte[] body)
    {
        if (body.Length == 0 || body.Length > 65_000)
            return false;
        while (Volatile.Read(ref _bulkQueued) > 24)
        {
            if (_link == null)
                return false;
            Thread.Sleep(1);
        }

        if (_link == null)
            return false;
        Interlocked.Increment(ref _bulkQueued);
        Queue(_bulk, body);
        return true;
    }

    private void Queue(ConcurrentQueue<byte[]> queue, byte[] body)
    {
        queue.Enqueue(body);
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* писатель уже проснётся */ }
    }

    private void PasteIntoForeground()
    {
        _capture.ApplyKey(0x11, 0, true, false);
        _capture.ApplyKey(0x56, 0, true, false);
        _capture.ApplyKey(0x56, 0, false, false);
        _capture.ApplyKey(0x11, 0, false, false);
    }

    private void StartLayoutWatch()
    {
        StopLayoutWatch();
        _layoutTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _layoutTimer.Tick += (_, _) => SendLayout();
        _layoutTimer.Start();
        bool lead = string.Compare(MachineName, _settings.PeerName ?? "", StringComparison.OrdinalIgnoreCase) <= 0;
        if (lead)
        {
            _layoutHoldOwnUntil = Environment.TickCount64 + 1500;
            SendLayout(force: true);
        }
        else
            _layoutQuietUntil = Environment.TickCount64 + 1500;
    }

    private void StopLayoutWatch()
    {
        if (_layoutTimer == null)
            return;
        _layoutTimer.Stop();
        _layoutTimer = null;
    }

    private void SendLayout(bool force = false)
    {
        if (_link == null)
            return;
        uint hkl = EdgeLayout.Foreground();
        if (hkl == 0)
            return;
        if (!force && (Environment.TickCount64 < _layoutQuietUntil || EdgeLayout.Same(hkl, _layoutSent)))
            return;
        _layoutSent = hkl;
        Enqueue(EdgeWire.Layout(hkl));
    }

    private void ApplyRemoteLayout(uint hkl)
    {
        if (hkl == 0 || Environment.TickCount64 < _layoutHoldOwnUntil)
            return;
        uint current = EdgeLayout.Foreground();
        if (EdgeLayout.Same(current, hkl))
        {
            _layoutSent = current;
            return;
        }

        _layoutQuietUntil = Environment.TickCount64 + 1000;
        if (!EdgeLayout.Apply(hkl))
        {
            _layoutSent = current;
            _layoutQuietUntil = Environment.TickCount64 + 5000;
            return;
        }

        _layoutSent = hkl;
    }

    private void RememberRemote(TcpClient client, string name)
    {
        if (client.Client.RemoteEndPoint is IPEndPoint ep)
            _settings.PeerHost = ep.Address.ToString();
        if (!string.IsNullOrWhiteSpace(name))
            _settings.PeerName = name;
        _settings.PeerPort = TcpPort;
        Save();
    }

    private long _heard;
    private long _asleepTold;

    private void Hear() => Volatile.Write(ref _heard, Environment.TickCount64);

    private bool PeerAliveNow()
        => _link != null && Environment.TickCount64 - Volatile.Read(ref _heard) < 3500;

    private bool HasSavedPeer()
        => !string.IsNullOrWhiteSpace(_settings.PeerName) || !string.IsNullOrWhiteSpace(_settings.PeerHost);

    private void SyncEdgeHook()
    {
        bool watch = _running && (_link != null || HasSavedPeer());
        Ui(() =>
        {
            _capture.PeerOnRight = _settings.PeerOnRight;
            if (!watch)
            {
                _capture.Detach();
                return;
            }

            try { _capture.Attach(); }
            catch (Exception ex) { Status = ex.Message; }
        });
    }

    private void TellAsleep()
    {
        long now = Environment.TickCount64;
        if (now - _asleepTold < 4000)
            return;
        _asleepTold = now;
        DesktopIcons.Toast("Второй спит");
    }

    private void ShutdownNetwork(string? status)
    {
        _session++;
        try { _run?.Cancel(); } catch { /* уже остановлен */ }
        try { _listener?.Stop(); } catch { /* уже закрыт */ }
        try { _udp?.Dispose(); } catch { /* уже закрыт */ }
        try { _link?.Client.Dispose(); } catch { /* уже закрыт */ }
        _listener = null;
        _udp = null;
        _link = null;
        _running = false;
        _busy = false;
        _pending?.Done.TrySetResult(false);
        _pending = null;
        OutgoingCode = null;
        Ui(() =>
        {
            StopLayoutWatch();
            _files.Stop();
            _capture.Detach();
        });
        if (status != null)
            Status = status;
        Raise();
    }

    private void Load()
    {
        try
        {
            string path = SettingsPath();
            if (!File.Exists(path))
                return;
            _settings = JsonSerializer.Deserialize<EdgeSettings>(File.ReadAllText(path), Json) ?? new EdgeSettings();
        }
        catch
        {
            _settings = new EdgeSettings();
        }
    }

    private void Save()
    {
        lock (_gate)
        {
            try
            {
                string path = SettingsPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string json = JsonSerializer.Serialize(_settings, Json);
                string temp = path + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                /* диск занят: в памяти настройка уже есть */
            }
        }
    }

    private static string SettingsPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zwmenu", "edge.json");

    private void Raise()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            Changed?.Invoke();
        else
            dispatcher.BeginInvoke(() => Changed?.Invoke());
    }

    private static void Ui(Action action)
    {
        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            if (dispatcher.HasShutdownStarted)
                return;
            dispatcher.Invoke(action);
        }
        catch
        {
            /* окно уже закрывается */
        }
    }

    private static async Task<bool> ReadMagic(NetworkStream stream)
    {
        var got = new byte[Magic.Length];
        await ReadExact(stream, got, CancellationToken.None).ConfigureAwait(false);
        return got.AsSpan().SequenceEqual(Magic);
    }

    private static async Task<byte[]?> ReadFrame(NetworkStream stream, CancellationToken token)
    {
        var head = new byte[2];
        await ReadExact(stream, head, token).ConfigureAwait(false);
        int len = BinaryPrimitives.ReadUInt16LittleEndian(head);
        if (len < 1)
            return null;
        var body = new byte[len];
        await ReadExact(stream, body, token).ConfigureAwait(false);
        return body;
    }

    private static async Task ReadExact(NetworkStream stream, byte[] buffer, CancellationToken token)
    {
        int got = 0;
        while (got < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(got, buffer.Length - got), token).ConfigureAwait(false);
            if (n == 0)
                throw new EndOfStreamException();
            got += n;
        }
    }

    private readonly ConcurrentQueue<byte[]> _chunkPool = new();
    private readonly ConcurrentQueue<byte[]> _framePool = new();

    private byte[] RentBulk(int size)
    {
        if (size == EdgeWire.FilePacketSize && _chunkPool.TryDequeue(out var got))
            return got;
        return new byte[size];
    }

    private Task WriteBody(NetworkStream stream, byte[] body, CancellationToken token = default) =>
        WriteBody(stream, body, token, recycle: false);

    private async Task WriteBody(NetworkStream stream, byte[] body, CancellationToken token, bool recycle)
    {
        int frameLen = 2 + body.Length;
        byte[] packet;
        if (recycle && _framePool.TryDequeue(out var rented))
            packet = rented;
        else
            packet = new byte[frameLen];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, (ushort)body.Length);
        body.AsSpan().CopyTo(packet.AsSpan(2));
        await stream.WriteAsync(packet.AsMemory(0, frameLen), token).ConfigureAwait(false);
        if (!recycle)
            return;
        if (_chunkPool.Count < 32)
            _chunkPool.Enqueue(body);
        if (_framePool.Count < 32)
            _framePool.Enqueue(packet);
    }

    private static IEnumerable<IPAddress> Broadcasts()
    {
        yield return IPAddress.Broadcast;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            foreach (var uni in nic.GetIPProperties().UnicastAddresses)
            {
                if (uni.Address.AddressFamily != AddressFamily.InterNetwork || uni.IPv4Mask == null)
                    continue;
                byte[] ip = uni.Address.GetAddressBytes();
                byte[] mask = uni.IPv4Mask.GetAddressBytes();
                if (ip.Length != 4 || mask.Length != 4)
                    continue;
                var broadcast = new byte[4];
                for (int i = 0; i < 4; i++)
                    broadcast[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPAddress(broadcast);
            }
        }
    }

    private static List<string> ReadAddresses()
    {
        var list = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            foreach (var uni in nic.GetIPProperties().UnicastAddresses)
            {
                if (uni.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(uni.Address))
                    list.Add(uni.Address.ToString());
            }
        }

        return list;
    }

    private static byte[] RandomBytes(int n)
    {
        var data = new byte[n];
        System.Security.Cryptography.RandomNumberGenerator.Fill(data);
        return data;
    }

    private static string FormatCode(string code) =>
        code.Length == 6 ? code[..3] + " " + code[3..] : code;

    private sealed class Link
    {
        public Link(TcpClient client, NetworkStream stream, int gen)
        {
            Client = client;
            Stream = stream;
            Gen = gen;
            LastRx = Environment.TickCount64;
        }

        public TcpClient Client { get; }
        public NetworkStream Stream { get; }
        public int Gen { get; }
        public CancellationTokenSource Stop { get; } = new();
        public long LastRx;
    }

    private sealed class EdgeSettings
    {
        public bool Armed { get; set; }
        public string? Token { get; set; }
        public string? PeerName { get; set; }
        public string? PeerHost { get; set; }
        public int PeerPort { get; set; } = TcpPort;
        public bool PeerOnRight { get; set; } = true;

        [JsonIgnore]
        public byte[]? TokenBytes
        {
            get
            {
                if (string.IsNullOrEmpty(Token))
                    return null;
                try
                {
                    var data = Convert.FromBase64String(Token);
                    return data.Length == 16 ? data : null;
                }
                catch
                {
                    return null;
                }
            }
        }
    }
}

static class EdgeWire
{
    public const byte MoveCode = 1;
    public const byte ButtonCode = 2;
    public const byte WheelCode = 3;
    public const byte KeyCode = 4;
    public const byte EnterCode = 5;
    public const byte BackCode = 6;
    public const byte PingCode = 7;
    public const byte LayoutCode = 13;
    public const byte FileListCode = 14;
    public const int FileChunkSize = 48 * 1024;
    public const int FilePacketSize = 17 + FileChunkSize;
    public const byte FileChunkCode = 15;
    public const byte FileEndCode = 16;
    public const byte ClipOpenCode = 17;
    public const byte ClipChunkCode = 18;
    public const byte ClipEndCode = 19;
    public const byte HomeCode = 20;
    public const byte CarryCode = 21;
    public const byte CarryCommitCode = 22;
    public const byte WorkshopCode = 23;
    public const byte DenyCode = 12;
    public const byte AcceptCode = 11;

    public static byte[] Move(int dx, int dy)
    {
        var body = new byte[5];
        body[0] = MoveCode;
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(1), (short)Math.Clamp(dx, short.MinValue, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(3), (short)Math.Clamp(dy, short.MinValue, short.MaxValue));
        return body;
    }

    public static byte[] Button(byte id, bool down) => new byte[] { ButtonCode, id, down ? (byte)1 : (byte)0 };

    public static byte[] Wheel(short delta)
    {
        var body = new byte[3];
        body[0] = WheelCode;
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(1), delta);
        return body;
    }

    public static byte[] Key(int vk, int scan, bool down, bool extended)
    {
        var body = new byte[7];
        body[0] = KeyCode;
        body[1] = down ? (byte)1 : (byte)0;
        body[2] = extended ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(3), (ushort)vk);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(5), (ushort)scan);
        return body;
    }

    public static byte[] Enter(int yRatio)
    {
        var body = new byte[3];
        body[0] = EnterCode;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), (ushort)Math.Clamp(yRatio, 0, 10000));
        return body;
    }

    public static byte[] Back(int yRatio)
    {
        var body = new byte[3];
        body[0] = BackCode;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), (ushort)Math.Clamp(yRatio, 0, 10000));
        return body;
    }

    public static byte[] Layout(uint hkl)
    {
        var body = new byte[5];
        body[0] = LayoutCode;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1), hkl);
        return body;
    }

    public static byte[] Home() => new byte[] { HomeCode };

    public static byte[] Carry(string label)
    {
        string text = string.IsNullOrWhiteSpace(label) ? "Файл" : label.Trim();
        if (text.Length > 80)
            text = text[..80];
        byte[] raw = Encoding.UTF8.GetBytes(text);
        var body = new byte[2 + raw.Length];
        body[0] = CarryCode;
        body[1] = 1;
        raw.CopyTo(body.AsSpan(2));
        return body;
    }

    public static byte[] CarryEnd() => new byte[] { CarryCode, 0 };

    public static byte[] CarryCommit() => new byte[] { CarryCommitCode };
    public static byte[] Ping() => new byte[] { PingCode };
    public static byte[] Pong() => new byte[] { 8 };
    public static byte[] Deny() => new byte[] { DenyCode };

    public static byte[] Accept(byte[] token)
    {
        var body = new byte[17];
        body[0] = AcceptCode;
        token.AsSpan(0, 16).CopyTo(body.AsSpan(1));
        return body;
    }

    public static byte[] Hello(bool peerOnRight, string name, byte[]? token, string code)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(10);
        ms.WriteByte(peerOnRight ? (byte)1 : (byte)0);
        Put(ms, Encoding.UTF8.GetBytes(name));
        Put(ms, token ?? Array.Empty<byte>());
        Put(ms, Encoding.ASCII.GetBytes(code));
        return ms.ToArray();
    }

    public static bool TryHello(byte[] body, out bool peerOnRight, out string name, out byte[] token, out string code)
    {
        peerOnRight = true;
        name = "";
        token = Array.Empty<byte>();
        code = "";
        if (body.Length < 2 || body[0] != 10)
            return false;
        peerOnRight = body[1] != 0;
        int i = 2;
        if (!Take(body, ref i, out var nameBytes))
            return false;
        if (!Take(body, ref i, out token))
            return false;
        if (!Take(body, ref i, out var codeBytes))
            return false;
        name = Encoding.UTF8.GetString(nameBytes);
        code = Encoding.ASCII.GetString(codeBytes);
        return name.Length > 0;
    }

    private static void Put(MemoryStream ms, byte[] data)
    {
        int n = Math.Min(data.Length, 255);
        ms.WriteByte((byte)n);
        ms.Write(data, 0, n);
    }

    private static bool Take(byte[] body, ref int i, out byte[] data)
    {
        data = Array.Empty<byte>();
        if (i >= body.Length)
            return false;
        int n = body[i++];
        if (i + n > body.Length)
            return false;
        data = body[i..(i + n)];
        i += n;
        return true;
    }
}
