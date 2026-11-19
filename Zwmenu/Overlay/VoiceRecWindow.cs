using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Zwmenu.Services;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfHAlign = System.Windows.HorizontalAlignment;
using WpfVAlign = System.Windows.VerticalAlignment;

namespace Zwmenu.Overlay;

/// <summary>
/// Панель записи голоса: старт, пауза, стоп, монитор. Файл на рабочий стол.
/// </summary>
public sealed class VoiceRecWindow : Window
{
    private static VoiceRecWindow? _current;

    private VoiceRecService Svc => VoiceRecService.Shared;
    private readonly DispatcherTimer _uiTimer;

    private TextBlock? _time;
    private TextBlock? _status;
    private TextBlock? _pathHint;
    private Button? _mainBtn;
    private Button? _pauseBtn;
    private Button? _monitorBtn;
    private Button? _stopBtn;
    private Button? _cancelBtn;
    private Button? _folderBtn;
    private System.Windows.Shapes.Rectangle? _levelFill;
    private Ellipse? _recDot;
    private Border? _card;

    public static void ShowCurrent(bool startRecording = false)
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
                if (startRecording && VoiceRecService.Shared.State == VoiceRecState.Idle)
                    VoiceRecService.Shared.Start();
                return;
            }

            var w = new VoiceRecWindow();
            _current = w;
            w.Closed += (_, _) =>
            {
                if (ReferenceEquals(_current, w))
                    _current = null;
            };
            w.Show();
            w.Activate();
            if (startRecording && VoiceRecService.Shared.State == VoiceRecState.Idle)
                VoiceRecService.Shared.Start();
        }

        if (app.Dispatcher.CheckAccess())
            Go();
        else
            app.Dispatcher.Invoke(Go);
    }

    public static void Toggle()
    {
        if (_current != null)
        {
            if (VoiceRecService.Shared.State is VoiceRecState.Recording or VoiceRecState.Paused)
                _current.Activate();
            else
                _current.Close();
            return;
        }

        ShowCurrent();
    }

    private VoiceRecWindow()
    {
        Title = "Запись голоса";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = true;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;

        BuildUi();
        PlaceBottomCenter();

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _uiTimer.Tick += (_, _) => RefreshUi();
        _uiTimer.Start();

        Svc.Changed += OnSvcChanged;
        Svc.Saved += OnSaved;
        Svc.Failed += OnFailed;

        KeyDown += OnKeyDown;
        Loaded += (_, _) =>
        {
            PlaceBottomCenter();
            RefreshUi();
        };
        Closed += (_, _) =>
        {
            _uiTimer.Stop();
            Svc.Changed -= OnSvcChanged;
            Svc.Saved -= OnSaved;
            Svc.Failed -= OnFailed;
            // Saving не трогаем: файл уже кодируется в фоне.
            if (Svc.State is VoiceRecState.Recording or VoiceRecState.Paused)
                Svc.Cancel();
        };
    }

    private void BuildUi()
    {
        var title = new TextBlock
        {
            Text = "Запись голоса",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(0xF5, 0xF0, 0xE8)
        };

        _recDot = new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = Brush(0xE8, 0x4A, 0x4A),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = WpfVAlign.Center
        };

        _time = new TextBlock
        {
            Text = "00:00",
            FontSize = 34,
            FontWeight = FontWeights.Medium,
            FontFamily = new FontFamily("Segoe UI Semibold"),
            Foreground = Brush(0xF5, 0xF0, 0xE8),
            Margin = new Thickness(0, 10, 0, 0)
        };

        _status = new TextBlock
        {
            Text = "Готов",
            FontSize = 13,
            Foreground = BrushA(0xB0, 0xC8, 0xD0, 0xC4),
            Margin = new Thickness(0, 2, 0, 0)
        };

        var levelBg = new Border
        {
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = BrushA(0x40, 0xFF, 0xFF, 0xFF),
            Margin = new Thickness(0, 14, 0, 0),
            ClipToBounds = true
        };
        _levelFill = new System.Windows.Shapes.Rectangle
        {
            Height = 8,
            Width = 0,
            RadiusX = 4,
            RadiusY = 4,
            Fill = new LinearGradientBrush(
                Color.FromRgb(0x3E, 0xC8, 0x9A),
                Color.FromRgb(0xE8, 0xC0, 0x4A),
                0),
            HorizontalAlignment = WpfHAlign.Left
        };
        levelBg.Child = _levelFill;

        _pathHint = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Foreground = BrushA(0x88, 0x9A, 0xC8, 0xB8),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed
        };

        _mainBtn = Pill("Запись", OnMain, accent: true, tip: "Начать запись");
        _pauseBtn = Pill("Пауза", OnPause, tip: "Пауза / продолжить (Пробел)");
        _monitorBtn = Pill("Слушать себя", OnMonitor, tip: "Проверка микрофона (M)");
        _stopBtn = Pill("Готово", OnStop, accent: true, tip: "Сохранить на рабочий стол (Enter)");
        _cancelBtn = Pill("Отмена", OnCancel, tip: "Сбросить без сохранения (Esc)");
        _folderBtn = Pill("Папка", OnOpenFolder, tip: "Открыть папку с записями");

        var row1 = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            Margin = new Thickness(0, 16, 0, 0),
            Children = { _mainBtn, Spacer(), _pauseBtn, Spacer(), _monitorBtn }
        };
        var row2 = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { _stopBtn, Spacer(), _cancelBtn, Spacer(), _folderBtn }
        };
        var voiceBtn = Pill("Другой голос", (_, _) => VoiceShiftWindow.ShowCurrent(), tip: "Тон для записи, звонков и игр");
        var row3 = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { voiceBtn }
        };

        var headLeft = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            VerticalAlignment = WpfVAlign.Center,
            Children = { _recDot, title }
        };

        var closeBtn = new Button
        {
            Content = "\uE711",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 12,
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(0),
            Foreground = BrushA(0xAA, 0xF5, 0xF0, 0xE8),
            Background = BrushA(0x22, 0xFF, 0xFF, 0xFF),
            VerticalAlignment = WpfVAlign.Center,
            Margin = new Thickness(6, 0, 0, 0)
        };
        closeBtn.Template = PillTemplate();
        closeBtn.Click += OnCloseClick;
        AttachQuietTip(closeBtn, "Закрыть");

        var settingsBtn = new Button
        {
            Content = "\uE713",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 12,
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(0),
            Foreground = BrushA(0xAA, 0xF5, 0xF0, 0xE8),
            Background = BrushA(0x22, 0xFF, 0xFF, 0xFF),
            VerticalAlignment = WpfVAlign.Center
        };
        settingsBtn.Template = PillTemplate();
        settingsBtn.Click += (_, _) => VoiceRecSettingsWindow.ShowNear(this);
        AttachQuietTip(settingsBtn, "Настройки диктофона");

        var headRight = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = WpfHAlign.Right,
            VerticalAlignment = WpfVAlign.Center,
            Children = { settingsBtn, closeBtn }
        };

        var head = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(headRight, Dock.Right);
        head.Children.Add(headRight);
        head.Children.Add(headLeft);

        var stack = new StackPanel
        {
            Children = { head, _time, _status, levelBg, _pathHint, row1, row2, row3 }
        };

        _card = new Border
        {
            Background = BrushA(0xEE, 0x12, 0x14, 0x18),
            BorderBrush = BrushA(0x55, 0x3A, 0x4A, 0x52),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(22, 18, 22, 18),
            Child = stack
        };

        // Drag area
        _card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 1)
            {
                try { DragMove(); } catch { /* ignore */ }
            }
        };

        Content = new Grid { Children = { _card }, Margin = new Thickness(12) };
    }

    private static FrameworkElement Spacer() => new FrameworkElement { Width = 8 };

    private static Button Pill(string text, RoutedEventHandler click, bool accent = false, string? tip = null)
    {
        var btn = new Button
        {
            Content = text,
            Padding = new Thickness(14, 8, 14, 8),
            FontSize = 13,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(0),
            Foreground = Brush(0xF5, 0xF0, 0xE8),
            Background = accent
                ? BrushA(0xFF, 0x2E, 0x6B, 0x5A)
                : BrushA(0x33, 0xFF, 0xFF, 0xFF)
        };
        btn.Template = PillTemplate();
        btn.Click += click;
        if (!string.IsNullOrWhiteSpace(tip))
            AttachQuietTip(btn, tip);
        return btn;
    }

    private static void AttachQuietTip(FrameworkElement el, string text)
    {
        ToolTipService.SetInitialShowDelay(el, 650);
        ToolTipService.SetBetweenShowDelay(el, 80);
        ToolTipService.SetShowDuration(el, 3500);
        el.ToolTip = new ToolTip
        {
            Content = text,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse,
            Background = BrushA(0xE8, 0x1A, 0x1C, 0x20),
            Foreground = BrushA(0xC8, 0xE8, 0xE4, 0xDC),
            BorderBrush = BrushA(0x40, 0xFF, 0xFF, 0xFF),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4, 8, 4),
            FontSize = 11.5,
            HasDropShadow = false
        };
    }

    private static ControlTemplate PillTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "bd";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
        border.SetBinding(Border.BackgroundProperty,
            new System.Windows.Data.Binding(nameof(Background))
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, WpfHAlign.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, WpfVAlign.Center);
        content.SetValue(ContentPresenter.MarginProperty, new Thickness(14, 8, 14, 8));
        border.AppendChild(content);
        template.VisualTree = border;
        return template;
    }

    private void PlaceBottomCenter()
    {
        double w = ActualWidth > 1 ? ActualWidth : Width;
        double h = ActualHeight > 1 ? ActualHeight : 260;
        var tip = System.Windows.Forms.Cursor.Position;
        var work = System.Windows.Forms.Screen.FromPoint(tip).WorkingArea;
        // После Show есть PresentationSource — считаем DIP рабочей области.
        if (IsLoaded || IsVisible)
        {
            Point tl = RadialLayout.DeviceToDip(this, work.Left, work.Top);
            Point br = RadialLayout.DeviceToDip(this, work.Right, work.Bottom);
            double left = tl.X + ((br.X - tl.X) - w) / 2;
            double top = br.Y - h - 28;
            RadialLayout.PlaceInWorkArea(this, left, top, w, h, tip);
            return;
        }

        // До Show: грубо, потом Loaded поправит.
        Left = work.Left + (work.Width - w) / 2.0;
        Top = work.Top + work.Height - h - 40;
    }

    private void OnSvcChanged() => Dispatcher.BeginInvoke(RefreshUi);

    private void OnSaved(string path)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_pathHint != null)
            {
                _pathHint.Text = System.IO.Path.GetFileName(path);
                _pathHint.Visibility = Visibility.Visible;
                AttachQuietTip(_pathHint, path);
            }

            if (_status != null)
                _status.Text = "Сохранено";
            RefreshUi();
        });
    }

    private void OnFailed(string msg)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_status != null)
                _status.Text = msg;
            RefreshUi();
        });
    }

    private void RefreshUi()
    {
        var st = Svc.State;
        var elapsed = Svc.Elapsed;
        if (_time != null)
            _time.Text = elapsed.ToString(elapsed.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss");

        if (_status != null && st != VoiceRecState.Idle)
        {
            _status.Text = st switch
            {
                VoiceRecState.Recording => Svc.IsMonitoring ? "Запись, монитор" : "Запись",
                VoiceRecState.Paused => Svc.IsMonitoring ? "Пауза, монитор" : "Пауза",
                VoiceRecState.Saving => "Сохраняю…",
                _ => _status.Text
            };
        }
        else if (_status != null && st == VoiceRecState.Idle && string.IsNullOrEmpty(_pathHint?.Text))
        {
            _status.Text = Svc.IsMonitoring ? "Монитор" : "Готов";
        }

        if (_recDot != null)
        {
            bool blink = st == VoiceRecState.Recording && (Environment.TickCount64 / 400) % 2 == 0;
            _recDot.Opacity = st == VoiceRecState.Paused ? 0.35 : blink || st == VoiceRecState.Recording ? 1 : 0.25;
            _recDot.Fill = st == VoiceRecState.Paused
                ? Brush(0xE8, 0xC0, 0x4A)
                : Brush(0xE8, 0x4A, 0x4A);
        }

        if (_levelFill != null && _card != null)
        {
            double maxW = Math.Max(40, _card.ActualWidth - 44);
            if (maxW < 40 || double.IsNaN(maxW))
                maxW = 360;
            float lvl = Math.Max(Svc.LevelPeak, Svc.LevelRms * 1.4f);
            _levelFill.Width = Math.Clamp(lvl, 0, 1) * maxW;
        }

        bool live = st is VoiceRecState.Recording or VoiceRecState.Paused;
        if (_mainBtn != null)
        {
            _mainBtn.IsEnabled = st == VoiceRecState.Idle;
            _mainBtn.Content = "●  Запись";
        }

        if (_pauseBtn != null)
        {
            _pauseBtn.IsEnabled = live;
            _pauseBtn.Content = st == VoiceRecState.Paused ? "Продолжить" : "Пауза";
        }

        if (_monitorBtn != null)
        {
            _monitorBtn.IsEnabled = true;
            _monitorBtn.Content = Svc.IsMonitoring ? "Не слушать" : "Слушать себя";
        }

        if (_stopBtn != null)
        {
            // Во время сохранения кнопку не жмём повторно.
            _stopBtn.IsEnabled = live;
            _stopBtn.Content = st == VoiceRecState.Saving ? "Сохраняю…" : "Готово";
        }
        if (_cancelBtn != null)
            _cancelBtn.IsEnabled = live;
    }

    private void OnMain(object sender, RoutedEventArgs e)
    {
        _pathHint!.Visibility = Visibility.Collapsed;
        _pathHint.Text = "";
        Svc.Enhance = ScreenshotStore.Current.VoiceRecEnhance;
        Svc.Start();
    }

    private void OnPause(object sender, RoutedEventArgs e) => Svc.TogglePause();

    private void OnMonitor(object sender, RoutedEventArgs e) => Svc.ToggleMonitor();

    private void OnStop(object sender, RoutedEventArgs e)
    {
        Svc.Enhance = ScreenshotStore.Current.VoiceRecEnhance;
        Svc.StopAndSave();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Svc.Cancel();
        if (_status != null)
            _status.Text = "Отменено";
        RefreshUi();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (Svc.State is VoiceRecState.Recording or VoiceRecState.Paused)
            Svc.Cancel();
        Close();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        string dir = VoiceRecService.ResolveOutputDir();
        string? file = Svc.LastSavedPath;
        try
        {
            if (!string.IsNullOrEmpty(file) && File.Exists(file))
                Process.Start("explorer.exe", "/select,\"" + file + "\"");
            else
            {
                Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", dir);
            }
        }
        catch { /* ignore */ }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            Svc.TogglePause();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (Svc.State is VoiceRecState.Recording or VoiceRecState.Paused)
                Svc.StopAndSave();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (Svc.State is VoiceRecState.Recording or VoiceRecState.Paused)
                Svc.Cancel();
            else
                Close();
            e.Handled = true;
        }
        else if (e.Key == Key.M)
        {
            Svc.ToggleMonitor();
            e.Handled = true;
        }
    }

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
}
