using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Zwmenu.Services;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Zwmenu.Overlay;

/// <summary>Тёмный просмотр видео. Картинка идёт через аппаратный декодер, окно не ждёт разбор всего файла.</summary>
public sealed class VideoWindow : Window
{
    static VideoWindow? _open;

    public static bool IsOpen => _open != null;

    readonly HwndHostSurface _surface;
    readonly MediaPlayer? _player;
    readonly TextBlock _title;
    readonly TextBlock _place;
    readonly TextBlock _hint;
    readonly TextBlock _timeNow;
    readonly TextBlock _timeEnd;
    readonly TextBlock _empty;
    readonly Slider _seek;
    readonly Slider _volume;
    readonly Button _play;
    readonly Button _mute;
    readonly Button _speed;
    readonly Button _loop;
    readonly Button _auto;
    readonly Button _fullBtn;
    readonly Border _bar;
    readonly Border _more;
    readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly List<string> _files = new();

    Media? _media;
    string _path = "";
    string _pending = "";
    int _index = -1;
    int _folderGen;
    bool _dragSeek;
    bool _fromPlayer;
    bool _loopOn = true;
    bool _autoOn;
    bool _full;
    bool _ready;
    bool _alive = true;
    bool _playing;
    bool _muted;
    bool _moreOpen;
    bool _soft;
    int _playGen;
    long _length;
    long _pos;
    double _rate = 1;
    DateTime _moved = DateTime.UtcNow;
    WindowStyle _styleBefore = WindowStyle.SingleBorderWindow;
    WindowState _stateBefore = WindowState.Normal;

    static readonly double[] Rates = { 0.75, 1, 1.25, 1.5, 2 };

    VideoWindow()
    {
        Title = "Видео";
        Width = 1180;
        Height = 700;
        MinWidth = 640;
        MinHeight = 420;
        Background = TemplateTheme.Brush(TemplateTheme.BgDeep);
        Foreground = TemplateTheme.Brush(TemplateTheme.Fg);
        FontFamily = new FontFamily("Segoe UI");
        WindowStartupLocation = WindowStartupLocation.Manual;
        AllowDrop = true;
        TemplateTheme.StyleWindow(this);
        var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        Width = Math.Max(MinWidth, Math.Min(1180, area.Width - 48));
        Height = Math.Max(MinHeight, Math.Min(700, area.Height - 36));
        Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
        Top = area.Top + Math.Max(0, (area.Height - Height) / 2);

        _surface = new HwndHostSurface();
        _empty = new TextBlock
        {
            Text = "Открываю…",
            Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        _title = Label("", 13, TemplateTheme.Fg, true);
        _place = Label("", 12, TemplateTheme.FgDim);
        _hint = Label("", 12, TemplateTheme.AccentText);
        _timeNow = Label("0:00", 12, TemplateTheme.Fg);
        _timeEnd = Label("0:00", 12, TemplateTheme.FgDim);
        _seek = MakeSeek();
        _volume = MakeVolume();

        _play = IconBtn("▶", TogglePlay, "Пуск и пауза. Пробел тоже", 44, 36, 16);
        _mute = IconBtn("🔊", ToggleMute, "Звук вкл и выкл. Клавиша M", 36, 32, 14);
        _speed = TextBtn("1×", CycleSpeed, "Скорость воспроизведения", 44);
        _loop = TextBtn("↻", ToggleLoop, "Крутить этот ролик снова", 36);
        _auto = TextBtn("⏭", ToggleAuto, "После конца открыть следующий файл", 36);
        _fullBtn = IconBtn("⛶", ToggleFull, "На весь экран. F или двойной щелчок", 36, 32, 14);

        var transport = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        transport.Children.Add(IconBtn("⏮", () => Step(-1), "Предыдущий файл в папке", 36, 32, 14));
        transport.Children.Add(IconBtn("⏪", () => SeekBy(-5), "На 5 секунд назад", 36, 32, 14));
        transport.Children.Add(_play);
        transport.Children.Add(IconBtn("⏩", () => SeekBy(5), "На 5 секунд вперёд", 36, 32, 14));
        transport.Children.Add(IconBtn("⏭", () => Step(1), "Следующий файл в папке", 36, 32, 14));

        var leftTools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        leftTools.Children.Add(_loop);
        leftTools.Children.Add(_auto);
        leftTools.Children.Add(_speed);
        leftTools.Children.Add(TextBtn("Aa", CycleSpu, "Субтитры, если есть в файле", 36));
        leftTools.Children.Add(TextBtn("♪", CycleAudio, "Другая звуковая дорожка", 36));

        var rightTools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        rightTools.Children.Add(_mute);
        rightTools.Children.Add(_volume);
        rightTools.Children.Add(_fullBtn);
        rightTools.Children.Add(TextBtn("…", ToggleMore, "Ещё действия", 36));

        var mid = new Grid { Margin = new Thickness(10, 0, 10, 8) };
        mid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(leftTools, 0);
        Grid.SetColumn(transport, 1);
        Grid.SetColumn(rightTools, 2);
        rightTools.HorizontalAlignment = HorizontalAlignment.Right;
        mid.Children.Add(leftTools);
        mid.Children.Add(transport);
        mid.Children.Add(rightTools);

        var seekRow = new DockPanel { Margin = new Thickness(14, 8, 14, 0), LastChildFill = true };
        DockPanel.SetDock(_timeNow, Dock.Left);
        DockPanel.SetDock(_timeEnd, Dock.Right);
        _timeNow.Margin = new Thickness(0, 0, 10, 0);
        _timeEnd.Margin = new Thickness(10, 0, 0, 0);
        _timeNow.VerticalAlignment = VerticalAlignment.Center;
        _timeEnd.VerticalAlignment = VerticalAlignment.Center;
        seekRow.Children.Add(_timeNow);
        seekRow.Children.Add(_timeEnd);
        seekRow.Children.Add(_seek);

        var head = new DockPanel { Margin = new Thickness(14, 8, 14, 0) };
        DockPanel.SetDock(_place, Dock.Right);
        head.Children.Add(_place);
        head.Children.Add(_title);

        _more = BuildMore();
        _more.Visibility = Visibility.Collapsed;

        var panel = new StackPanel();
        panel.Children.Add(head);
        panel.Children.Add(_hint);
        panel.Children.Add(seekRow);
        panel.Children.Add(mid);
        panel.Children.Add(_more);
        _bar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x0A, 0x0A, 0x0C)),
            BorderBrush = TemplateTheme.Brush(TemplateTheme.Border),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = panel
        };

        var stage = new Border
        {
            Background = Brushes.Black,
            Child = _surface
        };
        var root = new Grid { Background = Brushes.Black, Focusable = true, FocusVisualStyle = null };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(stage, 0);
        Grid.SetRow(_bar, 1);
        root.Children.Add(stage);
        root.Children.Add(_empty);
        root.Children.Add(_bar);

        Content = root;
        PreviewKeyDown += OnKey;
        PreviewMouseMove += (_, _) => NoteMove();
        PreviewMouseDown += (_, _) => NoteMove();
        Drop += OnDrop;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        PreviewMouseWheel += (_, e) =>
        {
            _volume.Value = Math.Clamp(_volume.Value + (e.Delta > 0 ? 5 : -5), 0, 100);
            e.Handled = true;
            NoteMove();
        };
        MouseLeftButtonDown += OnSurfaceClick;
        _idle.Tick += (_, _) => HideBarIfIdle();
        _idle.Start();
        MarkToggle(_loop, _loopOn);
        MarkToggle(_auto, _autoOn);

        try
        {
            _player = new MediaPlayer(VlcCore.Get());
            _player.EnableKeyInput = false;
            _player.EnableMouseInput = false;
            _player.Volume = 80;
            _player.TimeChanged += (_, e) =>
            {
                long ms = e.Time;
                Dispatcher.BeginInvoke(() => OnTime(ms));
            };
            _player.LengthChanged += (_, e) =>
            {
                long length = e.Length;
                Dispatcher.BeginInvoke(() => OnLength(length));
            };
            _player.Playing += (_, _) => Dispatcher.BeginInvoke(() => SetPlay(true));
            _player.Paused += (_, _) => Dispatcher.BeginInvoke(() => SetPlay(false));
            _player.Stopped += (_, _) => Dispatcher.BeginInvoke(() => SetPlay(false));
            _player.Muted += (_, _) => Dispatcher.BeginInvoke(RefreshMute);
            _player.Unmuted += (_, _) => Dispatcher.BeginInvoke(RefreshMute);
            _player.EncounteredError += (_, _) =>
            {
                int gen = _playGen;
                Dispatcher.BeginInvoke(() =>
                {
                    if (!_alive || gen != _playGen)
                        return;
                    if (!_soft)
                    {
                        _soft = true;
                        PlayFile(gen, software: true);
                        return;
                    }

                    Fail("Файл не открылся.");
                });
            };
            _player.EndReached += (_, _) =>
            {
                var dispatcher = Dispatcher;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(80);
                        _ = dispatcher.BeginInvoke(OnEnded);
                    }
                    catch
                    {
                        // Окно уже закрыто.
                    }
                });
            };
            Loaded += (_, _) =>
            {
                if (_player != null && _surface.Handle != IntPtr.Zero)
                    _player.Hwnd = _surface.Handle;
                _ready = true;
                if (_pending.Length > 0)
                {
                    string path = _pending;
                    _pending = "";
                    OpenPath(path);
                }
                WindowFront.Bring(this);
                Focus();
            };
            _surface.HandleReady += hwnd =>
            {
                if (_player != null)
                    _player.Hwnd = hwnd;
            };
        }
        catch (Exception ex)
        {
            _player = null;
            _empty.Text = "Проигрыватель не собрался. " + ex.Message;
        }

        SourceInitialized += (_, _) =>
        {
            int dark = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, 4);
        };
        Closed += (_, _) =>
        {
            _alive = false;
            _idle.Stop();
            try
            {
                if (_player != null)
                    _player.Hwnd = IntPtr.Zero;
            }
            catch { /* уже закрыт */ }
            try { _media?.Dispose(); } catch { /* уже закрыт */ }
            try { _player?.Dispose(); } catch { /* уже закрыт */ }
            if (ReferenceEquals(_open, this))
                _open = null;
            VlcCore.NoteClosed();
        };
    }

    public static void ShowFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        var app = Application.Current;
        if (app == null)
            return;
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(() => ShowFile(path));
            return;
        }

        if (_open == null)
        {
            _open = new VideoWindow();
            _open.Show();
        }
        _open.OpenPath(path);
        WindowFront.Bring(_open);
    }

    public static void PickAndShow()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Просмотр видео",
            Filter = VideoOpen.DialogFilter
        };
        if (dialog.ShowDialog() == true)
            ShowFile(dialog.FileName);
    }

    void OpenPath(string path)
    {
        path = Path.GetFullPath(path);
        if (_player == null)
        {
            _empty.Text = "Проигрыватель не собрался.";
            _empty.Visibility = Visibility.Visible;
            return;
        }
        if (!_ready)
        {
            _pending = path;
            _empty.Text = "Открываю…";
            _empty.Visibility = Visibility.Visible;
            return;
        }

        _path = path;
        _length = 0;
        _pos = 0;
        _fromPlayer = true;
        _seek.Value = 0;
        _fromPlayer = false;
        _timeNow.Text = "0:00";
        _timeEnd.Text = "0:00";
        _hint.Text = "";
        Title = Path.GetFileName(path);
        _title.Text = Path.GetFileName(path);
        _empty.Visibility = Visibility.Collapsed;
        int folder = ++_folderGen;
        int gen = ++_playGen;
        _soft = false;
        if (!PlayFile(gen, software: false))
            return;

        Task.Run(() =>
        {
            var files = ListFolder(path);
            Dispatcher.BeginInvoke(() =>
            {
                if (folder != _folderGen)
                    return;
                _files.Clear();
                _files.AddRange(files);
                _index = _files.FindIndex(f => f.Equals(_path, StringComparison.OrdinalIgnoreCase));
                if (_index < 0)
                    _index = 0;
                UpdateInfo();
            });
        });
    }

    bool PlayFile(int gen, bool software)
    {
        if (_player == null || gen != _playGen || _path.Length == 0)
            return false;
        if (_surface.Handle != IntPtr.Zero)
            _player.Hwnd = _surface.Handle;
        Media? media = null;
        try
        {
            media = new Media(VlcCore.Get(), _path, FromType.FromPath);
            media.AddOption(":no-video-title-show");
            media.AddOption(":file-caching=200");
            media.AddOption(software ? ":avcodec-hw=none" : ":avcodec-hw=dxva2");
            _player.Play(media);
            _media?.Dispose();
            _media = media;
            media = null;
            _player.SetRate((float)_rate);
            _empty.Visibility = Visibility.Collapsed;
            return true;
        }
        catch (Exception ex)
        {
            try { media?.Dispose(); } catch { /* файл не открылся */ }
            Fail(ex.Message);
            return false;
        }
    }

    void Fail(string message)
    {
        _empty.Visibility = Visibility.Visible;
        _empty.Text = "Это видео не открылось. " + message;
        _title.Text = Path.GetFileName(_path);
        SetPlay(false);
    }

    void OnTime(long ms)
    {
        if (!_alive || _dragSeek)
            return;
        _pos = ms;
        long length = _length;
        _fromPlayer = true;
        if (length > 0)
            _seek.Value = Math.Clamp(ms / (double)length, 0, 1);
        _fromPlayer = false;
        _timeNow.Text = Clock(ms);
        _timeEnd.Text = Clock(length);
    }

    void OnLength(long length)
    {
        if (!_alive || length <= 0)
            return;
        _length = length;
        OnTime(_pos);
        UpdateInfo();
    }

    void SeekTo(double fraction)
    {
        var player = _player;
        if (player == null || !_alive || _fromPlayer)
            return;
        long length = _length;
        if (length <= 0)
            return;
        long next = (long)(Math.Clamp(fraction, 0, 1) * length);
        _pos = next;
        _timeNow.Text = Clock(next);
        player.Time = next;
    }

    void SeekBy(int seconds)
    {
        var player = _player;
        if (player == null || !_alive)
            return;
        long next = _pos + seconds * 1000L;
        if (next < 0)
            next = 0;
        if (_length > 0 && next > _length)
            next = _length;
        _pos = next;
        _fromPlayer = true;
        if (_length > 0)
            _seek.Value = Math.Clamp(next / (double)_length, 0, 1);
        _fromPlayer = false;
        _timeNow.Text = Clock(next);
        player.Time = next;
        NoteMove();
    }

    void TogglePlay()
    {
        var player = _player;
        if (player == null || !_alive)
            return;
        if (_playing)
            player.Pause();
        else if (_pos > 0 && _length > 0 && _pos >= _length - 250)
            Restart();
        else
            player.Play();
        NoteMove();
    }

    void Restart()
    {
        var player = _player;
        var media = _media;
        if (player == null || media == null || !_alive)
            return;
        try
        {
            _pos = 0;
            _fromPlayer = true;
            _seek.Value = 0;
            _fromPlayer = false;
            _timeNow.Text = "0:00";
            if (_surface.Handle != IntPtr.Zero)
                player.Hwnd = _surface.Handle;
            player.Play(media);
            player.SetRate((float)_rate);
        }
        catch
        {
            SetPlay(false);
            _hint.Text = "Не удалось начать сначала.";
        }
    }

    void SetPlay(bool playing)
    {
        _playing = playing;
        _play.Content = playing ? "⏸" : "▶";
        if (playing)
            _empty.Visibility = Visibility.Collapsed;
    }

    void OnEnded()
    {
        var player = _player;
        var media = _media;
        if (!_alive || player == null || media == null)
            return;
        SetPlay(false);
        _pos = _length > 0 ? _length : _pos;
        if (_loopOn)
        {
            Restart();
            return;
        }
        if (_autoOn && _files.Count > 1)
            Step(1);
        else
            _hint.Text = "Конец. Пробел или ▶ начнёт сначала.";
    }

    void CycleSpeed()
    {
        var player = _player;
        if (player == null)
            return;
        int at = 0;
        for (int i = 0; i < Rates.Length; i++)
        {
            if (Math.Abs(Rates[i] - _rate) < 0.01)
            {
                at = (i + 1) % Rates.Length;
                break;
            }
        }
        _rate = Rates[at];
        player.SetRate((float)_rate);
        _speed.Content = _rate.ToString("0.##") + "×";
        _hint.Text = "Скорость " + _speed.Content;
        NoteMove();
    }

    void ToggleMute()
    {
        if (_player != null)
            _player.Mute = !_player.Mute;
        NoteMove();
    }

    void RefreshMute()
    {
        _muted = _player?.Mute == true;
        _mute.Content = _muted ? "🔇" : "🔊";
    }

    void ToggleLoop()
    {
        _loopOn = !_loopOn;
        if (_loopOn)
            _autoOn = false;
        MarkToggle(_loop, _loopOn);
        MarkToggle(_auto, _autoOn);
        _hint.Text = _loopOn ? "Повтор этого ролика включён" : "Повтор выключен";
        NoteMove();
    }

    void ToggleAuto()
    {
        _autoOn = !_autoOn;
        if (_autoOn)
            _loopOn = false;
        MarkToggle(_loop, _loopOn);
        MarkToggle(_auto, _autoOn);
        _hint.Text = _autoOn ? "После конца откроется следующий файл" : "Автопереход выключен";
        NoteMove();
    }

    void MarkToggle(Button button, bool on)
    {
        button.Background = TemplateTheme.Brush(on ? TemplateTheme.AccentSoft : TemplateTheme.BtnBg);
        button.BorderBrush = TemplateTheme.Brush(on ? TemplateTheme.AccentText : TemplateTheme.Border);
        button.Foreground = TemplateTheme.Brush(on ? TemplateTheme.AccentText : TemplateTheme.Fg);
    }

    void CycleSpu()
    {
        if (_player == null)
            return;
        var tracks = _player.SpuDescription;
        if (tracks == null || tracks.Length == 0)
        {
            _hint.Text = "Субтитров в файле нет";
            return;
        }
        int current = _player.Spu;
        int pick = tracks[0].Id;
        string name = tracks[0].Name ?? "";
        for (int i = 0; i < tracks.Length; i++)
        {
            if (tracks[i].Id == current)
            {
                var next = tracks[(i + 1) % tracks.Length];
                pick = next.Id;
                name = next.Name ?? "";
                break;
            }
        }
        _player.SetSpu(pick);
        _hint.Text = "Субтитры: " + name;
        NoteMove();
    }

    void CycleAudio()
    {
        if (_player == null)
            return;
        var tracks = _player.AudioTrackDescription;
        if (tracks == null || tracks.Length == 0)
        {
            _hint.Text = "Другой дорожки нет";
            return;
        }
        int current = _player.AudioTrack;
        int pick = tracks[0].Id;
        string name = tracks[0].Name ?? "";
        for (int i = 0; i < tracks.Length; i++)
        {
            if (tracks[i].Id == current)
            {
                var next = tracks[(i + 1) % tracks.Length];
                pick = next.Id;
                name = next.Name ?? "";
                break;
            }
        }
        _player.SetAudioTrack(pick);
        _hint.Text = "Звук: " + name;
        NoteMove();
    }

    void Snapshot()
    {
        if (_player == null || _path.Length == 0)
            return;
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (string.IsNullOrEmpty(dir))
                return;
            string name = Path.GetFileNameWithoutExtension(_path);
            string file = Path.Combine(dir, name + " кадр.png");
            int n = 2;
            while (File.Exists(file) && n < 50)
            {
                file = Path.Combine(dir, name + " кадр " + n + ".png");
                n++;
            }
            if (_player.TakeSnapshot(0, file, 0, 0))
                _hint.Text = "Кадр: " + Path.GetFileName(file);
            else
                _hint.Text = "Кадр не сохранился";
        }
        catch (Exception ex)
        {
            _hint.Text = ex.Message;
        }
        NoteMove();
    }

    void ToggleFull()
    {
        if (_full)
        {
            WindowStyle = _styleBefore;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _stateBefore;
            _full = false;
            _fullBtn.Content = "⛶";
            _bar.Visibility = Visibility.Visible;
        }
        else
        {
            _styleBefore = WindowStyle;
            _stateBefore = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            _full = true;
            _fullBtn.Content = "🗗";
            NoteMove();
        }
        UpdateInfo();
    }

    void ToggleMore()
    {
        _moreOpen = !_moreOpen;
        _more.Visibility = _moreOpen ? Visibility.Visible : Visibility.Collapsed;
        NoteMove();
    }

    void Step(int delta)
    {
        if (_files.Count == 0)
            return;
        _index = (_index + delta + _files.Count) % _files.Count;
        OpenPath(_files[_index]);
        NoteMove();
    }

    void ShowFolder()
    {
        if (_path.Length == 0 || !File.Exists(_path))
            return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select,\"" + _path + "\"",
                UseShellExecute = true
            });
        }
        catch
        {
            // Проводник не обязателен для просмотра.
        }
    }

    void OnSurfaceClick(object sender, MouseButtonEventArgs e)
    {
        NoteMove();
        if (e.OriginalSource is DependencyObject source)
        {
            for (var walk = source; walk != null; walk = VisualTreeHelper.GetParent(walk))
            {
                if (ReferenceEquals(walk, _bar) || walk is Button || walk is Slider || walk is Thumb)
                    return;
            }
        }
        if (e.ClickCount == 2)
        {
            ToggleFull();
            e.Handled = true;
            return;
        }
        if (e.ClickCount == 1 && e.ChangedButton == MouseButton.Left)
        {
            TogglePlay();
            e.Handled = true;
        }
        Focus();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        NoteMove();
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (Environment.TickCount64 < EdgeCapture.QuietEscapeUntil)
                return;
            if (_moreOpen)
            {
                ToggleMore();
                return;
            }
            if (_full)
                ToggleFull();
            else
                Close();
            return;
        }
        if (e.Key == Key.Space)
        {
            TogglePlay();
            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                Step(-1);
            else
                SeekBy(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -30 : -5);
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                Step(1);
            else
                SeekBy(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 30 : 5);
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            _volume.Value = Math.Min(100, _volume.Value + 5);
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            _volume.Value = Math.Max(0, _volume.Value - 5);
            e.Handled = true;
        }
        else if (e.Key == Key.F || e.Key == Key.F11)
        {
            ToggleFull();
            e.Handled = true;
        }
        else if (e.Key == Key.M)
        {
            ToggleMute();
            e.Handled = true;
        }
        else if (e.Key == Key.L)
        {
            ToggleLoop();
            e.Handled = true;
        }
        else if (e.Key == Key.Home)
        {
            SeekTo(0);
            e.Handled = true;
        }
        else if (e.Key == Key.End && _length > 0)
        {
            SeekTo(0.999);
            e.Handled = true;
        }
        else if (e.Key == Key.PageUp)
        {
            Step(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.PageDown)
        {
            Step(1);
            e.Handled = true;
        }
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
            return;
        foreach (string file in files)
        {
            if (VideoOpen.IsVideoPath(file))
            {
                OpenPath(file);
                return;
            }
        }
    }

    void NoteMove() => _moved = DateTime.UtcNow;

    void HideBarIfIdle()
    {
        if (!_full)
        {
            _bar.Visibility = Visibility.Visible;
            return;
        }
        if ((DateTime.UtcNow - _moved).TotalSeconds < 2.2 || _dragSeek || _moreOpen)
            _bar.Visibility = Visibility.Visible;
        else
            _bar.Visibility = Visibility.Collapsed;
    }

    void UpdateInfo()
    {
        if (_path.Length == 0)
            return;
        _title.Text = Path.GetFileName(_path);
        _place.Text = _files.Count == 0
            ? ""
            : (_index + 1) + " / " + _files.Count + (_full ? "  ·  весь экран" : "");
    }

    Border BuildMore()
    {
        var row = new WrapPanel { Margin = new Thickness(10, 0, 10, 10) };
        row.Children.Add(TextBtn("Кадр", Snapshot, "Сохранить текущий кадр рядом с файлом", 70));
        row.Children.Add(TextBtn("Папка", ShowFolder, "Открыть папку и показать этот файл", 70));
        row.Children.Add(TextBtn("Открывать такие", () => VideoOpen.OfferDefault(_path), "Предложить Zwmenu для таких файлов", 120));
        return new Border { Child = row };
    }

    Slider MakeSeek()
    {
        var seek = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Height = 22,
            VerticalAlignment = VerticalAlignment.Center,
            IsMoveToPointEnabled = true,
            Focusable = false
        };
        seek.PreviewMouseLeftButtonDown += (_, _) => _dragSeek = true;
        seek.PreviewMouseLeftButtonUp += (_, _) =>
        {
            _dragSeek = false;
            SeekTo(seek.Value);
        };
        seek.ValueChanged += (_, _) =>
        {
            if (_dragSeek)
                _timeNow.Text = Clock((long)(seek.Value * Math.Max(_length, 0)));
        };
        return seek;
    }

    Slider MakeVolume()
    {
        var volume = new Slider
        {
            Width = 84,
            Minimum = 0,
            Maximum = 100,
            Value = 80,
            Margin = new Thickness(4, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsMoveToPointEnabled = true,
            Focusable = false
        };
        TemplateTheme.Hint(volume, "Громкость. Колесо мыши тоже меняет");
        volume.ValueChanged += (_, _) =>
        {
            if (_player != null)
                _player.Volume = (int)volume.Value;
            if (_muted && volume.Value > 0)
                _player!.Mute = false;
        };
        return volume;
    }

    static TextBlock Label(string text, double size, Color color, bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = TemplateTheme.Brush(color),
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center
    };

    static Button IconBtn(string glyph, Action click, string hint, double width, double height, double size)
        => MakeBtn(glyph, click, hint, width, height, size);

    static Button TextBtn(string text, Action click, string hint, double width)
        => MakeBtn(text, click, hint, width, 32, 13);

    static Button MakeBtn(string text, Action click, string hint, double width, double height, double size)
    {
        var button = new Button
        {
            Content = text,
            Width = width,
            Height = height,
            Margin = new Thickness(3, 0, 3, 0),
            Padding = new Thickness(0),
            FontSize = size,
            FontFamily = new FontFamily("Segoe UI Symbol, Segoe UI"),
            Foreground = TemplateTheme.Brush(TemplateTheme.Fg),
            Background = TemplateTheme.Brush(TemplateTheme.BtnBg),
            BorderBrush = TemplateTheme.Brush(TemplateTheme.Border),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false
        };
        button.Template = BtnTemplate();
        button.Click += (_, _) => click();
        TemplateTheme.Hint(button, hint);
        return button;
    }

    static ControlTemplate BtnTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "bd";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
        border.SetValue(Border.SnapsToDevicePixelsProperty, true);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        template.VisualTree = border;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Button.BackgroundProperty, TemplateTheme.Brush(TemplateTheme.BtnHover), "bd"));
        template.Triggers.Add(hover);
        return template;
    }

    static string Clock(long ms)
    {
        if (ms < 0)
            ms = 0;
        var time = TimeSpan.FromMilliseconds(ms);
        if (time.TotalHours >= 1)
            return ((int)time.TotalHours) + ":" + time.Minutes.ToString("00") + ":" + time.Seconds.ToString("00");
        return time.Minutes + ":" + time.Seconds.ToString("00");
    }

    static List<string> ListFolder(string path)
    {
        var list = new List<string>();
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(dir))
                {
                    if (VideoOpen.IsVideoPath(file))
                        list.Add(file);
                }
            }
            catch
            {
                // Папка без прав: остаётся один открытый файл.
            }
        }
        if (list.Count == 0 || !list.Exists(f => f.Equals(path, StringComparison.OrdinalIgnoreCase)))
            list.Add(path);
        list.Sort(CompareNames);
        const int cap = 400;
        if (list.Count <= cap)
            return list;
        int at = list.FindIndex(f => f.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (at < 0)
            at = 0;
        int start = Math.Clamp(at - cap / 2, 0, list.Count - cap);
        return list.GetRange(start, cap);
    }

    static int CompareNames(string a, string b)
    {
        string left = Path.GetFileName(a);
        string right = Path.GetFileName(b);
        int i = 0;
        int j = 0;
        while (i < left.Length && j < right.Length)
        {
            if (char.IsDigit(left[i]) && char.IsDigit(right[j]))
            {
                int i0 = i;
                int j0 = j;
                while (i < left.Length && char.IsDigit(left[i]))
                    i++;
                while (j < right.Length && char.IsDigit(right[j]))
                    j++;
                int cmp = (i - i0).CompareTo(j - j0);
                if (cmp == 0)
                    cmp = string.Compare(left, i0, right, j0, i - i0, StringComparison.Ordinal);
                if (cmp != 0)
                    return cmp;
            }
            else
            {
                int cmp = char.ToUpperInvariant(left[i]).CompareTo(char.ToUpperInvariant(right[j]));
                if (cmp != 0)
                    return cmp;
                i++;
                j++;
            }
        }
        return left.Length.CompareTo(right.Length);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    sealed class HwndHostSurface : HwndHost
    {
        public event Action<IntPtr>? HandleReady;
        public new IntPtr Handle { get; private set; }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            const int wsChild = 0x40000000;
            const int wsVisible = 0x10000000;
            const int wsClipChildren = 0x02000000;
            const int wsClipSiblings = 0x04000000;
            IntPtr hwnd = CreateWindowEx(
                0,
                "static",
                "",
                wsChild | wsVisible | wsClipChildren | wsClipSiblings,
                0, 0, 16, 16,
                hwndParent.Handle,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);
            Handle = hwnd;
            HandleReady?.Invoke(hwnd);
            return new HandleRef(this, hwnd);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            DestroyWindow(hwnd.Handle);
            Handle = IntPtr.Zero;
        }

        protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int wmEraseBkgnd = 0x0014;
            const int wmPaint = 0x000F;
            if (msg == wmEraseBkgnd)
            {
                handled = true;
                return new IntPtr(1);
            }
            if (msg == wmPaint)
            {
                var ps = new PaintStruct();
                IntPtr dc = BeginPaint(hwnd, out ps);
                if (dc != IntPtr.Zero)
                {
                    RECT rect;
                    GetClientRect(hwnd, out rect);
                    IntPtr brush = CreateSolidBrush(0x000000);
                    FillRect(dc, ref rect, brush);
                    DeleteObject(brush);
                    EndPaint(hwnd, ref ps);
                }
                handled = true;
                return IntPtr.Zero;
            }
            return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateWindowEx(
            int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern IntPtr BeginPaint(IntPtr hwnd, out PaintStruct ps);

        [DllImport("user32.dll")]
        static extern bool EndPaint(IntPtr hwnd, ref PaintStruct ps);

        [DllImport("user32.dll")]
        static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        static extern int FillRect(IntPtr dc, ref RECT rect, IntPtr brush);

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateSolidBrush(int color);

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr obj);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PaintStruct
        {
            public IntPtr Hdc;
            public bool Erase;
            public RECT Paint;
            public bool Restore;
            public bool IncUpdate;
            public int Reserved1, Reserved2, Reserved3, Reserved4, Reserved5, Reserved6, Reserved7, Reserved8;
        }
    }
}
