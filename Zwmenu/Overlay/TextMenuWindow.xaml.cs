using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Zwmenu.Services;
using IOPath = System.IO.Path;

namespace Zwmenu.Overlay;

/// <summary>
/// Кольцо для .json / .txt / .yml / .log и др.: 10 + 10 инструментов.
/// </summary>
public partial class TextMenuWindow : Window, IRequestClose
{
    private const double CanvasSize = 560;
    private const double HoleRadius = 92;
    private const double OuterOrbit = 208;
    private const double InnerOrbit = 138;
    private const double Chip = 42;

    private IReadOnlyList<string> _files;

    public void UseFiles(IReadOnlyList<string> files) => _files = files;
    private bool _busy;
    private CancellationTokenSource? _batchCts;
    private readonly ControlTemplate _chipTemplate;
    private readonly List<UIElement> _animated = [];

    public TextMenuWindow(IReadOnlyList<string> textFiles)
    {
        InitializeComponent();
        _files = textFiles;
        _chipTemplate = BuildChipTemplate();
        BuildRing();

        SourceInitialized += (_, _) => ApplyToolWindowStyle();
        Loaded += (_, _) => PlayIntro();
        Closed += (_, _) => _batchCts?.Cancel();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                RequestClose();
            }
        };
    }

    public void RequestClose()
    {
        _batchCts?.Cancel();
        try { Close(); }
        catch { /* already */ }
    }

    public void ShowNearCursor()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        int ax = cursor.X;
        int ay = cursor.Y;
        if (ShellSelectionService.TryGetSelectionCenter(out int sx, out int sy))
        {
            double dist = Math.Sqrt((sx - ax) * (sx - ax) + (sy - ay) * (sy - ay));
            if (dist < 48)
            {
                ax = sx;
                ay = sy;
            }
        }
        ShowAtScreen(ax, ay);
    }

    public void ShowAtScreen(int screenX, int screenY)
    {
        Left = -4000;
        Top = -4000;
        Width = CanvasSize;
        Height = CanvasSize;
        Show();
        UpdateLayout();
        RadialLayout.PlaceNearScreen(this, CanvasSize, screenX, screenY);
    }

    private void BuildRing()
    {
        double cx = CanvasSize / 2;
        double cy = CanvasSize / 2;

        double outerR = OuterOrbit + Chip * 0.55;
        MatteDonut.Data = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new EllipseGeometry(new Point(cx, cy), outerR, outerR),
            new EllipseGeometry(new Point(cx, cy), HoleRadius, HoleRadius));

        PlaceEllipse(HoleRim, cx, cy, HoleRadius);
        PlaceEllipse(OrbitRing, cx, cy, OuterOrbit);
        PlaceEllipse(ToolOrbitRing, cx, cy, InnerOrbit);

        var outer = TextFileService.OuterTools;
        for (int i = 0; i < outer.Count; i++)
        {
            double angle = RadialLayout.AngleTop(outer.Count, i);
            var btn = MakeChip(outer[i].Title, outer[i].Tag, ChipKind.Outer, outer[i].Hint);
            PlaceChip(btn, cx, cy, angle, OuterOrbit);
        }

        var inner = TextFileService.InnerTools;
        for (int i = 0; i < inner.Count; i++)
        {
            // Сдвиг на полшага — в промежутках внешнего кольца.
            double angle = RadialLayout.AngleTop(inner.Count, i) + Math.PI / inner.Count;
            var btn = MakeChip(inner[i].Title, inner[i].Tag, ChipKind.Inner, inner[i].Hint);
            PlaceChip(btn, cx, cy, angle, InnerOrbit);
        }
    }

    private static void PlaceEllipse(Ellipse el, double cx, double cy, double radius)
    {
        el.Width = radius * 2;
        el.Height = radius * 2;
        Canvas.SetLeft(el, cx - radius);
        Canvas.SetTop(el, cy - radius);
    }

    private void PlaceChip(Button btn, double cx, double cy, double angle, double orbit)
    {
        double x = cx + Math.Cos(angle) * orbit - Chip / 2;
        double y = cy + Math.Sin(angle) * orbit - Chip / 2;
        Canvas.SetLeft(btn, x);
        Canvas.SetTop(btn, y);
        Panel.SetZIndex(btn, 10);
        btn.Opacity = 0;
        btn.RenderTransformOrigin = new Point(0.5, 0.5);
        btn.RenderTransform = new ScaleTransform(0.82, 0.82);
        RingCanvas.Children.Add(btn);
        _animated.Add(btn);
    }

    private void PlayIntro()
    {
        SnapIn(MatteDonut);
        SnapIn(HoleRim);
        SnapIn(OrbitRing);
        SnapIn(ToolOrbitRing);
        foreach (var el in _animated)
            SnapIn(el);
    }

    private static void SnapIn(UIElement el)
    {
        el.BeginAnimation(UIElement.OpacityProperty, null);
        el.Opacity = 1;
        if (el is Button btn && btn.RenderTransform is ScaleTransform st)
        {
            st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            st.ScaleX = 1;
            st.ScaleY = 1;
        }
    }

    private enum ChipKind { Outer, Inner }

    private Button MakeChip(string title, string tag, ChipKind kind, string? tip)
    {
        var (bg, border) = kind == ChipKind.Outer
            ? (
                Color.FromArgb(0xF0, 0x1A, 0x18, 0x14),
                Color.FromArgb(0x99, 0xE8, 0xD4, 0xA8))
            : (
                Color.FromArgb(0xEE, 0x2A, 0x26, 0x1C),
                Color.FromArgb(0xAA, 0xD4, 0xB0, 0x90));

        var btn = new Button
        {
            Content = title,
            Tag = tag,
            Width = Chip,
            Height = Chip,
            Cursor = Cursors.Hand,
            Focusable = false,
            FontSize = title.Length >= 5 ? 9 : 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF0, 0xE8)),
            Background = new SolidColorBrush(bg),
            BorderThickness = new Thickness(1.2),
            BorderBrush = new SolidColorBrush(border),
            Template = _chipTemplate,
            ToolTip = tip
        };
        btn.Click += OnActionClick;
        btn.MouseEnter += (_, _) => SetScale(btn, 1.08);
        btn.MouseLeave += (_, _) => SetScale(btn, 1.0);
        return btn;
    }

    private static void SetScale(Button btn, double to)
    {
        if (btn.RenderTransform is not ScaleTransform st)
            return;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        st.ScaleX = to;
        st.ScaleY = to;
    }

    private void OnActionClick(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: string tag })
            return;

        string status = tag switch
        {
            "act:valid" => "Проверяю…",
            "act:pretty" => "Форматирую…",
            "act:mini" => "Сжимаю текст…",
            "act:copy" => "Копирую…",
            "act:stat" => "Считаю…",
            "act:hash" => "Хеширую…",
            _ => "Обрабатываю…"
        };

        StartBatch(status, tag);
    }

    private void StartBatch(string status, string tag)
    {
        _busy = true;
        ShowStatus(status, ok: null);
        IsHitTestVisible = false;

        _batchCts?.Cancel();
        _batchCts = new CancellationTokenSource();
        var ct = _batchCts.Token;
        string[] files = _files.ToArray();
        bool needsSta = tag is "act:copy";

        void Work()
        {
            int ok = 0;
            string? lastMsg = null;
            string? lastOut = null;
            bool anyFail = false;

            foreach (var file in files)
            {
                if (ct.IsCancellationRequested)
                    return;

                var result = TextFileService.Run(file, tag);
                if (result.Ok)
                {
                    ok++;
                    lastMsg = result.Message;
                    if (result.OutputPath != null)
                        lastOut = result.OutputPath;
                }
                else
                {
                    anyFail = true;
                    lastMsg = result.Message;
                }
            }

            if (ct.IsCancellationRequested)
                return;

            void Finish()
            {
                if (!IsLoaded || ct.IsCancellationRequested)
                    return;

                if (ok > 0 && !anyFail)
                {
                    string msg = files.Length == 1
                        ? (lastMsg ?? (lastOut != null ? "Готово: " + IOPath.GetFileName(lastOut) : "Готово"))
                        : $"Готово: {ok} из {files.Length}";
                    ShowStatus(msg, ok: true);
                    UiSounds.Notify();
                    int ms = tag is "act:valid" or "act:stat" or "act:copy" ? 2800 : 900;
                    var closeTimer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(ms)
                    };
                    closeTimer.Tick += (_, _) =>
                    {
                        closeTimer.Stop();
                        RequestClose();
                    };
                    closeTimer.Start();
                }
                else
                {
                    ShowStatus(lastMsg ?? "Не удалось.", ok: false);
                    IsHitTestVisible = true;
                    _busy = false;
                }
            }

            if (Dispatcher.CheckAccess()) Finish();
            else Dispatcher.BeginInvoke(Finish);
        }

        if (needsSta)
        {
            try { Work(); }
            catch (Exception ex)
            {
                ShowStatus(ex.Message, ok: false);
                IsHitTestVisible = true;
                _busy = false;
            }
        }
        else
        {
            Task.Run(Work, ct);
        }
    }

    public void MentionHolders()
    {
        IReadOnlyList<string> files = _files;
        Task.Run(() =>
        {
            string? text = FileLock.Brief(files);
            if (text == null)
                return;
            Dispatcher.BeginInvoke(() =>
            {
                if (IsLoaded)
                    ShowStatus(text, null);
            });
        });
    }

    private void ShowStatus(string text, bool? ok)
    {
        StatusText.Text = text;
        StatusText.Foreground = ok switch
        {
            true => new SolidColorBrush(Color.FromRgb(0xC5, 0xD4, 0xA0)),
            false => new SolidColorBrush(Color.FromRgb(0xE0, 0xB0, 0xA8)),
            _ => new SolidColorBrush(Color.FromRgb(0xF5, 0xF0, 0xE8))
        };
        StatusCard.Visibility = Visibility.Visible;
        StatusCard.Measure(new System.Windows.Size(360, 160));
        double cx = CanvasSize / 2;
        double cardW = Math.Min(360, Math.Max(StatusCard.DesiredSize.Width, 180));
        Canvas.SetLeft(StatusCard, cx - cardW / 2);
        // Чуть выше низа, чтобы многострочный текст валидации не обрезался.
        double top = CanvasSize - Math.Max(StatusCard.DesiredSize.Height + 12, 56);
        Canvas.SetTop(StatusCard, Math.Max(CanvasSize * 0.62, top));
        Panel.SetZIndex(StatusCard, 40);
    }

    private static ControlTemplate BuildChipTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(999));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(BackgroundProperty,
            new SolidColorBrush(Color.FromArgb(0xF8, 0x3A, 0x36, 0x2A))));
        hover.Setters.Add(new Setter(BorderBrushProperty,
            new SolidColorBrush(Color.FromArgb(0xEE, 0xE8, 0xD4, 0xA8))));
        template.Triggers.Add(hover);
        return template;
    }

    private void ApplyToolWindowStyle()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        const int GwlExstyle = -20;
        const int WsExToolwindow = 0x00000080;
        const int WsExNoactivate = 0x08000000;
        int style = Native.GetWindowLong(hwnd, GwlExstyle);
        Native.SetWindowLong(hwnd, GwlExstyle, style | WsExToolwindow | WsExNoactivate);
    }

    private static class Native
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
