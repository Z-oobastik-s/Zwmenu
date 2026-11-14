using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Zwmenu.Services;

namespace Zwmenu.Overlay;

/// <summary>Главное окно шаблонов: как Program Text Buffer — пилюли, поиск, полный список.</summary>
public sealed class SnippetWindow : Window
{
    private static SnippetWindow? _current;

    private readonly System.Windows.Controls.ListBox _list = TemplateTheme.DarkList();
    private readonly System.Windows.Controls.TextBox _search = TemplateTheme.Field();
    private readonly TextBlock _status = new()
    {
        Text = "Готово.",
        Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
        Margin = new Thickness(16, 8, 16, 12)
    };
    private readonly List<Snippet> _rows = new();
    private int? _dragFrom;
    private Point _dragStart;
    private bool _dragging;
    private IntPtr _other;

    private SnippetWindow()
    {
        Title = "Шаблоны";
        Width = 1100;
        Height = 700;
        MinWidth = 820;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        TemplateTheme.StyleWindow(this);

        var root = new DockPanel { Background = TemplateTheme.Brush(TemplateTheme.BgDeep) };

        var title = new Border
        {
            Background = TemplateTheme.Brush(TemplateTheme.BgTitle),
            Padding = new Thickness(16, 10, 16, 8),
            BorderBrush = TemplateTheme.Brush(TemplateTheme.Border),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        title.Child = new TextBlock
        {
            Text = "Шаблоны",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = TemplateTheme.Brush(TemplateTheme.Fg),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title);

        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);

        var body = new DockPanel { Margin = new Thickness(14) };

        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        toolbar.Children.Add(TemplateTheme.Pill("Добавить", Add));
        toolbar.Children.Add(TemplateTheme.Pill("Изменить", Edit));
        toolbar.Children.Add(TemplateTheme.Pill("Удалить", Delete));
        toolbar.Children.Add(new Border { Width = 10 });
        toolbar.Children.Add(TemplateTheme.Pill("Копировать в буфер", () => Copy(false), accent: true));
        toolbar.Children.Add(TemplateTheme.Pill("Импорт…", Import));
        toolbar.Children.Add(TemplateTheme.Pill("Экспорт…", Export));
        toolbar.Children.Add(TemplateTheme.Pill("Настройки…", OpenServer));
        toolbar.Children.Add(new Border { Width = 10 });
        toolbar.Children.Add(TemplateTheme.Pill("CMD", OpenCommands));
        toolbar.Children.Add(TemplateTheme.Pill("RCON", OpenServer));
        toolbar.Children.Add(TemplateTheme.Pill("WEB", OpenWeb));
        toolbar.Children.Add(TemplateTheme.Pill("Give…", GiveWindow.ShowCurrent));
        toolbar.Children.Add(TemplateTheme.Pill("AI", OpenLinks));
        DockPanel.SetDock(toolbar, Dock.Top);
        body.Children.Add(toolbar);

        var hint = new TextBlock
        {
            Text = "Выбери строку и нажми «Копировать» или дважды щёлкни. Перетащи строку, чтобы поменять порядок. Горячие клавиши работают и при закрытом окне. NumDel открывает это окно.",
            Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(hint, Dock.Top);
        body.Children.Add(hint);

        var searchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var searchLabel = new TextBlock
        {
            Text = "Поиск:",
            Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        DockPanel.SetDock(searchLabel, Dock.Left);
        searchRow.Children.Add(searchLabel);
        var clear = TemplateTheme.Pill("Очистить", () => _search.Text = "");
        clear.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(clear, Dock.Right);
        searchRow.Children.Add(clear);
        searchRow.Children.Add(_search);
        DockPanel.SetDock(searchRow, Dock.Top);
        body.Children.Add(searchRow);

        _list.BorderThickness = new Thickness(1);
        _list.MouseDoubleClick += (_, _) => Copy(false);
        _list.PreviewMouseLeftButtonDown += OnListDown;
        _list.PreviewMouseMove += OnListMove;
        _list.PreviewMouseLeftButtonUp += OnListUp;
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Copy(false);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete)
            {
                Delete();
                e.Handled = true;
            }
        };
        body.Children.Add(_list);
        root.Children.Add(body);
        Content = root;

        _search.TextChanged += (_, _) => Reload(SelectedId());
        SnippetStore.Load();
        Reload(SnippetStore.Snippets.FirstOrDefault()?.Id ?? 0);
        SetStatus("Готово.");

        Deactivated += (_, _) =>
        {
            IntPtr now = GetForegroundWindow();
            if (now != IntPtr.Zero && now != new WindowInteropHelper(this).Handle)
                _other = now;
        };
        Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, this))
                _current = null;
        };
    }

    public static bool IsEditing =>
        _current is { IsVisible: true, IsActive: true }
        || Application.Current.Windows.OfType<SnippetEditorDialog>().Any(w => w.IsVisible);

    public static void ShowCurrent()
    {
        if (_current == null)
            _current = new SnippetWindow();
        _current.Show();
        if (_current.WindowState == WindowState.Minimized)
            _current.WindowState = WindowState.Normal;
        _current.Activate();
    }

    public static void Toggle()
    {
        if (_current is { IsVisible: true, IsActive: true })
        {
            _current.Hide();
            return;
        }

        ShowCurrent();
    }

    public static void ShowServer()
    {
        ShowCurrent();
        _current!.OpenServer();
    }

    private void Reload(int selectId)
    {
        string q = (_search.Text ?? "").Trim();
        _rows.Clear();
        _rows.AddRange(SnippetStore.Snippets.Where(item =>
            q.Length == 0
            || item.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || item.Text.Contains(q, StringComparison.OrdinalIgnoreCase)));
        _list.Items.Clear();
        foreach (var item in _rows)
        {
            string hk = string.IsNullOrWhiteSpace(item.Hotkey) ? "" : "  [" + Pretty(item.Hotkey) + "]";
            _list.Items.Add(new Row(item.Id, item.Title + hk));
        }

        Select(selectId);
    }

    private void Add()
    {
        var dialog = new SnippetEditorDialog(this, "Новая заготовка", "", "", "", "copy");
        if (dialog.ShowDialog() != true || dialog.Result == null)
            return;
        var items = SnippetStore.Snippets.ToList();
        int id = items.Count == 0 ? 1 : items.Max(i => i.Id) + 1;
        items.Add(new Snippet
        {
            Id = id,
            Title = dialog.Result.Title,
            Text = dialog.Result.Text,
            Hotkey = dialog.Result.Hotkey,
            HotkeyAction = dialog.Result.Action
        });
        ClearHotkeyConflicts(items, id, dialog.Result.Hotkey);
        SnippetStore.SaveSnippets(items);
        Reload(id);
        SetStatus("Добавлено.");
    }

    private void Edit()
    {
        var item = Current();
        if (item == null)
        {
            SetStatus("Выбери заготовку для изменения.");
            return;
        }

        var dialog = new SnippetEditorDialog(this, "Изменить", item.Title, item.Text, item.Hotkey, item.HotkeyAction);
        if (dialog.ShowDialog() != true || dialog.Result == null)
            return;
        var items = SnippetStore.Snippets.ToList();
        var row = items.First(i => i.Id == item.Id);
        row.Title = dialog.Result.Title;
        row.Text = dialog.Result.Text;
        row.Hotkey = dialog.Result.Hotkey;
        row.HotkeyAction = dialog.Result.Action;
        ClearHotkeyConflicts(items, row.Id, row.Hotkey);
        SnippetStore.SaveSnippets(items);
        Reload(row.Id);
        SetStatus("Сохранено.");
    }

    private void Delete()
    {
        var item = Current();
        if (item == null)
        {
            SetStatus("Выбери заготовку для удаления.");
            return;
        }

        if (System.Windows.MessageBox.Show(
                "Удалить «" + item.Title + "»?",
                "Шаблоны",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var items = SnippetStore.Snippets.Where(i => i.Id != item.Id).ToList();
        SnippetStore.SaveSnippets(items);
        Reload(items.FirstOrDefault()?.Id ?? 0);
        SetStatus("Удалено.");
    }

    private void Copy(bool paste)
    {
        var item = Current();
        if (item == null)
        {
            SetStatus("Выбери заготовку в списке.");
            return;
        }

        string text = SnippetStore.Expand(item.Text);
        if (paste && _other != IntPtr.Zero)
        {
            SetForegroundWindow(_other);
            SnippetStore.PutClipboard(text, true);
            SetStatus("Вставлено в предыдущее окно.");
            return;
        }

        SnippetStore.PutClipboard(text, false);
        SetStatus("Скопировано в буфер.");
    }

    private void Import()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JSON|*.json|Все файлы|*.*",
            Title = "Импорт шаблонов"
        };
        if (dlg.ShowDialog() != true)
            return;
        try
        {
            var incoming = System.Text.Json.JsonSerializer.Deserialize<List<Snippet>>(File.ReadAllText(dlg.FileName));
            if (incoming == null || incoming.Count == 0)
            {
                SetStatus("В файле нет шаблонов.");
                return;
            }

            var items = SnippetStore.Snippets.ToList();
            int next = items.Count == 0 ? 1 : items.Max(i => i.Id) + 1;
            foreach (var sn in incoming)
            {
                items.Add(new Snippet
                {
                    Id = next++,
                    Title = string.IsNullOrWhiteSpace(sn.Title) ? "Без названия" : sn.Title,
                    Text = sn.Text ?? "",
                    Hotkey = sn.Hotkey ?? "",
                    HotkeyAction = string.Equals(sn.HotkeyAction, "paste", StringComparison.OrdinalIgnoreCase) ? "paste" : "copy"
                });
            }

            SnippetStore.SaveSnippets(items);
            Reload(items[^1].Id);
            SetStatus("Импортировано: " + incoming.Count);
        }
        catch (Exception ex)
        {
            SetStatus("Импорт не вышел: " + ex.Message);
        }
    }

    private void Export()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON|*.json",
            FileName = "snippets.json",
            Title = "Экспорт шаблонов"
        };
        if (dlg.ShowDialog() != true)
            return;
        try
        {
            File.WriteAllText(dlg.FileName, System.Text.Json.JsonSerializer.Serialize(
                SnippetStore.Snippets,
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                }), new UTF8Encoding(false));
            SetStatus("Экспортировано.");
        }
        catch (Exception ex)
        {
            SetStatus("Экспорт не вышел: " + ex.Message);
        }
    }

    private void OpenCommands()
    {
        var win = new ServerCommandsWindow(this);
        win.Show();
    }

    private void OpenServer()
    {
        var win = new ServerSettingsWindow(this);
        win.ShowDialog();
        SetStatus("Настройки сервера сохранены.");
    }

    private void OpenWeb()
    {
        string url = SnippetStore.Server.WebUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            SetStatus("Адрес веб-консоли пустой. Открой «Настройки…».");
            return;
        }

        SnippetStore.OpenUrl(url);
        SetStatus("Консоль открыта в браузере.");
    }

    private void OpenLinks()
    {
        var win = new LinksWindow(this);
        win.Show();
    }

    private void OnListDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(_list);
        _dragFrom = IndexAt(_dragStart);
        _dragging = false;
    }

    private void OnListMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragFrom == null)
            return;
        var pos = e.GetPosition(_list);
        if (!_dragging && (Math.Abs(pos.Y - _dragStart.Y) > 8 || Math.Abs(pos.X - _dragStart.X) > 8))
            _dragging = true;
    }

    private void OnListUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging && _dragFrom is int from)
        {
            int to = IndexAt(e.GetPosition(_list));
            if (to >= 0 && to != from)
                Move(from, to);
        }

        _dragFrom = null;
        _dragging = false;
    }

    private void Move(int fromVisible, int toVisible)
    {
        if (fromVisible < 0 || fromVisible >= _rows.Count || toVisible < 0 || toVisible >= _rows.Count)
            return;
        int fromId = _rows[fromVisible].Id;
        int toId = _rows[toVisible].Id;
        var items = SnippetStore.Snippets.ToList();
        int from = items.FindIndex(i => i.Id == fromId);
        int to = items.FindIndex(i => i.Id == toId);
        if (from < 0 || to < 0)
            return;
        var row = items[from];
        items.RemoveAt(from);
        items.Insert(to, row);
        SnippetStore.SaveSnippets(items);
        Reload(fromId);
        SetStatus("Порядок сохранён.");
    }

    private int IndexAt(Point pos)
    {
        var hit = _list.InputHitTest(pos) as DependencyObject;
        while (hit != null && hit is not ListBoxItem)
            hit = VisualTreeHelper.GetParent(hit);
        if (hit is ListBoxItem item)
            return _list.ItemContainerGenerator.IndexFromContainer(item);
        return -1;
    }

    private Snippet? Current()
    {
        int id = SelectedId();
        return id == 0 ? null : SnippetStore.Snippets.FirstOrDefault(i => i.Id == id);
    }

    private int SelectedId() => _list.SelectedItem is Row row ? row.Id : 0;

    private void Select(int id)
    {
        foreach (var item in _list.Items)
        {
            if (item is Row row && row.Id == id)
            {
                _list.SelectedItem = item;
                _list.ScrollIntoView(item);
                return;
            }
        }

        if (_list.Items.Count > 0)
            _list.SelectedIndex = 0;
    }

    private void SetStatus(string text) => _status.Text = text;

    private static void ClearHotkeyConflicts(List<Snippet> items, int keepId, string hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey))
            return;
        foreach (var item in items)
        {
            if (item.Id != keepId && string.Equals(item.Hotkey, hotkey, StringComparison.OrdinalIgnoreCase))
                item.Hotkey = "";
        }
    }

    private static string Pretty(string hotkey)
    {
        if (!SnippetStore.TryParseChord(hotkey, out bool ctrl, out bool alt, out bool shift, out bool win, out int vk))
            return hotkey;
        return SnippetStore.FormatChord(ctrl, alt, shift, win, vk);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private sealed record Row(int Id, string Label)
    {
        public override string ToString() => Label;
    }
}

sealed class SnippetEditorDialog : Window
{
    public sealed record ResultData(string Title, string Text, string Hotkey, string Action);

    public ResultData? Result { get; private set; }

    private readonly System.Windows.Controls.TextBox _title = TemplateTheme.Field();
    private readonly System.Windows.Controls.TextBox _body = TemplateTheme.Field(true);
    private readonly System.Windows.Controls.TextBox _hotkey = TemplateTheme.Field();
    private readonly System.Windows.Controls.RadioButton _copy = new() { Content = "Только копировать в буфер", Foreground = TemplateTheme.Brush(TemplateTheme.Fg), IsChecked = true, Margin = new Thickness(0, 0, 0, 4) };
    private readonly System.Windows.Controls.RadioButton _paste = new() { Content = "Вставить в активное окно (буфер + Ctrl+V)", Foreground = TemplateTheme.Brush(TemplateTheme.Fg) };

    public SnippetEditorDialog(Window owner, string title, string name, string text, string hotkey, string action)
    {
        Owner = owner;
        Title = title;
        Width = 720;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        TemplateTheme.StyleWindow(this);
        SnippetStore.SuspendHotkeys = true;

        var root = new DockPanel { Margin = new Thickness(16) };
        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        buttons.Children.Add(TemplateTheme.Pill("Отмена", () => { DialogResult = false; Close(); }));
        buttons.Children.Add(TemplateTheme.Pill("Сохранить", Save, accent: true));
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var form = new StackPanel();
        form.Children.Add(Label("Название"));
        _title.Text = name;
        _title.Margin = new Thickness(0, 0, 0, 10);
        form.Children.Add(_title);
        form.Children.Add(Label("Текст"));
        _body.Text = text;
        _body.Height = 260;
        _body.Margin = new Thickness(0, 0, 0, 10);
        form.Children.Add(_body);
        form.Children.Add(Label("Горячая клавиша"));
        _hotkey.Text = Pretty(hotkey);
        _hotkey.Margin = new Thickness(0, 0, 0, 4);
        _hotkey.PreviewKeyDown += Capture;
        form.Children.Add(_hotkey);
        form.Children.Add(new TextBlock
        {
            Text = "Кликни в поле и нажми сочетание. Backspace очищает.",
            Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
            Margin = new Thickness(0, 0, 0, 10)
        });
        bool paste = string.Equals(action, "paste", StringComparison.OrdinalIgnoreCase);
        _paste.IsChecked = paste;
        _copy.IsChecked = !paste;
        form.Children.Add(_copy);
        form.Children.Add(_paste);
        root.Children.Add(form);
        Content = root;
        Closed += (_, _) => SnippetStore.SuspendHotkeys = false;
    }

    private void Save()
    {
        string title = _title.Text.Trim();
        if (title.Length == 0)
            title = "Без названия";
        string hotkey = Normalize(_hotkey.Text);
        if (hotkey.Length > 0 && Reject(hotkey) is string err)
        {
            System.Windows.MessageBox.Show(err, "Шаблоны", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = new ResultData(title, _body.Text, hotkey, _paste.IsChecked == true ? "paste" : "copy");
        DialogResult = true;
        Close();
    }

    private void Capture(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Back or Key.Delete)
        {
            _hotkey.Text = "";
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk <= 0)
            return;
        var mods = Keyboard.Modifiers;
        _hotkey.Text = SnippetStore.FormatChord(
            mods.HasFlag(ModifierKeys.Control),
            mods.HasFlag(ModifierKeys.Alt),
            mods.HasFlag(ModifierKeys.Shift),
            mods.HasFlag(ModifierKeys.Windows),
            vk);
    }

    private static string Normalize(string text)
    {
        if (!SnippetStore.TryParseChord(text, out bool ctrl, out bool alt, out bool shift, out bool win, out int vk))
            return "";
        return SnippetStore.Canonical(ctrl, alt, shift, win, vk);
    }

    private static string Pretty(string hotkey)
    {
        if (!SnippetStore.TryParseChord(hotkey, out bool ctrl, out bool alt, out bool shift, out bool win, out int vk))
            return hotkey ?? "";
        return SnippetStore.FormatChord(ctrl, alt, shift, win, vk);
    }

    private static string? Reject(string canonical)
    {
        if (!SnippetStore.TryParseChord(canonical, out bool ctrl, out bool alt, out bool shift, out bool win, out int vk))
            return "Такое сочетание не разбирается.";
        if (ctrl || alt || shift || win)
            return null;
        var shot = ScreenshotStore.Current;
        if (vk == shot.Hotkey || vk == shot.ClipHotkey || vk == shot.TranslateHotkey)
            return "Эта клавиша уже занята снимком, клипом или переводом.";
        return null;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
        Margin = new Thickness(0, 0, 0, 4)
    };
}

sealed class LinksWindow : Window
{
    private readonly System.Windows.Controls.ListBox _list = TemplateTheme.DarkList();

    public LinksWindow(Window owner)
    {
        Owner = owner;
        Title = "AI и ссылки";
        Width = 520;
        Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        TemplateTheme.StyleWindow(this);
        var root = new DockPanel { Margin = new Thickness(14) };
        var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        top.Children.Add(TemplateTheme.Pill("Открыть", Open, accent: true));
        top.Children.Add(TemplateTheme.Pill("Закрыть", Close));
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        _list.MouseDoubleClick += (_, _) => Open();
        foreach (var link in SnippetStore.Links)
        {
            string prefix = string.IsNullOrWhiteSpace(link.Category) ? "" : link.Category + "  ·  ";
            _list.Items.Add(new Item(link.Id, prefix + link.Title));
        }

        if (_list.Items.Count > 0)
            _list.SelectedIndex = 0;
        root.Children.Add(_list);
        Content = root;
    }

    private void Open()
    {
        if (_list.SelectedItem is not Item item)
            return;
        var link = SnippetStore.Links.FirstOrDefault(l => l.Id == item.Id);
        if (link == null || string.IsNullOrWhiteSpace(link.Url))
            return;
        SnippetStore.OpenUrl(link.Url);
    }

    private sealed record Item(int Id, string Label)
    {
        public override string ToString() => Label;
    }
}

sealed class ServerCommandsWindow : Window
{
    private readonly System.Windows.Controls.ListBox _list = TemplateTheme.DarkList();
    private readonly TextBlock _out = new()
    {
        Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 8, 0, 0)
    };

    public ServerCommandsWindow(Window owner)
    {
        Owner = owner;
        Title = "CMD";
        Width = 480;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        TemplateTheme.StyleWindow(this);
        var root = new DockPanel { Margin = new Thickness(14) };
        var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        top.Children.Add(TemplateTheme.Pill("Выполнить", () => _ = Run(), accent: true));
        top.Children.Add(TemplateTheme.Pill("Настройки RCON…", () =>
        {
            new ServerSettingsWindow(this).ShowDialog();
        }));
        top.Children.Add(TemplateTheme.Pill("Закрыть", Close));
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        DockPanel.SetDock(_out, Dock.Bottom);
        root.Children.Add(_out);
        foreach (var cmd in SnippetStore.Server.Commands)
            _list.Items.Add(new Item(cmd.Id, cmd.Title));
        if (_list.Items.Count > 0)
            _list.SelectedIndex = 0;
        _list.MouseDoubleClick += async (_, _) => await Run();
        root.Children.Add(_list);
        Content = root;
    }

    private async Task Run()
    {
        if (_list.SelectedItem is not Item item)
            return;
        var cmd = SnippetStore.Server.Commands.FirstOrDefault(c => c.Id == item.Id);
        if (cmd == null)
            return;
        var server = SnippetStore.Server;
        _out.Text = "Команда уходит…";
        var (ok, text) = await RconClient.ExecuteAsync(server.Host, server.Port, server.Password, cmd.Command);
        _out.Text = text;
        _ = ok;
    }

    private sealed record Item(int Id, string Label)
    {
        public override string ToString() => Label;
    }
}

sealed class ServerSettingsWindow : Window
{
    private readonly System.Windows.Controls.TextBox _host = TemplateTheme.Field();
    private readonly System.Windows.Controls.TextBox _port = TemplateTheme.Field();
    private readonly PasswordBox _password = new()
    {
        Background = TemplateTheme.Brush(TemplateTheme.ListBg),
        Foreground = TemplateTheme.Brush(TemplateTheme.Fg),
        BorderBrush = TemplateTheme.Brush(TemplateTheme.Border),
        Padding = new Thickness(10, 7, 10, 7)
    };
    private readonly System.Windows.Controls.TextBox _web = TemplateTheme.Field();
    private readonly System.Windows.Controls.TextBox _hotkey = TemplateTheme.Field();
    private readonly System.Windows.Controls.TextBox _commands = TemplateTheme.Field(true);

    public ServerSettingsWindow(Window owner)
    {
        Owner = owner;
        Title = "Настройки";
        Width = 560;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        TemplateTheme.StyleWindow(this);
        SnippetStore.SuspendHotkeys = true;

        var server = SnippetStore.Server;
        _host.Text = server.Host;
        _port.Text = server.Port.ToString();
        _password.Password = server.Password;
        _web.Text = server.WebUrl;
        _hotkey.Text = Pretty(server.WindowHotkey);
        _hotkey.PreviewKeyDown += Capture;
        _commands.Height = 160;
        _commands.Text = string.Join("\n", server.Commands.Select(c => c.Title + " | " + c.Command));

        var root = new DockPanel { Margin = new Thickness(16) };
        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        buttons.Children.Add(TemplateTheme.Pill("Отмена", () => { DialogResult = false; Close(); }));
        buttons.Children.Add(TemplateTheme.Pill("Сохранить", Save, accent: true));
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var form = new StackPanel();
        form.Children.Add(Label("Хост RCON"));
        form.Children.Add(_host);
        form.Children.Add(Label("Порт"));
        _port.Margin = new Thickness(0, 0, 0, 8);
        _host.Margin = new Thickness(0, 0, 0, 8);
        form.Children.Add(_port);
        form.Children.Add(Label("Пароль"));
        _password.Margin = new Thickness(0, 0, 0, 8);
        form.Children.Add(_password);
        form.Children.Add(Label("Веб-консоль"));
        _web.Margin = new Thickness(0, 0, 0, 8);
        form.Children.Add(_web);
        form.Children.Add(Label("Клавиша окна шаблонов"));
        _hotkey.Margin = new Thickness(0, 0, 0, 8);
        form.Children.Add(_hotkey);
        form.Children.Add(Label("Команды CMD (строка: название | команда)"));
        form.Children.Add(_commands);
        root.Children.Add(form);
        Content = root;
        Closed += (_, _) => SnippetStore.SuspendHotkeys = false;
    }

    private void Save()
    {
        var server = SnippetStore.Server;
        server.Host = _host.Text.Trim();
        server.Port = int.TryParse(_port.Text.Trim(), out int port) ? port : server.Port;
        server.Password = _password.Password;
        server.WebUrl = _web.Text.Trim();
        string hotkey = Normalize(_hotkey.Text);
        server.WindowHotkey = hotkey.Length == 0 ? "numdel" : hotkey;
        var cmds = new List<ServerCommand>();
        int id = 1;
        foreach (string line in (_commands.Text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            string t = line.Trim();
            if (t.Length == 0)
                continue;
            int sep = t.IndexOf('|');
            string title = sep < 0 ? t : t[..sep].Trim();
            string command = sep < 0 ? t : t[(sep + 1)..].Trim();
            cmds.Add(new ServerCommand { Id = id++, Title = title, Command = command });
        }

        server.Commands = cmds;
        SnippetStore.SaveServer(server);
        DialogResult = true;
        Close();
    }

    private void Capture(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Back or Key.Delete)
        {
            _hotkey.Text = "";
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk <= 0)
            return;
        var mods = Keyboard.Modifiers;
        _hotkey.Text = SnippetStore.FormatChord(
            mods.HasFlag(ModifierKeys.Control),
            mods.HasFlag(ModifierKeys.Alt),
            mods.HasFlag(ModifierKeys.Shift),
            mods.HasFlag(ModifierKeys.Windows),
            vk);
    }

    private static string Normalize(string text)
    {
        if (!SnippetStore.TryParseChord(text, out bool ctrl, out bool alt, out bool shift, out bool win, out int vk))
            return "";
        return SnippetStore.Canonical(ctrl, alt, shift, win, vk);
    }

    private static string Pretty(string hotkey)
    {
        if (!SnippetStore.TryParseChord(hotkey, out bool ctrl, out bool alt, out bool shift, out bool win, out int vk))
            return hotkey ?? "";
        return SnippetStore.FormatChord(ctrl, alt, shift, win, vk);
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
        Margin = new Thickness(0, 0, 0, 4)
    };
}
