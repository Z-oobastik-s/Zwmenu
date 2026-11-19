using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Zwmenu.Services;
using Binding = System.Windows.Data.Binding;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Control = System.Windows.Controls.Control;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using DragAction = System.Windows.DragAction;
using QueryContinueDragEventArgs = System.Windows.QueryContinueDragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using IDataObject = System.Windows.IDataObject;
using ItemsControl = System.Windows.Controls.ItemsControl;
using MenuItem = System.Windows.Controls.MenuItem;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using SelectionMode = System.Windows.Controls.SelectionMode;
using WpfImage = System.Windows.Controls.Image;

namespace Zwmenu.Overlay;

/// <summary>
/// Полка без коробки: карточки стоят на полоске света.
/// Файлы уходят со стола. «Этот компьютер» и «Корзина» тоже.
/// </summary>
public sealed class ShelfWindow : Window
{
    private const string GhostFormat = "ZwmenuShelfGhost";

    private static readonly ControlTemplate BareScroller = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ScrollViewer'>" +
        "<ScrollContentPresenter x:Name='PART_ScrollContentPresenter' CanContentScroll='{TemplateBinding CanContentScroll}' Content='{TemplateBinding Content}'/>" +
        "</ControlTemplate>");
    private const string OrderFormat = "ZwmenuShelfOrder";

    private static ShelfWindow? _current;

    private readonly ObservableCollection<ShelfTile> _tiles = new();
    private readonly ItemsControl _list;
    private readonly ScrollViewer _scroller;
    private readonly Border _insert;
    private readonly Border _stackMark;
    private readonly Border _ringCap;
    private readonly TextBlock _ringText;
    private double _baseArt = 64;
    private double _baseName = 12;
    private double _baseGap = 8;
    private double _baseRadius = 16;
    private double _art = 64;
    private double _cardW = 84;
    private double _cellW = 92;
    private double _rowPitch = 94;
    private double _hoverScale = 1.2;
    private double _pressScale = 0.9;
    private double _radius = 16;
    private double _nameSize = 12;
    private bool _names = true;
    private int _maxRows = 3;
    private int _gap = 8;
    private int _bottom = -1;
    private int _align = 1;
    private int _plate = 94;
    private int _widthPct = 100;
    private int _scrollMode;
    private int _columns;
    private int _layout;
    private int _sort;
    private string _kindOrder = ShelfArrange.DefaultKinds;
    private bool _wheelDown;
    private bool _wheelSide;
    private int _nameW;
    private int _stageFocus = -1;
    private static readonly ItemsPanelTemplate WrapItems = MakeWrapPanel();
    private static readonly ItemsPanelTemplate IslandItems = new(new FrameworkElementFactory(typeof(IslandPanel)));
    private static readonly ItemsPanelTemplate StageItems = new(new FrameworkElementFactory(typeof(StagePanel)));
    private readonly Border _bar;
    private bool _hideInGame = true;
    private int _lookStamp = int.MinValue;
    private readonly TextBlock _empty;
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _game = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly Queue<ShelfTile> _thumbs = new();
    private FileSystemWatcher? _watcher;
    private bool _wantOpen;
    private bool _dragged;
    private bool _carry;
    private ShelfTile? _carryTile;
    private ShelfTile? _stackTarget;
    private int _carryAt;
    private bool _selfDrop;
    private bool _dragCancelled;
    private string? _openStack;
    private ShelfTile? _pressTile;
    private FrameworkElement? _pressHost;
    private Point _press;
    private int _thumbPump;

    private ShelfWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.Manual;
        Width = 420;
        Height = 108;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        FontFamily = new FontFamily("Segoe UI");
        AllowDrop = true;

        _empty = new TextBlock
        {
            Text = "Кидай сюда файлы, корзину, этот компьютер",
            Foreground = new SolidColorBrush(Color.FromArgb(0xD0, 0xF3, 0xE8, 0xDC)),
            FontSize = 13,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            IsHitTestVisible = false
        };

        _list = BuildList();
        _scroller = new ScrollViewer
        {
            Content = _list,
            Margin = new Thickness(14, 18, 14, 12),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Focusable = false,
            Padding = new Thickness(0),
            Template = BareScroller
        };
        _scroller.PreviewMouseWheel += OnShelfWheel;
        _insert = new Border
        {
            Width = 2,
            Height = 52,
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0xF2, 0xE2, 0xC4)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        _stackMark = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xF0, 0xF2, 0xE2, 0xC4)),
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Color.FromArgb(0x38, 0xF2, 0xE2, 0xC4)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        Panel.SetZIndex(_stackMark, 30);
        _bar = new Border
        {
            CornerRadius = new CornerRadius(22),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = System.Windows.VerticalAlignment.Top
        };
        var root = new Grid();
        _ringText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromArgb(0xF4, 0xF6, 0xEE, 0xE6)),
            FontSize = 13,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 210,
            IsHitTestVisible = false
        };
        _ringCap = new Border
        {
            Child = _ringText,
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x16, 0x12, 0x10)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xAA, 0xF0, 0xE0, 0xC8)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 4, 10, 4),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        Panel.SetZIndex(_ringCap, 40);
        root.Children.Add(_empty);
        root.Children.Add(_bar);
        root.Children.Add(_scroller);
        root.Children.Add(_insert);
        root.Children.Add(_stackMark);
        root.Children.Add(_ringCap);
        Content = root;
        MouseLeave += (_, _) => ClearRingHover();

        _watch.Tick += (_, _) =>
        {
            _watch.Stop();
            Refresh();
        };
        _game.Tick += (_, _) => ApplyPresence();

        DragEnter += OnDrag;
        DragOver += OnDrag;
        DragLeave += (_, e) =>
        {
            Point p = e.GetPosition(this);
            if (p.X < 0 || p.Y < 0 || p.X > ActualWidth || p.Y > ActualHeight)
                HideInsert();
        };
        Drop += OnDrop;
        PreviewMouseMove += (_, e) =>
        {
            if (_carry)
                OnCarryMove(e);
        };
        PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!_carry)
                return;
            FinishCarry();
            e.Handled = true;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (!_carry || e.Key != Key.Escape)
                return;
            CancelCarry();
            e.Handled = true;
        };
        PreviewMouseLeftButtonDown += OnPress;
        SourceInitialized += (_, _) => ApplyToolStyle();
        Closed += (_, _) =>
        {
            _game.Stop();
            _watch.Stop();
            _watcher?.Dispose();
            if (ReferenceEquals(_current, this))
                _current = null;
        };
    }

    public static void Toggle()
    {
        var window = Ensure();
        if (window.IsVisible)
        {
            window._wantOpen = false;
            window.Hide();
            window._game.Stop();
            window.StoreOpen(false);
            return;
        }

        window._wantOpen = true;
        window.StoreOpen(true);
        window.ApplyPresence();
        if (!window.IsVisible && GameFocus.IsPlaying())
            DesktopIcons.Toast("Полка спрятана, пока идёт игра");
    }

    public static void Restore()
    {
        if (!ShelfFiles.LoadPrefs().Open)
            return;
        var window = Ensure();
        window._wantOpen = true;
        window.ApplyPresence();
    }

    private static ShelfWindow Ensure()
    {
        if (_current != null)
            return _current;
        _current = new ShelfWindow();
        return _current;
    }

    private void ApplyPresence()
    {
        if (!_wantOpen)
        {
            if (IsVisible)
                Hide();
            _game.Stop();
            return;
        }

        if (!_game.IsEnabled)
            _game.Start();
        if (_hideInGame && GameFocus.IsPlaying())
        {
            if (IsVisible)
                Hide();
            return;
        }

        if (!IsVisible)
        {
            Place();
            StartWatch();
            Refresh();
        }
    }

    private void Place()
    {
        if (!IsVisible)
        {
            Left = -8000;
            Top = -8000;
            Show();
            UpdateLayout();
        }

        try
        {
            Fit();
        }
        catch
        {
            Width = 480;
            Height = 140;
            Left = 48;
            Top = 48;
        }
    }

    public static void ApplyLook()
    {
        var window = _current;
        if (window == null)
            return;
        if (!window.IsVisible)
        {
            window.ReadLook();
            return;
        }

        var prefs = ShelfFiles.CurrentLook();
        bool order = prefs.Sort != window._sort
            || prefs.Layout != window._layout
            || !string.Equals(prefs.KindOrder, window._kindOrder, StringComparison.Ordinal);
        if (order)
            window.Refresh();
        else
            window.Fit();
    }

    private void OnShelfWheel(object sender, MouseWheelEventArgs e)
    {
        if (_layout is 2 or 5 or 6 && !_wheelDown)
            return;
        if (_wheelDown)
            _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset - e.Delta);
        else if (_wheelSide || _scrollMode == 1)
            _scroller.ScrollToHorizontalOffset(_scroller.HorizontalOffset - e.Delta);
        else
            return;
        e.Handled = true;
    }

    private void ReadLook()
    {
        var prefs = ShelfFiles.CurrentLook();
        _baseArt = prefs.Icon;
        _baseName = prefs.NameSize;
        _baseGap = prefs.Gap;
        _baseRadius = prefs.Radius;
        _names = prefs.Names;
        _bottom = prefs.Bottom;
        _align = prefs.Align;
        _plate = prefs.Plate;
        _widthPct = prefs.ShelfWidth;
        _scrollMode = prefs.Scroll;
        _columns = prefs.Columns;
        _layout = prefs.Layout;
        _sort = prefs.Sort;
        _kindOrder = prefs.KindOrder;
        _maxRows = prefs.Rows;
        _hideInGame = prefs.HideInGame;
        _hoverScale = prefs.Hover / 100.0;
        _pressScale = prefs.Press ? 0.9 : 1;
        Topmost = prefs.Above;
        _scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
    }

    /// <summary>Имя справа от значка: лента, список, рельс, фишки, острова.</summary>
    private bool SideBySide() => _layout is 4 or 5 or 6 or 8 or 9;

    private (int art, int name, int gap, int radius, int nameW, double card, double cell, double pitch) Metrics(double scale)
    {
        scale = Math.Clamp(scale, 0.35, 1);
        int art = Math.Max(18, (int)Math.Round(_baseArt * scale));
        int name = Math.Max(8, (int)Math.Round(_baseName * scale));
        int gap = Math.Max(0, (int)Math.Round(_baseGap * scale));
        int radius = Math.Max(0, (int)Math.Round(_baseRadius * scale));
        if (SideBySide())
        {
            int nameW = _names ? Math.Max(128, name * 8) : 0;
            double card = art + (nameW > 0 ? nameW + 10 : 0) + 16;
            return (art, name, gap, radius, nameW, card, card + gap, art + 10 + gap);
        }

        if (_layout == 7)
        {
            double card = art + 6;
            return (art, name, gap, radius, 0, card, card + gap, card + gap);
        }

        double plateCard = art + Math.Max(8, (int)Math.Round(20 * scale));
        double nameBlock = _names ? 4 + Math.Max(14, name + 6) : 0;
        return (art, name, gap, radius, 0, plateCard, plateCard + gap, gap + art + nameBlock);
    }

    /// <summary>Масштаб запекается в размер карточки. LayoutTransform на прозрачном окне перерисовывал всю полку на каждое наведение.</summary>
    private void ApplyScale(double scale)
    {
        var sized = Metrics(scale);
        int stamp = HashCode.Combine(sized.art, sized.name, sized.gap, sized.radius, _names, _plate, _layout);
        _art = sized.art;
        _nameSize = sized.name;
        _gap = sized.gap;
        _radius = sized.radius;
        _nameW = sized.nameW;
        _cardW = sized.card;
        _cellW = sized.cell;
        _rowPitch = sized.pitch;
        if (_layout == 15)
        {
            if (_stageFocus >= _tiles.Count)
                _stageFocus = -1;
        }
        else if (_layout is >= 12 and <= 16 && _tiles.Count > 0 && (_stageFocus < 0 || _stageFocus >= _tiles.Count))
            _stageFocus = _layout == 16 ? 0 : (_tiles.Count - 1) / 2;
        if (_layout is >= 12 and <= 16)
        {
            StagePanel.SetFocus(_list, _stageFocus);
            StagePanel.SetPop(_list, _hoverScale);
        }
        else
            _ringCap.Visibility = Visibility.Collapsed;
        if (stamp == _lookStamp)
            return;
        _lookStamp = stamp;
        _list.ItemTemplate = MakeTemplate();
        _list.ItemsPanel = _layout switch
        {
            9 => IslandItems,
            >= 12 and <= 16 => StageItems,
            _ => WrapItems
        };
        StagePanel.SetKind(_list, _layout);
        double pad = Math.Max(4, Math.Round(sized.art * 0.08));
        foreach (var tile in _tiles)
            tile.SetPad(pad);
        _insert.Height = Math.Max(28, sized.art * 0.8);
    }

    private void Fit()
    {
        if (PresentationSource.FromVisual(this) == null)
            return;
        ReadLook();
        var work = WorkArea();
        Point tl = RadialLayout.DeviceToDip(this, work.Left, work.Top);
        Point br = RadialLayout.DeviceToDip(this, work.Right, work.Bottom);
        double workW = br.X - tl.X;
        double workH = br.Y - tl.Y;
        double room = Math.Max(200, (workW - 32) * _widthPct / 100.0);
        if (_layout == 11)
            room = Math.Min(room, Math.Max(320, workW * 0.62));
        int count = _tiles.Count;
        var seed = Metrics(1);
        double cell = seed.cell;
        double pitch = seed.pitch;
        double contentW;
        double contentH;
        double viewW;
        double viewH;
        double scale = 1;
        int cols = 1;
        int rows = 1;
        _wheelDown = false;
        _wheelSide = false;
        bool edge = _layout is 2 or 5 or 6;
        bool ribbon = _layout == 4;
        bool grouped = _layout is 3 or 9;
        bool stage = _layout is >= 12 and <= 16;
        if (count == 0)
        {
            contentW = viewW = 360;
            contentH = viewH = 88;
        }
        else if (edge)
        {
            cols = 1;
            rows = count;
            contentW = cell + 1;
            contentH = rows * pitch;
            viewW = contentW;
            viewH = Math.Min(contentH, Math.Max(pitch, workH - 64));
            _wheelDown = contentH > viewH + 2;
        }
        else if (ribbon)
        {
            cols = count;
            rows = 1;
            contentW = Math.Max(cell, cols * cell);
            contentH = pitch;
            viewW = Math.Min(contentW, room);
            viewH = contentH;
            _wheelSide = contentW > viewW + 2;
        }
        else if (grouped)
        {
            cols = _columns > 0 ? _columns : Math.Max(1, (int)(room / cell));
            contentW = cols * cell;
            contentH = GroupHeight(pitch, cols);
            scale = contentW <= room ? 1 : room / contentW;
            viewW = contentW * scale;
            viewH = contentH * scale;
        }
        else if (stage)
        {
            StageSize(cell, pitch, count, room, workH, out contentW, out contentH);
            viewW = contentW;
            viewH = contentH;
        }
        else if (_scrollMode == 1)
        {
            rows = Math.Min(_maxRows, count);
            cols = (int)Math.Ceiling(count / (double)rows);
            int across = _columns > 0 ? _columns : Math.Max(1, (int)(room / cell));
            contentW = Math.Max(cell, cols * cell);
            contentH = rows * pitch;
            viewW = Math.Min(contentW, Math.Min(room, Math.Max(cell, across * cell)));
            viewH = contentH;
            if (contentW <= viewW + 2)
            {
                viewW = contentW;
                _scrollMode = 0;
            }
        }
        else if (_scrollMode == 2)
        {
            cols = _columns > 0 ? _columns : Math.Max(1, (int)(room / cell));
            rows = (int)Math.Ceiling(count / (double)cols);
            contentW = cols * cell;
            contentH = rows * pitch;
            viewW = Math.Min(contentW, room);
            viewH = Math.Min(contentH, _maxRows * pitch);
            if (contentH <= viewH + 2)
            {
                viewH = contentH;
                _scrollMode = 0;
            }
        }
        else
        {
            if (_columns > 0)
            {
                cols = Math.Min(_columns, count);
                rows = (int)Math.Ceiling(count / (double)cols);
                if (rows > _maxRows)
                {
                    rows = _maxRows;
                    cols = (int)Math.Ceiling(count / (double)rows);
                }
            }
            else
            {
                for (int want = 1; want <= _maxRows; want++)
                {
                    int across = (int)Math.Ceiling(count / (double)want);
                    rows = want;
                    cols = across;
                    if (across * cell <= room || want == _maxRows)
                        break;
                }
            }

            contentW = cols * cell;
            contentH = rows * pitch;
            scale = contentW <= room ? 1 : room / contentW;
            viewW = contentW * scale;
            viewH = contentH * scale;
        }

        ApplyScale(edge || ribbon || stage ? 1 : scale);
        if (count > 0 && edge)
        {
            cols = 1;
            rows = count;
            contentW = _cellW + 1;
            contentH = rows * _rowPitch;
            viewW = contentW;
            double maxH = Math.Max(_rowPitch, workH - 36);
            int fit = Math.Max(1, (int)(maxH / Math.Max(1, _rowPitch)));
            viewH = Math.Min(contentH, fit * _rowPitch);
            _wheelDown = contentH > viewH + 2;
        }
        else if (count > 0 && ribbon)
        {
            contentW = Math.Max(_cellW, count * _cellW);
            contentH = _rowPitch;
            viewW = WholeCells(Math.Min(contentW, room), _cellW, contentW);
            viewH = contentH;
            _wheelSide = contentW > viewW + 2;
        }
        else if (count > 0 && grouped)
        {
            if (_layout == 9)
            {
                int widest = 1;
                int run = 0;
                foreach (var tile in _tiles)
                {
                    if (tile.IsHeader)
                    {
                        if (run > widest)
                            widest = run;
                        run = 0;
                    }
                    else
                        run++;
                }

                if (run > widest)
                    widest = run;
                int cap = _columns > 0 ? _columns : Math.Max(1, (int)(room / Math.Max(1, _cellW)));
                cols = Math.Max(1, Math.Min(cap, widest));
            }

            contentW = cols * _cellW;
            contentH = GroupHeight(_rowPitch, cols);
            viewW = contentW;
            viewH = contentH;
            double maxH = Math.Max(_rowPitch, workH - 64);
            if (viewH > maxH)
            {
                viewH = maxH;
                _wheelDown = true;
            }

            PaintHeaders(contentW, cols);
        }
        else if (count > 0 && stage)
        {
            StageSize(_cellW, _rowPitch, count, room, workH, out contentW, out contentH);
            viewW = contentW;
            viewH = contentH;
        }
        else if (count > 0)
        {
            contentW = cols * _cellW;
            contentH = rows * _rowPitch;
            if (_scrollMode == 1)
            {
                double limit = Math.Min(contentW, Math.Min(room, _columns > 0 ? _columns * _cellW : viewW));
                viewW = WholeCells(limit, _cellW, contentW);
            }
            else if (_scrollMode == 2)
            {
                viewW = Math.Min(contentW, room);
                viewH = Math.Min(contentH, _maxRows * _rowPitch);
                _wheelDown = contentH > viewH + 2;
            }
            else
            {
                viewW = contentW;
                viewH = contentH;
            }
        }

        _list.Width = Math.Max(1, contentW);
        if (_layout == 9)
        {
            _list.Height = double.NaN;
            double measured = 0;
            try
            {
                _list.UpdateLayout();
                measured = _list.DesiredSize.Height;
            }
            catch
            {
                measured = 0;
            }
            double formula = IslandHeight(_rowPitch, Math.Max(1, cols));
            contentH = Math.Max(measured, formula) + 14;
            double chrome = _scroller.Margin.Top + _scroller.Margin.Bottom;
            double maxView = Math.Max(_rowPitch, workH - chrome - 16);
            viewH = Math.Min(contentH, maxView);
            viewW = contentW;
            _wheelDown = contentH > viewH + 2;
        }

        _list.Height = Math.Max(1, contentH);
        _scroller.Width = Math.Max(1, viewW);
        _scroller.Height = Math.Max(1, viewH);
        Width = viewW + _scroller.Margin.Left + _scroller.Margin.Right;
        Height = viewH + _scroller.Margin.Top + _scroller.Margin.Bottom;
        if (edge)
        {
            bool right = _align != 0;
            Left = right ? br.X - Width - 12 : tl.X + 12;
            if (_wheelDown)
                Top = tl.Y + 12;
            else if (_bottom < 0 && _align == 1)
                Top = tl.Y + Math.Max(8, (workH - Height) / 2);
            else
                Top = br.Y - Height - (_bottom >= 0 ? _bottom : 28);
        }
        else if (_layout == 10)
        {
            double free = workW - Width;
            Left = _align switch
            {
                0 => tl.X + 16,
                2 => tl.X + Math.Max(16, free - 16),
                _ => tl.X + Math.Max(0, free / 2)
            };
            Top = tl.Y + (_bottom >= 0 ? _bottom : 16);
        }
        else if (_layout is 11 or 13 or 15)
        {
            Left = tl.X + Math.Max(8, (workW - Width) / 2);
            Top = tl.Y + Math.Max(8, (workH - Height) / 2);
        }
        else
        {
            double free = workW - Width;
            Left = _align switch
            {
                0 => tl.X + 16,
                2 => tl.X + Math.Max(16, free - 16),
                _ => tl.X + Math.Max(0, free / 2)
            };
            bool oneBand = viewH <= _rowPitch + 10;
            int drop = _bottom >= 0 ? _bottom : (oneBand ? 78 : 28);
            Top = br.Y - Height - drop;
        }

        if (_layout is 1 or 4 or 6 or 11 or 12 or 13 or 14 or 16)
        {
            int strength = _layout is >= 12 and <= 16 ? Math.Max(_plate, 72) : _plate;
            byte plate = (byte)Math.Clamp(0xE8 * strength / 100, 0, 255);
            _bar.Background = new SolidColorBrush(Color.FromArgb(plate, 0x16, 0x12, 0x10));
            _bar.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xF0, 0xE0, 0xC8));
            _bar.Width = viewW;
            _bar.Height = viewH;
            _bar.Margin = _scroller.Margin;
            double radius = _layout switch
            {
                11 => 22,
                6 => Math.Min(28, Math.Max(12, viewW / 2)),
                _ => Math.Min(26, Math.Max(10, viewH / 2))
            };
            _bar.CornerRadius = new CornerRadius(radius);
            _bar.Visibility = Visibility.Visible;
        }
        else
            _bar.Visibility = Visibility.Collapsed;

        ClampIntoWork();
    }

    private double GroupHeight(double pitch, int per)
    {
        per = Math.Max(1, per);
        double height = 0;
        int run = 0;
        bool any = false;
        foreach (var tile in _tiles)
        {
            any = true;
            if (tile.IsHeader)
            {
                if (run > 0)
                {
                    height += Math.Ceiling(run / (double)per) * pitch;
                    run = 0;
                }

                height += _layout == 9 ? 32 : 28;
            }
            else
                run++;
        }

        if (run > 0)
            height += Math.Ceiling(run / (double)per) * pitch;
        if (!any)
            return pitch;
        return Math.Max(pitch, height);
    }

    private static double WholeCells(double want, double cell, double full)
    {
        if (cell < 1 || want >= full - 2)
            return full;
        int n = Math.Max(1, (int)(want / cell));
        return Math.Min(full, n * cell);
    }

    private void PaintHeaders(double contentW, int per)
    {
        if (_layout == 3)
        {
            foreach (var tile in _tiles)
            {
                if (!tile.IsHeader)
                    continue;
                tile.Span = Math.Max(40, contentW - 18);
                tile.Band = double.NaN;
                tile.Pull = new Thickness(0);
            }

            return;
        }

        if (_layout != 9)
            return;

        ShelfTile? head = null;
        int run = 0;
        void Close()
        {
            if (head == null)
                return;
            head.Bar = double.NaN;
            head.Span = Math.Max(40, contentW - 8);
            head.Band = double.NaN;
            head.Pull = new Thickness(0, 10, 0, 2);
        }

        foreach (var tile in _tiles)
        {
            if (tile.IsHeader)
            {
                Close();
                head = tile;
                run = 0;
            }
            else
                run++;
        }

        Close();
    }

    private void StageSize(double cell, double pitch, int count, double room, double workH, out double contentW, out double contentH)
    {
        count = Math.Max(1, count);
        double dh = pitch;
        if (_layout is 14 or 15 && _names)
            dh = Math.Max(36, pitch - (4 + Math.Max(14, _nameSize + 6)));
        var box = StagePanel.Preferred(_layout, count, cell, dh, room, workH, _hoverScale);
        contentW = box.Width;
        contentH = box.Height;
    }

    private double IslandHeight(double pitch, int per)
    {
        per = Math.Max(1, per);
        double height = 4;
        int run = 0;
        void Flush()
        {
            if (run <= 0)
                return;
            height += Math.Ceiling(run / (double)per) * pitch + 10;
            run = 0;
        }

        foreach (var tile in _tiles)
        {
            if (tile.IsHeader)
            {
                Flush();
                height += 28;
            }
            else
                run++;
        }

        Flush();
        return Math.Max(pitch, height);
    }

    private static ItemsPanelTemplate MakeWrapPanel()
    {
        var panel = new FrameworkElementFactory(typeof(WrapPanel));
        panel.SetValue(WrapPanel.OrientationProperty, System.Windows.Controls.Orientation.Horizontal);
        return new ItemsPanelTemplate(panel);
    }

    private void NoteStage(DependencyObject? source)
    {
        var tile = TileFrom(source);
        if (tile == null || tile.IsHeader)
            return;
        int index = _tiles.IndexOf(tile);
        if (index < 0)
            return;
        if (index != _stageFocus)
        {
            _stageFocus = index;
            StagePanel.SetFocus(_list, index);
        }

        if (_layout != 15)
            return;
        _ringText.Text = tile.Name;
        _ringText.FontSize = Math.Max(11, _nameSize);
        var host = HostOf(source);
        if (host == null)
            return;
        double scale = ReferenceEquals(host, _pressHost) ? _pressScale : Math.Max(1, _hoverScale);
        Point center = host.TranslatePoint(new Point(host.ActualWidth / 2, host.ActualHeight / 2), this);
        _ringCap.Measure(new System.Windows.Size(220, 48));
        double w = Math.Max(36, _ringCap.DesiredSize.Width);
        double h = Math.Max(16, _ringCap.DesiredSize.Height);
        double x = center.X - w / 2;
        double y = center.Y + host.ActualHeight / 2 * scale + 4;
        x = Math.Max(6, Math.Min(x, Math.Max(6, ActualWidth - w - 6)));
        y = Math.Max(6, Math.Min(y, Math.Max(6, ActualHeight - h - 6)));
        _ringCap.Margin = new Thickness(x, y, 0, 0);
        _ringCap.Visibility = Visibility.Visible;
    }

    private void ClearRingHover()
    {
        if (_layout != 15)
        {
            _ringCap.Visibility = Visibility.Collapsed;
            return;
        }

        if (_pressTile != null)
            return;
        _stageFocus = -1;
        StagePanel.SetFocus(_list, -1);
        _ringCap.Visibility = Visibility.Collapsed;
    }

    private void ShowShelfAddMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(GhostMenu("Корзина", ShelfShell.RecycleClsid));
        menu.Items.Add(GhostMenu("Этот компьютер", ShelfShell.ComputerClsid));
        menu.IsOpen = true;
    }

    private System.Windows.Controls.MenuItem GhostMenu(string title, string clsid)
    {
        var item = new System.Windows.Controls.MenuItem { Header = "Положить: " + title };
        item.Click += (_, _) =>
        {
            try
            {
                if (ShelfFiles.TakeGhost(clsid, "::" + clsid, title))
                    Refresh();
            }
            catch
            {
                DesktopIcons.Toast("Не удалось положить");
            }
        };
        return item;
    }

    private void ClampIntoWork()
    {
        if (PresentationSource.FromVisual(this) == null)
            return;
        var work = WorkArea();
        Point tl = RadialLayout.DeviceToDip(this, work.Left, work.Top);
        Point br = RadialLayout.DeviceToDip(this, work.Right, work.Bottom);
        if (Width > br.X - tl.X - 16)
            Width = Math.Max(280, br.X - tl.X - 16);
        double maxWindow = br.Y - tl.Y - 16;
        if (Height > maxWindow && _scroller.Height > 40)
        {
            double chrome = Height - _scroller.Height;
            Height = maxWindow;
            _scroller.Height = Math.Max(40, Height - chrome);
            _wheelDown = true;
        }

        if (Left < tl.X + 8)
            Left = tl.X + 8;
        if (Top < tl.Y + 8)
            Top = tl.Y + 8;
        if (Left + Width > br.X - 8)
            Left = Math.Max(tl.X + 8, br.X - Width - 8);
        if (Top + Height > br.Y - 8)
            Top = Math.Max(tl.Y + 8, br.Y - Height - 8);
    }

    private bool OnAnyWorkArea(double left, double top)
    {
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var area = screen.WorkingArea;
            Point a = RadialLayout.DeviceToDip(this, area.Left, area.Top);
            Point b = RadialLayout.DeviceToDip(this, area.Right, area.Bottom);
            if (left > a.X - 80 && top > a.Y - 80 && left < b.X - 40 && top < b.Y - 40)
                return true;
        }

        return false;
    }

    private static System.Drawing.Rectangle WorkArea() =>
        System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
        ?? System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;

    private void StartWatch()
    {
        if (_watcher != null)
            return;
        try
        {
            _watcher = new FileSystemWatcher(ShelfFiles.DirectoryPath)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true
            };
            FileSystemEventHandler ping = (_, _) => Ping();
            _watcher.Created += ping;
            _watcher.Deleted += ping;
            _watcher.Changed += ping;
            _watcher.Renamed += (_, _) => Ping();
        }
        catch
        {
            // список обновится при следующем открытии
        }
    }

    private void Ping()
    {
        Dispatcher.BeginInvoke(() =>
        {
            _watch.Stop();
            _watch.Start();
        });
    }

    private void Refresh()
    {
        IReadOnlyList<ShelfEntry> disk;
        try { disk = ShelfFiles.List(); }
        catch { return; }

        var prefs = ShelfFiles.CurrentLook();
        var shown = ShelfArrange.Build(
            disk, prefs.Sort, prefs.Layout, prefs.KindOrder, _openStack, prefs.Stacks, prefs.Loose);
        var have = _tiles.ToDictionary(t => t.Key, StringComparer.OrdinalIgnoreCase);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < shown.Count; i++)
        {
            var spot = shown[i];
            keep.Add(spot.Key);
            if (have.TryGetValue(spot.Key, out var tile))
            {
                int at = _tiles.IndexOf(tile);
                if (at >= 0 && at != i)
                    _tiles.Move(at, i);
                if (spot.Entry is { } entry && tile.Stamp != entry.Stamp)
                {
                    tile.Stamp = entry.Stamp;
                    tile.Icon = null;
                    QueueThumb(tile);
                }

                continue;
            }

            var fresh = new ShelfTile(spot);
            if (i >= _tiles.Count)
                _tiles.Add(fresh);
            else
                _tiles.Insert(i, fresh);
            if (!fresh.IsHeader)
                QueueThumb(fresh);
        }

        for (int i = _tiles.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(_tiles[i].Key))
                _tiles.RemoveAt(i);
        }

        _empty.Visibility = _tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Fit();
        Dispatcher.BeginInvoke(HookTiles, DispatcherPriority.Loaded);
    }

    private void OnDrag(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(OrderFormat))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            PlaceInsert(IndexAt(e.GetPosition(_list)));
            return;
        }

        HideInsert();
        bool ours = e.Data.GetDataPresent(GhostFormat);
        bool files = HasFileDrop(e.Data) || HasShellIdList(e.Data);
        e.Effects = ours ? DragDropEffects.Move : files ? Offer(e) : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        _selfDrop = true;
        HideInsert();
        e.Handled = true;
        if (e.Data.GetDataPresent(OrderFormat) && e.Data.GetData(OrderFormat) is string key && key.Length > 0)
        {
            var prefs = ShelfFiles.CurrentLook();
            if (prefs.Sort == 0 && prefs.Layout is 0 or 1 or 2 or 4 or 5 or 6 or 7 or 8 or 10)
                ShelfFiles.MoveKey(key, IndexAt(e.GetPosition(_list)));
            Refresh();
            e.Effects = DragDropEffects.Move;
            return;
        }

        if (e.Data.GetDataPresent(GhostFormat))
        {
            e.Effects = DragDropEffects.Move;
            return;
        }

        var files = new List<string>();
        var ghosts = new List<ShelfShell.Hit>();
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            foreach (string path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;
                if (path.StartsWith("::", StringComparison.Ordinal))
                {
                    var special = ShelfShell.FromParsing(path);
                    if (special.Clsid != null)
                        ghosts.Add(special);
                    continue;
                }

                files.Add(path);
            }
        }

        byte[]? idList = ReadIdList(e.Data);
        if (idList != null)
        {
            foreach (var hit in ShelfShell.Parse(idList))
            {
                if (ShelfShell.IsDesktopSpecial(hit))
                {
                    ghosts.Add(hit);
                    continue;
                }

                if (hit.FilePath == null || ShelfShell.IsUnsafeToMove(hit.FilePath))
                    continue;
                if (files.Any(p => string.Equals(p, hit.FilePath, StringComparison.OrdinalIgnoreCase)))
                    continue;
                files.Add(hit.FilePath);
            }
        }

        if (files.Count == 0 && ghosts.Count == 0)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = Offer(e);
        var batch = files.ToArray();
        var specials = ghosts
            .GroupBy(hit => hit.Clsid ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        _ = Task.Run(() =>
        {
            Thread.Sleep(80);
            int taken = 0;
            int failed = 0;
            foreach (string path in batch)
            {
                try
                {
                    if (ShelfFiles.TakeIn(path))
                        taken++;
                    else if (!ShelfFiles.IsInside(path))
                        failed++;
                }
                catch
                {
                    failed++;
                }
            }

            foreach (var ghost in specials)
            {
                try
                {
                    if (ShelfFiles.TakeGhost(ghost.Clsid ?? "", ghost.Parsing ?? "", ghost.Name))
                        taken++;
                    else
                        failed++;
                }
                catch
                {
                    failed++;
                }
            }

            Dispatcher.BeginInvoke(() =>
            {
                Refresh();
                if (taken == 0 && failed > 0)
                    DesktopIcons.Toast("Не удалось забрать");
            });
        });
    }

    private ItemsControl BuildList()
    {
        var list = new ItemsControl
        {
            ItemsSource = _tiles,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ItemTemplate = MakeTemplate(),
            Focusable = false,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = System.Windows.VerticalAlignment.Top
        };
        list.ItemsPanel = WrapItems;
        list.PreviewMouseMove += (_, e) =>
        {
            if (_layout is >= 12 and <= 16)
                NoteStage(e.OriginalSource as DependencyObject);
            else
            {
                var host = HostOf(e.OriginalSource as DependencyObject);
                if (host != null && host.RenderTransform is not ScaleTransform)
                    Arm(host);
            }

            OnTileMove(list, e);
        };
        list.PreviewMouseLeftButtonUp += OnTileUp;
        list.PreviewMouseRightButtonUp += OnTileMenu;
        return list;
    }

    private DataTemplate MakeTemplate()
    {
        if (SideBySide())
            return MakeChipTemplate();
        if (_layout == 7)
            return MakePosterTemplate();
        return MakeCardTemplate();
    }

    private DataTemplate MakeCardTemplate()
    {
        bool dock = _layout == 1;
        byte plateA = dock ? (byte)0 : (byte)Math.Clamp(0xF0 * _plate / 100, 0, 255);
        byte plateB = dock ? (byte)0 : (byte)Math.Clamp(0xC4 * _plate / 100, 0, 255);
        if (_layout is >= 12 and <= 16)
        {
            plateA = (byte)Math.Max(plateA, (byte)220);
            plateB = (byte)Math.Max(plateB, (byte)176);
        }
        var clip = new RectangleGeometry(new Rect(0, 0, _art, _art), _radius, _radius);
        clip.Freeze();
        var plateBrush = new LinearGradientBrush(
            Color.FromArgb(plateA, 0x22, 0x18, 0x14),
            Color.FromArgb(plateB, 0x0E, 0x0A, 0x08),
            new Point(0, 0),
            new Point(0, 1));
        plateBrush.Freeze();
        var rim = new LinearGradientBrush(
            Color.FromArgb(0x88, 0xF0, 0xE0, 0xC8),
            Color.FromArgb(0x22, 0xC4, 0xA4, 0x84),
            new Point(0, 0),
            new Point(0, 1));
        rim.Freeze();
        var nameBrush = new SolidColorBrush(Color.FromArgb(0xF0, 0xF6, 0xEE, 0xE6));
        nameBrush.Freeze();

        var img = new FrameworkElementFactory(typeof(WpfImage));
        img.SetValue(WpfImage.StretchProperty, Stretch.Uniform);
        img.SetBinding(WpfImage.SourceProperty, new Binding(nameof(ShelfTile.Icon)));
        img.SetBinding(WpfImage.MarginProperty, new Binding(nameof(ShelfTile.PlateInset)));
        img.SetValue(WpfImage.IsHitTestVisibleProperty, false);
        img.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.LowQuality);

        var frame = new FrameworkElementFactory(typeof(Border));
        frame.SetValue(Border.WidthProperty, _art);
        frame.SetValue(Border.HeightProperty, _art);
        frame.SetValue(Border.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(_radius));
        frame.SetValue(Border.BackgroundProperty, plateBrush);
        frame.SetValue(Border.BorderBrushProperty, rim);
        frame.SetValue(Border.BorderThicknessProperty, new Thickness(dock ? 0 : 1));
        frame.SetValue(Border.ClipProperty, clip);
        frame.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        frame.AppendChild(img);

        var art = new FrameworkElementFactory(typeof(Grid));
        art.SetValue(FrameworkElement.WidthProperty, _art);
        art.SetValue(FrameworkElement.HeightProperty, _art);
        art.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        art.AppendChild(frame);
        art.AppendChild(StackBadge());

        var stack = new FrameworkElementFactory(typeof(StackPanel));
        double edge = _gap / 2.0;
        stack.SetValue(StackPanel.WidthProperty, _cardW);
        stack.SetValue(StackPanel.MarginProperty, new Thickness(edge));
        stack.SetValue(StackPanel.BackgroundProperty, Brushes.Transparent);
        stack.SetValue(StackPanel.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        stack.AppendChild(art);
        if (_names && _layout is not (14 or 15))
        {
            var name = new FrameworkElementFactory(typeof(TextBlock));
            name.SetBinding(TextBlock.TextProperty, new Binding(nameof(ShelfTile.Name)));
            name.SetValue(TextBlock.WidthProperty, _cardW);
            name.SetValue(TextBlock.HeightProperty, Math.Max(14, _nameSize + 6));
            name.SetValue(TextBlock.FontSizeProperty, _nameSize);
            name.SetValue(TextBlock.ForegroundProperty, nameBrush);
            name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            name.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
            name.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
            name.SetValue(TextBlock.MarginProperty, new Thickness(0, 4, 0, 0));
            name.SetValue(TextBlock.IsHitTestVisibleProperty, false);
            stack.AppendChild(name);
        }

        stack.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(ShelfTile.IsHeader)) { Converter = HeaderVis.Hide });
        var caption = new FrameworkElementFactory(typeof(TextBlock));
        caption.SetBinding(TextBlock.TextProperty, new Binding(nameof(ShelfTile.Name)));
        caption.SetBinding(TextBlock.VisibilityProperty, new Binding(nameof(ShelfTile.IsHeader)) { Converter = HeaderVis.Show });
        caption.SetValue(TextBlock.FontSizeProperty, 13.0);
        caption.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        caption.SetValue(TextBlock.ForegroundProperty, nameBrush);
        caption.SetValue(TextBlock.MarginProperty, new Thickness(10, 8, 8, 0));
        caption.SetBinding(FrameworkElement.WidthProperty, new Binding(nameof(ShelfTile.Span)));
        caption.SetValue(TextBlock.IsHitTestVisibleProperty, false);
        var root = new FrameworkElementFactory(typeof(StackPanel));
        root.AppendChild(caption);
        root.AppendChild(stack);
        return new DataTemplate { VisualTree = root };
    }

    private DataTemplate MakeChipTemplate()
    {
        bool own = _layout is 5 or 8;
        var fill = new SolidColorBrush(Color.FromArgb(own ? (byte)Math.Clamp(0xF0 * _plate / 100, 28, 255) : (byte)0, 0x22, 0x18, 0x14));
        fill.Freeze();
        var rim = new SolidColorBrush(Color.FromArgb(own ? (byte)0x88 : (byte)0, 0xF0, 0xE0, 0xC8));
        rim.Freeze();
        var nameBrush = new SolidColorBrush(Color.FromArgb(0xF0, 0xF6, 0xEE, 0xE6));
        nameBrush.Freeze();
        var island = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(0xE8 * _plate / 100, 40, 255), 0x16, 0x12, 0x10));
        island.Freeze();
        var islandRim = new SolidColorBrush(Color.FromArgb(0x66, 0xF0, 0xE0, 0xC8));
        islandRim.Freeze();

        var img = new FrameworkElementFactory(typeof(WpfImage));
        img.SetValue(WpfImage.StretchProperty, Stretch.Uniform);
        img.SetBinding(WpfImage.SourceProperty, new Binding(nameof(ShelfTile.Icon)));
        img.SetValue(WpfImage.MarginProperty, new Thickness(4));
        img.SetValue(WpfImage.IsHitTestVisibleProperty, false);
        img.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.LowQuality);
        var frame = new FrameworkElementFactory(typeof(Border));
        frame.SetValue(Border.WidthProperty, _art);
        frame.SetValue(Border.HeightProperty, _art);
        frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(Math.Min(_radius, _art / 2)));
        frame.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        frame.SetValue(Border.ClipToBoundsProperty, true);
        frame.AppendChild(img);
        var chipArt = new FrameworkElementFactory(typeof(Grid));
        chipArt.SetValue(FrameworkElement.WidthProperty, _art);
        chipArt.SetValue(FrameworkElement.HeightProperty, _art);
        chipArt.AppendChild(frame);
        chipArt.AppendChild(StackBadge());

        var inner = new FrameworkElementFactory(typeof(StackPanel));
        inner.SetValue(StackPanel.OrientationProperty, System.Windows.Controls.Orientation.Horizontal);
        inner.SetValue(StackPanel.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
        inner.AppendChild(chipArt);
        if (_names)
        {
            var name = new FrameworkElementFactory(typeof(TextBlock));
            name.SetBinding(TextBlock.TextProperty, new Binding(nameof(ShelfTile.Name)));
            name.SetValue(TextBlock.WidthProperty, (double)_nameW);
            name.SetValue(TextBlock.FontSizeProperty, _nameSize);
            name.SetValue(TextBlock.ForegroundProperty, nameBrush);
            name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            name.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
            name.SetValue(TextBlock.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
            name.SetValue(TextBlock.MarginProperty, new Thickness(8, 0, 8, 0));
            name.SetValue(TextBlock.IsHitTestVisibleProperty, false);
            inner.AppendChild(name);
        }

        var pill = new FrameworkElementFactory(typeof(Border));
        pill.SetValue(Border.WidthProperty, _cardW);
        pill.SetValue(Border.HeightProperty, _art + 8);
        pill.SetValue(Border.MarginProperty, new Thickness(_gap / 2.0));
        pill.SetValue(Border.CornerRadiusProperty, new CornerRadius(Math.Min(18, (_art + 8) / 2)));
        pill.SetValue(Border.BackgroundProperty, fill);
        pill.SetValue(Border.BorderBrushProperty, rim);
        pill.SetValue(Border.BorderThicknessProperty, new Thickness(own ? 1 : 0));
        pill.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(ShelfTile.IsHeader)) { Converter = HeaderVis.Hide });
        pill.AppendChild(inner);

        var caption = new FrameworkElementFactory(typeof(TextBlock));
        caption.SetBinding(TextBlock.TextProperty, new Binding(nameof(ShelfTile.Name)));
        caption.SetValue(TextBlock.FontSizeProperty, 13.0);
        caption.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        caption.SetValue(TextBlock.ForegroundProperty, nameBrush);
        caption.SetValue(TextBlock.MarginProperty, new Thickness(12, 6, 8, 0));
        caption.SetValue(TextBlock.IsHitTestVisibleProperty, false);
        var band = new FrameworkElementFactory(typeof(Border));
        band.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(ShelfTile.IsHeader)) { Converter = HeaderVis.Show });
        band.SetBinding(FrameworkElement.WidthProperty, new Binding(nameof(ShelfTile.Span)));
        band.SetBinding(FrameworkElement.HeightProperty, new Binding(nameof(ShelfTile.Band)));
        band.SetBinding(FrameworkElement.MarginProperty, new Binding(nameof(ShelfTile.Pull)));
        band.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        band.SetValue(UIElement.IsHitTestVisibleProperty, false);
        var islandBox = new FrameworkElementFactory(typeof(Border));
        islandBox.SetBinding(FrameworkElement.WidthProperty, new Binding(nameof(ShelfTile.Bar)));
        islandBox.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Left);
        islandBox.SetValue(FrameworkElement.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Stretch);
        islandBox.SetValue(Border.BackgroundProperty, _layout == 9 ? island : Brushes.Transparent);
        islandBox.SetValue(Border.BorderBrushProperty, _layout == 9 ? islandRim : Brushes.Transparent);
        islandBox.SetValue(Border.BorderThicknessProperty, new Thickness(_layout == 9 ? 1 : 0));
        islandBox.SetValue(Border.CornerRadiusProperty, new CornerRadius(16));
        islandBox.SetValue(UIElement.IsHitTestVisibleProperty, false);
        islandBox.AppendChild(caption);
        band.AppendChild(islandBox);

        var root = new FrameworkElementFactory(typeof(StackPanel));
        root.AppendChild(band);
        root.AppendChild(pill);
        return new DataTemplate { VisualTree = root };
    }

    private DataTemplate MakePosterTemplate()
    {
        var clip = new RectangleGeometry(new Rect(0, 0, _art, _art), _radius, _radius);
        clip.Freeze();
        var nameBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xF6, 0xEE, 0xE6));
        nameBrush.Freeze();
        var shade = new SolidColorBrush(Color.FromArgb(0xC0, 0x10, 0x0C, 0x08));
        shade.Freeze();
        var img = new FrameworkElementFactory(typeof(WpfImage));
        img.SetValue(WpfImage.StretchProperty, Stretch.UniformToFill);
        img.SetBinding(WpfImage.SourceProperty, new Binding(nameof(ShelfTile.Icon)));
        img.SetValue(WpfImage.IsHitTestVisibleProperty, false);
        img.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.LowQuality);
        var wash = new SolidColorBrush(Color.FromArgb(0x44, 0x22, 0x18, 0x14));
        wash.Freeze();
        var plate = new FrameworkElementFactory(typeof(Border));
        plate.SetValue(Border.WidthProperty, _art);
        plate.SetValue(Border.HeightProperty, _art);
        plate.SetValue(Border.CornerRadiusProperty, new CornerRadius(_radius));
        plate.SetValue(Border.ClipProperty, clip);
        plate.SetValue(Border.BackgroundProperty, wash);
        var layer = new FrameworkElementFactory(typeof(Grid));
        layer.AppendChild(img);
        if (_names)
        {
            var name = new FrameworkElementFactory(typeof(TextBlock));
            name.SetBinding(TextBlock.TextProperty, new Binding(nameof(ShelfTile.Name)));
            name.SetValue(TextBlock.FontSizeProperty, _nameSize);
            name.SetValue(TextBlock.ForegroundProperty, nameBrush);
            name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            name.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
            name.SetValue(TextBlock.MarginProperty, new Thickness(4, 2, 4, 2));
            name.SetValue(TextBlock.IsHitTestVisibleProperty, false);
            var cap = new FrameworkElementFactory(typeof(Border));
            cap.SetValue(Border.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Bottom);
            cap.SetValue(Border.BackgroundProperty, shade);
            cap.AppendChild(name);
            layer.AppendChild(cap);
        }

        layer.AppendChild(StackBadge());
        plate.AppendChild(layer);

        var stack = new FrameworkElementFactory(typeof(StackPanel));
        stack.SetValue(StackPanel.WidthProperty, _cardW);
        stack.SetValue(StackPanel.MarginProperty, new Thickness(_gap / 2.0));
        stack.AppendChild(plate);
        return new DataTemplate { VisualTree = stack };
    }

    private static FrameworkElementFactory StackBadge()
    {
        var ink = new SolidColorBrush(Color.FromArgb(0xFF, 0xF6, 0xEE, 0xE6));
        ink.Freeze();
        var fill = new SolidColorBrush(Color.FromArgb(0xE8, 0x3A, 0x28, 0x22));
        fill.Freeze();
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(ShelfTile.Badge)));
        text.SetValue(TextBlock.FontSizeProperty, 11.0);
        text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        text.SetValue(TextBlock.ForegroundProperty, ink);
        text.SetValue(TextBlock.MarginProperty, new Thickness(6, 0, 6, 1));
        text.SetValue(TextBlock.IsHitTestVisibleProperty, false);
        var box = new FrameworkElementFactory(typeof(Border));
        box.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(ShelfTile.IsStack)) { Converter = FlagVis.Instance });
        box.SetValue(Border.BackgroundProperty, fill);
        box.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        box.SetValue(Border.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Right);
        box.SetValue(Border.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Top);
        box.SetValue(Border.MarginProperty, new Thickness(0, 4, 4, 0));
        box.SetValue(UIElement.IsHitTestVisibleProperty, false);
        box.AppendChild(text);
        return box;
    }

    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        _pressTile = TileFrom(e.OriginalSource as DependencyObject);
        if (_pressTile is { IsHeader: true })
        {
            _pressTile = null;
            return;
        }

        _pressHost = HostOf(e.OriginalSource as DependencyObject);
        _press = e.GetPosition(this);
        _dragged = false;
        if (_layout == 15)
        {
            NoteStage(e.OriginalSource as DependencyObject);
            StagePanel.SetPop(_list, _pressScale);
            return;
        }

        Arm(_pressHost);
        SetScale(_pressHost, _pressScale);
    }

    private void OnTileMove(object sender, MouseEventArgs e)
    {
        if (_pressTile == null || e.LeftButton != MouseButtonState.Pressed || _dragged)
            return;
        Point now = e.GetPosition(this);
        if (Math.Abs(now.X - _press.X) + Math.Abs(now.Y - _press.Y) < 12)
            return;
        var tile = _pressTile;
        _dragged = true;
        _pressTile = null;
        SetScale(_pressHost, 1);
        if (_layout == 15)
            StagePanel.SetPop(_list, _hoverScale);
        _pressHost = null;
        if (!tile.Ghost && !File.Exists(tile.Launch) && !System.IO.Directory.Exists(tile.Launch))
            return;
        BeginCarry(tile);
    }

    private void DragTile(ShelfTile tile)
    {
        _selfDrop = false;
        _dragCancelled = false;
        var data = new DataObject();
        if (!tile.IsStack)
            data.SetData(OrderFormat, tile.Key);
        if (tile.Ghost)
            data.SetData(GhostFormat, tile.Clsid);
        else
        {
            var paths = tile.IsStack ? tile.StackPaths : new[] { tile.Launch };
            data.SetData(DataFormats.FileDrop, paths);
            ShelfFiles.BeginDrag(paths);
        }
        void OnQuery(object? _, QueryContinueDragEventArgs e)
        {
            if (!e.EscapePressed)
                return;
            _dragCancelled = true;
            e.Action = DragAction.Cancel;
            e.Handled = true;
        }

        DragDrop.AddQueryContinueDragHandler(_list, OnQuery);
        try
        {
            DragDrop.DoDragDrop(_list, data, DragDropEffects.Move | DragDropEffects.Copy);
        }
        finally
        {
            DragDrop.RemoveQueryContinueDragHandler(_list, OnQuery);
            ShelfFiles.EndDrag();
        }

        HideInsert();
        HideStackMark();
        if (!tile.Ghost || _selfDrop || _dragCancelled)
            return;
        try
        {
            ShelfFiles.ReturnGhost(tile.Clsid);
            Refresh();
        }
        catch
        {
            DesktopIcons.Toast("Не удалось вернуть на стол");
        }
    }

    private void BeginCarry(ShelfTile tile)
    {
        _carry = true;
        _carryTile = tile;
        _carryAt = _tiles.IndexOf(tile);
        _stackTarget = null;
        Mouse.Capture(this, CaptureMode.SubTree);
        Mouse.OverrideCursor = Cursors.SizeAll;
        ShowCarryHint(Mouse.GetPosition(this), "Сюда");
    }

    private void OnCarryMove(MouseEventArgs e)
    {
        if (!_carry || _carryTile == null)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            FinishCarry();
            return;
        }

        Point win = e.GetPosition(this);
        if (win.X < -56 || win.Y < -56 || win.X > ActualWidth + 56 || win.Y > ActualHeight + 56)
        {
            var tile = _carryTile;
            CancelCarry();
            if (tile != null)
                DragTile(tile);
            return;
        }

        Point inList = e.GetPosition(_list);
        var hit = TileUnder(inList);
        bool onto = hit != null
            && !ReferenceEquals(hit, _carryTile)
            && !hit.IsHeader
            && !hit.Ghost
            && !_carryTile.Ghost
            && hit.Members.Length > 0
            && Centered(hit, inList)
            && !SamePile(hit, _carryTile);
        _stackTarget = onto ? hit : null;
        if (onto)
            HideInsert();
        else
        {
            int next = IndexAt(inList);
            if (next != _carryAt)
            {
                _carryAt = next;
                PlaceInsert(_carryAt);
            }
        }

        bool edge = win.X < 0 || win.Y < 0 || win.X > ActualWidth || win.Y > ActualHeight;
        if (onto)
            ShowStackMark(hit!);
        else
            HideStackMark();
        ShowCarryHint(win, onto ? "Отпусти: сложится в «" + hit!.Name + "»" : edge ? "Дальше за край, на стол" : "Сюда, между значками");
    }

    private void FinishCarry()
    {
        var tile = _carryTile;
        var target = _stackTarget;
        int index = _carryAt;
        CancelCarry();
        if (tile == null)
            return;
        if (target != null && target.Members.Length > 0)
        {
            var incoming = new List<string>(target.Members);
            incoming.AddRange(tile.Members);
            string? id = ShelfFiles.Join(target.Members[0], incoming);
            if (id != null)
                _openStack = null;
            Refresh();
            return;
        }

        var prefs = ShelfFiles.LoadPrefs();
        bool held = !tile.IsStack && ShelfArrange.Held(ShelfFiles.List(), tile.Key, prefs.Stacks, prefs.Loose);
        string? anchor = AnchorKey(index, tile);
        if (!ShelfFiles.MoveBlock(tile.Members, anchor))
            return;
        if (held)
            ShelfFiles.Separate(tile.Key);
        Refresh();
    }

    private void CancelCarry()
    {
        _carry = false;
        _carryTile = null;
        _stackTarget = null;
        _hint = "";
        if (Mouse.Captured == this)
            Mouse.Capture(null);
        Mouse.OverrideCursor = null;
        HideInsert();
        HideStackMark();
        if (_layout != 15)
            _ringCap.Visibility = Visibility.Collapsed;
    }

    private string _hint = "";

    private void ShowCarryHint(Point inWindow, string text)
    {
        if (!string.Equals(_hint, text, StringComparison.Ordinal))
        {
            _hint = text;
            _ringText.Text = text;
            _ringText.FontSize = 13;
            _ringCap.Measure(new System.Windows.Size(260, 40));
        }

        double w = Math.Max(36, _ringCap.DesiredSize.Width);
        double x = Math.Clamp(inWindow.X + 14, 4, Math.Max(4, ActualWidth - w - 4));
        double y = Math.Clamp(inWindow.Y - 34, 4, Math.Max(4, ActualHeight - 32));
        _ringCap.Margin = new Thickness(x, y, 0, 0);
        _ringCap.Visibility = Visibility.Visible;
    }

    private ShelfTile? TileUnder(Point inList)
    {
        ShelfTile? nearest = null;
        double best = double.MaxValue;
        for (int i = 0; i < _tiles.Count; i++)
        {
            if (_tiles[i].IsHeader)
                continue;
            if (_list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement item)
                continue;
            Point origin = item.TranslatePoint(new Point(0, 0), _list);
            if (inList.X < origin.X || inList.Y < origin.Y || inList.X > origin.X + item.ActualWidth || inList.Y > origin.Y + item.ActualHeight)
                continue;
            double dx = inList.X - (origin.X + item.ActualWidth / 2);
            double dy = inList.Y - (origin.Y + item.ActualHeight / 2);
            double dist = dx * dx + dy * dy;
            if (dist >= best)
                continue;
            best = dist;
            nearest = _tiles[i];
        }

        return nearest;
    }

    private bool Centered(ShelfTile tile, Point inList)
    {
        int index = _tiles.IndexOf(tile);
        if (index < 0 || _list.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement item)
            return false;
        if (item.ActualWidth < 8 || item.ActualHeight < 8)
            return false;
        Point origin = item.TranslatePoint(new Point(0, 0), _list);
        double nx = (inList.X - origin.X) / item.ActualWidth;
        double ny = (inList.Y - origin.Y) / item.ActualHeight;
        return nx is > 0.06 and < 0.94 && ny is > 0.04 and < 0.88;
    }

    private static bool SamePile(ShelfTile left, ShelfTile right)
    {
        if (left.Members.Length == 0 || right.Members.Length == 0)
            return false;
        foreach (string key in left.Members)
        {
            if (right.Members.Any(item => string.Equals(item, key, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    private string? AnchorKey(int index, ShelfTile carried)
    {
        int start = Math.Clamp(index, 0, _tiles.Count);
        for (int i = start; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];
            if (tile.IsHeader || ReferenceEquals(tile, carried) || tile.Members.Length == 0)
                continue;
            return tile.Members[0];
        }

        return null;
    }

    private const uint MenuLeft = 0x7F10;
    private const uint MenuRight = 0x7F11;
    private const uint MenuApart = 0x7F12;
    private const uint MenuGather = 0x7F13;
    private const uint MenuBreak = 0x7F14;

    private (uint Id, string Label)[] ShelfMenu(ShelfTile tile)
    {
        var prefs = ShelfFiles.LoadPrefs();
        var disk = ShelfFiles.List();
        bool held = !tile.IsStack && !tile.IsHeader && ShelfArrange.Held(disk, tile.Key, prefs.Stacks, prefs.Loose);
        var list = new List<(uint Id, string Label)>();
        if (tile.Members.Length > 0)
        {
            list.Add((MenuLeft, "Влево"));
            list.Add((MenuRight, "Вправо"));
        }

        if (tile.IsStack)
            list.Add((MenuBreak, "Разобрать стопку"));
        else if (held)
            list.Add((MenuApart, "Достать из стопки"));
        else if (!tile.Ghost && ShelfFiles.IsLoose(tile.Key))
            list.Add((MenuApart, "Можно складывать"));
        else if (!tile.Ghost)
            list.Add((MenuApart, "Держать отдельно"));
        if (!tile.Ghost && tile.Launch.Length > 0)
        {
            string file = System.IO.Path.GetFileName(tile.Launch);
            if (file.Length > 0)
            {
                var sample = new ShelfEntry(tile.Launch, file, 0, false, tile.Launch, "", false);
                list.Add((MenuGather, ShelfArrange.GatherTitle(ShelfArrange.Bucket(sample))));
            }
        }

        return list.ToArray();
    }

    private void RunShelfMenu(ShelfTile tile, uint id)
    {
        try
        {
            if (id == MenuLeft)
                Nudge(tile, -1);
            else if (id == MenuRight)
                Nudge(tile, 1);
            else if (id == MenuBreak)
            {
                ShelfFiles.Break(tile.Members);
                if (string.Equals(_openStack, tile.Key, StringComparison.OrdinalIgnoreCase))
                    _openStack = null;
                Refresh();
            }
            else if (id == MenuApart)
            {
                var prefs = ShelfFiles.LoadPrefs();
                bool held = ShelfArrange.Held(ShelfFiles.List(), tile.Key, prefs.Stacks, prefs.Loose);
                if (!held && ShelfFiles.IsLoose(tile.Key))
                    ShelfFiles.Release(tile.Key);
                else
                    ShelfFiles.Separate(tile.Key);
                Refresh();
            }
            else if (id == MenuGather)
            {
                string key = tile.Launch.Length > 0 ? tile.Launch : tile.Key;
                if (!ShelfFiles.GatherLike(key))
                    DesktopIcons.Toast("Таких файлов меньше двух");
                else
                {
                    _openStack = null;
                    Refresh();
                }
            }
        }
        catch
        {
            DesktopIcons.Toast("Не вышло");
        }
    }

    private void Nudge(ShelfTile tile, int dir)
    {
        int at = _tiles.IndexOf(tile);
        if (at < 0 || tile.Members.Length == 0)
            return;
        int neighbor = -1;
        for (int i = at + dir; i >= 0 && i < _tiles.Count; i += dir)
        {
            if (_tiles[i].IsHeader)
                continue;
            neighbor = i;
            break;
        }

        if (neighbor < 0)
            return;
        string? anchor = null;
        if (dir < 0)
            anchor = _tiles[neighbor].Members.FirstOrDefault();
        else
        {
            for (int i = neighbor + 1; i < _tiles.Count; i++)
            {
                if (_tiles[i].IsHeader || ReferenceEquals(_tiles[i], tile) || _tiles[i].Members.Length == 0)
                    continue;
                anchor = _tiles[i].Members[0];
                break;
            }
        }

        ShelfFiles.MoveBlock(tile.Members, anchor);
        Refresh();
    }

    private int IndexAt(Point inList)
    {
        int nearest = -1;
        double best = double.MaxValue;
        double midX = 0;
        for (int i = 0; i < _tiles.Count; i++)
        {
            if (_tiles[i].IsHeader)
                continue;
            if (_list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement item)
                continue;
            Point origin = item.TranslatePoint(new Point(0, 0), _list);
            double cx = origin.X + item.ActualWidth / 2;
            double cy = origin.Y + item.ActualHeight / 2;
            double dx = inList.X - cx;
            double dy = inList.Y - cy;
            double dist = dx * dx + dy * dy;
            if (dist >= best)
                continue;
            best = dist;
            nearest = i;
            midX = cx;
        }

        if (nearest < 0)
            return _tiles.Count;
        return inList.X >= midX ? nearest + 1 : nearest;
    }

    private void PlaceInsert(int index)
    {
        if (_tiles.Count == 0)
        {
            _insert.Visibility = Visibility.Collapsed;
            return;
        }

        int look = Math.Min(index, _tiles.Count - 1);
        if (_list.ItemContainerGenerator.ContainerFromIndex(look) is not FrameworkElement item)
        {
            _insert.Visibility = Visibility.Collapsed;
            return;
        }

        Point origin = item.TranslatePoint(new Point(0, 0), this);
        double x = origin.X;
        if (index > look)
            x += item.ActualWidth;
        _insert.Margin = new Thickness(Math.Max(0, x - 1), origin.Y + 8, 0, 0);
        _insert.Visibility = Visibility.Visible;
    }

    private void HideInsert() => _insert.Visibility = Visibility.Collapsed;

    private void ShowStackMark(ShelfTile tile)
    {
        int index = _tiles.IndexOf(tile);
        if (index < 0 || _list.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement item)
        {
            _stackMark.Visibility = Visibility.Collapsed;
            return;
        }

        Point origin = item.TranslatePoint(new Point(0, 0), this);
        _stackMark.Width = Math.Max(12, item.ActualWidth - 6);
        _stackMark.Height = Math.Max(12, item.ActualHeight - 6);
        _stackMark.Margin = new Thickness(origin.X + 3, origin.Y + 3, 0, 0);
        _stackMark.Visibility = Visibility.Visible;
    }

    private void HideStackMark() => _stackMark.Visibility = Visibility.Collapsed;

    private void OnTileUp(object sender, MouseButtonEventArgs e)
    {
        var tile = _pressTile;
        var host = _pressHost;
        _pressTile = null;
        _pressHost = null;
        if (_layout == 15)
        {
            StagePanel.SetPop(_list, _hoverScale);
            if (!IsMouseOver)
                ClearRingHover();
        }
        SetScale(host, host != null && host.IsMouseOver ? _hoverScale : 1);
        if (tile is { IsHeader: true })
            return;
        if (tile == null || _dragged)
            return;
        if (tile.IsStack)
        {
            _openStack = string.Equals(_openStack, tile.Key, StringComparison.OrdinalIgnoreCase) ? null : tile.Key;
            Refresh();
            return;
        }
        try
        {
            if (tile.Ghost)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", tile.Launch) { UseShellExecute = true })?.Dispose();
            else
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tile.Launch) { UseShellExecute = true })?.Dispose();
        }
        catch
        {
            DesktopIcons.Toast("Не открылось");
        }
    }

    private void OnTileMenu(object sender, MouseButtonEventArgs e)
    {
        var tile = TileFrom(e.OriginalSource as DependencyObject);
        if (tile == null)
        {
            ShowShelfAddMenu();
            e.Handled = true;
            return;
        }

        if (tile.IsHeader)
            return;
        string launch = tile.Launch;
        bool ghost = tile.Ghost;
        string clsid = tile.Clsid;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var more = ShelfMenu(tile);
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                ShellContextMenu.Show(hwnd, launch, shift, () =>
                {
                    try
                    {
                        if (ghost)
                            ShelfFiles.ReturnGhost(clsid);
                        else
                            ShelfFiles.ReturnToDesktop(launch);
                    }
                    catch
                    {
                        DesktopIcons.Toast("Не удалось вернуть на стол");
                    }

                    Refresh();
                }, more, id => RunShelfMenu(tile, id));
            }
            catch
            {
                DesktopIcons.Toast("Меню не открылось");
            }
        });
        e.Handled = true;
    }

    private void QueueThumb(ShelfTile tile)
    {
        if (tile.Launch.Length == 0)
            return;
        lock (_thumbs)
            _thumbs.Enqueue(tile);
        EnsurePump();
    }

    private void EnsurePump()
    {
        if (Interlocked.CompareExchange(ref _thumbPump, 1, 0) != 0)
            return;
        _ = Task.Run(PumpThumbs);
    }

    private void PumpThumbs()
    {
        var ready = new List<(ShelfTile Tile, byte[] Png)>();
        try
        {
            while (true)
            {
                ShelfTile? tile;
                lock (_thumbs)
                {
                    if (!_thumbs.TryDequeue(out tile))
                        break;
                }

                byte[]? png = RenderThumb(tile.Launch, tile.Photo);
                if (png != null)
                    ready.Add((tile, png));
            }

            if (ready.Count > 0)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    foreach (var (tile, png) in ready)
                    {
                        if (!tile.Ghost && !File.Exists(tile.Launch) && !System.IO.Directory.Exists(tile.Launch))
                            continue;
                        tile.Icon = FreezePng(png);
                    }
                });
            }
        }
        finally
        {
            Interlocked.Exchange(ref _thumbPump, 0);
            bool more;
            lock (_thumbs)
                more = _thumbs.Count > 0;
            if (more)
                EnsurePump();
        }
    }

    private static byte[]? RenderThumb(string path, bool photo)
    {
        if (path.Length == 0 || !photo)
            return IconPng(path);
        try
        {
            var info = new FileInfo(path);
            if (info.Length is <= 0 or >= 40_000_000)
                return IconPng(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var src = new System.Drawing.Bitmap(stream);
            const int max = 128;
            int w;
            int h;
            if (src.Width >= src.Height)
            {
                w = max;
                h = Math.Max(1, src.Height * max / Math.Max(1, src.Width));
            }
            else
            {
                h = max;
                w = Math.Max(1, src.Width * max / Math.Max(1, src.Height));
            }

            using var thumb = new System.Drawing.Bitmap(w, h);
            using (var g = System.Drawing.Graphics.FromImage(thumb))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
                g.DrawImage(src, 0, 0, w, h);
            }

            using var ms = new MemoryStream();
            thumb.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }
        catch
        {
            return IconPng(path);
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    private static byte[]? IconPng(string path)
    {
        if (path.StartsWith("::", StringComparison.Ordinal))
            return NamespaceIcon(path);
        string ext = Path.GetExtension(path);
        bool unique = ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ico", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("::", StringComparison.Ordinal);
        string key = unique || ext.Length == 0 ? path : ext;
        if (IconCache.TryGetValue(key, out byte[]? cached))
            return cached;
        byte[]? png = LoadShellIcon(path);
        IconCache[key] = png;
        return png;
    }

    private static byte[]? NamespaceIcon(string parsing)
    {
        if (IconCache.TryGetValue(parsing, out byte[]? cached))
            return cached;
        IntPtr pidl = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(parsing, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
            {
                IconCache[parsing] = null;
                return null;
            }

            var info = new ShFileInfo();
            IntPtr result = SHGetFileInfo(pidl, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), 0x100 | 0x8);
            if (result == IntPtr.Zero || info.Icon == IntPtr.Zero)
            {
                IconCache[parsing] = null;
                return null;
            }

            try
            {
                byte[] png = PngFromIcon(info.Icon);
                IconCache[parsing] = png;
                return png;
            }
            finally
            {
                DestroyIcon(info.Icon);
            }
        }
        catch
        {
            IconCache[parsing] = null;
            return null;
        }
        finally
        {
            if (pidl != IntPtr.Zero)
                ILFree(pidl);
        }
    }

    private static byte[]? LoadShellIcon(string path)
    {
        try
        {
            var info = new ShFileInfo();
            IntPtr result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), 0x100);
            if (result == IntPtr.Zero || info.Icon == IntPtr.Zero)
                return null;
            try { return PngFromIcon(info.Icon); }
            finally { DestroyIcon(info.Icon); }
        }
        catch
        {
            return null;
        }
    }

    private static byte[] PngFromIcon(IntPtr icon)
    {
        using var drawn = System.Drawing.Icon.FromHandle(icon);
        using var bmp = drawn.ToBitmap();
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    private static ImageSource? FreezePng(byte[] png)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(png);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? ReadIdList(IDataObject data)
    {
        byte[]? managed = ReadManagedIdList(data);
        return managed ?? ReadComIdList(data);
    }

    private static byte[]? ReadManagedIdList(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent(ShelfShell.IdListFormat, autoConvert: false))
                return null;
            object raw = data.GetData(ShelfShell.IdListFormat, autoConvert: false);
            return raw switch
            {
                byte[] bytes => bytes,
                MemoryStream memory => memory.ToArray(),
                Stream stream => ReadStream(stream),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static byte[] ReadStream(Stream stream)
    {
        if (stream.CanSeek)
            stream.Position = 0;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static bool HasShellIdList(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(ShelfShell.IdListFormat, autoConvert: false))
                return true;
        }
        catch
        {
            // ниже спросим сам объект проводника
        }

        try
        {
            var com = OleOf(data);
            if (com == null)
                return false;
            var etc = ShellEtc();
            return com.QueryGetData(ref etc) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static byte[]? ReadComIdList(IDataObject data)
    {
        System.Runtime.InteropServices.ComTypes.STGMEDIUM medium = default;
        var locked = false;
        var taken = false;
        try
        {
            var com = OleOf(data);
            if (com == null)
                return null;
            var etc = ShellEtc();
            if (com.QueryGetData(ref etc) != 0)
                return null;
            com.GetData(ref etc, out medium);
            taken = true;
            if (medium.unionmember == IntPtr.Zero)
                return null;
            IntPtr ptr = GlobalLock(medium.unionmember);
            if (ptr == IntPtr.Zero)
                return null;
            locked = true;
            int size = (int)GlobalSize(medium.unionmember).ToUInt32();
            if (size < 8)
                return null;
            var bytes = new byte[size];
            Marshal.Copy(ptr, bytes, 0, size);
            return bytes;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (locked)
                GlobalUnlock(medium.unionmember);
            if (taken)
                ReleaseStgMedium(ref medium);
        }
    }

    private static System.Runtime.InteropServices.ComTypes.IDataObject? OleOf(IDataObject data)
    {
        if (data is System.Runtime.InteropServices.ComTypes.IDataObject direct)
            return direct;
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        foreach (var field in data.GetType().GetFields(flags))
        {
            if (field.GetValue(data) is System.Runtime.InteropServices.ComTypes.IDataObject inner)
                return inner;
        }

        return null;
    }

    private static short _shellIdList;

    private static System.Runtime.InteropServices.ComTypes.FORMATETC ShellEtc()
    {
        if (_shellIdList == 0)
            _shellIdList = (short)RegisterClipboardFormat(ShelfShell.IdListFormat);
        return new System.Runtime.InteropServices.ComTypes.FORMATETC
        {
            cfFormat = _shellIdList,
            dwAspect = System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL
        };
    }

    private void HookTiles()
    {
        if (_layout is >= 12 and <= 16)
            return;
        for (int i = 0; i < _tiles.Count; i++)
        {
            if (_list.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement host)
                Arm(host);
        }
    }

    private static bool HasFileDrop(IDataObject data)
    {
        try
        {
            return data.GetDataPresent(DataFormats.FileDrop);
        }
        catch
        {
            return false;
        }
    }

    private static DragDropEffects Offer(DragEventArgs e)
    {
        DragDropEffects allowed = e.AllowedEffects & (DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        if ((allowed & DragDropEffects.Copy) != 0)
            return DragDropEffects.Copy;
        if ((allowed & DragDropEffects.Link) != 0)
            return DragDropEffects.Link;
        if ((allowed & DragDropEffects.Move) != 0)
            return DragDropEffects.Move;
        return DragDropEffects.Copy;
    }

    private void Arm(FrameworkElement? host)
    {
        if (host == null || host.RenderTransform is ScaleTransform)
            return;
        host.RenderTransformOrigin = new Point(0.5, 0.5);
        host.RenderTransform = new ScaleTransform(1, 1);
        host.MouseEnter += OnTileEnter;
        host.MouseLeave += OnTileLeave;
        Panel.SetZIndex(host, 0);
    }

    private void OnTileEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement host)
            return;
        Panel.SetZIndex(host, 30);
        SetScale(host, ReferenceEquals(host, _pressHost) ? _pressScale : _hoverScale);
    }

    private void OnTileLeave(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement host)
            return;
        Panel.SetZIndex(host, 0);
        if (ReferenceEquals(host, _pressHost))
            return;
        SetScale(host, 1);
    }

    private static void SetScale(FrameworkElement? host, double scale)
    {
        if (host?.RenderTransform is not ScaleTransform transform)
            return;
        if (Math.Abs(transform.ScaleX - scale) < 0.001)
            return;
        transform.ScaleX = scale;
        transform.ScaleY = scale;
    }

    private static FrameworkElement? HostOf(DependencyObject? source)
    {
        FrameworkElement? found = null;
        while (source != null)
        {
            if (source is FrameworkElement element && element.DataContext is ShelfTile)
                found = element;
            source = VisualTreeHelper.GetParent(source);
        }

        return found;
    }

    private static ShelfTile? TileFrom(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is FrameworkElement element && element.DataContext is ShelfTile tile)
                return tile;
            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private void StoreOpen(bool open)
    {
        var prefs = ShelfFiles.LoadPrefs();
        prefs.Open = open;
        ShelfFiles.SavePrefs(prefs);
    }

    private void ApplyToolStyle()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        const int GwlExstyle = -20;
        const int WsExToolwindow = 0x00000080;
        const int WsExNoactivate = 0x08000000;
        int style = GetWindowLong(hwnd, GwlExstyle);
        SetWindowLong(hwnd, GwlExstyle, style | WsExToolwindow | WsExNoactivate);
    }

    private sealed class IslandPanel : Panel
    {
        private readonly List<Rect> _mats = new();
        private static readonly SolidColorBrush Mat = CreateMat();
        private static readonly System.Windows.Media.Pen Rim = CreateRim();

        private static SolidColorBrush CreateMat()
        {
            var brush = new SolidColorBrush(Color.FromArgb(0xD8, 0x16, 0x12, 0x10));
            brush.Freeze();
            return brush;
        }

        private static System.Windows.Media.Pen CreateRim()
        {
            var pen = new System.Windows.Media.Pen(new SolidColorBrush(Color.FromArgb(0x66, 0xF0, 0xE0, 0xC8)), 1);
            pen.Brush.Freeze();
            pen.Freeze();
            return pen;
        }

        protected override System.Windows.Size MeasureOverride(System.Windows.Size available)
        {
            double width = double.IsInfinity(available.Width) ? 640 : Math.Max(1, available.Width);
            double y = 4;
            double rowH = 0;
            double x = 8;
            bool row = false;
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(new System.Windows.Size(width, double.PositiveInfinity));
                if (Header(child))
                {
                    if (row)
                    {
                        y += rowH + 10;
                        rowH = 0;
                        x = 8;
                        row = false;
                    }

                    y += Math.Max(22, child.DesiredSize.Height);
                    continue;
                }

                double dw = Math.Max(1, child.DesiredSize.Width);
                if (row && x + dw > width - 4)
                {
                    y += rowH;
                    x = 8;
                    rowH = 0;
                }

                x += dw;
                rowH = Math.Max(rowH, child.DesiredSize.Height);
                row = true;
            }

            if (row)
                y += rowH;
            return new System.Windows.Size(width, y + 12);
        }

        protected override System.Windows.Size ArrangeOverride(System.Windows.Size final)
        {
            _mats.Clear();
            double y = 4;
            double rowH = 0;
            double x = 8;
            double rowY = 0;
            bool row = false;
            double matLeft = 8;
            double matTop = 0;
            bool mat = false;
            foreach (UIElement child in InternalChildren)
            {
                if (Header(child))
                {
                    if (row)
                    {
                        y += rowH + 10;
                        AddMat(matLeft, matTop, x, y - 10);
                        rowH = 0;
                        x = 8;
                        row = false;
                        mat = false;
                    }

                    double hh = Math.Max(22, child.DesiredSize.Height);
                    child.Arrange(new Rect(8, y, Math.Max(0, final.Width - 16), hh));
                    y += hh;
                    continue;
                }

                double dw = Math.Max(1, child.DesiredSize.Width);
                double dh = Math.Max(1, child.DesiredSize.Height);
                if (row && x + dw > final.Width - 4)
                {
                    y += rowH;
                    x = 8;
                    rowH = 0;
                }

                if (!mat)
                {
                    mat = true;
                    matLeft = x;
                    matTop = y;
                }

                child.Arrange(new Rect(x, y, dw, dh));
                x += dw;
                rowH = Math.Max(rowH, dh);
                rowY = y;
                row = true;
            }

            if (mat)
                AddMat(matLeft, matTop, x, rowY + rowH);
            InvalidateVisual();
            return final;
        }

        private void AddMat(double left, double top, double right, double bottom)
        {
            double w = Math.Max(0, right - left + 12);
            double h = Math.Max(0, bottom - top + 8);
            if (w < 8 || h < 8)
                return;
            _mats.Add(new Rect(Math.Max(0, left - 6), Math.Max(0, top - 4), w, h));
        }

        protected override void OnRender(DrawingContext dc)
        {
            foreach (Rect rect in _mats)
            {
                double w = Math.Min(rect.Width, Math.Max(0, ActualWidth - rect.X));
                if (w < 8 || rect.Height < 8)
                    continue;
                dc.DrawRoundedRectangle(Mat, Rim, new Rect(rect.X, rect.Y, w, rect.Height), 16, 16);
            }
        }

        private static bool Header(UIElement child)
        {
            if (child is FrameworkElement fe && fe.DataContext is ShelfTile tile)
                return tile.IsHeader;
            return false;
        }
    }

    private sealed class StagePanel : Panel
    {
        public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached(
            "Kind", typeof(int), typeof(StagePanel),
            new FrameworkPropertyMetadata(12, FrameworkPropertyMetadataOptions.AffectsArrange));

        public static readonly DependencyProperty FocusProperty = DependencyProperty.RegisterAttached(
            "Focus", typeof(int), typeof(StagePanel),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsArrange));

        public static readonly DependencyProperty PopProperty = DependencyProperty.RegisterAttached(
            "Pop", typeof(double), typeof(StagePanel),
            new FrameworkPropertyMetadata(1.2, FrameworkPropertyMetadataOptions.AffectsArrange));

        public static void SetKind(DependencyObject element, int value) => element.SetValue(KindProperty, value);
        public static int GetKind(DependencyObject element) => (int)element.GetValue(KindProperty);
        public static void SetFocus(DependencyObject element, int value) => element.SetValue(FocusProperty, value);
        public static int GetFocus(DependencyObject element) => (int)element.GetValue(FocusProperty);
        private static double _ringFit = 1.2;

        public static void SetPop(DependencyObject element, double value)
        {
            if (value >= 1)
                _ringFit = Math.Clamp(value, 1, 1.45);
            element.SetValue(PopProperty, value);
        }
        public static double GetPop(DependencyObject element) => (double)element.GetValue(PopProperty);

        internal static System.Windows.Size Preferred(int kind, int count, double dw, double dh, double room, double workH, double pop)
        {
            count = Math.Max(1, count);
            dw = Math.Max(24, dw);
            dh = Math.Max(24, dh);
            pop = Math.Clamp(pop, 1, 1.45);
            var (natW, natH) = Natural(kind, count, dw, dh, pop);
            double limitW = Math.Max(dw + 32, room);
            double limitH = Math.Max(dh + 32, workH * (kind == 15 ? 0.72 : 0.8));
            double k = Math.Min(1, Math.Min(limitW / natW, limitH / natH));
            return new System.Windows.Size(Math.Ceiling(natW * k) + 12, Math.Ceiling(natH * k) + 12);
        }

        protected override System.Windows.Size MeasureOverride(System.Windows.Size available)
        {
            foreach (UIElement child in InternalChildren)
                child.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = double.IsInfinity(available.Width) ? 480 : Math.Max(1, available.Width);
            double h = double.IsInfinity(available.Height) ? 220 : Math.Max(1, available.Height);
            return new System.Windows.Size(w, h);
        }

        protected override System.Windows.Size ArrangeOverride(System.Windows.Size final)
        {
            int n = InternalChildren.Count;
            if (n == 0)
                return final;
            int kind = 12;
            int focus = 0;
            double pop = 1.2;
            if (ItemsControl.GetItemsOwner(this) is ItemsControl owner)
            {
                kind = GetKind(owner);
                pop = GetPop(owner);
                focus = GetFocus(owner);
                if (kind == 15)
                    focus = focus >= 0 && focus < n ? focus : -1;
                else
                    focus = Math.Clamp(focus, 0, n - 1);
            }

            double dw = 1;
            double dh = 1;
            for (int i = 0; i < n; i++)
            {
                dw = Math.Max(dw, InternalChildren[i].DesiredSize.Width);
                dh = Math.Max(dh, InternalChildren[i].DesiredSize.Height);
            }

            var spots = Build(kind, n, focus, dw, dh, final.Width, final.Height, pop);
            for (int i = 0; i < n; i++)
            {
                var spot = spots[i];
                Pose(InternalChildren[i], new Rect(spot.X, spot.Y, dw, dh), spot.Scale, spot.Angle, spot.Z, spot.Origin);
            }

            return final;
        }

        protected override void OnRender(DrawingContext dc)
        {
            int kind = 12;
            if (ItemsControl.GetItemsOwner(this) is ItemsControl owner)
                kind = GetKind(owner);
            if (kind is not (12 or 14 or 16))
                return;
            dc.DrawRectangle(FloorBrush(), null, new Rect(0, ActualHeight * 0.42, ActualWidth, ActualHeight * 0.58));
        }

        private readonly struct Spot
        {
            public Spot(double x, double y, double scale, double angle, int z, Point origin)
            {
                X = x;
                Y = y;
                Scale = scale;
                Angle = angle;
                Z = z;
                Origin = origin;
            }

            public double X { get; }
            public double Y { get; }
            public double Scale { get; }
            public double Angle { get; }
            public int Z { get; }
            public Point Origin { get; }
        }

        private static (double W, double H) Natural(int kind, int count, double dw, double dh, double pop = 1.2)
        {
            int focus = count / 2;
            return kind switch
            {
                13 => CinemaBox(count, dw, dh),
                14 => FanBox(count, focus, dw, dh),
                15 => RingBox(count, dw, dh, pop),
                16 => ThroneBox(count, dw, dh),
                _ => CoverBox(count, dw, dh)
            };
        }

        private static Spot[] Build(int kind, int count, int focus, double dw, double dh, double width, double height, double pop)
        {
            var (natW, natH) = Natural(kind, count, dw, dh, pop);
            double k = Math.Min(1, Math.Min((width - 8) / Math.Max(1, natW), (height - 8) / Math.Max(1, natH)));
            double ox = Math.Max(4, (width - natW * k) / 2);
            double oy = Math.Max(4, (height - natH * k) / 2);
            return kind switch
            {
                13 => Cinema(count, focus, dw, dh, k, ox, oy),
                14 => Fan(count, focus, dw, dh, k, ox, oy),
                15 => Ring(count, focus, dw, dh, width, height, pop),
                16 => Throne(count, focus, dw, dh, k, ox, oy, width),
                _ => Cover(count, focus, dw, dh, k, ox, oy)
            };
        }

        private static double[] CoverScales(int count, int focus)
        {
            var scales = new double[count];
            for (int i = 0; i < count; i++)
            {
                int dist = Math.Abs(i - focus);
                scales[i] = dist == 0 ? 1.62 : dist == 1 ? 1.08 : 0.78;
            }

            return scales;
        }

        private static (double W, double H) CoverBox(int count, double dw, double dh)
        {
            double[] scales = CoverScales(count, count / 2);
            double width = 8;
            for (int i = 0; i < count; i++)
            {
                double vw = dw * scales[i];
                width += vw + (i == count - 1 ? 0 : 10);
            }

            return (width + 8, dh * 1.62 + 20);
        }

        private static Spot[] Cover(int count, int focus, double dw, double dh, double k, double ox, double oy)
        {
            double[] scales = CoverScales(count, focus);
            var spots = new Spot[count];
            double x = ox + 8 * k;
            double bottom = oy + (dh * 1.62 + 10) * k;
            var origin = new Point(0.5, 1);
            for (int i = 0; i < count; i++)
            {
                double scale = scales[i] * k;
                double vw = dw * scale;
                double vh = dh * scale;
                double visualLeft = x;
                double visualTop = bottom - vh;
                spots[i] = new Spot(
                    visualLeft - (dw - vw) / 2,
                    visualTop - dh + vh,
                    scale,
                    0,
                    30 - Math.Abs(i - focus),
                    origin);
                x += vw + (i == count - 1 ? 0 : 10 * k);
            }

            return spots;
        }

        private static (double W, double H) CinemaBox(int count, double dw, double dh)
        {
            int others = Math.Max(0, count - 1);
            int rows = others > 4 ? 2 : 1;
            int cols = others == 0 ? 0 : (int)Math.Ceiling(others / (double)rows);
            double heroW = dw * 1.7;
            double heroH = dh * 1.7;
            double gridW = cols == 0 ? 0 : cols * dw + Math.Max(0, cols - 1) * 8;
            double gridH = others == 0 ? 0 : rows * dh + Math.Max(0, rows - 1) * 8;
            return (24 + heroW + (cols > 0 ? 28 + gridW : 0) + 16, 24 + Math.Max(heroH, gridH) + 20);
        }

        private static Spot[] Cinema(int count, int focus, double dw, double dh, double k, double ox, double oy)
        {
            int others = Math.Max(0, count - 1);
            int rows = others > 4 ? 2 : 1;
            int cols = others == 0 ? 0 : (int)Math.Ceiling(others / (double)rows);
            double heroS = 1.7 * k;
            double heroW = dw * heroS;
            double heroH = dh * heroS;
            double gridW = cols == 0 ? 0 : cols * dw * k + Math.Max(0, cols - 1) * 8 * k;
            double gridH = others == 0 ? 0 : rows * dh * k + Math.Max(0, rows - 1) * 8 * k;
            double blockH = Math.Max(heroH, gridH);
            var spots = new Spot[count];
            double visualLeft = ox + 12 * k;
            double visualTop = oy + 10 * k + (blockH - heroH) / 2;
            spots[focus] = new Spot(
                visualLeft - dw / 2 + heroW / 2,
                visualTop - dh / 2 + heroH / 2,
                heroS,
                0,
                8,
                new Point(0.5, 0.5));
            double gridX = visualLeft + heroW + 28 * k;
            double gridY = oy + 10 * k + (blockH - gridH) / 2;
            int seen = 0;
            var rest = new Point(0.5, 0.5);
            for (int i = 0; i < count; i++)
            {
                if (i == focus)
                    continue;
                int col = cols == 0 ? 0 : seen % cols;
                int row = cols == 0 ? 0 : seen / cols;
                double vw = dw * k;
                double vh = dh * k;
                double left = gridX + col * (vw + 8 * k);
                double top = gridY + row * (vh + 8 * k);
                spots[i] = new Spot(left - (dw - vw) / 2, top - (dh - vh) / 2, k, 0, 1, rest);
                seen++;
            }

            return spots;
        }

        private static (double W, double H) FanBox(int count, int focus, double dw, double dh)
        {
            var raw = FanRaw(count, focus, dw, dh, 1);
            var (minX, minY, maxX, maxY) = Union(raw, dw, dh);
            return (maxX - minX + 36, maxY - minY + 36);
        }

        private static Spot[] Fan(int count, int focus, double dw, double dh, double k, double ox, double oy)
        {
            var raw = FanRaw(count, focus, dw, dh, k);
            var (minX, minY, _, _) = Union(raw, dw, dh);
            var spots = new Spot[count];
            for (int i = 0; i < count; i++)
            {
                var spot = raw[i];
                spots[i] = new Spot(ox + spot.X - minX + 16 * k, oy + spot.Y - minY + 16 * k, spot.Scale, spot.Angle, spot.Z, spot.Origin);
            }

            return spots;
        }

        private static Spot[] FanRaw(int count, int focus, double dw, double dh, double k)
        {
            double span = Math.Min(54, Math.Max(20, (count - 1) * 6.4));
            double step = dw * 0.78 * k;
            var origin = new Point(0.5, 1);
            var spots = new Spot[count];
            for (int i = 0; i < count; i++)
            {
                double t = count == 1 ? 0.5 : i / (double)(count - 1);
                double angle = -span / 2 + span * t;
                double scale = k;
                double lift = 0;
                int z = i;
                if (i == focus)
                {
                    angle *= 0.18;
                    scale = 1.16 * k;
                    lift = dh * 0.2 * k;
                    z = 40;
                }

                double cx = dw + i * step;
                spots[i] = new Spot(cx - dw / 2, -lift, scale, angle, z, origin);
            }

            return spots;
        }

        private static (double MinX, double MinY, double MaxX, double MaxY) Union(Spot[] spots, double dw, double dh)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var spot in spots)
            {
                var box = Transformed(spot, dw, dh);
                minX = Math.Min(minX, box.X);
                minY = Math.Min(minY, box.Y);
                maxX = Math.Max(maxX, box.Right);
                maxY = Math.Max(maxY, box.Bottom);
            }

            if (spots.Length == 0)
                return (0, 0, dw, dh);
            return (minX, minY, maxX, maxY);
        }

        private static Rect Transformed(Spot spot, double dw, double dh)
        {
            double ox = spot.Origin.X * dw;
            double oy = spot.Origin.Y * dh;
            double rad = spot.Angle * Math.PI / 180.0;
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            double[,] corners = { { 0, 0 }, { dw, 0 }, { dw, dh }, { 0, dh } };
            for (int i = 0; i < 4; i++)
            {
                double px = corners[i, 0];
                double py = corners[i, 1];
                double rx = (px - ox) * spot.Scale;
                double ry = (py - oy) * spot.Scale;
                double x = spot.X + ox + rx * cos - ry * sin;
                double y = spot.Y + oy + rx * sin + ry * cos;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }

            return new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
        }

        private static (double W, double H) RingBox(int count, double dw, double dh, double pop)
        {
            double item = Math.Max(dw, dh);
            double grow = Math.Clamp(Math.Max(1, pop), 1, 1.45);
            double chord = dw * 0.98;
            double radius = count <= 1 ? 0 : chord / (2 * Math.Sin(Math.PI / count));
            double reach = radius + item * grow / 2 + 28;
            double size = Math.Max(item * grow + 48, reach * 2);
            return (size, size);
        }

        private static Spot[] Ring(int count, int focus, double dw, double dh, double width, double height, double pop)
        {
            double item = Math.Max(dw, dh);
            double grow = _ringFit;
            double maxR = Math.Min(width, height) / 2 - item * grow / 2 - 20;
            if (maxR < 12)
                maxR = 12;
            double want = count <= 1 ? 0 : (dw * 0.98) / (2 * Math.Sin(Math.PI / count));
            double radius = Math.Min(maxR, want);
            double cx = width / 2;
            double cy = height / 2;
            var spots = new Spot[count];
            var origin = new Point(0.5, 0.5);
            for (int i = 0; i < count; i++)
            {
                double angle = -Math.PI / 2 + Math.PI * 2 * i / Math.Max(1, count);
                if (count == 1)
                    angle = -Math.PI / 2;
                double scale = i == focus ? Math.Clamp(pop, 0.82, 1.45) : 1;
                double x = cx + Math.Cos(angle) * radius;
                double y = cy + Math.Sin(angle) * radius;
                spots[i] = new Spot(x - dw / 2, y - dh / 2, scale, 0, i == focus ? 20 : 1, origin);
            }

            return spots;
        }

        private static (double W, double H) ThroneBox(int count, double dw, double dh)
        {
            int others = Math.Max(0, count - 1);
            double topW = others == 0 ? 0 : others * dw + Math.Max(0, others - 1) * 8;
            double heroW = dw * 1.7;
            double heroH = dh * 1.7;
            return (Math.Max(topW, heroW) + 24, 16 + (others > 0 ? dh + 14 : 0) + heroH + 12);
        }

        private static Spot[] Throne(int count, int focus, double dw, double dh, double k, double ox, double oy, double width)
        {
            int others = Math.Max(0, count - 1);
            double gap = 8 * k;
            double topW = others == 0 ? 0 : others * dw * k + Math.Max(0, others - 1) * gap;
            double heroS = 1.7 * k;
            double heroW = dw * heroS;
            double heroH = dh * heroS;
            var spots = new Spot[count];
            double contentW = ThroneBox(count, dw, dh).W * k;
            double x = ox + Math.Max(0, (contentW - topW) / 2);
            double y = oy + 8 * k;
            int seen = 0;
            var topOrigin = new Point(0.5, 0);
            for (int i = 0; i < count; i++)
            {
                if (i == focus)
                    continue;
                double left = x + seen * (dw * k + gap);
                double vw = dw * k;
                spots[i] = new Spot(left - (dw - vw) / 2, y, k, 0, 1, topOrigin);
                seen++;
            }

            double heroLeft = ox + Math.Max(0, (contentW - heroW) / 2);
            double heroTop = oy + 8 * k + (others > 0 ? dh * k + 14 * k : 0);
            spots[focus] = new Spot(
                heroLeft - (dw - heroW) / 2,
                heroTop - dh + heroH,
                heroS,
                0,
                30,
                new Point(0.5, 1));
            return spots;
        }

        private static void Pose(UIElement child, Rect slot, double scale, double angle, int z, Point origin)
        {
            child.Arrange(slot);
            Panel.SetZIndex(child, z);
            var group = new TransformGroup();
            group.Children.Add(new ScaleTransform(Math.Max(0.2, scale), Math.Max(0.2, scale)));
            if (Math.Abs(angle) > 0.1)
                group.Children.Add(new RotateTransform(angle));
            child.RenderTransform = group;
            child.RenderTransformOrigin = origin;
        }
    }

    private static Brush FloorBrush()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x14, 0x0C, 0x0A), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x55, 0x18, 0x10, 0x0C), 0.45));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x88, 0x12, 0x0C, 0x0A), 1));
        brush.Freeze();
        return brush;
    }

    private static Brush EdgeFade()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        brush.GradientStops.Add(new GradientStop(Colors.White, 0.06));
        brush.GradientStops.Add(new GradientStop(Colors.White, 0.94));
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        brush.Freeze();
        return brush;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClipboardFormat(string format);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr hMem);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref System.Runtime.InteropServices.ComTypes.STGMEDIUM medium);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string cls, string? title);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attrs, ref ShFileInfo info, uint size, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(IntPtr pidl, uint attrs, ref ShFileInfo info, uint size, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bind, out IntPtr pidl, uint attrs, out uint eaten);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr Icon;
        public int Index;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string Display;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string Type;
    }

    private sealed class ShelfTile : INotifyPropertyChanged
    {
        private ImageSource? _icon;

        public ShelfTile(ShelfEntry entry)
            : this(new ShelfSpot(entry.Key, entry.Name, entry))
        {
        }

        public ShelfTile(ShelfSpot spot)
        {
            Key = spot.Key;
            Name = spot.Name;
            IsHeader = spot.Entry == null;
            Launch = "";
            Clsid = "";
            Members = Array.Empty<string>();
            _span = double.NaN;
            _band = double.NaN;
            _barWidth = double.NaN;
            if (spot.Entry is not { } entry)
                return;
            Stamp = entry.Stamp;
            Ghost = entry.Ghost;
            Launch = entry.Launch;
            Clsid = entry.Clsid;
            Photo = entry.Photo;
            PlateInset = entry.Photo ? new Thickness(2) : new Thickness(16);
            StackPaths = spot.Stack ?? Array.Empty<string>();
            IsStack = StackPaths.Length >= 2;
            Badge = IsStack ? StackPaths.Length.ToString() : "";
            Members = IsStack ? StackPaths : new[] { spot.Key };
        }

        public double Span
        {
            get => _span;
            set
            {
                if (double.IsNaN(_span) && double.IsNaN(value))
                    return;
                if (!double.IsNaN(_span) && !double.IsNaN(value) && Math.Abs(_span - value) < 0.5)
                    return;
                _span = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Span)));
            }
        }

        public double Bar
        {
            get => _barWidth;
            set
            {
                if (double.IsNaN(_barWidth) && double.IsNaN(value))
                    return;
                if (!double.IsNaN(_barWidth) && !double.IsNaN(value) && Math.Abs(_barWidth - value) < 0.5)
                    return;
                _barWidth = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Bar)));
            }
        }

        public double Band
        {
            get => _band;
            set
            {
                if (double.IsNaN(_band) && double.IsNaN(value))
                    return;
                if (!double.IsNaN(_band) && !double.IsNaN(value) && Math.Abs(_band - value) < 0.5)
                    return;
                _band = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Band)));
            }
        }

        public Thickness Pull
        {
            get => _pull;
            set
            {
                if (_pull == value)
                    return;
                _pull = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Pull)));
            }
        }

        public void SetPad(double pad)
        {
            var next = Photo ? new Thickness(2) : new Thickness(pad);
            if (next == PlateInset)
                return;
            PlateInset = next;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlateInset)));
        }

        public string Key { get; }
        public string Name { get; }
        public bool IsHeader { get; }
        private double _span;
        private double _band = double.NaN;
        private double _barWidth = double.NaN;
        private Thickness _pull;
        public long Stamp { get; set; }
        public bool Ghost { get; }
        public string Launch { get; }
        public string Clsid { get; }
        public bool Photo { get; }
        public bool IsStack { get; }
        public string Badge { get; } = "";
        public string[] StackPaths { get; } = Array.Empty<string>();
        public string[] Members { get; } = Array.Empty<string>();
        public Thickness PlateInset { get; private set; }

        public ImageSource? Icon
        {
            get => _icon;
            set
            {
                _icon = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class FlagVis : System.Windows.Data.IValueConverter
    {
        public static readonly FlagVis Instance = new();

        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => value is true ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class HeaderVis : System.Windows.Data.IValueConverter
    {
        public static readonly HeaderVis Show = new(true);
        public static readonly HeaderVis Hide = new(false);
        private readonly bool _whenHeader;

        private HeaderVis(bool whenHeader) => _whenHeader = whenHeader;

        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => value is true == _whenHeader ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }
}
