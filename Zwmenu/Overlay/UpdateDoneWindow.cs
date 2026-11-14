using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Zwmenu.Services;

namespace Zwmenu.Overlay;

/// <summary>Короткое окно после тихого обновления. Без рамки, живёт несколько секунд.</summary>
public sealed class UpdateDoneWindow : Window
{
    readonly System.Windows.Controls.Border _life;
    readonly DispatcherTimer _timer;
    double _gone;
    const double StaySeconds = 6.4;

    public UpdateDoneWindow()
    {
        Width = 520;
        Height = 280;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        Cursor = Cursors.Arrow;
        Background = TemplateTheme.Brush(TemplateTheme.BgDeep);

        var canvas = new Canvas { IsHitTestVisible = false, Width = 520, Height = 280 };
        Add(canvas, Block(168, 168, 0, 0, Color.FromRgb(0x14, 0x24, 0x20)));
        Add(canvas, Block(86, 86, 404, 0, Color.FromRgb(0x18, 0x18, 0x1C)));
        Add(canvas, Block(14, 168, 0, 112, TemplateTheme.AccentText));
        Add(canvas, Outline(120, 120, 372, 132));
        Add(canvas, Block(46, 8, 36, 214, Color.FromRgb(0x3A, 0x3A, 0x40)));
        Add(canvas, Block(22, 8, 36, 230, TemplateTheme.AccentText));

        var title = new TextBlock
        {
            Text = "Обновлено",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 34,
            FontWeight = FontWeights.SemiBold,
            Foreground = TemplateTheme.Brush(TemplateTheme.Fg),
            Margin = new Thickness(0, 0, 0, 10)
        };
        var line = new TextBlock
        {
            Text = "Программа успешно обновлена.",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 16,
            Foreground = TemplateTheme.Brush(Color.FromRgb(0xC8, 0xC8, 0xC8)),
            Margin = new Thickness(0, 0, 0, 8)
        };
        var version = new TextBlock
        {
            Text = "Сейчас стоит версия " + AppUpdate.Current,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            Foreground = TemplateTheme.Brush(TemplateTheme.AccentText)
        };
        var words = new StackPanel
        {
            Margin = new Thickness(48, 0, 36, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        words.Children.Add(title);
        words.Children.Add(line);
        words.Children.Add(version);

        _life = new System.Windows.Controls.Border
        {
            Height = 3,
            Width = 0,
            Background = TemplateTheme.Brush(TemplateTheme.AccentText),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom
        };

        var root = new Grid();
        root.Children.Add(canvas);
        root.Children.Add(words);
        root.Children.Add(_life);
        Content = root;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(32) };
        _timer.Tick += (_, _) => Advance();
        MouseLeftButtonUp += (_, _) => Close();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape)
                return;
            e.Handled = true;
            Close();
        };
        Closed += (_, _) => _timer.Stop();
        ContentRendered += (_, _) =>
        {
            Place();
            _timer.Start();
        };
    }

    public static void ShowNow()
    {
        var window = new UpdateDoneWindow();
        window.Show();
    }

    void Advance()
    {
        _gone += _timer.Interval.TotalSeconds;
        var portion = Math.Min(1, _gone / StaySeconds);
        _life.Width = Math.Max(0, ActualWidth) * portion;
        if (_gone >= StaySeconds)
            Close();
    }

    void Place()
    {
        var area = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
            ?? System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        Left = area.Left + Math.Max(0, (area.Width - ActualWidth) / 2);
        Top = area.Top + Math.Max(0, (area.Height - ActualHeight) / 2);
    }

    static void Add(Canvas canvas, FrameworkElement shape)
    {
        canvas.Children.Add(shape);
    }

    static System.Windows.Shapes.Rectangle Block(double width, double height, double x, double y, Color color)
    {
        var shape = new System.Windows.Shapes.Rectangle
        {
            Width = width,
            Height = height,
            Fill = TemplateTheme.Brush(color)
        };
        Canvas.SetLeft(shape, x);
        Canvas.SetTop(shape, y);
        return shape;
    }

    static System.Windows.Shapes.Rectangle Outline(double width, double height, double x, double y)
    {
        var shape = new System.Windows.Shapes.Rectangle
        {
            Width = width,
            Height = height,
            Fill = Brushes.Transparent,
            Stroke = TemplateTheme.Brush(Color.FromRgb(0x3A, 0x3A, 0x40)),
            StrokeThickness = 1
        };
        Canvas.SetLeft(shape, x);
        Canvas.SetTop(shape, y);
        return shape;
    }
}
