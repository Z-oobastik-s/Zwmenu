using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

        var chrome = new Grid { ClipToBounds = true };
        chrome.Children.Add(new System.Windows.Controls.Border
        {
            Background = TemplateTheme.Brush(TemplateTheme.BgPanel)
        });
        chrome.Children.Add(new System.Windows.Controls.Border
        {
            Background = ForgeChrome.PanelWash(),
            IsHitTestVisible = false
        });
        chrome.Children.Add(new System.Windows.Controls.Border
        {
            BorderBrush = TemplateTheme.Brush(Color.FromArgb(0x55, TemplateTheme.AccentText.R, TemplateTheme.AccentText.G, TemplateTheme.AccentText.B)),
            BorderThickness = new Thickness(0, 2, 0, 0),
            Margin = new Thickness(1),
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Top,
            Height = 2
        });

        var markHost = new System.Windows.Controls.Border
        {
            Width = 88,
            Height = 88,
            CornerRadius = new CornerRadius(44),
            ClipToBounds = true,
            BorderBrush = TemplateTheme.Brush(Color.FromArgb(0x77, TemplateTheme.AccentText.R, TemplateTheme.AccentText.G, TemplateTheme.AccentText.B)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(44, 0, 28, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Effect = TrayPaint.Shadow()
        };
        var mark = ForgeChrome.Mark();
        if (mark != null)
        {
            markHost.Child = new System.Windows.Controls.Image
            {
                Source = mark,
                Stretch = Stretch.UniformToFill
            };
        }
        else
        {
            markHost.Background = TemplateTheme.Brush(TemplateTheme.AccentSoft);
            markHost.Child = new System.Windows.Controls.Border
            {
                Width = 4,
                Background = TemplateTheme.Brush(TemplateTheme.AccentText),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Opacity = 0.85
            };
        }

        var signal = new System.Windows.Controls.Border
        {
            Width = 6,
            CornerRadius = new CornerRadius(3),
            Background = TemplateTheme.Brush(TemplateTheme.AccentText),
            Opacity = 0.35,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(36, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var title = new TextBlock
        {
            Text = "Обновлено",
            FontFamily = new FontFamily(UiTheme.Current.FontUi),
            FontSize = 32,
            FontWeight = FontWeights.SemiBold,
            Foreground = TemplateTheme.Brush(TemplateTheme.Fg),
            Margin = new Thickness(0, 0, 0, 8)
        };
        var line = new TextBlock
        {
            Text = "Программа успешно обновлена.",
            FontFamily = new FontFamily(UiTheme.Current.FontUi),
            FontSize = 15,
            Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
            Margin = new Thickness(0, 0, 0, 6)
        };
        var version = new TextBlock
        {
            Text = "Сейчас стоит версия " + AppUpdate.Current,
            FontFamily = new FontFamily(UiTheme.Current.FontUi),
            FontSize = 14,
            Foreground = TemplateTheme.Brush(TemplateTheme.AccentText),
            FontWeight = FontWeights.SemiBold
        };
        var words = new StackPanel
        {
            Margin = new Thickness(160, 0, 40, 28),
            VerticalAlignment = VerticalAlignment.Center
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

        chrome.Children.Add(signal);
        chrome.Children.Add(markHost);
        chrome.Children.Add(words);
        chrome.Children.Add(_life);

        var frame = new System.Windows.Controls.Border
        {
            BorderBrush = TemplateTheme.Brush(TemplateTheme.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(UiTheme.Current.CornerPanel),
            Effect = TrayPaint.Shadow(),
            Child = chrome
        };

        Content = frame;

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
            ForgeChrome.FadeIn(frame, 220);
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
}