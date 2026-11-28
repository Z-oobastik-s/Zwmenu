using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Zwmenu.Services;

namespace Zwmenu.Overlay;

/// <summary>Меню значка: свои цвета, лента снимков и просмотр по наведению.</summary>
public sealed class TrayMenuWindow : Window
{
    public static event Action? StatusRequested;
    public static event Action? SettingsRequested;
    public static event Action? ExitRequested;

    static TrayMenuWindow? _open;
    readonly StackPanel _main = new();
    readonly StackPanel _voicePage = new()
    {
        Width = 470,
        Margin = new Thickness(0, 0, 0, 14),
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        Background = TemplateTheme.Brush(TemplateTheme.BgPanel)
    };
    readonly ScrollViewer _mainScroll;
    readonly ScrollViewer _voiceScroll;
    readonly List<VoiceChip> _chips = new();
    TextBlock? _toggleLabel;
    TextBlock? _hearLabel;
    TextBlock? _voiceStatus;
    int _voiceIntent;
    int _ax;
    int _ay;
    long _shownAt;
    bool _inside;
    bool _ready;

    TrayMenuWindow(int clipPhase)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = 348;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 900;
        FontFamily = new FontFamily(UiTheme.Current.FontUi);
        ShowActivated = true;
        MouseEnter += (_, _) => _inside = true;
        MouseLeave += (_, _) => _inside = false;
        Deactivated += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (!IsVisible || _inside || ShotPeek.PointerInside())
                return;
            if (Environment.TickCount64 - _shownAt < 250)
                return;
            CloseMenu();
        });
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                CloseMenu();
        };
        _main.Children.Add(Head(clipPhase));
        _main.Children.Add(ForgeChrome.Card(Shots(), new Thickness(8, 8, 8, 8)));
        _main.Children.Add(ForgeChrome.Section("Медиа"));
        _main.Children.Add(Row("Открыть статус", () => { CloseMenu(); StatusRequested?.Invoke(); }));
        _main.Children.Add(Row("Просмотр фото", () => { CloseMenu(); PhotoWindow.PickAndShow(); }));
        _main.Children.Add(Row("Просмотр видео", () => { CloseMenu(); VideoWindow.PickAndShow(); }));
        _main.Children.Add(Row("Музыка", () => { CloseMenu(); MusicWindow.PickAndShow(); }));
        _main.Children.Add(ForgeChrome.Section("Связь"));
        _main.Children.Add(EdgeRow());
        _main.Children.Add(Row("Настроить два экрана", () => { CloseMenu(); EdgeShareWindow.ShowCurrent(); }));
        _main.Children.Add(ForgeChrome.Section("Инструменты"));
        _main.Children.Add(Row("Шаблоны", () => { CloseMenu(); SnippetWindow.ShowCurrent(); }));
        _main.Children.Add(Row("Голос", ShowVoice));
        _main.Children.Add(Row("Настройки колеса", () => { CloseMenu(); SettingsRequested?.Invoke(); }));
        _main.Children.Add(Row("Проверить обновление", () => { CloseMenu(); AppUpdate.CheckSoon(manual: true); }));
        _main.Children.Add(ForgeChrome.Section("Справка"));
        _main.Children.Add(Row("Как это работает", () =>
        {
            var point = System.Windows.Forms.Cursor.Position;
            CloseMenu();
            GuideWindow.ShowNear(point.X, point.Y);
        }));
        _main.Children.Add(Row("Выход", () => { CloseMenu(); ExitRequested?.Invoke(); }, leave: true));

        _mainScroll = TrayPaint.HiddenScroll(_main);
        _mainScroll.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        _mainScroll.Background = Brushes.Transparent;
        _voiceScroll = TrayPaint.HiddenScroll(_voicePage);
        _voiceScroll.Visibility = Visibility.Collapsed;
        _voiceScroll.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        _voiceScroll.Background = Brushes.Transparent;
        PreviewMouseWheel += (_, e) =>
        {
            var scroll = _voiceScroll.Visibility == Visibility.Visible ? _voiceScroll : _mainScroll;
            if (scroll.ScrollableHeight <= 0)
                return;
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta * 0.45);
            e.Handled = true;
        };
        var pages = new Grid();
        pages.Children.Add(_mainScroll);
        pages.Children.Add(_voiceScroll);
        var shell = ForgeChrome.Shell(pages, new Thickness(10, 10, 10, 8));
        Content = shell;
        ForgeChrome.FadeIn(shell);
        FillVoice();
        VoiceShiftLive.Shared.Changed += OnVoiceLive;
    }

    public static void ShowAt(int x, int y, int clipPhase)
    {
        var app = Application.Current;
        if (app == null)
            return;
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(() => ShowAt(x, y, clipPhase));
            return;
        }

        GuideWindow.CloseGuide();
        CloseMenu();
        var menu = new TrayMenuWindow(clipPhase);
        _open = menu;
        menu._ax = x;
        menu._ay = y;
        menu.Left = -4000;
        menu.Top = -4000;
        menu.Show();
        menu.Fit();
        TrayIconService.HoldTip();
        menu._shownAt = Environment.TickCount64;
        menu._ready = true;
        menu.Activate();
        TrayDismiss.Arm(menu, () => menu.Holds(System.Windows.Forms.Cursor.Position), CloseMenu);
    }

    public static void CloseMenu()
    {
        ShotPeek.ClosePeek();
        var menu = _open;
        _open = null;
        if (menu == null)
            return;
        TrayDismiss.Disarm(menu);
        try { menu.Close(); } catch { /* уже закрыто */ }
        TrayIconService.ReleaseTip();
    }

    public static bool IsOpen => _open is { IsVisible: true };

    bool Holds(System.Drawing.Point cursor) =>
        _inside || ShotPeek.PointerInside() || TrayPaint.ContainsPoint(this, cursor.X, cursor.Y);

    void Fit()
    {
        double room = TrayPaint.Room(_ax, _ay);
        MaxHeight = Math.Max(220, room);
        // Оставляем место рамке Shell, иначе низ меню уезжает за край экрана.
        double inner = Math.Max(160, MaxHeight - 28);
        if (_voiceScroll.Visibility == Visibility.Visible)
        {
            _voiceScroll.MaxHeight = inner;
            _voiceScroll.Height = Math.Min(_voiceScroll.Height, inner);
        }
        else
        {
            _mainScroll.MaxHeight = inner;
            Height = double.NaN;
            SizeToContent = SizeToContent.Height;
        }

        UpdateLayout();
        TrayPaint.PlaceInCorner(this, _ax, _ay);
    }

    void ShowVoice()
    {
        FillVoice();
        _mainScroll.Visibility = Visibility.Collapsed;
        _voiceScroll.Visibility = Visibility.Visible;
        Width = 504;
        SizeToContent = SizeToContent.Manual;
        double room = TrayPaint.Room(_ax, _ay);
        MaxHeight = Math.Max(220, room);
        _voicePage.Measure(new System.Windows.Size(470, double.PositiveInfinity));
        double content = _voicePage.DesiredSize.Height;
        const double chrome = 28;
        double height = Math.Min(MaxHeight, Math.Max(260, content + chrome));
        Height = height;
        double scrollH = Math.Max(180, height - chrome);
        _voiceScroll.Height = scrollH;
        _voiceScroll.MaxHeight = scrollH;
        Fit();
        double missing = _voiceScroll.ExtentHeight - _voiceScroll.ViewportHeight;
        if (missing > 0.5 && Height + missing <= MaxHeight + 0.5)
        {
            Height += missing;
            _voiceScroll.Height += missing;
            _voiceScroll.MaxHeight = _voiceScroll.Height;
            Fit();
        }
    }

    void ShowList()
    {
        _voiceScroll.Visibility = Visibility.Collapsed;
        _voiceScroll.Height = double.NaN;
        _voiceScroll.MaxHeight = double.PositiveInfinity;
        _mainScroll.Visibility = Visibility.Visible;
        Width = 348;
        Height = double.NaN;
        SizeToContent = SizeToContent.Height;
        Fit();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (ReferenceEquals(_open, this))
            _open = null;
        ShotPeek.ClosePeek();
        TrayDismiss.Disarm(this);
        VoiceShiftLive.Shared.Changed -= OnVoiceLive;
        base.OnClosed(e);
    }

    static UIElement Head(int clipPhase)
    {
        string sub = clipPhase == 2
            ? "Идёт запись. Esc останавливает."
            : clipPhase == 1
                ? "Готовлю запись."
                : "Правый клик открыл это меню.";
        return ForgeChrome.BrandHead("Zwmenu", sub, 32);
    }

    UIElement Shots()
    {
        var wrap = new StackPanel();
        wrap.Children.Add(new TextBlock
        {
            Text = "Недавние снимки",
            Foreground = TemplateTheme.Brush(TemplateTheme.Fg),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 2)
        });
        wrap.Children.Add(new TextBlock
        {
            Text = "Наведение показывает. Клик копирует. Alt и клик копируют текст. Правый клик открывает файл.",
            Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });

        var paths = ShotHistory.Paths();
        if (paths.Count == 0)
        {
            wrap.Children.Add(new TextBlock
            {
                Text = "Пока пусто. " + ScreenshotStore.HotkeyTitle(ScreenshotStore.Current.Hotkey) + " делает первый снимок.",
                Foreground = TrayPaint.Brush(TemplateTheme.FgDim),
                FontSize = 12,
                Margin = new Thickness(4, 0, 4, 6),
                TextWrapping = TextWrapping.Wrap
            });
            return wrap;
        }

        var strip = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 2) };
        foreach (string path in paths)
            strip.Children.Add(Thumb(path));
        wrap.Children.Add(strip);
        return wrap;
    }

    Border Thumb(string path)
    {
        var image = new System.Windows.Controls.Image
        {
            Width = 54,
            Height = 40,
            Stretch = Stretch.UniformToFill,
            Source = LoadThumb(path),
            IsHitTestVisible = false
        };
        var frame = new Border
        {
            Width = 58,
            Height = 48,
            Margin = new Thickness(2, 0, 2, 0),
            CornerRadius = new CornerRadius(8),
            Background = TemplateTheme.Brush(TemplateTheme.BtnBg),
            BorderBrush = TemplateTheme.Brush(TemplateTheme.Border),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Cursor = Cursors.Hand,
            Child = new Grid
            {
                Children =
                {
                    image,
                    new Border
                    {
                        Background = TrayPaint.Brush(Color.FromArgb(170, 0, 0, 0)),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(3, 0, 3, 0),
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(3, 0, 0, 2),
                        IsHitTestVisible = false,
                        Child = new TextBlock
                        {
                            Text = Clock(path),
                            Foreground = TrayPaint.Brush(Colors.White),
                            FontSize = 9
                        }
                    }
                }
            }
        };
        frame.MouseEnter += (_, _) =>
        {
            frame.BorderBrush = TrayPaint.Brush(TemplateTheme.AccentText);
            if (_ready)
                ShotPeek.Show(path, this);
        };
        frame.MouseLeave += (_, _) => frame.BorderBrush = TemplateTheme.Brush(TemplateTheme.Border);
        frame.MouseLeftButtonUp += (_, e) =>
        {
            bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
            if (alt && ShotPeek.TryCopyText(path))
                Toast("Текст снимка в буфере");
            else if (alt && !ShotPeek.Knows(path))
            {
                CopyImage(path, toast: false);
                Toast("Текст ещё читается, в буфере пока картинка", force: true);
            }
            else
                CopyImage(path);
            e.Handled = true;
            CloseMenu();
        };
        frame.MouseRightButtonUp += (_, e) =>
        {
            Reveal(path);
            e.Handled = true;
            CloseMenu();
        };
        return frame;
    }

    UIElement EdgeRow()
    {
        bool on = false;
        try { on = EdgeShareService.Shared.Running; } catch { /* связь ещё не поднята */ }
        return Row(on ? "Выключить два экрана" : "Включить два экрана", () =>
        {
            try { EdgeShareService.Shared.Toggle(); } catch { /* окно связи покажет само */ }
            CloseMenu();
        });
    }

    void FillVoice()
    {
        _voicePage.Children.Clear();
        var head = new Grid { Margin = new Thickness(8, 4, 8, 2) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = "Голос",
                    Foreground = TrayPaint.Brush(TemplateTheme.Fg),
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold
                },
                new TextBlock
                {
                    Text = "Тембр микрофона. Нажатие сразу включает.",
                    Foreground = TrayPaint.Brush(TemplateTheme.FgDim),
                    FontSize = 12,
                    Margin = new Thickness(0, 2, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                }
            }
        });
        var back = Row("К меню", ShowList);
        back.Padding = new Thickness(8, 6, 8, 6);
        back.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        Grid.SetColumn(back, 1);
        head.Children.Add(back);
        _voicePage.Children.Add(head);
        _voicePage.Children.Add(Row("Открыть настройки", () =>
        {
            CloseMenu();
            VoiceShiftWindow.ShowCurrent();
        }));
        var toggle = Row("", () =>
        {
            if (VoiceShiftLive.Shared.Busy)
            {
                if (_voiceStatus != null)
                    _voiceStatus.Text = "Подожди секунду.";
                return;
            }

            _voiceIntent = VoiceShiftLive.Shared.Running ? 2 : 1;
            VoiceShiftLive.Shared.ToggleSaved();
            PaintVoice();
        });
        _toggleLabel = ForgeChrome.RowLabel(toggle)
                       ?? throw new InvalidOperationException("Нет подписи у строки голоса.");
        _voicePage.Children.Add(toggle);
        var hear = Row("", () =>
        {
            bool turnOn = !ScreenshotStore.Current.VoiceShiftHear;
            bool starting = turnOn && !VoiceShiftLive.Shared.Running && !VoiceShiftLive.Shared.Busy;
            VoiceShiftLive.Shared.SetHear(turnOn, startIfOff: true);
            if (starting)
                _voiceIntent = 1;
            PaintVoice();
        });
        _hearLabel = ForgeChrome.RowLabel(hear)
                     ?? throw new InvalidOperationException("Нет подписи у строки прослушки.");
        _voicePage.Children.Add(hear);
        _voiceStatus = new TextBlock
        {
            Foreground = TrayPaint.Brush(TemplateTheme.FgDim),
            FontSize = 12,
            Margin = new Thickness(8, 2, 8, 6),
            TextWrapping = TextWrapping.Wrap
        };
        _voicePage.Children.Add(_voiceStatus);
        VoiceColor.Refresh();
        KeepMila();
        _chips.Clear();
        AddVoiceGroup("Девушки", color => color.Model == "female_voice_hfg_new.onnx");
        AddVoiceGroup("Образы", color => color.Model.Length == 0 && color.Face.On);
        AddVoiceGroup("Эффекты", color => color.Model.Length == 0 && !color.Face.On);
        PaintVoice();
    }

    static void KeepMila()
    {
        int preset = VoiceShift.Shared.Preset;
        if (preset < 0 || preset >= VoiceColor.All.Count)
            return;
        string model = VoiceColor.All[preset].Model ?? "";
        if (model is not ("female_voice_old_hq.onnx" or "female_voice_old.onnx"))
            return;
        for (int i = 0; i < VoiceColor.All.Count; i++)
        {
            if (VoiceColor.All[i].Model == "female_voice_hfg_new.onnx")
            {
                VoiceShift.Shared.ApplyPreset(i);
                return;
            }
        }
    }

    void OnVoiceLive()
    {
        if (!Dispatcher.CheckAccess())
        {
            try { Dispatcher.BeginInvoke(OnVoiceLive); } catch { /* меню уже закрыто */ }
            return;
        }

        if (_voiceScroll.Visibility != Visibility.Visible)
            return;
        if (!VoiceShiftLive.Shared.Busy)
            _voiceIntent = 0;
        PaintVoice();
    }

    void PaintVoice()
    {
        bool live = VoiceShiftLive.Shared.Running;
        if (_toggleLabel != null)
        {
            _toggleLabel.Text = _voiceIntent == 1
                ? "Включаю отдачу"
                : _voiceIntent == 2
                    ? "Выключаю отдачу"
                    : live ? "Выключить отдачу" : "Включить отдачу";
            _toggleLabel.Foreground = TrayPaint.Brush(live || _voiceIntent != 0 ? TemplateTheme.AccentText : TemplateTheme.Fg);
        }

        if (_hearLabel != null)
            _hearLabel.Text = ScreenshotStore.Current.VoiceShiftHear ? "Не слышать себя" : "Слышать себя";
        if (_voiceStatus != null)
        {
            string note = _voiceIntent == 1
                ? "Включаю. Сейчас скажу, вышло или нет."
                : _voiceIntent == 2
                    ? "Выключаю. Микрофон сейчас вернётся."
                    : VoiceShiftLive.Shared.Note;
            bool bad = note.StartsWith("Не ", StringComparison.Ordinal) || note == SetupOffer.CableNote;
            _voiceStatus.Text = note;
            _voiceStatus.Foreground = TrayPaint.Brush(bad
                ? Color.FromRgb(0xE7, 0xB4, 0xB0)
                : live ? TemplateTheme.AccentText : TemplateTheme.FgDim);
        }
        int current = VoiceShift.Shared.Preset;
        foreach (var chip in _chips)
        {
            bool on = chip.Index == current;
            chip.Idle = TrayPaint.Brush(on ? TemplateTheme.AccentSoft : TemplateTheme.BtnBg);
            chip.Hot = TrayPaint.Brush(on ? TemplateTheme.Accent : TemplateTheme.BtnHover);
            chip.Box.BorderBrush = TrayPaint.Brush(on ? TemplateTheme.AccentText : TemplateTheme.Border);
            chip.Label.Foreground = TrayPaint.Brush(on ? TemplateTheme.AccentText : TemplateTheme.Fg);
            chip.Box.Background = chip.Box.IsMouseOver ? chip.Hot : chip.Idle;
        }
    }

    void AddVoiceGroup(string title, Func<VoiceColor, bool> take)
    {
        var wrap = new WrapPanel { ItemWidth = 156, ItemHeight = 36, Margin = new Thickness(0, 0, 0, 4) };
        bool any = false;
        for (int i = 0; i < VoiceColor.All.Count; i++)
        {
            if (!take(VoiceColor.All[i]))
                continue;
            any = true;
            var chip = new VoiceChip
            {
                Index = i,
                Label = new TextBlock
                {
                    Text = VoiceColor.All[i].Title,
                    FontSize = 12.5,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            };
            chip.Box = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(3, 2, 3, 2),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                VerticalAlignment = System.Windows.VerticalAlignment.Stretch,
                Cursor = Cursors.Hand,
                Child = chip.Label
            };
            var held = chip;
            held.Box.MouseEnter += (_, _) => held.Box.Background = held.Hot;
            held.Box.MouseLeave += (_, _) => held.Box.Background = held.Idle;
            held.Box.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                VoiceShift.Shared.ApplyPreset(held.Index);
                PaintVoice();
            };
            _chips.Add(chip);
            wrap.Children.Add(chip.Box);
        }

        if (!any)
            return;
        if (title.Length > 0)
        {
            _voicePage.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = TrayPaint.Brush(TemplateTheme.FgDim),
                FontSize = 11.5,
                Margin = new Thickness(8, 4, 8, 1)
            });
        }

        _voicePage.Children.Add(wrap);
    }

    sealed class VoiceChip
    {
        public Border Box = null!;
        public TextBlock Label = null!;
        public int Index;
        public Brush Idle = Brushes.Transparent;
        public Brush Hot = Brushes.Transparent;
    }

    static Border Row(string text, Action click, bool leave = false) =>
        ForgeChrome.MenuRow(text, click, danger: leave);

    static Border Rule() => new()
    {
        Height = 1,
        Background = TemplateTheme.Brush(TemplateTheme.Border),
        Margin = new Thickness(4, 6, 4, 6),
        Opacity = 0.7
    };

    static ImageSource? LoadThumb(string path)
    {
        try
        {
            string thumb = ShotHistory.ThumbPath(path);
            string file = File.Exists(thumb) ? thumb : path;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = 120;
            bmp.UriSource = new Uri(file);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    static string Clock(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (name.Length >= 15 && name[8] == '-')
            return name.Substring(9, 2) + ":" + name.Substring(11, 2);
        return "";
    }

    public static void CopyImage(string path, bool toast = true)
    {
        try
        {
            using var bmp = new System.Drawing.Bitmap(path);
            ScreenCapture.CopyToClipboard(bmp);
            if (toast)
                Toast("Снимок в буфере");
        }
        catch
        {
            Toast("Не удалось скопировать снимок");
        }
    }

    static void Reveal(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"")
            {
                UseShellExecute = true
            })?.Dispose();
        }
        catch
        {
            Toast("Файл снимка не открылся");
        }
    }

    public static void Toast(string text, bool force = false)
    {
        try
        {
            if (!force && !ScreenshotStore.Current.ShowNotifications)
                return;
        }
        catch
        {
            if (!force)
                return;
        }

        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        ScreenshotToast.Show(text, screen.Bounds, 2.2);
    }
}

/// <summary>Крупный кадр рядом с меню. Текст дочитывается, если на снимке он есть.</summary>
static class ShotPeek
{
    static PeekWindow? _open;
    static readonly Dictionary<string, string> Notes = new(StringComparer.OrdinalIgnoreCase);

    public static void Show(string path, Window menu)
    {
        var app = Application.Current;
        if (app == null)
            return;
        if (_open == null || !_open.IsVisible)
        {
            _open = new PeekWindow();
            _open.Show();
        }

        _open.Bind(path);
        _open.UpdateLayout();
        double left = menu.Left - _open.ActualWidth - 10;
        if (left < 8)
            left = menu.Left + menu.ActualWidth + 10;
        _open.Left = left;
        _open.Top = menu.Top;
        if (Notes.TryGetValue(path, out string? ready))
            _open.SetNote(ready);
        else
        {
            _open.SetNote("Смотрю, есть ли текст…");
            string file = path;
            _ = Task.Run(() =>
            {
                string note = ReadNote(file);
                Notes[file] = note;
                app.Dispatcher.BeginInvoke(() =>
                {
                    if (_open != null && _open.IsFor(file))
                        _open.SetNote(note);
                });
            });
        }
    }

    public static void ClosePeek()
    {
        var window = _open;
        _open = null;
        if (window == null)
            return;
        try { window.Close(); } catch { /* уже закрыто */ }
    }

    public static bool PointerInside() => _open?.Inside == true;

    public static bool Knows(string path) => Notes.ContainsKey(path);

    public static bool TryCopyText(string path)
    {
        if (!Notes.TryGetValue(path, out string? note) || !HasWords(note))
            return false;
        try
        {
            System.Windows.Clipboard.SetText(note);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static string ReadNote(string path)
    {
        try
        {
            byte[]? pixels = Sheet(path, out int width, out int height);
            if (pixels == null)
                return "";
            string text = ScreenText.ReadAsync(pixels, width, height).GetAwaiter().GetResult();
            return Compact(text);
        }
        catch
        {
            return "";
        }
    }

    static string Compact(string text)
    {
        var lines = text.Replace('\r', '\n').Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string joined = string.Join("\n", lines);
        return joined.Length > 900 ? joined[..900] + "…" : joined;
    }

    static bool HasWords(string text)
    {
        int letters = 0;
        foreach (char c in text)
        {
            if (char.IsLetter(c))
                letters++;
        }

        return letters >= 8;
    }

    static byte[]? Sheet(string path, out int width, out int height)
    {
        width = height = 0;
        using var src = SkiaSharp.SKBitmap.Decode(path);
        if (src == null)
            return null;
        const int max = 720;
        SkiaSharp.SKBitmap use = src;
        SkiaSharp.SKBitmap? scaled = null;
        try
        {
            if (src.Width > max || src.ColorType != SkiaSharp.SKColorType.Bgra8888)
            {
                int w = src.Width > max ? max : src.Width;
                int h = Math.Max(1, src.Height * w / src.Width);
                scaled = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(w, h, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul));
                src.ScalePixels(scaled, SkiaSharp.SKFilterQuality.Medium);
                use = scaled;
            }

            width = use.Width;
            height = use.Height;
            int packed = width * 4;
            var pixels = new byte[packed * height];
            int stride = use.RowBytes;
            IntPtr scan = use.GetPixels();
            for (int y = 0; y < height; y++)
                System.Runtime.InteropServices.Marshal.Copy(scan + y * stride, pixels, y * packed, packed);
            return pixels;
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    sealed class PeekWindow : Window
    {
        readonly System.Windows.Controls.Image _image = new() { Stretch = Stretch.Uniform, MaxWidth = 420, MaxHeight = 280 };
        readonly TextBlock _note = new()
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = TrayPaint.Brush(Color.FromRgb(0xE6, 0xE6, 0xE4)),
            FontSize = 13,
            LineHeight = 18,
            Margin = new Thickness(0, 10, 0, 0)
        };
        readonly Border _copyText;
        string _path = "";
        public bool Inside { get; private set; }

        public PeekWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            FontFamily = new FontFamily("Segoe UI");
            MouseEnter += (_, _) => Inside = true;
            MouseLeave += (_, _) => Inside = false;

            _copyText = TemplateTheme.Pill("Копировать текст", () =>
            {
                if (TryCopyText(_path))
                    TrayMenuWindow.Toast("Текст снимка в буфере");
            }, accent: true);
            _copyText.Visibility = Visibility.Collapsed;
            _copyText.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            _copyText.Margin = new Thickness(0, 10, 0, 0);

            var stack = new StackPanel { Width = 420 };
            stack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10),
                ClipToBounds = true,
                Background = TrayPaint.Brush(Color.FromRgb(0x08, 0x08, 0x09)),
                Child = _image
            });
            stack.Children.Add(_note);
            stack.Children.Add(_copyText);
            Content = new Border
            {
                Background = TemplateTheme.Brush(TemplateTheme.BgPanel),
                BorderBrush = TemplateTheme.Brush(TemplateTheme.Border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(UiTheme.Current.CornerPanel),
                Padding = new Thickness(12),
                Effect = TrayPaint.Shadow(),
                Child = stack
            };
        }

        public void Bind(string path)
        {
            _path = path;
            _image.Source = Load(path);
            _copyText.Visibility = Visibility.Collapsed;
        }

        public bool IsFor(string path) => path.Equals(_path, StringComparison.OrdinalIgnoreCase);

        public void SetNote(string note)
        {
            if (string.IsNullOrWhiteSpace(note))
            {
                _note.Text = "Картинка. Текста на ней почти нет.";
                _copyText.Visibility = Visibility.Collapsed;
                return;
            }

            if (note == "Смотрю, есть ли текст…")
            {
                _note.Text = note;
                _copyText.Visibility = Visibility.Collapsed;
                return;
            }

            _note.Text = note;
            _copyText.Visibility = Visibility.Visible;
        }

        static ImageSource? Load(string path)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.DecodePixelWidth = 840;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }
    }
}
