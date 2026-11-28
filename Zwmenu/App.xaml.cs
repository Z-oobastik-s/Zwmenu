using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Zwmenu.Overlay;
using Zwmenu.Services;
using Zwmenu.Settings;
using Zwmenu.Workout;

namespace Zwmenu;

public partial class App: Application
{
    private LauncherService? _launcher;
    private TrayIconService? _tray;
    private MainWindow? _window;
    private SettingsWindow? _settings;
    private PathFixWindow? _pathFix;
    private bool _exiting;
    private Mutex? _singleInstance;
    private bool _ownsInstance;
    private int _uiFault;
    private WorkoutReminderService? _workoutReminder;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (PrintPeer.TakeRequest(e.Args))
            return;
        if (PhotoOpen.TakeRequest(e.Args))
            return;
        if (VideoOpen.TakeRequest(e.Args))
            return;
        if (MusicOpen.TakeRequest(e.Args))
            return;
        if (e.Args.Any(a => string.Equals(a, "--looksheet", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            LookSheet.Run();
            return;
        }

        if (e.Args.Any(a => string.Equals(a, "--desk-preview", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            DeskFlyoutPreview.Run();
            return;
        }

        if (e.Args.Any(a => string.Equals(a, "--ui-preview", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            UiSurfacePreview.Run();
            return;
        }

        if (e.Args.Any(a => string.Equals(a, "--flyout-preview", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            FlyoutPreview.Run();
            return;
        }

        if (e.Args.Any(a => string.Equals(a, "--selfcheck", StringComparison.OrdinalIgnoreCase)))
        {
            string report = Path.Combine(Path.GetTempPath(), "zwmenu-selfcheck.txt");
            try
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                BatchRename.SelfCheck();
                PictureBench.SelfCheck();
                ClipBench.SelfCheck();
                ServerDesk.SelfCheck();
                MachineDesk.SelfCheck();
                FileLock.SelfCheck();
                FolderConsole.SelfCheck();
                BackgroundRemoveService.SelfCheck();
                AppUpdate.SelfCheck();
                SetupOffer.SelfCheck();
                PhotoOpen.SelfCheck();
                VideoOpen.SelfCheck();
                MusicOpen.SelfCheck();
                FileKindIcons.SelfCheck();
                ScreenCapture.SelfCheck();
                ShelfArrange.SelfCheck();
                EdgeCapture.SelfCheck();
                VoiceShift.SelfCheck();
                VoiceShiftLive.SelfCheckPolicy();
                WorkshopWindow.Smoke();
                DiskSweep.SelfCheck();
                File.WriteAllText(report, "ok");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(report, ex.ToString());
                Environment.Exit(1);
            }
        }

        if (!TryOwnInstance())
        {
            Environment.Exit(0);
            return;
        }
        SessionEnding += (_, args) =>
        {
            args.Cancel = false;
            ExitApp(fromWindows: true);
        };
        AutostartService.RepairStoredPath();
        try { SnippetStore.Load(); } catch { /* шаблоны подтянутся при открытии окна */ }
        UiSounds.HookGlobal();

        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            if (_exiting || Interlocked.Exchange(ref _uiFault, 1) != 0)
                return;
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zwmenu");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "last-error.txt"), args.Exception.ToString());
            }
            catch
            {
                // журнал необязателен
            }
            try
            {
                System.Windows.MessageBox.Show(
                    "Ошибка: " + args.Exception.Message + "\n\nZwmenu закроется, чтобы не оставить сломанные хуки.",
                    "Zwmenu",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch
            {
                // окно ошибки уже не показать
            }
            try { ExitApp(fromWindows: true); }
            catch { Environment.Exit(1); }
        };

        CursorKeep.Install();
        _launcher = new LauncherService();
        _launcher.ActionProblem += (_, problem) => Dispatcher.BeginInvoke(() => ShowPathFix(problem));
        _launcher.ClipStateChanged += phase => _tray?.SetClipPhase(phase);
        _launcher.Start();

        // Без окна на старте: сразу в трей. MainWindow создаём при первом открытии статуса.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _tray = new TrayIconService();
        _tray.ShowRequested += (_, _) => ShowMainWindow();
        _tray.SettingsRequested += (_, _) => ShowSettings();
        _tray.ExitRequested += (_, _) => ExitApp();
        _tray.Show();

        EdgeShareService.Shared.Changed += () =>
        {
            if (EdgeShareService.Shared.Pending != null)
                Dispatcher.BeginInvoke(EdgeShareWindow.ShowCurrent);
        };

        // Открытие файла из проводника: серверы сразу. Иначе поднимем их ступенью BootPace.
        bool openMedia = e.Args.Any(a =>
            string.Equals(a, "--photo", StringComparison.OrdinalIgnoreCase)
            || string.Equals(a, "--video", StringComparison.OrdinalIgnoreCase)
            || string.Equals(a, "--music", StringComparison.OrdinalIgnoreCase));
        if (openMedia)
        {
            PrintPeer.StartServer();
            PhotoOpen.StartServer();
            VideoOpen.StartServer();
            MusicOpen.StartServer();
            PhotoOpen.OpenFromArgs(e.Args);
            VideoOpen.OpenFromArgs(e.Args);
            MusicOpen.OpenFromArgs(e.Args);
        }

        UpdateWindow.ExitForUpdate = () =>
        {
            try { _settings?.CloseForExit(); } catch { /* окно настроек уже закрыто */ }
            ExitApp(fromWindows: true);
        };
        if (e.Args.Any(a => string.Equals(a, "--updated", StringComparison.OrdinalIgnoreCase)))
            UpdateDoneWindow.ShowNow();
        if (e.Args.Any(a => string.Equals(a, "--update-failed", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(() =>
                UpdateWindow.ShowNote("Обновление не поставилось. Файлы не скопировались. Попробуй снова или поставь полный установщик с GitHub."));
        }

        // Ступени: после перезагрузки ПК паузы длиннее (BootPace.SoftLogin).
        BootPace.Schedule(Dispatcher,
            (1.2, 3.5, () =>
            {
                _launcher?.WarmGameFocus();
                StayAwake.Start();
            }),
            (2.5, 7.0, () =>
            {
                try { ShelfWindow.Restore(); } catch { /* полка поднимется из настроек */ }
            }),
            (4.0, 12.0, () => _launcher?.BeginWarmup()),
            (6.0, 16.0, () =>
            {
                if (!openMedia)
                {
                    PrintPeer.StartServer();
                    PhotoOpen.StartServer();
                    VideoOpen.StartServer();
                    MusicOpen.StartServer();
                }

                EdgeShareService.Shared.Start();
            }),
            (8.0, 22.0, () =>
            {
                _workoutReminder ??= new WorkoutReminderService();
            }),
            (10.0, 28.0, () =>
            {
                _ = BootPace.RunLowAsync(DiskSweep.Run);
            }),
            (12.0, 32.0, () =>
            {
                try { VoiceShiftLive.Shared.Restore(); }
                catch { /* окно голоса покажет, если микрофон не открылся */ }
            }),
            (14.0, 36.0, () => SetupOffer.CheckSoon()),
            (18.0, 48.0, () =>
            {
                AppUpdate.CheckSoon(manual: false);
                BootPace.TrimWorkingSet();
            }));
    }

    private void ShowPathFix(ActionProblem problem)
    {
        _launcher?.CloseMenu();

        if (_pathFix != null)
        {
            _pathFix.Activate();
            return;
        }

        if (_launcher == null)
            return;

        var window = new PathFixWindow(problem, (action, path) => _launcher.UpdateLaunchPath(action, path));
        _pathFix = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_pathFix, window))
                _pathFix = null;
        };
        window.Show();
        window.Activate();
    }

    private void ShowSettings()
    {
        _launcher?.CloseMenu();

        if (_settings != null)
        {
            _settings.Show();
            if (_settings.WindowState != WindowState.Maximized)
                _settings.WindowState = WindowState.Maximized;
            _settings.Activate();
            return;
        }

        var window = new SettingsWindow();
        _settings = window;
        window.Saved += (_, _) => _launcher?.ReloadFromDisk();
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_settings, window))
                _settings = null;
        };
        window.Show();
    }

    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting)
            return;

        // Hide to tray instead of quitting: hooks stay alive.
        e.Cancel = true;
        _window?.Hide();
    }

    private void ShowMainWindow()
    {
        if (_launcher == null)
            return;

        if (_window == null)
        {
            _window = new MainWindow(_launcher);
            _window.Closing += OnMainWindowClosing;
            _window.SettingsRequested += (_, _) => ShowSettings();
        }

        _window.Show();
        _window.Activate();
    }

    private bool TryOwnInstance()
    {
        try
        {
            _singleInstance = new Mutex(false, "ZwmenuSingleInstance");
            try
            {
                _ownsInstance = _singleInstance.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                _ownsInstance = true;
            }

            return _ownsInstance;
        }
        catch
        {
            return true;
        }
    }

    private void ReleaseInstance()
    {
        if (!_ownsInstance || _singleInstance == null)
            return;
        try { _singleInstance.ReleaseMutex(); }
        catch { /* уже отпущен */ }
        _ownsInstance = false;
        try { _singleInstance.Dispose(); }
        catch { /* уже закрыт */ }
        _singleInstance = null;
    }

    private void ExitApp() => ExitApp(fromWindows: false);

    private void ExitApp(bool fromWindows)
    {
        if (_exiting)
            return;

        if (!fromWindows && _settings != null)
        {
            _settings.CloseForExit();
            if (_settings != null)
                return;
        }

        _exiting = true;
        BootPace.CancelAll();
        VoiceShiftLive.Shared.StopForExit();
        PrintPeer.StopServer();
        PhotoOpen.StopServer();
        VideoOpen.StopServer();
        MusicOpen.StopServer();
        AppUpdate.Stop();
        EdgeShareService.Shared.Dispose();
        _workoutReminder?.Dispose();
        _launcher?.Dispose();
        _tray?.Dispose();
        try { StayAwake.SetManual(false); } catch { /* необязательно */ }

        if (_window != null)
        {
            _window.Closing -= OnMainWindowClosing;
            _window.Close();
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseInstance();
        EdgeShareService.Shared.Dispose();
        _workoutReminder?.Dispose();
        _launcher?.Dispose();
        _tray?.Dispose();
        base.OnExit(e);
    }
}
