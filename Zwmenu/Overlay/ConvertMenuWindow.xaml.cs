using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Zwmenu.Services;
using IOPath = System.IO.Path;

namespace Zwmenu.Overlay;

/// <summary>
/// Три орбиты вокруг файла с большой чистой дыркой под ярлык.
/// Снаружи формат, середина размер, внутри сжатие и фон.
/// </summary>
public partial class ConvertMenuWindow : Window, IRequestClose
{
    // Больше полотна + дырка ~96px радиус: типичный ярлык (~80–100) не перекрывается.
    private const double CanvasSize = 600;
    private const double HoleRadius = 96;
    private const double FormatOrbit = 228;
    private const double SizeOrbit = 168;
    // HoleRadius + ActionChip/2 + зазор — чипы не залезают в дырку.
    private const double ActionOrbit = 130;
    private const double FormatChip = 46;
    private const double SizeChip = 46;
    private const double ActionChip = 52;

    private IReadOnlyList<string> _files;

    public void UseFiles(IReadOnlyList<string> files) => _files = files;
    private bool _busy;
    private CancellationTokenSource? _batchCts;
    private readonly ControlTemplate _chipTemplate;
    private readonly List<UIElement> _animated = [];

    public ConvertMenuWindow(IReadOnlyList<string> imageFiles)
    {
        InitializeComponent();
        _files = imageFiles;
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
        // UIA BoundingRectangle часто врёт на длинных именах — как в media/text.
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

        // Пончик: внешний диск минус дырка под ярлык.
        double outerR = FormatOrbit + FormatChip * 0.72;
        MatteDonut.Data = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new EllipseGeometry(new Point(cx, cy), outerR, outerR),
            new EllipseGeometry(new Point(cx, cy), HoleRadius, HoleRadius));

        HoleRim.Width = HoleRadius * 2;
        HoleRim.Height = HoleRadius * 2;
        Canvas.SetLeft(HoleRim, cx - HoleRadius);
        Canvas.SetTop(HoleRim, cy - HoleRadius);

        OrbitRing.Width = FormatOrbit * 2;
        OrbitRing.Height = FormatOrbit * 2;
        Canvas.SetLeft(OrbitRing, cx - FormatOrbit);
        Canvas.SetTop(OrbitRing, cy - FormatOrbit);

        AddOrbitGuide(cx, cy, SizeOrbit, Color.FromArgb(0x38, 0x8E, 0xB4, 0xD4));
        AddOrbitGuide(cx, cy, ActionOrbit, Color.FromArgb(0x40, 0xA8, 0xC5, 0xA0));

        var formats = ImageConvertService.Formats;
        for (int i = 0; i < formats.Count; i++)
        {
            double angle = -Math.PI / 2 + i * (2 * Math.PI / formats.Count);
            PlaceChip(
                MakeChip(formats[i].Title, "fmt:" + formats[i].Id, FormatChip, ChipKind.Format),
                cx, cy, angle, FormatOrbit, FormatChip);
        }

        var sizes = ImageResizeService.Presets;
        int sizeSlots = sizes.Count + 1;
        for (int i = 0; i < sizes.Count; i++)
        {
            double angle = -Math.PI / 2 + (Math.PI / sizeSlots) + i * (2 * Math.PI / sizeSlots);
            var btn = MakeChip(sizes[i].Title, "size:" + sizes[i].Id, SizeChip, ChipKind.Size);
            btn.ToolTip = sizes[i].Hint;
            if (sizes[i].Title.Length > 4)
                btn.FontSize = 10;
            PlaceChip(btn, cx, cy, angle, SizeOrbit, SizeChip);
        }
        {
            double angle = -Math.PI / 2 + (Math.PI / sizeSlots) + sizes.Count * (2 * Math.PI / sizeSlots);
            var custom = MakeChip("Свой", "size:custom", SizeChip, ChipKind.Size);
            custom.ToolTip = "Ввести ширину и высоту";
            PlaceChip(custom, cx, cy, angle, SizeOrbit, SizeChip);
        }

        var presets = ImageConvertService.CompressPresets;
        // Строгий крест: верх / право / низ / лево — симметрия и воздух вокруг ярлыка.
        (string title, string tag, string tip, ChipKind kind, double angle)[] actions =
        [
            (presets[0].Title, "zip:" + presets[0].Id, presets[0].Hint, ChipKind.Compress, -Math.PI / 2),
            (presets[1].Title, "zip:" + presets[1].Id, presets[1].Hint, ChipKind.Compress, 0),
            (presets[2].Title, "zip:" + presets[2].Id, presets[2].Hint, ChipKind.Compress, Math.PI / 2),
            ("Фон", "bg:cut", "Убрать фон одним кликом (PNG)", ChipKind.Bg, Math.PI)
        ];
        foreach (var a in actions)
        {
            var btn = MakeChip(a.title, a.tag, ActionChip, a.kind);
            btn.ToolTip = a.tip;
            PlaceChip(btn, cx, cy, a.angle, ActionOrbit, ActionChip);
        }
    }

    private void AddOrbitGuide(double cx, double cy, double orbit, Color stroke)
    {
        var el = new Ellipse
        {
            Width = orbit * 2,
            Height = orbit * 2,
            StrokeThickness = 1,
            IsHitTestVisible = false,
            Opacity = 0,
            Stroke = new SolidColorBrush(stroke)
        };
        Canvas.SetLeft(el, cx - orbit);
        Canvas.SetTop(el, cy - orbit);
        RingCanvas.Children.Insert(0, el);
        _animated.Add(el);
    }

    private void PlaceChip(Button btn, double cx, double cy, double angle, double orbit, double size)
    {
        double x = cx + Math.Cos(angle) * orbit - size / 2;
        double y = cy + Math.Sin(angle) * orbit - size / 2;
        Canvas.SetLeft(btn, Math.Round(x));
        Canvas.SetTop(btn, Math.Round(y));
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
        foreach (var el in _animated)
            SnapIn(el);
    }

    private static void SnapIn(UIElement el)
    {
        el.BeginAnimation(OpacityProperty, null);
        el.Opacity = 1;
        if (el is Button btn && btn.RenderTransform is ScaleTransform st)
        {
            st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            st.ScaleX = 1;
            st.ScaleY = 1;
        }
    }

    private enum ChipKind { Format, Compress, Size, Bg }

    private Button MakeChip(string title, string tag, double size, ChipKind kind)
    {
        var (bg, border) = kind switch
        {
            ChipKind.Compress => (
                Color.FromArgb(0xEE, 0x1E, 0x32, 0x2A),
                Color.FromArgb(0xAA, 0xA8, 0xC5, 0xA0)),
            ChipKind.Size => (
                Color.FromArgb(0xEE, 0x1A, 0x28, 0x3A),
                Color.FromArgb(0xAA, 0x8E, 0xB4, 0xD4)),
            ChipKind.Bg => (
                Color.FromArgb(0xF0, 0x3A, 0x24, 0x32),
                Color.FromArgb(0xBB, 0xE0, 0xB0, 0xA8)),
            _ => (
                Color.FromArgb(0xF2, 0x16, 0x16, 0x1A),
                Color.FromArgb(0x88, 0xF5, 0xF0, 0xE8))
        };

        var btn = new Button
        {
            Content = title,
            Tag = tag,
            Width = size,
            Height = size,
            Cursor = Cursors.Hand,
            Focusable = false,
            FontSize = title.Length >= 5 ? 10 : 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF7, 0xF3, 0xEC)),
            Background = new SolidColorBrush(bg),
            BorderThickness = new Thickness(1.35),
            BorderBrush = new SolidColorBrush(border),
            Template = _chipTemplate
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

        if (tag == "size:custom")
        {
            RunCustomSize();
            return;
        }

        string status = tag switch
        {
            _ when tag.StartsWith("zip:", StringComparison.Ordinal) => "Сжимаю…",
            _ when tag.StartsWith("size:", StringComparison.Ordinal) => "Меняю размер…",
            _ when tag.StartsWith("bg:", StringComparison.Ordinal) => "Убираю фон…",
            _ => "Конвертация…"
        };

        StartBatch(status, file => DispatchAction(file, tag));
    }

    private void RunCustomSize()
    {
        var dlg = new ResizeCustomDialog();
        dlg.Owner = this;
        bool? ok = dlg.ShowDialog();
        if (ok != true)
            return;

        int w = dlg.ResultWidth;
        int h = dlg.ResultHeight;
        StartBatch("Меняю размер…", file => ImageResizeService.ResizeExact(file, w, h));
    }

    private static (string? Path, string? Error) DispatchAction(string file, string tag)
    {
        if (tag.StartsWith("zip:", StringComparison.Ordinal))
            return ImageConvertService.Compress(file, tag[4..]);
        if (tag.StartsWith("fmt:", StringComparison.Ordinal))
            return ImageConvertService.Convert(file, tag[4..]);
        if (tag.StartsWith("size:", StringComparison.Ordinal))
            return ImageResizeService.ResizeToPreset(file, tag[5..]);
        if (tag.StartsWith("bg:", StringComparison.Ordinal))
            return BackgroundRemoveService.Remove(file);
        return (null, "Неизвестное действие.");
    }

    private void StartBatch(string status, Func<string, (string? Path, string? Error)> work)
    {
        _busy = true;
        ShowStatus(status, ok: null);
        IsHitTestVisible = false;

        _batchCts?.Cancel();
        _batchCts = new CancellationTokenSource();
        var ct = _batchCts.Token;
        string[] files = _files.ToArray();
        Task.Run(() =>
        {
            int ok = 0;
            string? lastError = null;
            string? lastOut = null;
            long savedBytes = 0;

            foreach (var file in files)
            {
                if (ct.IsCancellationRequested)
                    return;

                long before = 0;
                try { before = new FileInfo(file).Length; } catch { /* ignore */ }

                var (path, err) = work(file);
                if (path != null)
                {
                    ok++;
                    lastOut = path;
                    try
                    {
                        long after = new FileInfo(path).Length;
                        if (before > after)
                            savedBytes += before - after;
                    }
                    catch { /* ignore */ }
                }
                else
                    lastError = err;
            }

            if (ct.IsCancellationRequested)
                return;

            Dispatcher.BeginInvoke(() =>
            {
                if (!IsLoaded || ct.IsCancellationRequested)
                    return;

                if (ok > 0 && lastOut != null)
                {
                    string msg = files.Length == 1
                        ? "Готово: " + IOPath.GetFileName(lastOut)
                        : $"Готово: {ok} из {files.Length}";
                    if (savedBytes > 0)
                        msg += $" (−{FormatSize(savedBytes)})";
                    ShowStatus(msg, ok: true);
                    UiSounds.Notify();
                    var closeTimer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(900)
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
                    ShowStatus(lastError ?? "Не удалось.", ok: false);
                    IsHitTestVisible = true;
                    _busy = false;
                }
            });
        }, ct);
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
            true => new SolidColorBrush(Color.FromRgb(0xA8, 0xC5, 0xA0)),
            false => new SolidColorBrush(Color.FromRgb(0xD4, 0xA1, 0x9A)),
            _ => new SolidColorBrush(Color.FromRgb(0xF5, 0xF0, 0xE8))
        };
        StatusCard.Visibility = Visibility.Visible;
        StatusCard.Measure(new System.Windows.Size(280, 80));
        double cx = CanvasSize / 2;
        Canvas.SetLeft(StatusCard, cx - StatusCard.DesiredSize.Width / 2);
        Canvas.SetTop(StatusCard, CanvasSize - 58);
        Panel.SetZIndex(StatusCard, 40);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} Б";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} КБ";
        return $"{bytes / (1024.0 * 1024.0):0.##} МБ";
    }

    private static ControlTemplate BuildChipTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
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
            new SolidColorBrush(Color.FromArgb(0xF8, 0x3A, 0x3E, 0x48))));
        hover.Setters.Add(new Setter(BorderBrushProperty,
            new SolidColorBrush(Color.FromArgb(0xEE, 0xF5, 0xF0, 0xE8))));
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
