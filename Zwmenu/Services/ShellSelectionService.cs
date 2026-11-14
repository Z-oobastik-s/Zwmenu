using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace Zwmenu.Services;

/// <summary>Выбранные файлы в проводнике / на рабочем столе.</summary>
public static class ShellSelectionService
{
    private const uint GaRoot = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>Папка проводника или рабочего стола, которые сейчас перед глазами. Не выбранный значок.</summary>
    public static string? TryCurrentFolder(IntPtr foreground)
    {
        try
        {
            if (foreground == IntPtr.Zero)
                foreground = GetForegroundWindow();
            if (IsDesktopSurface(foreground))
                return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null)
                return null;

            dynamic shell = Activator.CreateInstance(shellType)!;
            foreach (dynamic window in shell.Windows())
            {
                try
                {
                    IntPtr shellHwnd = new(Convert.ToInt64(window.HWND));
                    if (!IsSameOrChild(foreground, shellHwnd) && GetAncestor(foreground, GaRoot) != shellHwnd)
                        continue;
                    string folder = TryFolderPath(window);
                    if (folder.Length == 0 || !Directory.Exists(folder))
                        return null;
                    return folder;
                }
                catch
                {
                    // окно закрылось
                }
            }
        }
        catch
        {
            // COM недоступен
        }

        return null;
    }

    public static IReadOnlyList<string> GetSelectedFiles()
    {
        var list = new List<string>();
        try
        {
            IntPtr fg = GetForegroundWindow();
            bool desktopFg = IsDesktopSurface(fg);

            // 1) Рабочий стол: ListView / UI Automation (Shell.Windows часто не видит Desktop).
            if (desktopFg)
                CollectDesktopSelection(list);

            if (list.Count > 0)
                return list;

            // 2) Окна проводника через Shell.Application (папка D:\фото и т.д.).
            CollectExplorerSelection(fg, list);
        }
        catch
        {
            // COM / UIA недоступны
        }

        return list;
    }

    /// <summary>
    /// Центр выбранной иконки/файла в экранных пикселях.
    /// Нужен для кольца конвертации: якорь по ярлыку, а не по курсору.
    /// </summary>
    public static bool TryGetSelectionCenter(out int screenX, out int screenY)
    {
        screenX = 0;
        screenY = 0;
        try
        {
            IntPtr fg = GetForegroundWindow();
            if (IsDesktopSurface(fg) && TryGetDesktopSelectionCenter(out screenX, out screenY))
                return true;

            if (TryGetForegroundSelectionCenter(fg, out screenX, out screenY))
                return true;
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static bool TryGetDesktopSelectionCenter(out int screenX, out int screenY)
    {
        screenX = 0;
        screenY = 0;
        IntPtr listView = FindDesktopListView();
        if (listView == IntPtr.Zero)
            return false;

        try
        {
            var root = AutomationElement.FromHandle(listView);
            if (root == null)
                return false;

            if (root.TryGetCurrentPattern(SelectionPattern.Pattern, out object? patObj)
                && patObj is SelectionPattern sel)
            {
                var items = sel.Current.GetSelection();
                if (items.Length == 0)
                    return false;
                return AverageBoundsCenter(items, out screenX, out screenY);
            }
        }
        catch
        {
            // UIA недоступна
        }

        return false;
    }

    private static bool TryGetForegroundSelectionCenter(IntPtr foreground, out int screenX, out int screenY)
    {
        screenX = 0;
        screenY = 0;
        if (foreground == IntPtr.Zero)
            return false;

        try
        {
            var root = AutomationElement.FromHandle(GetAncestor(foreground, GaRoot));
            if (root == null)
                return false;

            var condition = new PropertyCondition(AutomationElement.IsOffscreenProperty, false);
            var candidates = root.FindAll(TreeScope.Descendants, condition);
            var selected = new List<AutomationElement>();
            foreach (AutomationElement el in candidates)
            {
                try
                {
                    if (el.Current.ControlType != ControlType.ListItem
                        && el.Current.ControlType != ControlType.DataItem
                        && el.Current.ControlType != ControlType.TreeItem)
                        continue;
                    if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? p)
                        && p is SelectionItemPattern sip
                        && sip.Current.IsSelected)
                    {
                        selected.Add(el);
                    }
                }
                catch { /* ignore item */ }
            }

            if (selected.Count == 0)
                return false;
            return AverageBoundsCenter(selected, out screenX, out screenY);
        }
        catch
        {
            return false;
        }
    }

    private static bool AverageBoundsCenter(IEnumerable<AutomationElement> items, out int screenX, out int screenY)
    {
        screenX = 0;
        screenY = 0;
        double sumX = 0, sumY = 0;
        int n = 0;
        foreach (var item in items)
        {
            try
            {
                var r = item.Current.BoundingRectangle;
                if (r.Width <= 0 || r.Height <= 0 || double.IsInfinity(r.X))
                    continue;
                // Якорим в центр иконки (не подпись): чуть выше середины прямоугольника.
                sumX += r.X + r.Width * 0.5;
                sumY += r.Y + r.Height * 0.32;
                n++;
            }
            catch { /* ignore */ }
        }

        if (n == 0)
            return false;
        screenX = (int)Math.Round(sumX / n);
        screenY = (int)Math.Round(sumY / n);
        return true;
    }

    private static void CollectExplorerSelection(IntPtr foreground, List<string> list)
    {
        Type? shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType == null)
            return;

        dynamic shell = Activator.CreateInstance(shellType)!;
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        foreach (dynamic window in shell.Windows())
        {
            try
            {
                long hwnd = Convert.ToInt64(window.HWND);
                IntPtr shellHwnd = new(hwnd);
                // Фокус часто на дочернем DirectUIHWND: считаем совпадением, если fg внутри окна проводника.
                if (!IsSameOrChild(foreground, shellHwnd) && GetAncestor(foreground, GaRoot) != shellHwnd)
                    continue;

                string folderPath = TryFolderPath(window);
                // Desktop через Shell.Windows — редко, но пусть будет.
                if (string.Equals(folderPath, desktop, StringComparison.OrdinalIgnoreCase)
                    && list.Count == 0)
                {
                    CollectSelected(window, list);
                    if (list.Count > 0)
                        return;
                }

                CollectSelected(window, list);
                if (list.Count > 0)
                    return;
            }
            catch
            {
                // окно закрылось
            }
        }
    }

    private static void CollectDesktopSelection(List<string> list)
    {
        IntPtr listView = FindDesktopListView();
        if (listView == IntPtr.Zero)
            return;

        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        // OneDrive «Desktop» иногда другой путь.
        string desktopAlt = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        try
        {
            var root = AutomationElement.FromHandle(listView);
            if (root == null)
                return;

            if (root.TryGetCurrentPattern(SelectionPattern.Pattern, out object? patObj)
                && patObj is SelectionPattern sel)
            {
                foreach (AutomationElement item in sel.Current.GetSelection())
                {
                    string name = item.Current.Name?.Trim() ?? "";
                    if (name.Length == 0)
                        continue;
                    TryAddDesktopFile(list, desktop, name);
                    if (list.Count > 0 && File.Exists(list[^1]))
                        continue;
                    if (!string.Equals(desktop, desktopAlt, StringComparison.OrdinalIgnoreCase))
                        TryAddDesktopFile(list, desktopAlt, name);
                }
                return;
            }
        }
        catch
        {
            // UIA недоступна
        }
    }

    private static void TryAddDesktopFile(List<string> list, string desktopDir, string name)
    {
        // Name из ListView: «photo.png» или без расширения для некоторых типов — пробуем как есть.
        string path = Path.Combine(desktopDir, name);
        if (File.Exists(path) || Directory.Exists(path))
        {
            list.Add(path);
            return;
        }

        // Иногда в Name нет расширения: ищем единственный файл с таким префиксом.
        try
        {
            if (!Directory.Exists(desktopDir))
                return;
            var matches = Directory.EnumerateFiles(desktopDir, name + ".*")
                .Take(3)
                .ToList();
            if (matches.Count == 1)
                list.Add(matches[0]);
        }
        catch { /* ignore */ }
    }

    private static IntPtr FindDesktopListView()
    {
        IntPtr progman = FindWindow("Progman", null);
        IntPtr defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView != IntPtr.Zero)
        {
            IntPtr lv = FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
            if (lv != IntPtr.Zero)
                return lv;
        }

        // После смены обоев DefView часто висит в WorkerW.
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            var sb = new StringBuilder(64);
            GetClassName(h, sb, sb.Capacity);
            if (sb.ToString() != "WorkerW")
                return true;
            IntPtr view = FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view == IntPtr.Zero)
                return true;
            IntPtr lv = FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
            if (lv == IntPtr.Zero)
                return true;
            found = lv;
            return false;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>Окно вида рабочего стола. Ему уходит команда «показать или скрыть значки».</summary>
    public static IntPtr FindDesktopDefView()
    {
        IntPtr listView = FindDesktopListView();
        if (listView == IntPtr.Zero)
            return IntPtr.Zero;
        return GetParent(listView);
    }

    private static void CollectSelected(dynamic window, List<string> list)
    {
        dynamic doc = window.Document;
        dynamic items = doc.SelectedItems();
        int count = (int)items.Count;
        for (int i = 0; i < count; i++)
        {
            try
            {
                dynamic item = items.Item(i);
                string? path = item.Path as string;
                if (string.IsNullOrWhiteSpace(path))
                    continue;
                if (File.Exists(path) || Directory.Exists(path))
                    list.Add(path);
            }
            catch { /* ignore item */ }
        }
    }

    private static string TryFolderPath(dynamic window)
    {
        try
        {
            return (window.Document.Folder.Self.Path as string) ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static bool IsDesktopSurface(IntPtr hwnd)
    {
        IntPtr cur = hwnd;
        for (int i = 0; i < 8 && cur != IntPtr.Zero; i++)
        {
            if (ClassName(cur) is "Progman" or "WorkerW" or "SHELLDLL_DefView" or "SysListView32")
                return true;
            cur = GetParent(cur);
        }
        return ClassName(hwnd) is "Progman" or "WorkerW";
    }

    private static bool IsSameOrChild(IntPtr childOrSelf, IntPtr ancestor)
    {
        IntPtr cur = childOrSelf;
        while (cur != IntPtr.Zero)
        {
            if (cur == ancestor)
                return true;
            cur = GetParent(cur);
        }
        return false;
    }

    /// <summary>Снять выделение в проводнике и на рабочем столе, чтобы файл не остался «приклеен» к курсору.</summary>
    public static void ClearSelection()
    {
        try
        {
            IntPtr fg = GetForegroundWindow();
            if (IsDesktopSurface(fg))
                ClearAutomation(FindDesktopListView());
            else
                ClearExplorer(fg);
        }
        catch
        {
            // COM / UIA заняты
        }
    }

    private static void ClearAutomation(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;
        var root = AutomationElement.FromHandle(hwnd);
        if (root == null)
            return;
        if (!root.TryGetCurrentPattern(SelectionPattern.Pattern, out object? patObj) || patObj is not SelectionPattern sel)
            return;
        foreach (AutomationElement item in sel.Current.GetSelection())
        {
            try
            {
                if (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? ip) && ip is SelectionItemPattern sip)
                    sip.RemoveFromSelection();
            }
            catch
            {
                // элемент уже исчез
            }
        }
    }

    private static void ClearExplorer(IntPtr foreground)
    {
        Type? shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType == null)
            return;
        dynamic shell = Activator.CreateInstance(shellType)!;
        foreach (dynamic window in shell.Windows())
        {
            try
            {
                IntPtr shellHwnd = new(Convert.ToInt64(window.HWND));
                if (!IsSameOrChild(foreground, shellHwnd) && GetAncestor(foreground, GaRoot) != shellHwnd)
                    continue;
                dynamic doc = window.Document;
                dynamic items = doc.SelectedItems();
                int count = (int)items.Count;
                for (int i = count - 1; i >= 0; i--)
                {
                    try { doc.SelectItem(items.Item(i), 0); }
                    catch { /* этот значок уже снят */ }
                }
            }
            catch
            {
                // окно закрылось
            }
        }
    }

    private static string ClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return "";
        var sb = new StringBuilder(64);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
