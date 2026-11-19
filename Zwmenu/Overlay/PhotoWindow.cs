using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SkiaSharp;
using Zwmenu.Services;
using Zwmenu.Thumbs;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using Pen = System.Windows.Media.Pen;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Zwmenu.Overlay;

/// <summary>
/// Тёмный просмотр картинок. Пустые пиксели рисуются клеткой.
/// Две картинки можно наложить и увидеть отличия, сведения пишутся в сам файл.
/// </summary>
public sealed class PhotoWindow : Window
{
    static PhotoWindow? _open;

    readonly PhotoCanvas _view;
    readonly TextBlock _info;
    readonly TextBlock _empty;
    readonly StackPanel _strip;
    readonly ScrollViewer _stripScroll;
    readonly StackPanel _confirm;
    readonly Border _save;
    readonly Border _pause;
    readonly DispatcherTimer _anim = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly DispatcherTimer _show = new() { Interval = TimeSpan.FromSeconds(3.2) };

    readonly List<string> _files = new();
    readonly Dictionary<string, Border> _tiles = new(StringComparer.OrdinalIgnoreCase);
    PhotoDocument? _doc;
    BitmapSource[] _frames = Array.Empty<BitmapSource>();
    int _frame;
    int _index = -1;
    int _loadGen;
    int _folderGen;
    int _turned;
    int _fileW;
    int _fileH;
    long _fileBytes;
    string _path = "";
    bool _paused;
    bool _asking;
    bool _full;
    WindowStyle _styleBefore = WindowStyle.SingleBorderWindow;
    CancellationTokenSource? _thumbs;
    readonly DispatcherTimer _cmpWait = new() { Interval = TimeSpan.FromMilliseconds(140) };
    readonly StackPanel _cmpBar;
    readonly Slider _cmpSlider;
    readonly TextBlock _cmpLegend;
    readonly Border _cmpDiff;
    readonly Border _cmpWipe;
    readonly Border _cmpMix;
    readonly ColumnDefinition _metaCol;
    readonly TextBox _metaTitle;
    readonly TextBox _metaAuthor;
    readonly TextBox _metaDesc;
    readonly TextBox _metaKeys;
    readonly TextBox _metaRights;
    readonly TextBox _metaTaken;
    readonly TextBlock _metaFacts;
    readonly TextBlock _metaStatus;
    readonly Border _metaWrite;
    SKBitmap? _cmpOther;
    string _cmpOtherName = "";
    CmpMode _cmp = CmpMode.Off;
    int _cmpGen;
    bool _cmpRefit;
    bool _metaOn;
    int _metaGen;
    enum CmpMode { Off, Diff, Wipe, Mix }
    BitmapSource? _currentThumb;
    ReadyPhoto? _prefetch;
    string _prefetchPath = "";
    int _prefetchTicket;

    PhotoWindow()
    {
        Title = "Просмотр";
        Width = 1180;
        Height = 700;
        MinWidth = 720;
        MinHeight = 460;
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

        _view = new PhotoCanvas();
        _view.Zoomed += UpdateInfo;
        _info = new TextBlock
        {
            Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
            FontSize = 12,
            Margin = new Thickness(12, 0, 12, 8),
            TextWrapping = TextWrapping.Wrap
        };
        _empty = new TextBlock
        {
            Text = "Открываю…",
            Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        _strip = new StackPanel { Orientation = Orientation.Horizontal };
        _stripScroll = new ScrollViewer
        {
            Content = _strip,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 92,
            Background = TemplateTheme.Brush(TemplateTheme.BgTitle)
        };
        _stripScroll.PreviewMouseWheel += (_, e) =>
        {
            _stripScroll.ScrollToHorizontalOffset(_stripScroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        };
        _confirm = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 16),
            Visibility = Visibility.Collapsed
        };
        var ask = new TextBlock
        {
            Text = "Удалить в корзину?",
            Foreground = TemplateTheme.Brush(TemplateTheme.Fg),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 6),
            FontSize = 13
        };
        _confirm.Children.Add(ask);
        _confirm.Children.Add(TemplateTheme.Pill("Да", ConfirmDelete, danger: true, hint: "Подтвердить удаление в корзину"));
        _confirm.Children.Add(TemplateTheme.Pill("Нет", () => Ask(false), hint: "Оставить файл"));

        var bar = new WrapPanel { Margin = new Thickness(10, 8, 10, 2) };
        bar.Children.Add(TemplateTheme.Pill("−", () => _view.ZoomAt(Center(), 1 / 1.12), hint: "Уменьшить"));
        bar.Children.Add(TemplateTheme.Pill("+", () => _view.ZoomAt(Center(), 1.12), hint: "Увеличить"));
        bar.Children.Add(TemplateTheme.Pill("Вписать", () => { _view.Fit(); UpdateInfo(); }, hint: "Показать картинку целиком"));
        bar.Children.Add(TemplateTheme.Pill("1:1", () => { _view.Actual(); UpdateInfo(); }, hint: "Один пиксель файла на один пиксель экрана"));
        bar.Children.Add(TemplateTheme.Pill("Назад", () => Step(-1), hint: "Предыдущий файл в папке"));
        bar.Children.Add(TemplateTheme.Pill("Дальше", () => Step(1), hint: "Следующий файл в папке"));
        bar.Children.Add(TemplateTheme.Pill("Поворот", Turn, hint: "Повернуть на четверть по часовой"));
        _save = TemplateTheme.Pill("Сохранить поворот", SaveTurn, hint: "Записать повёрнутую копию рядом с файлом");
        _save.Visibility = Visibility.Collapsed;
        bar.Children.Add(_save);
        bar.Children.Add(TemplateTheme.Pill("Копировать", CopyImage, hint: "Скопировать картинку"));
        bar.Children.Add(TemplateTheme.Pill("Папка", ShowFolder, hint: "Открыть папку и показать этот файл"));
        bar.Children.Add(TemplateTheme.Pill("Удалить", () => Ask(true), danger: true, hint: "Убрать файл в корзину"));
        bar.Children.Add(TemplateTheme.Pill("Показ", ToggleShow, hint: "Самой листать папку"));
        _pause = TemplateTheme.Pill("Пауза", TogglePause, hint: "Остановить и снова пустить кадры");
        _pause.Visibility = Visibility.Collapsed;
        bar.Children.Add(_pause);
        bar.Children.Add(TemplateTheme.Pill("Сравнить", StartCompare, hint: "Наложить вторую картинку и подсветить отличия"));
        bar.Children.Add(TemplateTheme.Pill("Сведения", ToggleMeta, hint: "Название, автор, дата и данные съёмки"));
        bar.Children.Add(TemplateTheme.Pill("Открывать такие", () => PhotoOpen.OfferDefault(_path), hint: "Предложить Zwmenu для файлов этого типа"));

        _cmpDiff = TemplateTheme.Pill("Отличия", () => SetCmp(CmpMode.Diff), hint: "Серое совпало, красное только эта, зелёное только вторая");
        _cmpWipe = TemplateTheme.Pill("Шторка", () => SetCmp(CmpMode.Wipe), hint: "Ползунок двигает границу между двумя картинками");
        _cmpMix = TemplateTheme.Pill("Смешать", () => SetCmp(CmpMode.Mix), hint: "Вторая картинка поверх первой, ползунок задаёт силу");
        _cmpSlider = new Slider
        {
            Width = 128,
            Minimum = 0,
            Maximum = 1,
            Value = 0.5,
            Margin = new Thickness(2, 0, 8, 6),
            VerticalAlignment = VerticalAlignment.Center,
            IsMoveToPointEnabled = true
        };
        TemplateTheme.Hint(_cmpSlider, "В отличиях это чувствительность, в шторке и смеси это доля второй картинки");
        _cmpSlider.ValueChanged += (_, _) =>
        {
            if (_cmp == CmpMode.Off)
                return;
            if (_cmp == CmpMode.Diff)
            {
                _cmpWait.Stop();
                _cmpWait.Start();
            }
            else
                _view.SetSplit(_cmpSlider.Value);
        };
        _cmpBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(10, 0, 10, 0),
            Visibility = Visibility.Collapsed
        };
        _cmpBar.Children.Add(_cmpDiff);
        _cmpBar.Children.Add(_cmpWipe);
        _cmpBar.Children.Add(_cmpMix);
        _cmpBar.Children.Add(_cmpSlider);
        _cmpBar.Children.Add(TemplateTheme.Pill("Другая", StartCompare, hint: "Выбрать другую картинку для наложения"));
        _cmpBar.Children.Add(TemplateTheme.Pill("Хватит", StopCompare, hint: "Вернуться к одной картинке"));
        _cmpLegend = new TextBlock
        {
            Margin = new Thickness(12, 0, 12, 4),
            FontSize = 12,
            Visibility = Visibility.Collapsed
        };
        _cmpLegend.Inlines.Add(new Run("серое совпало") { Foreground = TemplateTheme.Brush(TemplateTheme.FgDim) });
        _cmpLegend.Inlines.Add(new Run("    "));
        _cmpLegend.Inlines.Add(new Run("красное только эта") { Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x6A, 0x6A)) });
        _cmpLegend.Inlines.Add(new Run("    "));
        _cmpLegend.Inlines.Add(new Run("зелёное только вторая") { Foreground = new SolidColorBrush(Color.FromRgb(0x6D, 0xC0, 0x78)) });

        _metaTitle = MetaBox();
        _metaAuthor = MetaBox();
        _metaDesc = MetaBox(true);
        _metaKeys = MetaBox();
        _metaRights = MetaBox();
        _metaTaken = MetaBox();
        _metaFacts = new TextBlock
        {
            Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 6)
        };
        _metaStatus = new TextBlock
        {
            Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 8)
        };
        _metaWrite = TemplateTheme.Pill("Записать", SaveMeta, accent: true, hint: "Записать поля в файл. Пиксели не пережимаются");
        var meta = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };
        meta.Children.Add(MetaLabel("Название"));
        meta.Children.Add(_metaTitle);
        meta.Children.Add(MetaLabel("Автор"));
        meta.Children.Add(_metaAuthor);
        meta.Children.Add(MetaLabel("Описание"));
        meta.Children.Add(_metaDesc);
        meta.Children.Add(MetaLabel("Ключевые слова"));
        meta.Children.Add(_metaKeys);
        meta.Children.Add(MetaLabel("Права"));
        meta.Children.Add(_metaRights);
        meta.Children.Add(MetaLabel("Снято"));
        meta.Children.Add(_metaTaken);
        meta.Children.Add(_metaFacts);
        meta.Children.Add(_metaWrite);
        meta.Children.Add(_metaStatus);
        var metaScroll = new ScrollViewer
        {
            Content = meta,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = TemplateTheme.Brush(TemplateTheme.BgPanel)
        };
        _metaCol = new ColumnDefinition { Width = new GridLength(0) };

        var stage = new Grid();
        stage.ColumnDefinitions.Add(new ColumnDefinition());
        stage.ColumnDefinitions.Add(_metaCol);
        stage.Children.Add(_view);
        stage.Children.Add(_empty);
        stage.Children.Add(_confirm);
        Grid.SetColumn(metaScroll, 1);
        stage.Children.Add(metaScroll);

        var root = new DockPanel();
        var top = new StackPanel();
        top.Children.Add(bar);
        top.Children.Add(_cmpBar);
        top.Children.Add(_cmpLegend);
        top.Children.Add(_info);
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_stripScroll, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(_stripScroll);
        root.Children.Add(stage);
        Content = root;

        _anim.Tick += (_, _) => AdvanceFrame();
        _show.Tick += (_, _) => Step(1);
        _cmpWait.Tick += (_, _) =>
        {
            _cmpWait.Stop();
            ScheduleCompare();
        };
        PreviewKeyDown += OnKey;
        PreviewMouseWheel += OnWheel;
        Drop += OnDrop;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.XButton1)
            {
                Step(-1);
                e.Handled = true;
            }
            else if (e.ChangedButton == MouseButton.XButton2)
            {
                Step(1);
                e.Handled = true;
            }
        };
        Closed += (_, _) =>
        {
            _loadGen++;
            _folderGen++;
            _prefetchTicket++;
            _cmpGen++;
            _cmpOther?.Dispose();
            _cmpOther = null;
            _anim.Stop();
            _show.Stop();
            _thumbs?.Cancel();
            _prefetch?.Dispose();
            _prefetch = null;
            _doc?.Dispose();
            _doc = null;
            if (ReferenceEquals(_open, this))
                _open = null;
        };
        SourceInitialized += (_, _) =>
        {
            int dark = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, 4);
        };
        Loaded += (_, _) => WindowFront.Bring(this);
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
            _open = new PhotoWindow();
            _open.Show();
        }
        _open.OpenPath(path);
        WindowFront.Bring(_open);
    }

    public static void PickAndShow()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Просмотр фото",
            Filter = PhotoCodec.DialogFilter
        };
        if (dialog.ShowDialog() == true)
            ShowFile(dialog.FileName);
    }

    void OpenPath(string path)
    {
        path = Path.GetFullPath(path);
        _thumbs?.Cancel();
        int folder = ++_folderGen;
        int load = ++_loadGen;
        _prefetchTicket++;
        _prefetch?.Dispose();
        _prefetch = null;
        _prefetchPath = "";
        _anim.Stop();
        if (_doc == null)
        {
            _empty.Text = "Открываю…";
            _empty.Visibility = Visibility.Visible;
        }

        Task.Run(() =>
        {
            try
            {
                var ready = LoadReady(path);
                Dispatcher.BeginInvoke(() =>
                {
                    if (folder != _folderGen || load != _loadGen)
                    {
                        ready.Dispose();
                        return;
                    }

                    _files.Clear();
                    _files.Add(path);
                    _index = 0;
                    ApplyReady(load, path, ready);
                });

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
                    BuildStrip();
                    if (_currentThumb != null
                        && _tiles.TryGetValue(_path, out var tile)
                        && tile.Child is Image tileImage
                        && tileImage.Source == null)
                        tileImage.Source = _currentThumb;
                    StartThumbs(folder);
                    PrefetchNext(folder);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() => Fail(load, ex.Message));
            }
        });
    }

    void OpenIndex(int index)
    {
        if (_files.Count == 0)
            return;
        _index = (index + _files.Count) % _files.Count;
        string path = _files[_index];
        _path = path;
        _anim.Stop();
        Ask(false);
        int load = ++_loadGen;
        Highlight();
        if (TryTakePrefetch(path, out var ready) && ready != null)
        {
            ApplyReady(load, path, ready);
            PrefetchNext(_folderGen);
            return;
        }

        Task.Run(() =>
        {
            try
            {
                var loaded = LoadReady(path);
                Dispatcher.BeginInvoke(() =>
                {
                    if (load != _loadGen)
                    {
                        loaded.Dispose();
                        return;
                    }

                    ApplyReady(load, path, loaded);
                    PrefetchNext(_folderGen);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() => Fail(load, ex.Message));
            }
        });
    }

    void ApplyReady(int gen, string path, ReadyPhoto ready)
    {
        if (gen != _loadGen)
        {
            ready.Dispose();
            return;
        }

        _doc?.Dispose();
        _doc = ready.Doc;
        _frames = ready.Frames;
        _frame = 0;
        _turned = 0;
        _paused = false;
        _path = path;
        _fileW = ready.Doc.SourceWidth;
        _fileH = ready.Doc.SourceHeight;
        _fileBytes = ready.Bytes;
        _save.Visibility = Visibility.Collapsed;
        _pause.Visibility = ready.Doc.Animated ? Visibility.Visible : Visibility.Collapsed;
        SetPauseText();
        _empty.Visibility = Visibility.Collapsed;
        Title = Path.GetFileName(path);
        _view.SetImage(_frames.Length > 0 ? _frames[0] : null, ready.Doc.HasAlpha, refit: true);
        _currentThumb = ready.Thumb;
        if (_tiles.TryGetValue(path, out var tile) && tile.Child is Image tileImage && ready.Thumb != null)
            tileImage.Source = ready.Thumb;
        Highlight();
        UpdateInfo();
        if (ready.Doc.Animated && !_paused && _cmp == CmpMode.Off)
            ArmAnim();
        if (_cmp != CmpMode.Off)
        {
            _cmpRefit = true;
            ScheduleCompare();
        }
        if (_metaOn)
            LoadMeta();
    }

    void Fail(int gen, string message)
    {
        if (gen != _loadGen)
            return;
        _anim.Stop();
        _doc?.Dispose();
        _doc = null;
        _frames = Array.Empty<BitmapSource>();
        _currentThumb = null;
        _view.SetImage(null, false, true);
        _pause.Visibility = Visibility.Collapsed;
        _save.Visibility = Visibility.Collapsed;
        _empty.Visibility = Visibility.Visible;
        _empty.Text = "Этот файл не открылся. " + message;
        _info.Text = Path.GetFileName(_path);
    }

    void Step(int delta)
    {
        if (_files.Count == 0)
            return;
        OpenIndex(_index + delta);
    }

    void AdvanceFrame()
    {
        if (_doc == null || _frames.Length < 2 || _paused)
            return;
        _frame = (_frame + 1) % _frames.Length;
        if (_cmp == CmpMode.Off)
            _view.SetImage(_frames[_frame], _doc.HasAlpha, refit: false);
        ArmAnim();
    }

    void ArmAnim()
    {
        if (_doc == null || _doc.DelaysMs.Length == 0)
            return;
        int delay = _doc.DelaysMs[Math.Clamp(_frame, 0, _doc.DelaysMs.Length - 1)];
        _anim.Interval = TimeSpan.FromMilliseconds(Math.Clamp(delay, 20, 2000));
        _anim.Start();
    }

    void Turn()
    {
        if (_doc == null || _doc.Frames.Length == 0)
            return;
        var next = new SKBitmap[_doc.Frames.Length];
        try
        {
            for (int i = 0; i < _doc.Frames.Length; i++)
                next[i] = PhotoCodec.Rotate(_doc.Frames[i]);
        }
        catch
        {
            foreach (var frame in next)
                frame?.Dispose();
            return;
        }

        var rotated = new PhotoDocument
        {
            SourceWidth = _doc.Height,
            SourceHeight = _doc.Width,
            HasAlpha = _doc.HasAlpha,
            Animated = _doc.Animated,
            FramesCut = _doc.FramesCut,
            Kind = _doc.Kind,
            Frames = next,
            DelaysMs = _doc.DelaysMs.ToArray()
        };
        _doc.Dispose();
        _doc = rotated;
        _frames = next.Select(ToSource).ToArray();
        _turned = (_turned + 1) % 4;
        _save.Visibility = _turned == 0 ? Visibility.Collapsed : Visibility.Visible;
        _view.SetImage(_frames[_frame % _frames.Length], rotated.HasAlpha, refit: true);
        UpdateInfo();
        if (_cmp != CmpMode.Off)
        {
            _cmpRefit = true;
            ScheduleCompare();
        }
    }

    void SaveTurn()
    {
        if (_doc == null || _turned == 0 || _frames.Length == 0)
            return;
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (string.IsNullOrEmpty(dir))
                return;
            string name = Path.GetFileNameWithoutExtension(_path);
            string dest = Path.Combine(dir, name + " поворот.png");
            int n = 2;
            while (File.Exists(dest) && n < 50)
            {
                dest = Path.Combine(dir, name + " поворот " + n + ".png");
                n++;
            }
            PhotoCodec.SavePng(_doc.Frames[_frame % _doc.Frames.Length], dest);
            Toast("Поворот сохранён");
            OpenPath(dest);
        }
        catch (Exception ex)
        {
            _info.Text = "Не сохранилось. " + ex.Message;
        }
    }

    void CopyImage()
    {
        if (_doc == null || _frames.Length == 0)
            return;
        int i = _frame % _frames.Length;
        SKBitmap clone = _doc.Frames[i].Copy();
        BitmapSource shown = _frames[i];
        Task.Run(() =>
        {
            try
            {
                using (clone)
                using (var image = SKImage.FromBitmap(clone))
                using (var png = image.Encode(SKEncodedImageFormat.Png, 100))
                {
                    if (png == null)
                        return;
                    byte[] raw = png.ToArray();
                    Dispatcher.BeginInvoke(() =>
                    {
                        try
                        {
                            var data = new DataObject();
                            data.SetData(DataFormats.Bitmap, shown, true);
                            data.SetData("PNG", new MemoryStream(raw), false);
                            Clipboard.SetDataObject(data, true);
                            Toast("Скопировано");
                        }
                        catch
                        {
                            _info.Text = "Не скопировалось.";
                        }
                    });
                }
            }
            catch
            {
                Dispatcher.BeginInvoke(() => _info.Text = "Не скопировалось.");
            }
        });
    }

    void ShowFolder()
    {
        if (!File.Exists(_path))
            return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select,\"" + _path + "\"",
                UseShellExecute = true
            })?.Dispose();
        }
        catch
        {
            _info.Text = "Папка не открылась.";
        }
    }

    void Ask(bool on)
    {
        _asking = on && File.Exists(_path);
        _confirm.Visibility = _asking ? Visibility.Visible : Visibility.Collapsed;
    }

    void ConfirmDelete()
    {
        string path = _path;
        Ask(false);
        if (!File.Exists(path))
            return;
        if (!Recycle(path))
        {
            _info.Text = "Не удалилось.";
            return;
        }

        int at = _files.FindIndex(f => f.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
            _files.RemoveAt(at);
        if (_tiles.TryGetValue(path, out var tile))
        {
            _strip.Children.Remove(tile);
            _tiles.Remove(path);
        }
        if (_files.Count == 0)
        {
            _doc?.Dispose();
            _doc = null;
            _view.SetImage(null, false, true);
            _empty.Visibility = Visibility.Visible;
            _empty.Text = "В папке больше нет картинок.";
            _info.Text = "";
            Title = "Просмотр";
            return;
        }
        OpenIndex(Math.Min(at, _files.Count - 1));
    }

    void TogglePause()
    {
        if (_doc == null || !_doc.Animated)
            return;
        _paused = !_paused;
        SetPauseText();
        if (_paused)
            _anim.Stop();
        else
            ArmAnim();
    }

    void SetPauseText()
    {
        if (_pause.Child is TextBlock text)
            text.Text = _paused ? "Пуск" : "Пауза";
    }

    void ToggleShow()
    {
        if (_show.IsEnabled)
        {
            _show.Stop();
            UpdateInfo();
            return;
        }
        if (_files.Count < 2)
            return;
        _show.Start();
        UpdateInfo();
    }

    void ToggleFull()
    {
        _full = !_full;
        if (_full)
        {
            _styleBefore = WindowStyle;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = _styleBefore;
            WindowState = WindowState.Normal;
        }
    }

    void UpdateInfo()
    {
        if (_doc == null)
            return;
        int zoom = (int)Math.Round(_view.Zoom * 100);
        string alpha = _doc.HasAlpha ? "прозрачность есть" : "фон сплошной";
        string place = (_index + 1) + " из " + _files.Count;
        string extra = "";
        if (_doc.Animated)
            extra += "  ·  " + _doc.Frames.Length + " кадров";
        if (_doc.FramesCut)
            extra += "  ·  показаны первые " + _doc.Frames.Length;
        if (_turned != 0)
            extra += "  ·  повёрнуто";
        if (_show.IsEnabled)
            extra += "  ·  показ";
        if (_cmp != CmpMode.Off)
            extra += "  ·  сравнение с " + _cmpOtherName;
        _info.Text = Path.GetFileName(_path)
            + "  ·  " + _fileW + "×" + _fileH
            + "  ·  " + _doc.Kind
            + "  ·  " + SizeText(_fileBytes)
            + "  ·  " + alpha
            + "  ·  " + zoom + "%"
            + "  ·  " + place
            + extra;
    }

    void BuildStrip()
    {
        _strip.Children.Clear();
        _tiles.Clear();
        var files = _files.ToArray();
        foreach (string file in files)
        {
            var image = new Image
            {
                Width = 72,
                Height = 72,
                Stretch = Stretch.Uniform
            };
            var border = new Border
            {
                Width = 80,
                Height = 80,
                Margin = new Thickness(6, 6, 0, 6),
                Background = TemplateTheme.Brush(TemplateTheme.BgPanel),
                BorderBrush = TemplateTheme.Brush(TemplateTheme.Border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Child = image,
                Cursor = Cursors.Hand,
                ToolTip = Path.GetFileName(file)
            };
            string captured = file;
            border.MouseLeftButtonUp += (_, e) =>
            {
                int at = _files.FindIndex(f => f.Equals(captured, StringComparison.OrdinalIgnoreCase));
                if (at >= 0)
                    OpenIndex(at);
                e.Handled = true;
            };
            _tiles[file] = border;
            _strip.Children.Add(border);
        }
        Highlight();
    }

    void StartThumbs(int folder)
    {
        _thumbs?.Cancel();
        _thumbs = new CancellationTokenSource();
        var token = _thumbs.Token;
        var files = _files.ToArray();
        var thread = new Thread(() => FillThumbs(files, folder, token))
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "Zwmenu.PhotoThumbs"
        };
        thread.Start();
    }

    void FillThumbs(string[] files, int gen, CancellationToken token)
    {
        foreach (string file in files)
        {
            if (token.IsCancellationRequested || gen != _folderGen)
                return;
            try
            {
                using var thumb = PhotoCodec.Thumbnail(file, 144) ?? (PhotoWide.IsExtra(file) ? PhotoWide.Thumbnail(file, 144) : null);
                if (thumb == null)
                    continue;
                var source = ToSource(thumb);
                Dispatcher.BeginInvoke(() =>
                {
                    if (gen != _folderGen || !_tiles.TryGetValue(file, out var border))
                        return;
                    if (border.Child is Image image && image.Source == null)
                        image.Source = source;
                });
            }
            catch
            {
                // Битый файл остаётся пустой клеткой.
            }
        }
    }

    bool TryTakePrefetch(string path, out ReadyPhoto? ready)
    {
        ready = null;
        if (_prefetch == null || !path.Equals(_prefetchPath, StringComparison.OrdinalIgnoreCase))
            return false;
        ready = _prefetch;
        _prefetch = null;
        _prefetchPath = "";
        return true;
    }

    void PrefetchNext(int folder)
    {
        int next = _index + 1;
        if (next >= _files.Count)
            return;
        string path = _files[next];
        if (_prefetch != null && path.Equals(_prefetchPath, StringComparison.OrdinalIgnoreCase))
            return;
        int ticket = ++_prefetchTicket;
        Task.Run(() =>
        {
            try
            {
                var ready = LoadReady(path);
                Dispatcher.BeginInvoke(() =>
                {
                    if (ticket != _prefetchTicket || folder != _folderGen)
                    {
                        ready.Dispose();
                        return;
                    }

                    _prefetch?.Dispose();
                    _prefetch = ready;
                    _prefetchPath = path;
                });
            }
            catch
            {
                // Следующий кадр откроется обычным путём.
            }
        });
    }

    ReadyPhoto LoadReady(string path)
    {
        var doc = PhotoFiles.Open(path);
        var frames = new BitmapSource[doc.Frames.Length];
        for (int i = 0; i < doc.Frames.Length; i++)
            frames[i] = ToSource(doc.Frames[i]);
        BitmapSource? thumb = null;
        if (doc.Frames.Length > 0)
        {
            using var small = PhotoCodec.Fit(doc.Frames[0], 144);
            thumb = ToSource(small);
        }

        return new ReadyPhoto
        {
            Doc = doc,
            Frames = frames,
            Thumb = thumb,
            Bytes = new FileInfo(path).Length
        };
    }

    void Highlight()
    {
        foreach (var pair in _tiles)
        {
            bool on = pair.Key.Equals(_path, StringComparison.OrdinalIgnoreCase);
            pair.Value.BorderBrush = TemplateTheme.Brush(on ? TemplateTheme.AccentText : TemplateTheme.Border);
            pair.Value.BorderThickness = new Thickness(on ? 2 : 1);
            if (on)
                pair.Value.BringIntoView();
        }
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (_asking)
        {
            if (e.Key == Key.Enter)
            {
                ConfirmDelete();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Ask(false);
                e.Handled = true;
            }
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (Environment.TickCount64 < EdgeCapture.QuietEscapeUntil)
                return;
            if (_cmp != CmpMode.Off)
                StopCompare();
            else if (_metaOn)
                ToggleMeta();
            else if (_full)
                ToggleFull();
            else
                Close();
            return;
        }
        if (e.Key == Key.Left || e.Key == Key.PageUp || e.Key == Key.Back)
            Step(-1);
        else if (e.Key == Key.Right || e.Key == Key.PageDown || e.Key == Key.Space)
            Step(1);
        else if (e.Key == Key.Home && _files.Count > 0)
            OpenIndex(0);
        else if (e.Key == Key.End && _files.Count > 0)
            OpenIndex(_files.Count - 1);
        else if (e.Key == Key.F11)
            ToggleFull();
        else if (e.Key == Key.F)
        {
            _view.Fit();
            UpdateInfo();
        }
        else if (e.Key == Key.D1 || e.Key == Key.NumPad1)
        {
            _view.Actual();
            UpdateInfo();
        }
        else if (e.Key == Key.OemPlus || e.Key == Key.Add)
            _view.ZoomAt(Center(), 1.12);
        else if (e.Key == Key.OemMinus || e.Key == Key.Subtract)
            _view.ZoomAt(Center(), 1 / 1.12);
        else if (e.Key == Key.R)
            Turn();
        else if (e.Key == Key.P)
            TogglePause();
        else if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            CopyImage();
        else if (e.Key == Key.Delete)
            Ask(true);
        else
            return;
        e.Handled = true;
        UpdateInfo();
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_stripScroll.IsMouseOver)
            return;
        _view.ZoomAt(e.GetPosition(_view), e.Delta > 0 ? 1.12 : 1 / 1.12);
        UpdateInfo();
        e.Handled = true;
    }

    void OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
            return;
        string? image = files.FirstOrDefault(PhotoCodec.IsImagePath);
        if (image != null)
            OpenPath(image);
    }

    Point Center() => new(_view.ActualWidth / 2, _view.ActualHeight / 2);

    void Toast(string text)
    {
        var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        ScreenshotToast.Show(text, area, 2.2);
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
                    if (PhotoCodec.IsImagePath(file))
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
        int start = at - cap / 2;
        if (start < 0)
            start = 0;
        if (start + cap > list.Count)
            start = list.Count - cap;
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

    static string SizeText(long bytes)
    {
        if (bytes < 1024)
            return bytes + " Б";
        if (bytes < 1024 * 1024)
            return (bytes / 1024.0).ToString("0.#") + " КБ";
        return (bytes / 1024.0 / 1024.0).ToString("0.#") + " МБ";
    }

    static BitmapSource ToSource(SKBitmap bitmap)
    {
        SKBitmap? owned = null;
        SKBitmap src = bitmap;
        if (bitmap.ColorType != SKColorType.Bgra8888 || bitmap.AlphaType == SKAlphaType.Unpremul)
        {
            var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            owned = new SKBitmap(info);
            using (var canvas = new SKCanvas(owned))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.DrawBitmap(bitmap, 0, 0);
            }
            src = owned;
        }

        int width = src.Width;
        int height = src.Height;
        int stride = src.RowBytes;
        byte[] bytes = new byte[stride * height];
        Marshal.Copy(src.GetPixels(), bytes, 0, bytes.Length);
        owned?.Dispose();
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, bytes, stride);
        source.Freeze();
        return source;
    }

    sealed class ReadyPhoto : IDisposable
    {
        public required PhotoDocument Doc { get; init; }
        public required BitmapSource[] Frames { get; init; }
        public BitmapSource? Thumb { get; init; }
        public long Bytes { get; init; }
        public void Dispose() => Doc.Dispose();
    }

    static bool Recycle(string path)
    {
        try
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = 3,
                pFrom = path + "\0\0",
                fFlags = 0x0010 | 0x0040 | 0x0004 | 0x0400
            };
            return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pFrom;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }

    static TextBox MetaBox(bool multi = false)
    {
        var box = TemplateTheme.Field(multi);
        box.Margin = new Thickness(0, 2, 0, 0);
        if (multi)
            box.MinHeight = 52;
        return box;
    }

    static TextBlock MetaLabel(string text) => new()
    {
        Text = text,
        Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
        FontSize = 11,
        Margin = new Thickness(0, 8, 0, 0)
    };

    void ToggleMeta()
    {
        _metaOn = !_metaOn;
        _metaCol.Width = new GridLength(_metaOn ? 250 : 0);
        if (_metaOn)
            LoadMeta();
    }

    void LoadMeta()
    {
        string path = _path;
        if (path.Length == 0)
            return;
        int gen = ++_metaGen;
        Task.Run(() =>
        {
            PhotoNote note;
            try
            {
                note = PhotoMeta.Read(path);
            }
            catch (Exception ex)
            {
                note = new PhotoNote { Note = ex.Message };
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (gen != _metaGen || !path.Equals(_path, StringComparison.OrdinalIgnoreCase))
                    return;
                _metaTitle.Text = note.Title;
                _metaAuthor.Text = note.Author;
                _metaDesc.Text = note.Description;
                _metaKeys.Text = note.Keywords;
                _metaRights.Text = note.Copyright;
                _metaTaken.Text = note.Taken;
                _metaTitle.IsReadOnly = !note.CanWrite;
                _metaAuthor.IsReadOnly = !note.CanWrite;
                _metaDesc.IsReadOnly = !note.CanWrite;
                _metaKeys.IsReadOnly = !note.CanWrite;
                _metaRights.IsReadOnly = !note.CanWrite;
                _metaTaken.IsReadOnly = !note.CanWrite;
                _metaWrite.Visibility = note.CanWrite ? Visibility.Visible : Visibility.Collapsed;
                _metaFacts.Text = string.Join("\n", note.Facts.Select(f => f.Label + ": " + f.Value));
                _metaStatus.Text = note.Note;
            });
        });
    }

    void SaveMeta()
    {
        string path = _path;
        if (path.Length == 0)
            return;
        var note = new PhotoNote
        {
            Title = _metaTitle.Text.Trim(),
            Author = _metaAuthor.Text.Trim(),
            Description = _metaDesc.Text.Trim(),
            Keywords = _metaKeys.Text.Trim(),
            Copyright = _metaRights.Text.Trim(),
            Taken = _metaTaken.Text.Trim()
        };
        _metaStatus.Text = "Записываю…";
        Task.Run(() =>
        {
            try
            {
                PhotoMeta.Write(path, note);
                Dispatcher.BeginInvoke(() =>
                {
                    if (path.Equals(_path, StringComparison.OrdinalIgnoreCase))
                        _metaStatus.Text = "Записал.";
                });
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (path.Equals(_path, StringComparison.OrdinalIgnoreCase))
                        _metaStatus.Text = ex.Message;
                });
            }
        });
    }

    void StartCompare()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Вторая картинка",
            Filter = PhotoCodec.DialogFilter
        };
        if (dialog.ShowDialog() != true)
            return;
        string path = dialog.FileName;
        Task.Run(() =>
        {
            try
            {
                using var doc = PhotoFiles.Open(path);
                if (doc.Frames.Length == 0)
                    throw new InvalidDataException("В файле нет картинки.");
                var frame = doc.Frames[0].Copy();
                Dispatcher.BeginInvoke(() =>
                {
                    _cmpOther?.Dispose();
                    _cmpOther = frame;
                    _cmpOtherName = Path.GetFileName(path);
                    _cmp = CmpMode.Diff;
                    _cmpSlider.Value = 0.5;
                    _cmpRefit = true;
                    _cmpBar.Visibility = Visibility.Visible;
                    _cmpLegend.Visibility = Visibility.Visible;
                    _anim.Stop();
                    MarkCmp();
                    ScheduleCompare();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() => _info.Text = "Вторая картинка не открылась. " + ex.Message);
            }
        });
    }

    void StopCompare()
    {
        _cmp = CmpMode.Off;
        _cmpGen++;
        _cmpWait.Stop();
        _cmpOther?.Dispose();
        _cmpOther = null;
        _cmpOtherName = "";
        _cmpBar.Visibility = Visibility.Collapsed;
        _cmpLegend.Visibility = Visibility.Collapsed;
        if (_doc != null && _frames.Length > 0)
            _view.SetImage(_frames[Math.Clamp(_frame, 0, _frames.Length - 1)], _doc.HasAlpha, refit: true);
        if (_doc is { Animated: true } && !_paused)
            ArmAnim();
        UpdateInfo();
    }

    void SetCmp(CmpMode mode)
    {
        if (_cmpOther == null)
            return;
        _cmp = mode;
        _cmpLegend.Visibility = mode == CmpMode.Diff ? Visibility.Visible : Visibility.Collapsed;
        MarkCmp();
        if (mode == CmpMode.Diff)
            ScheduleCompare();
        else
            _view.SetSplit(_cmpSlider.Value, mode == CmpMode.Wipe ? 2 : 3);
    }

    void MarkCmp()
    {
        Mark(_cmpDiff, _cmp == CmpMode.Diff);
        Mark(_cmpWipe, _cmp == CmpMode.Wipe);
        Mark(_cmpMix, _cmp == CmpMode.Mix);
    }

    static void Mark(Border button, bool on) =>
        button.BorderBrush = TemplateTheme.Brush(on ? TemplateTheme.AccentText : TemplateTheme.Border);

    void ScheduleCompare()
    {
        if (_cmp == CmpMode.Off || _doc == null || _doc.Frames.Length == 0 || _cmpOther == null)
            return;
        int gen = ++_cmpGen;
        var mode = _cmp;
        double amount = _cmpSlider.Value;
        bool refit = _cmpRefit;
        _cmpRefit = false;
        int threshold = 6 + (int)Math.Round(amount * 70);
        SKBitmap left;
        SKBitmap right;
        try
        {
            left = _doc.Frames[Math.Clamp(_frame, 0, _doc.Frames.Length - 1)].Copy();
            right = _cmpOther.Copy();
        }
        catch
        {
            return;
        }

        Task.Run(() =>
        {
            SKBitmap? fitted = null;
            SKBitmap? diff = null;
            try
            {
                fitted = FitInside(right, left.Width, left.Height);
                right.Dispose();
                right = null!;
                if (mode == CmpMode.Diff)
                    diff = ColorDiff(left, fitted, threshold);
                var under = ToSource(left);
                var over = ToSource(fitted);
                BitmapSource? painted = diff == null ? null : ToSource(diff);
                Dispatcher.BeginInvoke(() =>
                {
                    if (gen != _cmpGen || _cmp == CmpMode.Off)
                        return;
                    int paint = _cmp == CmpMode.Diff ? 1 : _cmp == CmpMode.Wipe ? 2 : 3;
                    _view.ShowCompare(under, over, painted, paint, _cmpSlider.Value, refit);
                    UpdateInfo();
                });
            }
            catch
            {
                // Сравнение не должно ронять просмотр.
            }
            finally
            {
                left.Dispose();
                right?.Dispose();
                fitted?.Dispose();
                diff?.Dispose();
            }
        });
    }

    static SKBitmap FitInside(SKBitmap src, int width, int height)
    {
        var dst = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(dst);
        canvas.Clear(new SKColor(0, 0, 0, 255));
        float scale = Math.Min(width / (float)src.Width, height / (float)src.Height);
        float dw = src.Width * scale;
        float dh = src.Height * scale;
        var dest = new SKRect((width - dw) / 2f, (height - dh) / 2f, (width + dw) / 2f, (height + dh) / 2f);
        canvas.DrawBitmap(src, dest);
        return dst;
    }

    static SKBitmap ColorDiff(SKBitmap left, SKBitmap right, int threshold)
    {
        int width = left.Width;
        int height = left.Height;
        var dst = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        int ls = left.RowBytes;
        int rs = right.RowBytes;
        int ds = dst.RowBytes;
        byte[] rowL = new byte[ls];
        byte[] rowR = new byte[rs];
        byte[] rowD = new byte[ds];
        for (int y = 0; y < height; y++)
        {
            Marshal.Copy(left.GetPixels() + y * ls, rowL, 0, Math.Min(ls, width * 4));
            Marshal.Copy(right.GetPixels() + y * rs, rowR, 0, Math.Min(rs, width * 4));
            for (int x = 0; x < width; x++)
            {
                int i = x * 4;
                int db = Math.Abs(rowL[i] - rowR[i]);
                int dg = Math.Abs(rowL[i + 1] - rowR[i + 1]);
                int dr = Math.Abs(rowL[i + 2] - rowR[i + 2]);
                int delta = Math.Max(dr, Math.Max(dg, db));
                if (delta <= threshold)
                {
                    int gray = ((rowL[i + 2] * 3 + rowL[i + 1] * 6 + rowL[i]) / 10) * 2 / 3;
                    rowD[i] = rowD[i + 1] = rowD[i + 2] = (byte)gray;
                }
                else
                {
                    int lumL = rowL[i + 2] * 3 + rowL[i + 1] * 6 + rowL[i];
                    int lumR = rowR[i + 2] * 3 + rowR[i + 1] * 6 + rowR[i];
                    if (lumL >= lumR)
                    {
                        rowD[i] = (byte)(db / 6);
                        rowD[i + 1] = (byte)(dg / 6);
                        rowD[i + 2] = (byte)Math.Min(255, 36 + delta);
                    }
                    else
                    {
                        rowD[i] = (byte)(db / 6);
                        rowD[i + 1] = (byte)Math.Min(255, 36 + delta);
                        rowD[i + 2] = (byte)(dr / 6);
                    }
                }
                rowD[i + 3] = 255;
            }
            Marshal.Copy(rowD, 0, dst.GetPixels() + y * ds, width * 4);
        }
        return dst;
    }

    sealed class PhotoCanvas : FrameworkElement
    {
        static readonly Brush Backdrop = TemplateTheme.Brush(TemplateTheme.BgDeep);
        static readonly Brush Line = TemplateTheme.Brush(Color.FromRgb(0x3A, 0x3A, 0x3E));
        static readonly Pen FrameLine = CreateFrame();
        static readonly Brush Checker = MakeChecker();
        BitmapSource? _source;
        BitmapSource? _over;
        BitmapSource? _diff;
        int _paint;
        double _split = 0.5;
        bool _checker;
        double _panX;
        double _panY;
        Point _drag;
        bool _dragging;

        public double Zoom { get; private set; } = 1;
        public bool UserZoom { get; private set; }
        public event Action? Zoomed;

        public PhotoCanvas()
        {
            ClipToBounds = true;
            SizeChanged += (_, _) =>
            {
                if (!UserZoom)
                    Fit();
            };
            MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2)
                {
                    if (Math.Abs(Zoom - 1) < 0.02)
                        Fit();
                    else
                        Actual();
                    Zoomed?.Invoke();
                    e.Handled = true;
                    return;
                }
                _dragging = true;
                _drag = e.GetPosition(this);
                CaptureMouse();
            };
            MouseMove += (_, e) =>
            {
                if (!_dragging)
                    return;
                var p = e.GetPosition(this);
                _panX += p.X - _drag.X;
                _panY += p.Y - _drag.Y;
                _drag = p;
                UserZoom = true;
                ClampPan();
                InvalidateVisual();
            };
            MouseLeftButtonUp += (_, _) =>
            {
                _dragging = false;
                ReleaseMouseCapture();
            };
        }

        public void SetImage(BitmapSource? source, bool checker, bool refit)
        {
            _source = source;
            _over = null;
            _diff = null;
            _paint = 0;
            _checker = checker;
            if (refit)
                UserZoom = false;
            if (!UserZoom)
                Fit();
            else
                ClampPan();
            bool pixel = source != null && Zoom >= 2 && source.PixelWidth < 280 && source.PixelHeight < 280;
            RenderOptions.SetBitmapScalingMode(this, pixel ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Fant);
            InvalidateVisual();
        }

        public void ShowCompare(BitmapSource under, BitmapSource? over, BitmapSource? diff, int paint, double split, bool refit)
        {
            _source = under;
            _over = over;
            _diff = diff;
            _paint = paint;
            _split = Math.Clamp(split, 0, 1);
            _checker = false;
            if (refit)
                UserZoom = false;
            if (!UserZoom)
                Fit();
            else
                ClampPan();
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Fant);
            InvalidateVisual();
        }

        public void SetSplit(double split, int paint = 0)
        {
            _split = Math.Clamp(split, 0, 1);
            if (paint != 0)
                _paint = paint;
            InvalidateVisual();
        }

        public void Fit()
        {
            UserZoom = false;
            if (_source == null || ActualWidth < 8 || ActualHeight < 8)
            {
                Zoom = 1;
                _panX = 0;
                _panY = 0;
                InvalidateVisual();
                return;
            }
            double z = Math.Min(ActualWidth / _source.PixelWidth, ActualHeight / _source.PixelHeight);
            Zoom = z > 1 ? 1 : z;
            Center();
            InvalidateVisual();
        }

        public void Actual()
        {
            UserZoom = true;
            Zoom = 1;
            Center();
            InvalidateVisual();
        }

        public void ZoomAt(Point cursor, double factor)
        {
            if (_source == null)
                return;
            UserZoom = true;
            double next = Math.Clamp(Zoom * factor, 0.05, 16);
            double ix = (cursor.X - _panX) / Zoom;
            double iy = (cursor.Y - _panY) / Zoom;
            Zoom = next;
            _panX = cursor.X - ix * Zoom;
            _panY = cursor.Y - iy * Zoom;
            bool pixel = Zoom >= 2 && _source.PixelWidth < 280 && _source.PixelHeight < 280;
            RenderOptions.SetBitmapScalingMode(this, pixel ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Fant);
            ClampPan();
            InvalidateVisual();
            Zoomed?.Invoke();
        }

        void Center()
        {
            if (_source == null)
                return;
            _panX = (ActualWidth - _source.PixelWidth * Zoom) / 2;
            _panY = (ActualHeight - _source.PixelHeight * Zoom) / 2;
        }

        void ClampPan()
        {
            if (_source == null)
                return;
            double w = _source.PixelWidth * Zoom;
            double h = _source.PixelHeight * Zoom;
            if (w <= ActualWidth)
                _panX = (ActualWidth - w) / 2;
            else
                _panX = Math.Clamp(_panX, ActualWidth - w - 24, 24);
            if (h <= ActualHeight)
                _panY = (ActualHeight - h) / 2;
            else
                _panY = Math.Clamp(_panY, ActualHeight - h - 24, 24);
        }

        protected override void OnRender(DrawingContext dc)
        {
            var view = new Rect(0, 0, ActualWidth, ActualHeight);
            dc.DrawRectangle(Backdrop, null, view);
            if (_source == null)
                return;
            var rect = new Rect(_panX, _panY, _source.PixelWidth * Zoom, _source.PixelHeight * Zoom);
            if (_checker && _paint == 0)
                dc.DrawRectangle(Checker, null, rect);
            if (_paint == 1 && _diff != null)
            {
                dc.DrawImage(_diff, rect);
            }
            else
            {
                dc.DrawImage(_source, rect);
                if (_paint == 2 && _over != null)
                {
                    double cut = rect.X + rect.Width * _split;
                    dc.PushClip(new RectangleGeometry(new Rect(rect.X, rect.Y, Math.Max(0, cut - rect.X), rect.Height)));
                    dc.DrawImage(_over, rect);
                    dc.Pop();
                    dc.DrawLine(FrameLine, new Point(cut, rect.Y), new Point(cut, rect.Bottom));
                }
                else if (_paint == 3 && _over != null)
                {
                    dc.PushOpacity(Math.Clamp(_split, 0, 1));
                    dc.DrawImage(_over, rect);
                    dc.Pop();
                }
            }
            dc.DrawRectangle(null, FrameLine, rect);
        }

        static Pen CreateFrame()
        {
            var pen = new Pen(Line, 1);
            pen.Freeze();
            return pen;
        }

        static Brush MakeChecker()
        {
            var dark = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1A));
            var light = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x30));
            dark.Freeze();
            light.Freeze();
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
            group.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
            group.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
            var brush = new DrawingBrush(group)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 16, 16),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, 16, 16),
                ViewboxUnits = BrushMappingMode.Absolute
            };
            brush.Freeze();
            return brush;
        }
    }
}
