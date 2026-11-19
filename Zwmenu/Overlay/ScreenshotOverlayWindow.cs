using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Zwmenu.Services;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingRect = System.Drawing.Rectangle;
using MediaPen = System.Windows.Media.Pen;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using WpfCursor = System.Windows.Input.Cursor;

namespace Zwmenu.Overlay;

/// <summary>
/// Выделение на одном мониторе. Снимок уже снят до показа окна,
/// поэтому в кадр не попадает рамка.
/// </summary>
public sealed class ScreenshotOverlayWindow : Window
{
    private enum Grip { None, Move, N, S, E, W, NE, NW, SE, SW }

    private readonly DrawingBitmap _bitmap;
    private readonly DrawingRect _monitor;
    private readonly string _device;
    private readonly SelectionLayer _layer;
    private readonly ShotMarkup _markup = new();
    private readonly MarkupLayer _ink;
    private readonly System.Windows.Controls.Canvas _chrome;
    private readonly System.Windows.Controls.Border _bar;
    private readonly System.Windows.Controls.Border _tab;
    private readonly System.Windows.Controls.TextBlock _tabGlyph;
    private readonly System.Windows.Controls.Border _textHost;
    private readonly System.Windows.Controls.TextBox _text;
    private readonly System.Windows.Shapes.Ellipse _nibHalo;
    private readonly System.Windows.Shapes.Ellipse _nib;
    private readonly System.Windows.Shapes.Ellipse _pickDot;
    private readonly System.Windows.Threading.DispatcherTimer _barHide;
    private System.Windows.Controls.Button _undoButton = null!;
    private System.Windows.Controls.Button _redoButton = null!;
    private readonly System.Windows.Shapes.Rectangle _readBox;
    private readonly System.Windows.Controls.Border _readCard;
    private readonly System.Windows.Controls.TextBlock _readBody;
    private readonly System.Windows.Controls.TextBlock _readCopyCaption;
    private readonly System.Windows.Controls.TextBlock _readSwitchCaption;
    private readonly System.Windows.Controls.Button _readSwitch;
    private readonly System.Windows.Controls.StackPanel _readActions;
    private readonly System.Windows.Controls.Border _historyPanel;
    private readonly System.Windows.Controls.StackPanel _historyRow;
    private bool _readDrag;
    private bool _readCardDrag;
    private Point _readStart;
    private Point _readCardGrab;
    private int _readGen;
    private CancellationTokenSource? _readCancel;
    private string _readOriginalText = "";
    private string _readRussianText = "";
    private bool _showingTranslation = true;
    private bool _readReady;
    private int _copyNote;
    private readonly Dictionary<ShotTool, System.Windows.Controls.Button> _toolButtons = new();
    private readonly List<(System.Windows.Controls.Button Button, Color Color)> _colorButtons = new();
    private readonly List<System.Windows.Controls.Button> _sizeButtons = new();
    private Rect _sel;
    private bool _selReady;
    private bool _busy;
    private bool _dragging;
    private bool _creating;
    private bool _drawing;
    private bool _barOpen;
    private bool _textMove;
    private bool _textResize;
    private bool _committingText;
    private Grip _grip;
    private ShotTool _tool = ShotTool.Select;
    private Point _dragStart;
    private Rect _dragRect;
    private Point _textOrigin;
    private Point _textDragStart;
    private Point _textHostOrigin;
    private Point _lastPoint;
    private bool _hasPoint;
    private double _textFont = 22;
    private double _textFontStart;
    private double _textWidthStart;
    private Color _color = Color.FromRgb(0xE2, 0x3D, 0x3D);
    private double _stroke = 5;

    public ScreenshotOverlayWindow(DrawingBitmap bitmap, DrawingRect monitor, string device)
    {
        _bitmap = bitmap;
        _monitor = monitor;
        _device = device ?? "";

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Black;
        Focusable = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Title = "Zwmenu снимок";

        var image = new System.Windows.Controls.Image
        {
            Source = ScreenCapture.ToFrozen(bitmap),
            Stretch = Stretch.Fill,
            IsHitTestVisible = false
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.LowQuality);

        _layer = new SelectionLayer { IsHitTestVisible = false };
        _ink = new MarkupLayer { IsHitTestVisible = false, Markup = _markup };
        _text = BuildTextBox();
        _textHost = BuildTextHost();
        _nibHalo = new System.Windows.Shapes.Ellipse
        {
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Stroke = Brushes.Black,
            StrokeThickness = 1.5,
            Fill = Brushes.Transparent
        };
        _nib = new System.Windows.Shapes.Ellipse
        {
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Stroke = Brushes.White,
            StrokeThickness = 1.25
        };
        _pickDot = new System.Windows.Shapes.Ellipse
        {
            Width = 12,
            Height = 12,
            Fill = new SolidColorBrush(_color)
        };
        _bar = BuildToolbar();
        _tabGlyph = new System.Windows.Controls.TextBlock
        {
            Text = "›",
            FontSize = 16,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF0, 0xE8)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        _tab = new System.Windows.Controls.Border
        {
            Width = 22,
            Height = 34,
            CornerRadius = new CornerRadius(0, 10, 10, 0),
            Background = new SolidColorBrush(Color.FromArgb(0xF4, 0x16, 0x16, 0x1A)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x28, 0xF5, 0xF0, 0xE8)),
            BorderThickness = new Thickness(0, 1, 1, 1),
            Child = _tabGlyph,
            Cursor = Cursors.Hand
        };
        _barHide = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        _barHide.Tick += (_, _) =>
        {
            _barHide.Stop();
            if (!DockContainsPointer())
                SetBarOpen(false, true);
        };
        Closed += (_, _) => _barHide.Stop();
        _bar.MouseEnter += (_, _) => ShowBar();
        _tab.MouseEnter += (_, _) => ShowBar();
        _bar.MouseLeave += (_, _) => ScheduleBarHide();
        _tab.MouseLeave += (_, _) => ScheduleBarHide();
        _readBox = BuildReadBox();
        _readBody = new System.Windows.Controls.TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF0, 0xE8)),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 26, 0)
        };
        _readCopyCaption = ReadCaption("Копировать");
        _readSwitchCaption = ReadCaption("Оригинал");
        _readSwitch = ReadButton(_readSwitchCaption, ToggleReadingText);
        _readSwitch.Background = Brushes.Transparent;
        _readSwitch.Visibility = Visibility.Collapsed;
        _readSwitchCaption.Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xF5, 0xF0, 0xE8));
        _readActions = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0)
        };
        _readActions.Children.Add(ReadButton(_readCopyCaption, CopyReading));
        _readActions.Children.Add(_readSwitch);
        _readActions.Visibility = Visibility.Collapsed;
        _readCard = BuildReadCard();
        _readCard.Cursor = Cursors.SizeAll;
        _readCard.MouseLeftButtonDown += OnReadCardDown;
        _readCard.MouseMove += OnReadCardMove;
        _readCard.MouseLeftButtonUp += OnReadCardUp;
        _readCard.LostMouseCapture += (_, _) => _readCardDrag = false;
        _historyRow = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal
        };
        _historyPanel = BuildHistoryPanel();
        _historyPanel.MouseEnter += (_, _) => _barHide.Stop();
        _historyPanel.MouseLeave += (_, _) => ScheduleBarHide();
        _chrome = new System.Windows.Controls.Canvas { Background = null };
        _chrome.Children.Add(_bar);
        _chrome.Children.Add(_tab);
        _chrome.Children.Add(_historyPanel);
        _chrome.Children.Add(_readBox);
        _chrome.Children.Add(_readCard);
        _chrome.Children.Add(_nibHalo);
        _chrome.Children.Add(_nib);
        _chrome.Children.Add(_textHost);
        Closed += (_, _) =>
        {
            _readGen++;
            _readCancel?.Cancel();
            try { _bitmap.Dispose(); }
            catch { /* снимок уже отпущен */ }
        };

        var root = new System.Windows.Controls.Grid();
        root.Children.Add(image);
        root.Children.Add(_ink);
        root.Children.Add(_layer);
        root.Children.Add(_chrome);
        Content = root;

        Loaded += (_, _) =>
        {
            EnsureSelection();
            SetBarOpen(false, false);
            Activate();
            Keyboard.Focus(this);
        };
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        MouseLeave += (_, _) =>
        {
            if (_drawing || IsMouseCaptured)
                return;
            HideNib();
            if (Cursor == Cursors.None)
                Cursor = Cursors.Arrow;
        };
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseWheel += OnWheel;
    }

    public bool IsEditingText => _textHost.Visibility == Visibility.Visible;

    public bool IsReading => _readCard.Visibility == Visibility.Visible;

    public event Action? ClipRequested;

    public void CancelTextEdit()
    {
        _textMove = false;
        _textResize = false;
        _text.Text = "";
        _textHost.Visibility = Visibility.Collapsed;
        Keyboard.Focus(this);
    }

    public void Undo()
    {
        if (_busy || _drawing || IsEditingText)
            return;
        if (_markup.Undo())
            _ink.InvalidateVisual();
        RefreshHistory();
    }

    public void Redo()
    {
        if (_busy || _drawing || IsEditingText)
            return;
        if (_markup.Redo())
            _ink.InvalidateVisual();
        RefreshHistory();
    }

    public void Copy()
    {
        if (_busy)
            return;
        CommitText();
        _busy = true;
        try
        {
            using var crop = Crop();
            if (crop == null)
            {
                _busy = false;
                return;
            }

            ScreenCapture.CopyToClipboard(crop);
            ShotHistory.RememberLater(crop);
            var settings = ScreenshotStore.Current;
            if (settings.RememberRegion)
                Remember();
            var screen = _monitor;
            bool notify = settings.ShowNotifications;
            Close();
            if (notify)
            {
                try { ScreenshotToast.Show("Скопировано в буфер обмена", screen); }
                catch { /* снимок уже в буфере */ }
            }
        }
        catch
        {
            _busy = false;
            ScreenshotToast.Show("Не удалось скопировать в буфер", _monitor, 2.4);
        }
    }

    public void Save()
    {
        if (_busy)
            return;
        CommitText();
        _busy = true;
        try
        {
            using var crop = Crop();
            if (crop == null)
            {
                _busy = false;
                return;
            }

            var settings = ScreenshotStore.Current;
            string path = ScreenCapture.SaveToDesktop(crop, settings);
            ShotHistory.RememberLater(crop);
            if (settings.RememberRegion)
                Remember();
            var screen = _monitor;
            bool notify = settings.ShowNotifications;
            string file = System.IO.Path.GetFileName(path);
            Close();
            if (notify)
            {
                try { ScreenshotToast.Show("Сохранено на рабочий стол: " + file, screen); }
                catch { /* файл уже записан */ }
            }
        }
        catch
        {
            _busy = false;
            ScreenshotToast.Show("Не удалось сохранить на рабочий стол", _monitor, 2.4);
        }
    }

    public void Cancel()
    {
        if (_busy)
            return;
        _busy = true;
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, new IntPtr(-1), _monitor.X, _monitor.Y, _monitor.Width, _monitor.Height, 0x0040);

        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget == null)
        {
            Left = _monitor.X;
            Top = _monitor.Y;
            Width = Math.Max(1, _monitor.Width);
            Height = Math.Max(1, _monitor.Height);
            return;
        }

        var m = src.CompositionTarget.TransformFromDevice;
        var tl = m.Transform(new Point(_monitor.X, _monitor.Y));
        var br = m.Transform(new Point(_monitor.Right, _monitor.Bottom));
        Left = tl.X;
        Top = tl.Y;
        Width = Math.Max(1, br.X - tl.X);
        Height = Math.Max(1, br.Y - tl.Y);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsEditingText)
            return;
        if (e.IsRepeat)
            return;

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Cancel();
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;

        if (e.Key == Key.C)
        {
            e.Handled = true;
            Copy();
        }
        else if (e.Key == Key.S)
        {
            e.Handled = true;
            Save();
        }
    }

    public void SelectAll()
    {
        if (_busy || ActualWidth < 2 || ActualHeight < 2)
            return;
        _dragging = false;
        _creating = false;
        try { ReleaseMouseCapture(); }
        catch { /* захват уже снят */ }
        _selReady = true;
        _sel = new Rect(0, 0, ActualWidth, ActualHeight);
        _layer.Selection = _sel;
        _layer.InvalidateVisual();
    }

    private void EnsureSelection()
    {
        if (_selReady || ActualWidth < 2 || ActualHeight < 2)
            return;

        var settings = ScreenshotStore.Current;
        if (settings.RememberRegion
            && settings.HasRegion
            && settings.RegionW >= 8
            && settings.RegionH >= 8
            && string.Equals(settings.MonitorDevice, _device, StringComparison.OrdinalIgnoreCase))
        {
            _selReady = true;
            double sx = ActualWidth / _bitmap.Width;
            double sy = ActualHeight / _bitmap.Height;
            _sel = Clamp(new Rect(
                settings.RegionX * sx,
                settings.RegionY * sy,
                settings.RegionW * sx,
                settings.RegionH * sy));
        }
        else
        {
            _sel = Rect.Empty;
            _layer.Selection = Rect.Empty;
        }

        if (_selReady)
            _layer.Selection = _sel;
        _layer.InvalidateVisual();
    }

    private void Remember()
    {
        double sx = _bitmap.Width / Math.Max(1, ActualWidth);
        double sy = _bitmap.Height / Math.Max(1, ActualHeight);
        int x = (int)Math.Round(_sel.X * sx);
        int y = (int)Math.Round(_sel.Y * sy);
        int w = Math.Max(1, (int)Math.Round(_sel.Width * sx));
        int h = Math.Max(1, (int)Math.Round(_sel.Height * sy));
        if (x < 0)
        {
            w += x;
            x = 0;
        }
        if (y < 0)
        {
            h += y;
            y = 0;
        }
        if (x + w > _bitmap.Width)
            w = _bitmap.Width - x;
        if (y + h > _bitmap.Height)
            h = _bitmap.Height - y;
        if (w < 8 || h < 8)
            return;

        ScreenshotStore.SaveRegion(_device, x, y, w, h);
    }

    private DrawingBitmap? Crop()
    {
        if (!_selReady || _sel.Width < 8 || _sel.Height < 8)
            return null;
        if (_bitmap.Width < 1 || _bitmap.Height < 1 || ActualWidth < 1 || ActualHeight < 1)
            return null;

        double sx = _bitmap.Width / ActualWidth;
        double sy = _bitmap.Height / ActualHeight;
        var rect = new DrawingRect(
            (int)Math.Round(_sel.X * sx),
            (int)Math.Round(_sel.Y * sy),
            Math.Max(1, (int)Math.Round(_sel.Width * sx)),
            Math.Max(1, (int)Math.Round(_sel.Height * sy)));
        rect.Intersect(new DrawingRect(0, 0, _bitmap.Width, _bitmap.Height));
        if (rect.Width < 1 || rect.Height < 1)
            return null;

        var clone = _bitmap.Clone(rect, PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(clone))
            _markup.Paint(graphics, rect, ActualWidth, ActualHeight, _bitmap.Width, _bitmap.Height);
        return clone;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        EnsureSelection();
        if (IsChrome(e.OriginalSource as DependencyObject))
            return;

        var p = e.GetPosition(this);
        SetBarOpen(false, true);

        if (_tool == ShotTool.Text)
        {
            CommitText();
            OpenText(p);
            e.Handled = true;
            return;
        }

        if (_tool == ShotTool.Pick)
        {
            PickColor(p);
            e.Handled = true;
            return;
        }

        if (_tool == ShotTool.Read)
        {
            _readDrag = true;
            _readStart = p;
            ShowReadBox(new Rect(p.X, p.Y, 1, 1));
            CaptureMouse();
            Cursor = Cursors.None;
            UpdateNib(p);
            e.Handled = true;
            return;
        }

        if (_tool != ShotTool.Select)
        {
            _markup.Color = _color;
            _markup.Thickness = _stroke;
            _markup.Begin(_tool, p);
            _drawing = true;
            CaptureMouse();
            Cursor = Cursors.None;
            UpdateNib(p);
            _ink.InvalidateVisual();
            e.Handled = true;
            return;
        }

        var grip = Hit(p);
        if (grip == Grip.None)
        {
            _creating = true;
            _dragging = true;
            _selReady = true;
            _grip = Grip.None;
            _dragStart = p;
            _sel = new Rect(p.X, p.Y, 0, 0);
            _layer.Selection = _sel;
            _layer.InvalidateVisual();
            CaptureMouse();
            Cursor = Cursors.Cross;
            e.Handled = true;
            return;
        }

        _creating = false;
        _dragging = true;
        _grip = grip;
        _dragStart = p;
        _dragRect = _sel;
        CaptureMouse();
        Cursor = CursorFor(grip);
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        bool overChrome = IsChrome(e.OriginalSource as DependencyObject);
        if (_readDrag)
        {
            ShowReadBox(NormRect(_readStart, p));
            Cursor = Cursors.None;
            UpdateNib(p);
            return;
        }

        if (_drawing)
        {
            _markup.Continue(p);
            _ink.InvalidateVisual();
            if (overChrome)
            {
                Cursor = Cursors.Arrow;
                HideNib();
            }
            else
            {
                Cursor = Cursors.None;
                UpdateNib(p);
            }
            return;
        }

        if (!_dragging)
        {
            if (overChrome)
            {
                Cursor = Cursors.Arrow;
                HideNib();
            }
            else
            {
                ApplyToolCursor(p);
                UpdateNib(p);
            }
            return;
        }

        _sel = ApplyDrag(p);
        _layer.Selection = _sel;
        _layer.InvalidateVisual();
        if (overChrome)
            Cursor = Cursors.Arrow;
        else
            Cursor = CursorFor(_grip);
        HideNib();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_readDrag)
        {
            var p = e.GetPosition(this);
            _readDrag = false;
            ReleaseMouseCapture();
            var rect = NormRect(_readStart, p);
            HideReadBox();
            ApplyToolCursor(p);
            UpdateNib(p);
            if (rect.Width >= 8 && rect.Height >= 8)
                _ = RecognizeAsync(rect);
            return;
        }

        if (_drawing)
        {
            var p = e.GetPosition(this);
            _markup.Continue(p);
            _markup.End();
            _drawing = false;
            ReleaseMouseCapture();
            _ink.InvalidateVisual();
            RefreshHistory();
            bool over = IsChrome(e.OriginalSource as DependencyObject);
            if (over)
            {
                Cursor = Cursors.Arrow;
                HideNib();
            }
            else
            {
                ApplyToolCursor(p);
                UpdateNib(p);
            }
            return;
        }

        if (!_dragging)
            return;

        _dragging = false;
        ReleaseMouseCapture();
        if (_creating)
        {
            _creating = false;
            if (_sel.Width < 8 || _sel.Height < 8)
            {
                _selReady = false;
                _sel = Rect.Empty;
                _layer.Selection = Rect.Empty;
                _layer.InvalidateVisual();
            }
        }

        var up = e.GetPosition(this);
        Cursor = _selReady ? CursorFor(Hit(up)) : Cursors.Cross;
    }

    private Rect ApplyDrag(Point p)
    {
        if (_creating)
        {
            var born = NormRect(_dragStart, p);
            double x = Math.Clamp(born.X, 0, ActualWidth);
            double y = Math.Clamp(born.Y, 0, ActualHeight);
            double right = Math.Clamp(born.Right, 0, ActualWidth);
            double bottom = Math.Clamp(born.Bottom, 0, ActualHeight);
            return new Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
        }

        double dx = p.X - _dragStart.X;
        double dy = p.Y - _dragStart.Y;
        Rect r = _dragRect;
        double W = ActualWidth;
        double H = ActualHeight;
        double minW = Math.Min(24, Math.Max(1, W));
        double minH = Math.Min(24, Math.Max(1, H));

        switch (_grip)
        {
            case Grip.Move:
            {
                double x = Math.Clamp(r.X + dx, 0, Math.Max(0, W - r.Width));
                double y = Math.Clamp(r.Y + dy, 0, Math.Max(0, H - r.Height));
                return new Rect(x, y, r.Width, r.Height);
            }
            case Grip.E:
                return new Rect(r.X, r.Y, Math.Clamp(r.Width + dx, minW, Math.Max(minW, W - r.X)), r.Height);
            case Grip.S:
                return new Rect(r.X, r.Y, r.Width, Math.Clamp(r.Height + dy, minH, Math.Max(minH, H - r.Y)));
            case Grip.W:
            {
                double left = Math.Clamp(r.X + dx, 0, r.Right - minW);
                return new Rect(left, r.Y, r.Right - left, r.Height);
            }
            case Grip.N:
            {
                double top = Math.Clamp(r.Y + dy, 0, r.Bottom - minH);
                return new Rect(r.X, top, r.Width, r.Bottom - top);
            }
            case Grip.NE:
            {
                double top = Math.Clamp(r.Y + dy, 0, r.Bottom - minH);
                double width = Math.Clamp(r.Width + dx, minW, Math.Max(minW, W - r.X));
                return new Rect(r.X, top, width, r.Bottom - top);
            }
            case Grip.NW:
            {
                double left = Math.Clamp(r.X + dx, 0, r.Right - minW);
                double top = Math.Clamp(r.Y + dy, 0, r.Bottom - minH);
                return new Rect(left, top, r.Right - left, r.Bottom - top);
            }
            case Grip.SE:
            {
                double width = Math.Clamp(r.Width + dx, minW, Math.Max(minW, W - r.X));
                double height = Math.Clamp(r.Height + dy, minH, Math.Max(minH, H - r.Y));
                return new Rect(r.X, r.Y, width, height);
            }
            case Grip.SW:
            {
                double left = Math.Clamp(r.X + dx, 0, r.Right - minW);
                double height = Math.Clamp(r.Height + dy, minH, Math.Max(minH, H - r.Y));
                return new Rect(left, r.Y, r.Right - left, height);
            }
            default:
                return r;
        }
    }

    private Grip Hit(Point p)
    {
        if (!_selReady)
            return Grip.None;

        const double edge = 12;
        Rect sel = _sel;
        bool n = p.Y <= sel.Top + edge && p.Y >= sel.Top - 4 && p.X >= sel.Left - 4 && p.X <= sel.Right + 4;
        bool s = p.Y >= sel.Bottom - edge && p.Y <= sel.Bottom + 4 && p.X >= sel.Left - 4 && p.X <= sel.Right + 4;
        bool w = p.X <= sel.Left + edge && p.X >= sel.Left - 4 && p.Y >= sel.Top - 4 && p.Y <= sel.Bottom + 4;
        bool e = p.X >= sel.Right - edge && p.X <= sel.Right + 4 && p.Y >= sel.Top - 4 && p.Y <= sel.Bottom + 4;

        // У полного экрана кромка совпадает с краем монитора: ловим её изнутри.
        if (sel.Top <= 1 && p.Y <= edge && p.X >= -4 && p.X <= ActualWidth + 4)
            n = true;
        if (sel.Bottom >= ActualHeight - 1 && p.Y >= ActualHeight - edge && p.X >= -4 && p.X <= ActualWidth + 4)
            s = true;
        if (sel.Left <= 1 && p.X <= edge && p.Y >= -4 && p.Y <= ActualHeight + 4)
            w = true;
        if (sel.Right >= ActualWidth - 1 && p.X >= ActualWidth - edge && p.Y >= -4 && p.Y <= ActualHeight + 4)
            e = true;

        if (n && w) return Grip.NW;
        if (n && e) return Grip.NE;
        if (s && w) return Grip.SW;
        if (s && e) return Grip.SE;
        if (n) return Grip.N;
        if (s) return Grip.S;
        if (w) return Grip.W;
        if (e) return Grip.E;
        if (sel.Contains(p)) return Grip.Move;
        return Grip.None;
    }

    private static WpfCursor CursorFor(Grip grip) => grip switch
    {
        Grip.Move => Cursors.SizeAll,
        Grip.N or Grip.S => Cursors.SizeNS,
        Grip.E or Grip.W => Cursors.SizeWE,
        Grip.NE or Grip.SW => Cursors.SizeNESW,
        Grip.NW or Grip.SE => Cursors.SizeNWSE,
        _ => Cursors.Arrow
    };

    private Rect Clamp(Rect r)
    {
        double W = ActualWidth;
        double H = ActualHeight;
        double minW = Math.Min(24, Math.Max(1, W));
        double minH = Math.Min(24, Math.Max(1, H));
        double w = Math.Clamp(r.Width, minW, W);
        double h = Math.Clamp(r.Height, minH, H);
        double x = Math.Clamp(r.X, 0, Math.Max(0, W - w));
        double y = Math.Clamp(r.Y, 0, Math.Max(0, H - h));
        return new Rect(x, y, w, h);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private System.Windows.Controls.TextBox BuildTextBox()
    {
        var box = new System.Windows.Controls.TextBox
        {
            MinWidth = 120,
            Padding = new Thickness(8, 4, 14, 8),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = _textFont,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(_color),
            CaretBrush = new SolidColorBrush(_color),
            BorderThickness = new Thickness(0),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = System.Windows.VerticalAlignment.Top
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                return;
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitText();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelTextEdit();
            }
        };
        return box;
    }

    private System.Windows.Controls.Border BuildTextHost()
    {
        var move = new System.Windows.Controls.Border
        {
            Height = 16,
            Margin = new Thickness(6, 4, 6, 0),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0xF5, 0xF0, 0xE8)),
            Cursor = Cursors.SizeAll,
            ToolTip = "Перетащить текст",
            Child = new System.Windows.Controls.TextBlock
            {
                Text = "···",
                FontSize = 12,
                Margin = new Thickness(0, -3, 0, 0),
                Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xF5, 0xF0, 0xE8)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            }
        };
        move.MouseLeftButtonDown += OnTextMoveDown;
        move.MouseMove += OnTextMoveMove;
        move.MouseLeftButtonUp += OnTextMoveUp;

        var resize = new System.Windows.Controls.Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF0, 0xE8)),
            Cursor = Cursors.SizeNWSE,
            ToolTip = "Размер текста",
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 4, 4)
        };
        resize.MouseLeftButtonDown += OnTextResizeDown;
        resize.MouseMove += OnTextResizeMove;
        resize.MouseLeftButtonUp += OnTextResizeUp;

        var grid = new System.Windows.Controls.Grid();
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        System.Windows.Controls.Grid.SetRow(move, 0);
        System.Windows.Controls.Grid.SetRow(_text, 1);
        System.Windows.Controls.Grid.SetRow(resize, 1);
        grid.Children.Add(move);
        grid.Children.Add(_text);
        grid.Children.Add(resize);

        return new System.Windows.Controls.Border
        {
            Visibility = Visibility.Collapsed,
            Width = 220,
            MinWidth = 96,
            MinHeight = 52,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x14, 0x14, 0x16)),
            BorderBrush = new SolidColorBrush(_color),
            BorderThickness = new Thickness(1.5),
            Child = grid
        };
    }

    private System.Windows.Controls.Border BuildToolbar()
    {
        var row = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal
        };
        (ShotTool Tool, string Glyph)[] items =
        {
            (ShotTool.Select, "▭"),
            (ShotTool.Pen, "✎"),
            (ShotTool.Marker, "▬"),
            (ShotTool.Line, "╱"),
            (ShotTool.Arrow, "➤"),
            (ShotTool.ArrowOpen, "↗"),
            (ShotTool.ArrowBoth, "⇔"),
            (ShotTool.Rect, "□"),
            (ShotTool.Ellipse, "○"),
            (ShotTool.Text, "А"),
            (ShotTool.Eraser, "⌫"),
            (ShotTool.Read, "Я")
        };
        foreach (var item in items)
            row.Children.Add(BuildToolButton(item.Tool, item.Glyph));
        row.Children.Add(BuildPickButton());
        row.Children.Add(ChromeButton("◷", ToggleHistory));
        row.Children.Add(BuildRecordButton());
        row.Children.Add(ToolGap());

        var colors = row;
        (byte R, byte G, byte B, string Name)[] swatches =
        {
            (0xE2, 0x3D, 0x3D, "Красный"),
            (0xF0, 0x8A, 0x24, "Оранжевый"),
            (0xF2, 0xC1, 0x4E, "Жёлтый"),
            (0x3E, 0xAE, 0x5A, "Зелёный"),
            (0x3E, 0xC6, 0xE0, "Голубой"),
            (0x3A, 0x7B, 0xD5, "Синий"),
            (0x8E, 0x5B, 0xD0, "Фиолетовый"),
            (0xFF, 0xFF, 0xFF, "Белый"),
            (0x16, 0x16, 0x1A, "Чёрный")
        };
        foreach (var swatch in swatches)
            colors.Children.Add(BuildColorButton(Color.FromRgb(swatch.R, swatch.G, swatch.B)));
        colors.Children.Add(ToolGap());
        colors.Children.Add(BuildSizeButton(2));
        colors.Children.Add(BuildSizeButton(5));
        colors.Children.Add(BuildSizeButton(10));
        colors.Children.Add(ToolGap());
        _undoButton = ChromeButton("↩", () => Undo());
        _redoButton = ChromeButton("↪", () => Redo());
        _undoButton.Background = Brushes.Transparent;
        _redoButton.Background = Brushes.Transparent;
        colors.Children.Add(_undoButton);
        colors.Children.Add(_redoButton);
        RefreshHistory();

        var bar = new System.Windows.Controls.Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF4, 0x16, 0x16, 0x1A)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x28, 0xF5, 0xF0, 0xE8)),
            BorderThickness = new Thickness(0, 1, 1, 1),
            CornerRadius = new CornerRadius(0, 12, 12, 0),
            Padding = new Thickness(4, 2, 4, 2),
            IsHitTestVisible = false,
            Child = row
        };
        SelectTool(ShotTool.Select);
        SelectColor(_color);
        SelectStroke(5);
        return bar;
    }

    private System.Windows.Controls.Button BuildToolButton(ShotTool tool, string glyph)
    {
        var button = ChromeButton(glyph, () => SelectTool(tool));
        _toolButtons[tool] = button;
        return button;
    }

    private System.Windows.Controls.Button BuildColorButton(Color color)
    {
        var swatch = new System.Windows.Controls.Border
        {
            Width = 12,
            Height = 12,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(color),
            BorderBrush = color.R + color.G + color.B > 720
                ? new SolidColorBrush(Color.FromArgb(0x88, 0x14, 0x14, 0x16))
                : null,
            BorderThickness = new Thickness(color.R + color.G + color.B > 720 ? 1 : 0)
        };
        var button = new System.Windows.Controls.Button
        {
            Width = 18,
            Height = 24,
            Margin = new Thickness(1, 0, 1, 0),
            Padding = new Thickness(0),
            Style = FlatStyle(),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Content = swatch,
            Cursor = Cursors.Hand
        };
        button.Click += (_, _) => SelectColor(color);
        _colorButtons.Add((button, color));
        return button;
    }

    private System.Windows.Controls.Button BuildSizeButton(double width)
    {
        var dot = new System.Windows.Controls.Border
        {
            Width = Math.Clamp(width * 0.7 + 2, 3, 10),
            Height = Math.Clamp(width * 0.7 + 2, 3, 10),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.White
        };
        var button = new System.Windows.Controls.Button
        {
            Width = 18,
            Height = 24,
            Margin = new Thickness(1, 0, 1, 0),
            Padding = new Thickness(0),
            Tag = width,
            Style = FlatStyle(),
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E)),
            BorderThickness = new Thickness(0),
            Content = dot,
            Cursor = Cursors.Hand
        };
        button.Click += (_, _) => SelectStroke(width);
        _sizeButtons.Add(button);
        return button;
    }

    private System.Windows.Controls.Button ChromeButton(string glyph, Action click)
    {
        var button = new System.Windows.Controls.Button
        {
            Width = 24,
            Height = 24,
            Margin = new Thickness(0),
            Padding = new Thickness(0),
            Style = FlatStyle(),
            Content = new System.Windows.Controls.TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF0, 0xE8)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            },
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF0, 0xE8)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xF5, 0xF0, 0xE8)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand
        };
        button.Click += (_, _) => click();
        return button;
    }

    private System.Windows.Controls.Button BuildRecordButton()
    {
        var button = new System.Windows.Controls.Button
        {
            Height = 24,
            Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(8, 0, 8, 0),
            Style = FlatStyle(),
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E)),
            Cursor = Cursors.Hand,
            Content = new System.Windows.Controls.TextBlock
            {
                Text = "Запись",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF0, 0xE8)),
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            }
        };
        button.Click += (_, _) =>
        {
            ClipRequested?.Invoke();
            Close();
        };
        return button;
    }

    private System.Windows.Controls.Button BuildPickButton()
    {
        var button = new System.Windows.Controls.Button
        {
            Width = 24,
            Height = 24,
            Margin = new Thickness(0),
            Padding = new Thickness(0),
            Style = FlatStyle(),
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E)),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Content = _pickDot
        };
        button.Click += (_, _) => SelectTool(ShotTool.Pick);
        _toolButtons[ShotTool.Pick] = button;
        return button;
    }

    private static Style FlatStyle()
    {
        var border = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
        border.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(System.Windows.Controls.Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
        var presenter = new FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter));
        presenter.SetValue(System.Windows.Controls.ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        presenter.SetValue(System.Windows.Controls.ContentPresenter.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
        border.AppendChild(presenter);
        var template = new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.Button)) { VisualTree = border };
        return new Style(typeof(System.Windows.Controls.Button))
        {
            Setters = { new Setter(System.Windows.Controls.Control.TemplateProperty, template) }
        };
    }

    private static System.Windows.Controls.Border ToolGap() => new()
    {
        Width = 1,
        Height = 14,
        Margin = new Thickness(4, 5, 4, 0),
        Background = new SolidColorBrush(Color.FromArgb(0x28, 0xF5, 0xF0, 0xE8))
    };

    private void ShowBar()
    {
        _barHide.Stop();
        SetBarOpen(true, true);
    }

    private void ScheduleBarHide()
    {
        _barHide.Stop();
        _barHide.Start();
    }

    private bool DockContainsPointer()
    {
        if (_tab.IsMouseOver || _bar.IsMouseOver || _historyPanel.IsMouseOver)
            return true;
        if (!_barOpen)
            return false;

        var p = Mouse.GetPosition(this);
        double left = _bar.GetValue(System.Windows.Controls.Canvas.LeftProperty) is double value ? value : 0;
        double width = _bar.ActualWidth > 1 ? _bar.ActualWidth : _bar.DesiredSize.Width;
        double height = Math.Max(32, _bar.ActualHeight);
        return new Rect(left, 8, Math.Max(1, width), height).Contains(p);
    }

    private void SetBarOpen(bool open, bool animate)
    {
        if (open == _barOpen && animate)
            return;

        if (!open)
            HideHistory();
        _barOpen = open;
        _bar.Measure(new System.Windows.Size(double.PositiveInfinity, 48));
        double width = _bar.DesiredSize.Width > 1 ? _bar.DesiredSize.Width : 480;
        const double top = 8;
        const double tabWidth = 22;
        double target = open ? tabWidth - 1 : tabWidth - width;
        _bar.IsHitTestVisible = open;
        _tabGlyph.Text = open ? "‹" : "›";
        System.Windows.Controls.Canvas.SetLeft(_tab, 0);
        System.Windows.Controls.Canvas.SetTop(_tab, top);
        System.Windows.Controls.Canvas.SetTop(_bar, top);
        var left = System.Windows.Controls.Canvas.LeftProperty;
        _bar.BeginAnimation(left, null);
        System.Windows.Controls.Canvas.SetLeft(_bar, target);
    }

    private void RefreshHistory()
    {
        _undoButton.Opacity = _markup.CanUndo ? 1 : 0.35;
        _redoButton.Opacity = _markup.CanRedo ? 1 : 0.35;
    }

    private void SelectTool(ShotTool tool)
    {
        if (tool != ShotTool.Text)
            CommitText();
        _tool = tool;
        foreach (var pair in _toolButtons)
        {
            bool on = pair.Key == tool;
            pair.Value.Background = new SolidColorBrush(on
                ? Color.FromRgb(0x3E, 0x5A, 0x6E)
                : Colors.Transparent);
        }
    }

    private void SelectColor(Color color)
    {
        _color = color;
        _markup.Color = color;
        var live = new SolidColorBrush(color);
        _text.Foreground = live;
        _text.CaretBrush = live;
        _textHost.BorderBrush = live;
        _pickDot.Fill = live;
        foreach (var (button, swatch) in _colorButtons)
        {
            bool on = swatch == color;
            button.Background = new SolidColorBrush(on ? Colors.White : Colors.Transparent);
        }
    }

    private void SelectStroke(double width)
    {
        _stroke = width;
        _markup.Thickness = width;
        foreach (var button in _sizeButtons)
        {
            bool on = button.Tag is double value && Math.Abs(value - width) < 0.1;
            button.Background = new SolidColorBrush(on
                ? Color.FromRgb(0x3E, 0x5A, 0x6E)
                : Colors.Transparent);
        }
    }

    private void OpenText(Point point)
    {
        _text.Text = "";
        _text.FontSize = _textFont;
        _textHost.Width = 220;
        _textHost.Height = double.NaN;
        _textHost.MinHeight = _textFont + 36;
        _textOrigin = point;
        _textHost.Visibility = Visibility.Visible;
        double left = Math.Clamp(point.X, 8, Math.Max(8, ActualWidth - 240));
        double top = Math.Clamp(point.Y, 8, Math.Max(8, ActualHeight - 80));
        System.Windows.Controls.Canvas.SetLeft(_textHost, left);
        System.Windows.Controls.Canvas.SetTop(_textHost, top);
        _text.Focus();
        Keyboard.Focus(_text);
    }

    private void CommitText()
    {
        if (_committingText || !IsEditingText)
            return;

        _committingText = true;
        string value = _text.Text;
        double size = _text.FontSize;
        Point origin;
        double maxWidth;
        try
        {
            origin = _text.TranslatePoint(new Point(_text.Padding.Left, _text.Padding.Top), this);
            maxWidth = Math.Max(24, _text.ActualWidth - _text.Padding.Left - _text.Padding.Right);
        }
        catch
        {
            origin = _textOrigin;
            maxWidth = 180;
        }

        _textHost.Visibility = Visibility.Collapsed;
        _markup.Color = _color;
        _markup.AddText(origin, value, size, maxWidth);
        _ink.InvalidateVisual();
        RefreshHistory();
        Keyboard.Focus(this);
        _committingText = false;
    }

    private void OnTextMoveDown(object sender, MouseButtonEventArgs e)
    {
        _textMove = true;
        _textDragStart = e.GetPosition(this);
        double x = System.Windows.Controls.Canvas.GetLeft(_textHost);
        double y = System.Windows.Controls.Canvas.GetTop(_textHost);
        _textHostOrigin = new Point(double.IsNaN(x) ? 0 : x, double.IsNaN(y) ? 0 : y);
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void OnTextMoveMove(object sender, MouseEventArgs e)
    {
        if (!_textMove)
            return;
        var p = e.GetPosition(this);
        double left = _textHostOrigin.X + (p.X - _textDragStart.X);
        double top = _textHostOrigin.Y + (p.Y - _textDragStart.Y);
        double maxX = Math.Max(0, ActualWidth - 48);
        double maxY = Math.Max(0, ActualHeight - 36);
        System.Windows.Controls.Canvas.SetLeft(_textHost, Math.Clamp(left, 0, maxX));
        System.Windows.Controls.Canvas.SetTop(_textHost, Math.Clamp(top, 0, maxY));
    }

    private void OnTextMoveUp(object sender, MouseButtonEventArgs e)
    {
        if (!_textMove)
            return;
        _textMove = false;
        ((UIElement)sender).ReleaseMouseCapture();
        e.Handled = true;
        Keyboard.Focus(_text);
    }

    private void OnTextResizeDown(object sender, MouseButtonEventArgs e)
    {
        _textResize = true;
        _textDragStart = e.GetPosition(this);
        _textFontStart = _textFont;
        _textWidthStart = _textHost.ActualWidth > 1 ? _textHost.ActualWidth : _textHost.Width;
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void OnTextResizeMove(object sender, MouseEventArgs e)
    {
        if (!_textResize)
            return;
        var p = e.GetPosition(this);
        double dx = p.X - _textDragStart.X;
        double dy = p.Y - _textDragStart.Y;
        _textHost.Width = Math.Clamp(_textWidthStart + dx, 96, Math.Max(96, ActualWidth - 16));
        SetTextFont(_textFontStart + dy * 0.45);
    }

    private void OnTextResizeUp(object sender, MouseButtonEventArgs e)
    {
        if (!_textResize)
            return;
        _textResize = false;
        ((UIElement)sender).ReleaseMouseCapture();
        e.Handled = true;
        Keyboard.Focus(_text);
    }

    private void SetTextFont(double size)
    {
        _textFont = Math.Clamp(size, 10, 180);
        _text.FontSize = _textFont;
        _textHost.Height = double.NaN;
        _textHost.MinHeight = _textFont + 36;
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;

        e.Handled = true;
        int sign = e.Delta > 0 ? 1 : -1;
        if (IsEditingText)
        {
            SetTextFont(_textFont + sign * 2);
            return;
        }

        SelectStroke(Math.Clamp(_stroke + sign * 2, 1, 40));
        if (_hasPoint && Cursor == Cursors.None)
            UpdateNib(_lastPoint);
    }

    private void PickColor(Point point)
    {
        if (_bitmap.Width < 1 || _bitmap.Height < 1 || ActualWidth < 1 || ActualHeight < 1)
            return;

        int x = (int)Math.Round(point.X * _bitmap.Width / ActualWidth);
        int y = (int)Math.Round(point.Y * _bitmap.Height / ActualHeight);
        x = Math.Clamp(x, 0, _bitmap.Width - 1);
        y = Math.Clamp(y, 0, _bitmap.Height - 1);
        var pixel = _bitmap.GetPixel(x, y);
        SelectColor(Color.FromRgb(pixel.R, pixel.G, pixel.B));
    }

    private void ApplyToolCursor(Point point)
    {
        if (_tool == ShotTool.Select)
        {
            var grip = Hit(point);
            Cursor = grip == Grip.None ? Cursors.Cross : CursorFor(grip);
        }
        else if (_tool == ShotTool.Text)
            Cursor = Cursors.IBeam;
        else
            Cursor = Cursors.None;
    }

    private void HideNib()
    {
        _nib.Visibility = Visibility.Collapsed;
        _nibHalo.Visibility = Visibility.Collapsed;
    }

    private void UpdateNib(Point point)
    {
        _lastPoint = point;
        _hasPoint = true;
        if (IsEditingText || _tool is ShotTool.Select or ShotTool.Text)
        {
            HideNib();
            return;
        }

        bool hollow = _tool is ShotTool.Eraser or ShotTool.Pick or ShotTool.Read;
        double size = _tool == ShotTool.Eraser
            ? _markup.EraserRadius * 2
            : _tool is ShotTool.Pick or ShotTool.Read
                ? 14
                : Math.Max(9, _tool == ShotTool.Marker ? _stroke * 3.1 : _stroke + 4);
        double halo = size + 3;
        _nib.Width = size;
        _nib.Height = size;
        _nibHalo.Width = halo;
        _nibHalo.Height = halo;
        _nib.Fill = hollow
            ? Brushes.Transparent
            : new SolidColorBrush(Color.FromArgb(0xC8, _color.R, _color.G, _color.B));
        _nib.Visibility = Visibility.Visible;
        _nibHalo.Visibility = Visibility.Visible;
        System.Windows.Controls.Canvas.SetLeft(_nib, point.X - size / 2);
        System.Windows.Controls.Canvas.SetTop(_nib, point.Y - size / 2);
        System.Windows.Controls.Canvas.SetLeft(_nibHalo, point.X - halo / 2);
        System.Windows.Controls.Canvas.SetTop(_nibHalo, point.Y - halo / 2);
    }

    private bool IsChrome(DependencyObject? source)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, _bar) || ReferenceEquals(source, _tab)
                || ReferenceEquals(source, _textHost) || ReferenceEquals(source, _text)
                || ReferenceEquals(source, _readCard) || ReferenceEquals(source, _historyPanel))
                return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    public void CancelReading()
    {
        _readGen++;
        _readCancel?.Cancel();
        _readDrag = false;
        HideReadBox();
        _readCard.Visibility = Visibility.Collapsed;
        _readOriginalText = "";
        _readRussianText = "";
        _readReady = false;
    }

    public void CopyReading()
    {
        string text = ShownText();
        if (text.Length == 0)
            return;
        try
        {
            System.Windows.Clipboard.SetText(text);
            NoteCopied(true);
        }
        catch
        {
            NoteCopied(false);
        }
    }

    private static System.Windows.Shapes.Rectangle BuildReadBox() => new()
    {
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
        Stroke = Brushes.White,
        StrokeThickness = 1.5,
        Fill = new SolidColorBrush(Color.FromArgb(0x28, 0xF5, 0xF0, 0xE8))
    };

    private static System.Windows.Controls.TextBlock ReadCaption(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF0, 0xE8)),
        FontSize = 13,
        VerticalAlignment = System.Windows.VerticalAlignment.Center
    };

    private System.Windows.Controls.Border BuildReadCard()
    {
        var close = ReadButton(ReadCaption("×"), CancelReading);
        close.Width = 22;
        close.Height = 22;
        close.Margin = new Thickness(0);
        close.Padding = new Thickness(0);
        close.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        close.VerticalAlignment = System.Windows.VerticalAlignment.Top;
        close.Background = Brushes.Transparent;

        var text = new System.Windows.Controls.StackPanel();
        text.Children.Add(_readBody);
        text.Children.Add(_readActions);

        var root = new System.Windows.Controls.Grid();
        root.Children.Add(new System.Windows.Controls.ScrollViewer
        {
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            Content = text
        });
        root.Children.Add(close);

        return new System.Windows.Controls.Border
        {
            Visibility = Visibility.Collapsed,
            Width = 360,
            MaxHeight = 280,
            Padding = new Thickness(12, 10, 8, 10),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0xF4, 0x16, 0x16, 0x1A)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xF5, 0xF0, 0xE8)),
            BorderThickness = new Thickness(1),
            Child = root
        };
    }

    private System.Windows.Controls.Button ReadButton(System.Windows.Controls.TextBlock caption, Action click)
    {
        var button = new System.Windows.Controls.Button
        {
            Height = 28,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(10, 0, 10, 0),
            Style = FlatStyle(),
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E)),
            Cursor = Cursors.Hand,
            Content = caption
        };
        button.Click += (_, _) => click();
        return button;
    }

    private string ShownText()
    {
        if (!_readReady)
            return "";
        if (_showingTranslation && _readRussianText.Length > 0)
            return _readRussianText;
        return _readOriginalText;
    }

    private void PresentReading(string message, bool ready)
    {
        _readReady = ready;
        _copyNote++;
        _readCopyCaption.Text = "Копировать";
        _readBody.Text = message;
        bool canSwitch = ready
            && _readOriginalText.Length > 0
            && _readRussianText.Length > 0
            && !string.Equals(_readOriginalText.Trim(), _readRussianText.Trim(), StringComparison.Ordinal);
        _readSwitch.Visibility = canSwitch ? Visibility.Visible : Visibility.Collapsed;
        _readSwitchCaption.Text = _showingTranslation ? "Оригинал" : "Перевод";
        _readActions.Visibility = ready && ShownText().Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        KeepReadCardInside();
    }

    private void ToggleReadingText()
    {
        if (_readSwitch.Visibility != Visibility.Visible)
            return;
        _showingTranslation = !_showingTranslation;
        _readBody.Text = ShownText();
        _readSwitchCaption.Text = _showingTranslation ? "Оригинал" : "Перевод";
        _copyNote++;
        _readCopyCaption.Text = "Копировать";
    }

    private async void NoteCopied(bool ok)
    {
        int note = ++_copyNote;
        _readCopyCaption.Text = ok ? "Скопировано" : "Буфер занят";
        await Task.Delay(900);
        if (note == _copyNote)
            _readCopyCaption.Text = "Копировать";
    }

    private System.Windows.Controls.Border BuildHistoryPanel() => new()
    {
        Visibility = Visibility.Collapsed,
        Padding = new Thickness(6),
        CornerRadius = new CornerRadius(12),
        Background = new SolidColorBrush(Color.FromArgb(0xF4, 0x16, 0x16, 0x1A)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x28, 0xF5, 0xF0, 0xE8)),
        BorderThickness = new Thickness(1),
        Child = _historyRow
    };

    private void ToggleHistory()
    {
        if (_historyPanel.Visibility == Visibility.Visible)
        {
            HideHistory();
            return;
        }

        _historyRow.Children.Clear();
        var paths = ShotHistory.Paths();
        if (paths.Count == 0)
        {
            _historyRow.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Пока нет",
                Margin = new Thickness(6, 4, 6, 4),
                Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xF5, 0xF0, 0xE8)),
                FontSize = 12
            });
        }
        else
        {
            foreach (var path in paths)
                _historyRow.Children.Add(HistoryThumb(path));
        }

        _historyPanel.Visibility = Visibility.Visible;
        System.Windows.Controls.Canvas.SetLeft(_historyPanel, 8);
        System.Windows.Controls.Canvas.SetTop(_historyPanel, 48);
    }

    private System.Windows.Controls.Button HistoryThumb(string path)
    {
        System.Windows.Media.ImageSource? image = null;
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            string thumb = ShotHistory.ThumbPath(path);
            string load = File.Exists(thumb) ? thumb : path;
            if (!File.Exists(thumb))
                bmp.DecodePixelWidth = 112;
            bmp.UriSource = new Uri(load);
            bmp.EndInit();
            bmp.Freeze();
            image = bmp;
        }
        catch
        {
            image = null;
        }

        var button = new System.Windows.Controls.Button
        {
            Width = 76,
            Height = 48,
            Margin = new Thickness(2, 0, 2, 0),
            Style = FlatStyle(),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Content = new System.Windows.Controls.Image
            {
                Source = image,
                Stretch = Stretch.UniformToFill,
                Width = 72,
                Height = 44
            }
        };
        string file = path;
        button.Click += (_, _) =>
        {
            try
            {
                using var bmp = new System.Drawing.Bitmap(file);
                ScreenCapture.CopyToClipboard(bmp);
            }
            catch
            {
                return;
            }

            HideHistory();
            ScreenshotToast.Show("Снимок в буфере обмена", _monitor, 1.4);
        };
        return button;
    }

    private void HideHistory()
    {
        _historyPanel.Visibility = Visibility.Collapsed;
        _historyRow.Children.Clear();
    }

    private static Rect NormRect(Point a, Point b)
    {
        double x = Math.Min(a.X, b.X);
        double y = Math.Min(a.Y, b.Y);
        return new Rect(x, y, Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
    }

    private void ShowReadBox(Rect rect)
    {
        _readBox.Visibility = Visibility.Visible;
        System.Windows.Controls.Canvas.SetLeft(_readBox, rect.X);
        System.Windows.Controls.Canvas.SetTop(_readBox, rect.Y);
        _readBox.Width = Math.Max(1, rect.Width);
        _readBox.Height = Math.Max(1, rect.Height);
    }

    private void HideReadBox() => _readBox.Visibility = Visibility.Collapsed;

    private CancellationToken BeginReading()
    {
        _readCancel?.Cancel();
        _readCancel = new CancellationTokenSource();
        return _readCancel.Token;
    }

    private async Task RecognizeAsync(Rect dip)
    {
        int gen = ++_readGen;
        var ct = BeginReading();
        _readOriginalText = "";
        _readRussianText = "";
        _showingTranslation = true;
        _readCard.Visibility = Visibility.Visible;
        PresentReading("Читаю…", false);
        _readCard.UpdateLayout();
        PlaceReadCard(dip);

        System.Drawing.Bitmap? crop = CropRect(dip);
        if (crop == null)
        {
            PresentReading("Слишком маленькая область.", false);
            return;
        }

        byte[] pixels;
        int width;
        int height;
        try
        {
            pixels = ScreenText.CopyPixels(crop, out width, out height);
        }
        finally
        {
            crop.Dispose();
        }

        if (pixels.Length == 0)
        {
            PresentReading("Слишком маленькая область.", false);
            return;
        }

        string original;
        try
        {
            original = await Task.Run(
                async () => await ScreenText.ReadAsync(pixels, width, height).ConfigureAwait(false),
                ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (gen != _readGen)
                return;
            PresentReading(ex.Message, false);
            return;
        }

        if (gen != _readGen)
            return;
        _readOriginalText = original;
        if (original.Length == 0)
        {
            PresentReading("Текст не найден.", false);
            return;
        }

        PresentReading("Перевожу…", false);
        string russian;
        try
        {
            russian = await ScreenText.TranslateAsync(original, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            russian = "";
        }

        if (gen != _readGen)
            return;
        _readRussianText = russian;
        bool translated = russian.Length > 0
            && !string.Equals(russian.Trim(), original.Trim(), StringComparison.Ordinal);
        _showingTranslation = translated;
        PresentReading(translated ? russian : original, true);
    }

    private void PlaceReadCard(Rect dip)
    {
        double width = _readCard.Width;
        double height = ReadCardHeight();
        double left = dip.X;
        double below = dip.Bottom + 10;
        double above = dip.Y - 10 - height;
        bool belowFits = below + height <= ActualHeight - 8;
        bool aboveFits = above >= 8;
        double top = belowFits ? below : aboveFits ? above : below;
        MoveReadCard(left, top, width, height);
    }

    private void KeepReadCardInside()
    {
        if (_readCard.Visibility != Visibility.Visible || ActualWidth < 32 || ActualHeight < 32)
            return;
        double left = System.Windows.Controls.Canvas.GetLeft(_readCard);
        double top = System.Windows.Controls.Canvas.GetTop(_readCard);
        if (double.IsNaN(left) || double.IsNaN(top))
            return;
        MoveReadCard(left, top, _readCard.Width, ReadCardHeight());
    }

    private double ReadCardHeight()
    {
        _readCard.Measure(new System.Windows.Size(Math.Max(1, _readCard.Width), double.PositiveInfinity));
        double height = _readCard.DesiredSize.Height;
        if (height < 1)
            height = _readCard.ActualHeight;
        if (height < 1)
            height = 120;
        return height;
    }

    private void MoveReadCard(double left, double top, double width, double height)
    {
        const double margin = 8;
        double maxLeft = ActualWidth - width - margin;
        double maxTop = ActualHeight - height - margin;
        if (maxLeft < margin)
            maxLeft = margin;
        if (maxTop < margin)
            maxTop = margin;
        if (left < margin)
            left = margin;
        if (left > maxLeft)
            left = maxLeft;
        if (top < margin)
            top = margin;
        if (top > maxTop)
            top = maxTop;
        System.Windows.Controls.Canvas.SetLeft(_readCard, left);
        System.Windows.Controls.Canvas.SetTop(_readCard, top);
    }

    private void OnReadCardDown(object sender, MouseButtonEventArgs e)
    {
        if (IsReadControl(e.OriginalSource as DependencyObject))
            return;
        _readCardDrag = true;
        var point = e.GetPosition(this);
        double left = System.Windows.Controls.Canvas.GetLeft(_readCard);
        double top = System.Windows.Controls.Canvas.GetTop(_readCard);
        if (double.IsNaN(left))
            left = 0;
        if (double.IsNaN(top))
            top = 0;
        _readCardGrab = new Point(point.X - left, point.Y - top);
        _readCard.CaptureMouse();
        e.Handled = true;
    }

    private void OnReadCardMove(object sender, MouseEventArgs e)
    {
        if (!_readCardDrag)
            return;
        var point = e.GetPosition(this);
        double height = _readCard.ActualHeight > 1 ? _readCard.ActualHeight : ReadCardHeight();
        MoveReadCard(point.X - _readCardGrab.X, point.Y - _readCardGrab.Y, _readCard.Width, height);
        e.Handled = true;
    }

    private void OnReadCardUp(object sender, MouseButtonEventArgs e)
    {
        if (!_readCardDrag)
            return;
        _readCardDrag = false;
        _readCard.ReleaseMouseCapture();
        e.Handled = true;
    }

    private static bool IsReadControl(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is System.Windows.Controls.Button or System.Windows.Controls.Primitives.ScrollBar)
                return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private System.Drawing.Bitmap? CropRect(Rect dip)
    {
        if (_bitmap.Width < 1 || _bitmap.Height < 1 || ActualWidth < 1 || ActualHeight < 1)
            return null;

        double sx = _bitmap.Width / ActualWidth;
        double sy = _bitmap.Height / ActualHeight;
        var rect = new System.Drawing.Rectangle(
            (int)Math.Round(dip.X * sx),
            (int)Math.Round(dip.Y * sy),
            Math.Max(1, (int)Math.Round(dip.Width * sx)),
            Math.Max(1, (int)Math.Round(dip.Height * sy)));
        rect.Intersect(new System.Drawing.Rectangle(0, 0, _bitmap.Width, _bitmap.Height));
        if (rect.Width < 4 || rect.Height < 4)
            return null;
        return _bitmap.Clone(rect, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    }

    private sealed class SelectionLayer : FrameworkElement
    {
        private static readonly SolidColorBrush Dim;
        private static readonly MediaPen BorderPen;
        private static readonly MediaPen HandlePen;

        public Rect Selection { get; set; }

        static SelectionLayer()
        {
            Dim = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0));
            Dim.Freeze();
            BorderPen = new MediaPen(Brushes.White, 1.5);
            BorderPen.Freeze();
            var handleBrush = new SolidColorBrush(Color.FromArgb(0xE0, 0x14, 0x14, 0x16));
            handleBrush.Freeze();
            HandlePen = new MediaPen(handleBrush, 1);
            HandlePen.Freeze();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 1 || h <= 1 || Selection.Width < 1 || Selection.Height < 1)
                return;

            Rect sel = Selection;
            if (sel.Top > 0)
            if (w <= 1 || h <= 1)
                return;
            if (Selection.Width < 1 || Selection.Height < 1)
            {
                dc.DrawRectangle(Dim, null, new Rect(0, 0, w, h));
                return;
            }
                dc.DrawRectangle(Dim, null, new Rect(0, Math.Max(0, sel.Bottom), w, h - Math.Max(0, sel.Bottom)));

            double midTop = Math.Clamp(sel.Top, 0, h);
            double midH = Math.Max(0, Math.Min(sel.Bottom, h) - midTop);
            if (sel.Left > 0 && midH > 0)
                dc.DrawRectangle(Dim, null, new Rect(0, midTop, Math.Min(sel.Left, w), midH));
            if (sel.Right < w && midH > 0)
                dc.DrawRectangle(Dim, null, new Rect(Math.Max(0, sel.Right), midTop, w - Math.Max(0, sel.Right), midH));

            double l = Math.Max(sel.Left, 1);
            double t = Math.Max(sel.Top, 1);
            double r = Math.Min(sel.Right, w - 1);
            double b = Math.Min(sel.Bottom, h - 1);
            if (r > l && b > t)
                dc.DrawRectangle(null, BorderPen, new Rect(l, t, r - l, b - t));

            double hx(double x) => x <= 1 ? 7 : x >= w - 1 ? w - 7 : x;
            double hy(double y) => y <= 1 ? 7 : y >= h - 1 ? h - 7 : y;
            DrawHandle(dc, hx(sel.Left), hy(sel.Top));
            DrawHandle(dc, hx(sel.Right), hy(sel.Top));
            DrawHandle(dc, hx(sel.Left), hy(sel.Bottom));
            DrawHandle(dc, hx(sel.Right), hy(sel.Bottom));
            DrawHandle(dc, hx(sel.Left + sel.Width / 2), hy(sel.Top));
            DrawHandle(dc, hx(sel.Right), hy(sel.Top + sel.Height / 2));
            DrawHandle(dc, hx(sel.Left + sel.Width / 2), hy(sel.Bottom));
            DrawHandle(dc, hx(sel.Left), hy(sel.Top + sel.Height / 2));
        }

        private static void DrawHandle(DrawingContext dc, double x, double y)
        {
            const double hs = 8;
            dc.DrawRectangle(Brushes.White, HandlePen, new Rect(x - hs / 2, y - hs / 2, hs, hs));
        }
    }

    private sealed class MarkupLayer : FrameworkElement
    {
        public ShotMarkup? Markup { get; set; }

        protected override void OnRender(DrawingContext dc)
        {
            if (Markup == null || ActualWidth < 1)
                return;

            double dip = 1;
            try { dip = VisualTreeHelper.GetDpi(this).PixelsPerDip; }
            catch { /* слой ещё без hwnd */ }
            Markup.Draw(dc, dip);
        }
    }
}
