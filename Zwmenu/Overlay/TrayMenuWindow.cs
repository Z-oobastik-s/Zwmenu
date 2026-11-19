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
    readonly StackPanel _voicePage = new() { Visibility = Visibility.Collapsed, Width = 480 };
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
        Width = 332;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 900;
        FontFamily = new FontFamily("Segoe UI");
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
        _main.Children.Add(Shots());
        _main.Children.Add(Rule());
        _main.Children.Add(Row("Открыть статус", () => { CloseMenu(); StatusRequested?.Invoke(); }));
        _main.Children.Add(Row("Просмотр фото", () => { CloseMenu(); PhotoWindow.PickAndShow(); }));
        _main.Children.Add(Row("Просмотр видео", () => { CloseMenu(); VideoWindow.PickAndShow(); }));
        _main.Children.Add(Row("Музыка", () => { CloseMenu(); MusicWindow.PickAndShow(); }));
        _main.Children.Add(EdgeRow());
        _main.Children.Add(Row("Настроить два экрана", () => { CloseMenu(); EdgeShareWindow.ShowCurrent(); }));
        _main.Children.Add(Row("Шаблоны", () => { CloseMenu(); SnippetWindow.ShowCurrent(); }));
        _main.Children.Add(Row("Голос", ShowVoice));
        _main.Children.Add(Row("Настройки колеса", () => { CloseMenu(); SettingsRequested?.Invoke(); }));
        _main.Children.Add(Row("Проверить обновление", () => { CloseMenu(); AppUpdate.CheckSoon(manual: true); }));
        _main.Children.Add(Rule());
        _main.Children.Add(Row("Как это работает", () =>
        {
            var point = System.Windows.Forms.Cursor.Position;
            CloseMenu();
            GuideWindow.ShowNear(point.X, point.Y);
        }));
        _main.Children.Add(Row("Выход", () => { CloseMenu(); ExitRequested?.Invoke(); }, leave: true));

        var pages = new Grid();
        pages.Children.Add(_main);
        pages.Children.Add(_voicePage);
        Content = new Border
        {
            Background = TrayPaint.Brush(Color.FromRgb(0x10, 0x11, 0x13)),
            BorderBrush = TrayPaint.Brush(Color.FromRgb(0x3A, 0x3C, 0x40)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(8, 8, 8, 8),
            Effect = TrayPaint.Shadow(),
            Child = pages
        };
        FillVoice();
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
        UpdateLayout();
        TrayPaint.PlaceInCorner(this, _ax, _ay);
    }

    void ShowVoice()
    {
        _main.Visibility = Visibility.Collapsed;
        _voicePage.Visibility = Visibility.Visible;
        Width = 496;
        Fit();
    }

    void ShowList()
    {
        _voicePage.Visibility = Visibility.Collapsed;
        _main.Visibility = Visibility.Visible;
        Width = 332;
        Fit();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (ReferenceEquals(_open, this))
            _open = null;
        ShotPeek.ClosePeek();
        TrayDismiss.Disarm(this);
        base.OnClosed(e);
    }

    static UIElement Head(int clipPhase)
    {
        var stack = new StackPanel { Margin = new Thickness(8, 6, 8, 8) };
        stack.Children.Add(new TextBlock
        {
            Text = "Zwmenu",
            Foreground = TrayPaint.Brush(TemplateTheme.Fg),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        });
        string sub = clipPhase == 2
            ? "Идёт запись. Esc останавливает."
            : clipPhase == 1
                ? "Готовлю запись."
                : "Правый клик открыл это меню.";
        stack.Children.Add(new TextBlock
        {
            Text = sub,
            Foreground = TrayPaint.Brush(TemplateTheme.FgDim),
            FontSize = 11.5,
            Margin = new Thickness(0, 1, 0, 0)
        });
        return stack;
    }

    UIElement Shots()
    {
        var wrap = new StackPanel { Margin = new Thickness(4, 0, 4, 6) };
        wrap.Children.Add(new TextBlock
        {
            Text = "Недавние снимки",
            Foreground = TrayPaint.Brush(TemplateTheme.Fg),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(4, 0, 0, 2)
        });
        wrap.Children.Add(new TextBlock
        {
            Text = "Наведение показывает снимок. Клик копирует картинку. Alt и клик копируют текст. Правый клик открывает файл.",
            Foreground = TrayPaint.Brush(TemplateTheme.FgDim),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 0, 4, 8)
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
            Background = TrayPaint.Brush(Color.FromRgb(0x1A, 0x1B, 0x1E)),
            BorderBrush = TrayPaint.Brush(Color.FromRgb(0x2E, 0x30, 0x34)),
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
        frame.MouseLeave += (_, _) => frame.BorderBrush = TrayPaint.Brush(Color.FromRgb(0x2E, 0x30, 0x34));
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
        _voicePage.Children.Add(Row("Открыть настройки", () => { CloseMenu(); VoiceShiftWindow.ShowCurrent(); }));
        string toggle = VoiceShiftLive.Shared.Running ? "Выключить отдачу" : "Включить отдачу";
        _voicePage.Children.Add(Row(toggle, () =>
        {
            VoiceShiftLive.Shared.ToggleSaved();
            CloseMenu();
        }));
        bool hear = ScreenshotStore.Current.VoiceShiftHear;
        _voicePage.Children.Add(Row(hear ? "Не слышать себя" : "Слышать себя", () =>
        {
            VoiceShiftLive.Shared.SetHear(!ScreenshotStore.Current.VoiceShiftHear, startIfOff: true);
            CloseMenu();
        }));
        AddVoiceGroup("Девушки", color => color.Model.Length > 0);
        AddVoiceGroup("Образы", color => color.Model.Length == 0 && color.Face.On);
        AddVoiceGroup("Эффекты", color => color.Model.Length == 0 && !color.Face.On);
    }

    void AddVoiceGroup(string title, Func<VoiceColor, bool> take)
    {
        var wrap = new WrapPanel { ItemWidth = 160, ItemHeight = 34, Margin = new Thickness(0, 0, 0, 2) };
        int current = VoiceShift.Shared.Preset;
        bool any = false;
        for (int i = 0; i < VoiceColor.All.Count; i++)
        {
            if (!take(VoiceColor.All[i]))
                continue;
            any = true;
            int index = i;
            bool on = i == current;
            var idle = TrayPaint.Brush(on ? Color.FromRgb(0x1A, 0x2E, 0x2A) : Color.FromRgb(0x1C, 0x1E, 0x22));
            var hot = TrayPaint.Brush(on ? Color.FromRgb(0x24, 0x3C, 0x36) : Color.FromRgb(0x2A, 0x2C, 0x32));
            var chip = new Border
            {
                Background = idle,
                BorderBrush = TrayPaint.Brush(on ? TemplateTheme.AccentText : Color.FromRgb(0x32, 0x34, 0x38)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(3, 2, 3, 2),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                VerticalAlignment = System.Windows.VerticalAlignment.Stretch,
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = VoiceColor.All[i].Title,
                    Foreground = TrayPaint.Brush(on ? TemplateTheme.AccentText : TemplateTheme.Fg),
                    FontSize = 12.5,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            };
            chip.MouseEnter += (_, _) => chip.Background = hot;
            chip.MouseLeave += (_, _) => chip.Background = idle;
            chip.MouseLeftButtonUp += (_, _) =>
            {
                VoiceShift.Shared.ApplyPreset(index);
                CloseMenu();
            };
            wrap.Children.Add(chip);
        }

        if (!any)
            return;
        _voicePage.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = TrayPaint.Brush(TemplateTheme.FgDim),
            FontSize = 11.5,
            Margin = new Thickness(8, 8, 8, 2)
        });
        _voicePage.Children.Add(wrap);
    }

    static Border Row(string text, Action click, bool leave = false)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = TrayPaint.Brush(leave ? Color.FromRgb(0xE7, 0xB4, 0xB0) : TemplateTheme.Fg),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        var row = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 7, 8, 7),
            Cursor = Cursors.Hand,
            Child = label
        };
        row.MouseEnter += (_, _) => row.Background = TrayPaint.Brush(Color.FromRgb(0x22, 0x24, 0x28));
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => click();
        return row;
    }

    static Border Rule() => new()
    {
        Height = 1,
        Background = TrayPaint.Brush(Color.FromRgb(0x2A, 0x2C, 0x30)),
        Margin = new Thickness(8, 4, 8, 4)
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
                Background = TrayPaint.Brush(Color.FromRgb(0x10, 0x11, 0x13)),
                BorderBrush = TrayPaint.Brush(Color.FromRgb(0x3A, 0x3C, 0x40)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
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
