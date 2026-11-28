using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Zwmenu.Models;
using Zwmenu.Overlay;

namespace Zwmenu.Services;

/// <summary>Owns hooks + radial menu lifetime. One menu at a time.</summary>
public sealed class LauncherService: IDisposable
{
    private readonly MouseHook _mouse;
    private readonly KeyboardHook _keyboard;
    private readonly Dispatcher _dispatcher;
    private FileConvertHotkeyService? _fileConvert;
    private RadialMenuWindow? _menu;
    private IReadOnlyList<MenuAction> _actions = Array.Empty<MenuAction>();
    private bool _menuDirty = true;
    private bool _disposed;
    private bool _opening;
    private int _hotkeyHeld;
    private int _dictationKeyHeld;
    private bool _warming;
    private bool _warmReady;
    private (int X, int Y)? _pendingOpen;
    private readonly DispatcherTimer _middleHoldTimer;
    private MouseHook.ScreenPoint _middleDownPt;
    private bool _middleHoldArmed;
    private bool _middleOpenedMenu;
    private ScreenshotOverlayWindow? _shot;
    private bool _shotOpening;
    private bool _shotCancel;
    private readonly ScreenClipRecorder _clip = new();
    private bool _clipAsk;
    private Window? _clipAskWindow;
    private DictationService? _dictation;

    public bool MouseHookOk { get; private set; }
    public bool KeyboardHookOk { get; private set; }
    public string StatusError { get; private set; } = "";
    public string ConfigWarning { get; private set; } = "";
    public int ItemCount => _actions.Count;
    public bool IsRunning => MouseHookOk && KeyboardHookOk;

    public event EventHandler? StatusChanged;
    public event EventHandler? ConfigChanged;
    public event Action<int>? ClipStateChanged;
    public event EventHandler<string>? ActionFailed;
    public event EventHandler<ActionProblem>? ActionProblem;

    public LauncherService()
    {
        ApplyConfig(MenuStore.Load(prepareIcons: false));
        _dispatcher = Dispatcher.CurrentDispatcher;
        MenuStore.Changed += () => _dispatcher.BeginInvoke(ReloadFromDisk);
        _mouse = new MouseHook();
        _keyboard = new KeyboardHook
        {
            EscapePressed = () => _dispatcher.BeginInvoke(() =>
            {
                if (_shot != null)
                {
                    _shot.Cancel();
                    return;
                }

                _fileConvert?.ClosePopup();
                CloseMenu();
            }),
            SwallowKeyDown = OnSwallowKey
        };
        _keyboard.SwallowedKeyUp += vk =>
        {
            VoiceShiftLive.ReleaseKey(vk);
            if (_hotkeyHeld == vk)
                _hotkeyHeld = 0;
            if (_dictationKeyHeld == vk)
            {
                _dictationKeyHeld = 0;
                _ = Task.Run(() =>
                {
                    try { _dictation?.TriggerUp(); }
                    catch { /* ignore */ }
                });
            }
        };
        _middleHoldTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _middleHoldTimer.Tick += OnMiddleHoldTick;

        _dictation = new DictationService(_dispatcher);
        // GameFocus и полка: после BootPace, не в конструкторе (холодный вход в Windows).
        ApplyMiddleClickSettings();
        _mouse.MiddleDown += OnMiddleDown;
        _mouse.MiddleUp += OnMiddleUp;
        _mouse.SideDown += OnSideDown;
        _mouse.SideUp += OnSideUp;
        _mouse.AnyButtonDown += OnAnyButtonDown;

        ActionDispatcher.ActionFailed += (_, msg) =>
            _dispatcher.BeginInvoke(() => ActionFailed?.Invoke(this, msg));
        ActionDispatcher.Problem += (_, problem) =>
            _dispatcher.BeginInvoke(() => ActionProblem?.Invoke(this, problem));
    }

    /// <summary>Обновить путь пункта (после переноса программы) и сохранить menu.json.</summary>
    public bool UpdateLaunchPath(MenuAction action, string newPath)
    {
        newPath = (newPath ?? "").Trim().Trim('"');
        if (newPath.Length == 0)
            return false;

        var live = FindAction(action);
        if (live == null)
            return false;

        live.Value = newPath;
        if (string.IsNullOrWhiteSpace(live.Label) || live.Label == "Новый пункт")
            live.Label = Path.GetFileNameWithoutExtension(newPath);
        live.IconPath = "";
        AppIconService.EnsureIcon(live);

        try
        {
            MenuStore.Save(_actions);
            ReloadFromDisk();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private MenuAction? FindAction(MenuAction needle)
    {
        foreach (var item in _actions)
        {
            if (ReferenceEquals(item, needle))
                return item;
            foreach (var child in item.Children)
            {
                if (ReferenceEquals(child, needle))
                    return child;
                // Совпадение по старому пути + подписи: если объект уже другой после reload.
                if (child.Kind == needle.Kind
                    && string.Equals(child.Label, needle.Label, StringComparison.Ordinal)
                    && string.Equals(child.Value, needle.Value, StringComparison.OrdinalIgnoreCase))
                    return child;
            }

            if (item.Kind == needle.Kind
                && string.Equals(item.Label, needle.Label, StringComparison.Ordinal)
                && string.Equals(item.Value, needle.Value, StringComparison.OrdinalIgnoreCase))
                return item;
        }
        return null;
    }

    public void Start()
    {
        StatusError = "";
        MouseHookOk = TryStart(_mouse.Start, "мышь");
        KeyboardHookOk = TryStart(_keyboard.Start, "клавиатура");
        _clip.StateChanged += phase =>
        {
            if (_dispatcher.HasShutdownStarted)
                return;
            _dispatcher.BeginInvoke(() => ClipStateChanged?.Invoke(phase));
        };
        _clip.Finished += (text, bounds, _) =>
        {
            if (_dispatcher.HasShutdownStarted)
                return;
            _dispatcher.BeginInvoke(() =>
            {
                if (ScreenshotStore.Current.ShowNotifications)
                    ScreenshotToast.Show(text, bounds, 2.4);
            });
        };
        _ = Task.Run(ShotHistory.Prune);

        if (MouseHookOk != KeyboardHookOk)
        {
            _mouse.Stop();
            _keyboard.Stop();
            MouseHookOk = KeyboardHookOk = false;
            if (string.IsNullOrEmpty(StatusError))
                StatusError = "Хуки встали криво: оба откатили.";
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);

        // Конвертер хоткеев лёгкий. Тяжёлый прогрев колеса: BeginWarmup через BootPace.
        if (MouseHookOk && KeyboardHookOk)
        {
            _fileConvert = new FileConvertHotkeyService(
                _keyboard,
                _mouse,
                _dispatcher,
                () => _menu is { IsVisible: true } || _shot != null || _shotOpening);
        }
    }

    /// <summary>Отложенный прогрев иконок и окна колеса. Вызывать со ступени BootPace.</summary>
    public void BeginWarmup()
    {
        if (_disposed || _warming || _warmReady)
            return;
        if (!(MouseHookOk && KeyboardHookOk))
        {
            _warmReady = true;
            return;
        }

        _ = WarmupAsync();
    }

    public void WarmGameFocus()
    {
        try { GameFocus.Warm(); } catch { /* фокус игры необязателен на старте */ }
    }

    /// <summary>
    /// Иконки и Shell — в фоне. Окно колеса и первый кадр layered-HWND — на UI до клика.
    /// Если СКМ пришёл раньше, показ откладывается до конца прогрева, а не строится в клике.
    /// </summary>
    private async Task WarmupAsync()
    {
        var actions = _actions;
        try
        {
            await BootPace.RunLowAsync(() =>
            {
                MenuStore.PrepareIcons(actions);
                try { ProcessKillService.Snapshot(42); } catch { /* список процессов необязателен */ }
                foreach (var item in actions)
                {
                    if (!item.IsFoldersGroup)
                        continue;
                    foreach (var child in item.Children)
                    {
                        if (!string.IsNullOrWhiteSpace(child.Value) && Directory.Exists(child.Value))
                            FolderPeekService.List(child.Value);
                    }
                }
            }).ConfigureAwait(true);
        }
        catch
        {
            // колесо откроется и без кэша иконок
        }

        if (_disposed)
            return;

        _warming = true;
        try
        {
            AppearanceStore.Reload();
            RingImageCache.WarmActiveSkin();
            RingImageCache.WarmActions(_actions);
            EnsureMenuBuilt();
        }
        catch (Exception ex)
        {
            ActionFailed?.Invoke(this, "Не удалось подготовить колесо: " + ex.Message);
        }
        finally
        {
            _warming = false;
            _warmReady = true;
        }

        if (_pendingOpen is { } pending)
        {
            _pendingOpen = null;
            if (GameFocus.IsPlaying(fresh: true))
                return;
            try { OpenMenu(pending.X, pending.Y); }
            catch (Exception ex)
            {
                ActionFailed?.Invoke(this, "Колесо не открылось: " + ex.Message);
            }
        }

        // Кодировщик нужен только к записи. Модель фона поднимается в момент «Фон».
        double encDelay = BootPace.SoftLogin ? 45 : 18;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(encDelay)).ConfigureAwait(false); } catch { return; }
            if (_disposed)
                return;
            try { await _clip.PrepareEncoderAsync().ConfigureAwait(false); } catch { /* нет сети или ffmpeg */ }
        });
    }

    private static void FlushRender()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Render, new DispatcherOperationCallback(state =>
        {
            ((DispatcherFrame)state!).Continue = false;
            return null;
        }), frame);
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Перечитывает menu.json и сразу пересобирает колесо: без отложенного Idle.</summary>
    public void ReloadFromDisk()
    {
        CloseMenu();
        ApplyConfig(MenuStore.Load());
        EnsureMenuBuilt();
        ApplyMiddleClickSettings();
        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyConfig(MenuLoadResult result)
    {
        _actions = result.Items;
        ConfigWarning = result.Warning ?? "";
        _menuDirty = true;
    }

    private bool TryStart(Action start, string which)
    {
        try
        {
            start();
            return true;
        }
        catch (Exception ex)
        {
            if (string.IsNullOrEmpty(StatusError))
                StatusError = $"Хук «{which}» не установился: {ex.Message}";
            return false;
        }
    }

    /// <summary>Подтянуть режим СКМ из кэша настроек (без диска и без перезапуска хука).</summary>
    public void ApplyMiddleClickSettings()
    {
        _mouse.Mode = AppearanceStore.GetMiddleClickMode();
        _middleHoldTimer.Interval = TimeSpan.FromMilliseconds(AppearanceStore.GetMiddleHoldMs());
        byte side = (byte)Math.Clamp(ScreenshotStore.Current.DictationSideButton, 0, 2);
        _mouse.CaptureSideButton = side;
        _dictation?.ApplySettings();
    }

    private void OnSideDown(object? sender, MouseHook.XButtonEventArgs e)
    {
        // Хук WH_MOUSE_LL: нельзя Dispose WaveIn/Vosk на этом потоке — будет 0xc0000005.
        if (_disposed || _shot != null || _shotOpening || _clip.IsActive || _clipAsk)
            return;
        if (e.Which != _mouse.CaptureSideButton || _mouse.CaptureSideButton == 0)
            return;
        _ = Task.Run(() =>
        {
            try { _dictation?.TriggerDown(); }
            catch { /* never break the hook */ }
        });
    }

    private void OnSideUp(object? sender, MouseHook.XButtonEventArgs e)
    {
        // SideUp приходит только для захваченной кнопки (_xSwallowWhich).
        _ = Task.Run(() =>
        {
            try { _dictation?.TriggerUp(); }
            catch { /* never break the hook */ }
        });
    }

    private void OnMiddleDown(object? sender, MouseHook.ScreenPoint pt)
    {
        _dispatcher.BeginInvoke(() =>
        {
            try
            {
                // Mode берём из памяти: Save в настройках уже обновил AppearanceStore.Current.
                ApplyMiddleClickSettings();
                _middleDownPt = pt;
                _middleOpenedMenu = false;

                if (_mouse.Mode == MiddleButtonMode.Hold)
                {
                    // Ждём удержание: короткий клик потом отдадим системе.
                    _middleHoldArmed = true;
                    _middleHoldTimer.Stop();
                    _middleHoldTimer.Start();
                    return;
                }

                // Instant / Alt+СКМ: сразу колесо.
                OpenMenu(pt.X, pt.Y);
                _middleOpenedMenu = true;
            }
            catch (Exception ex)
            {
                ActionFailed?.Invoke(this, "Колесо не открылось: " + ex.Message);
            }
        });
    }

    private void OnMiddleUp(object? sender, MouseHook.ScreenPoint pt)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (!_middleHoldArmed)
                return;

            _middleHoldArmed = false;
            _middleHoldTimer.Stop();

            // Уже открыли по удержанию: up только отпускает кнопку.
            if (_middleOpenedMenu)
                return;

            // Короткий клик: как обычный СКМ Windows (вкладка в браузере и т.д.).
            try { MouseHook.SendMiddleClickPassthrough(); }
            catch { /* ignore */ }
        });
    }

    private void OnMiddleHoldTick(object? sender, EventArgs e)
    {
        _middleHoldTimer.Stop();
        if (!_middleHoldArmed || _disposed)
            return;

        _middleHoldArmed = false;
        try
        {
            OpenMenu(_middleDownPt.X, _middleDownPt.Y);
            _middleOpenedMenu = true;
        }
        catch (Exception ex)
        {
            ActionFailed?.Invoke(this, "Колесо не открылось: " + ex.Message);
        }
    }

    private void OnAnyButtonDown(object? sender, MouseHook.ScreenPoint pt)
    {
        if (_opening)
            return;
        // Снимок поверх колеса: клики рамки не должны закрывать круг.
        if (_shot != null || _shotOpening)
            return;
        if (_menu is not { IsVisible: true })
            return;

        _dispatcher.BeginInvoke(() =>
        {
            if (_opening || _shot != null || _shotOpening)
                return;
            if (_menu is not { IsVisible: true } menu)
                return;

            try
            {
                // Прозрачные дыры HWND огромные: клик «мимо диска» всё равно внутри окна.
                // Закрываем, если попадание не в кольцо, кнопки или выезд.
                if (!menu.HitsKeepAlive(pt.X, pt.Y))
                    CloseMenu();
            }
            catch
            {
                CloseMenu();
            }
        });
    }

    private void EnsureMenuBuilt()
    {
        if (_disposed)
            return;
        if (_menu != null && !_menuDirty)
            return;

        try
        {
            DestroyMenu();
            var menu = new RadialMenuWindow(_actions);
            menu.ActionChosen += OnActionChosen;
            menu.Dismissed += (_, _) => CloseMenu();
            // Создаём скрытым: Show() один раз оффскрин, потом Hide: HWND готов.
            menu.Left = -10000;
            menu.Top = -10000;
            menu.Show();
            menu.Warmup();
            // Кадр до Hide: первый Show layered-окна иначе оплачивается кликом.
            FlushRender();
            menu.Hide();
            _menu = menu;
            _menuDirty = false;
        }
        catch (Exception ex)
        {
            ActionFailed?.Invoke(this, "Не удалось подготовить колесо: " + ex.Message);
        }
    }

    private void OpenMenu(int screenX, int screenY)
    {
        _opening = true;
        try
        {
            ShellHere.Note();
            if (_shot != null || _shotOpening)
                return;

            // Запасная проверка: хук уже пропускает СКМ в игре, но отложенный прогрев не должен открыть колесо поверх.
            if (GameFocus.IsPlaying(fresh: true))
            {
                _pendingOpen = null;
                return;
            }

            if (_warming || !_warmReady)
            {
                _pendingOpen = (screenX, screenY);
                // Первый СКМ до ступени BootPace: прогреваем сразу, без ожидания таймера.
                if (!_warming)
                    BeginWarmup();
                return;
            }

            if (_menu == null || _menuDirty)
                EnsureMenuBuilt();

            if (_menu == null)
                return;

            _fileConvert?.ClosePopup();
            _mouse.ReportOtherButtons = true;
            _menu.ShowAt(screenX, screenY);
        }
        finally
        {
            // Чуть позже разрешаем «клик снаружи», чтобы открытие СКМ само не закрыло колесо.
            _dispatcher.BeginInvoke(() => _opening = false, DispatcherPriority.Input);
        }
    }

    private void OnActionChosen(object? sender, MenuAction action)
    {
        if (action.Kind is ActionKind.None or ActionKind.Group)
        {
            if (action.Kind == ActionKind.None)
                CloseMenu();
            return;
        }

        CloseMenu();
        try
        {
            ActionDispatcher.Execute(action);
        }
        catch (Exception ex)
        {
            ActionFailed?.Invoke(this, ex.Message);
        }
    }

    public void CloseMenu()
    {
        _mouse.ReportOtherButtons = false;
        if (_menu is not { IsVisible: true })
            return;

        try { _menu.Hide(); }
        catch { /* уже закрывается */ }
    }

    private void DestroyMenu()
    {
        if (_menu == null)
            return;

        var menu = _menu;
        _menu = null;
        menu.ActionChosen -= OnActionChosen;
        try { menu.Close(); }
        catch { /* уже закрыто */ }
    }

    private bool OnSwallowKey(int vk)
    {
        try
        {
            if (_dispatcher.HasShutdownStarted)
                return false;

            if (_shot != null)
            {
                if (_shot.IsEditingText)
                {
                    if (vk == 0x1B)
                        _dispatcher.BeginInvoke(() => _shot?.CancelTextEdit());
                    return vk == 0x1B;
                }

                if (_shot.IsReading)
                {
                    if (vk == 0x1B)
                    {
                        _dispatcher.BeginInvoke(() => _shot?.CancelReading());
                        return true;
                    }

                    if (_keyboard.IsCtrlDown && vk == 0x43)
                    {
                        _dispatcher.BeginInvoke(() => _shot?.CopyReading());
                        return true;
                    }
                }

                if (vk == 0x1B)
                    _dispatcher.BeginInvoke(() => _shot?.Cancel());
                else if (_keyboard.IsCtrlDown && vk == 0x41)
                    _dispatcher.BeginInvoke(() => _shot?.SelectAll());
                else if (_keyboard.IsCtrlDown && vk == 0x43)
                    _dispatcher.BeginInvoke(() => _shot?.Copy());
                else if (_keyboard.IsCtrlDown && vk == 0x53)
                    _dispatcher.BeginInvoke(() => _shot?.Save());
                else if (_keyboard.IsCtrlDown && vk == 0x59)
                    _dispatcher.BeginInvoke(() => _shot?.Redo());
                else if (_keyboard.IsCtrlDown && vk == 0x5A)
                {
                    if ((GetAsyncKeyState(0x10) & 0x8000) != 0)
                        _dispatcher.BeginInvoke(() => _shot?.Redo());
                    else
                        _dispatcher.BeginInvoke(() => _shot?.Undo());
                }
                return true;
            }

            if (_shotOpening && vk == 0x1B)
            {
                _shotCancel = true;
                return true;
            }

            if (_clipAsk)
            {
                if (vk == 0x1B)
                    _dispatcher.BeginInvoke(CancelClipAsk);
                return true;
            }

            if (_clip.IsActive)
            {
                int clipKey = ScreenshotStore.Current.ClipHotkey;
                bool bareClip = vk == clipKey && !_keyboard.IsCtrlDown && !ExtraModifierDown();
                if (vk == 0x1B || bareClip)
                {
                    _dispatcher.BeginInvoke(() => _clip.Stop());
                    return true;
                }

                // Карточку перевода в кадр не кладём. Клавишу глотаем, окно не открываем.
                if (BareTranslate(vk))
                    return true;

                return false;
            }

            if (ScreenshotStore.SuspendHotkey)
                return false;

            if (!_keyboard.IsCtrlDown && !ExtraModifierDown() && VoiceShiftLive.TryBareKey(vk))
                return true;

            int dictVk = ScreenshotStore.Current.DictationHotkey;
            if (dictVk != 0
                && vk == dictVk
                && !_keyboard.IsCtrlDown
                && !ExtraModifierDown()
                && _shot == null
                && !_shotOpening
                && !_clip.IsActive
                && !_clipAsk
                && !GameFocus.IsPlaying())
            {
                if (_dictationKeyHeld == vk)
                    return true;
                _dictationKeyHeld = vk;
                _ = Task.Run(() =>
                {
                    try { _dictation?.TriggerDown(); }
                    catch { /* ignore */ }
                });
                return true;
            }

            if (!SnippetStore.SuspendHotkeys && !GameFocus.IsPlaying() && SnippetStore.TryHit(vk, out var snippetHit))
            {
                if (_hotkeyHeld == vk)
                    return true;
                _hotkeyHeld = vk;
                var hit = snippetHit;
                _dispatcher.BeginInvoke(() => SnippetStore.Perform(hit));
                return true;
            }

            int clipHotkey = ScreenshotStore.Current.ClipHotkey;
            if (vk == clipHotkey)
            {
                if (_shotOpening)
                    return true;
                if (_keyboard.IsCtrlDown || ExtraModifierDown())
                    return false;
                _dispatcher.BeginInvoke(ToggleClip);
                return true;
            }

            if (BareTranslate(vk))
            {
                if (_shotOpening)
                    return true;
                _dispatcher.BeginInvoke(OpenClipboardTranslate);
                return true;
            }

            int hotkey = ScreenshotStore.Current.Hotkey;
            if (vk != hotkey)
                return false;
            if (_shotOpening)
                return true;
            if (_keyboard.IsCtrlDown || ExtraModifierDown())
                return false;

            _shotCancel = false;
            _shotOpening = true;
            _dispatcher.BeginInvoke(OpenScreenshot);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void OpenClipboardTranslate()
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _clip.IsActive)
            return;

        CloseMenu();
        try
        {
            ClipboardTranslateWindow.ShowCurrent();
        }
        catch
        {
            /* буфер или окно: карточка сама пишет, что не вышло */
        }
    }

    private bool BareTranslate(int vk) =>
        vk == ScreenshotStore.Current.TranslateHotkey
        && !_keyboard.IsCtrlDown
        && !ExtraModifierDown();

    private void ToggleClip()
    {
        if (_disposed || _clipAsk)
            return;
        if (_clip.IsActive)
        {
            _clip.Stop();
            return;
        }

        if (_shot != null || _shotOpening)
            return;
        CloseMenu();
        _fileConvert?.ClosePopup();
        var devices = ClipAudioDevices.Current();
        var settings = ScreenshotStore.Current;
        _clipAsk = true;
        bool accepted;
        ClipAudio audio;
        try
        {
            accepted = ClipAudioWindow.Ask(
                settings.ClipAudio,
                devices.System != null,
                devices.Microphone != null,
                out audio,
                window => _clipAskWindow = window);
        }
        finally
        {
            _clipAsk = false;
            _clipAskWindow = null;
        }

        if (!accepted || _disposed || _clip.IsActive)
            return;

        settings.ClipAudio = audio switch
        {
            ClipAudio.System => "system",
            ClipAudio.Microphone => "mic",
            ClipAudio.Both => "both",
            _ => "silent"
        };
        ScreenshotStore.Save(settings);
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed || _clip.IsActive || _shot != null || _shotOpening)
                return;
            _clip.Start(audio);
        }, DispatcherPriority.ApplicationIdle);
    }

    private void CancelClipAsk()
    {
        try { _clipAskWindow?.Close(); } catch { /* уже закрыт */ }
    }

    private void OpenScreenshot()
    {
        if (_disposed || _shot != null || _shotCancel)
        {
            _shotOpening = false;
            _shotCancel = false;
            return;
        }

        // Колесо оставляем: можно снять сам круг. Закрываем только чужие попапы.
        _fileConvert?.ClosePopup();

        var screen = PickScreenshotScreen(ScreenshotStore.Current);
        bool cursor = ScreenshotStore.Current.CaptureCursor;
        var bounds = screen.Bounds;
        string device = screen.DeviceName;

        // Кадр на следующем idle: круг уже отрисован, рамка выделения ещё не поверх.
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed || _shot != null || _shotCancel)
            {
                _shotOpening = false;
                _shotCancel = false;
                return;
            }

            _ = Task.Run(() =>
            {
                System.Drawing.Bitmap? bmp = null;
                try
                {
                    bmp = ScreenCapture.CaptureMonitor(bounds, cursor);
                }
                catch
                {
                    bmp?.Dispose();
                    bmp = null;
                }

                _dispatcher.BeginInvoke(() =>
                {
                    if (bmp == null || _disposed || _shot != null || _shotCancel)
                    {
                        bmp?.Dispose();
                        _shotOpening = false;
                        _shotCancel = false;
                        if (bmp == null && !_disposed && !_shotCancel)
                            ScreenshotToast.Show("Не удалось снять экран.", bounds, 2.4);
                        return;
                    }

                    ShowScreenshot(bmp, bounds, device);
                });
            });
        }, DispatcherPriority.ApplicationIdle);
    }

    private void ShowScreenshot(System.Drawing.Bitmap bmp, System.Drawing.Rectangle bounds, string device)
    {
        if (_disposed || _shot != null)
        {
            bmp.Dispose();
            _shotOpening = false;
            return;
        }

        bool released = false;
        void Release()
        {
            if (released)
                return;
            released = true;
            bmp.Dispose();
        }

        ScreenshotOverlayWindow? win = null;
        try
        {
            win = new ScreenshotOverlayWindow(bmp, bounds, device);
            _shot = win;
            bool startClip = false;
            win.ClipRequested += () => startClip = true;
            win.Closed += (_, _) =>
            {
                if (ReferenceEquals(_shot, win))
                    _shot = null;
                Release();
                if (!startClip)
                    return;
                startClip = false;
                _dispatcher.BeginInvoke(ToggleClip, DispatcherPriority.ApplicationIdle);
            };
            _shotOpening = false;
            win.Show();
            try { win.Activate(); } catch { /* фокус не обязателен: клавиши ловит хук */ }
        }
        catch
        {
            _shotOpening = false;
            if (win != null)
            {
                _shot = null;
                try { win.Close(); } catch { /* уже закрыто */ }
            }

            Release();
            ScreenshotToast.Show("Не удалось открыть выделение.", bounds, 2.4);
        }
    }

    private static System.Windows.Forms.Screen PickScreenshotScreen(ScreenshotSettings settings)
    {
        if (settings.RememberRegion && settings.HasRegion && !string.IsNullOrWhiteSpace(settings.MonitorDevice))
        {
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                if (string.Equals(screen.DeviceName, settings.MonitorDevice, StringComparison.OrdinalIgnoreCase))
                    return screen;
            }
        }

        return System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static bool ExtraModifierDown()
    {
        const int vkShift = 0x10;
        const int vkMenu = 0x12;
        const int vkLwin = 0x5B;
        const int vkRwin = 0x5C;
        return (GetAsyncKeyState(vkShift) & 0x8000) != 0
            || (GetAsyncKeyState(vkMenu) & 0x8000) != 0
            || (GetAsyncKeyState(vkLwin) & 0x8000) != 0
            || (GetAsyncKeyState(vkRwin) & 0x8000) != 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _middleHoldTimer.Stop();
        _middleHoldArmed = false;
        _mouse.ReportOtherButtons = false;
        try { _shot?.Close(); } catch { /* уже закрыт */ }
        _shot = null;
        try { _clip.Stop(); } catch { /* уже остановлен */ }
        _fileConvert?.Dispose();
        _fileConvert = null;
        _dictation?.Dispose();
        _dictation = null;
        DestroyMenu();
        _mouse.Dispose();
        _keyboard.Dispose();
        GC.SuppressFinalize(this);
    }
}
