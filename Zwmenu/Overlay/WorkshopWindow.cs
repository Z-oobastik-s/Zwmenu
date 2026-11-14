using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Zwmenu.Services;

namespace Zwmenu.Overlay;

/// <summary>Имена, дом, картинки, видео, сервер и записи. Один сектор на колесе.</summary>
public sealed partial class WorkshopWindow : Window
{
    static readonly string[] Pages =
    {
        "Имена", "Дом", "Сеть", "Устройства", "Место", "Картинки", "Видео", "Сервер", "Записи"
    };

    static WorkshopWindow? _open;
    readonly Dictionary<string, UIElement> _built = new();
    readonly ContentControl _host = new();
    readonly TextBlock _status;
    readonly System.Windows.Controls.ListBox _nav;
    readonly List<string> _names = new();
    bool _busy;
    bool _filling;
    bool _startTouched;
    System.Windows.Controls.ListBox? _fileList;
    System.Windows.Controls.ListBox? _previewList;
    System.Windows.Controls.TextBox? _pattern;
    System.Windows.Controls.TextBox? _start;
    System.Windows.Controls.TextBox? _step;
    System.Windows.Controls.TextBox? _pad;
    int _orderMode;
    readonly List<Border> _orderPills = new();
    System.Windows.Controls.CheckBox? _keepExt;
    System.Windows.Controls.CheckBox? _micLock;
    System.Windows.Controls.CheckBox? _camLock;
    StackPanel? _diskHost;
    System.Windows.Controls.TextBox? _serverLog;

    public static void ShowCurrent()
    {
        if (_open != null)
        {
            _open.Activate();
            return;
        }

        var window = new WorkshopWindow();
        _open = window;
        window.Show();
    }

    WorkshopWindow()
    {
        Title = "Мастерская";
        Cursor = System.Windows.Input.Cursors.Arrow;
        Width = 980;
        Height = 680;
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        TemplateTheme.StyleWindow(this);
        WorkshopHub.Noted += OnNote;
        Closed += (_, _) =>
        {
            WorkshopHub.Noted -= OnNote;
            if (_open == this)
                _open = null;
        };

        _status = new TextBlock
        {
            Foreground = TemplateTheme.Brush(TemplateTheme.AccentText),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 4, 12, 10),
            FontSize = 13
        };
        _nav = TemplateTheme.DarkList();
        _nav.Margin = new Thickness(12, 12, 8, 0);
        foreach (string page in Pages)
            _nav.Items.Add(page);
        _nav.SelectionChanged += (_, _) =>
        {
            if (_nav.SelectedItem is string name)
                Show(name);
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_nav, 0);
        Grid.SetColumn(_nav, 0);
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _host,
            Margin = new Thickness(8, 12, 12, 0)
        };
        Grid.SetColumn(scroll, 1);
        Grid.SetRowSpan(_status, 1);
        Grid.SetColumn(_status, 0);
        Grid.SetColumnSpan(_status, 2);
        Grid.SetRow(_status, 1);
        grid.Children.Add(_nav);
        grid.Children.Add(scroll);
        grid.Children.Add(_status);
        Content = grid;
        _nav.SelectedIndex = 0;
    }

    void OnNote(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
            _status.Text = text;
    }

    void Show(string name)
    {
        if (!_built.TryGetValue(name, out var page))
        {
            page = name switch
            {
                "Имена" => NamesPage(),
                "Дом" => HousePage(),
                "Сеть" => NetPage(),
                "Устройства" => DevicesPage(),
                "Место" => DiskPage(),
                "Картинки" => PicturesPage(),
                "Видео" => VideoPage(),
                "Сервер" => ServerPage(),
                "Записи" => VoicePage(),
                _ => Hint("Нет такой страницы.")
            };
            _built[name] = page;
        }

        _host.Content = page;
        if (name == "Устройства")
            RefreshDevices();
    }

    UIElement NamesPage()
    {
        var root = Page("Имена пачкой");
        root.Children.Add(Hint("«Серия 1» сама станет Серия 1, Серия 2. Шаблон: {n} {n:00} {name} {stem} {date} {ext}. Сначала список, потом кнопка. Откат один, последний."));
        var row = Row();
        row.Children.Add(TemplateTheme.Pill("Выбрать файлы", PickNames, accent: true));
        row.Children.Add(TemplateTheme.Pill("Из проводника", FromExplorer));
        row.Children.Add(TemplateTheme.Pill("С полки", FromShelf));
        row.Children.Add(TemplateTheme.Pill("Убрать", RemoveName));
        row.Children.Add(TemplateTheme.Pill("Выше", () => MoveName(-1)));
        row.Children.Add(TemplateTheme.Pill("Ниже", () => MoveName(1)));
        row.Children.Add(TemplateTheme.Pill("Очистить", () => { _names.Clear(); PaintNames(); }));
        root.Children.Add(row);

        _fileList = TemplateTheme.DarkList();
        _fileList.Height = 120;
        _fileList.Margin = new Thickness(0, 0, 0, 8);
        root.Children.Add(_fileList);

        _pattern = TemplateTheme.Field();
        _pattern.Text = "Серия 1";
        _pattern.Margin = new Thickness(0, 0, 0, 6);
        _pattern.TextChanged += (_, _) =>
        {
            if (_filling)
                return;
            string text = _pattern.Text ?? "";
            var match = Regex.Match(text.Trim(), @"^(.*?)(\d+)\s*$");
            if (match.Success && !text.Contains('{') && !_startTouched)
            {
                _filling = true;
                if (_start != null)
                    _start.Text = int.Parse(match.Groups[2].Value).ToString();
                if (_pad != null)
                    _pad.Text = match.Groups[2].Value.Length.ToString();
                _filling = false;
            }

            if (text.Trim().Length == 0)
                _startTouched = false;
            PaintPreview();
        };
        root.Children.Add(_pattern);

        _start = Small("1");
        _step = Small("1");
        _pad = Small("0");
        _start.TextChanged += (_, _) => { if (!_filling) _startTouched = true; PaintPreview(); };
        _step.TextChanged += (_, _) => PaintPreview();
        _pad.TextChanged += (_, _) => PaintPreview();
        var numbers = Row();
        numbers.Children.Add(Label("Начало"));
        numbers.Children.Add(_start);
        numbers.Children.Add(Label("Шаг"));
        numbers.Children.Add(_step);
        numbers.Children.Add(Label("Нули"));
        numbers.Children.Add(_pad);
        root.Children.Add(numbers);
        var orders = Row();
        orders.Children.Add(OrderPill("По имени", 0));
        orders.Children.Add(OrderPill("По дате", 1));
        orders.Children.Add(OrderPill("Как выбраны", 2));
        root.Children.Add(orders);

        _keepExt = TemplateTheme.Tick("Оставить своё расширение");
        _keepExt.IsChecked = true;
        _keepExt.Margin = new Thickness(0, 0, 0, 8);
        _keepExt.Checked += (_, _) => PaintPreview();
        _keepExt.Unchecked += (_, _) => PaintPreview();
        root.Children.Add(_keepExt);

        _previewList = TemplateTheme.DarkList();
        _previewList.Height = 140;
        _previewList.Margin = new Thickness(0, 0, 0, 8);
        root.Children.Add(_previewList);
        var apply = Row();
        apply.Children.Add(TemplateTheme.Pill("Переименовать", () => { _ = ApplyNames(); }, accent: true));
        apply.Children.Add(TemplateTheme.Pill("Откатить", () => { _ = UndoNames(); }));
        root.Children.Add(apply);
        return root;
    }

    void PickNames()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Файлы для имён", CheckFileExists = true };
        if (dlg.ShowDialog(this) != true)
            return;
        AddNames(dlg.FileNames);
    }

    void FromExplorer()
    {
        try
        {
            AddNames(ShellSelectionService.GetSelectedFiles());
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    void FromShelf()
    {
        var paths = new List<string>();
        foreach (var entry in ShelfFiles.List())
        {
            if (!entry.Ghost && File.Exists(entry.Launch))
                paths.Add(entry.Launch);
        }

        AddNames(paths);
        if (paths.Count == 0)
            _status.Text = "На полке нет файлов.";
    }

    void AddNames(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;
            if (_names.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
                continue;
            _names.Add(path);
        }

        PaintNames();
    }

    void RemoveName()
    {
        if (_fileList?.SelectedIndex is int i && i >= 0 && i < _names.Count)
            _names.RemoveAt(i);
        PaintNames();
    }

    void MoveName(int delta)
    {
        if (_fileList?.SelectedIndex is not int i || i < 0)
            return;
        int next = i + delta;
        if (next < 0 || next >= _names.Count)
            return;
        (_names[i], _names[next]) = (_names[next], _names[i]);
        PaintNames();
        if (_fileList != null)
            _fileList.SelectedIndex = next;
    }

    void PaintNames()
    {
        if (_fileList == null)
            return;
        _fileList.Items.Clear();
        foreach (string path in _names)
            _fileList.Items.Add(Path.GetFileName(path));
        PaintPreview();
    }

    Border OrderPill(string text, int index)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = TemplateTheme.Brush(TemplateTheme.Fg),
            FontSize = 12,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        var border = new Border
        {
            Background = TemplateTheme.Brush(index == _orderMode ? TemplateTheme.Accent : TemplateTheme.BtnBg),
            BorderBrush = TemplateTheme.Brush(TemplateTheme.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(0, 0, 6, 6),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = label,
            Tag = index
        };
        border.MouseLeftButtonUp += (_, _) =>
        {
            _orderMode = index;
            foreach (var pill in _orderPills)
            {
                int at = pill.Tag is int n ? n : -1;
                pill.Background = TemplateTheme.Brush(at == _orderMode ? TemplateTheme.Accent : TemplateTheme.BtnBg);
            }
            PaintPreview();
        };
        _orderPills.Add(border);
        return border;
    }

    RenameRequest Request()
    {
        var order = _orderMode switch
        {
            1 => RenameOrder.Modified,
            2 => RenameOrder.AsGiven,
            _ => RenameOrder.Name
        };
        return new RenameRequest
        {
            Pattern = _pattern?.Text ?? "",
            Start = Num(_start?.Text, 1),
            Step = Num(_step?.Text, 1),
            Pad = Math.Max(0, Num(_pad?.Text, 0)),
            KeepExtension = _keepExt?.IsChecked != false,
            Order = order
        };
    }

    void PaintPreview()
    {
        if (_previewList == null)
            return;
        _previewList.Items.Clear();
        if (_names.Count == 0)
            return;
        try
        {
            var preview = BatchRename.Preview(_names, Request());
            if (preview.Problem != null)
            {
                _previewList.Items.Add(preview.Problem);
                return;
            }

            foreach (var row in preview.Rows)
            {
                _previewList.Items.Add(row.Problem != null
                    ? row.OldName + "  " + row.Problem
                    : row.OldName + "  ->  " + row.NewName + (row.Same ? "  (так и есть)" : ""));
            }
        }
        catch (Exception ex)
        {
            _previewList.Items.Add(ex.Message);
        }
    }

    async Task ApplyNames()
    {
        if (_busy)
        {
            _status.Text = "Подожди, прошлая работа ещё идёт.";
            return;
        }

        var files = _names.ToList();
        var request = Request();
        var preview = BatchRename.Preview(files, request);
        if (preview.Problem != null || !preview.CanApply)
        {
            _status.Text = preview.Problem ?? preview.Rows.FirstOrDefault(r => r.Problem != null)?.Problem ?? "Нечего менять.";
            return;
        }

        int count = preview.Rows.Count(r => !r.Same);
        if (!Ask("Переименовать файлов: " + count + "?"))
            return;
        _busy = true;
        try
        {
            var (done, error) = await Task.Run(() => BatchRename.Apply(files, request));
            if (!IsLoaded)
                return;
            if (error != null)
            {
                _status.Text = error;
                return;
            }

            _names.Clear();
            _names.AddRange(preview.Rows.Select(r => string.IsNullOrEmpty(r.To) ? r.From : r.To));
            PaintNames();
            _status.Text = "Переименовано: " + done + ". Откат вернёт прошлые имена.";
        }
        finally
        {
            _busy = false;
        }
    }

    async Task UndoNames()
    {
        if (_busy)
            return;
        if (!BatchRename.CanUndo)
        {
            _status.Text = "Откатывать нечего.";
            return;
        }

        if (!Ask("Вернуть прошлые имена?"))
            return;
        _busy = true;
        try
        {
            var (done, error) = await Task.Run(BatchRename.Undo);
            if (!IsLoaded)
                return;
            _status.Text = error ?? "Вернул имён: " + done + ".";
            _names.Clear();
            PaintNames();
        }
        finally
        {
            _busy = false;
        }
    }

    UIElement HousePage()
    {
        var root = Page("Дом");
        root.Children.Add(Hint("Уход закрывает этот экран, выключает звук и останавливает незаконченную передачу. Если второй на связи и на той же сборке, то же уходит ему. Взгляд и печать тоже требуют одну сборку на обоих."));
        var row = Row();
        row.Children.Add(TemplateTheme.Pill("Уйти с обоих", () =>
        {
            if (!Ask("Закрыть этот экран и попросить второй сделать то же?"))
                return;
            _status.Text = MachineDesk.Leave(tellPeer: true);
        }, accent: true));
        row.Children.Add(TemplateTheme.Pill("Только этот", () =>
        {
            if (!Ask("Закрыть только этот экран?"))
                return;
            _status.Text = MachineDesk.Leave(tellPeer: false);
        }));
        row.Children.Add(TemplateTheme.Pill("Вернуть звук", () => _status.Text = MachineDesk.RestoreSound()));
        root.Children.Add(row);

        var awake = TemplateTheme.Tick("Не гасить экран. Передача файлов и так его держит.");
        awake.IsChecked = StayAwake.Manual;
        awake.Margin = new Thickness(0, 8, 0, 12);
        awake.Checked += (_, _) => StayAwake.SetManual(true);
        awake.Unchecked += (_, _) => StayAwake.SetManual(false);
        root.Children.Add(awake);

        var remote = Row();
        remote.Children.Add(TemplateTheme.Pill("Взгляд на второй", () =>
        {
            if (!EdgeShareService.Shared.Linked)
            {
                _status.Text = "Второй не на связи.";
                return;
            }

            if (!WorkshopHub.Send(WorkshopHub.GlanceAsk, ReadOnlySpan<byte>.Empty))
            {
                _status.Text = "Запрос не ушёл.";
                return;
            }

            _status.Text = "Прошу экран второго. Если у него старая сборка, ответ не придёт.";
        }, accent: true));
        remote.Children.Add(TemplateTheme.Pill("Печать на его принтере", () => { _ = PrintRemote(); }));
        root.Children.Add(remote);
        return root;
    }

    async Task PrintRemote()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Файл на принтер второго", CheckFileExists = true };
        if (dlg.ShowDialog(this) != true)
            return;
        _status.Text = "Отправляю на печать...";
        _status.Text = await WorkshopHub.SendFileToPrint(dlg.FileName);
    }

    UIElement NetPage()
    {
        var root = Page("Сеть и автозагрузка");
        root.Children.Add(Hint("Кто отвечает по этой сети. Автозагрузку для всех пользователей только показываю. Свою можно убрать и вернуть."));
        var peers = TemplateTheme.DarkList();
        peers.Height = 140;
        peers.Margin = new Thickness(0, 0, 0, 8);
        var startup = TemplateTheme.DarkList();
        startup.Height = 180;
        startup.Margin = new Thickness(0, 0, 0, 8);
        void LoadPeers()
        {
            peers.Items.Clear();
            string? saved = EdgeShareService.Shared.PeerName;
            peers.Items.Add(EdgeShareService.Shared.Linked
                ? "Второй на связи" + (string.IsNullOrWhiteSpace(saved) ? "" : ": " + saved)
                : "Второй не на связи" + (string.IsNullOrWhiteSpace(saved) ? "" : ". Сохранён: " + saved));
            foreach (var peer in MachineDesk.ScanLan())
                peers.Items.Add(peer.Address + "   " + peer.Mac + "   " + peer.Kind);
            if (peers.Items.Count == 1)
                peers.Items.Add("В arp пусто.");
        }

        void LoadStartup()
        {
            startup.Items.Clear();
            foreach (var item in MachineDesk.Startup())
                startup.Items.Add(new StartRow(item));
            if (startup.Items.Count == 0)
                startup.Items.Add("Автозагрузка пустая.");
        }

        var row = Row();
        row.Children.Add(TemplateTheme.Pill("Обновить", () => { LoadPeers(); LoadStartup(); }, accent: true));
        row.Children.Add(TemplateTheme.Pill("Убрать выбранное", () =>
        {
            if (startup.SelectedItem is not StartRow rowItem)
            {
                _status.Text = "Выбери свою запись.";
                return;
            }

            if (!Ask("Убрать «" + rowItem.Item.Name + "» из автозагрузки?"))
                return;
            _status.Text = MachineDesk.DisableStartup(rowItem.Item);
            LoadStartup();
        }));
        row.Children.Add(TemplateTheme.Pill("Вернуть убранное", () =>
        {
            _status.Text = MachineDesk.RestoreStartup();
            LoadStartup();
        }));
        root.Children.Add(row);
        root.Children.Add(peers);
        root.Children.Add(startup);
        LoadPeers();
        LoadStartup();
        return root;
    }

    UIElement DevicesPage()
    {
        var root = Page("Микрофон и камера");
        root.Children.Add(Hint("Кто держит устройство прямо сейчас, по отметке Windows. Замок запрещает новый доступ. Уже открытая программа может не отпустить, пока её не закроешь."));
        var who = TemplateTheme.DarkList();
        who.Height = 160;
        who.Margin = new Thickness(0, 0, 0, 8);
        who.Tag = "who";
        _micLock = TemplateTheme.Tick("Микрофон закрыт");
        _micLock.Margin = new Thickness(0, 0, 16, 8);
        _camLock = TemplateTheme.Tick("Камера закрыта");
        _camLock.Margin = new Thickness(0, 0, 0, 8);
        var row = Row();
        row.Children.Add(TemplateTheme.Pill("Обновить", RefreshDevices, accent: true));
        row.Children.Add(TemplateTheme.Pill("Применить замок", () =>
        {
            _status.Text = MachineDesk.SetDeviceLock(_micLock.IsChecked == true, _camLock.IsChecked == true);
            RefreshDevices();
        }));
        root.Children.Add(row);
        root.Children.Add(who);
        var locks = Row();
        locks.Children.Add(_micLock);
        locks.Children.Add(_camLock);
        root.Children.Add(locks);
        root.Tag = who;
        return root;
    }

    void RefreshDevices()
    {
        if (_built.TryGetValue("Устройства", out var page) && page is StackPanel panel && panel.Tag is System.Windows.Controls.ListBox who)
        {
            who.Items.Clear();
            var list = MachineDesk.Devices();
            if (list.Count == 0)
                who.Items.Add("Сейчас никто не держит микрофон и камеру.");
            foreach (var item in list)
                who.Items.Add(item.Device + ": " + item.Name);
        }

        var closed = MachineDesk.ClosedDevices();
        if (_micLock != null)
            _micLock.IsChecked = closed.Mic;
        if (_camLock != null)
            _camLock.IsChecked = closed.Cam;
    }

    UIElement DiskPage()
    {
        var root = Page("Место");
        root.Children.Add(Hint("Ищу файлы от 30 МБ на столе, в загрузках, документах, видео и картинках. Глубже четырёх папок не лезу. В корзину уходит только то, что отмечено. Остальное не трогаю."));
        var row = Row();
        row.Children.Add(TemplateTheme.Pill("Найти тяжёлые", () => { _ = ScanDisk(); }, accent: true));
        row.Children.Add(TemplateTheme.Pill("Отмеченное в корзину", () => { _ = TrashMarked(); }));
        root.Children.Add(row);
        var scroll = new ScrollViewer { Height = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _diskHost = new StackPanel();
        scroll.Content = _diskHost;
        root.Children.Add(scroll);
        return root;
    }

    async Task ScanDisk()
    {
        if (_busy || _diskHost == null)
            return;
        _busy = true;
        _status.Text = "Смотрю папки...";
        _diskHost.Children.Clear();
        try
        {
            var files = await Task.Run(() => MachineDesk.FindHeavy(CancellationToken.None));
            if (!IsLoaded || _diskHost == null)
                return;
            if (files.Count == 0)
            {
                _status.Text = "Крупных файлов в этих папках нет.";
                return;
            }

            foreach (var file in files)
            {
                var tick = TemplateTheme.Tick(file.Label);
                tick.Tag = file.Path;
                tick.Margin = new Thickness(0, 2, 0, 2);
                _diskHost.Children.Add(tick);
            }

            _status.Text = "Нашёл: " + files.Count + ". Отметь, что убрать в корзину.";
        }
        finally
        {
            _busy = false;
        }
    }

    async Task TrashMarked()
    {
        if (_diskHost == null)
            return;
        var paths = new List<string>();
        foreach (var child in _diskHost.Children)
        {
            if (child is System.Windows.Controls.CheckBox tick && tick.IsChecked == true && tick.Tag is string path)
                paths.Add(path);
        }

        if (paths.Count == 0)
        {
            _status.Text = "Ничего не отмечено.";
            return;
        }

        if (!Ask("Убрать в корзину файлов: " + paths.Count + "?"))
            return;
        _status.Text = await Task.Run(() => MachineDesk.Recycle(paths));
        await ScanDisk();
    }

    UIElement PicturesPage()
    {
        var root = Page("Картинки");
        root.Children.Add(Hint("Новый файл ложится рядом. Исходник не затираю."));
        var row = Row();
        row.Children.Add(TemplateTheme.Pill("Гифка", () => { _ = MakeGif(); }, accent: true));
        row.Children.Add(TemplateTheme.Pill("Лист", () => { _ = MakeSheet(0); }));
        row.Children.Add(TemplateTheme.Pill("Две рядом", () => { _ = MakeSheet(2); }));
        row.Children.Add(TemplateTheme.Pill("Обрезка", () => { _ = CropOne(); }));
        row.Children.Add(TemplateTheme.Pill("Поворот вправо", () => { _ = Turn(System.Drawing.RotateFlipType.Rotate90FlipNone); }));
        row.Children.Add(TemplateTheme.Pill("Поворот влево", () => { _ = Turn(System.Drawing.RotateFlipType.Rotate270FlipNone); }));
        row.Children.Add(TemplateTheme.Pill("Вверх ногами", () => { _ = Turn(System.Drawing.RotateFlipType.Rotate180FlipNone); }));
        root.Children.Add(row);
        return root;
    }

    async Task MakeGif()
    {
        var files = PickMany("Картинки|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp");
        if (files == null)
            return;
        string? seconds = AskText("Сколько секунд держать каждый кадр?", "0,4");
        if (seconds == null)
            return;
        await Work(async () =>
        {
            var (path, error) = await ClipBench.GifAsync(files, NumD(seconds, 0.4));
            return error ?? ("Гифка: " + path);
        });
    }

    async Task MakeSheet(int columns)
    {
        var files = PickMany("Картинки|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp");
        if (files == null)
            return;
        if (columns == 2 && files.Length != 2)
        {
            _status.Text = "Для «две рядом» нужны ровно две картинки.";
            return;
        }

        int cols = columns == 2 ? 2 : Math.Clamp(files.Length < 3 ? files.Length : 3, 1, 8);
        await Work(() => Task.Run(() =>
        {
            var (path, error) = PictureBench.Sheet(files, cols);
            return error ?? ("Лист: " + path);
        }));
    }

    async Task CropOne()
    {
        string? file = PickOne("Картинки|*.png;*.jpg;*.jpeg;*.bmp;*.webp");
        if (file == null)
            return;
        if (!CropWindow.TryPick(file, out int x, out int y, out int w, out int h))
            return;
        await Work(() => Task.Run(() =>
        {
            var (path, error) = PictureBench.Crop(file, new System.Drawing.Rectangle(x, y, w, h));
            return error ?? ("Обрезка: " + path);
        }));
    }

    async Task Turn(System.Drawing.RotateFlipType flip)
    {
        string? file = PickOne("Картинки|*.png;*.jpg;*.jpeg;*.bmp;*.webp");
        if (file == null)
            return;
        await Work(() => Task.Run(() =>
        {
            var (path, error) = PictureBench.Rotate(file, flip);
            return error ?? ("Поворот: " + path);
        }));
    }

    UIElement VideoPage()
    {
        var root = Page("Видео");
        root.Children.Add(Hint("Кадр, звук, кусок и ролик из картинок. FFmpeg скачается сам, если его ещё нет. Это может занять минуту."));
        var row = Row();
        row.Children.Add(TemplateTheme.Pill("Кадр", () => { _ = GrabFrame(); }, accent: true));
        row.Children.Add(TemplateTheme.Pill("Звук из видео", () => { _ = GrabAudio(); }));
        row.Children.Add(TemplateTheme.Pill("Кусок", () => { _ = TrimClip(); }));
        row.Children.Add(TemplateTheme.Pill("Ролик из кадров", () => { _ = MakeSlide(); }));
        root.Children.Add(row);
        return root;
    }

    async Task GrabFrame()
    {
        string? file = PickOne("Видео|*.mp4;*.mkv;*.mov;*.avi;*.webm");
        if (file == null)
            return;
        string? second = AskText("Секунда кадра", "1");
        if (second == null)
            return;
        await Work(async () =>
        {
            var (path, error) = await ClipBench.FrameAsync(file, NumD(second, 1));
            return error ?? ("Кадр: " + path);
        });
    }

    async Task GrabAudio()
    {
        string? file = PickOne("Видео|*.mp4;*.mkv;*.mov;*.avi;*.webm");
        if (file == null)
            return;
        await Work(async () =>
        {
            var (path, error) = await MediaConvertService.ExtractAudioAsync(file);
            return error ?? ("Звук: " + path);
        });
    }

    async Task TrimClip()
    {
        string? file = PickOne("Видео|*.mp4;*.mkv;*.mov;*.avi;*.webm");
        if (file == null)
            return;
        string? start = AskText("Начало, секунды", "0");
        if (start == null)
            return;
        string? end = AskText("Конец, секунды", "10");
        if (end == null)
            return;
        await Work(async () =>
        {
            var (path, error) = await ClipBench.TrimAsync(file, NumD(start, 0), NumD(end, 10));
            return error ?? ("Кусок: " + path);
        });
    }

    async Task MakeSlide()
    {
        var files = PickMany("Картинки|*.png;*.jpg;*.jpeg;*.bmp;*.webp");
        if (files == null)
            return;
        string? seconds = AskText("Сколько секунд держать каждый кадр?", "2");
        if (seconds == null)
            return;
        await Work(async () =>
        {
            var (path, error) = await ClipBench.SlideshowAsync(files, NumD(seconds, 2));
            return error ?? ("Ролик: " + path);
        });
    }

    UIElement ServerPage()
    {
        var root = Page("Сервер");
        var server = SnippetStore.Server;
        string where = string.IsNullOrWhiteSpace(server.Host)
            ? "Адрес RCON не задан. Он берётся из шаблонов."
            : "RCON " + server.Host + ":" + (server.Port <= 0 ? 25575 : server.Port);
        root.Children.Add(Hint(where + ". Ник, кик, бан, день, дождь, набор, фраза. Остановка сервера спрашивает ещё раз: включить его обратно из RCON нельзя, это делает твой bat."));
        var nick = TemplateTheme.Field();
        nick.Text = "";
        nick.Margin = new Thickness(0, 0, 0, 6);
        var reason = TemplateTheme.Field();
        reason.Margin = new Thickness(0, 0, 0, 8);
        root.Children.Add(Label("Ник"));
        root.Children.Add(nick);
        root.Children.Add(Label("Причина кика или бана"));
        root.Children.Add(reason);
        var row = Row();
        row.Children.Add(TemplateTheme.Pill("В белый список", () => { _ = ServerGo(() => ServerDesk.Whitelist(NeedNick(nick))); }, accent: true));
        row.Children.Add(TemplateTheme.Pill("Кик", () => { _ = ServerGo(() => ServerDesk.Kick(NeedNick(nick), reason.Text)); }));
        row.Children.Add(TemplateTheme.Pill("Бан", () => { _ = ServerGo(() => ServerDesk.Ban(NeedNick(nick), reason.Text)); }));
        root.Children.Add(row);
        var world = Row();
        world.Children.Add(TemplateTheme.Pill("День", () => { _ = ServerGo(() => ServerDesk.TimeCommand("day")!); }));
        world.Children.Add(TemplateTheme.Pill("Ночь", () => { _ = ServerGo(() => ServerDesk.TimeCommand("night")!); }));
        world.Children.Add(TemplateTheme.Pill("Ясно", () => { _ = ServerGo(() => ServerDesk.WeatherCommand("clear")!); }));
        world.Children.Add(TemplateTheme.Pill("Дождь", () => { _ = ServerGo(() => ServerDesk.WeatherCommand("rain")!); }));
        root.Children.Add(world);
        var kit = TemplateTheme.Field(multi: true);
        kit.Text = ServerDesk.DefaultKit;
        kit.Height = 90;
        kit.Margin = new Thickness(0, 0, 0, 6);
        root.Children.Add(Label("Набор, одна команда give на строку"));
        root.Children.Add(kit);
        var kitRow = Row();
        kitRow.Children.Add(TemplateTheme.Pill("Выдать набор", () => { _ = GiveKit(kit.Text); }, accent: true));
        root.Children.Add(kitRow);
        var say = TemplateTheme.Field();
        say.Margin = new Thickness(0, 0, 0, 6);
        root.Children.Add(Label("Фраза в чат"));
        root.Children.Add(say);
        var sayRow = Row();
        sayRow.Children.Add(TemplateTheme.Pill("Сказать", () =>
        {
            string text = (say.Text ?? "").Trim();
            if (text.Length == 0)
            {
                _status.Text = "Фраза пустая.";
                return;
            }

            _ = ServerGo(() => ServerDesk.Say(text));
        }));
        sayRow.Children.Add(TemplateTheme.Pill("Остановить сервер", () => { _ = StopServer(); }));
        root.Children.Add(sayRow);
        _serverLog = TemplateTheme.Field(multi: true);
        _serverLog.IsReadOnly = true;
        _serverLog.Height = 80;
        root.Children.Add(_serverLog);
        return root;
    }

    string NeedNick(System.Windows.Controls.TextBox box)
    {
        string nick = (box.Text ?? "").Trim();
        string? problem = ServerDesk.NickProblem(nick);
        if (problem != null)
            throw new InvalidOperationException(problem);
        return nick;
    }

    async Task GiveKit(string text)
    {
        string? problem = ServerDesk.KitProblem(text, out var commands);
        if (problem != null)
        {
            _status.Text = problem;
            return;
        }

        await Work(async () =>
        {
            var (ok, reply) = await ServerDesk.RunAllAsync(commands);
            return (ok ? "Набор ушёл.\n" : "Набор остановился.\n") + reply;
        });
    }

    async Task StopServer()
    {
        if (!Ask("Остановить сервер? Снова его поднимет тот bat или ярлык, которым ты его запускаешь. Из этой кнопки включить выключенный сервер нельзя."))
            return;
        await ServerGo(() => "stop");
    }

    async Task ServerGo(Func<string> command)
    {
        string text;
        try { text = command(); }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            _status.Text = "Команда пустая.";
            return;
        }

        await Work(async () =>
        {
            var (ok, reply) = await ServerDesk.RunAsync(text);
            return (ok ? "Ушло. " : "Не ушло. ") + reply;
        });
    }

    UIElement VoicePage()
    {
        var root = Page("Записи");
        root.Children.Add(Hint("Убрать гул из уже записанного файла и склеить несколько записей по порядку. Новые файлы ложатся рядом, исходники остаются."));
        var row = Row();
        row.Children.Add(TemplateTheme.Pill("Убрать гул", () => { _ = Denoise(); }, accent: true));
        row.Children.Add(TemplateTheme.Pill("Склеить", () => { _ = JoinVoice(); }));
        root.Children.Add(row);
        root.Children.Add(Hint("Отдых в тренировке уже берёт секунды из упражнения: после отжиманий свои, после пресса свои."));
        return root;
    }

    async Task Denoise()
    {
        string? file = PickOne("Звук|*.wav;*.mp3;*.m4a;*.ogg;*.flac;*.aac|Все|*.*");
        if (file == null)
            return;
        await Work(async () =>
        {
            var (path, error) = await ClipBench.DenoiseAsync(file);
            return error ?? ("Готово: " + path);
        });
    }

    async Task JoinVoice()
    {
        var files = PickMany("Звук|*.wav;*.mp3;*.m4a;*.ogg;*.flac;*.aac|Все|*.*");
        if (files == null)
            return;
        await Work(async () =>
        {
            var (path, error) = await ClipBench.JoinAsync(files);
            return error ?? ("Склейка: " + path);
        });
    }

    async Task Work(Func<Task<string>> job)
    {
        if (_busy)
        {
            _status.Text = "Подожди, прошлая работа ещё идёт.";
            return;
        }

        _busy = true;
        _status.Text = "Работаю...";
        try
        {
            string text = await job();
            if (IsLoaded)
            {
                _status.Text = text;
                if (_serverLog != null && _nav.SelectedItem as string == "Сервер")
                    _serverLog.Text = text;
            }
        }
        catch (Exception ex)
        {
            if (IsLoaded)
                _status.Text = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    string? PickOne(string filter)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = filter, CheckFileExists = true };
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }

    string[]? PickMany(string filter)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = filter, Multiselect = true, CheckFileExists = true };
        return dlg.ShowDialog(this) == true ? dlg.FileNames : null;
    }

    string? AskText(string title, string value)
    {
        var box = TemplateTheme.Field();
        box.Text = value;
        box.Margin = new Thickness(0, 8, 0, 0);
        var window = new Window
        {
            Title = title,
            Width = 360,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize
        };
        TemplateTheme.StyleWindow(window);
        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(new TextBlock { Text = title, Foreground = TemplateTheme.Brush(TemplateTheme.Fg) });
        root.Children.Add(box);
        string? result = null;
        var buttons = Row();
        buttons.Children.Add(TemplateTheme.Pill("Дальше", () => { result = box.Text; window.DialogResult = true; }, accent: true));
        buttons.Children.Add(TemplateTheme.Pill("Отмена", () => window.DialogResult = false));
        root.Children.Add(buttons);
        window.Content = root;
        return window.ShowDialog() == true ? result ?? "" : null;
    }

    bool Ask(string text) =>
        System.Windows.MessageBox.Show(this, text, "Мастерская", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question)
        == System.Windows.MessageBoxResult.Yes;

    static int Num(string? text, int fallback) =>
        int.TryParse((text ?? "").Trim(), out int n) ? n : fallback;

    static double NumD(string? text, double fallback)
    {
        string raw = (text ?? "").Trim().Replace(',', '.');
        return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n)
            ? n
            : fallback;
    }

    static StackPanel Page(string title)
    {
        var root = new StackPanel();
        root.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = TemplateTheme.Brush(TemplateTheme.Fg),
            FontSize = 20,
            Margin = new Thickness(0, 0, 0, 6)
        });
        return root;
    }

    static TextBlock Hint(string text) => new()
    {
        Text = text,
        Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 8),
        FontSize = 12
    };

    static TextBlock Label(string text) => new()
    {
        Text = text,
        Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
        Margin = new Thickness(0, 0, 8, 6),
        VerticalAlignment = System.Windows.VerticalAlignment.Center
    };

    static System.Windows.Controls.TextBox Small(string text)
    {
        var box = TemplateTheme.Field();
        box.Text = text;
        box.Width = 64;
        box.Margin = new Thickness(0, 0, 8, 6);
        return box;
    }

    public static void Smoke()
    {
        var window = new WorkshopWindow();
        try
        {
            window.Left = -4000;
            window.ShowInTaskbar = false;
            window.Show();
            for (int i = 0; i < Pages.Length; i++)
                window._nav.SelectedIndex = i;
            window._keepExt?.ApplyTemplate();
            window.UpdateLayout();
        }
        finally
        {
            window.Close();
        }
    }

    static Panel Row() => new WrapPanel
    {
        Orientation = System.Windows.Controls.Orientation.Horizontal
    };

    sealed class StartRow
    {
        public StartRow(StartupItem item) => Item = item;
        public StartupItem Item { get; }
        public override string ToString() => Item.Name + "   " + Item.Where;
    }
}
