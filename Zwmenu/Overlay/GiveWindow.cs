using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Zwmenu.Services;

namespace Zwmenu.Overlay;

/// <summary>Конструктор minecraft:give как в Program Text Buffer.</summary>
public sealed class GiveWindow : Window
{
    private static GiveWindow? _current;

    private readonly System.Windows.Controls.TextBox _target = TemplateTheme.Field();
    private readonly System.Windows.Controls.TextBox _count = TemplateTheme.Field();
    private readonly System.Windows.Controls.TextBox _itemFilter = TemplateTheme.Field();
    private readonly System.Windows.Controls.TextBox _itemId = TemplateTheme.Field();
    private readonly System.Windows.Controls.ListBox _items = TemplateTheme.DarkList();
    private readonly TextBlock _itemRu = new()
    {
        Foreground = TemplateTheme.Brush(TemplateTheme.AccentText),
        FontFamily = new FontFamily("Consolas"),
        FontSize = 13,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 12)
    };
    private readonly System.Windows.Controls.TextBox _enchFilter = TemplateTheme.Field();
    private readonly System.Windows.Controls.ListBox _enchCatalog = TemplateTheme.DarkList();
    private readonly System.Windows.Controls.ListBox _enchPicked = TemplateTheme.DarkList();
    private readonly System.Windows.Controls.ComboBox _attrType = DarkCombo();
    private readonly System.Windows.Controls.ComboBox _attrSlot = DarkCombo();
    private readonly System.Windows.Controls.ComboBox _attrOp = DarkCombo();
    private readonly System.Windows.Controls.TextBox _attrAmount = TemplateTheme.Field();
    private readonly System.Windows.Controls.ListBox _attrList = TemplateTheme.DarkList();
    private readonly System.Windows.Controls.TextBox _customName = TemplateTheme.Field();
    private readonly System.Windows.Controls.TextBox _lore = TemplateTheme.Field(true);
    private readonly System.Windows.Controls.TextBox _preview = TemplateTheme.Field(true);
    private readonly TextBlock _status = new()
    {
        Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
        Margin = new Thickness(0, 0, 0, 8),
        TextWrapping = TextWrapping.Wrap
    };
    private readonly Grid _pageItem = new();
    private readonly Grid _pageEnch = new();
    private readonly Grid _pageAttr = new();
    private readonly Grid _pageText = new();
    private readonly Dictionary<string, Border> _nav = new();
    private readonly List<(string Id, int Level)> _ench = new();
    private readonly List<GiveCommand.AttrMod> _attrs = new();

    private GiveWindow()
    {
        Title = "Выдача предмета";
        Width = 1080;
        Height = 720;
        MinWidth = 860;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        TemplateTheme.StyleWindow(this);

        BuildItemPage();
        BuildEnchPage();
        BuildAttrPage();
        BuildTextPage();

        var root = new DockPanel { Background = TemplateTheme.Brush(TemplateTheme.BgDeep) };

        var hdr = new Border
        {
            Background = TemplateTheme.Brush(TemplateTheme.BgTitle),
            Padding = new Thickness(14, 10, 14, 10),
            BorderBrush = TemplateTheme.Brush(TemplateTheme.Border),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        var hdrRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        hdrRow.Children.Add(TemplateTheme.Pill("← К шаблонам", Close));
        hdrRow.Children.Add(new Border
        {
            Width = 3,
            Height = 18,
            Background = TemplateTheme.Brush(TemplateTheme.GiveAccent),
            Margin = new Thickness(8, 0, 10, 0),
            CornerRadius = new CornerRadius(1)
        });
        hdrRow.Children.Add(new TextBlock
        {
            Text = "Выдача предмета",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = TemplateTheme.Brush(TemplateTheme.Fg),
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        });
        hdrRow.Children.Add(new TextBlock
        {
            Text = "  minecraft:give  ·  Java 1.21",
            Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        });
        hdr.Child = hdrRow;
        DockPanel.SetDock(hdr, Dock.Top);
        root.Children.Add(hdr);

        var bottom = new Border
        {
            Background = TemplateTheme.Brush(Color.FromRgb(0x05, 0x05, 0x06)),
            Padding = new Thickness(14, 12, 14, 14),
            BorderBrush = TemplateTheme.Brush(TemplateTheme.Border),
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        var bottomStack = new StackPanel();
        bottomStack.Children.Add(_status);
        bottomStack.Children.Add(new TextBlock
        {
            Text = "КОМАНДА",
            Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 6)
        });
        _preview.Height = 78;
        _preview.FontFamily = new FontFamily("Consolas");
        bottomStack.Children.Add(_preview);
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        actions.Children.Add(TemplateTheme.Pill("Обновить", RefreshPreview));
        actions.Children.Add(TemplateTheme.Pill("Копировать", CopyCmd, accent: true));
        actions.Children.Add(TemplateTheme.Pill("Выполнить через RCON", () => _ = RunRcon()));
        actions.Children.Add(TemplateTheme.Pill("Настройки RCON…", () => SnippetWindow.ShowServer()));
        bottomStack.Children.Add(actions);
        bottom.Child = bottomStack;
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(168) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });

        var nav = new Border
        {
            Background = TemplateTheme.Brush(Color.FromRgb(0x09, 0x09, 0x0C)),
            BorderBrush = TemplateTheme.Brush(TemplateTheme.Border),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(8, 14, 8, 8)
        };
        var navStack = new StackPanel();
        navStack.Children.Add(new TextBlock
        {
            Text = "РАЗДЕЛ",
            Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
            FontSize = 11,
            Margin = new Thickness(8, 0, 0, 8)
        });
        foreach (var (key, label) in new[] { ("item", "Предмет"), ("ench", "Зачарования"), ("attr", "Атрибуты"), ("text", "Имя и описание") })
        {
            var btn = NavButton(label, () => ShowPage(key));
            _nav[key] = btn;
            navStack.Children.Add(btn);
        }
        nav.Child = navStack;
        body.Children.Add(nav);

        var work = new Grid { Margin = new Thickness(10, 10, 10, 8) };
        Grid.SetColumn(work, 1);
        work.Children.Add(_pageItem);
        work.Children.Add(_pageEnch);
        work.Children.Add(_pageAttr);
        work.Children.Add(_pageText);
        body.Children.Add(work);

        var side = new Border
        {
            Background = TemplateTheme.Brush(Color.FromRgb(0x0A, 0x0A, 0x0E)),
            BorderBrush = TemplateTheme.Brush(TemplateTheme.Border),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Padding = new Thickness(14)
        };
        Grid.SetColumn(side, 2);
        var sideStack = new StackPanel();
        sideStack.Children.Add(new TextBlock
        {
            Text = "СВОДКА",
            Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 10)
        });
        sideStack.Children.Add(_itemRu);
        sideStack.Children.Add(new TextBlock
        {
            Text = "Зачарования и атрибуты попадают в команду снизу. Разделы слева.",
            Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
            TextWrapping = TextWrapping.Wrap
        });
        side.Child = sideStack;
        body.Children.Add(side);
        root.Children.Add(body);
        Content = root;

        foreach (var a in GiveCommand.AttrTypes)
            _attrType.Items.Add(a.Id + " — " + a.Ru);
        foreach (var a in GiveCommand.AttrSlots)
            _attrSlot.Items.Add(a.Id + " — " + a.Ru);
        foreach (var a in GiveCommand.AttrOps)
            _attrOp.Items.Add(a.Id + " — " + a.Ru);
        if (_attrType.Items.Count > 0) _attrType.SelectedIndex = 0;
        if (_attrSlot.Items.Count > 0) _attrSlot.SelectedIndex = 0;
        if (_attrOp.Items.Count > 0) _attrOp.SelectedIndex = 0;
        _attrAmount.Text = "1";
        _itemId.Text = "diamond_sword";
        _target.Text = "@p";
        _count.Text = "1";

        _itemFilter.TextChanged += (_, _) => FillItems();
        _itemId.TextChanged += (_, _) => { UpdateStrip(); RefreshPreview(); };
        _items.SelectionChanged += (_, _) =>
        {
            if (_items.SelectedItem is string line)
                _itemId.Text = line.Split('—')[0].Trim();
        };
        _enchFilter.TextChanged += (_, _) => FillEnchCatalog();
        _enchCatalog.MouseDoubleClick += (_, _) => AddEnch();
        _customName.TextChanged += (_, _) => RefreshPreview();
        _lore.TextChanged += (_, _) => RefreshPreview();
        _target.TextChanged += (_, _) => RefreshPreview();
        _count.TextChanged += (_, _) => RefreshPreview();

        FillItems();
        FillEnchCatalog();
        ShowPage("item");
        RefreshPreview();
        Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, this))
                _current = null;
        };
    }

    public static void ShowCurrent()
    {
        if (_current == null)
            _current = new GiveWindow();
        _current.Show();
        if (_current.WindowState == WindowState.Minimized)
            _current.WindowState = WindowState.Normal;
        _current.Activate();
    }

    private void BuildItemPage()
    {
        _pageItem.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _pageItem.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _pageItem.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _pageItem.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        var left = Wrap("Цель (игрок / селектор)", _target);
        top.Children.Add(left);
        _count.Margin = new Thickness(12, 0, 0, 0);
        var right = Wrap("Кол-во", _count);
        Grid.SetColumn(right, 1);
        top.Children.Add(right);
        _pageItem.Children.Add(top);

        var filter = Wrap("Поиск предмета", _itemFilter);
        filter.Margin = new Thickness(0, 0, 0, 8);
        Grid.SetRow(filter, 1);
        _pageItem.Children.Add(filter);

        Grid.SetRow(_items, 2);
        _pageItem.Children.Add(_items);

        var id = Wrap("ID вручную", _itemId);
        id.Margin = new Thickness(0, 8, 0, 0);
        Grid.SetRow(id, 3);
        _pageItem.Children.Add(id);
    }

    private void BuildEnchPage()
    {
        _pageEnch.ColumnDefinitions.Add(new ColumnDefinition());
        _pageEnch.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        _pageEnch.ColumnDefinitions.Add(new ColumnDefinition());
        _pageEnch.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _pageEnch.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _pageEnch.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var left = Wrap("Каталог зачарований", _enchFilter);
        _pageEnch.Children.Add(left);
        Grid.SetRow(_enchCatalog, 1);
        _pageEnch.Children.Add(_enchCatalog);
        var add = TemplateTheme.Pill("Добавить →", AddEnch, accent: true);
        Grid.SetRow(add, 2);
        _pageEnch.Children.Add(add);

        var right = new TextBlock
        {
            Text = "На предмете",
            Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
            Margin = new Thickness(0, 0, 0, 6)
        };
        Grid.SetColumn(right, 2);
        _pageEnch.Children.Add(right);
        Grid.SetColumn(_enchPicked, 2);
        Grid.SetRow(_enchPicked, 1);
        _pageEnch.Children.Add(_enchPicked);
        var rem = TemplateTheme.Pill("Убрать", RemoveEnch);
        Grid.SetColumn(rem, 2);
        Grid.SetRow(rem, 2);
        _pageEnch.Children.Add(rem);
    }

    private void BuildAttrPage()
    {
        var stack = new StackPanel();
        stack.Children.Add(Label("Тип"));
        _attrType.Margin = new Thickness(0, 0, 0, 8);
        stack.Children.Add(_attrType);
        stack.Children.Add(Label("Слот / операция / величина"));
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        _attrOp.Margin = new Thickness(8, 0, 0, 0);
        _attrAmount.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_attrOp, 1);
        Grid.SetColumn(_attrAmount, 2);
        row.Children.Add(_attrSlot);
        row.Children.Add(_attrOp);
        row.Children.Add(_attrAmount);
        stack.Children.Add(row);
        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        buttons.Children.Add(TemplateTheme.Pill("Добавить модификатор", AddAttr, accent: true));
        buttons.Children.Add(TemplateTheme.Pill("Убрать", RemoveAttr));
        stack.Children.Add(buttons);
        _attrList.Height = 280;
        stack.Children.Add(_attrList);
        _pageAttr.Children.Add(stack);
    }

    private void BuildTextPage()
    {
        var stack = new StackPanel();
        stack.Children.Add(Label("Имя предмета"));
        _customName.Margin = new Thickness(0, 0, 0, 12);
        stack.Children.Add(_customName);
        stack.Children.Add(Label("Описание (каждая строка — отдельная строка lore)"));
        _lore.Height = 240;
        stack.Children.Add(_lore);
        _pageText.Children.Add(stack);
    }

    private void ShowPage(string key)
    {
        _pageItem.Visibility = key == "item" ? Visibility.Visible : Visibility.Collapsed;
        _pageEnch.Visibility = key == "ench" ? Visibility.Visible : Visibility.Collapsed;
        _pageAttr.Visibility = key == "attr" ? Visibility.Visible : Visibility.Collapsed;
        _pageText.Visibility = key == "text" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var pair in _nav)
        {
            bool on = pair.Key == key;
            pair.Value.Background = TemplateTheme.Brush(on ? TemplateTheme.AccentSoft : Color.FromRgb(0x09, 0x09, 0x0C));
            if (pair.Value.Child is TextBlock tb)
                tb.Foreground = TemplateTheme.Brush(on ? TemplateTheme.AccentText : TemplateTheme.FgDim);
        }
    }

    private void FillItems()
    {
        string q = (_itemFilter.Text ?? "").Trim().ToLowerInvariant();
        _items.Items.Clear();
        foreach (var item in GiveCommand.Items())
        {
            if (q.Length > 0 && !item.Blob.Contains(q, StringComparison.Ordinal))
                continue;
            _items.Items.Add(item.Line);
            if (_items.Items.Count > 900)
                break;
        }
    }

    private void FillEnchCatalog()
    {
        string q = (_enchFilter.Text ?? "").Trim().ToLowerInvariant();
        _enchCatalog.Items.Clear();
        foreach (var ench in GiveCommand.Enchantments())
        {
            if (q.Length > 0 && !ench.Line.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;
            _enchCatalog.Items.Add(ench.Line);
        }
    }

    private void AddEnch()
    {
        if (_enchCatalog.SelectedItem is not string line)
            return;
        string id = line.Split('—')[0].Trim();
        var row = GiveCommand.Enchantments().FirstOrDefault(e => e.Id == id);
        if (_ench.Any(e => e.Id == id))
            return;
        _ench.Add((id, row?.MaxLevel ?? 1));
        _enchPicked.Items.Clear();
        foreach (var e in _ench)
            _enchPicked.Items.Add(e.Id + " " + e.Level);
        RefreshPreview();
    }

    private void RemoveEnch()
    {
        int i = _enchPicked.SelectedIndex;
        if (i < 0 || i >= _ench.Count)
            return;
        _ench.RemoveAt(i);
        _enchPicked.Items.Clear();
        foreach (var e in _ench)
            _enchPicked.Items.Add(e.Id + " " + e.Level);
        RefreshPreview();
    }

    private void AddAttr()
    {
        string type = TakeId(_attrType.SelectedItem?.ToString());
        string slot = TakeId(_attrSlot.SelectedItem?.ToString());
        string op = TakeId(_attrOp.SelectedItem?.ToString());
        if (type.Length == 0)
            return;
        _attrs.Add(new GiveCommand.AttrMod(type, _attrAmount.Text.Trim(), slot, op, "zwmenu:" + Guid.NewGuid().ToString("N")[..8]));
        _attrList.Items.Clear();
        foreach (var a in _attrs)
            _attrList.Items.Add(a.Type + "  " + a.Amount + "  " + a.Slot);
        RefreshPreview();
    }

    private void RemoveAttr()
    {
        int i = _attrList.SelectedIndex;
        if (i < 0 || i >= _attrs.Count)
            return;
        _attrs.RemoveAt(i);
        _attrList.Items.Clear();
        foreach (var a in _attrs)
            _attrList.Items.Add(a.Type + "  " + a.Amount + "  " + a.Slot);
        RefreshPreview();
    }

    private void UpdateStrip()
    {
        string id = (_itemId.Text ?? "").Trim();
        string shortId = id.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase) ? id[10..] : id;
        string ru = GiveCommand.ItemRu(shortId);
        _itemRu.Text = string.IsNullOrEmpty(ru) ? id : id + "\n" + ru;
    }

    private void RefreshPreview()
    {
        try
        {
            int.TryParse(_count.Text.Trim(), out int count);
            if (count <= 0) count = 1;
            _preview.Text = GiveCommand.Build(
                _target.Text,
                _itemId.Text,
                count,
                _ench,
                _attrs,
                _customName.Text,
                (_lore.Text ?? "").Replace("\r\n", "\n").Split('\n'));
            UpdateStrip();
            _status.Text = "";
        }
        catch (Exception ex)
        {
            _preview.Text = "";
            _status.Text = ex.Message;
        }
    }

    private void CopyCmd()
    {
        RefreshPreview();
        if (string.IsNullOrWhiteSpace(_preview.Text))
            return;
        try
        {
            System.Windows.Clipboard.SetText(_preview.Text);
            _status.Text = "Команда скопирована.";
        }
        catch
        {
            _status.Text = "Не удалось записать в буфер.";
        }
    }

    private async Task RunRcon()
    {
        RefreshPreview();
        if (string.IsNullOrWhiteSpace(_preview.Text))
            return;
        var server = SnippetStore.Server;
        _status.Text = "Команда уходит на сервер…";
        var (ok, text) = await RconClient.ExecuteAsync(server.Host, server.Port, server.Password, _preview.Text);
        _status.Text = text;
        if (!ok)
            _status.Foreground = TemplateTheme.Brush(Color.FromRgb(0xE8, 0x98, 0x98));
        else
            _status.Foreground = TemplateTheme.Brush(TemplateTheme.FgDim);
    }

    private static string TakeId(string? label) =>
        string.IsNullOrWhiteSpace(label) ? "" : label.Split('—')[0].Trim();

    private static Border NavButton(string text, Action click)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
            FontSize = 13,
            Margin = new Thickness(10, 8, 10, 8)
        };
        var border = new Border
        {
            Child = label,
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 0, 4),
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = TemplateTheme.Brush(Color.FromRgb(0x09, 0x09, 0x0C))
        };
        border.MouseLeftButtonUp += (_, _) => click();
        return border;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
        Margin = new Thickness(0, 0, 0, 4)
    };

    private static StackPanel Wrap(string text, UIElement child)
    {
        var stack = new StackPanel();
        stack.Children.Add(Label(text));
        stack.Children.Add(child);
        return stack;
    }

    private static System.Windows.Controls.ComboBox DarkCombo() => new()
    {
        Background = TemplateTheme.Brush(TemplateTheme.ListBg),
        Foreground = TemplateTheme.Brush(TemplateTheme.Fg),
        BorderBrush = TemplateTheme.Brush(TemplateTheme.Border)
    };
}
