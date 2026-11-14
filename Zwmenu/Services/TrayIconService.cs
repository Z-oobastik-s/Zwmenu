using Zwmenu.Overlay;

namespace Zwmenu.Services;

/// <summary>Иконка в системном трее.</summary>
public sealed class TrayIconService: IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ToolStripMenuItem _recent;
    private readonly System.Windows.Forms.ToolStripMenuItem _edges;
    private int _clipPhase;
    private bool _disposed;

    public event EventHandler? ShowRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;

    public TrayIconService()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        _recent = new System.Windows.Forms.ToolStripMenuItem("Недавние снимки");
        menu.Items.Add(_recent);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _edges = new System.Windows.Forms.ToolStripMenuItem("Выключить два экрана");
        _edges.Click += (_, _) => EdgeShareService.Shared.Toggle();
        menu.Opening += (_, _) =>
        {
            FillRecent();
            _edges.Text = EdgeShareService.Shared.Running ? "Выключить два экрана" : "Включить два экрана";
        };
        menu.Items.Add("Открыть статус", null, (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(_edges);
        menu.Items.Add("Настроить два экрана", null, (_, _) => EdgeShareWindow.ShowCurrent());
        menu.Items.Add("Шаблоны", null, (_, _) => SnippetWindow.ShowCurrent());
        menu.Items.Add("Настройки колеса", null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Проверить обновление", null, (_, _) => AppUpdate.CheckSoon(manual: true));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Text = TrayTip(),
            Visible = false,
            Icon = LoadAppIcon(),
            ContextMenuStrip = menu
        };

        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Show() => _icon.Visible = true;

    public void SetClipPhase(int phase)
    {
        _clipPhase = phase;
        try { _icon.Text = TrayTip(); } catch { /* подсказка трея не критична */ }
    }

    private void FillRecent()
    {
        _recent.DropDownItems.Clear();
        var paths = ShotHistory.Paths();
        if (paths.Count == 0)
        {
            _recent.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem("Пока нет снимков") { Enabled = false });
            return;
        }

        int n = 1;
        foreach (var path in paths)
        {
            string label = n + ". " + ShotClock(path);
            string file = path;
            _recent.DropDownItems.Add(label, null, (_, _) => CopyShot(file));
            n++;
        }
    }

    private static void CopyShot(string path)
    {
        try
        {
            using var bmp = new System.Drawing.Bitmap(path);
            ScreenCapture.CopyToClipboard(bmp);
            var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            if (ScreenshotStore.Current.ShowNotifications)
                ScreenshotToast.Show("Снимок в буфере обмена", screen.Bounds);
        }
        catch
        {
            var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            ScreenshotToast.Show("Не удалось скопировать снимок", screen.Bounds, 2.2);
        }
    }

    private static string ShotClock(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (name.Length >= 15 && name[8] == '-')
            return name.Substring(9, 2) + ":" + name.Substring(11, 2) + ":" + name.Substring(13, 2);
        return name;
    }

    private string TrayTip()
    {
        try
        {
            if (_clipPhase == 2)
                return "Zwmenu: идёт запись, Esc — стоп";
            if (_clipPhase == 1)
                return "Zwmenu: готовлю запись";
            string tip = "Zwmenu: СКМ колесо, "
                + ScreenshotStore.HotkeyTitle(ScreenshotStore.Current.Hotkey)
                + " снимок, "
                + ScreenshotStore.HotkeyTitle(ScreenshotStore.Current.ClipHotkey)
                + " клип, "
                + ScreenshotStore.HotkeyTitle(ScreenshotStore.Current.TranslateHotkey)
                + " перевод";
            var ds = ScreenshotStore.Current;
            if (ds.DictationSideButton is 1 or 2)
                tip += ds.DictationSideButton == 1 ? ", X1 диктовка" : ", X2 диктовка";
            else if (ds.DictationHotkey != 0)
                tip += ", " + ScreenshotStore.HotkeyTitle(ds.DictationHotkey) + " диктовка";
            return tip.Length <= 127 ? tip : tip[..127];
        }
        catch
        {
            return "Zwmenu";
        }
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "zwmenu.ico");
            if (File.Exists(path))
                return new System.Drawing.Icon(path);
        }
        catch
        {
            // fallback ниже
        }
        return System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        GC.SuppressFinalize(this);
    }
}
