using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Zwmenu.Services;

namespace Zwmenu.Overlay;

/// <summary>Окно связи двух ноутбуков: одна мышь через край экрана.</summary>
public sealed class EdgeShareWindow : Window
{
    private readonly TextBlock _status;
    private readonly TextBlock _prompt;
    private readonly StackPanel _peers;
    private readonly StackPanel _askRow;
    private readonly System.Windows.Controls.TextBox _host;
    private readonly System.Windows.Controls.Button _power;
    private readonly System.Windows.Controls.RadioButton _right;
    private readonly System.Windows.Controls.RadioButton _left;
    private bool _suspend;

    private EdgeShareWindow()
    {
        Title = "Два экрана";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x1A));
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI");

        var ink = Freeze(0xF5, 0xF0, 0xE8);
        var muted = Freeze(0xC8, 0xC2, 0xB8);
        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock
        {
            Text = "Одна мышь на два ноутбука в одной сети. После запуска Zwmenu связь поднимается сама, подтверждение не нужно. В игре эта мышь крутит камеру. Убрать связь на время можно из трея. Курсор переходит через край, язык на обоих один, а буфер общий: текст, картинка и файл. Файл с рабочего стола или из проводника можно дотащить до края в любую сторону: у курсора на другом ноутбуке будет плюс, и файл ляжет на его рабочий стол. После этого кнопку нужно отпустить. Если курсор завис на втором экране, нажми Esc три раза подряд быстрее чем за две секунды: мышь вернётся. Одиночный Esc её не трогает. Закрой ShareMouse и Hamachi, иначе край перехватят они.",
            Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            Margin = new Thickness(0, 0, 0, 12)
        });
        _status = new TextBlock { Foreground = ink, FontSize = 14.5, TextWrapping = TextWrapping.Wrap };
        root.Children.Add(_status);
        _prompt = new TextBlock
        {
            Foreground = ink,
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed
        };
        root.Children.Add(_prompt);
        _askRow = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed
        };
        var allow = Action("Разрешить", (_, _) => EdgeShareService.Shared.Respond(true));
        var deny = Action("Отклонить", (_, _) => EdgeShareService.Shared.Respond(false));
        deny.Margin = new Thickness(8, 0, 0, 0);
        _askRow.Children.Add(allow);
        _askRow.Children.Add(deny);
        root.Children.Add(_askRow);

        root.Children.Add(new TextBlock
        {
            Text = "Где стоит второй экран",
            Foreground = muted,
            Margin = new Thickness(0, 14, 0, 6),
            FontSize = 12.5
        });
        var sides = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        _right = new System.Windows.Controls.RadioButton { Content = "Справа", Foreground = ink, Margin = new Thickness(0, 0, 16, 0) };
        _left = new System.Windows.Controls.RadioButton { Content = "Слева", Foreground = ink };
        _right.Checked += (_, _) => SideChanged(true);
        _left.Checked += (_, _) => SideChanged(false);
        sides.Children.Add(_right);
        sides.Children.Add(_left);
        root.Children.Add(sides);

        _peers = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        root.Children.Add(_peers);

        var dial = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        dial.ColumnDefinitions.Add(new ColumnDefinition());
        dial.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _host = new System.Windows.Controls.TextBox
        {
            MinHeight = 28,
            Padding = new Thickness(6, 4, 6, 4),
            Background = Brushes.White,
            Foreground = Brushes.Black,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var byHost = Action("По адресу", (_, _) => EdgeShareService.Shared.ConnectHost(_host.Text));
        byHost.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(byHost, 1);
        dial.Children.Add(_host);
        dial.Children.Add(byHost);
        root.Children.Add(dial);

        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        _power = Action("Включить", OnPower);
        var forget = Action("Забыть", (_, _) => EdgeShareService.Shared.Forget());
        forget.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(_power);
        row.Children.Add(forget);
        root.Children.Add(row);

        Content = root;
        EdgeShareService.Shared.Changed += OnChanged;
        Closed += (_, _) => EdgeShareService.Shared.Changed -= OnChanged;
        Loaded += (_, _) => Refresh();
    }

    public static void ShowCurrent()
    {
        if (Current == null)
        {
            var window = new EdgeShareWindow();
            Current = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(Current, window))
                    Current = null;
            };
            window.Show();
        }

        Current.Activate();
        Current.Refresh();
    }

    private static EdgeShareWindow? Current { get; set; }

    private void OnChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Refresh);
            return;
        }

        Refresh();
    }

    private void Refresh()
    {
        var link = EdgeShareService.Shared;
        _status.Text = link.Status;
        if (link.LocalAddresses.Count > 0)
            _status.Text += "\nЭтот ноутбук: " + link.MachineName + ", " + string.Join(", ", link.LocalAddresses);

        string? code = link.Pending?.Code ?? link.OutgoingCode;
        if (!string.IsNullOrEmpty(code) && code.Length == 6)
            code = code[..3] + " " + code[3..];
        _prompt.Text = string.IsNullOrEmpty(code) ? "" : "Код " + code;
        _prompt.Visibility = string.IsNullOrEmpty(code) ? Visibility.Collapsed : Visibility.Visible;
        _askRow.Visibility = link.Pending == null ? Visibility.Collapsed : Visibility.Visible;
        _power.Content = link.Running ? "Выключить" : "Включить";

        _suspend = true;
        _right.IsChecked = link.PeerOnRight;
        _left.IsChecked = !link.PeerOnRight;
        _suspend = false;

        _peers.Children.Clear();
        var peers = link.Peers;
        if (!link.Running)
            return;
        if (peers.Count == 0)
        {
            _peers.Children.Add(new TextBlock
            {
                Text = "Второго ноутбука пока не видно. Включи «Два экрана» и на нём. Если Windows спросит брандмауэр, разреши частную сеть. Или впиши его адрес ниже.",
                Foreground = Freeze(0xC8, 0xC2, 0xB8),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5
            });
            return;
        }

        foreach (var peer in peers)
        {
            var line = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(new TextBlock
            {
                Text = peer.Name + "   " + peer.Host,
                Foreground = Freeze(0xF5, 0xF0, 0xE8),
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            });
            var button = Action("Подключить", (_, _) => EdgeShareService.Shared.ConnectPeer(peer));
            Grid.SetColumn(button, 1);
            line.Children.Add(button);
            _peers.Children.Add(line);
        }
    }

    private void SideChanged(bool onRight)
    {
        if (_suspend)
            return;
        EdgeShareService.Shared.SetPeerOnRight(onRight);
    }

    private void OnPower(object sender, RoutedEventArgs e)
    {
        if (EdgeShareService.Shared.Running)
            EdgeShareService.Shared.Stop();
        else
            EdgeShareService.Shared.Start();
    }

    private static System.Windows.Controls.Button Action(string caption, RoutedEventHandler click)
    {
        var button = new System.Windows.Controls.Button
        {
            Content = caption,
            MinWidth = 110,
            Height = 32,
            Padding = new Thickness(12, 4, 12, 4),
            Foreground = Freeze(0xF5, 0xF0, 0xE8),
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x30)),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Style = Flat()
        };
        button.Click += click;
        return button;
    }

    private static Style Flat()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(System.Windows.Controls.Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        return new Style(typeof(System.Windows.Controls.Button))
        {
            Setters = { new Setter(System.Windows.Controls.Control.TemplateProperty, new ControlTemplate(typeof(System.Windows.Controls.Button)) { VisualTree = border }) }
        };
    }

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
