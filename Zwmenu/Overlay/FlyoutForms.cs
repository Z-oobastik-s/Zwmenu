using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Zwmenu.Services;

namespace Zwmenu.Overlay;

/// <summary>Готовый выезд: корень на холсте, зона hit test, список живых частей.</summary>
public sealed class FlyoutMount
{
    public required FrameworkElement Root { get; init; }
    public Rect Cluster { get; init; }
    public required List<FrameworkElement> Parts { get; init; }
}

/// <summary>
/// Формы выезда вокруг диска. Симметрия и близость важнее «эффектов».
/// keepOut = кромка сразу за кнопками кольца, не декоративный ореол скина.
/// </summary>
public static class FlyoutForms
{
    public static FlyoutMount Build(
        string formId,
        double cx,
        double cy,
        double keepOut,
        double aimAngle,
        string title,
        IReadOnlyList<FrameworkElement> items,
        double canvasW,
        double canvasH)
    {
        string form = FlyoutLooks.EffectiveForm(formId, items.Count, deskUi: false);
        return form switch
        {
            "fan" => BuildFan(cx, cy, keepOut, aimAngle, title, items, canvasW, canvasH),
            "ribbon" or "arc" => BuildRibbon(cx, cy, keepOut, aimAngle, title, items, canvasW, canvasH),
            "crown" => BuildCrown(cx, cy, keepOut, aimAngle, title, items, canvasW, canvasH),
            "burst" => BuildBurst(cx, cy, keepOut, aimAngle, title, items, canvasW, canvasH),
            "dock" => BuildDock(cx, cy, keepOut, title, items, canvasW, canvasH, bandW: 260),
            _ => BuildHalo(cx, cy, keepOut, aimAngle, title, items, canvasW, canvasH)
        };
    }

    /// <summary>Только пульт: ползунки в стыке.</summary>
    public static FlyoutMount BuildDense(
        string formId,
        double cx,
        double cy,
        double keepOut,
        string title,
        FrameworkElement content,
        double contentW,
        double contentH,
        double canvasW,
        double canvasH)
    {
        _ = formId;
        return BuildDockContent(cx, cy, keepOut, title, content, contentW, contentH, canvasW, canvasH);
    }

    /// <summary>Ореол: только круглые значки. Шаг считаем из размера точки, чтобы не наезжали.</summary>
    static FlyoutMount BuildHalo(
        double cx, double cy, double keepOut, double aimAngle,
        string title, IReadOnlyList<FrameworkElement> items,
        double canvasW, double canvasH)
    {
        var host = MakeHost();
        var parts = new List<FrameworkElement>();
        int n = Math.Max(items.Count, 1);
        const double dot = 46;
        const double gap = 14;
        bool full = n >= 8;
        double orbit;
        double start;
        double sweep;
        if (full)
        {
            orbit = Math.Max(keepOut + 40, n * (dot + gap) / (2 * Math.PI));
            start = aimAngle + Math.PI / n;
            sweep = Math.PI * 2 * (n - 1) / n;
        }
        else
        {
            double step = 0.62;
            orbit = Math.Max(keepOut + 36, (dot + gap) / step);
            sweep = (n - 1) * step;
            start = aimAngle - sweep / 2;
        }

        EnsureHostSize(host, cx, cy, orbit + dot + 36, canvasW, canvasH);

        var guide = new System.Windows.Shapes.Path
        {
            Data = ArcStroke(cx, cy, orbit, start, start + Math.Max(sweep, 0.01)),
            Stroke = Accent(0x55),
            StrokeThickness = 1.5,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false
        };
        host.Children.Add(guide);
        parts.Add(guide);

        var titleEl = TitleBadge(title);
        PlaceAt(host, titleEl,
            cx + Math.Cos(aimAngle) * (orbit + dot * 0.5 + 18),
            cy + Math.Sin(aimAngle) * (orbit + dot * 0.5 + 18),
            center: true);
        parts.Add(titleEl);

        for (int i = 0; i < n; i++)
        {
            double a = n == 1 ? aimAngle : start + i * (sweep / Math.Max(n - 1, 1));
            var item = items[i];
            item.Width = dot;
            item.Height = dot;
            item.ClipToBounds = true;
            PlaceAt(host, item, cx + Math.Cos(a) * orbit, cy + Math.Sin(a) * orbit, center: true);
            parts.Add(item);
        }

        var cluster = BoundsOf(parts);
        Play(host, parts, "orbit", cx, cy);
        return new FlyoutMount { Root = host, Cluster = cluster, Parts = parts };
    }

    /// <summary>Лента: колонка читаемых строк с мягким выгибом наружу. Не круг из подписей.</summary>
    static FlyoutMount BuildRibbon(
        double cx, double cy, double keepOut, double aimAngle,
        string title, IReadOnlyList<FrameworkElement> items,
        double canvasW, double canvasH)
    {
        return BuildReadableColumn(
            cx, cy, keepOut, aimAngle, title, items, canvasW, canvasH,
            cellW: 176, cellH: 34, bulge: 22, rays: false, motion: "unfurl", maxCols: 1);
    }

    /// <summary>Корона: одна или две короткие дуги значков у кнопки. Имена на наведении.</summary>
    static FlyoutMount BuildCrown(
        double cx, double cy, double keepOut, double aimAngle,
        string title, IReadOnlyList<FrameworkElement> items,
        double canvasW, double canvasH)
    {
        var host = MakeHost();
        var parts = new List<FrameworkElement>();
        int n = Math.Max(items.Count, 1);
        const double dot = 44;
        int per = n <= 6 ? n : (int)Math.Ceiling(n / 2.0);
        double orbit = Math.Max(keepOut + 34, (dot + 16) / 0.5);
        EnsureHostSize(host, cx, cy, orbit + dot + 70, canvasW, canvasH);

        var titleEl = TitleBadge(title);
        PlaceAt(host, titleEl,
            cx + Math.Cos(aimAngle) * (orbit + (n > per ? dot + 56 : 36)),
            cy + Math.Sin(aimAngle) * (orbit + (n > per ? dot + 56 : 36)),
            center: true);
        parts.Add(titleEl);

        void Row(int from, int count, double r, bool stagger)
        {
            if (count <= 0)
                return;
            double step = 0.58;
            double span = (count - 1) * step;
            double a0 = aimAngle - span / 2 + (stagger ? step * 0.5 : 0);
            var guide = new System.Windows.Shapes.Path
            {
                Data = ArcStroke(cx, cy, r, a0 - 0.08, a0 + Math.Max(span, 0.01) + 0.08),
                Stroke = Accent(0x66),
                StrokeThickness = 1.4,
                IsHitTestVisible = false
            };
            host.Children.Add(guide);
            parts.Add(guide);
            for (int i = 0; i < count; i++)
            {
                double a = count == 1 ? aimAngle : a0 + i * step;
                var item = items[from + i];
                item.Width = dot;
                item.Height = dot;
                item.ClipToBounds = true;
                PlaceAt(host, item, cx + Math.Cos(a) * r, cy + Math.Sin(a) * r, center: true);
                parts.Add(item);
            }
        }

        Row(0, Math.Min(per, n), orbit, stagger: false);
        if (n > per)
            Row(per, n - per, orbit + dot + 22, stagger: true);

        var cluster = BoundsOf(parts);
        Play(host, parts, "orbit", cx, cy);
        return new FlyoutMount { Root = host, Cluster = cluster, Parts = parts };
    }

    /// <summary>Всплеск: та же ровная колонка, от кнопки к каждой строке тонкий луч.</summary>
    static FlyoutMount BuildBurst(
        double cx, double cy, double keepOut, double aimAngle,
        string title, IReadOnlyList<FrameworkElement> items,
        double canvasW, double canvasH)
    {
        return BuildReadableColumn(
            cx, cy, keepOut, aimAngle, title, items, canvasW, canvasH,
            cellW: 156, cellH: 34, bulge: 0, rays: true, motion: "cascade", maxCols: 2);
    }

    /// <summary>Веер: ровная колонка у кнопки. Строки не разъезжаются и не наезжают.</summary>
    static FlyoutMount BuildFan(
        double cx, double cy, double keepOut, double aimAngle,
        string title, IReadOnlyList<FrameworkElement> items,
        double canvasW, double canvasH)
    {
        return BuildReadableColumn(
            cx, cy, keepOut, aimAngle, title, items, canvasW, canvasH,
            cellW: 168, cellH: 36, bulge: 0, rays: false, motion: "cascade", maxCols: 2);
    }

    /// <summary>
    /// Колонка снаружи диска, со стороны кнопки. Две колонки, если пунктов больше восьми.
    /// Каждая строка на своей полке: подписи не пересекаются.
    /// </summary>
    static FlyoutMount BuildReadableColumn(
        double cx, double cy, double keepOut, double aimAngle,
        string title, IReadOnlyList<FrameworkElement> items,
        double canvasW, double canvasH,
        double cellW, double cellH, double bulge, bool rays, string motion, int maxCols)
    {
        var host = MakeHost();
        var parts = new List<FrameworkElement>();
        int n = Math.Max(items.Count, 1);
        const double gap = 5;
        int cols = Math.Max(1, Math.Min(maxCols, n > 8 ? 2 : 1));
        int rows = (int)Math.Ceiling(n / (double)cols);
        double blockW = cols * cellW + (cols - 1) * gap + bulge;
        double blockH = rows * cellH + Math.Max(0, rows - 1) * gap;

        double cos = Math.Cos(aimAngle);
        bool right = Math.Abs(cos) < 0.4 ? canvasW - cx >= cx : cos > 0;
        double left = right ? cx + keepOut + 14 : cx - keepOut - 14 - blockW;
        double buttonY = cy + Math.Sin(aimAngle) * keepOut;
        double top = buttonY - cellH;
        const double titleH = 28;
        top = Math.Clamp(top, titleH + 6, Math.Max(titleH + 6, canvasH - blockH - 12));
        left = Math.Clamp(left, 8, Math.Max(8, canvasW - blockW - 12));

        host.Width = Math.Max(canvasW, left + blockW + 20);
        host.Height = Math.Max(canvasH, top + blockH + 20);

        var titleEl = TitleBadge(title);
        PlaceAt(host, titleEl, left, top - titleH, center: false);
        parts.Add(titleEl);

        for (int i = 0; i < n; i++)
        {
            int col = i % cols;
            int row = i / cols;
            double wave = Math.Sin(Math.PI * (row + 0.5) / rows) * bulge;
            double x = right
                ? left + col * (cellW + gap) + wave
                : left + bulge + col * (cellW + gap) - wave;
            double y = top + row * (cellH + gap);
            var item = items[i];
            item.Width = cellW;
            item.Height = cellH;
            item.RenderTransform = null;
            item.ClipToBounds = true;

            if (rays)
            {
                // Горизонтальный луч снаружи диска: не пересекает кнопки кольца.
                double x2 = right ? x : x + cellW;
                var ray = new Line
                {
                    X1 = right ? cx + keepOut + 2 : cx - keepOut - 2,
                    Y1 = y + cellH * 0.5,
                    X2 = x2,
                    Y2 = y + cellH * 0.5,
                    Stroke = Accent(0x55),
                    StrokeThickness = 1.1,
                    IsHitTestVisible = false
                };
                host.Children.Add(ray);
                parts.Add(ray);
            }

            PlaceAt(host, item, x, y, center: false);
            parts.Add(item);
        }

        var cluster = new Rect(left - 4, top - titleH, blockW + bulge + 8, blockH + titleH + 4);
        Play(host, parts, motion, cx, cy);
        return new FlyoutMount { Root = host, Cluster = cluster, Parts = parts };
    }

    static FlyoutMount BuildDock(
        double cx, double cy, double keepOut, string title,
        IReadOnlyList<FrameworkElement> items,
        double canvasW, double canvasH, double bandW)
    {
        var stack = new StackPanel();
        foreach (var item in items)
        {
            item.Margin = new Thickness(0, 0, 0, 3);
            item.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            item.RenderTransform = null;
            stack.Children.Add(item);
        }

        stack.Measure(new System.Windows.Size(bandW - 20, 2000));
        double contentH = Math.Max(stack.DesiredSize.Height, 80);
        return BuildDockContent(cx, cy, keepOut, title, stack, bandW - 20, contentH, canvasW, canvasH);
    }

    /// <summary>Стык: вплотную к кромке, без длинного стебля.</summary>
    static FlyoutMount BuildDockContent(
        double cx, double cy, double keepOut, string? title,
        FrameworkElement content, double contentW, double contentH,
        double canvasW, double canvasH)
    {
        const double gap = 4;
        const double pad = 10;
        double panelW = Math.Max(contentW + pad * 2, 240);
        double panelH = Math.Clamp(contentH + pad * 2 + (string.IsNullOrWhiteSpace(title) ? 0 : 26), 100, Math.Max(160, canvasH - 24));

        double left = cx + keepOut + gap;
        double top = Math.Clamp(cy - panelH / 2, 10, Math.Max(10, canvasH - panelH - 10));
        double needRight = left + panelW + 12;

        var body = new StackPanel();
        if (!string.IsNullOrWhiteSpace(title))
            body.Children.Add(TitleBadge(title!));
        body.Children.Add(content);

        var shell = new Border
        {
            Width = panelW,
            Height = panelH,
            Padding = new Thickness(pad),
            Background = PanelFill(0xF4),
            BorderBrush = Accent(0x77),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = body,
            SnapsToDevicePixels = true
        };

        // Вогнутый край под диск.
        double discR = keepOut + 2;
        shell.Clip = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, panelW, panelH), 14, 14),
            new EllipseGeometry(new Point(cx - left, cy - top), discR, discR));

        var host = MakeHost();
        host.Width = Math.Max(canvasW, needRight);
        host.Height = Math.Max(canvasH, top + panelH + 12);
        Canvas.SetLeft(shell, Math.Round(left));
        Canvas.SetTop(shell, Math.Round(top));
        host.Children.Add(shell);

        var parts = new List<FrameworkElement> { shell };
        var cluster = new Rect(left, top, panelW, panelH);
        Play(host, parts, "dockslide", cx, cy);
        return new FlyoutMount { Root = host, Cluster = cluster, Parts = parts };
    }

    /// <summary>Прямоугольник живых плиток. Дуги и лучи не раздувают зону до всего круга.</summary>
    static Rect BoundsOf(IEnumerable<FrameworkElement> parts)
    {
        Rect box = Rect.Empty;
        foreach (var el in parts)
        {
            if (el is Line or System.Windows.Shapes.Path)
                continue;
            double x = Canvas.GetLeft(el);
            double y = Canvas.GetTop(el);
            double w = el.Width > 1 ? el.Width : 40;
            double h = el.Height > 1 ? el.Height : 24;
            if (double.IsNaN(x) || double.IsNaN(y) || w < 8 || h < 8)
                continue;
            var piece = new Rect(x, y, w, h);
            box = box.IsEmpty ? piece : Rect.Union(box, piece);
        }

        return box;
    }

    /// <summary>Проверка всех форм: плитки не пересекаются и не заходят в диск.</summary>
    public static string Audit()
    {
        var text = new System.Text.StringBuilder();
        const double cx = 360;
        const double cy = 320;
        const double keep = 150;
        int[] counts = [1, 4, 12];
        foreach (var style in FlyoutLooks.StyleCatalog)
        {
            foreach (int n in counts)
            {
                var items = new List<FrameworkElement>();
                for (int i = 0; i < n; i++)
                {
                    items.Add(new Border
                    {
                        Width = style.Id is "halo" or "crown" ? 46 : 120,
                        Height = style.Id is "halo" or "crown" ? 46 : 34,
                        Background = Brushes.DimGray
                    });
                }

                var mount = Build(style.Id, cx, cy, keep, -Math.PI / 2, style.Title, items, 980, 720);
                var boxes = new List<Rect>();
                foreach (var part in mount.Parts)
                {
                    if (part is Line or System.Windows.Shapes.Path)
                        continue;
                    double x = Canvas.GetLeft(part);
                    double y = Canvas.GetTop(part);
                    if (double.IsNaN(x) || double.IsNaN(y) || part.Width < 20 || part.Height < 16 || double.IsNaN(part.Height))
                        continue;
                    boxes.Add(new Rect(x, y, part.Width, part.Height));
                }

                for (int a = 0; a < boxes.Count; a++)
                {
                    var ra = boxes[a];
                    if (ra.Width < 8 || ra.Height < 8)
                        continue;
                    ra.Inflate(-3, -3);
                    if (ra.Width < 1 || ra.Height < 1)
                        continue;
                    if (DiscHits(cx, cy, keep - 4, ra))
                        text.AppendLine($"{style.Id} n={n}: плитка {a} зашла в диск");
                    for (int b = a + 1; b < boxes.Count; b++)
                    {
                        var rb = boxes[b];
                        if (rb.Width < 8 || rb.Height < 8)
                            continue;
                        rb.Inflate(-3, -3);
                        if (rb.Width < 1 || rb.Height < 1)
                            continue;
                        if (ra.IntersectsWith(rb))
                            text.AppendLine($"{style.Id} n={n}: плитки {a} и {b} пересекаются");
                    }
                }

                if (mount.Cluster.IsEmpty)
                    text.AppendLine($"{style.Id} n={n}: пустая зона");
            }
        }

        if (text.Length == 0)
            text.AppendLine("ok");
        return text.ToString();
    }

    static bool DiscHits(double cx, double cy, double r, Rect box)
    {
        double left = Math.Min(box.Left, box.Right);
        double right = Math.Max(box.Left, box.Right);
        double top = Math.Min(box.Top, box.Bottom);
        double bottom = Math.Max(box.Top, box.Bottom);
        double nx = Math.Clamp(cx, left, right);
        double ny = Math.Clamp(cy, top, bottom);
        double dx = cx - nx;
        double dy = cy - ny;
        return dx * dx + dy * dy < r * r;
    }

    static Canvas MakeHost() => new()
    {
        Background = null,
        ClipToBounds = false
    };

    static void EnsureHostSize(Canvas host, double cx, double cy, double radius, double canvasW, double canvasH)
    {
        host.Width = Math.Max(canvasW, cx + radius + 28);
        host.Height = Math.Max(canvasH, Math.Max(cy + radius + 28, radius * 2 + 40));
    }

    static void PlaceAt(Canvas host, FrameworkElement el, double x, double y, bool center)
    {
        el.Measure(new System.Windows.Size(400, 200));
        double w = el.Width > 1 ? el.Width : Math.Max(el.DesiredSize.Width, 40);
        double h = el.Height > 1 ? el.Height : Math.Max(el.DesiredSize.Height, 28);
        if (el.Width <= 0) el.Width = w;
        if (el.Height <= 0) el.Height = h;
        Canvas.SetLeft(el, Math.Round(center ? x - w / 2 : x));
        Canvas.SetTop(el, Math.Round(center ? y - h / 2 : y));
        Panel.SetZIndex(el, 50);
        if (!host.Children.Contains(el))
            host.Children.Add(el);
    }

    static FrameworkElement TitleBadge(string title) => new Border
    {
        Padding = new Thickness(10, 3, 10, 3),
        CornerRadius = new CornerRadius(9),
        Background = PanelFill(0xDD),
        BorderBrush = Accent(0x88),
        BorderThickness = new Thickness(1),
        Child = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(title) ? "Меню" : title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Solid(TemplateTheme.AccentText),
            IsHitTestVisible = false
        },
        IsHitTestVisible = false
    };

    static Geometry ArcStroke(double cx, double cy, double r, double a0, double a1)
    {
        Point P(double a) => new(cx + Math.Cos(a) * r, cy + Math.Sin(a) * r);
        bool large = Math.Abs(a1 - a0) > Math.PI;
        var geo = new StreamGeometry();
        using (var gc = geo.Open())
        {
            gc.BeginFigure(P(a0), isFilled: false, isClosed: false);
            gc.ArcTo(P(a1), new System.Windows.Size(r, r), 0, large, SweepDirection.Clockwise, true, false);
        }

        geo.Freeze();
        return geo;
    }

    static Geometry AnnularSector(double cx, double cy, double inner, double outer, double a0, double a1)
    {
        Point O(double r, double a) => new(cx + Math.Cos(a) * r, cy + Math.Sin(a) * r);
        bool large = Math.Abs(a1 - a0) > Math.PI;
        var geo = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (var gc = geo.Open())
        {
            gc.BeginFigure(O(outer, a0), isFilled: true, isClosed: true);
            gc.ArcTo(O(outer, a1), new System.Windows.Size(outer, outer), 0, large, SweepDirection.Clockwise, true, false);
            gc.LineTo(O(inner, a1), true, false);
            gc.ArcTo(O(inner, a0), new System.Windows.Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true, false);
        }

        geo.Freeze();
        return geo;
    }

    static void Play(FrameworkElement host, List<FrameworkElement> parts, string motionId, double cx, double cy)
    {
        string id = AppearanceStore.Current.FlyoutMotion;
        if (string.IsNullOrWhiteSpace(id) || id is "snap" or "slide" or "rise" or "bloom")
            id = motionId;
        else
            id = FlyoutLooks.NormalizeMotion(id);

        double w = host.Width > 1 ? host.Width : 1;
        double h = host.Height > 1 ? host.Height : 1;
        host.RenderTransformOrigin = new Point(Math.Clamp(cx / w, 0.05, 0.95), Math.Clamp(cy / h, 0.05, 0.95));
        host.RenderTransform = new TransformGroup
        {
            Children = { new ScaleTransform(1, 1), new TranslateTransform(0, 0) }
        };

        if (host.RenderTransform is not TransformGroup g
            || g.Children[0] is not ScaleTransform scale
            || g.Children[1] is not TranslateTransform move)
            return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        double ms = FlyoutLooks.GetMotion(id).Ms;
        host.BeginAnimation(UIElement.OpacityProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        move.BeginAnimation(TranslateTransform.XProperty, null);

        switch (id)
        {
            case "dockslide":
                host.Opacity = 0;
                move.X = -20;
                host.BeginAnimation(UIElement.OpacityProperty, Anim(0, 1, ms * 0.85, ease));
                move.BeginAnimation(TranslateTransform.XProperty, Anim(-20, 0, ms, ease));
                break;
            case "unfurl":
                host.Opacity = 0;
                scale.ScaleX = 0.88;
                scale.ScaleY = 0.88;
                host.BeginAnimation(UIElement.OpacityProperty, Anim(0, 1, ms, ease));
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(0.88, 1, ms, ease));
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(0.88, 1, ms, ease));
                break;
            case "cascade":
                // Много частей: один fade хоста, без очереди (она лагает на процессах).
                if (parts.Count > 12)
                {
                    host.Opacity = 0;
                    host.BeginAnimation(UIElement.OpacityProperty, Anim(0, 1, ms, ease));
                    break;
                }

                host.Opacity = 1;
                int i = 0;
                foreach (var p in parts)
                {
                    p.Opacity = 0;
                    var fade = Anim(0, 1, 110, ease);
                    fade.BeginTime = TimeSpan.FromMilliseconds(i * 14);
                    p.BeginAnimation(UIElement.OpacityProperty, fade);
                    i++;
                }

                break;
            default:
                host.Opacity = 0;
                scale.ScaleX = 0.9;
                scale.ScaleY = 0.9;
                host.BeginAnimation(UIElement.OpacityProperty, Anim(0, 1, ms * 0.8, ease));
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(0.9, 1, ms, ease));
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(0.9, 1, ms, ease));
                break;
        }
    }

    static DoubleAnimation Anim(double from, double to, double ms, IEasingFunction ease) => new()
    {
        From = from,
        To = to,
        Duration = TimeSpan.FromMilliseconds(Math.Max(1, ms)),
        EasingFunction = ease,
        FillBehavior = FillBehavior.HoldEnd
    };

    static Brush PanelFill(byte alpha)
    {
        var c = TemplateTheme.BgPanel;
        return Solid(Color.FromArgb(alpha, c.R, c.G, c.B));
    }

    static Brush Accent(byte alpha) =>
        Solid(Color.FromArgb(alpha, TemplateTheme.AccentText.R, TemplateTheme.AccentText.G, TemplateTheme.AccentText.B));

    static SolidColorBrush Solid(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
