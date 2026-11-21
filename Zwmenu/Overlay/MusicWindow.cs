using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Zwmenu.Services;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using ListView = System.Windows.Controls.ListView;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Zwmenu.Overlay;

/// <summary>Тёмный проигрыватель музыки. Сначала Play, теги и обложка подтягиваются рядом.</summary>
public sealed class MusicWindow : Window
{
    static MusicWindow? _open;

    public static bool IsOpen => _open != null;

    readonly MediaPlayer? _player;
    readonly Image _cover;
    readonly Border _coverFrame;
    readonly TextBlock _title;
    readonly TextBlock _artist;
    readonly TextBlock _album;
    readonly TextBlock _facts;
    readonly TextBlock _hint;
    readonly TextBlock _timeNow;
    readonly TextBlock _timeEnd;
    readonly TextBlock _empty;
    readonly Slider _seek;
    readonly Slider _volume;
    readonly Button _play;
    readonly Button _mute;
    readonly Button _loop;
    readonly Button _shuffle;
    readonly Button _speed;
    readonly ListView _list;
    readonly Border _listHost;
    readonly Border _metaPane;
    readonly ScrollViewer _metaScroll;
    readonly ColumnDefinition _metaCol;
    readonly TextBox _mTitle;
    readonly TextBox _mArtist;
    readonly TextBox _mAlbum;
    readonly TextBox _mAlbumArtist;
    readonly TextBox _mGenre;
    readonly TextBox _mYear;
    readonly TextBox _mTrack;
    readonly TextBox _mComment;
    readonly TextBox _mLyrics;
    readonly Image _metaCover;
    readonly Border _metaCoverFrame;
    readonly TextBlock _metaStatus;
    readonly Button _metaWrite;
    readonly Button _coverPick;
    readonly Button _coverClear;
    readonly List<string> _files = new();
    readonly List<string> _order = new();

    Media? _media;
    string _path = "";
    string _pending = "";
    int _index = -1;
    int _folderGen;
    int _metaGen;
    int _loopMode = 2; // 0 off, 1 one, 2 all
    bool _shuffleOn;
    bool _dragSeek;
    bool _fromPlayer;
    bool _alive = true;
    bool _playing;
    bool _muted;
    bool _metaOn;
    bool _coverDirty;
    bool _coverClearFlag;
    byte[]? _coverBytes;
    string _coverMime = "";
    string _metaFilledPath = "";
    long _length;
    long _pos;
    double _rate = 1;
    MusicNote? _note;
    BitmapSource? _coverBmp;

    static readonly double[] Rates = { 0.75, 1, 1.25, 1.5, 2 };
    static readonly string[] LoopGlyph = { "→", "①", "↻" };
    static readonly string[] LoopHint =
    {
        "Без повтора",
        "Повтор одного трека",
        "Повтор всего списка"
    };

    MusicWindow()
    {
        Title = "Музыка";
        Width = 1080;
        Height = 640;
        MinWidth = 720;
        MinHeight = 460;
        Background = TemplateTheme.Brush(TemplateTheme.BgDeep);
        Foreground = TemplateTheme.Brush(TemplateTheme.Fg);
        FontFamily = new FontFamily("Segoe UI");
        WindowStartupLocation = WindowStartupLocation.Manual;
        AllowDrop = true;
        TemplateTheme.StyleWindow(this);
        var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        Width = Math.Max(MinWidth, Math.Min(1080, area.Width - 40));
        Height = Math.Max(MinHeight, Math.Min(640, area.Height - 36));
        Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
        Top = area.Top + Math.Max(0, (area.Height - Height) / 2);

        _cover = new Image
        {
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _coverFrame = new Border
        {
            Width = 280,
            Height = 280,
            CornerRadius = new CornerRadius(18),
            Background = CoverBrush(),
            BorderBrush = TemplateTheme.Brush(TemplateTheme.Border),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = _cover
        };
        _title = Big("", 26, TemplateTheme.Fg, true);
        _artist = Big("", 16, TemplateTheme.AccentText);
        _album = Big("", 13, TemplateTheme.FgDim);
        _facts = Big("", 12, TemplateTheme.FgDim);
        _hint = Big("", 12, TemplateTheme.AccentText);
        _hint.Margin = new Thickness(0, 8, 0, 0);
        _timeNow = Big("0:00", 12, TemplateTheme.Fg);
        _timeEnd = Big("0:00", 12, TemplateTheme.FgDim);
        _empty = Big("Открываю…", 15, TemplateTheme.FgDim);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _seek = MakeSeek();
        _volume = MakeVolume();
        _play = Glyph("▶", TogglePlay, "Пуск и пауза. Пробел", 48, 40, 18);
        _mute = Glyph("🔊", ToggleMute, "Звук. Клавиша M", 36, 32, 14);
        _loop = Glyph(LoopGlyph[_loopMode], CycleLoop, LoopHint[_loopMode], 36, 32, 14);
        _shuffle = Glyph("⇄", ToggleShuffle, "Случайный порядок", 36, 32, 14);
        _speed = TextBtn("1×", CycleSpeed, "Скорость", 44);

        _list = new ListView
        {
            Background = TemplateTheme.Brush(TemplateTheme.ListBg),
            BorderBrush = TemplateTheme.Brush(TemplateTheme.Border),
            BorderThickness = new Thickness(1),
            Foreground = TemplateTheme.Brush(TemplateTheme.Fg),
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 0)
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Hidden);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is TrackRow row && !row.Path.Equals(_path, StringComparison.OrdinalIgnoreCase))
                OpenPath(row.Path);
        };

        _mTitle = Field();
        _mArtist = Field();
        _mAlbum = Field();
        _mAlbumArtist = Field();
        _mGenre = Field();
        _mYear = Field();
        _mTrack = Field();
        _mComment = Field();
        _mLyrics = Field(true);
        _metaCover = new Image { Stretch = Stretch.UniformToFill };
        _metaCoverFrame = new Border
        {
            Width = 120,
            Height = 120,
            CornerRadius = new CornerRadius(10),
            Background = TemplateTheme.Brush(TemplateTheme.BgDeep),
            BorderBrush = TemplateTheme.Brush(TemplateTheme.Border),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 6),
            Child = _metaCover
        };
        _metaStatus = Big("", 11, TemplateTheme.FgDim);
        _metaWrite = TextBtn("Записать", SaveMeta, "Сохранить теги без перекодирования звука", 100);
        _metaWrite.Visibility = Visibility.Collapsed;
        _coverPick = TextBtn("Обложка", PickCover, "Поставить свою картинку обложки", 90);
        _coverClear = TextBtn("Убрать", ClearCoverEdit, "Убрать обложку из файла", 80);
        _metaScroll = BuildMetaScroll();
        _metaPane = new Border
        {
            Background = TemplateTheme.Brush(TemplateTheme.BgPanel),
            BorderBrush = TemplateTheme.Brush(TemplateTheme.Border),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Child = _metaScroll,
            Visibility = Visibility.Collapsed
        };

        var now = new StackPanel { Margin = new Thickness(28, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center };
        now.Children.Add(_title);
        now.Children.Add(_artist);
        now.Children.Add(_album);
        now.Children.Add(_facts);
        now.Children.Add(_hint);

        var hero = new DockPanel { Margin = new Thickness(22, 22, 12, 8) };
        DockPanel.SetDock(_coverFrame, Dock.Left);
        hero.Children.Add(_coverFrame);
        hero.Children.Add(now);

        var transport = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 8)
        };
        transport.Children.Add(Glyph("⏮", () => Step(-1), "Предыдущий трек", 36, 32, 14));
        transport.Children.Add(Glyph("⏪", () => SeekBy(-5), "На 5 секунд назад", 36, 32, 14));
        transport.Children.Add(_play);
        transport.Children.Add(Glyph("⏩", () => SeekBy(5), "На 5 секунд вперёд", 36, 32, 14));
        transport.Children.Add(Glyph("⏭", () => Step(1), "Следующий трек", 36, 32, 14));

        var tools = new DockPanel { Margin = new Thickness(16, 0, 16, 10) };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(_loop);
        left.Children.Add(_shuffle);
        left.Children.Add(_speed);
        left.Children.Add(TextBtn("♬", ToggleMeta, "Сведения и правка тегов", 36));
        left.Children.Add(TextBtn("📁", ShowFolder, "Папка с этим файлом", 36));
        left.Children.Add(TextBtn("↗", () => MusicOpen.OfferDefault(_path), "Открывать такие через Zwmenu", 36));
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(_mute);
        right.Children.Add(_volume);
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        tools.Children.Add(left);
        tools.Children.Add(right);

        var seekRow = new DockPanel { Margin = new Thickness(18, 0, 18, 0), LastChildFill = true };
        DockPanel.SetDock(_timeNow, Dock.Left);
        DockPanel.SetDock(_timeEnd, Dock.Right);
        _timeNow.Margin = new Thickness(0, 0, 10, 0);
        _timeEnd.Margin = new Thickness(10, 0, 0, 0);
        seekRow.Children.Add(_timeNow);
        seekRow.Children.Add(_timeEnd);
        seekRow.Children.Add(_seek);

        var stage = new DockPanel();
        DockPanel.SetDock(tools, Dock.Bottom);
        DockPanel.SetDock(transport, Dock.Bottom);
        DockPanel.SetDock(seekRow, Dock.Bottom);
        stage.Children.Add(tools);
        stage.Children.Add(transport);
        stage.Children.Add(seekRow);
        stage.Children.Add(hero);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        _metaCol = new ColumnDefinition { Width = new GridLength(0) };
        body.ColumnDefinitions.Add(_metaCol);
        _listHost = new Border
        {
            Background = TemplateTheme.Brush(TemplateTheme.BgPanel),
            BorderBrush = TemplateTheme.Brush(TemplateTheme.Border),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Padding = new Thickness(8),
            Child = _list
        };
        Grid.SetColumn(stage, 0);
        Grid.SetColumn(_listHost, 1);
        Grid.SetColumn(_metaPane, 2);
        body.Children.Add(stage);
        body.Children.Add(_listHost);
        body.Children.Add(_metaPane);

        var root = new Grid { Background = Atmosphere() };
        root.Children.Add(body);
        root.Children.Add(_empty);
        Content = root;

        PreviewKeyDown += OnKey;
        Drop += OnDrop;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        PreviewMouseWheel += OnWheel;
        Mark(_loop, true);
        Mark(_shuffle, false);

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
            _player.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() => Fail("Файл не открылся."));
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
                    catch { /* окно закрыто */ }
                });
            };
            Loaded += (_, _) =>
            {
                if (_pending.Length > 0)
                {
                    string path = _pending;
                    _pending = "";
                    OpenPath(path);
                }
                WindowFront.Bring(this);
                Focus();
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
            try { _media?.Dispose(); } catch { /* уже */ }
            try { _player?.Dispose(); } catch { /* уже */ }
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
            _open = new MusicWindow();
            _open.Show();
        }
        _open.OpenPath(path);
        WindowFront.Bring(_open);
    }

    public static void PickAndShow()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Музыка",
            Filter = MusicOpen.DialogFilter
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
        if (!IsLoaded)
        {
            _pending = path;
            return;
        }

        _path = path;
        _length = 0;
        _pos = 0;
        _note = null;
        _coverBmp = null;
        _metaFilledPath = "";
        _fromPlayer = true;
        _seek.Value = 0;
        _fromPlayer = false;
        _timeNow.Text = "0:00";
        _timeEnd.Text = "0:00";
        _hint.Text = "";
        _title.Text = Path.GetFileNameWithoutExtension(path);
        _artist.Text = "";
        _album.Text = "";
        _facts.Text = "";
        _cover.Source = null;
        _metaCover.Source = null;
        _coverFrame.Background = CoverBrush();
        Title = Path.GetFileName(path);
        _empty.Visibility = Visibility.Collapsed;
        int folder = ++_folderGen;
        int meta = ++_metaGen;

        Media? media = null;
        try
        {
            media = new Media(VlcCore.Get(), path, FromType.FromPath);
            media.AddOption(":no-video");
            media.AddOption(":file-caching=150");
            _player.Play(media);
            _media?.Dispose();
            _media = media;
            media = null;
            _player.SetRate((float)_rate);
        }
        catch (Exception ex)
        {
            try { media?.Dispose(); } catch { /* */ }
            Fail(ex.Message);
            return;
        }

        Task.Run(() =>
        {
            var files = ListFolder(path);
            var note = MusicMeta.Read(path);
            byte[]? coverBytes = note.Cover;
            Dispatcher.BeginInvoke(() =>
            {
                if (!_alive)
                    return;
                if (folder == _folderGen)
                {
                    _files.Clear();
                    _files.AddRange(files);
                    RebuildOrder(keepPath: path);
                    FillList();
                }
                if (meta == _metaGen)
                {
                    // Декод картинки только на UI-потоке, иначе WPF часто отдаёт null.
                    var cover = MusicMeta.CoverImage(coverBytes, 320);
                    ApplyNote(note, cover);
                }
            });
        });
    }

    void ApplyNote(MusicNote note, BitmapSource? cover)
    {
        _note = note;
        _coverBmp = cover;
        if (!string.IsNullOrWhiteSpace(note.Title))
            _title.Text = note.Title;
        _artist.Text = string.IsNullOrWhiteSpace(note.Artist) ? "Неизвестный исполнитель" : note.Artist;
        _album.Text = string.IsNullOrWhiteSpace(note.Album) ? Path.GetFileName(Path.GetDirectoryName(note.Path) ?? "") : note.Album;
        _facts.Text = note.Facts;
        if (cover != null)
        {
            _cover.Source = cover;
            _coverFrame.Background = Brushes.Black;
        }
        else
        {
            _cover.Source = null;
            _coverFrame.Background = CoverBrush(note.Title + note.Artist);
        }
        if (_metaOn)
            FillMetaFields(note, force: true);
        Title = _title.Text;
    }

    void Fail(string message)
    {
        _empty.Visibility = Visibility.Visible;
        _empty.Text = "Этот трек не открылся. " + message;
        SetPlay(false);
    }

    void OnTime(long ms)
    {
        if (!_alive || _dragSeek)
            return;
        _pos = ms;
        _fromPlayer = true;
        if (_length > 0)
            _seek.Value = Math.Clamp(ms / (double)_length, 0, 1);
        _fromPlayer = false;
        _timeNow.Text = Clock(ms);
        _timeEnd.Text = Clock(_length);
    }

    void OnLength(long length)
    {
        if (!_alive || length <= 0)
            return;
        _length = length;
        OnTime(_pos);
    }

    void SeekTo(double fraction)
    {
        var player = _player;
        if (player == null || !_alive || _fromPlayer || _length <= 0)
            return;
        long next = (long)(Math.Clamp(fraction, 0, 1) * _length);
        _pos = next;
        _timeNow.Text = Clock(next);
        player.Time = next;
    }

    void SeekBy(int seconds)
    {
        var player = _player;
        if (player == null || !_alive)
            return;
        long next = Math.Clamp(_pos + seconds * 1000L, 0, Math.Max(_length, 0));
        _pos = next;
        _fromPlayer = true;
        if (_length > 0)
            _seek.Value = Math.Clamp(next / (double)_length, 0, 1);
        _fromPlayer = false;
        _timeNow.Text = Clock(next);
        player.Time = next;
    }

    void TogglePlay()
    {
        var player = _player;
        if (player == null || !_alive)
            return;
        if (_playing)
            player.Pause();
        else if (_length > 0 && _pos >= _length - 250)
            Restart();
        else
            player.Play();
    }

    void Restart()
    {
        var player = _player;
        var media = _media;
        if (player == null || media == null)
            return;
        try
        {
            _pos = 0;
            _fromPlayer = true;
            _seek.Value = 0;
            _fromPlayer = false;
            player.Play(media);
            player.SetRate((float)_rate);
        }
        catch
        {
            SetPlay(false);
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
        if (!_alive || _player == null)
            return;
        SetPlay(false);
        if (_loopMode == 1)
        {
            Restart();
            return;
        }
        if (_loopMode == 2 || _shuffleOn)
        {
            Step(1);
            return;
        }
        _hint.Text = "Конец. Пробел начнёт сначала.";
    }

    void Step(int delta)
    {
        if (_order.Count == 0)
            return;
        int at = _order.FindIndex(f => f.Equals(_path, StringComparison.OrdinalIgnoreCase));
        if (at < 0)
            at = 0;
        at = (at + delta + _order.Count) % _order.Count;
        OpenPath(_order[at]);
    }

    void CycleLoop()
    {
        _loopMode = (_loopMode + 1) % 3;
        _loop.Content = LoopGlyph[_loopMode];
        TemplateTheme.Hint(_loop, LoopHint[_loopMode]);
        Mark(_loop, _loopMode != 0);
        _hint.Text = LoopHint[_loopMode];
    }

    void ToggleShuffle()
    {
        _shuffleOn = !_shuffleOn;
        Mark(_shuffle, _shuffleOn);
        RebuildOrder(keepPath: _path);
        FillList();
        _hint.Text = _shuffleOn ? "Случайный порядок" : "По порядку папки";
    }

    void CycleSpeed()
    {
        if (_player == null)
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
        _player.SetRate((float)_rate);
        _speed.Content = _rate.ToString("0.##") + "×";
        _hint.Text = "Скорость " + _speed.Content;
    }

    void ToggleMute()
    {
        if (_player != null)
            _player.Mute = !_player.Mute;
    }

    void RefreshMute()
    {
        _muted = _player?.Mute == true;
        _mute.Content = _muted ? "🔇" : "🔊";
    }

    void ToggleMeta()
    {
        _metaOn = !_metaOn;
        if (_metaOn)
        {
            // Сначала просто открыть колонку, поля не перечитывать при каждом клике.
            _metaPane.Visibility = Visibility.Visible;
            _metaCol.Width = new GridLength(280);
            if (_note == null && _path.Length > 0)
                LoadMeta();
            else if (_note != null && _metaFilledPath != _path)
                FillMetaFields(_note, force: true);
        }
        else
        {
            _metaCol.Width = new GridLength(0);
            _metaPane.Visibility = Visibility.Collapsed;
        }
    }

    void LoadMeta()
    {
        string path = _path;
        int gen = ++_metaGen;
        Task.Run(() =>
        {
            var note = MusicMeta.Read(path);
            byte[]? coverBytes = note.Cover;
            Dispatcher.BeginInvoke(() =>
            {
                if (!_alive || gen != _metaGen)
                    return;
                ApplyNote(note, MusicMeta.CoverImage(coverBytes, 320));
            });
        });
    }

    void FillMetaFields(MusicNote note, bool force = false)
    {
        if (!force && _metaFilledPath == _path && !_coverDirty)
            return;
        _mTitle.Text = note.Title;
        _mArtist.Text = note.Artist;
        _mAlbum.Text = note.Album;
        _mAlbumArtist.Text = note.AlbumArtist;
        _mGenre.Text = note.Genre;
        _mYear.Text = note.Year;
        _mTrack.Text = note.Track;
        _mComment.Text = note.Comment;
        _mLyrics.Text = note.Lyrics;
        _coverDirty = false;
        _coverClearFlag = false;
        _coverBytes = note.Cover;
        _coverMime = note.CoverMime;
        // Та же картинка, что слева. Без повторного декода.
        _metaCover.Source = _coverBmp;
        _metaCoverFrame.Background = _coverBmp == null
            ? TemplateTheme.Brush(TemplateTheme.BgDeep)
            : Brushes.Black;
        bool can = note.CanWrite;
        _metaWrite.Visibility = can ? Visibility.Visible : Visibility.Collapsed;
        _coverPick.Visibility = can ? Visibility.Visible : Visibility.Collapsed;
        _coverClear.Visibility = can ? Visibility.Visible : Visibility.Collapsed;
        _metaStatus.Text = can
            ? "Звук не пережимается, пишутся только теги."
            : "Этот формат только для чтения.";
        _metaFilledPath = _path;
    }

    void ShowMetaCover(byte[]? bytes)
    {
        // Только после выбора новой обложки вручную.
        var image = bytes == null ? null : MusicMeta.CoverImage(bytes, 160);
        _coverBmp = image;
        _metaCover.Source = image;
        if (image != null)
            _cover.Source = image;
        _metaCoverFrame.Background = image == null
            ? TemplateTheme.Brush(TemplateTheme.BgDeep)
            : Brushes.Black;
        _coverFrame.Background = image == null
            ? CoverBrush(_mTitle.Text + _mArtist.Text)
            : Brushes.Black;
    }

    void PickCover()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Обложка",
            Filter = "Картинка|*.jpg;*.jpeg;*.png;*.webp;*.bmp|Все файлы|*.*"
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            byte[]? bytes = MusicMeta.LoadCoverFile(dialog.FileName, out string mime);
            if (bytes == null)
                return;
            _coverBytes = bytes;
            _coverMime = mime;
            _coverDirty = true;
            _coverClearFlag = false;
            ShowMetaCover(bytes);
            _metaStatus.Text = "Обложка выбрана. Нажми «Записать».";
        }
        catch (Exception ex)
        {
            _metaStatus.Text = ex.Message;
        }
    }

    void ClearCoverEdit()
    {
        _coverBytes = null;
        _coverMime = "";
        _coverDirty = true;
        _coverClearFlag = true;
        ShowMetaCover(null);
        _metaStatus.Text = "Обложка будет убрана после «Записать».";
    }

    void SaveMeta()
    {
        if (_path.Length == 0)
            return;
        var note = new MusicNote
        {
            Title = _mTitle.Text,
            Artist = _mArtist.Text,
            Album = _mAlbum.Text,
            AlbumArtist = _mAlbumArtist.Text,
            Genre = _mGenre.Text,
            Year = _mYear.Text,
            Track = _mTrack.Text,
            Comment = _mComment.Text,
            Lyrics = _mLyrics.Text,
            Cover = _coverBytes,
            CoverMime = _coverMime,
            CoverChanged = _coverDirty && !_coverClearFlag,
            ClearCover = _coverDirty && _coverClearFlag
        };
        string path = _path;
        _metaStatus.Text = "Пишу…";
        _metaWrite.IsEnabled = false;
        _coverPick.IsEnabled = false;
        _coverClear.IsEnabled = false;
        Task.Run(() =>
        {
            try
            {
                MusicMeta.Write(path, note);
                var fresh = MusicMeta.Read(path);
                byte[]? coverBytes = fresh.Cover;
                Dispatcher.BeginInvoke(() =>
                {
                    if (!_alive)
                        return;
                    _metaWrite.IsEnabled = true;
                    _coverPick.IsEnabled = true;
                    _coverClear.IsEnabled = true;
                    _metaStatus.Text = "Сохранено.";
                    if (path.Equals(_path, StringComparison.OrdinalIgnoreCase))
                        ApplyNote(fresh, MusicMeta.CoverImage(coverBytes, 320));
                    FillList();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (!_alive)
                        return;
                    _metaWrite.IsEnabled = true;
                    _coverPick.IsEnabled = true;
                    _coverClear.IsEnabled = true;
                    _metaStatus.Text = ex.Message;
                });
            }
        });
    }

    void RebuildOrder(string keepPath)
    {
        _order.Clear();
        if (_files.Count == 0)
        {
            if (keepPath.Length > 0)
                _order.Add(keepPath);
            return;
        }
        _order.AddRange(_files);
        if (_shuffleOn)
        {
            var rng = new Random(keepPath.GetHashCode() ^ Environment.TickCount);
            for (int i = _order.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (_order[i], _order[j]) = (_order[j], _order[i]);
            }
            int at = _order.FindIndex(f => f.Equals(keepPath, StringComparison.OrdinalIgnoreCase));
            if (at > 0)
            {
                (_order[0], _order[at]) = (_order[at], _order[0]);
            }
        }
        _index = _order.FindIndex(f => f.Equals(keepPath, StringComparison.OrdinalIgnoreCase));
    }

    void FillList()
    {
        var rows = new List<TrackRow>();
        var source = _shuffleOn ? _order : _files;
        for (int i = 0; i < source.Count; i++)
        {
            string file = source[i];
            string name = Path.GetFileNameWithoutExtension(file);
            rows.Add(new TrackRow
            {
                Path = file,
                Text = (i + 1).ToString().PadLeft(2) + "  " + name
            });
        }
        _list.ItemsSource = rows;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Path.Equals(_path, StringComparison.OrdinalIgnoreCase))
            {
                _list.SelectedIndex = i;
                _list.ScrollIntoView(rows[i]);
                break;
            }
        }
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
        catch { /* */ }
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox)
            return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_metaOn)
                ToggleMeta();
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
        else if (e.Key == Key.M)
        {
            ToggleMute();
            e.Handled = true;
        }
        else if (e.Key == Key.L)
        {
            CycleLoop();
            e.Handled = true;
        }
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.None)
        {
            ToggleShuffle();
            e.Handled = true;
        }
        else if (e.Key == Key.I)
        {
            ToggleMeta();
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
            if (MusicOpen.IsMusicPath(file))
            {
                OpenPath(file);
                return;
            }
        }
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        var from = e.OriginalSource as DependencyObject
            ?? Mouse.DirectlyOver as DependencyObject;

        if (IsUnder(from, _metaPane))
        {
            // Сначала крутим то, над чем курсор (поле «Текст»), иначе всю колонку сведений.
            if (!WheelScroll(InnermostScroll(from, _metaPane), e.Delta))
                WheelScroll(_metaScroll, e.Delta);
            e.Handled = true;
            return;
        }

        if (IsUnder(from, _listHost))
        {
            WheelScroll(InnermostScroll(from, _listHost) ?? FindScroll(_list), e.Delta);
            e.Handled = true;
            return;
        }

        _volume.Value = Math.Clamp(_volume.Value + (e.Delta > 0 ? 5 : -5), 0, 100);
        e.Handled = true;
    }

    static bool WheelScroll(ScrollViewer? scroll, int delta)
    {
        if (scroll == null)
            return false;
        double before = scroll.VerticalOffset;
        scroll.ScrollToVerticalOffset(before - delta);
        return Math.Abs(scroll.VerticalOffset - before) > 0.01
            || scroll.ScrollableHeight > 0;
    }

    static bool IsUnder(DependencyObject? cur, DependencyObject root)
    {
        while (cur != null)
        {
            if (ReferenceEquals(cur, root))
                return true;
            cur = ParentOf(cur);
        }
        return false;
    }

    static DependencyObject? ParentOf(DependencyObject cur)
    {
        if (cur is Visual || cur is System.Windows.Media.Media3D.Visual3D)
            return VisualTreeHelper.GetParent(cur);
        return LogicalTreeHelper.GetParent(cur);
    }

    static ScrollViewer? InnermostScroll(DependencyObject? from, DependencyObject stopAt)
    {
        ScrollViewer? found = null;
        for (var cur = from; cur != null && !ReferenceEquals(cur, stopAt); cur = ParentOf(cur))
        {
            if (cur is ScrollViewer sv)
                found = sv;
        }
        return found;
    }

    static ScrollViewer? FindScroll(DependencyObject root)
    {
        if (root is ScrollViewer self)
            return self;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindScroll(VisualTreeHelper.GetChild(root, i));
            if (found != null)
                return found;
        }
        return null;
    }

    static void KillScrollBars(FrameworkElement host)
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

    ScrollViewer BuildMetaScroll()
    {
        var stack = new StackPanel { Margin = new Thickness(12, 10, 12, 14) };
        stack.Children.Add(Big("Сведения", 14, TemplateTheme.Fg, true));
        stack.Children.Add(Label("Обложка"));
        stack.Children.Add(_metaCoverFrame);
        var coverRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        coverRow.Children.Add(_coverPick);
        coverRow.Children.Add(_coverClear);
        stack.Children.Add(coverRow);
        stack.Children.Add(Label("Название"));
        stack.Children.Add(_mTitle);
        stack.Children.Add(Label("Исполнитель"));
        stack.Children.Add(_mArtist);
        stack.Children.Add(Label("Альбом"));
        stack.Children.Add(_mAlbum);
        stack.Children.Add(Label("Альбомный исполнитель"));
        stack.Children.Add(_mAlbumArtist);
        stack.Children.Add(Label("Жанр"));
        stack.Children.Add(_mGenre);
        stack.Children.Add(Label("Год"));
        stack.Children.Add(_mYear);
        stack.Children.Add(Label("Номер"));
        stack.Children.Add(_mTrack);
        stack.Children.Add(Label("Комментарий"));
        stack.Children.Add(_mComment);
        stack.Children.Add(Label("Текст"));
        stack.Children.Add(_mLyrics);
        stack.Children.Add(_metaWrite);
        stack.Children.Add(_metaStatus);
        var scroll = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
            Focusable = false
        };
        KillScrollBars(scroll);
        KillScrollBars(_list);
        return scroll;
    }

    Slider MakeSeek()
    {
        var seek = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Height = 22,
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
            Width = 90,
            Minimum = 0,
            Maximum = 100,
            Value = 80,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsMoveToPointEnabled = true,
            Focusable = false
        };
        TemplateTheme.Hint(volume, "Громкость");
        volume.ValueChanged += (_, _) =>
        {
            if (_player != null)
                _player.Volume = (int)volume.Value;
            if (_muted && volume.Value > 0)
                _player!.Mute = false;
        };
        return volume;
    }

    static TextBlock Big(string text, double size, Color color, bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = TemplateTheme.Brush(color),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 4)
    };

    static TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
        Margin = new Thickness(0, 8, 0, 2)
    };

    TextBox Field(bool multi = false)
    {
        var box = new TextBox
        {
            Background = TemplateTheme.Brush(TemplateTheme.BgDeep),
            Foreground = TemplateTheme.Brush(TemplateTheme.Fg),
            BorderBrush = TemplateTheme.Brush(TemplateTheme.Border),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 5, 8, 5),
            FontSize = 13,
            AcceptsReturn = multi,
            TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        if (multi)
            box.MinHeight = 96;
        KillScrollBars(box);
        if (multi)
        {
            box.PreviewMouseWheel += (s, e) =>
            {
                WheelScroll(_metaScroll, e.Delta);
                e.Handled = true;
            };
        }
        return box;
    }

    static Button Glyph(string glyph, Action click, string hint, double w, double h, double size)
        => MakeBtn(glyph, click, hint, w, h, size);

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

    static void Mark(Button button, bool on)
    {
        button.Background = TemplateTheme.Brush(on ? TemplateTheme.AccentSoft : TemplateTheme.BtnBg);
        button.BorderBrush = TemplateTheme.Brush(on ? TemplateTheme.AccentText : TemplateTheme.Border);
        button.Foreground = TemplateTheme.Brush(on ? TemplateTheme.AccentText : TemplateTheme.Fg);
    }

    static Brush Atmosphere()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1)
        };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x07, 0x07, 0x08), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x10, 0x14, 0x16), 0.55));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x0A, 0x0C, 0x10), 1));
        brush.Freeze();
        return brush;
    }

    static Brush CoverBrush(string? seed = null)
    {
        int hash = string.IsNullOrEmpty(seed) ? 17 : seed.GetHashCode();
        byte a = (byte)(30 + (hash & 40));
        byte b = (byte)(50 + ((hash >> 8) & 50));
        byte c = (byte)(70 + ((hash >> 16) & 60));
        var brush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1)
        };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(a, b, c), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x12, 0x12, 0x14), 1));
        brush.Freeze();
        return brush;
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
                    if (MusicOpen.IsMusicPath(file))
                        list.Add(file);
                }
            }
            catch { /* */ }
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
        return string.Compare(left, right, StringComparison.CurrentCultureIgnoreCase);
    }

    sealed class TrackRow
    {
        public string Path { get; set; } = "";
        public string Text { get; set; } = "";
        public override string ToString() => Text;
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
