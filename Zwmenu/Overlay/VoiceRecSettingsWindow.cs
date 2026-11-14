using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Zwmenu.Services;
using WpfHAlign = System.Windows.HorizontalAlignment;
using WpfVAlign = System.Windows.VerticalAlignment;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace Zwmenu.Overlay;

/// <summary>
/// Настройки диктофона: компактно, без скролла, живой превью микрофона, в пределах экрана.
/// </summary>
public sealed class VoiceRecSettingsWindow : Window
{
    private static VoiceRecSettingsWindow? _current;
    private bool _suspend;
    private bool _ownedMetering;
    private Window? _anchor;
    private long _liveFxTick;

    private readonly Dictionary<string, CheckBox> _checks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Slider Slider, TextBlock Value)> _sliders = new(StringComparer.Ordinal);
    private ComboBox _format = null!;
    private ComboBox _mic = null!;
    private TextBox _folder = null!;
    private Button _listenBtn = null!;
    private CheckBox _rawBox = null!;
    private TextBlock _previewHint = null!;
    private TextBlock _clipHint = null!;
    private Border _levelTrack = null!;
    private System.Windows.Shapes.Rectangle _levelFill = null!;
    private readonly DispatcherTimer _uiTimer;

    private VoiceRecService Svc => VoiceRecService.Shared;

    public static void ShowNear(Window? owner)
    {
        var app = Application.Current;
        if (app == null)
            return;

        void Go()
        {
            if (_current != null)
            {
                if (_current.WindowState == WindowState.Minimized)
                    _current.WindowState = WindowState.Normal;
                _current.Activate();
                _current.PlaceSmart();
                return;
            }

            var w = new VoiceRecSettingsWindow { _anchor = owner };
            _current = w;
            if (owner != null)
                w.Owner = owner;

            w.Closed += (_, _) =>
            {
                if (ReferenceEquals(_current, w))
                    _current = null;
            };
            w.Show();
            w.PlaceSmart();
            w.Activate();
        }

        if (app.Dispatcher.CheckAccess())
            Go();
        else
            app.Dispatcher.Invoke(Go);
    }

    private VoiceRecSettingsWindow()
    {
        Title = "Настройки диктофона";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Content = new Grid
        {
            Margin = new Thickness(10),
            Children = { BuildCard() }
        };

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _uiTimer.Tick += (_, _) => RefreshPreviewUi();

        Load();
        KeyDown += OnKeyDown;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlaceSmart();
        Dispatcher.BeginInvoke(new Action(PlaceSmart), DispatcherPriority.Loaded);
        try
        {
            Svc.StartMetering();
            _ownedMetering = true;
            _uiTimer.Start();
            RefreshPreviewUi();
        }
        catch { /* ignore */ }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _uiTimer.Stop();
        if (_ownedMetering)
        {
            _ownedMetering = false;
            try { Svc.StopMetering(); } catch { /* ignore */ }
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.M)
        {
            ToggleListen();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Ставит окно рядом с панелью записи (выше), иначе по центру монитора.
    /// Всегда в пределах WorkingArea.
    /// </summary>
    private void PlaceSmart()
    {
        UpdateLayout();
        double w = ActualWidth > 1 ? ActualWidth : Width;
        double h = ActualHeight > 1 ? ActualHeight : 520;
        var tip = System.Windows.Forms.Cursor.Position;

        double preferLeft;
        double preferTop;

        if (_anchor != null && _anchor.IsVisible)
        {
            preferLeft = _anchor.Left + (_anchor.Width - w) / 2;
            preferTop = _anchor.Top - h - 12;
            var work = System.Windows.Forms.Screen.FromPoint(tip).WorkingArea;
            Point tl = RadialLayout.DeviceToDip(this, work.Left, work.Top);
            Point br = RadialLayout.DeviceToDip(this, work.Right, work.Bottom);
            // Если над панелью не влезает — под ней; если и так нет — центр рабочей области.
            if (preferTop < tl.Y + 10)
                preferTop = _anchor.Top + Math.Max(_anchor.ActualHeight, _anchor.Height) + 12;
            if (preferTop + h > br.Y - 10)
            {
                Point center = RadialLayout.WorkAreaCenterDip(this, tip);
                preferLeft = center.X - w / 2;
                preferTop = center.Y - h / 2;
            }
        }
        else
        {
            Point center = RadialLayout.WorkAreaCenterDip(this, tip);
            preferLeft = center.X - w / 2;
            preferTop = center.Y - h / 2;
        }

        RadialLayout.PlaceInWorkArea(this, preferLeft, preferTop, w, h, tip);
    }

    private Border BuildCard()
    {
        var title = new TextBlock
        {
            Text = "Настройки диктофона",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = Fg(),
            VerticalAlignment = WpfVAlign.Center
        };
        var close = IconBtn("\uE711", Close, "Закрыть");
        var head = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        head.Children.Add(title);
        head.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 1)
            {
                try { DragMove(); } catch { /* ignore */ }
            }
        };

        var preview = BuildPreview();

        var fxGrid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 2, 0, 4) };
        AddCheck(fxGrid, "enhance", "Обработка", "Мастер. Выкл: почти сырой звук, только усиление.");
        AddCheck(fxGrid, "highPass", "Срез гула", "Убирает гул вентилятора, стола, низкий фон.");
        AddCheck(fxGrid, "gate", "Шумовой гейт", "Глушит тишину между фразами, меньше шороха.");
        AddCheck(fxGrid, "compress", "Компрессия", "Выравнивает громкость: шёпот и крик ближе друг к другу.");
        AddCheck(fxGrid, "presence", "Ясность", "Поднимает середину, речь читается чётче.");
        AddCheck(fxGrid, "warmth", "Теплота", "Чуть добавляет низ, голос мягче.");
        AddCheck(fxGrid, "deEsser", "Антишипение", "Прибирает резкие «с» и «ш».");
        AddCheck(fxGrid, "limiter", "Лимитер", "Не даёт пикам уйти в хрип/клиппинг.");
        AddCheck(fxGrid, "agc", "Автоуровень", "Сам тянет громкость во время записи. Осторожно с шумом.");
        AddCheck(fxGrid, "normalize", "Нормализация", "После записи поднимает пик до комфортной громкости.");
        AddCheck(fxGrid, "trim", "Обрезка тишины", "Срезает пустоту в начале и конце файла.");
        AddCheck(fxGrid, "copyPath", "Путь в буфер", "После сохранения копирует полный путь к файлу.");

        var sliders = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        AddSlider(sliders, "gain", "Усиление", -12, 18, 1, "дБ",
            "Громкость входа до эффектов. Если тихо говоришь: +3…+9.");
        AddSlider(sliders, "hpHz", "Срез гула", 40, 160, 5, "Гц",
            "Выше: агрессивнее режет бас. Для речи обычно около 80 Гц.");
        AddSlider(sliders, "gateDb", "Порог гейта", -70, -30, 1, "дБ",
            "Ниже: гейт чувствительнее, сильнее глушит фон.");
        AddSlider(sliders, "comp", "Компрессия", 0, 100, 5, "%",
            "Сильнее: ровнее речь, но может «дышать».");
        AddSlider(sliders, "presDb", "Ясность", 0, 8, 0.2, "дБ",
            "Слишком много: металлический, резкий голос.");
        AddSlider(sliders, "warmDb", "Теплота", 0, 6, 0.2, "дБ",
            "Слишком много: гулко и мутно.");

        var fine = new UniformGrid { Columns = 2, Margin = new Thickness(0, 2, 0, 0) };
        AddSliderCell(fine, "fade", "Фейд", 0, 50, 2, "мс",
            "Мягкий вход и выход файла. Обычно хватает 8..16 мс.");
        AddSliderCell(fine, "mon", "Монитор", 10, 100, 5, "%",
            "Громкость «слушать себя». В колонках лучше ниже.");
        AddSliderCell(fine, "pre", "Старт", 0, 500, 20, "мс",
            "Не писать первые мс после «Запись» (клик мыши).");
        AddSliderCell(fine, "max", "Лимит", 1, 120, 1, "мин",
            "Автостоп, если запись слишком длинная.");

        _format = new ComboBox
        {
            Width = 168,
            HorizontalAlignment = WpfHAlign.Left,
            VerticalAlignment = WpfVAlign.Center,
            DisplayMemberPath = nameof(FormatPick.Title)
        };
        _format.ItemsSource = new FormatPick[]
        {
            new("mp3", "MP3"),
            new("m4a", "M4A (AAC)"),
            new("ogg", "OGG (Vorbis)"),
            new("flac", "FLAC"),
            new("wav16", "WAV 16 бит"),
            new("wav24", "WAV 24 бит")
        };
        Tip(_format, "MP3, M4A, OGG компактные. FLAC без потерь. WAV сырой, больше файл.");
        _format.SelectionChanged += (_, _) => { if (!_suspend) Save(); };

        _folder = new TextBox
        {
            VerticalContentAlignment = WpfVAlign.Center,
            Height = 28,
            Padding = new Thickness(8, 2, 8, 2)
        };
        Tip(_folder, "Куда класть файлы. Пусто: рабочий стол.");
        _folder.LostFocus += (_, _) => { if (!_suspend) Save(); };

        var browse = Pill("…", OnBrowse, "Выбрать папку");
        var folderGrid = new Grid();
        folderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_folder, 0);
        Grid.SetColumn(browse, 1);
        browse.Margin = new Thickness(8, 0, 0, 0);
        folderGrid.Children.Add(_folder);
        folderGrid.Children.Add(browse);

        var fileRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = true };
        DockPanel.SetDock(_format, Dock.Left);
        _format.Margin = new Thickness(0, 0, 12, 0);
        fileRow.Children.Add(_format);
        fileRow.Children.Add(folderGrid);

        var reset = Pill("Сбросить", OnReset, "Вернуть заводские значения диктофона");
        var done = Pill("Готово", Close, "Сохранить и закрыть", accent: true);
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        DockPanel.SetDock(done, Dock.Right);
        DockPanel.SetDock(reset, Dock.Left);
        footer.Children.Add(done);
        footer.Children.Add(reset);

        var body = new StackPanel
        {
            Children =
            {
                head,
                preview,
                Section("Эффекты", fxGrid),
                Section("Тон", sliders, fine),
                Section("Файл", fileRow),
                footer
            }
        };

        return new Border
        {
            Background = BrushA(0xF4, 0x12, 0x14, 0x18),
            BorderBrush = BrushA(0x55, 0x3A, 0x4A, 0x52),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(20, 16, 20, 16),
            Child = body
        };
    }

    private Border BuildPreview()
    {
        _previewHint = new TextBlock
        {
            Text = "Говори в микрофон, крути параметры, слушай разницу",
            FontSize = 12,
            Foreground = BrushA(0xAA, 0xC8, 0xD0, 0xC4),
            VerticalAlignment = WpfVAlign.Center
        };
        _clipHint = new TextBlock
        {
            Text = "",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(0xE8, 0x6A, 0x4A),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = WpfVAlign.Center
        };

        _levelTrack = new Border
        {
            Height = 10,
            CornerRadius = new CornerRadius(5),
            Background = BrushA(0x40, 0xFF, 0xFF, 0xFF),
            Margin = new Thickness(0, 10, 0, 10),
            ClipToBounds = true
        };
        _levelFill = new System.Windows.Shapes.Rectangle
        {
            Height = 10,
            Width = 0,
            RadiusX = 5,
            RadiusY = 5,
            Fill = new LinearGradientBrush(
                Color.FromRgb(0x3E, 0xC8, 0x9A),
                Color.FromRgb(0xE8, 0xC0, 0x4A),
                0),
            HorizontalAlignment = WpfHAlign.Left
        };
        _levelTrack.Child = _levelFill;

        _listenBtn = Pill("Слушать себя", ToggleListen, "Слышать себя в наушниках (M). Лучше не на колонках.");
        _rawBox = new CheckBox
        {
            Content = "Сырой вход",
            Margin = new Thickness(10, 0, 0, 0),
            Foreground = Fg(),
            FontSize = 12.5,
            VerticalAlignment = WpfVAlign.Center,
            VerticalContentAlignment = WpfVAlign.Center
        };
        Tip(_rawBox, "Вкл: слышишь микрофон без эффектов. Выкл: как будет в записи.");
        _rawBox.Click += (_, _) =>
        {
            if (_suspend) return;
            Svc.MonitorBypass = _rawBox.IsChecked == true;
        };

        _mic = new ComboBox
        {
            MinWidth = 180,
            HorizontalAlignment = WpfHAlign.Stretch,
            VerticalAlignment = WpfVAlign.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        Tip(_mic, "Устройство захвата. То же, что для диктовки.");
        _mic.SelectionChanged += OnMicChanged;

        var top = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_clipHint, Dock.Right);
        top.Children.Add(_clipHint);
        top.Children.Add(_previewHint);

        var controls = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_listenBtn, Dock.Left);
        DockPanel.SetDock(_rawBox, Dock.Left);
        controls.Children.Add(_listenBtn);
        controls.Children.Add(_rawBox);
        controls.Children.Add(_mic);

        return new Border
        {
            Background = BrushA(0x28, 0xFF, 0xFF, 0xFF),
            BorderBrush = BrushA(0x28, 0xFF, 0xFF, 0xFF),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 6),
            Child = new StackPanel
            {
                Children =
                {
                    top,
                    _levelTrack,
                    controls
                }
            }
        };
    }

    private void FillMicCombo()
    {
        var items = new List<MicPick> { new(-1, "Микрофон: по умолчанию") };
        try
        {
            int n = NAudio.Wave.WaveIn.DeviceCount;
            for (int i = 0; i < n; i++)
            {
                string name;
                try { name = NAudio.Wave.WaveIn.GetCapabilities(i).ProductName; }
                catch { name = "Микрофон " + i; }
                if (string.IsNullOrWhiteSpace(name))
                    name = "Микрофон " + i;
                items.Add(new MicPick(i, TrimMicName(name)));
            }
        }
        catch { /* ignore */ }

        _mic.DisplayMemberPath = nameof(MicPick.Title);
        _mic.ItemsSource = items;
        int want = ScreenshotStore.Current.DictationMicDevice;
        int idx = items.FindIndex(m => m.Index == want);
        _mic.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private static string TrimMicName(string name)
    {
        name = name.Trim();
        return name.Length <= 42 ? name : name[..39] + "…";
    }

    private void OnMicChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspend) return;
        if (_mic.SelectedItem is not MicPick pick)
            return;
        var s = ScreenshotStore.Current;
        if (s.DictationMicDevice == pick.Index)
            return;
        s.DictationMicDevice = pick.Index;
        ScreenshotStore.Save(s);
        Svc.RestartMicDevice();
    }

    private void ToggleListen()
    {
        Svc.ToggleMonitor();
        RefreshPreviewUi();
    }

    private void RefreshPreviewUi()
    {
        double maxW = Math.Max(40, _levelTrack.ActualWidth);
        if (maxW < 40 || double.IsNaN(maxW))
            maxW = 500;
        float lvl = Math.Max(Svc.LevelPeak, Svc.LevelRms * 1.35f);
        _levelFill.Width = Math.Clamp(lvl, 0, 1) * maxW;
        _levelFill.Fill = Svc.IsClipping
            ? Brush(0xE8, 0x5A, 0x4A)
            : new LinearGradientBrush(
                Color.FromRgb(0x3E, 0xC8, 0x9A),
                Color.FromRgb(0xE8, 0xC0, 0x4A),
                0);

        _clipHint.Text = Svc.IsClipping ? "Клип" : "";
        _listenBtn.Content = Svc.IsMonitoring ? "Не слушать" : "Слушать себя";
        _listenBtn.Background = Svc.IsMonitoring
            ? BrushA(0xFF, 0x2E, 0x6B, 0x5A)
            : BrushA(0x33, 0xFF, 0xFF, 0xFF);

        if (Svc.IsMonitoring)
            _previewHint.Text = Svc.MonitorBypass
                ? "Слышишь сырой вход. Сними «Сырой», чтобы слышать эффекты"
                : "Слышишь с эффектами. Меняй ползунки, разница сразу";
        else
            _previewHint.Text = "Уровень живой. Нажми «Слушать себя», чтобы слышать правки";
    }

    private void AddCheck(UniformGrid grid, string key, string title, string tip)
    {
        var box = new CheckBox
        {
            Content = title,
            Margin = new Thickness(0, 4, 10, 4),
            Foreground = Fg(),
            FontSize = 13,
            VerticalContentAlignment = WpfVAlign.Center
        };
        Tip(box, tip);
        box.Click += (_, _) =>
        {
            if (_suspend) return;
            Save();
        };
        _checks[key] = box;
        grid.Children.Add(box);
    }

    private void AddSlider(Panel host, string key, string title, double min, double max, double tick, string unit, string tip)
    {
        var value = new TextBlock
        {
            MinWidth = 54,
            FontSize = 12.5,
            Foreground = BrushA(0xB8, 0xF5, 0xF0, 0xE8),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = WpfVAlign.Center
        };
        var slider = MakeSlider(min, max, tick);
        Tip(slider, tip);
        _sliders[key] = (slider, value);

        var cap = new TextBlock
        {
            Text = title,
            Width = 108,
            FontSize = 13,
            Foreground = Fg(),
            Opacity = 0.92,
            VerticalAlignment = WpfVAlign.Center
        };
        Tip(cap, tip);

        var unitTb = new TextBlock
        {
            Text = unit,
            Width = 28,
            FontSize = 11.5,
            Opacity = 0.55,
            Foreground = Fg(),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = WpfVAlign.Center
        };

        var row = new DockPanel { Margin = new Thickness(0, 5, 0, 0), LastChildFill = true };
        DockPanel.SetDock(cap, Dock.Left);
        DockPanel.SetDock(unitTb, Dock.Right);
        DockPanel.SetDock(value, Dock.Right);
        row.Children.Add(cap);
        row.Children.Add(unitTb);
        row.Children.Add(value);
        row.Children.Add(slider);
        host.Children.Add(row);
    }

    private void AddSliderCell(UniformGrid grid, string key, string title, double min, double max, double tick, string unit, string tip)
    {
        var value = new TextBlock
        {
            MinWidth = 40,
            FontSize = 12.5,
            Foreground = BrushA(0xB8, 0xF5, 0xF0, 0xE8),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = WpfVAlign.Center
        };
        var slider = MakeSlider(min, max, tick);
        Tip(slider, tip);
        _sliders[key] = (slider, value);

        var cap = new TextBlock
        {
            Text = title,
            Width = 64,
            FontSize = 12.5,
            Foreground = Fg(),
            VerticalAlignment = WpfVAlign.Center
        };
        Tip(cap, tip);

        var unitTb = new TextBlock
        {
            Text = unit,
            FontSize = 11,
            Opacity = 0.5,
            Foreground = Fg(),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = WpfVAlign.Center
        };

        var row = new DockPanel { Margin = new Thickness(0, 5, 12, 0), LastChildFill = true };
        DockPanel.SetDock(cap, Dock.Left);
        DockPanel.SetDock(unitTb, Dock.Right);
        DockPanel.SetDock(value, Dock.Right);
        row.Children.Add(cap);
        row.Children.Add(unitTb);
        row.Children.Add(value);
        row.Children.Add(slider);
        grid.Children.Add(row);
    }

    private Slider MakeSlider(double min, double max, double tick)
    {
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            TickFrequency = tick,
            IsSnapToTickEnabled = true,
            VerticalAlignment = WpfVAlign.Center,
            Margin = new Thickness(8, 0, 8, 0)
        };
        slider.ValueChanged += (_, _) =>
        {
            if (_suspend) return;
            RefreshLabels();
            // Пока тянут ползунок: живой превью без записи на диск каждый тик.
            PersistLiveOnly();
        };
        slider.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (_suspend) return;
            _liveFxTick = 0; // финальный превью без throttle
            Save();
        };
        slider.LostFocus += (_, _) =>
        {
            if (_suspend) return;
            Save();
        };
        return slider;
    }

    private void PersistLiveOnly()
    {
        // В Current сразу; DSP не чаще ~12 раз/с, иначе лаги на пересоздании цепочки.
        ApplyToStore(ScreenshotStore.Current);
        long now = Environment.TickCount64;
        if (now - _liveFxTick < 80)
            return;
        _liveFxTick = now;
        Svc.RefreshLiveSettings();
    }

    private void Load()
    {
        _suspend = true;
        var s = ScreenshotStore.Current;
        SetCheck("enhance", s.VoiceRecEnhance);
        SetCheck("highPass", s.VoiceRecHighPass);
        SetCheck("gate", s.VoiceRecGate);
        SetCheck("compress", s.VoiceRecCompress);
        SetCheck("presence", s.VoiceRecPresence);
        SetCheck("warmth", s.VoiceRecWarmth);
        SetCheck("deEsser", s.VoiceRecDeEsser);
        SetCheck("limiter", s.VoiceRecLimiter);
        SetCheck("agc", s.VoiceRecAgc);
        SetCheck("normalize", s.VoiceRecNormalize);
        SetCheck("trim", s.VoiceRecTrimSilence);
        SetCheck("copyPath", s.VoiceRecCopyPath);

        SetSlider("gain", s.VoiceRecInputGainDb);
        SetSlider("hpHz", s.VoiceRecHighPassHz);
        SetSlider("gateDb", s.VoiceRecGateDb);
        SetSlider("comp", s.VoiceRecCompressStrength);
        SetSlider("presDb", s.VoiceRecPresenceDb);
        SetSlider("warmDb", s.VoiceRecWarmthDb);
        SetSlider("fade", s.VoiceRecFadeMs);
        SetSlider("mon", s.VoiceRecMonitorVolume);
        SetSlider("pre", s.VoiceRecPreRollMs);
        SetSlider("max", s.VoiceRecMaxMinutes);

        string fmt = ScreenshotStore.NormalizeVoiceRecFormat(s.VoiceRecFormat, s.VoiceRecBitDepth);
        if (_format.ItemsSource is IEnumerable<FormatPick> picks)
        {
            var list = picks.ToList();
            int idx = list.FindIndex(p => p.Id == fmt);
            _format.SelectedIndex = idx >= 0 ? idx : 0;
        }

        _folder.Text = s.VoiceRecFolder ?? "";
        _rawBox.IsChecked = Svc.MonitorBypass;
        FillMicCombo();
        RefreshLabels();
        _suspend = false;
        Svc.RefreshLiveSettings();
    }

    private void Save()
    {
        var s = ScreenshotStore.Current;
        ApplyToStore(s);
        ScreenshotStore.Save(s);
        Svc.RefreshLiveSettings();
    }

    private void ApplyToStore(ScreenshotSettings s)
    {
        s.VoiceRecEnhance = On("enhance");
        s.VoiceRecHighPass = On("highPass");
        s.VoiceRecGate = On("gate");
        s.VoiceRecCompress = On("compress");
        s.VoiceRecPresence = On("presence");
        s.VoiceRecWarmth = On("warmth");
        s.VoiceRecDeEsser = On("deEsser");
        s.VoiceRecLimiter = On("limiter");
        s.VoiceRecAgc = On("agc");
        s.VoiceRecNormalize = On("normalize");
        s.VoiceRecTrimSilence = On("trim");
        s.VoiceRecCopyPath = On("copyPath");

        s.VoiceRecInputGainDb = (float)Val("gain");
        s.VoiceRecHighPassHz = (float)Val("hpHz");
        s.VoiceRecGateDb = (float)Val("gateDb");
        s.VoiceRecCompressStrength = (float)Val("comp");
        s.VoiceRecPresenceDb = (float)Val("presDb");
        s.VoiceRecWarmthDb = (float)Val("warmDb");
        s.VoiceRecFadeMs = (int)Val("fade");
        s.VoiceRecMonitorVolume = (int)Val("mon");
        s.VoiceRecPreRollMs = (int)Val("pre");
        s.VoiceRecMaxMinutes = (int)Val("max");
        string fmt = _format.SelectedItem is FormatPick pick
            ? pick.Id
            : ScreenshotStore.NormalizeVoiceRecFormat(s.VoiceRecFormat, s.VoiceRecBitDepth);
        s.VoiceRecFormat = fmt;
        s.VoiceRecBitDepth = fmt is "wav24" ? 24 : 16;
        s.VoiceRecFolder = (_folder.Text ?? "").Trim().Trim('"');
    }

    private void OnReset()
    {
        ApplyDefaults(ScreenshotStore.Current);
        ScreenshotStore.Save(ScreenshotStore.Current);
        Load();
    }

    public static void ApplyDefaults(ScreenshotSettings s)
    {
        s.VoiceRecEnhance = true;
        s.VoiceRecHighPass = true;
        s.VoiceRecGate = true;
        s.VoiceRecCompress = true;
        s.VoiceRecPresence = true;
        s.VoiceRecWarmth = true;
        s.VoiceRecDeEsser = true;
        s.VoiceRecLimiter = true;
        s.VoiceRecAgc = false;
        s.VoiceRecNormalize = true;
        s.VoiceRecTrimSilence = true;
        s.VoiceRecCopyPath = true;
        s.VoiceRecInputGainDb = 0;
        s.VoiceRecHighPassHz = 75;
        s.VoiceRecGateDb = -52;
        s.VoiceRecCompressStrength = 55;
        s.VoiceRecPresenceDb = 2.8f;
        s.VoiceRecWarmthDb = 1.8f;
        s.VoiceRecFadeMs = 12;
        s.VoiceRecMonitorVolume = 55;
        s.VoiceRecPreRollMs = 220;
        s.VoiceRecMaxMinutes = 30;
        s.VoiceRecBitDepth = 16;
        s.VoiceRecFormat = "mp3";
    }

    private void OnBrowse()
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Папка для голосовых записей",
            UseDescriptionForTitle = true,
            SelectedPath = string.IsNullOrWhiteSpace(_folder.Text)
                ? VoiceRecService.ResolveOutputDir()
                : _folder.Text
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return;
        _folder.Text = dlg.SelectedPath;
        Save();
    }

    private void RefreshLabels()
    {
        Label("gain", $"{Val("gain"):0}");
        Label("hpHz", $"{Val("hpHz"):0}");
        Label("gateDb", $"{Val("gateDb"):0}");
        Label("comp", $"{Val("comp"):0}");
        Label("presDb", $"{Val("presDb"):0.0}");
        Label("warmDb", $"{Val("warmDb"):0.0}");
        Label("fade", $"{Val("fade"):0}");
        Label("mon", $"{Val("mon"):0}");
        Label("pre", $"{Val("pre"):0}");
        Label("max", $"{Val("max"):0}");
    }

    private void SetCheck(string key, bool on)
    {
        if (_checks.TryGetValue(key, out var box))
            box.IsChecked = on;
    }

    private void SetSlider(string key, double value)
    {
        if (_sliders.TryGetValue(key, out var pair))
            pair.Slider.Value = value;
    }

    private bool On(string key) =>
        _checks.TryGetValue(key, out var box) && box.IsChecked == true;

    private double Val(string key) =>
        _sliders.TryGetValue(key, out var pair) ? pair.Slider.Value : 0;

    private void Label(string key, string text)
    {
        if (_sliders.TryGetValue(key, out var pair))
            pair.Value.Text = text;
    }

    private static Border Section(string title, params UIElement[] children)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushA(0xCC, 0xC8, 0xD0, 0xC4),
            Margin = new Thickness(2, 0, 0, 6)
        });
        foreach (var child in children)
            stack.Children.Add(child);

        return new Border
        {
            Background = BrushA(0x14, 0xFF, 0xFF, 0xFF),
            BorderBrush = BrushA(0x18, 0xFF, 0xFF, 0xFF),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 10, 12, 12),
            Margin = new Thickness(0, 8, 0, 0),
            Child = stack
        };
    }

    private static void Tip(FrameworkElement el, string text)
    {
        ToolTipService.SetInitialShowDelay(el, 420);
        ToolTipService.SetBetweenShowDelay(el, 60);
        ToolTipService.SetShowDuration(el, 8000);
        el.ToolTip = new ToolTip
        {
            Content = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 280,
                FontSize = 12,
                Foreground = BrushA(0xE0, 0xF5, 0xF0, 0xE8)
            },
            Placement = PlacementMode.Mouse,
            Background = BrushA(0xF0, 0x18, 0x1A, 0x1E),
            BorderBrush = BrushA(0x40, 0xFF, 0xFF, 0xFF),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7, 10, 7),
            HasDropShadow = false
        };
    }

    private static Button Pill(string text, Action click, string tip, bool accent = false)
    {
        var btn = new Button
        {
            Content = text,
            MinWidth = 96,
            Padding = new Thickness(14, 8, 14, 8),
            FontSize = 13,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(0),
            Foreground = Fg(),
            Background = accent ? BrushA(0xFF, 0x2E, 0x6B, 0x5A) : BrushA(0x33, 0xFF, 0xFF, 0xFF)
        };
        btn.Template = RoundTemplate();
        btn.Click += (_, _) => click();
        Tip(btn, tip);
        return btn;
    }

    private static Button IconBtn(string glyph, Action click, string tip)
    {
        var btn = new Button
        {
            Content = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 12,
            Width = 28,
            Height = 28,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(0),
            Foreground = BrushA(0xAA, 0xF5, 0xF0, 0xE8),
            Background = BrushA(0x22, 0xFF, 0xFF, 0xFF)
        };
        btn.Template = RoundTemplate();
        btn.Click += (_, _) => click();
        Tip(btn, tip);
        return btn;
    }

    private static ControlTemplate RoundTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
        border.SetBinding(Border.BackgroundProperty,
            new System.Windows.Data.Binding(nameof(Background))
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, WpfHAlign.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, WpfVAlign.Center);
        content.SetValue(ContentPresenter.MarginProperty, new Thickness(8, 4, 8, 4));
        border.AppendChild(content);
        template.VisualTree = border;
        return template;
    }

    private static SolidColorBrush Fg() => Brush(0xF5, 0xF0, 0xE8);

    private static SolidColorBrush Brush(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    private static SolidColorBrush BrushA(byte a, byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        br.Freeze();
        return br;
    }

    private sealed record MicPick(int Index, string Title);
    private sealed record FormatPick(string Id, string Title);
}
