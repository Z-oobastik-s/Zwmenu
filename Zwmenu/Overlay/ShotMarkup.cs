using System.Drawing.Drawing2D;
using System.Windows.Media;
using DColor = System.Drawing.Color;
using DFont = System.Drawing.Font;
using DPen = System.Drawing.Pen;
using DRect = System.Drawing.Rectangle;
using Graphics = System.Drawing.Graphics;
using MediaColor = System.Windows.Media.Color;
using WpfBrush = System.Windows.Media.SolidColorBrush;
using WpfPen = System.Windows.Media.Pen;
using WpfPoint = System.Windows.Point;

namespace Zwmenu.Overlay;

public enum ShotTool
{
    Select,
    Pen,
    Marker,
    Line,
    Arrow,
    ArrowOpen,
    ArrowBoth,
    Rect,
    Ellipse,
    Text,
    Eraser,
    Pick,
    Read
}

/// <summary>Пометки на снимке: на экране и в сохранённом кадре одни и те же.</summary>
public sealed class ShotMarkup
{
    private readonly List<Mark> _marks = new();
    private readonly List<List<Mark>> _undo = new();
    private readonly List<List<Mark>> _redo = new();
    private Mark? _draft;
    private bool _undoArmed;
    private bool _changed;
    private bool _erasing;

    public MediaColor Color { get; set; } = MediaColor.FromRgb(0xE2, 0x3D, 0x3D);

    public double Thickness { get; set; } = 5;

    public double EraserRadius => Math.Max(12, Thickness * 2.4);

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public void Begin(ShotTool tool, WpfPoint point)
    {
        ArmUndo();
        _draft = null;
        _erasing = tool == ShotTool.Eraser;
        if (_erasing)
        {
            Erase(point);
            return;
        }

        _draft = new Mark
        {
            Kind = tool,
            Color = Color,
            Thickness = Thickness,
            A = point,
            B = point
        };
        if (tool is ShotTool.Pen or ShotTool.Marker)
            _draft.Points.Add(point);
    }

    public void Continue(WpfPoint point)
    {
        if (_erasing)
        {
            Erase(point);
            return;
        }

        if (_draft == null)
            return;

        if (_draft.Kind is ShotTool.Pen or ShotTool.Marker)
        {
            var last = _draft.Points[^1];
            if (Dist(last, point) < 1.2)
                return;
            _draft.Points.Add(point);
            return;
        }

        _draft.B = point;
    }

    public void End()
    {
        if (_draft != null)
        {
            if (_draft.Kind is ShotTool.Pen or ShotTool.Marker)
            {
                if (_draft.Points.Count > 0)
                {
                    _marks.Add(_draft);
                    _changed = true;
                }
            }
            else if (Dist(_draft.A, _draft.B) >= 2)
            {
                _marks.Add(_draft);
                _changed = true;
            }
        }

        _draft = null;
        _erasing = false;
        if (_undoArmed && !_changed && _undo.Count > 0)
            _undo.RemoveAt(_undo.Count - 1);
        else if (_undoArmed && _changed)
            _redo.Clear();
        _undoArmed = false;
    }

    public void AddText(WpfPoint origin, string text, double fontSize, double maxWidth)
    {
        text = text.Trim();
        if (text.Length == 0)
            return;

        ArmUndo();
        _marks.Add(new Mark
        {
            Kind = ShotTool.Text,
            Color = Color,
            Thickness = Thickness,
            A = origin,
            Text = text,
            FontSize = fontSize,
            MaxWidth = maxWidth
        });
        _changed = true;
        _undoArmed = false;
        _redo.Clear();
    }

    public bool Undo()
    {
        if (_undo.Count == 0)
            return false;

        Push(_redo);
        Restore(_undo);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0)
            return false;

        Push(_undo);
        Restore(_redo);
        return true;
    }

    public void Draw(DrawingContext dc, double pixelsPerDip)
    {
        _pixelsPerDip = pixelsPerDip <= 0 ? 1 : pixelsPerDip;
        foreach (var mark in _marks)
            DrawMark(dc, mark);
        if (_draft != null)
            DrawMark(dc, _draft);
    }

    public void Paint(Graphics graphics, DRect crop, double dipWidth, double dipHeight, double pixelWidth, double pixelHeight)
    {
        if (dipWidth < 1 || dipHeight < 1)
            return;

        double sx = pixelWidth / dipWidth;
        double sy = pixelHeight / dipHeight;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        foreach (var mark in _marks)
            PaintMark(graphics, mark, sx, sy, crop);
    }

    private void Erase(WpfPoint point)
    {
        double radius = Math.Max(12, Thickness * 2.4);
        var next = new List<Mark>(_marks.Count);
        foreach (var mark in _marks)
        {
            if (mark.Kind is ShotTool.Pen or ShotTool.Marker)
            {
                var parts = SplitStroke(mark, point, radius);
                if (parts.Count != 1 || parts.Count == 1 && parts[0] != mark)
                    _changed = true;
                next.AddRange(parts);
            }
            else if (Hits(mark, point, radius))
            {
                _changed = true;
            }
            else
            {
                next.Add(mark);
            }
        }

        _marks.Clear();
        _marks.AddRange(next);
    }

    private static List<Mark> SplitStroke(Mark mark, WpfPoint point, double radius)
    {
        var result = new List<Mark>();
        var run = new List<WpfPoint>();
        bool dropped = false;
        foreach (var pt in mark.Points)
        {
            if (Dist(pt, point) <= radius)
            {
                dropped = true;
                Flush(mark, run, result);
                run = new List<WpfPoint>();
            }
            else
            {
                run.Add(pt);
            }
        }

        Flush(mark, run, result);
        if (!dropped)
            return new List<Mark> { mark };
        return result;
    }

    private static void Flush(Mark source, List<WpfPoint> run, List<Mark> result)
    {
        if (run.Count == 0)
            return;

        result.Add(new Mark
        {
            Kind = source.Kind,
            Color = source.Color,
            Thickness = source.Thickness,
            Points = run
        });
    }

    private static bool Hits(Mark mark, WpfPoint point, double radius)
    {
        switch (mark.Kind)
        {
            case ShotTool.Line:
            case ShotTool.Arrow:
            case ShotTool.ArrowOpen:
            case ShotTool.ArrowBoth:
                return DistToSegment(point, mark.A, mark.B) <= radius;
            case ShotTool.Rect:
                return NearRectStroke(point, mark.A, mark.B, radius);
            case ShotTool.Ellipse:
                return NearEllipseStroke(point, mark.A, mark.B, radius);
            case ShotTool.Text:
                double width = mark.MaxWidth > 12
                    ? mark.MaxWidth
                    : Math.Max(12, mark.Text.Length * mark.FontSize * 0.55);
                int lines = 0;
                foreach (var line in (mark.Text.Length == 0 ? " " : mark.Text).Split('\n'))
                {
                    double lineWidth = Math.Max(mark.FontSize, line.Length * mark.FontSize * 0.55);
                    lines += mark.MaxWidth > 12
                        ? Math.Max(1, (int)Math.Ceiling(lineWidth / mark.MaxWidth))
                        : 1;
                }
                double height = Math.Max(1, lines) * mark.FontSize * 1.35;
                var box = new System.Windows.Rect(mark.A.X - 4, mark.A.Y - 2, width + 8, height + 4);
                box.Inflate(radius * 0.35, radius * 0.35);
                return box.Contains(point);
            default:
                return false;
        }
    }

    private void DrawMark(DrawingContext dc, Mark mark)
    {
        var color = mark.Kind == ShotTool.Marker ? Fade(mark.Color, 110) : mark.Color;
        double width = mark.Kind == ShotTool.Marker ? mark.Thickness * 3.1 : mark.Thickness;
        var brush = new WpfBrush(color);
        brush.Freeze();
        var pen = new WpfPen(brush, width)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();

        switch (mark.Kind)
        {
            case ShotTool.Pen:
            case ShotTool.Marker:
                DrawStroke(dc, mark.Points, pen, width);
                break;
            case ShotTool.Line:
                dc.DrawLine(pen, mark.A, mark.B);
                break;
            case ShotTool.Arrow:
            case ShotTool.ArrowOpen:
            case ShotTool.ArrowBoth:
                DrawArrow(dc, mark.A, mark.B, brush, pen, mark.Thickness, ArrowStyle(mark.Kind));
                break;
            case ShotTool.Rect:
                dc.DrawRectangle(null, pen, Norm(mark.A, mark.B));
                break;
            case ShotTool.Ellipse:
                var box = Norm(mark.A, mark.B);
                dc.DrawEllipse(null, pen, new WpfPoint(box.X + box.Width / 2, box.Y + box.Height / 2), box.Width / 2, box.Height / 2);
                break;
            case ShotTool.Text:
                DrawText(dc, mark, _pixelsPerDip);
                break;
        }
    }

    private static void DrawStroke(DrawingContext dc, List<WpfPoint> points, WpfPen pen, double width)
    {
        if (points.Count == 0)
            return;
        if (points.Count == 1)
        {
            dc.DrawEllipse(pen.Brush, null, points[0], width / 2, width / 2);
            return;
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            ctx.PolyLineTo(points.Skip(1).ToArray(), true, true);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static void DrawArrow(DrawingContext dc, WpfPoint a, WpfPoint b, WpfBrush brush, WpfPen pen, double thickness, int style)
    {
        if (!ArrowHead(a, b, thickness, out var shaft, out var tip, out var left, out var right))
        {
            dc.DrawEllipse(brush, null, a, thickness, thickness);
            return;
        }

        if (style == 1)
        {
            dc.DrawLine(pen, a, tip);
            dc.DrawLine(pen, tip, left);
            dc.DrawLine(pen, tip, right);
            return;
        }

        if (style == 2 && ArrowHead(b, a, thickness, out var shaft2, out var tip2, out var left2, out var right2))
        {
            dc.DrawLine(pen, shaft, shaft2);
            FillHead(dc, brush, tip, left, right);
            FillHead(dc, brush, tip2, left2, right2);
            return;
        }

        dc.DrawLine(pen, a, shaft);
        FillHead(dc, brush, tip, left, right);
    }

    private static void FillHead(DrawingContext dc, WpfBrush brush, WpfPoint tip, WpfPoint left, WpfPoint right)
    {
        var head = new StreamGeometry();
        using (var ctx = head.Open())
        {
            ctx.BeginFigure(tip, true, true);
            ctx.LineTo(left, true, false);
            ctx.LineTo(right, true, false);
        }

        head.Freeze();
        dc.DrawGeometry(brush, null, head);
    }

    private static void DrawText(DrawingContext dc, Mark mark, double pixelsPerDip)
    {
        var brush = new WpfBrush(mark.Color);
        brush.Freeze();
        var text = new FormattedText(
            mark.Text,
            System.Globalization.CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            mark.FontSize,
            brush,
            pixelsPerDip <= 0 ? 1 : pixelsPerDip);
        if (mark.MaxWidth > 12)
            text.MaxTextWidth = mark.MaxWidth;
        dc.DrawText(text, mark.A);
    }

    private double _pixelsPerDip = 1;

    private void PaintMark(Graphics graphics, Mark mark, double sx, double sy, DRect crop)
    {
        var color = mark.Kind == ShotTool.Marker ? Fade(mark.Color, 110) : mark.Color;
        float width = (float)((mark.Kind == ShotTool.Marker ? mark.Thickness * 3.1 : mark.Thickness) * ((sx + sy) / 2));
        using var pen = new DPen(ToGdi(color), Math.Max(1f, width))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var brush = new System.Drawing.SolidBrush(ToGdi(color));

        switch (mark.Kind)
        {
            case ShotTool.Pen:
            case ShotTool.Marker:
                PaintStroke(graphics, mark.Points, pen, brush, width, sx, sy, crop);
                break;
            case ShotTool.Line:
                graphics.DrawLine(pen, Map(mark.A, sx, sy, crop), Map(mark.B, sx, sy, crop));
                break;
            case ShotTool.Arrow:
            case ShotTool.ArrowOpen:
            case ShotTool.ArrowBoth:
                PaintArrow(graphics, mark, pen, brush, sx, sy, crop, ArrowStyle(mark.Kind));
                break;
            case ShotTool.Rect:
                var box = Norm(mark.A, mark.B);
                graphics.DrawRectangle(pen, MapRect(box, sx, sy, crop));
                break;
            case ShotTool.Ellipse:
                var oval = Norm(mark.A, mark.B);
                graphics.DrawEllipse(pen, MapRect(oval, sx, sy, crop));
                break;
            case ShotTool.Text:
                using (var font = new DFont("Segoe UI", Math.Max(8f, (float)(mark.FontSize * sy)), System.Drawing.GraphicsUnit.Pixel))
                {
                    var origin = Map(mark.A, sx, sy, crop);
                    if (mark.MaxWidth > 12)
                    {
                        graphics.DrawString(
                            mark.Text,
                            font,
                            brush,
                            new System.Drawing.RectangleF(origin.X, origin.Y, (float)(mark.MaxWidth * sx), 4000));
                    }
                    else
                    {
                        graphics.DrawString(mark.Text, font, brush, origin);
                    }
                }
                break;
        }
    }

    private static void PaintStroke(
        Graphics graphics,
        List<WpfPoint> points,
        DPen pen,
        System.Drawing.SolidBrush brush,
        float width,
        double sx,
        double sy,
        DRect crop)
    {
        if (points.Count == 0)
            return;
        if (points.Count == 1)
        {
            var c = Map(points[0], sx, sy, crop);
            graphics.FillEllipse(brush, c.X - width / 2, c.Y - width / 2, width, width);
            return;
        }

        var mapped = new System.Drawing.PointF[points.Count];
        for (int i = 0; i < points.Count; i++)
            mapped[i] = Map(points[i], sx, sy, crop);
        graphics.DrawLines(pen, mapped);
    }

    private static void PaintArrow(Graphics graphics, Mark mark, DPen pen, System.Drawing.SolidBrush brush, double sx, double sy, DRect crop, int style)
    {
        if (!ArrowHead(mark.A, mark.B, mark.Thickness, out var shaft, out var tip, out var left, out var right))
        {
            var c = Map(mark.A, sx, sy, crop);
            float d = Math.Max(2f, (float)(mark.Thickness * sx));
            graphics.FillEllipse(brush, c.X - d, c.Y - d, d * 2, d * 2);
            return;
        }

        if (style == 1)
        {
            graphics.DrawLine(pen, Map(mark.A, sx, sy, crop), Map(tip, sx, sy, crop));
            graphics.DrawLine(pen, Map(tip, sx, sy, crop), Map(left, sx, sy, crop));
            graphics.DrawLine(pen, Map(tip, sx, sy, crop), Map(right, sx, sy, crop));
            return;
        }

        if (style == 2 && ArrowHead(mark.B, mark.A, mark.Thickness, out var shaft2, out var tip2, out var left2, out var right2))
        {
            graphics.DrawLine(pen, Map(shaft, sx, sy, crop), Map(shaft2, sx, sy, crop));
            graphics.FillPolygon(brush, new[] { Map(tip, sx, sy, crop), Map(left, sx, sy, crop), Map(right, sx, sy, crop) });
            graphics.FillPolygon(brush, new[] { Map(tip2, sx, sy, crop), Map(left2, sx, sy, crop), Map(right2, sx, sy, crop) });
            return;
        }

        graphics.DrawLine(pen, Map(mark.A, sx, sy, crop), Map(shaft, sx, sy, crop));
        graphics.FillPolygon(brush, new[]
        {
            Map(tip, sx, sy, crop),
            Map(left, sx, sy, crop),
            Map(right, sx, sy, crop)
        });
    }

    private static int ArrowStyle(ShotTool kind) => kind switch
    {
        ShotTool.ArrowOpen => 1,
        ShotTool.ArrowBoth => 2,
        _ => 0
    };

    private void ArmUndo()
    {
        Push(_undo);
        _undoArmed = true;
        _changed = false;
    }

    private void Push(List<List<Mark>> stack)
    {
        stack.Add(_marks.Select(Clone).ToList());
        if (stack.Count > 40)
            stack.RemoveAt(0);
    }

    private void Restore(List<List<Mark>> stack)
    {
        var snapshot = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        _marks.Clear();
        _marks.AddRange(snapshot);
        _draft = null;
        _erasing = false;
        _undoArmed = false;
    }

    private static Mark Clone(Mark mark) => new()
    {
        Kind = mark.Kind,
        Color = mark.Color,
        Thickness = mark.Thickness,
        A = mark.A,
        B = mark.B,
        Text = mark.Text,
        FontSize = mark.FontSize,
        MaxWidth = mark.MaxWidth,
        Points = new List<WpfPoint>(mark.Points)
    };

    private static MediaColor Fade(MediaColor color, byte alpha) =>
        MediaColor.FromArgb(alpha, color.R, color.G, color.B);

    private static DColor ToGdi(MediaColor color) =>
        DColor.FromArgb(color.A, color.R, color.G, color.B);

    private static System.Drawing.PointF Map(WpfPoint point, double sx, double sy, DRect crop) =>
        new((float)(point.X * sx - crop.X), (float)(point.Y * sy - crop.Y));

    private static System.Drawing.RectangleF MapRect(System.Windows.Rect rect, double sx, double sy, DRect crop)
    {
        float x = (float)(rect.X * sx - crop.X);
        float y = (float)(rect.Y * sy - crop.Y);
        return new System.Drawing.RectangleF(x, y, (float)(rect.Width * sx), (float)(rect.Height * sy));
    }

    private static System.Windows.Rect Norm(WpfPoint a, WpfPoint b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private static bool ArrowHead(
        WpfPoint a,
        WpfPoint b,
        double thickness,
        out WpfPoint shaft,
        out WpfPoint tip,
        out WpfPoint left,
        out WpfPoint right)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        shaft = a;
        tip = b;
        left = b;
        right = b;
        if (len < 2)
            return false;

        dx /= len;
        dy /= len;
        double head = Math.Clamp(thickness * 3.4 + 10, 12, 48);
        if (head > len * 0.72)
            head = Math.Max(8, len * 0.72);
        tip = b;
        shaft = new WpfPoint(b.X - dx * head, b.Y - dy * head);
        double wing = head * 0.48;
        left = new WpfPoint(shaft.X + -dy * wing, shaft.Y + dx * wing);
        right = new WpfPoint(shaft.X - -dy * wing, shaft.Y - dx * wing);
        return true;
    }

    private static double Dist(WpfPoint a, WpfPoint b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double DistToSegment(WpfPoint p, WpfPoint a, WpfPoint b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double len2 = dx * dx + dy * dy;
        if (len2 < 0.01)
            return Dist(p, a);
        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        return Dist(p, new WpfPoint(a.X + t * dx, a.Y + t * dy));
    }

    private static bool NearRectStroke(WpfPoint p, WpfPoint a, WpfPoint b, double radius)
    {
        var box = Norm(a, b);
        if (box.Width < 1 && box.Height < 1)
            return Dist(p, a) <= radius;
        var tl = new WpfPoint(box.Left, box.Top);
        var tr = new WpfPoint(box.Right, box.Top);
        var br = new WpfPoint(box.Right, box.Bottom);
        var bl = new WpfPoint(box.Left, box.Bottom);
        return DistToSegment(p, tl, tr) <= radius
            || DistToSegment(p, tr, br) <= radius
            || DistToSegment(p, br, bl) <= radius
            || DistToSegment(p, bl, tl) <= radius;
    }

    private static bool NearEllipseStroke(WpfPoint p, WpfPoint a, WpfPoint b, double radius)
    {
        var box = Norm(a, b);
        double rx = Math.Max(1, box.Width / 2);
        double ry = Math.Max(1, box.Height / 2);
        double cx = box.X + rx;
        double cy = box.Y + ry;
        double nx = (p.X - cx) / rx;
        double ny = (p.Y - cy) / ry;
        double d = Math.Sqrt(nx * nx + ny * ny);
        return Math.Abs(d - 1) * Math.Min(rx, ry) <= radius;
    }

    private sealed class Mark
    {
        public ShotTool Kind;
        public MediaColor Color;
        public double Thickness;
        public WpfPoint A;
        public WpfPoint B;
        public string Text = "";
        public double FontSize = 22;
        public double MaxWidth;
        public List<WpfPoint> Points = new();
    }
}
