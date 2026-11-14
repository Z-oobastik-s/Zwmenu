using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Zwmenu.Overlay;

/// <summary>Рамка обрезки мышью. Возвращает прямоугольник в пикселях картинки.</summary>
public sealed class CropWindow : Window
{
    readonly BitmapSource _bmp;
    readonly double _scale;
    readonly System.Windows.Shapes.Rectangle _rubber;
    bool _drag;
    Point _down;
    public int CropX { get; private set; }
    public int CropY { get; private set; }
    public int CropW { get; private set; }
    public int CropH { get; private set; }

    CropWindow(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        _bmp = bmp;
        double maxW = 760;
        double maxH = 460;
        _scale = Math.Min(maxW / bmp.PixelWidth, maxH / bmp.PixelHeight);
        if (_scale > 1)
            _scale = 1;
        double viewW = Math.Max(1, bmp.PixelWidth * _scale);
        double viewH = Math.Max(1, bmp.PixelHeight * _scale);

        Title = "Обрезка";
        Width = viewW + 40;
        Height = viewH + 120;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        TemplateTheme.StyleWindow(this);

        var canvas = new Canvas { Width = viewW, Height = viewH, Background = Brushes.Black };
        var image = new System.Windows.Controls.Image
        {
            Source = bmp,
            Width = viewW,
            Height = viewH,
            Stretch = Stretch.Fill
        };
        canvas.Children.Add(image);
        _rubber = new System.Windows.Shapes.Rectangle
        {
            Stroke = Brushes.White,
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
            Visibility = Visibility.Collapsed
        };
        canvas.Children.Add(_rubber);
        canvas.MouseLeftButtonDown += OnDown;
        canvas.MouseMove += OnMove;
        canvas.MouseLeftButtonUp += OnUp;

        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(new TextBlock
        {
            Text = "Потяни рамку. Сохранится новый файл, исходник не трогаю.",
            Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
            Margin = new Thickness(0, 0, 0, 8)
        });
        root.Children.Add(canvas);
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(TemplateTheme.Pill("Сохранить", Accept, accent: true));
        buttons.Children.Add(TemplateTheme.Pill("Отмена", () => DialogResult = false));
        root.Children.Add(buttons);
        Content = root;
    }

    public static bool TryPick(string path, out int x, out int y, out int w, out int h)
    {
        x = y = w = h = 0;
        try
        {
            var window = new CropWindow(path);
            if (window.ShowDialog() != true)
                return false;
            x = window.CropX;
            y = window.CropY;
            w = window.CropW;
            h = window.CropH;
            return w >= 2 && h >= 2;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Обрезка");
            return false;
        }
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        _down = e.GetPosition((IInputElement)sender);
        _drag = true;
        ((UIElement)sender).CaptureMouse();
        Place(_down, _down);
    }

    void OnMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_drag)
            return;
        Place(_down, e.GetPosition((IInputElement)sender));
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_drag)
            return;
        _drag = false;
        ((UIElement)sender).ReleaseMouseCapture();
        Place(_down, e.GetPosition((IInputElement)sender));
    }

    void Place(Point a, Point b)
    {
        double x = Math.Max(0, Math.Min(a.X, b.X));
        double y = Math.Max(0, Math.Min(a.Y, b.Y));
        double w = Math.Abs(a.X - b.X);
        double h = Math.Abs(a.Y - b.Y);
        _rubber.Visibility = Visibility.Visible;
        _rubber.Width = w;
        _rubber.Height = h;
        Canvas.SetLeft(_rubber, x);
        Canvas.SetTop(_rubber, y);
    }

    void Accept()
    {
        if (_rubber.Visibility != Visibility.Visible || _rubber.Width < 4 || _rubber.Height < 4)
            return;
        double back = _bmp.PixelWidth / Math.Max(1, ((Canvas)_rubber.Parent).Width);
        CropX = (int)Math.Round(Canvas.GetLeft(_rubber) * back);
        CropY = (int)Math.Round(Canvas.GetTop(_rubber) * back);
        CropW = (int)Math.Round(_rubber.Width * back);
        CropH = (int)Math.Round(_rubber.Height * back);
        DialogResult = true;
    }
}
