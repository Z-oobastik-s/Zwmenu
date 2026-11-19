using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Zwmenu.Services;
using CheckBox = System.Windows.Controls.CheckBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using WpfHAlign = System.Windows.HorizontalAlignment;
using WpfVAlign = System.Windows.VerticalAlignment;

namespace Zwmenu.Overlay;

/// <summary>Голоса, отдача в звонок и игру, короткие звуки.</summary>
public sealed class VoiceShiftWindow : Window
{
    private static VoiceShiftWindow? _current;

    private readonly Slider _semi;
    private readonly Slider _gain;
    private readonly CheckBox _hear;
    private readonly TextBlock _status;
    private readonly Button _live;
    private readonly Button _keyBtn;
    private readonly Button _micBtn;
    private readonly Button _earBtn;
    private readonly Slider _boost;
    private readonly Slider _gate;
    private readonly Slider _low;
    private readonly Slider _comp;
    private readonly Slider _hearGain;
    private readonly Slider _late;
    private readonly TextBlock _boostVal;
    private readonly TextBlock _gateVal;
    private readonly TextBlock _lowVal;
    private readonly TextBlock _compVal;
    private readonly TextBlock _hearVal;
    private readonly TextBlock _lateVal;
    private readonly TextBlock _semiVal;
    private readonly TextBlock _gainVal;
    private readonly TextBlock _meterWord;
    private readonly ScaleTransform _meterScale = new(0, 1);
    private readonly DispatcherTimer _meterTimer;
    private readonly System.Windows.Controls.Primitives.UniformGrid _girls = new() { Columns = 3, Margin = new Thickness(0, 2, 0, 0) };
    private readonly System.Windows.Controls.Primitives.UniformGrid _faces = new() { Columns = 4, Margin = new Thickness(0, 2, 0, 0) };
    private readonly System.Windows.Controls.Primitives.UniformGrid _presets = new() { Columns = 4, Margin = new Thickness(0, 2, 0, 0) };
    private readonly WrapPanel _board = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly List<Button> _presetButtons = new();
    private bool _suspend;
    private int _arm = -1;
    private int _meterBand = -1;
    private VoiceShiftLive.AudioEnd[] _mics = Array.Empty<VoiceShiftLive.AudioEnd>();
    private VoiceShiftLive.AudioEnd[] _ears = Array.Empty<VoiceShiftLive.AudioEnd>();

    public static void ShowCurrent()
    {
        var app = Application.Current;
        if (app == null)
            return;
        void Go()
        {
            if (_current != null)
            {
                _current.Activate();
                return;
            }

            var w = new VoiceShiftWindow();
            _current = w;
            w.Closed += (_, _) =>
            {
                VoiceShiftLive.Arming = false;
                if (ReferenceEquals(_current, w))
                    _current = null;
            };
            w.Show();
            w.Activate();
        }

        if (app.Dispatcher.CheckAccess())
            Go();
        else
            app.Dispatcher.Invoke(Go);
    }

    private VoiceShiftWindow()
    {
        Title = "Голос";
        var area = SystemParameters.WorkArea;
        Width = Math.Min(1040, area.Width - 24);
        Height = Math.Min(680, area.Height - 12);
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = true;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
        PreviewKeyDown += OnPreviewKey;

        var title = new TextBlock
        {
            Text = "Голос",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ink(0xF5, 0xF0, 0xE8),
            VerticalAlignment = WpfVAlign.Center,
            Cursor = Cursors.SizeAll
        };
        title.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 1)
            {
                try { DragMove(); } catch { /* окно уже закрывается */ }
            }
        };
        var close = Quiet("\uE711", Close);
        close.FontFamily = new FontFamily("Segoe MDL2 Assets");
        close.Width = 28;
        close.Height = 28;
        close.Padding = new Thickness(0);
        var head = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        head.Children.Add(title);

        var hint = new TextBlock
        {
            Text = "Слева голос для звонка. Справа микрофон: устройство, тишина, гул и куда слышно себя.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            Foreground = InkA(0xB0, 0xC8, 0xD0, 0xC4),
            Margin = new Thickness(0, 8, 0, 0)
        };

        _semi = new Slider
        {
            Minimum = -15,
            Maximum = 15,
            Value = VoiceShift.Shared.Semi,
            VerticalAlignment = WpfVAlign.Center
        };
        _gain = new Slider
        {
            Minimum = 0.5,
            Maximum = 2,
            Value = VoiceShift.Shared.Gain,
            VerticalAlignment = WpfVAlign.Center
        };
        _semi.PreviewMouseLeftButtonUp += (_, _) => VoiceShift.Shared.SaveTone();
        _gain.PreviewMouseLeftButtonUp += (_, _) => VoiceShift.Shared.SaveTone();
        _semi.LostFocus += (_, _) => VoiceShift.Shared.SaveTone();
        _gain.LostFocus += (_, _) => VoiceShift.Shared.SaveTone();

        for (int i = 0; i < VoiceColor.All.Count; i++)
        {
            int index = i;
            var btn = Quiet(VoiceColor.All[i].Title, () =>
            {
                VoiceShift.Shared.ApplyPreset(index);
                _suspend = true;
                _semi.Value = VoiceColor.All[index].Semi;
                _suspend = false;
                Paint();
            });
            btn.Height = 30;
            btn.Padding = new Thickness(4, 0, 4, 0);
            btn.Margin = new Thickness(2);
            btn.HorizontalAlignment = WpfHAlign.Stretch;
            btn.HorizontalContentAlignment = WpfHAlign.Center;
            btn.FontSize = 12;
            btn.Content = new TextBlock
            {
                Text = VoiceColor.All[i].Title,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = WpfVAlign.Center
            };
            btn.ToolTip = VoiceColor.All[i].Title;
            _presetButtons.Add(btn);
            if (VoiceColor.All[i].Model.Length > 0)
                _girls.Children.Add(btn);
            else if (VoiceColor.All[i].Face.On)
                _faces.Children.Add(btn);
            else
                _presets.Children.Add(btn);
        }

        _hear = new CheckBox
        {
            Content = "Слышать себя",
            Foreground = Ink(0xF5, 0xF0, 0xE8),
            Margin = new Thickness(2, 8, 0, 2),
            VerticalAlignment = WpfVAlign.Center,
            IsChecked = ScreenshotStore.Current.VoiceShiftHear
        };
        _hear.Checked += (_, _) => Hear(true);
        _hear.Unchecked += (_, _) => Hear(false);

        var saved = ScreenshotStore.Current;
        _micBtn = MenuButton(MicTitle(saved.VoiceShiftMicId), OpenMicMenu);
        _earBtn = MenuButton(EarTitle(saved.VoiceShiftHearId), OpenEarMenu);
        _boost = Knob(0.5, 4, VoiceShift.Shared.Boost);
        _gate = Knob(0, 0.04, VoiceShift.Shared.GateLevel);
        _low = Knob(40, 220, VoiceShift.Shared.LowCut);
        _comp = Knob(0, 1.2, VoiceShift.Shared.Comp);
        _hearGain = Knob(0, 1, VoiceShift.Shared.HearGain);
        _late = Knob(0, 200, saved.VoiceShiftLatencyMs);
        _boostVal = ValueText(BoostText());
        _gateVal = ValueText(GateText());
        _lowVal = ValueText(LowText());
        _compVal = ValueText(CompText());
        _hearVal = ValueText(HearText());
        _lateVal = ValueText(LateText());
        _semiVal = ValueText(SemiText());
        _gainVal = ValueText(GainText());
        _boost.ValueChanged += (_, _) => { if (!_suspend) { VoiceShift.Shared.SetBoost((float)_boost.Value); _boostVal.Text = BoostText(); } };
        _gate.ValueChanged += (_, _) => { if (!_suspend) { VoiceShift.Shared.SetGateLevel((float)_gate.Value); _gateVal.Text = GateText(); } };
        _low.ValueChanged += (_, _) => { if (!_suspend) { VoiceShift.Shared.SetLowCut((float)_low.Value); _lowVal.Text = LowText(); } };
        _comp.ValueChanged += (_, _) => { if (!_suspend) { VoiceShift.Shared.SetComp((float)_comp.Value); _compVal.Text = CompText(); } };
        _hearGain.ValueChanged += (_, _) => { if (!_suspend) { VoiceShift.Shared.SetHearGain((float)_hearGain.Value); _hearVal.Text = HearText(); } };
        _late.ValueChanged += (_, _) => { if (!_suspend) _lateVal.Text = LateText(); };
        _late.PreviewMouseLeftButtonUp += (_, _) => CommitLatency();
        _late.LostFocus += (_, _) => CommitLatency();
        foreach (var slider in new[] { _boost, _gate, _low, _comp, _hearGain })
        {
            slider.PreviewMouseLeftButtonUp += (_, _) => VoiceShift.Shared.SaveMic();
            slider.LostFocus += (_, _) => VoiceShift.Shared.SaveMic();
        }

        var meter = new Border
        {
            Height = 8,
            HorizontalAlignment = WpfHAlign.Stretch,
            VerticalAlignment = WpfVAlign.Center,
            CornerRadius = new CornerRadius(4),
            Background = OnBrush,
            RenderTransform = _meterScale,
            RenderTransformOrigin = new Point(0, 0.5)
        };
        var meterTrack = new Border
        {
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = InkA(0x44, 0xFF, 0xFF, 0xFF),
            ClipToBounds = true,
            VerticalAlignment = WpfVAlign.Center,
            Child = meter
        };
        _meterWord = new TextBlock
        {
            Text = "жди голос",
            Foreground = InkA(0xB0, 0xC8, 0xD0, 0xC4),
            FontSize = 12,
            VerticalAlignment = WpfVAlign.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Width = 150
        };
        var meterRow = new Grid { Margin = new Thickness(0, 8, 0, 2) };
        meterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        meterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_meterWord, 1);
        meterRow.Children.Add(meterTrack);
        meterRow.Children.Add(_meterWord);
        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _meterTimer.Tick += (_, _) => TickMeter();

        _live = BarButton("Включить", ToggleLive);
        var reset = BarButton("Сбросить клавишу", ResetVoice);
        _keyBtn = BarButton(KeyTitle(), ArmToggle);
        var row = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(_live);
        row.Children.Add(reset);
        row.Children.Add(_keyBtn);

        _status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            Foreground = InkA(0xB0, 0xC8, 0xD0, 0xC4),
            Margin = new Thickness(0, 8, 0, 0)
        };
        _semi.ValueChanged += (_, _) =>
        {
            if (_suspend)
                return;
            VoiceShift.Shared.SetSemi((float)_semi.Value);
            _semiVal.Text = SemiText();
            if (!VoiceShiftLive.Shared.Running)
                _status.Text = IdleText();
        };
        _gain.ValueChanged += (_, _) =>
        {
            if (_suspend)
                return;
            VoiceShift.Shared.SetGain((float)_gain.Value);
            _gainVal.Text = GainText();
        };

        var add = BarButton("Добавить звук", AddSound);
        var cable = BarButton("Поставить кабель", () => SetupOffer.ShowNow());
        var sound = BarButton("Звук Windows", OpenWindowsSound);
        var resetMic = BarButton("Микрофон как был", ResetMic);
        var tools = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4, Margin = new Thickness(0, 10, 0, 0) };
        tools.Children.Add(cable);
        tools.Children.Add(sound);
        tools.Children.Add(resetMic);
        tools.Children.Add(add);

        var left = new StackPanel();
        left.Children.Add(Section("Девушки"));
        left.Children.Add(_girls);
        left.Children.Add(Section("Образы"));
        left.Children.Add(_faces);
        left.Children.Add(Section("Эффекты"));
        left.Children.Add(_presets);
        left.Children.Add(KnobRow("Тон", _semi, _semiVal));
        left.Children.Add(KnobRow("Громкость", _gain, _gainVal));

        var right = new StackPanel();
        right.Children.Add(Section("Устройство"));
        right.Children.Add(_micBtn);
        right.Children.Add(meterRow);
        right.Children.Add(KnobRow("Усиление", _boost, _boostVal));
        right.Children.Add(KnobRow("Тишина", _gate, _gateVal));
        right.Children.Add(KnobRow("Срез низа", _low, _lowVal));
        right.Children.Add(KnobRow("Ровно", _comp, _compVal));
        right.Children.Add(_hear);
        right.Children.Add(KnobRow("В ушах", _hearGain, _hearVal));
        right.Children.Add(Section("Куда слышать"));
        right.Children.Add(_earBtn);
        right.Children.Add(KnobRow("Запас", _late, _lateVal));

        var columns = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var leftCard = Pane(left);
        var rightCard = Pane(right);
        Grid.SetColumn(rightCard, 2);
        columns.Children.Add(leftCard);
        columns.Children.Add(rightCard);

        var sounds = new TextBlock
        {
            Text = "Звуки",
            Margin = new Thickness(2, 12, 0, 0),
            Foreground = Ink(0xF5, 0xF0, 0xE8),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold
        };

        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(hint);
        stack.Children.Add(row);
        stack.Children.Add(_status);
        stack.Children.Add(columns);
        stack.Children.Add(tools);
        stack.Children.Add(sounds);
        stack.Children.Add(_board);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = stack,
            Focusable = false
        };
        KillScroll(scroll);
        var card = new Border
        {
            Background = InkA(0xEE, 0x12, 0x14, 0x18),
            BorderBrush = InkA(0x55, 0x3A, 0x4A, 0x52),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(18, 14, 14, 14),
            Child = scroll
        };
        Content = new Grid { Children = { card }, Margin = new Thickness(10) };

        VoiceShift.Shared.Changed += OnTone;
        VoiceShiftLive.Shared.Changed += OnLive;
        VoiceBoard.Shared.Changed += OnBoard;
        VoiceGirl.Shared.Changed += OnGirl;
        Closed += (_, _) =>
        {
            _meterTimer.Stop();
            if (_devicePop != null)
                _devicePop.IsOpen = false;
            VoiceShift.Shared.Changed -= OnTone;
            VoiceShiftLive.Shared.Changed -= OnLive;
            VoiceBoard.Shared.Changed -= OnBoard;
            VoiceGirl.Shared.Changed -= OnGirl;
        };
        FillBoard();
        Paint();
        Loaded += (_, _) =>
        {
            VoiceShiftLive.Shared.RequestPrepare();
            VoiceShiftLive.Shared.RequestDevices(ShowDevices);
            _meterTimer.Start();
        };
    }

    private void ToggleLive()
    {
        if (VoiceShiftLive.Shared.Busy)
            return;
        if (VoiceShiftLive.Shared.Running)
        {
            _status.Text = "Выключаю…";
            _live.IsEnabled = false;
            VoiceShiftLive.Shared.RequestStop();
            return;
        }

        BeginEnable();
    }

    private void ResetVoice()
    {
        var s = ScreenshotStore.Current;
        s.VoiceShiftToggleKey = 0;
        ScreenshotStore.Save(s);
        Disarm();
        Paint();
        _status.Text = "Клавиша снята. Ей снова можно печатать.";
    }

    private void BeginEnable()
    {
        _status.Text = "Включаю…";
        _live.IsEnabled = false;
        VoiceShiftLive.Shared.RequestStart(_hear.IsChecked == true);
    }

    private void Hear(bool on)
    {
        if (_suspend)
            return;
        var s = ScreenshotStore.Current;
        s.VoiceShiftHear = on;
        ScreenshotStore.Save(s);
        if (VoiceShiftLive.Shared.Running)
            VoiceShiftLive.Shared.SetHear(on, startIfOff: false);
    }

    private void AddSound()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Звук в микрофон",
            Filter = "Звук|*.wav;*.mp3;*.m4a;*.wma;*.aac|Все файлы|*.*"
        };
        if (dlg.ShowDialog(this) == true)
            VoiceBoard.Shared.Add(dlg.FileName);
    }

    private void ArmToggle()
    {
        _arm = -2;
        VoiceShiftLive.Arming = true;
        _keyBtn.Content = "Нажми клавишу";
    }

    private void Disarm()
    {
        _arm = -1;
        VoiceShiftLive.Arming = false;
        _keyBtn.Content = KeyTitle();
    }

    private void FillBoard()
    {
        _board.Children.Clear();
        foreach (var clip in VoiceBoard.Shared.Clips)
        {
            string path = clip.Path;
            string title = clip.Title;
            if (clip.Key != 0)
                title += "  " + ScreenshotStore.HotkeyTitle(clip.Key);
            var btn = Quiet(title, () => VoiceBoard.Shared.Play(path));
            var menu = new ContextMenu();
            var key = new MenuItem { Header = "Назначить клавишу" };
            key.Click += (_, _) =>
            {
                _arm = 0;
                VoiceShiftLive.Arming = true;
                _keyBtn.Content = "Клавиша для «" + clip.Title + "»";
                _armPath = path;
            };
            var remove = new MenuItem { Header = "Убрать" };
            remove.Click += (_, _) => VoiceBoard.Shared.Remove(path);
            menu.Items.Add(key);
            menu.Items.Add(remove);
            btn.ContextMenu = menu;
            _board.Children.Add(btn);
        }
    }

    private string _armPath = "";

    private void OnTone()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnTone);
            return;
        }

        _suspend = true;
        _semi.Value = VoiceShift.Shared.Semi;
        _gain.Value = VoiceShift.Shared.Gain;
        _semiVal.Text = SemiText();
        _gainVal.Text = GainText();
        _suspend = false;
        Paint();
    }

    private void OnLive()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnLive);
            return;
        }

        Paint();
    }

    private void OnBoard()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(FillBoard);
            return;
        }

        FillBoard();
    }

    private void Paint()
    {
        int preset = VoiceShift.Shared.Preset;
        for (int i = 0; i < _presetButtons.Count; i++)
        {
            bool on = i == preset;
            _presetButtons[i].Background = on ? OnBrush : OffBrush;
            _presetButtons[i].FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }

        if (_arm < 0)
            _keyBtn.Content = KeyTitle();
        _suspend = true;
        _hear.IsChecked = ScreenshotStore.Current.VoiceShiftHear;
        _suspend = false;
        _live.IsEnabled = VoiceShiftLive.Shared.Prepared && !VoiceShiftLive.Shared.Busy;
        if (VoiceShiftLive.Shared.Running)
        {
            _live.Content = "Выключить";
            _live.Background = OnBrush;
            _status.Text = VoiceShiftLive.Shared.Note + GirlNote();
        }
        else if (VoiceShiftLive.Shared.Busy)
        {
            _live.Content = "Включить";
            _live.Background = OffBrush;
        }
        else
        {
            _live.Content = "Включить";
            _live.Background = OffBrush;
            _status.Text = IdleText();
        }
    }

    private void OnGirl()
    {
        if (!Dispatcher.CheckAccess())
        {
            try { Dispatcher.BeginInvoke(OnGirl); } catch { /* окно уже закрыто */ }
            return;
        }

        Paint();
    }

    private static string GirlNote()
    {
        string girl = VoiceGirl.Shared.Status;
        return girl.Length == 0 ? "" : " " + girl;
    }

    private string IdleText()
    {
        string girl = GirlNote();
        if (VoiceShift.Shared.Library)
            return VoiceShift.Shared.Title + ", голос из библиотеки." + girl + " " + VoiceShiftLive.Shared.Note;
        float semi = VoiceShift.Shared.Semi;
        string shift = Math.Abs(semi) < 0.08f
            ? "без сдвига"
            : (semi > 0 ? "выше на " : "ниже на ") + Math.Abs(semi).ToString("0.#");
        return VoiceShift.Shared.Title + ", " + shift + ". " + VoiceShiftLive.Shared.Note + girl;
    }

    private static readonly SolidColorBrush OnBrush = Freeze(0xFF, 0x2E, 0x6B, 0x5A);
    private static readonly SolidColorBrush OffBrush = Freeze(0x33, 0xFF, 0xFF, 0xFF);
    private static readonly SolidColorBrush HoverBrush = Freeze(0xFF, 0x2A, 0x32, 0x38);
    private static readonly SolidColorBrush PressBrush = Freeze(0xFF, 0x1C, 0x24, 0x2A);

    private static SolidColorBrush Freeze(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private string KeyTitle()
    {
        int vk = ScreenshotStore.Current.VoiceShiftToggleKey;
        return vk == 0 ? "Клавиша вкл" : "Клавиша " + ScreenshotStore.HotkeyTitle(vk);
    }

    private void TickMeter()
    {
        float peak = VoiceShiftLive.Shared.Running ? VoiceShiftLive.Shared.Peak : 0f;
        float shown = (float)_meterScale.ScaleX;
        shown = Math.Max(peak, shown * 0.78f);
        if (shown < 0.012f)
            shown = 0f;
        _meterScale.ScaleX = shown;
        int band = shown < 0.02f ? 0 : shown < 0.12f ? 1 : shown < 0.55f ? 2 : 3;
        if (band == _meterBand)
            return;
        _meterBand = band;
        _meterWord.Text = band switch
        {
            0 => VoiceShiftLive.Shared.Running ? "тихо, почти тишина" : "жди голос",
            1 => "слабо, добавь усиление",
            2 => "нормально",
            _ => "громко, убавь усиление"
        };
    }

    private void ShowDevices(VoiceShiftLive.AudioEnd[] mics, VoiceShiftLive.AudioEnd[] ears)
    {
        _mics = mics ?? Array.Empty<VoiceShiftLive.AudioEnd>();
        _ears = ears ?? Array.Empty<VoiceShiftLive.AudioEnd>();
        var s = ScreenshotStore.Current;
        _micBtn.Content = _mics.Length == 0 ? "Микрофон не вижу" : MicTitle(s.VoiceShiftMicId);
        _earBtn.Content = _ears.Length == 0 ? "Некуда слышать себя" : EarTitle(s.VoiceShiftHearId);
        if (_mics.Length == 0 && !VoiceShiftLive.Shared.Running)
            _status.Text = "Микрофона нет. Открой звук Windows и включи его, потом обнови список.";
    }

    private Popup? _devicePop;

    private void OpenMicMenu() => OpenDeviceMenu(_micBtn, _mics, ChooseMic, "Микрофонов нет");

    private void OpenEarMenu() => OpenDeviceMenu(_earBtn, _ears, ChooseEar, "Колонок и наушников нет");

    private void OpenDeviceMenu(Button owner, VoiceShiftLive.AudioEnd[] items, Action<string, string> pick, string empty)
    {
        if (_devicePop != null)
        {
            _devicePop.IsOpen = false;
            _devicePop = null;
        }

        var list = new StackPanel();
        void AddRow(string title, Action click)
        {
            var label = new TextBlock
            {
                Text = title,
                Foreground = Ink(0xF5, 0xF0, 0xE8),
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = WpfVAlign.Center
            };
            var row = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(12, 8, 12, 8),
                Cursor = Cursors.Hand,
                Child = label
            };
            row.MouseEnter += (_, _) => row.Background = OnBrush;
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (_devicePop != null)
                    _devicePop.IsOpen = false;
                click();
            };
            list.Children.Add(row);
        }

        if (items.Length == 0)
            AddRow(empty, () => { });
        foreach (var item in items)
        {
            string id = item.Id;
            string name = item.Name;
            AddRow(name, () => pick(id, name));
        }

        AddRow("Обновить список", () => VoiceShiftLive.Shared.RequestDevices(ShowDevices));

        var card = new Border
        {
            Background = InkA(0xFF, 0x16, 0x1A, 0x1E),
            BorderBrush = InkA(0x88, 0x3A, 0x4A, 0x52),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            MinWidth = Math.Max(240, owner.ActualWidth),
            MaxWidth = 420,
            Child = list
        };
        var pop = new Popup
        {
            PlacementTarget = owner,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.None,
            Child = card
        };
        pop.Opened += (_, _) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (PresentationSource.FromVisual(card) is System.Windows.Interop.HwndSource source)
                    SetWindowPos(source.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013);
            }, DispatcherPriority.Loaded);
        };
        _devicePop = pop;
        pop.IsOpen = true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = false)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private void ChooseMic(string id, string name)
    {
        var s = ScreenshotStore.Current;
        s.VoiceShiftMicId = id;
        ScreenshotStore.Save(s);
        _micBtn.Content = Short(name);
        if (VoiceShiftLive.Shared.Running)
        {
            _status.Text = "Переключаю микрофон…";
            VoiceShiftLive.Shared.RequestApplyRoute();
        }
        else
            _status.Text = "Микрофон выбран. Заработает, когда включишь голос.";
    }

    private void ChooseEar(string id, string name)
    {
        var s = ScreenshotStore.Current;
        s.VoiceShiftHearId = id;
        ScreenshotStore.Save(s);
        _earBtn.Content = Short(name);
        if (VoiceShiftLive.Shared.Running && _hear.IsChecked == true)
        {
            _status.Text = "Переключаю, куда слышно себя…";
            VoiceShiftLive.Shared.RequestApplyRoute();
        }
        else
            _status.Text = "Слышать себя будет сюда. На звонок это не влияет.";
    }

    private void CommitLatency()
    {
        int ms = (int)Math.Round(_late.Value);
        if (ms is > 0 and < 40)
        {
            ms = 40;
            _suspend = true;
            _late.Value = 40;
            _suspend = false;
        }

        var s = ScreenshotStore.Current;
        if (s.VoiceShiftLatencyMs == ms)
            return;
        s.VoiceShiftLatencyMs = ms;
        ScreenshotStore.Save(s);
        _lateVal.Text = LateText();
        if (!VoiceShiftLive.Shared.Running)
            return;
        _status.Text = "Обновляю запас…";
        VoiceShiftLive.Shared.RequestApplyRoute();
    }

    private void ResetMic()
    {
        int was = ScreenshotStore.Current.VoiceShiftLatencyMs;
        _suspend = true;
        _boost.Value = 1;
        _gate.Value = 0.007;
        _low.Value = 90;
        _comp.Value = 0.62;
        _hearGain.Value = 0.72;
        _late.Value = 0;
        _suspend = false;
        VoiceShift.Shared.SetBoost(1f);
        VoiceShift.Shared.SetGateLevel(0.007f);
        VoiceShift.Shared.SetLowCut(90f);
        VoiceShift.Shared.SetComp(0.62f);
        VoiceShift.Shared.SetHearGain(0.72f);
        VoiceShift.Shared.SaveMic();
        var s = ScreenshotStore.Current;
        s.VoiceShiftLatencyMs = 0;
        ScreenshotStore.Save(s);
        _boostVal.Text = BoostText();
        _gateVal.Text = GateText();
        _lowVal.Text = LowText();
        _compVal.Text = CompText();
        _hearVal.Text = HearText();
        _lateVal.Text = LateText();
        _status.Text = "Микрофон снова как был.";
        if (was != 0 && VoiceShiftLive.Shared.Running)
            VoiceShiftLive.Shared.RequestApplyRoute();
    }

    private static void OpenWindowsSound()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mmsys.cpl")
            {
                UseShellExecute = true
            });
        }
        catch
        {
            /* звук Windows не открылся */
        }
    }

    private string MicTitle(string id)
    {
        if (id.Length == 0)
            return "Обычный микрофон Windows";
        foreach (var item in _mics)
        {
            if (item.Id == id)
                return Short(item.Name);
        }

        return "Микрофон не на месте";
    }

    private string EarTitle(string id)
    {
        if (id.Length == 0)
            return "Колонки или наушники";
        foreach (var item in _ears)
        {
            if (item.Id == id)
                return Short(item.Name);
        }

        return "Это устройство не на месте";
    }

    private static string Short(string name)
    {
        name = (name ?? "").Trim();
        return name.Length <= 42 ? name : name[..39] + "…";
    }

    private string BoostText() => _boost.Value.ToString("0.0") + "×";

    private string GateText() => _gate.Value < 0.0008 ? "выкл" : _gate.Value.ToString("0.000");

    private string LowText() => ((int)Math.Round(_low.Value)).ToString() + " Гц";

    private string CompText() => _comp.Value < 0.02 ? "выкл" : ((int)Math.Round(_comp.Value * 100)).ToString();

    private string HearText() => ((int)Math.Round(_hearGain.Value * 100)).ToString() + "%";

    private string LateText()
    {
        int ms = (int)Math.Round(_late.Value);
        if (ms <= 0)
            return "само";
        return ms + " мс";
    }

    private string SemiText()
    {
        float value = (float)_semi.Value;
        if (Math.Abs(value) < 0.05f)
            return "0";
        return (value > 0 ? "+" : "") + value.ToString("0.#");
    }

    private string GainText() => _gain.Value.ToString("0.00") + "×";

    private static Slider Knob(double min, double max, double value) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = value,
        VerticalAlignment = WpfVAlign.Center,
        Margin = new Thickness(8, 0, 8, 0)
    };

    private static TextBlock ValueText(string text) => new()
    {
        Text = text,
        Width = 52,
        FontSize = 12,
        TextAlignment = TextAlignment.Right,
        Foreground = InkA(0xD0, 0xC8, 0xD0, 0xC4),
        VerticalAlignment = WpfVAlign.Center
    };

    private static Grid KnobRow(string title, Slider slider, TextBlock value)
    {
        var grid = new Grid { Height = 28, Margin = new Thickness(0, 3, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(108) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        var name = new TextBlock
        {
            Text = title,
            Foreground = Ink(0xF5, 0xF0, 0xE8),
            FontSize = 12.5,
            VerticalAlignment = WpfVAlign.Center
        };
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(value, 2);
        grid.Children.Add(name);
        grid.Children.Add(slider);
        grid.Children.Add(value);
        return grid;
    }

    private Button BarButton(string text, Action click)
    {
        var btn = Quiet(text, click);
        btn.Height = 32;
        btn.Margin = new Thickness(3, 0, 3, 0);
        btn.HorizontalAlignment = WpfHAlign.Stretch;
        btn.HorizontalContentAlignment = WpfHAlign.Center;
        return btn;
    }

    private static Border Pane(UIElement child) => new()
    {
        Background = InkA(0xFF, 0x1A, 0x1E, 0x22),
        BorderBrush = InkA(0x33, 0x3A, 0x4A, 0x52),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(14),
        Padding = new Thickness(12, 10, 12, 12),
        Child = child
    };

    private static TextBlock Section(string text) => new()
    {
        Text = text,
        Margin = new Thickness(2, 8, 0, 2),
        Foreground = InkA(0xCC, 0xC8, 0xD0, 0xC4),
        FontSize = 12,
        FontWeight = FontWeights.SemiBold
    };

    private Button MenuButton(string text, Action click)
    {
        var btn = Quiet(text, click);
        btn.Height = 32;
        btn.Margin = new Thickness(0, 2, 0, 2);
        btn.HorizontalAlignment = WpfHAlign.Stretch;
        btn.HorizontalContentAlignment = WpfHAlign.Left;
        return btn;
    }

    private static void KillScroll(FrameworkElement host)
    {
        var style = new Style(typeof(System.Windows.Controls.Primitives.ScrollBar));
        style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 0.0));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0.0));
        style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 0.0));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0.0));
        style.Setters.Add(new Setter(UIElement.OpacityProperty, 0.0));
        style.Setters.Add(new Setter(UIElement.IsHitTestVisibleProperty, false));
        style.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0)));
        host.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = style;
    }

    private TextBlock Label(string text) => new()
    {
        Text = text,
        Margin = new Thickness(0, 8, 0, 2),
        Foreground = Ink(0xF5, 0xF0, 0xE8),
        FontSize = 13
    };

    private Button Quiet(string text, Action click)
    {
        var btn = new Button
        {
            Content = text,
            Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(10, 5, 10, 5),
            FontSize = 12.5,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Foreground = Ink(0xF5, 0xF0, 0xE8),
            Background = OffBrush,
            HorizontalAlignment = WpfHAlign.Left,
            FocusVisualStyle = null,
            Template = QuietTemplate()
        };
        btn.Click += (_, _) => click();
        return btn;
    }

    private static ControlTemplate? _quietTemplate;

    private static ControlTemplate QuietTemplate()
    {
        if (_quietTemplate != null)
            return _quietTemplate;

        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "bd";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(Button.HorizontalContentAlignmentProperty));
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, new TemplateBindingExtension(Button.VerticalContentAlignmentProperty));
        border.AppendChild(content);
        template.VisualTree = border;

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, HoverBrush, "bd"));
        hover.Setters.Add(new Setter(Button.ForegroundProperty, Ink(0xF5, 0xF0, 0xE8)));
        template.Triggers.Add(hover);
        var press = new Trigger { Property = Button.IsPressedProperty, Value = true };
        press.Setters.Add(new Setter(Border.BackgroundProperty, PressBrush, "bd"));
        press.Setters.Add(new Setter(Button.ForegroundProperty, Ink(0xF5, 0xF0, 0xE8)));
        template.Triggers.Add(press);
        _quietTemplate = template;
        return template;
    }

    private void OnPreviewKey(object sender, KeyEventArgs e)
    {
        if (_arm == -1)
            return;
        int vk = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
        if (vk == 0x1B)
        {
            Disarm();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.None || !ScreenshotStore.IsAllowedHotkey(vk))
            return;
        var s = ScreenshotStore.Current;
        if (vk == s.Hotkey || vk == s.ClipHotkey || vk == s.TranslateHotkey || vk == s.DictationHotkey)
        {
            _status.Text = "Эта клавиша уже занята снимком, клипом, переводом или диктовкой.";
            Disarm();
            e.Handled = true;
            return;
        }

        if (_arm == -2)
        {
            s.VoiceShiftToggleKey = vk;
            ScreenshotStore.Save(s);
        }
        else if (!string.IsNullOrEmpty(_armPath))
            VoiceBoard.Shared.SetKey(_armPath, vk);

        Disarm();
        e.Handled = true;
        Paint();
    }

    private static SolidColorBrush Ink(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush InkA(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
