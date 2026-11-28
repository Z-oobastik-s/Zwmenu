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
    private readonly TextBlock _powerCaption;
    private readonly System.Windows.Controls.Border _power;
    private readonly System.Windows.Controls.RadioButton _right;
    private readonly System.Windows.Controls.RadioButton _left;
    private bool _suspend;

    private EdgeShareWindow()
    {
        Title = "Два экрана";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        TemplateTheme.StyleWindow(this);

        const string intro =
            "Одна мышь на два ноутбука в одной сети. После запуска Zwmenu связь поднимается сама, подтверждение не нужно. "
            + "В игре эта мышь крутит камеру. Убрать связь на время можно из трея. Курсор переходит через край, язык на обоих один, а буфер общий: текст, картинка и файл. "
            + "Файл с рабочего стола или из проводника можно дотащить до края в любую сторону: у курсора на другом ноутбуке будет плюс, и файл ляжет на его рабочий стол. После этого кнопку нужно отпустить. "
            + "Если курсор завис на втором экране, нажми Esc три раза подряд быстрее чем за две секунды: мышь вернётся. Одиночный Esc её не трогает. Закрой ShareMouse и Hamachi, иначе край перехватят они.";

        _status = new TextBlock
        {
            Foreground = TemplateTheme.Brush(TemplateTheme.Fg),
            FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap
        };
        _prompt = new TextBlock
        {
            Foreground = TemplateTheme.Brush(TemplateTheme.AccentText),
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed
        };
        _askRow = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed
        };
        _askRow.Children.Add(TemplateTheme.Pill("Разрешить", () => EdgeShareService.Shared.Respond(true), accent: true));
        _askRow.Children.Add(TemplateTheme.Pill("Отклонить", () => EdgeShareService.Shared.Respond(false)));

        var sides = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        _right = new System.Windows.Controls.RadioButton
        {
            Content = "Справа",
            Foreground = TemplateTheme.Brush(TemplateTheme.Fg),
            Margin = new Thickness(0, 0, 18, 0),
            FontFamily = new FontFamily(UiTheme.Current.FontUi),
            FontSize = 13,
            Cursor = Cursors.Hand
        };
        _left = new System.Windows.Controls.RadioButton
        {
            Content = "Слева",
            Foreground = TemplateTheme.Brush(TemplateTheme.Fg),
            FontFamily = new FontFamily(UiTheme.Current.FontUi),
            FontSize = 13,
            Cursor = Cursors.Hand
        };
        _right.Checked += (_, _) => SideChanged(true);
        _left.Checked += (_, _) => SideChanged(false);
        sides.Children.Add(_right);
        sides.Children.Add(_left);

        _peers = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

        var dial = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        dial.ColumnDefinitions.Add(new ColumnDefinition());
        dial.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _host = TemplateTheme.Field();
        _host.MinHeight = 32;
        _host.VerticalContentAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_host, 0);
        dial.Children.Add(_host);
        var byHost = TemplateTheme.Pill("По адресу", () => EdgeShareService.Shared.ConnectHost(_host.Text));
        Grid.SetColumn(byHost, 1);
        dial.Children.Add(byHost);

        _powerCaption = new TextBlock
        {
            Text = "Включить",
            Foreground = TemplateTheme.Brush(TemplateTheme.AccentText),
            FontFamily = new FontFamily(UiTheme.Current.FontUi),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var powerFill = TemplateTheme.Brush(TemplateTheme.Accent);
        var powerHover = TemplateTheme.Brush(TemplateTheme.AccentHover);
        _power = new System.Windows.Controls.Border
        {
            Background = powerFill,
            BorderBrush = TemplateTheme.Brush(Color.FromArgb(0x66, TemplateTheme.AccentText.R, TemplateTheme.AccentText.G, TemplateTheme.AccentText.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(UiTheme.Current.CornerPill),
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 6, 6),
            Cursor = Cursors.Hand,
            Child = _powerCaption,
            SnapsToDevicePixels = true
        };
        _power.MouseEnter += (_, _) => _power.Background = powerHover;
        _power.MouseLeave += (_, _) => _power.Background = powerFill;
        _power.MouseLeftButtonUp += (_, _) => OnPower();

        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        actions.Children.Add(_power);
        actions.Children.Add(TemplateTheme.Pill("Забыть", () => EdgeShareService.Shared.Forget()));

        var body = new StackPanel();
        body.Children.Add(ForgeChrome.BrandHead("Два экрана", "Одна мышь через край монитора"));
        body.Children.Add(ForgeChrome.Card(new TextBlock
        {
            Text = intro,
            Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            LineHeight = 17
        }, new Thickness(10, 8, 10, 8)));

        body.Children.Add(ForgeChrome.Section("Связь"));
        body.Children.Add(ForgeChrome.Card(_status, new Thickness(10, 8, 10, 8)));
        body.Children.Add(_prompt);
        body.Children.Add(_askRow);

        body.Children.Add(ForgeChrome.Section("Расположение"));
        body.Children.Add(ForgeChrome.Card(sides, new Thickness(10, 8, 10, 8)));

        body.Children.Add(ForgeChrome.Section("Узлы в сети"));
        body.Children.Add(ForgeChrome.Card(_peers, new Thickness(10, 8, 10, 8)));

        body.Children.Add(ForgeChrome.Section("Подключение"));
        body.Children.Add(ForgeChrome.Card(dial, new Thickness(10, 6, 10, 6)));
        body.Children.Add(actions);

        var shell = ForgeChrome.Shell(TrayPaint.HiddenScroll(body), new Thickness(14, 12, 14, 10));
        Content = shell;

        EdgeShareService.Shared.Changed += OnChanged;
        Closed += (_, _) => EdgeShareService.Shared.Changed -= OnChanged;
        Loaded += (_, _) => Refresh();
        ContentRendered += (_, _) => ForgeChrome.FadeIn(shell);
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
        _powerCaption.Text = link.Running ? "Выключить" : "Включить";

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
                Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
                LineHeight = 17
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
                Foreground = TemplateTheme.Brush(TemplateTheme.Fg),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13
            });
            var button = TemplateTheme.Pill("Подключить", () => EdgeShareService.Shared.ConnectPeer(peer));
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

    private void OnPower()
    {
        if (EdgeShareService.Shared.Running)
            EdgeShareService.Shared.Stop();
        else
            EdgeShareService.Shared.Start();
    }
}