using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Zwmenu.Services;
using IOPath = System.IO.Path;

namespace Zwmenu.Overlay;

/// <summary>
/// Два идеальных кольца: снаружи форматы (8×45°), внутри инструменты (8×45°, со сдвигом).
/// Ярлык в центре дырки; курсор = якорь (UIA с длинным именем часто врёт).
/// </summary>
public partial class MediaMenuWindow : Window, IRequestClose
{
    private const double CanvasSize = 560;
    private const double HoleRadius = 92;
    private const double FormatOrbit = 208;
    private const double ToolOrbit = 138;
    private const double Chip = 44;

    private IReadOnlyList<string> _files;
    private bool _builtForVideo;

    public void UseFiles(IReadOnlyList<string> files)
    {
        bool video = MediaConvertService.HasVideo(files);
        _files = files;
        if (video == _builtForVideo)
            return;
        _builtForVideo = video;
        for (int i = RingCanvas.Children.Count - 1; i >= 0; i--)
        {
            if (RingCanvas.Children[i] is Button)
                RingCanvas.Children.RemoveAt(i);
        }
        _animated.RemoveAll(el => el is Button);
        BuildRing();
    }
    private bool _busy;
    private CancellationTokenSource? _batchCts;
    private readonly ControlTemplate _chipTemplate;
    private readonly List<UIElement> _animated = [];

    public MediaMenuWindow(IReadOnlyList<string> mediaFiles)
    {
        InitializeComponent();
        _files = mediaFiles;
        _chipTemplate = BuildChipTemplate();
        BuildRing();
        _builtForVideo = MediaConvertService.HasVideo(_files);

        SourceInitialized += (_, _) => ApplyToolWindowStyle();
        Loaded += (_, _) =>
        {
            PlayIntro();
            _ = FfmpegHost.EnsureAsync();
        };
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
        // При Ctrl курсор уже на файле — это самый точный центр.
        // UIA BoundingRectangle с длинным именем уезжает вниз/вбок.
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

        // Пончик ровно под внешние кнопки (центры на FormatOrbit).
        double outerR = FormatOrbit + Chip * 0.55;
        MatteDonut.Data = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new EllipseGeometry(new Point(cx, cy), outerR, outerR),
            new EllipseGeometry(new Point(cx, cy), HoleRadius, HoleRadius));

        PlaceEllipse(HoleRim, cx, cy, HoleRadius);
        PlaceEllipse(OrbitRing, cx, cy, FormatOrbit);
        PlaceEllipse(ToolOrbitRing, cx, cy, ToolOrbit);

        // Внешнее: 8 форматов, шаг 45°, старт сверху (MP3).
        var formats = MediaConvertService.FormatsFor(_files);
        int fn = formats.Count;
        for (int i = 0; i < fn; i++)
        {
            double angle = RadialLayout.AngleTop(fn, i);
            var btn = MakeChip(formats[i].Title, "fmt:" + formats[i].Id, ChipKind.Format);
            btn.ToolTip = formats[i].Video ? "Видео " + formats[i].Extension : "Аудио " + formats[i].Extension;
            PlaceChip(btn, cx, cy, angle, FormatOrbit);
        }

        // Внутреннее: 8 инструментов, тот же шаг, сдвиг 22.5° — в промежутках форматов.
        var tools = MediaConvertService.Tools;
        int tn = tools.Count;
        for (int i = 0; i < tn; i++)
        {
            double angle = RadialLayout.AngleTop(tn, i) + Math.PI / tn; // +22.5° при 8
            var kind = tools[i].Tag.StartsWith("zip:", StringComparison.Ordinal)
                ? ChipKind.Quality
                : ChipKind.Fx;
            var btn = MakeChip(tools[i].Title, tools[i].Tag, kind, tools[i].Hint);
            PlaceChip(btn, cx, cy, angle, ToolOrbit);
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
        // Центр кнопки строго на окружности; одинаковый Chip → идеальная симметрия.
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

    private enum ChipKind { Format, Quality, Fx }

    private Button MakeChip(string title, string tag, ChipKind kind, string? tip = null)
    {
        var (bg, border) = kind switch
        {
            ChipKind.Quality => (
                Color.FromArgb(0xEE, 0x1A, 0x32, 0x36),
                Color.FromArgb(0xAA, 0xA0, 0xD4, 0xC8)),
            ChipKind.Fx => (
                Color.FromArgb(0xEE, 0x2A, 0x28, 0x3C),
                Color.FromArgb(0xAA, 0xC8, 0xB0, 0xE0)),
            _ => (
                Color.FromArgb(0xF2, 0x14, 0x18, 0x20),
                Color.FromArgb(0x88, 0xC8, 0xE0, 0xF0))
        };

        var btn = new Button
        {
            Content = title,
            Tag = tag,
            Width = Chip,
            Height = Chip,
            Cursor = Cursors.Hand,
            Focusable = false,
            FontSize = title.Length >= 5 ? 9.5 : 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF5, 0xFA)),
            Background = new SolidColorBrush(bg),
            BorderThickness = new Thickness(1.25),
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
            _ when tag.StartsWith("zip:", StringComparison.Ordinal) => "Сжимаю…",
            "act:fadein" => "Нарастание…",
            "act:fadeout" => "Затухание…",
            "act:gain" => "Усиливаю…",
            "act:mono" => "Моно…",
            "act:extract" => "Достаю звук…",
            _ => "Конвертация…"
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
        var progress = new Progress<string>(msg =>
        {
            if (!ct.IsCancellationRequested)
                Dispatcher.BeginInvoke(() => { if (IsLoaded) ShowStatus(msg, ok: null); });
        });

        Task.Run(async () =>
        {
            try
            {
                if (!ct.IsCancellationRequested)
                    await Dispatcher.InvokeAsync(() => { if (IsLoaded) ShowStatus("Готовлю FFmpeg…", ok: null); });
                string? exe = await FfmpegHost.EnsureAsync(progress, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                    return;
                if (exe == null)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!IsLoaded) return;
                        ShowStatus(FfmpegHost.LastError ?? "FFmpeg не установлен.", ok: false);
                        IsHitTestVisible = true;
                        _busy = false;
                    });
                    return;
                }

                await Dispatcher.InvokeAsync(() => { if (IsLoaded) ShowStatus(status, ok: null); });

                int okCount = 0;
                string? lastError = null;
                string? lastOut = null;
                long savedBytes = 0;

                foreach (var file in files)
                {
                    if (ct.IsCancellationRequested)
                        return;

                    long before = 0;
                    try { before = new FileInfo(file).Length; } catch { /* ignore */ }

                    var (path, err) = await DispatchAsync(file, tag, ct).ConfigureAwait(false);
                    if (path != null)
                    {
                        okCount++;
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

                await Dispatcher.InvokeAsync(() =>
                {
                    if (!IsLoaded || ct.IsCancellationRequested)
                        return;

                    if (okCount > 0 && lastOut != null)
                    {
                        string msg = files.Length == 1
                            ? "Готово: " + IOPath.GetFileName(lastOut)
                            : $"Готово: {okCount} из {files.Length}";
                        if (savedBytes > 0)
                            msg += $" (−{FormatSize(savedBytes)})";
                        ShowStatus(msg, ok: true);
                        UiSounds.Notify();
                        var closeTimer = new System.Windows.Threading.DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(1000)
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
                        ShowStatus(lastError ?? FfmpegHost.LastError ?? "Не удалось.", ok: false);
                        IsHitTestVisible = true;
                        _busy = false;
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // закрыли меню
            }
        }, ct);
    }

    private static Task<(string? Path, string? Error)> DispatchAsync(string file, string tag, CancellationToken ct)
    {
        if (tag.StartsWith("fmt:", StringComparison.Ordinal))
            return MediaConvertService.ConvertAsync(file, tag[4..], ct: ct);
        if (tag.StartsWith("zip:", StringComparison.Ordinal))
            return MediaConvertService.CompressAsync(file, tag[4..], ct);
        return tag switch
        {
            "act:extract" => MediaConvertService.ExtractAudioAsync(file, ct),
            "act:mono" => MediaConvertService.ToMonoAsync(file, ct),
            "act:fadein" => MediaConvertService.FadeInAsync(file, ct: ct),
            "act:fadeout" => MediaConvertService.FadeOutAsync(file, ct: ct),
            "act:gain" => MediaConvertService.GainAsync(file, 6, ct),
            _ => Task.FromResult<(string?, string?)>((null, "Неизвестное действие."))
        };
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
            true => new SolidColorBrush(Color.FromRgb(0xA0, 0xD4, 0xC8)),
            false => new SolidColorBrush(Color.FromRgb(0xE0, 0xB0, 0xA8)),
            _ => new SolidColorBrush(Color.FromRgb(0xF0, 0xF5, 0xFA))
        };
        StatusCard.Visibility = Visibility.Visible;
        StatusCard.Measure(new System.Windows.Size(300, 80));
        double cx = CanvasSize / 2;
        Canvas.SetLeft(StatusCard, cx - StatusCard.DesiredSize.Width / 2);
        Canvas.SetTop(StatusCard, CanvasSize - 52);
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
            new SolidColorBrush(Color.FromArgb(0xF8, 0x32, 0x3A, 0x48))));
        hover.Setters.Add(new Setter(BorderBrushProperty,
            new SolidColorBrush(Color.FromArgb(0xEE, 0xC8, 0xE0, 0xF0))));
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
