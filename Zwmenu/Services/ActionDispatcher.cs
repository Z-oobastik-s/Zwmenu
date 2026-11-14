using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Zwmenu.Models;
using Zwmenu.Overlay;
using Zwmenu.Workout;

namespace Zwmenu.Services;

public static class ActionDispatcher
{
    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint KeyeventfExtendedkey = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeybdInput ki;
        [FieldOffset(0)] public MouseInput mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    public static event EventHandler<string>? ActionFailed;
    public static event EventHandler<ActionProblem>? Problem;

    public static void Execute(MenuAction action)
    {
        try
        {
            switch (action.Kind)
            {
                case ActionKind.None:
                case ActionKind.Group:
                    break;
                case ActionKind.Keys:
                    SendShortcut(action.Value);
                    break;
                case ActionKind.Launch:
                    Launch(action);
                    break;
            }
        }
        catch (Exception ex)
        {
            RaiseFriendly(action, ex);
        }
    }

    private static void Launch(MenuAction action)
    {
        string target = (action.Value ?? "").Trim().Trim('"');
        if (target.Length == 0)
        {
            RaiseProblem(action,
                "Путь не указан",
                "У этого пункта на колесе пустой путь. Укажи файл, программу или папку: и всё заработает.",
                null,
                canFix: true);
            return;
        }

        if (target.StartsWith("zwmenu:", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(target, "zwmenu:workout", StringComparison.OrdinalIgnoreCase))
                OpenWorkoutWindow();
            else if (string.Equals(target, MenuAction.ClipboardTranslateValue, StringComparison.OrdinalIgnoreCase))
                ClipboardTranslateWindow.ShowCurrent();
            else if (string.Equals(target, MenuAction.EdgeShareValue, StringComparison.OrdinalIgnoreCase))
                EdgeShareWindow.ShowCurrent();
            else if (string.Equals(target, MenuAction.SnippetsValue, StringComparison.OrdinalIgnoreCase))
                SnippetWindow.ShowCurrent();
            else if (string.Equals(target, MenuAction.VoiceRecValue, StringComparison.OrdinalIgnoreCase))
                VoiceRecWindow.ShowCurrent();
            else if (string.Equals(target, "zwmenu:voicerec:start", StringComparison.OrdinalIgnoreCase))
                VoiceRecWindow.ShowCurrent(startRecording: true);
            else if (string.Equals(target, "zwmenu:voicerec:pause", StringComparison.OrdinalIgnoreCase))
                VoiceRecService.Shared.Pause();
            else if (string.Equals(target, "zwmenu:voicerec:resume", StringComparison.OrdinalIgnoreCase))
                VoiceRecService.Shared.Resume();
            else if (string.Equals(target, "zwmenu:voicerec:stop", StringComparison.OrdinalIgnoreCase))
            {
                VoiceRecWindow.ShowCurrent();
                VoiceRecService.Shared.StopAndSave();
            }
            else if (string.Equals(target, MenuAction.DesktopIconsValue, StringComparison.OrdinalIgnoreCase))
            {
                if (!DesktopIcons.TryToggle(out bool hidden, out string error))
                {
                    DesktopIcons.Toast(error);
                    return;
                }

                MenuStore.RememberDesktopIcons(hidden);
                DesktopIcons.Toast(hidden ? "Иконки скрыты" : "Иконки на месте");
            }
            else if (string.Equals(target, MenuAction.ShelfValue, StringComparison.OrdinalIgnoreCase))
                ShelfWindow.Toggle();
            else if (string.Equals(target, MenuAction.WorkshopValue, StringComparison.OrdinalIgnoreCase))
                WorkshopWindow.ShowCurrent();
            return;
        }

        if (MenuAction.IsWebAddress(target) || MenuAction.IsProtocolTarget(target))
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
            return;
        }

        // Приложения из Microsoft Store / Пуска без «Расположение файла».
        if (MenuAction.IsShellAppTarget(target))
        {
            try
            {
                string shellTarget = MenuAction.NormalizeShellAppTarget(target);
                Process.Start(new ProcessStartInfo(shellTarget) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex)
            {
                RaiseProblem(action,
                    $"«{action.Label}» не открылось",
                    "Это приложение из Пуска (Store). Не удалось запустить через AppsFolder. Проверь, что Claude установлен.",
                    target,
                    canFix: false);
                ActionFailed?.Invoke(null, ex.Message);
            }
            return;
        }

        if (IsCmdExe(target))
        {
            try
            {
                FolderConsole.Launch(ShellHere.Folder(), action.RunAsAdmin);
            }
            catch (Exception ex)
            {
                if (ex is System.ComponentModel.Win32Exception win && win.NativeErrorCode == 1223)
                    return;
                RaiseFriendly(action, ex);
            }
            return;
        }

        // Локальный путь (диск:\..., \\share, относительный с слэшем): проверяем до запуска.
        if (LooksLikeLocalPath(target))
        {
            if (!File.Exists(target) && !Directory.Exists(target))
            {
                RaiseProblem(action,
                    $"«{action.Label}» не найдено",
                    "Файл или папка больше не лежит по сохранённому пути. Часто так бывает после переноса программы в другую папку. Укажи новый путь: пункт на колесе обновится.",
                    target,
                    canFix: true);
                return;
            }
        }

        // Уже запущено (браузер, Cursor…) — развернуть/на передний план, не плодить второй процесс.
        // С аргументами (URL, панель управления и т.п.) — всегда новый вызов.
        // explorer.exe живёт всегда как оболочка: «активировать» его = ничего не открыть.
        bool hasArgs = !string.IsNullOrWhiteSpace(action.Arguments);
        if (IsExplorerExe(target))
        {
            string explorerArgs = hasArgs ? action.Arguments.Trim() : "shell:MyComputerFolder";
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe")
                {
                    Arguments = explorerArgs,
                    UseShellExecute = true
                })?.Dispose();
            }
            catch (Exception ex)
            {
                RaiseFriendly(action, ex);
            }
            return;
        }

        if (!hasArgs && !action.RunAsAdmin && TryActivateRunning(target))
            return;

        try
        {
            var info = new ProcessStartInfo(target) { UseShellExecute = true };
            if (hasArgs && AcceptsArguments(target))
                info.Arguments = action.Arguments;
            if (action.RunAsAdmin && AcceptsArguments(target))
                info.Verb = "runas";

            Process.Start(info)?.Dispose();
        }
        catch (Exception ex)
        {
            // Пользователь нажал «Нет» в UAC: не орём как о фатальной ошибке.
            if (ex is System.ComponentModel.Win32Exception win && win.NativeErrorCode == 1223)
                return;
            RaiseFriendly(action, ex);
        }
    }

    /// <summary>
    /// Если .exe уже крутится — показать его окно. Папки / протоколы не трогаем.
    /// </summary>
    private static bool TryActivateRunning(string target)
    {
        string exe = (target ?? "").Trim().Trim('"');
        if (exe.Length == 0)
            return false;
        if (Directory.Exists(exe))
            return false;

        string ext = Path.GetExtension(exe);
        if (ext.Length > 0 && !ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return false;

        string processName = Path.GetFileNameWithoutExtension(exe);
        if (processName.Length == 0)
            return false;

        string? fullExe = null;
        try
        {
            if (File.Exists(exe))
                fullExe = Path.GetFullPath(exe);
        }
        catch { /* PATH-only вроде notepad.exe */ }

        Process[]? procs = null;
        try
        {
            procs = Process.GetProcessesByName(processName);
            if (procs.Length == 0)
                return false;

            var pids = new HashSet<int>();
            foreach (var p in procs)
            {
                try
                {
                    if (fullExe != null)
                    {
                        try
                        {
                            string? mod = p.MainModule?.FileName;
                            if (!string.IsNullOrEmpty(mod)
                                && !string.Equals(Path.GetFullPath(mod), fullExe, StringComparison.OrdinalIgnoreCase))
                                continue;
                        }
                        catch
                        {
                            // AccessDenied у части процессов — оставляем по имени.
                        }
                    }

                    pids.Add(p.Id);
                    IntPtr main = p.MainWindowHandle;
                    if (main != IntPtr.Zero && IsWindowVisible(main) && ActivateWindow(main))
                        return true;
                }
                catch
                {
                    // ignore one process
                }
            }

            if (pids.Count == 0)
                return false;

            // Chrome / Opera: окно часто не у MainWindowHandle первого процесса.
            IntPtr found = FindVisibleWindowForPids(pids);
            return found != IntPtr.Zero && ActivateWindow(found);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (procs != null)
            {
                foreach (var p in procs)
                {
                    try { p.Dispose(); } catch { /* ignore */ }
                }
            }
        }
    }

    private static IntPtr FindVisibleWindowForPids(HashSet<int> pids)
    {
        IntPtr best = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd))
                return true;
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (!pids.Contains((int)pid))
                return true;
            // Без хозяина (owner) — обычно главное окно приложения.
            if (GetWindow(hWnd, GwOwner) != IntPtr.Zero)
                return true;
            int len = GetWindowTextLength(hWnd);
            if (len <= 0)
                return true;
            best = hWnd;
            return false; // stop
        }, IntPtr.Zero);
        return best;
    }

    private static bool ActivateWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
            return false;

        if (IsIconic(hWnd))
            ShowWindow(hWnd, SwRestore);
        else
            ShowWindow(hWnd, SwShow);

        uint foreThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint thisThread = GetCurrentThreadId();
        if (foreThread != 0 && foreThread != thisThread)
            AttachThreadInput(foreThread, thisThread, true);

        bool ok = SetForegroundWindow(hWnd);
        BringWindowToTop(hWnd);

        if (foreThread != 0 && foreThread != thisThread)
            AttachThreadInput(foreThread, thisThread, false);

        return ok || GetForegroundWindow() == hWnd;
    }

    private const int SwRestore = 9;
    private const int SwShow = 5;
    private const uint GwOwner = 4;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    private static void RaiseFriendly(MenuAction action, Exception ex)
    {
        string raw = ex.Message ?? "";
        bool missing = IsMissingFileError(raw) || action.PathMissing;
        string path = (action.Value ?? "").Trim();

        if (missing)
        {
            RaiseProblem(action,
                $"«{action.Label}» не найдено",
                "Не удалось запустить: по старому пути ничего нет. Скорее всего программу перенесли или удалили. Укажи актуальный файл или папку.",
                path,
                canFix: true);
            return;
        }

        RaiseProblem(action,
            $"«{action.Label}» не открылось",
            "Запуск не удался. Проверь путь в настройках колеса или укажи другой файл."
            + (string.IsNullOrWhiteSpace(raw) ? "": $"\n\nТехнически: {Shorten(raw)}"),
            string.IsNullOrWhiteSpace(path) ? null: path,
            canFix: LooksLikeLocalPath(path));
    }

    private static void RaiseProblem(MenuAction action, string title, string explanation, string? path, bool canFix)
    {
        var problem = new ActionProblem
        {
            Action = action,
            Title = title,
            Explanation = explanation,
            BrokenPath = path,
            CanFixPath = canFix
        };
        Problem?.Invoke(null, problem);
        ActionFailed?.Invoke(null, $"{title}. {explanation}");
    }

    private static WorkoutWindow? _workout;

    public static void OpenWorkoutWindow()
    {
        var app = Application.Current;
        if (app == null)
            return;

        void Show()
        {
            if (_workout != null)
            {
                if (_workout.WindowState == WindowState.Minimized)
                    _workout.WindowState = WindowState.Normal;
                _workout.Activate();
                return;
            }

            _ = WorkoutVoiceListener.PreloadAsync();
            var w = new WorkoutWindow();
            _workout = w;
            w.Closed += (_, _) =>
            {
                if (ReferenceEquals(_workout, w))
                    _workout = null;
            };
            w.Show();
            w.Activate();
        }

        if (app.Dispatcher.CheckAccess())
            Show();
        else
            app.Dispatcher.Invoke(Show);
    }

    private static bool IsExplorerExe(string target)
    {
        string name = Path.GetFileName(target.Trim().Trim('"'));
        return name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("explorer", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCmdExe(string target)
    {
        string name = Path.GetFileName(target.Trim().Trim('"'));
        return name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cmd", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeLocalPath(string target) =>
        target.Contains('\\') || target.Contains('/') || (target.Length >= 2 && target[1] == ':');

    private static bool IsMissingFileError(string message)
    {
        string m = message.ToLowerInvariant();
        return m.Contains("не удается найти")
            || m.Contains("не удаётся найти")
            || m.Contains("cannot find")
            || m.Contains("not found")
            || m.Contains("file not found")
            || m.Contains("directorynotfound")
            || m.Contains("filenotfound");
    }

    private static string Shorten(string message)
    {
        // Убираем англоязычный мусор про working directory.
        int cut = message.IndexOf("with working directory", StringComparison.OrdinalIgnoreCase);
        if (cut > 0)
            message = message[..cut].Trim().TrimEnd('.');
        if (message.Length > 180)
            message = message[..177] + "…";
        return message;
    }

    private static bool AcceptsArguments(string target)
    {
        string ext = Path.GetExtension(target);
        return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase);
    }

    private static void SendShortcut(string shortcut)
    {
        var keys = ParseShortcut(shortcut);
        if (keys.Count == 0)
            return;

        var downs = new List<Input>();
        var ups = new List<Input>();

        foreach (ushort vk in keys)
            downs.Add(Key(vk, up: false));

        for (int i = keys.Count - 1; i >= 0; i--)
            ups.Add(Key(keys[i], up: true));

        var all = downs.Concat(ups).ToArray();
        if (SendInput((uint)all.Length, all, Marshal.SizeOf<Input>()) == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private static Input Key(ushort vk, bool up)
    {
        uint flags = up ? KeyeventfKeyup: 0;
        if (IsExtended(vk))
            flags |= KeyeventfExtendedkey;

        return new Input
        {
            type = InputKeyboard,
            u = new InputUnion { ki = new KeybdInput { wVk = vk, dwFlags = flags } }
        };
    }

    private static bool IsExtended(ushort vk) => vk is 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x21 or 0x22 or 0x23 or 0x24;

    private static List<ushort> ParseShortcut(string shortcut)
    {
        var result = new List<ushort>();
        foreach (var part in shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            ushort? vk = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => 0x11,
                "ALT" => 0x12,
                "SHIFT" => 0x10,
                "WIN" or "WINDOWS" or "LWIN" => 0x5B,
                "ESC" or "ESCAPE" => 0x1B,
                "ENTER" or "RETURN" => 0x0D,
                "TAB" => 0x09,
                "SPACE" => 0x20,
                "DELETE" or "DEL" => 0x2E,
                "BACKSPACE" => 0x08,
                _ when part.Length == 1 && char.IsLetterOrDigit(part[0]) =>
                    (ushort)char.ToUpperInvariant(part[0]),
                _ => null
            };

            if (vk is null)
                throw new ArgumentException($"Неизвестная клавиша «{part}» в сочетании «{shortcut}».");

            result.Add(vk.Value);
        }

        return result;
    }
}
