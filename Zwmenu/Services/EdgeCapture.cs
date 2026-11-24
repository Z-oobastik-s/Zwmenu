using System.Runtime.InteropServices;

namespace Zwmenu.Services;

/// <summary>
/// Пока связь есть: следит за краем своего экрана.
/// Когда курсор туда упирается, прячет его и шлёт движение на второй ноутбук.
/// Чужой ввод сюда не приходит, пока пользователь сам не перешёл через край.
/// ЗАПРЕТ: скорость мыши Windows не читать, не менять, не «компенсировать».
/// На столе только SetCursorPos по пикселям. Относительный ввод только в игре (курсор спрятан), без Unscale.
/// </summary>
sealed class EdgeCapture : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WhKeyboardLl = 13;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmMouseWheel = 0x020A;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const uint LlmhfInjected = 0x00000001;
    private static readonly UIntPtr EdgeExtra = MouseHook.EdgeExtra;
    private const int LlkfExtended = 0x01;
    private const int LlkfInjected = 0x10;

    private const uint MouseMove = 0x0001;
    private const uint MouseMoveNoCoalesce = 0x2000;
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint RightDown = 0x0008;
    private const uint RightUp = 0x0010;
    private const uint MiddleDown = 0x0020;
    private const uint MiddleUp = 0x0040;
    private const uint XDown = 0x0080;
    private const uint XUp = 0x0100;
    private const uint Wheel = 0x0800;
    private const int CursorShowing = 0x0001;
    private const uint KeyUp = 0x0002;
    private const uint KeyExtended = 0x0001;

    private readonly LowLevelMouseProc _mouseProc;
    private readonly LowLevelKeyboardProc _keyProc;
    private IntPtr _mouseHook;
    private IntPtr _keyHook;
    private bool _peerOnRight = true;
    private bool _allowCross = true;
    private bool _attached;
    private volatile bool _captured;
    private volatile bool _hosting;
    private bool _pinned;
    private bool _mustLeave = true;
    private volatile bool _inHook;
    private bool _track;
    private int _trackX;
    private int _trackY;
    private long _grabbedUntil;
    private bool _escHeld;
    private int _escHits;
    private long _escStarted;
    private int _cursorHides;
    private int _cursorHeld;
    private int _guardLeft;
    private int _guardTop;
    private int _guardRight;
    private int _guardBottom;
    private int _boxX;
    private int _boxY;
    private int _originX;
    private int _originY;
    private readonly System.Threading.Timer _moveFlush;
    private long _blockUntil;
    private int _warpX;
    private int _warpY;
    private long _warpUntil;
    private bool _holdUntilLeftUp;
    private bool _leftHeld;
    private bool _disposed;
    private int _timerBoost;

    public EdgeCapture()
    {
        _mouseProc = OnMouse;
        _keyProc = OnKey;
        // 4 мс: с timeBeginPeriod(1) реально чаще, без него Windows часто даёт ~15 мс и курсор плывёт.
        _moveFlush = new System.Threading.Timer(FlushMove, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    }

    public bool PeerOnRight
    {
        get => _peerOnRight;
        set => _peerOnRight = value;
    }

    /// <summary>Разрешён ли уход курсора на второй ноутбук по краю.</summary>
    public bool AllowCross
    {
        get => _allowCross;
        set => _allowCross = value;
    }

    public Action<byte[]>? Sink { get; set; }

    public Func<bool>? PeerAlive { get; set; }

    /// <summary>На этом ноутбуке тронули свой тачпад или мышь. Курсор остаётся здесь.</summary>
    public Action<int>? OnLocalGrab { get; set; }

    /// <summary>Край толкнули, а второго ноутбука нет. Курсор остаётся здесь.</summary>
    public Action? OnEdgeAsleep { get; set; }

    public bool Captured => _captured;

    public bool ReturnAllowed => _hosting && Environment.TickCount64 >= _blockUntil;

    /// <summary>На этом ноутбуке зажата левая кнопка, которую прислал второй.</summary>
    public bool LeftHeld => _leftHeld;

    public bool Hosting
    {
        get => _hosting;
        set
        {
            bool was = _hosting;
            _hosting = value;
            if (!value)
            {
                _track = false;
                _grabbedUntil = 0;
                if (!_captured)
                    EaseTimer();
                if (was)
                {
                    try { EdgeChime.Leave(); }
                    catch { /* звук необязателен */ }
                    try { OnStoppedHosting?.Invoke(); }
                    catch { /* подсказка переноса не обязательна */ }
                }
            }
            else
                BoostTimer();
        }
    }

    public void Attach()
    {
        if (_attached || _disposed)
            return;
        ClipCursor(IntPtr.Zero);
        _attached = true;
        _mustLeave = true;
        _pinned = false;
        _mouseHook = InstallMouseHook();
        _keyHook = InstallKeyHook();
    }

    public void Detach()
    {
        _holdUntilLeftUp = false;
        _leftHeld = false;
        ReleaseCapture(placeX: null, placeY: null);
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        if (_keyHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyHook);
            _keyHook = IntPtr.Zero;
        }

        _attached = false;
        _hosting = false;
        _pinned = false;
        EaseTimer();
    }

    /// <summary>Второй ноутбук вернул курсор. Ставим его у своего края и снова даём мышь системе.</summary>
    public void ReturnHere(int yRatio)
    {
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        int y = bounds.Top;
        if (bounds.Height > 1)
            y = bounds.Top + (int)((bounds.Height - 1L) * Math.Clamp(yRatio, 0, 10000) / 10000);
        int x = _peerOnRight ? bounds.Right - 12 : bounds.Left + 12;
        _mustLeave = true;
        _pinned = false;
        _blockUntil = Environment.TickCount64 + 250;
        _escHits = 0;
        _warpX = x;
        _warpY = y;
        // Короткое окно: в очереди ещё лежит центр, куда курсор прижимали.
        // Дольше держать нельзя: живое движение через середину экрана тоже попадёт в эту зону.
        _warpUntil = Environment.TickCount64 + 90;
        // Постановка курсора сама зовёт крючок. Пока _inHook, это событие просто применяется,
        // и крючок не успевает снова зажать край.
        _inHook = true;
        try { ReleaseCapture(x, y); }
        finally { _inHook = false; }
        EdgeChime.Arrive();
        // Кнопку не отпускаем сами. Синтетический LeftUp съедает настоящее отпускание,
        // и перенос со второго ноутбука остаётся висеть с подписью.
        if (_holdUntilLeftUp || (GetAsyncKeyState(0x01) & 0x8000) != 0)
            _holdUntilLeftUp = true;
    }

    /// <summary>Левая кнопка сейчас физически зажата.</summary>
    public bool LeftButtonDown => (GetAsyncKeyState(0x01) & 0x8000) != 0;

    /// <summary>Обратный перенос: подпись уже на этом ноутбуке, ждём настоящий отпуск кнопки.</summary>
    public void HoldForCarryRelease() => _holdUntilLeftUp = true;

    /// <summary>Курсор больше не на этом ноутбуке: отпустить зажатый край.</summary>
    public void ForceUnhost()
    {
        Hosting = false;
        _blockUntil = Environment.TickCount64 + 700;
    }

    public void ApplyMove(int dx, int dy)
    {
        if (!_hosting || (dx == 0 && dy == 0))
            return;

        // ЗАПРЕТ СКОРОСТИ МЫШИ (правило .cursor/rules/no-mouse-speed.mdc):
        // не SPI_GET/SETMOUSESPEED, не Unscale, не множители dx/dy.
        // Стол (курсор виден): только PlaceExact/SetCursorPos, пиксель в пиксель.
        // Относительный SendInput на столе запрещён: Windows снова разгоняет курсор.
        // Игра (курсор спрятан): сырой SendRelative без масштаба под скорость Windows.
        long now = Environment.TickCount64;
        bool hidden = CursorHidden();
        if (hidden)
            _grabbedUntil = now + 200;
        if (hidden || now < _grabbedUntil)
        {
            _track = false;
            SendRelative(dx, dy);
            return;
        }

        if (!_track && GetCursorPos(out var current))
        {
            _trackX = current.X;
            _trackY = current.Y;
            _track = true;
        }

        if (!_track)
        {
            SendRelative(dx, dy);
            return;
        }

        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        int x = Math.Clamp(_trackX + dx, bounds.Left, Math.Max(bounds.Left, bounds.Right - 1));
        int y = Math.Clamp(_trackY + dy, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - 1));
        _trackX = x;
        _trackY = y;
        PlaceExact(x, y);
    }

    public void ApplyButton(byte id, bool down)
    {
        if (id == 1)
            _leftHeld = down;
        if (!_hosting)
            return;

        // На столе СКМ открывает наше колесо. В игре она нужна самой игре.
        var extra = id == 3 && (CursorHidden() || Environment.TickCount64 < _grabbedUntil)
            ? MouseHook.InjectExtra
            : EdgeExtra;
        switch (id)
        {
            case 1:
                SendMouse(0, 0, 0, down ? LeftDown : LeftUp, extra);
                break;
            case 2:
                SendMouse(0, 0, 0, down ? RightDown : RightUp, extra);
                break;
            case 3:
                SendMouse(0, 0, 0, down ? MiddleDown : MiddleUp, extra);
                break;
            case 4:
                SendMouse(0, 0, 1, down ? XDown : XUp, extra);
                break;
            case 5:
                SendMouse(0, 0, 2, down ? XDown : XUp, extra);
                break;
        }
    }

    public void ApplyWheel(int delta)
    {
        if (_hosting)
            SendMouse(0, 0, (uint)delta, Wheel, EdgeExtra);
    }

    public void ApplyKey(int vk, int scan, bool down, bool extended)
    {
        var input = new INPUT
        {
            Type = 1,
            U = new InputUnion
            {
                Ki = new KEYBDINPUT
                {
                    WVk = (ushort)vk,
                    WScan = (ushort)scan,
                    DwFlags = (down ? 0u : KeyUp) | (extended ? KeyExtended : 0u),
                    DwExtraInfo = MouseHook.InjectExtra
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Курсор второго ноутбука упёрся в край возврата.</summary>
    public bool HitReturnEdge(int dx)
    {
        // Игра держит курсор у края и прячет его. Взгляд в эту сторону не должен выкидывать из игры.
        if (CursorHidden() || Environment.TickCount64 < _grabbedUntil)
            return false;
        if (!GetCursorPos(out var pt))
            return false;
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        if (_peerOnRight)
            return pt.X >= bounds.Right - 2 && dx > 0;
        return pt.X <= bounds.Left + 1 && dx < 0;
    }

    public int CurrentYRatio()
    {
        if (!GetCursorPos(out var pt))
            return 0;
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        if (bounds.Height <= 1)
            return 0;
        long ratio = (pt.Y - bounds.Top) * 10000L / bounds.Height;
        return (int)Math.Clamp(ratio, 0, 10000);
    }

    public void PlaceOnEntry(int yRatio)
    {
        // Этот ноутбук принимает чужую мышь. Свой зажим в центр надо снять,
        // иначе чужой курсор рисуется только посередине первого экрана.
        if (_captured)
            ReleaseCapture(null, null);
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        int y = bounds.Top;
        if (bounds.Height > 1)
            y = bounds.Top + (int)((bounds.Height - 1L) * Math.Clamp(yRatio, 0, 10000) / 10000);
        int x = _peerOnRight ? bounds.Right - 9 : bounds.Left + 8;
        _hosting = true;
        BoostTimer();
        _blockUntil = Environment.TickCount64 + 350;
        EdgeChime.Arrive();
        if (!CursorHidden())
        {
            _trackX = x;
            _trackY = y;
            _track = true;
            PlaceExact(x, y);
            return;
        }

        _track = false;
        _grabbedUntil = Environment.TickCount64 + 200;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Detach();
        try { _moveFlush.Dispose(); } catch { /* уже закрыт */ }
    }

    private void BeginCapture(int x, int y)
    {
        if (_captured || _hosting)
            return;
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y));
        var area = screen.Bounds;
        _boxX = area.Left + area.Width / 2;
        _boxY = area.Top + area.Height / 2;
        _guardLeft = area.Left;
        _guardTop = area.Top;
        _guardRight = area.Right;
        _guardBottom = area.Bottom;
        var box = new RECT
        {
            Left = area.Left,
            Top = area.Top,
            Right = Math.Max(area.Left + 1, area.Right),
            Bottom = Math.Max(area.Top + 1, area.Bottom)
        };
        _captured = true;
        _originX = x;
        _originY = y;
        _escHits = 0;
        _escHeld = false;
        bool left = (GetAsyncKeyState(0x01) & 0x8000) != 0;
        // Зажатую кнопку на тот экран не отдаём: иначе там рисуется рамка выделения на весь стол.
        if (left)
        {
            _holdUntilLeftUp = true;
            NudgeCancelDrag();
        }

        HideCursor();
        Zwmenu.Overlay.EdgeCarryCue.Park();
        Zwmenu.Overlay.CursorKeep.Suspend();
        _cursorHeld = 1;
        ClipCursor(ref box);
        BoostTimer();
        _moveFlush.Change(0, 4);
        Post(EdgeWire.Enter(_yRatio(x, y)));
        EdgeChime.Leave();
        if (left)
        {
            try { OnEdgeCross?.Invoke(); }
            catch { /* крючок мыши не должен падать */ }
        }
    }

    /// <summary>Курсор ушёл за край с зажатой кнопкой. Вызов обязан быть мгновенным.</summary>
    public Action? OnEdgeCross { get; set; }

    /// <summary>Левая кнопка нажата на этом ноутбуке. Вызов обязан быть мгновенным.</summary>
    public Action? OnArmSelection { get; set; }

    /// <summary>Кнопку отпустили после переноса. Можно убрать плюс у курсора.</summary>
    public Action? OnCarryRelease { get; set; }

    /// <summary>Курсор ушёл с этого ноутбука. Местную подсказку переноса надо спрятать.</summary>
    public Action? OnStoppedHosting { get; set; }

    /// <summary>Три Esc за две секунды. Вызов обязан быть мгновенным.</summary>
    public Action? OnForceReturn { get; set; }

    private void ReleaseCapture(int? placeX, int? placeY)
    {
        if (!_captured && placeX == null)
        {
            ClipCursor(IntPtr.Zero);
            ShowCursorBack();
            Zwmenu.Overlay.EdgeCarryCue.Unpark();
            return;
        }

        _captured = false;
        try
        {
            _moveFlush.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        }
        catch { /* таймер уже закрыт */ }
        if (!_hosting)
            EaseTimer();
        ClipCursor(IntPtr.Zero);
        ShowCursorBack();
        Zwmenu.Overlay.EdgeCarryCue.Unpark();
        if (_cursorHeld != 0)
        {
            _cursorHeld = 0;
            Zwmenu.Overlay.CursorKeep.Resume();
        }
        if (placeX is int x && placeY is int y)
            SetCursorPos(x, y);
    }

    private void HideCursor()
    {
        _cursorHides = 0;
        int count;
        do
        {
            count = ShowCursor(false);
            _cursorHides++;
        }
        while (count >= 0 && _cursorHides < 16);
    }

    private void ShowCursorBack()
    {
        int left = _cursorHides;
        _cursorHides = 0;
        for (int i = 0; i < left; i++)
            ShowCursor(true);
    }

    private IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || _inHook)
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<Msll>(lParam);
        if ((data.Flags & LlmhfInjected) != 0
            || data.ExtraInfo == MouseHook.InjectExtra
            || data.ExtraInfo == EdgeExtra)
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        int msg = wParam.ToInt32();
        if (_hosting)
        {
            // Своя постановка курсора приходит той же точкой. Её не отдаём системе,
            // иначе чужой шаг дёргается. Настоящий тачпад или мышь этого ноутбука
            // стоят в другом месте: курсор забираем себе, иначе им нельзя шевелить.
            if (msg == WmMouseMove && !IsLocalPointer(data.Point.X, data.Point.Y, _trackX, _trackY))
                return (IntPtr)1;
            GrabLocal();
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        // Палец отпустили уже на своём экране. Отпуск отдаём системе, иначе кнопка залипает,
        // и одновременно с него стартует перенос, который ждал этот клик.
        if (_holdUntilLeftUp && !_captured && msg == WmLButtonUp)
        {
            _holdUntilLeftUp = false;
            try { OnCarryRelease?.Invoke(); }
            catch { /* сеть не роняет крючок */ }
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        // Хвост зажима: точка ещё в центре экрана. Пропустить её — курсор прыгает в центр и замирает.
        // SetCursorPos отсюда не зовём: из крючка он встаёт в рекурсию и мышь снова тормозит.
        if (!_captured && msg == WmMouseMove && IsStalePin(data.Point.X, data.Point.Y))
            return (IntPtr)1;

        if (_captured)
        {
            if (PeerAlive?.Invoke() == false)
            {
                try { ReturnHere(CurrentYRatio()); }
                catch { /* курсор уже дома */ }
                try { OnEdgeAsleep?.Invoke(); }
                catch { /* строка необязательна */ }
                return (IntPtr)1;
            }

            // Событие не держим: положение курсора уже сменилось, его заберёт таймер.
            // Глотать его нельзя, на этой Windows курсор тогда замирает.
            if (msg == WmMouseMove)
                return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

            _inHook = true;
            try
            {
                if (msg == WmMouseWheel)
                {
                    short delta = unchecked((short)((data.MouseData >> 16) & 0xFFFF));
                    Post(EdgeWire.Wheel(delta));
                }
                else if (ButtonOf(msg, data.MouseData, out byte id, out bool down))
                {
                    if (id == 1 && _holdUntilLeftUp)
                    {
                        if (!down)
                        {
                            _holdUntilLeftUp = false;
                            try { OnCarryRelease?.Invoke(); }
                            catch { /* сеть не роняет крючок */ }
                        }
                    }
                    else
                        Post(EdgeWire.Button(id, down));
                }
            }
            finally
            {
                _inHook = false;
            }

            return (IntPtr)1;
        }

        if (msg == WmLButtonDown)
        {
            try { OnArmSelection?.Invoke(); }
            catch { /* крючок мыши не должен падать */ }
        }
        if (msg != WmMouseMove)
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        if (Environment.TickCount64 < _blockUntil)
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        bool onEdge = _peerOnRight
            ? data.Point.X >= bounds.Right - 1
            : data.Point.X <= bounds.Left;
        if (_mustLeave)
        {
            if (!onEdge)
                _mustLeave = false;
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        if (!onEdge)
        {
            _pinned = false;
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        if (!_pinned)
        {
            _pinned = true;
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        if (PeerAlive?.Invoke() == false)
        {
            try { OnEdgeAsleep?.Invoke(); }
            catch { /* строка необязательна */ }
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        // Пока игра прячет курсор, край не переход. В меню и в поле ввода курсор виден, уход на второй ноутбук снова можно.
        if (CursorHidden() || !_allowCross)
        {
            _pinned = false;
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        _inHook = true;
        try
        {
            BeginCapture(data.Point.X, data.Point.Y);
        }
        finally
        {
            _inHook = false;
        }

        return (IntPtr)1;
    }

    private IntPtr OnKey(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || (!_captured && !_hosting))
            return CallNextHookEx(_keyHook, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<KbdLl>(lParam);
        if ((data.Flags & LlkfInjected) != 0)
            return CallNextHookEx(_keyHook, nCode, wParam, lParam);

        int msg = wParam.ToInt32();
        bool down = msg is WmKeyDown or WmSysKeyDown;
        bool up = msg is WmKeyUp or WmSysKeyUp;
        if (!down && !up)
            return CallNextHookEx(_keyHook, nCode, wParam, lParam);

        if (TakeEsc(data.VkCode, down))
        {
            try { ForceReturn(); }
            catch { /* возврат мыши не роняет крючок */ }
            if (!_captured)
                return CallNextHookEx(_keyHook, nCode, wParam, lParam);
            return (IntPtr)1;
        }

        if (!_captured)
            return CallNextHookEx(_keyHook, nCode, wParam, lParam);

        Post(EdgeWire.Key(data.VkCode, data.ScanCode, down, (data.Flags & LlkfExtended) != 0));
        return (IntPtr)1;
    }

    private bool TakeEsc(int vk, bool down)
    {
        if (vk != 0x1B)
        {
            if (down)
                _escHits = 0;
            return false;
        }

        if (!down)
        {
            _escHeld = false;
            return false;
        }

        if (_escHeld)
            return false;
        _escHeld = true;
        long now = Environment.TickCount64;
        if (_escHits == 0 || now - _escStarted > 2000)
        {
            _escStarted = now;
            _escHits = 1;
            return false;
        }

        _escHits++;
        if (_escHits < 3)
            return false;
        _escHits = 0;
        return true;
    }

    private void ForceReturn()
    {
        if (_captured)
            ReturnHere(CurrentYRatio());
        else
            ForceUnhost();
        try { OnForceReturn?.Invoke(); }
        catch { /* обрыв связи не оставляет мышь зажатой */ }
    }

    private static bool ButtonOf(int msg, uint mouseData, out byte id, out bool down)
    {
        down = false;
        id = 0;
        switch (msg)
        {
            case WmLButtonDown: id = 1; down = true; return true;
            case WmLButtonUp: id = 1; return true;
            case WmRButtonDown: id = 2; down = true; return true;
            case WmRButtonUp: id = 2; return true;
            case WmMButtonDown: id = 3; down = true; return true;
            case WmMButtonUp: id = 3; return true;
            case WmXButtonDown:
            case WmXButtonUp:
                id = (byte)(((mouseData >> 16) & 0xFFFF) == 2 ? 5 : 4);
                down = msg == WmXButtonDown;
                return true;
            default:
                return false;
        }
    }

    private void FlushMove(object? _)
    {
        if (!_captured || _hosting || !GetCursorPos(out var pt))
            return;
        var step = OneStep(_originX, _originY, pt.X, pt.Y);
        _originX = pt.X;
        _originY = pt.Y;
        if ((step.Dx != 0 || step.Dy != 0) && _captured && !_hosting)
            Post(EdgeWire.Move(step.Dx, step.Dy));
        if (!_captured || _hosting)
            return;
        if (pt.X == _boxX && pt.Y == _boxY)
            return;
        PlaceExact(_boxX, _boxY);
        _originX = _boxX;
        _originY = _boxY;
    }

    /// <summary>
    /// Шаг — это только отход от точки, где курсор реально стоит.
    /// После возврата в центр следующая точка отсчёта там, куда он встал,
    /// а не там, куда целились: иначе один и тот же сдвиг уходит дважды
    /// и на втором экране мышь бежит быстрее.
    /// </summary>
    internal readonly struct Step
    {
        public int Dx { get; init; }
        public int Dy { get; init; }
    }

    internal static Step OneStep(int originX, int originY, int x, int y)
        => new() { Dx = x - originX, Dy = y - originY };

    /// <summary>Чужой курсор стоит в track. Сдвиг дальше этого — уже местный палец или мышь.</summary>
    internal static bool IsLocalPointer(int x, int y, int trackX, int trackY)
        => Math.Abs(x - trackX) > 14 || Math.Abs(y - trackY) > 14;

    void BoostTimer()
    {
        if (Interlocked.Exchange(ref _timerBoost, 1) != 0)
            return;
        try { timeBeginPeriod(1); }
        catch { /* multimedia timer недоступен */ }
    }

    void EaseTimer()
    {
        if (Interlocked.Exchange(ref _timerBoost, 0) == 0)
            return;
        try { timeEndPeriod(1); }
        catch { /* multimedia timer недоступен */ }
    }

    /// <summary>Курсор почти у края экрана. Один раз уводим в центр, чтобы шаг не оборвался.</summary>
    internal static bool ShouldRecenter(int x, int y, int left, int top, int right, int bottom)
        => x <= left + 48 || y <= top + 48 || x >= right - 49 || y >= bottom - 49;

    private void GrabLocal()
    {
        if (!_hosting)
            return;
        _mustLeave = true;
        _pinned = false;
        _blockUntil = Environment.TickCount64 + 500;
        int ratio = CurrentYRatio();
        Hosting = false;
        try { OnLocalGrab?.Invoke(ratio); }
        catch { /* сеть не роняет крючок */ }
    }

    /// <summary>Точная точка. Чужой ввод сюда не примешивается и скорость Windows не трогается.</summary>
    private void PlaceExact(int x, int y)
    {
        _inHook = true;
        try { SetCursorPos(x, y); }
        finally { _inHook = false; }
    }

    public static void SelfCheck()
    {
        var first = OneStep(683, 384, 710, 390);
        if (first.Dx != 27 || first.Dy != 6)
            throw new InvalidOperationException("шаг от центра");
        int origin = 683;
        int sent = OneStep(origin, 0, 710, 0).Dx;
        origin = 683;
        sent += OneStep(origin, 0, 693, 0).Dx;
        if (sent != 37)
            throw new InvalidOperationException("после возврата в центр шаг удвоился");
        origin = 710;
        int missed = OneStep(683, 0, 710, 0).Dx + OneStep(origin, 0, 720, 0).Dx;
        if (missed != 37)
            throw new InvalidOperationException("неудачный возврат отправлен повторно");
        if (IsLocalPointer(100, 100, 100, 100) || IsLocalPointer(110, 100, 100, 100))
            throw new InvalidOperationException("своя точка принята за тачпад");
        if (!IsLocalPointer(120, 100, 100, 100))
            throw new InvalidOperationException("тачпад не отличили от чужого курсора");
        if (ShouldRecenter(683, 384, 0, 0, 1366, 768))
            throw new InvalidOperationException("середина экрана принята за край");
        if (!ShouldRecenter(10, 384, 0, 0, 1366, 768) || !ShouldRecenter(1340, 384, 0, 0, 1366, 768))
            throw new InvalidOperationException("край не уводит курсор в центр");
    }

    private void Post(byte[] frame)
    {
        try { Sink?.Invoke(frame); }
        catch { /* оборванная связь не роняет крючок */ }
    }

    /// <summary>
    /// Закончить перетаскивание оболочки, не бросая файл в папку под курсором.
    /// SendInput с потока хука встаёт намертво, поэтому кнопки жмём в стороне.
    /// </summary>
    public void ReleaseShellDrag(bool clearSelection, bool releaseButton)
    {
        if (releaseButton)
        {
            _leftHeld = false;
            Task.Run(ReleaseHeldButton);
        }
        else
            Task.Run(CancelShellDrag);

        if (!clearSelection)
            return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
            return;
        dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            try { ShellSelectionService.ClearSelection(); }
            catch { /* выделение снимем в следующий раз */ }
        });
    }

    private void ReleaseHeldButton()
    {
        try { SendMouse(0, 0, 0, LeftUp, EdgeExtra); }
        catch { /* ввод уже не нужен */ }
    }

    /// <summary>
    /// Пока курсор был на другом ноутбуке, его держали в центре и каждый сдвиг
    /// возвращали туда через SetCursorPos. Эти точки догоняют крючок уже после
    /// возврата. Они рядом с центром, а живой курсор в этот момент у края.
    /// </summary>
    private bool IsStalePin(int x, int y)
    {
        if (Environment.TickCount64 >= _warpUntil)
            return false;
        long dx = x - _boxX;
        long dy = y - _boxY;
        return dx * dx + dy * dy <= 160L * 160L;
    }

    /// <summary>Снять зажим курсора с любого потока, если связь умерла вне интерфейса.</summary>
    public void EmergencyRelease()
    {
        _captured = false;
        _hosting = false;
        _pinned = false;
        _holdUntilLeftUp = false;
        _leftHeld = false;
        try
        {
            _moveFlush.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        }
        catch { /* таймер уже закрыт */ }
        EaseTimer();
        ClipCursor(IntPtr.Zero);
        ShowCursorBack();
        Zwmenu.Overlay.EdgeCarryCue.Unpark();
        if (_cursorHeld != 0)
        {
            _cursorHeld = 0;
            Zwmenu.Overlay.CursorKeep.Resume();
        }
    }

    private static void CancelShellDrag()
    {
        CancelMode(GetCapture());
        CancelMode(GetForegroundWindow());
    }

    private static void NudgeCancelDrag() => CancelShellDrag();

    /// <summary>Снять рамку выделения проводника. Клавишу Esc чужим окнам не шлём: видео выходит из полного экрана.</summary>
    private static void CancelMode(IntPtr hwnd)
    {
        if (!IsShell(hwnd))
            return;
        PostMessage(hwnd, 0x001F, IntPtr.Zero, IntPtr.Zero);
    }

    private static bool IsShell(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || BelongsHere(hwnd))
            return false;
        var name = new System.Text.StringBuilder(64);
        if (GetClassName(hwnd, name, name.Capacity) <= 0)
            return false;
        return name.ToString() is "CabinetWClass" or "ExploreWClass" or "Progman" or "WorkerW" or "Shell_TrayWnd";
    }

    private static bool BelongsHere(IntPtr hwnd)
    {
        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        return pid != 0 && pid == (uint)Environment.ProcessId;
    }

    private static int _yRatio(int _, int y)
    {
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        if (bounds.Height <= 1)
            return 0;
        long ratio = (y - bounds.Top) * 10000L / bounds.Height;
        return (int)Math.Clamp(ratio, 0, 10000);
    }

    /// <summary>
    /// Только для игры (курсор спрятан): сырой относительный сдвиг.
    /// ЗАПРЕТ: не читать и не менять скорость мыши Windows, не Unscale, не множители.
    /// На столе этот метод для чужого курсора не использовать.
    /// </summary>
    private void SendRelative(int dx, int dy)
    {
        if (dx == 0 && dy == 0)
            return;
        SendMouse(dx, dy, 0, MouseMove | MouseMoveNoCoalesce, EdgeExtra);
    }

    private static bool CursorHidden()
    {
        var info = new CursorInfo { CbSize = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info))
            return false;
        return (info.Flags & CursorShowing) == 0;
    }

    private static void SendMouse(int dx, int dy, uint data, uint flags, UIntPtr extra)
    {
        var input = new INPUT
        {
            Type = 0,
            U = new InputUnion
            {
                Mi = new MOUSEINPUT
                {
                    Dx = dx,
                    Dy = dy,
                    MouseData = data,
                    DwFlags = flags,
                    DwExtraInfo = extra
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private IntPtr InstallMouseHook() => Install(_mouseProc, WhMouseLl);

    private IntPtr InstallKeyHook() => Install(_keyProc, WhKeyboardLl);

    private static IntPtr Install(Delegate proc, int id)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        using var module = process.MainModule
            ?? throw new InvalidOperationException("Не удалось определить модуль для хука перехода между экранами.");
        IntPtr hook = id == WhMouseLl
            ? SetMouseHook(id, (LowLevelMouseProc)proc, GetModuleHandle(module.ModuleName), 0)
            : SetKeyHook(id, (LowLevelKeyboardProc)proc, GetModuleHandle(module.ModuleName), 0);
        if (hook == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Не удалось поставить хук перехода между экранами.");
        return hook;
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookEx", SetLastError = true)]
    private static extern IntPtr SetMouseHook(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookEx", SetLastError = true)]
    private static extern IntPtr SetKeyHook(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CursorInfo info);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool ClipCursor(ref RECT rect);

    [DllImport("user32.dll")]
    private static extern bool ClipCursor(IntPtr rect);

    [DllImport("user32.dll")]
    private static extern int ShowCursor(bool show);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] inputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int CbSize;
        public int Flags;
        public IntPtr Cursor;
        public NativePoint ScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msll
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLl
    {
        public int VkCode;
        public int ScanCode;
        public int Flags;
        public int Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

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
