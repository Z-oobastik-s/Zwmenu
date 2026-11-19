using System.Reflection;
using System.Windows.Forms;
using Zwmenu.Overlay;

namespace Zwmenu.Services;

/// <summary>Иконка в системном трее.</summary>
public sealed class TrayIconService: IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ToolStripMenuItem _recent;
    private readonly System.Windows.Forms.ToolStripMenuItem _edges;
    private readonly System.Windows.Forms.ToolStripMenuItem _voice;
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
            FillVoice();
            _edges.Text = EdgeShareService.Shared.Running ? "Выключить два экрана" : "Включить два экрана";
        };
        menu.Items.Add("Открыть статус", null, (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Просмотр фото", null, (_, _) => PhotoWindow.PickAndShow());
        menu.Items.Add("Просмотр видео", null, (_, _) => VideoWindow.PickAndShow());
        menu.Items.Add("Музыка", null, (_, _) => MusicWindow.PickAndShow());
        menu.Items.Add(_edges);
        menu.Items.Add("Настроить два экрана", null, (_, _) => EdgeShareWindow.ShowCurrent());
        menu.Items.Add("Шаблоны", null, (_, _) => SnippetWindow.ShowCurrent());
        _voice = new System.Windows.Forms.ToolStripMenuItem("Голос")
        {
            DropDown = new WheelMenu()
        };
        menu.Items.Add(_voice);
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

    private void FillVoice()
    {
        _voice.DropDownItems.Clear();
        _voice.DropDownItems.Add("Открыть настройки", null, (_, _) => VoiceShiftWindow.ShowCurrent());
        string toggle = VoiceShiftLive.Shared.Running ? "Выключить" : "Включить";
        _voice.DropDownItems.Add(toggle, null, (_, _) => VoiceShiftLive.Shared.ToggleSaved());
        bool hear = ScreenshotStore.Current.VoiceShiftHear;
        var hearItem = new System.Windows.Forms.ToolStripMenuItem("Слышать себя") { Checked = hear };
        hearItem.Click += (_, _) => VoiceShiftLive.Shared.SetHear(!ScreenshotStore.Current.VoiceShiftHear, startIfOff: true);
        _voice.DropDownItems.Add(hearItem);
        _voice.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
        AddVoices("Девушки", color => color.Model.Length > 0);
        AddVoices("Образы", color => color.Model.Length == 0 && color.Face.On);
        AddVoices("Эффекты", color => color.Model.Length == 0 && !color.Face.On);
    }

    private void AddVoices(string title, Func<VoiceColor, bool> take)
    {
        var group = new System.Windows.Forms.ToolStripMenuItem(title) { DropDown = new WheelMenu() };
        int current = VoiceShift.Shared.Preset;
        bool here = false;
        for (int i = 0; i < VoiceColor.All.Count; i++)
        {
            if (!take(VoiceColor.All[i]))
                continue;
            int index = i;
            bool on = i == current;
            if (on)
                here = true;
            var item = new System.Windows.Forms.ToolStripMenuItem(VoiceColor.All[i].Title) { Checked = on };
            item.Click += (_, _) => VoiceShift.Shared.ApplyPreset(index);
            group.DropDownItems.Add(item);
        }

        if (group.DropDownItems.Count == 0)
            return;
        group.Checked = here;
        _voice.DropDownItems.Add(group);
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

    /// <summary>Меню, которое умещается в экран и листается колесом.</summary>
    private sealed class WheelMenu : ToolStripDropDownMenu
    {
        private const int WM_MOUSEWHEEL = 0x020A;

        private static readonly MethodInfo? ScrollByItem = typeof(ToolStripDropDownMenu).GetMethod(
            "ScrollInternal",
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new[] { typeof(bool) },
            null);

        private static readonly PropertyInfo? NeedsScroll = typeof(ToolStripDropDownMenu).GetProperty(
            "RequiresScrollButtons",
            BindingFlags.Instance | BindingFlags.NonPublic);

        public WheelMenu()
        {
            ShowCheckMargin = true;
            ShowImageMargin = false;
        }

        protected override void OnOpening(System.ComponentModel.CancelEventArgs e)
        {
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            int below = area.Bottom - Cursor.Position.Y;
            int above = Cursor.Position.Y - area.Top;
            int room = Math.Max(below, above) - 8;
            if (room < 240)
                room = Math.Max(240, area.Height - 8);
            MaximumSize = new Size(420, Math.Min(room, area.Height - 8));
            base.OnOpening(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEWHEEL && Wheel(m.WParam))
            {
                m.Result = IntPtr.Zero;
                return;
            }

            base.WndProc(ref m);
        }

        private bool Wheel(IntPtr wparam)
        {
            if (ScrollByItem == null || NeedsScroll?.GetValue(this) is not true)
                return false;
            int delta = (short)((wparam.ToInt64() >> 16) & 0xFFFF);
            if (delta == 0)
                return false;
            bool up = delta > 0;
            int steps = Math.Clamp(Math.Abs(delta) / 40, 1, 6);
            for (int i = 0; i < steps; i++)
                ScrollByItem.Invoke(this, new object[] { up });
            return true;
        }
    }
}
