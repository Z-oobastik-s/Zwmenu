using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Zwmenu.Models;
using Zwmenu.Services;

namespace Zwmenu.Settings;

public partial class SettingsWindow: Window
{
    private readonly ObservableCollection<MenuAction> _items = new();
    private ICollectionView? _itemView;
    private string _itemQuery = "";
    private MenuAction? _selected;
    private bool _suspend;
    private bool _dirty;
    private string? _assignWhich;

    public string ConfigPath => MenuStore.FilePath;

    public event EventHandler? Saved;

    public SettingsWindow()
    {
        // Пока грузится XAML, ComboBox шлёт SelectionChanged: Edited/ChildList ещё null.
        _suspend = true;
        InitializeComponent();
        AppChrome.ApplyWindowIcon(this);
        _itemView = CollectionViewSource.GetDefaultView(_items);
        _itemView.Filter = ItemVisible;
        ItemList.ItemsSource = _itemView;
        BuildPickers();
        LoadFromDisk();
        AutostartBox.IsChecked = AutostartService.IsEnabled();
        AppearanceStore.Reload();
        FillMiddleClickModeCombo();
        FillDictation();
        FillRingSkinCombo();
        InitRingLooks();
        FillScreenshot();
        FillTranslateHotkey();
        FillShelf();
        _suspend = false;
        ShowPage("wheel");
        SourceInitialized += (_, _) => OpenMaximized();
        PreviewKeyDown += OnSettingsPreviewKeyDown;
        PreviewMouseDown += OnSettingsPreviewMouseDown;
    }

    private void OpenMaximized()
    {
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget != null)
        {
            var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
            var corner = src.CompositionTarget.TransformFromDevice.Transform(
                new System.Windows.Point(area.Left + 24, area.Top + 24));
            WindowState = WindowState.Normal;
            Left = corner.X;
            Top = corner.Y;
        }

        WindowState = WindowState.Maximized;
    }

    private string _behSection = "voice";

    private void OnBehSection(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
            ShowBeh(tag);
    }

    private void ShowBeh(string tag)
    {
        _behSection = tag is "run" or "look" or "voice" ? tag : "voice";
        BehRun.Visibility = _behSection == "run" ? Visibility.Visible : Visibility.Collapsed;
        BehVoice.Visibility = _behSection == "voice" ? Visibility.Visible : Visibility.Collapsed;
        BehLook.Visibility = _behSection == "look" ? Visibility.Visible : Visibility.Collapsed;
        PaintNav(BehNavRun, _behSection == "run");
        PaintNav(BehNavVoice, _behSection == "voice");
        PaintNav(BehNavLook, _behSection == "look");
    }

    private void OnNavWheel(object sender, RoutedEventArgs e) => ShowPage("wheel");

    private void OnNavBehavior(object sender, RoutedEventArgs e) => ShowPage("behavior");

    private void OnNavShot(object sender, RoutedEventArgs e) => ShowPage("shot");

    private void OnNavShelf(object sender, RoutedEventArgs e) => ShowPage("shelf");

    private void ShowPage(string page)
    {
        if (_assignWhich is "shot" or "clip" && page != "shot")
            StopHotkeyAssign();
        else if ((_assignWhich is "translate" or "dictation") && page != "behavior")
            StopHotkeyAssign();

        PageWheel.Visibility = page == "wheel" ? Visibility.Visible : Visibility.Collapsed;
        PageBehavior.Visibility = page == "behavior" ? Visibility.Visible : Visibility.Collapsed;
        PageShot.Visibility = page == "shot" ? Visibility.Visible : Visibility.Collapsed;
        PageShelf.Visibility = page == "shelf" ? Visibility.Visible : Visibility.Collapsed;
        ConfigPathText.Visibility = page == "wheel" ? Visibility.Visible : Visibility.Collapsed;
        RevealFileButton.Visibility = page == "wheel" ? Visibility.Visible : Visibility.Collapsed;

        PageHint.Text = page switch
        {
            "behavior" => "Запуск, диктовка и оформление — по отдельным разделам.",
            "shot" => "Клавиша снимка, уведомления и файл на рабочем столе.",
            "shelf" => "Вид полки. Всё ниже применяется сразу, кнопку «Сохранить» для неё жать не нужно.",
            _ => "Пункты колеса. Первый — в центре, остальные сверху по часовой."
        };
        if (page == "behavior")
            ShowBeh(_behSection);

        PaintNav(NavWheel, page == "wheel");
        PaintNav(NavBehavior, page == "behavior");
        PaintNav(NavShot, page == "shot");
        PaintNav(NavShelf, page == "shelf");
    }

    private static void PaintNav(Button button, bool on)
    {
        button.Background = new SolidColorBrush(on
            ? Color.FromRgb(0x3E, 0x5A, 0x6E)
            : Color.FromRgb(0x2A, 0x2A, 0x2E));
        button.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void OnItemSearchChanged(object sender, TextChangedEventArgs e)
    {
        _itemQuery = ItemSearchBox.Text.Trim();
        bool typing = ItemSearchBox.Text.Length > 0;
        SearchWatermark.Visibility = typing ? Visibility.Collapsed : Visibility.Visible;
        SearchClear.Visibility = typing ? Visibility.Visible : Visibility.Collapsed;
        _itemView?.Refresh();
        bool any = _itemView is { IsEmpty: false };
        SearchEmpty.Visibility = _itemQuery.Length > 0 && !any ? Visibility.Visible : Visibility.Collapsed;
        if (ItemList.SelectedItem == null && any)
            ItemList.SelectedIndex = 0;
    }

    private void OnSearchClear(object sender, RoutedEventArgs e)
    {
        ItemSearchBox.Text = "";
        ItemSearchBox.Focus();
    }

    private bool ItemVisible(object obj) => obj is MenuAction action && ItemMatches(action);

    private bool ItemMatches(MenuAction action)
    {
        if (_itemQuery.Length == 0)
            return true;
        if (FieldHas(action))
            return true;
        foreach (var child in action.Children)
        {
            if (FieldHas(child))
                return true;
        }

        return false;
    }

    private bool FieldHas(MenuAction action) =>
        Contains(action.Label)
        || Contains(action.Description)
        || Contains(action.Value)
        || Contains(action.Role)
        || Contains(action.ExtraLabel);

    private bool Contains(string? text) =>
        !string.IsNullOrEmpty(text)
        && text.Contains(_itemQuery, StringComparison.CurrentCultureIgnoreCase);

    private void OnSettingsPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_assignWhich != "dictation")
            return;
        if (e.ChangedButton is not (MouseButton.XButton1 or MouseButton.XButton2))
            return;

        e.Handled = true;
        byte side = e.ChangedButton == MouseButton.XButton1 ? (byte)1 : (byte)2;
        StopHotkeyAssign();
        SelectDictationSide(side);
    }

    private void OnSettingsPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_assignWhich != null)
        {
            e.Handled = true;
            if (e.IsRepeat)
                return;
            if (Keyboard.Modifiers != ModifierKeys.None)
                return;
            if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
                return;
            if (e.Key == Key.Escape)
            {
                StopHotkeyAssign();
                return;
            }

            int vk = KeyInterop.VirtualKeyFromKey(e.Key);
            if (!ScreenshotStore.IsAllowedHotkey(vk))
                return;

            string which = _assignWhich;
            StopHotkeyAssign();
            if (which == "clip")
                SelectClipHotkey(vk);
            else if (which == "translate")
                SelectTranslateHotkey(vk);
            else if (which == "dictation")
                SelectDictationHotkey(vk);
            else
                SelectHotkey(vk);
            return;
        }

        if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            OnSaveClick(this, new RoutedEventArgs());
        }
    }

    private void LoadFromDisk()
    {
        var result = MenuStore.Load(prepareIcons: false);
        _items.Clear();
        foreach (var item in result.Items)
            _items.Add(item.Clone());
        MenuStore.Renumber(_items);
        _dirty = false;
        ItemList.SelectedIndex = _items.Count > 0 ? 0: -1;
        FooterStatus.Text = result.Warning ?? "Изменения записываются в файл и сразу попадают на колесо.";
        FooterStatus.Foreground = result.Warning == null
            ? new SolidColorBrush(Color.FromArgb(0xBF, 0xF5, 0xF0, 0xE8))
           : new SolidColorBrush(Color.FromRgb(0xD4, 0xA1, 0x9A));
    }

    private void BuildPickers()
    {
        foreach (var glyph in Catalog.Glyphs)
        {
            var btn = new Button
            {
                Width = 36,
                Height = 36,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(0),
                Content = new TextBlock
                {
                    Text = glyph.Glyph,
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 15,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                },
                ToolTip = glyph.Name,
                Tag = glyph.Glyph
            };
            btn.Click += (_, _) =>
            {
                if (Edited == null) return;
                Edited.Glyph = glyph.Glyph;
                MarkDirty();
                RefreshPreview();
            };
            IconHost.Children.Add(btn);
        }

        foreach (var tint in Catalog.Tints)
        {
            var btn = new Button
            {
                Width = 32,
                Height = 32,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(0),
                ToolTip = tint.Name,
                Tag = tint.Hex,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Content = new Border
                {
                    Width = 26,
                    Height = 26,
                    CornerRadius = new CornerRadius(13),
                    Background = (Brush)new BrushConverter().ConvertFromString(tint.Hex)!,
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xF5, 0xF0, 0xE8)),
                    BorderThickness = new Thickness(1)
                }
            };
            btn.Click += (_, _) =>
            {
                if (Edited == null) return;
                Edited.Tint = tint.Hex;
                MarkDirty();
                RefreshPreview();
            };
            TintHost.Children.Add(btn);
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _suspend = true;
        ChildList.SelectedIndex = -1;
        _suspend = false;

        _selected = ItemList.SelectedItem as MenuAction;
        ChildList.ItemsSource = _selected?.Children;
        Editor.IsEnabled = _selected != null;
        PushToForm();
    }

    private MenuAction? Edited => ChildList?.SelectedItem as MenuAction ?? _selected;

    private void OnChildSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suspend)
            return;
        PushToForm();
    }

    private void PushToForm()
    {
        _suspend = true;
        try
        {
            var edited = Edited;
            if (edited == null)
            {
                LabelBox.Text = "";
                DescriptionBox.Text = "";
                PathBox.Text = "";
                ArgsBox.Text = "";
                PlaceText.Text = "Выбери пункт слева";
                PathState.Text = "";
                return;
            }

            LabelBox.Text = edited.Label;
            DescriptionBox.Text = edited.Description;
            PathBox.Text = edited.Value;
            ArgsBox.Text = edited.Arguments;
            if (RunAsAdminBox != null)
                RunAsAdminBox.IsChecked = edited.RunAsAdmin;
            KindBox.SelectedIndex = edited.Kind switch
            {
                ActionKind.None => 0,
                ActionKind.Keys => 2,
                ActionKind.Group => 3,
                _ => 1
            };
            if (GroupLayoutBox != null && _selected != null)
            {
                // Layout всегда от родительской группы, даже когда правим ребёнка.
                GroupLayoutBox.SelectedIndex = _selected.IsProcessGroup ? 2
                   : _selected.IsFoldersGroup ? 1: 0;
            }
            ApplyKindUi(edited.Kind);
            bool child = !ReferenceEquals(edited, _selected);
            bool showChildren = _selected?.Kind == ActionKind.Group;
            if (ChildrenPanel != null)
                ChildrenPanel.Visibility = showChildren ? Visibility.Visible: Visibility.Collapsed;
            if (GroupLayoutPanel != null)
                GroupLayoutPanel.Visibility = showChildren && !child ? Visibility.Visible: Visibility.Collapsed;
            if (ChildEditBanner != null)
            {
                ChildEditBanner.Visibility = child ? Visibility.Visible: Visibility.Collapsed;
                if (child && ChildEditText != null)
                    ChildEditText.Text =
                        $"Пункт «{edited.Label}» внутри «{_selected?.Label}». Список группы ниже: добавляй/удаляй без прыжков.";
            }
            if (KindBox != null)
                KindBox.IsEnabled = true;
            PlaceText.Text = child
                ? $"Внутри группы «{_selected?.Label}»"
               : edited.Tag == 0
                    ? "Центр колеса"
                   : edited.Kind == ActionKind.Group
                        ? $"Группа: место {edited.Tag}"
                       : $"По кругу: место {edited.Tag}";
            RefreshPreview();
            RefreshPathState();
        }
        finally
        {
            _suspend = false;
        }
    }

    private void FlushEditorToModel()
    {
        if (Edited == null || LabelBox == null)
            return;

        Edited.Label = LabelBox.Text;
        Edited.Description = DescriptionBox.Text;
        string newValue = PathBox.Text.Trim();
        bool pathChanged = !string.Equals(Edited.Value, newValue, StringComparison.OrdinalIgnoreCase);
        Edited.Value = newValue;
        Edited.Arguments = ArgsBox.Text.Trim();
        if (RunAsAdminBox != null)
            Edited.RunAsAdmin = RunAsAdminBox.IsChecked == true;

        bool child = !ReferenceEquals(Edited, _selected);
        if (KindBox != null)
        {
            var kind = KindBox.SelectedIndex switch
            {
                0 => ActionKind.None,
                2 => ActionKind.Keys,
                3 => ActionKind.Group,
                _ => ActionKind.Launch
            };
            // Внутри группы: только запуск или клавиши (не ещё одна группа).
            if (child && kind is ActionKind.None or ActionKind.Group)
                kind = ActionKind.Launch;
            Edited.Kind = kind;
        }

        if (pathChanged && Edited.Kind == ActionKind.Launch)
        {
            Edited.IconPath = "";
            AppIconService.EnsureIcon(Edited);
            RefreshPreview();
        }
    }

    private void OnFieldChanged(object sender, TextChangedEventArgs e)
    {
        if (_suspend || Edited == null)
            return;

        FlushEditorToModel();
        MarkDirty();
        RefreshPathState();
    }

    private void OnRunAsAdminClick(object sender, RoutedEventArgs e)
    {
        if (_suspend || Edited == null)
            return;
        Edited.RunAsAdmin = RunAsAdminBox.IsChecked == true;
        MarkDirty();
        RefreshPathState();
    }

    private void OnBackToGroup(object sender, RoutedEventArgs e)
    {
        if (_selected == null)
            return;
        _suspend = true;
        ChildList.SelectedIndex = -1;
        _suspend = false;
        PushToForm();
    }

    private void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspend || Edited == null || LaunchFields == null)
            return;

        bool child = !ReferenceEquals(Edited, _selected);
        var kind = KindBox.SelectedIndex switch
        {
            0 => ActionKind.None,
            2 => ActionKind.Keys,
            3 => ActionKind.Group,
            _ => ActionKind.Launch
        };
        if (child && kind is ActionKind.None or ActionKind.Group)
            kind = ActionKind.Launch;

        Edited.Kind = kind;
        if (Edited.Kind == ActionKind.Group)
        {
            Edited.Value = "";
            Edited.Arguments = "";
            if (string.IsNullOrWhiteSpace(Edited.GroupLayout))
                Edited.GroupLayout = "apps";
            if (GroupLayoutBox != null)
                GroupLayoutBox.SelectedIndex = Edited.IsFoldersGroup ? 1: 0;
            if (string.IsNullOrWhiteSpace(Edited.Description)
                || Edited.Description.StartsWith("Открывает ", StringComparison.Ordinal)
                || Edited.Description.StartsWith("Запускает ", StringComparison.Ordinal))
            {
                Edited.Description = "Наведи или кликни: выедут добавленные пункты.";
                DescriptionBox.Text = Edited.Description;
            }
        }
        else if (Edited.Kind == ActionKind.Keys
                 && (string.IsNullOrWhiteSpace(Edited.Value) || Edited.Value.Contains('\\')))
        {
            Edited.Value = "Ctrl+W";
            PathBox.Text = Edited.Value;
        }

        ApplyKindUi(Edited.Kind);
        MarkDirty();
        RefreshPathState();
    }

    private void ApplyKindUi(ActionKind kind)
    {
        if (LaunchFields == null)
            return;
        bool showPath = kind is ActionKind.Launch or ActionKind.Keys;
        LaunchFields.Visibility = showPath ? Visibility.Visible : Visibility.Collapsed;
        bool launchExtras = kind == ActionKind.Launch;
        if (LaunchExtraFields != null)
            LaunchExtraFields.Visibility = launchExtras ? Visibility.Visible : Visibility.Collapsed;
        if (BrowseFolderBtn != null)
            BrowseFolderBtn.Visibility = launchExtras ? Visibility.Visible : Visibility.Collapsed;
        if (BrowseFileBtn != null)
            BrowseFileBtn.Visibility = launchExtras ? Visibility.Visible : Visibility.Collapsed;
        if (PathLabel != null)
            PathLabel.Text = kind == ActionKind.Keys ? "Сочетание" : "Путь";
        if (PathHint != null)
        {
            PathHint.Text = kind == ActionKind.Keys
                ? "Формат: Ctrl+W, Ctrl+Shift+T, Win+E, Alt+F4. Сработает в активном окне."
                : "Папка → проводник. .exe / .lnk → запуск. https://сайт → браузер. Путь с диска D тоже ок.";
        }
    }

    private void OnGroupLayoutChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspend || _selected == null || GroupLayoutBox == null)
            return;
        if (_selected.Kind != ActionKind.Group)
            return;

        _selected.GroupLayout = GroupLayoutBox.SelectedIndex switch
        {
            1 => "folders",
            2 => "processes",
            _ => "apps"
        };
        _selected.IconPath = "";
        AppIconService.EnsureIcons(new[] { _selected });
        MarkDirty();
        if (ReferenceEquals(Edited, _selected))
        {
            RefreshPreview();
            RefreshPathState();
        }
    }

    private void RefreshPreview()
    {
        if (Edited == null)
            return;

        try
        {
            PreviewDisc.Background = Edited.HasFileIcon
                ? new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x2C))
               : (Brush)new BrushConverter().ConvertFromString(Edited.Tint)!;
        }
        catch
        {
            PreviewDisc.Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3E));
        }

        if (Edited.HasFileIcon)
        {
            try
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(Edited.IconPath, UriKind.Absolute);
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 40;
                bitmap.EndInit();
                bitmap.Freeze();
                PreviewDisc.Child = new System.Windows.Controls.Image
                {
                    Source = bitmap,
                    Width = 40,
                    Height = 40,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                };
                return;
            }
            catch
            {
                // glyph fallback
            }
        }

        PreviewDisc.Child = new TextBlock
        {
            Text = Edited.Glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 22,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0xF4, 0xEE)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
    }

    private void RefreshPathState()
    {
        if (Edited == null)
            return;

        if (Edited.Kind == ActionKind.None)
        {
            PathState.Text = "Ничего не открывает: только закрывает колесо.";
            PathState.Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xF5, 0xF0, 0xE8));
            return;
        }

        if (Edited.Kind == ActionKind.Group && ReferenceEquals(Edited, _selected))
        {
            string mode = Edited.IsProcessGroup ? "процессы"
               : Edited.IsFoldersGroup ? "сетка папок": "список программ";
            PathState.Text = Edited.IsProcessGroup
                ? "Группа процессов: наведение показывает список, клик по иконке открывает диспетчер."
               : Edited.Children.Count == 0
                    ? $"Группа ({mode}): добавь пункты ниже, они выедут при наведении."
                   : $"Группа ({mode}): выедет {Edited.Children.Count} пунктов. Сама кнопка ничего не запускает.";
            PathState.Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xC5, 0xA0));
            return;
        }

        if (string.IsNullOrWhiteSpace(Edited.Value))
        {
            PathState.Text = "Путь пустой. Укажи папку или файл, иначе кнопка ничего не откроет.";
            PathState.Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xA1, 0x9A));
            return;
        }

        if (Edited.PathMissing)
        {
            PathState.Text = Edited.RunAsAdmin
                ? "Путь не найден. Сними галочку админа или укажи актуальный файл."
               : "По этому пути сейчас ничего нет. Сохранить можно: заработает, когда файл появится.";
            PathState.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xC0, 0x78));
            return;
        }

        string baseState = MenuAction.IsWebAddress(Edited.Value) || MenuAction.IsProtocolTarget(Edited.Value)
            ? "Системная ссылка: откроется через Windows."
           : MenuAction.IsShellAppTarget(Edited.Value)
                ? "Приложение из Пуска (Store)."
               : Directory.Exists(Edited.Value)
                    ? "Папка на месте."
                   : File.Exists(Edited.Value)
                        ? "Файл на месте."
                       : "Имя программы: Windows поищет его в системе.";
        if (Edited.RunAsAdmin)
            baseState += " Запуск от администратора (UAC).";
        PathState.Text = baseState;
        PathState.Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xC5, 0xA0));
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (_items.Count >= MenuStore.MaxItems)
        {
            Note("На колесе помещается центр и ещё 16 пунктов.");
            return;
        }

        if (ItemSearchBox.Text.Length > 0)
            ItemSearchBox.Text = "";

        var item = new MenuAction
        {
            Label = "Новый пункт",
            Description = "Открывает выбранную папку или программу.",
            Kind = ActionKind.Launch,
            Glyph = "\uE8B7",
            Tint = "#FF3E4E62"
        };
        _items.Add(item);
        MenuStore.Renumber(_items);
        ItemList.SelectedItem = item;
        MarkDirty();
        PathBox.Focus();
    }

    private void OnDuplicate(object sender, RoutedEventArgs e)
    {
        if (_selected == null)
            return;
        if (_items.Count >= MenuStore.MaxItems)
        {
            Note("На колесе помещается центр и ещё 16 пунктов.");
            return;
        }

        if (ItemSearchBox.Text.Length > 0)
            ItemSearchBox.Text = "";

        var copy = _selected.Clone();
        copy.Label = _selected.Label + ": копия";
        int index = _items.IndexOf(_selected);
        _items.Insert(index + 1, copy);
        MenuStore.Renumber(_items);
        ItemList.SelectedItem = copy;
        MarkDirty();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_selected == null)
            return;
        if (_items.Count <= 1)
        {
            Note("Последний пункт убрать нельзя: колесу нужен хотя бы центр.");
            return;
        }

        var answer = System.Windows.MessageBox.Show(this,
            $"Убрать «{_selected.Label}» с колеса?",
            "Zwmenu",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;

        int index = _items.IndexOf(_selected);
        _items.RemoveAt(index);
        MenuStore.Renumber(_items);
        _itemView?.Refresh();
        MenuAction? pick = null;
        for (int i = Math.Min(index, _items.Count - 1); i >= 0; i--)
        {
            if (ItemMatches(_items[i]))
            {
                pick = _items[i];
                break;
            }
        }

        pick ??= _items.FirstOrDefault(ItemMatches);
        ItemList.SelectedItem = pick;
        MarkDirty();
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int dir)
    {
        if (_selected == null)
            return;
        int index = _items.IndexOf(_selected);
        int next = index + dir;
        if (next < 0 || next >= _items.Count)
            return;

        _items.Move(index, next);
        MenuStore.Renumber(_items);
        ItemList.SelectedIndex = next;
        PushToForm();
        MarkDirty();
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        var answer = System.Windows.MessageBox.Show(this,
            "Вернуть стандартные пункты? Текущий список в окне будет заменён. На диск это попадёт только после «Сохранить».",
            "Zwmenu",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;

        _items.Clear();
        foreach (var item in MenuAction.CreateDefaults())
            _items.Add(item);
        MenuStore.Renumber(_items);
        ItemList.SelectedIndex = 0;
        MarkDirty();
    }

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        if (Edited == null)
            return;

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Папка, которую откроет этот пункт",
            Multiselect = false
        };
        if (Directory.Exists(Edited.Value))
            dialog.InitialDirectory = Edited.Value;

        if (dialog.ShowDialog(this) != true)
            return;

        ApplyPickedPath(dialog.FolderName, isFolder: true);
    }

    private void OnBrowseFile(object sender, RoutedEventArgs e)
    {
        if (Edited == null)
            return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Программа, ярлык или файл",
            Filter = "Программы и ярлыки (*.exe;*.lnk)|*.exe;*.lnk|Все файлы (*.*)|*.*",
            CheckFileExists = true
        };
        if (File.Exists(Edited.Value))
            dialog.InitialDirectory = Path.GetDirectoryName(Edited.Value);

        if (dialog.ShowDialog(this) != true)
            return;

        ApplyPickedPath(dialog.FileName, isFolder: false);
    }

    private void ApplyPickedPath(string path, bool isFolder)
    {
        var target = Edited;
        if (target == null)
            return;

        string name = isFolder
            ? Path.GetFileName(path.TrimEnd('\\', '/'))
           : Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(name))
            name = path;

        bool placeholder = string.IsNullOrWhiteSpace(target.Label)
            || target.Label is "Новый пункт" or "Без названия";
        if (placeholder)
            target.Label = name;

        bool autoText = string.IsNullOrWhiteSpace(target.Description)
            || target.Description.StartsWith("Открывает ", StringComparison.Ordinal)
            || target.Description.StartsWith("Запускает ", StringComparison.Ordinal);
        if (autoText)
        {
            string ext = Path.GetExtension(path);
            bool program = ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase);
            target.Description = isFolder
                ? $"Открывает папку «{name}»."
               : program
                    ? $"Запускает «{name}»."
                   : $"Открывает «{name}».";
        }

        target.Value = path;
        target.Kind = ActionKind.Launch;
        target.IconPath = "";
        AppIconService.EnsureIcon(target);
        PushToForm();
        MarkDirty();
    }

    private void OnTest(object sender, RoutedEventArgs e)
    {
        if (Edited == null)
            return;
        if (Edited.Kind == ActionKind.None)
        {
            Note("Этот пункт только закрывает колесо: проверять нечего.");
            return;
        }

        string? error = null;
        void OnFailed(object? _, string message) => error = message;
        ActionDispatcher.ActionFailed += OnFailed;
        try
        {
            ActionDispatcher.Execute(Edited);
        }
        finally
        {
            ActionDispatcher.ActionFailed -= OnFailed;
        }

        Note(error ?? $"«{Edited.Label}» открыто.");
    }

    private void OnAddChild(object sender, RoutedEventArgs e)
    {
        if (_selected == null)
            return;
        if (_selected.Children.Count >= MenuStore.MaxChildren)
        {
            Note($"У одного пункта может выезжать не больше {MenuStore.MaxChildren}.");
            return;
        }

        var child = new MenuAction
        {
            Label = "Новый пункт",
            Description = "Открывает выбранную папку или программу.",
            Kind = ActionKind.Launch,
            Glyph = "\uE8B7",
            Tint = _selected.Tint
        };
        _selected.Children.Add(child);
        ChildList.SelectedItem = child;
        MarkDirty();
        PathBox.Focus();
    }

    private void OnRemoveChild(object sender, RoutedEventArgs e)
    {
        if (_selected == null || ChildList.SelectedItem is not MenuAction child)
            return;

        int index = _selected.Children.IndexOf(child);
        _selected.Children.Remove(child);
        if (_selected.Children.Count > 0)
            ChildList.SelectedIndex = Math.Min(index, _selected.Children.Count - 1);
        MarkDirty();
    }

    private void OnChildUp(object sender, RoutedEventArgs e) => MoveChild(-1);

    private void OnChildDown(object sender, RoutedEventArgs e) => MoveChild(1);

    private void MoveChild(int dir)
    {
        if (_selected == null || ChildList.SelectedItem is not MenuAction child)
            return;
        int index = _selected.Children.IndexOf(child);
        int next = index + dir;
        if (next < 0 || next >= _selected.Children.Count)
            return;

        _selected.Children.Move(index, next);
        ChildList.SelectedIndex = next;
        MarkDirty();
    }

    private void OnReveal(object sender, RoutedEventArgs e)
    {
        try
        {
            MenuStore.RevealFile();
        }
        catch (Exception ex)
        {
            Note("Не удалось открыть папку: " + ex.Message);
        }
    }

    private void OnAutostartClick(object sender, RoutedEventArgs e)
    {
        if (_suspend) return;
        bool want = AutostartBox.IsChecked == true;
        if (!AutostartService.SetEnabled(want))
        {
            _suspend = true;
            AutostartBox.IsChecked = AutostartService.IsEnabled();
            _suspend = false;
            Note("Не удалось изменить автозапуск. Проверь права Windows.");
            return;
        }

        Note(want
            ? "Автозапуск включён: Zwmenu стартует вместе с Windows."
            : "Автозапуск выключен.");
    }

    private void FillMiddleClickModeCombo()
    {
        var items = new List<MiddleClickModeItem>
        {
            new("hold", "Удержание: меню / короткий клик: как в Windows"),
            new("instant", "Сразу открывать меню (перехватывать СКМ)"),
            new("alt", "Меню только по Alt + СКМ")
        };
        MiddleClickModeCombo.DisplayMemberPath = nameof(MiddleClickModeItem.Title);
        MiddleClickModeCombo.ItemsSource = items;
        string cur = AppearanceStore.GetMiddleClickMode() switch
        {
            MiddleButtonMode.Instant => "instant",
            MiddleButtonMode.Alt => "alt",
            _ => "hold"
        };
        int idx = items.FindIndex(i => i.Id == cur);
        MiddleClickModeCombo.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void OnMiddleClickModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspend) return;
        if (MiddleClickModeCombo.SelectedItem is not MiddleClickModeItem pick)
            return;

        var s = AppearanceStore.Current;
        s.MiddleClickMode = pick.Id;
        AppearanceStore.Save(s);
        Note(pick.Id switch
        {
            "instant" => "СКМ сразу открывает колесо (как раньше).",
            "alt" => "Колесо по Alt + СКМ. Обычный СКМ свободен для браузера.",
            _ => "Короткий СКМ: как в Windows. Удержание: колесо Zwmenu."
        });
    }

    private void FillDictation()
    {
        var s = ScreenshotStore.Current;
        var sideItems = new List<MiddleClickModeItem>
        {
            new("1", "Боковая «назад» (X1) — зажал и говори"),
            new("2", "Боковая «вперёд» (X2) — зажал и говори"),
            new("0", "Не использовать мышь")
        };
        DictationSideCombo.DisplayMemberPath = nameof(MiddleClickModeItem.Title);
        DictationSideCombo.ItemsSource = sideItems;
        string cur = s.DictationSideButton switch
        {
            2 => "2",
            0 => "0",
            _ => "1"
        };
        int idx = sideItems.FindIndex(i => i.Id == cur);
        DictationSideCombo.SelectedIndex = idx >= 0 ? idx : 0;

        FillDictationHotkeyCombo();
        FillDictationMicCombo();

        DictationHudBox.IsChecked = s.DictationShowHud;
        DictationSoundsBox.IsChecked = s.DictationSounds;
        DictationPasteBox.IsChecked = s.DictationAutoPaste;
        DictationCapBox.IsChecked = s.DictationCapitalize;
        DictationSpaceBox.IsChecked = s.DictationTrailingSpace;
        if (DictationVoiceCmdBox != null)
            DictationVoiceCmdBox.IsChecked = s.DictationVoiceCommands;

        if (DictationLangCombo != null)
        {
            var langs = new List<MiddleClickModeItem>
            {
                new("0", "Суржик: русский + украинский (рекомендую)"),
                new("1", "Только русский"),
                new("2", "Только украинский")
            };
            DictationLangCombo.DisplayMemberPath = nameof(MiddleClickModeItem.Title);
            DictationLangCombo.ItemsSource = langs;
            string langId = s.DictationLangMode switch
            {
                1 => "1",
                2 => "2",
                _ => "0"
            };
            int li = langs.FindIndex(i => i.Id == langId);
            DictationLangCombo.SelectedIndex = li >= 0 ? li : 0;
        }

        DictationQuietSlider.Value = s.DictationQuietMs;
        DictationBufferSlider.Value = s.DictationBufferMs;
        DictationHudMsSlider.Value = s.DictationHudMs;
        DictationPasteDelaySlider.Value = s.DictationPasteDelayMs;
        UpdateDictationTimingLabels();
    }

    private void FillDictationHotkeyCombo()
    {
        var s = ScreenshotStore.Current;
        var keys = new List<HotkeyItem> { new(0, "Нет (только мышь)") };
        for (int vk = 0x70; vk <= 0x7B; vk++)
            keys.Add(new HotkeyItem(vk, ScreenshotStore.HotkeyTitle(vk)));
        keys.Add(new HotkeyItem(0x2D, "Insert"));
        keys.Add(new HotkeyItem(0x91, "Scroll Lock"));
        if (s.DictationHotkey != 0 && keys.All(k => k.Vk != s.DictationHotkey))
            keys.Insert(1, new HotkeyItem(s.DictationHotkey, ScreenshotStore.HotkeyTitle(s.DictationHotkey)));
        DictationHotkeyCombo.DisplayMemberPath = nameof(HotkeyItem.Title);
        DictationHotkeyCombo.ItemsSource = keys;
        int idx = keys.FindIndex(k => k.Vk == s.DictationHotkey);
        DictationHotkeyCombo.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void FillDictationMicCombo()
    {
        var s = ScreenshotStore.Current;
        var items = new List<MicItem> { new(-1, "По умолчанию") };
        try
        {
            int n = NAudio.Wave.WaveIn.DeviceCount;
            for (int i = 0; i < n; i++)
            {
                string name;
                try { name = NAudio.Wave.WaveIn.GetCapabilities(i).ProductName; }
                catch { name = "Микрофон " + i; }
                if (string.IsNullOrWhiteSpace(name))
                    name = "Микрофон " + i;
                items.Add(new MicItem(i, name.Trim()));
            }
        }
        catch
        {
            // без NAudio списка — только «по умолчанию»
        }

        DictationMicCombo.DisplayMemberPath = nameof(MicItem.Title);
        DictationMicCombo.ItemsSource = items;
        int want = s.DictationMicDevice;
        int idx = items.FindIndex(m => m.Index == want);
        if (idx < 0)
            idx = 0;
        DictationMicCombo.SelectedIndex = idx;
    }

    private void OnDictationSideChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspend) return;
        if (DictationSideCombo.SelectedItem is not MiddleClickModeItem pick)
            return;
        if (!int.TryParse(pick.Id, out int side))
            return;

        var s = ScreenshotStore.Current;
        if (s.DictationSideButton == side)
            return;
        s.DictationSideButton = side;
        ScreenshotStore.Save(s);
        Saved?.Invoke(this, EventArgs.Empty);
        Note(DictationTriggerNote(s));
    }

    private void OnDictationHotkeyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspend) return;
        if (DictationHotkeyCombo.SelectedItem is not HotkeyItem item)
            return;

        var s = ScreenshotStore.Current;
        if (s.DictationHotkey == item.Vk)
            return;
        if (item.Vk != 0 && ConflictsWithOtherHotkey(item.Vk, s, ignoreDictation: true))
        {
            Note(HotkeyConflictMessage(item.Vk, s));
            ReselectHotkey(DictationHotkeyCombo, s.DictationHotkey);
            return;
        }

        s.DictationHotkey = item.Vk;
        ScreenshotStore.Save(s);
        Saved?.Invoke(this, EventArgs.Empty);
        Note(DictationTriggerNote(s));
    }

    private void OnDictationHotkeyAssign(object sender, RoutedEventArgs e)
    {
        if (_assignWhich != null)
        {
            StopHotkeyAssign();
            return;
        }

        _assignWhich = "dictation";
        ScreenshotStore.SuspendHotkey = true;
        ScreenshotStore.SuspendDictationCapture = true;
        DictationHotkeyAssign.Content = "Жду…";
        Note("Нажми боковую кнопку мыши (X1/X2) или клавишу. Esc — отмена.");
    }

    private void SelectDictationSide(byte side)
    {
        if (side is not (1 or 2))
            return;
        var s = ScreenshotStore.Current;
        s.DictationSideButton = side;
        // Клавишу не трогаем: мышь и клавиатура могут жить вместе.
        ScreenshotStore.Save(s);
        Saved?.Invoke(this, EventArgs.Empty);

        _suspend = true;
        if (DictationSideCombo.ItemsSource is List<MiddleClickModeItem> items)
        {
            int idx = items.FindIndex(i => i.Id == side.ToString());
            if (idx >= 0)
                DictationSideCombo.SelectedIndex = idx;
        }
        _suspend = false;
        Note(DictationTriggerNote(s));
    }

    private void SelectDictationHotkey(int vk)
    {
        if (DictationHotkeyCombo.ItemsSource is not List<HotkeyItem> keys)
            return;

        var s = ScreenshotStore.Current;
        if (ConflictsWithOtherHotkey(vk, s, ignoreDictation: true))
        {
            Note(HotkeyConflictMessage(vk, s));
            return;
        }

        if (keys.All(k => k.Vk != vk))
        {
            keys.Insert(1, new HotkeyItem(vk, ScreenshotStore.HotkeyTitle(vk)));
            DictationHotkeyCombo.ItemsSource = null;
            DictationHotkeyCombo.ItemsSource = keys;
        }

        _suspend = true;
        DictationHotkeyCombo.SelectedIndex = keys.FindIndex(k => k.Vk == vk);
        _suspend = false;
        s.DictationHotkey = vk;
        ScreenshotStore.Save(s);
        Saved?.Invoke(this, EventArgs.Empty);
        Note(DictationTriggerNote(s));
    }

    private void OnDictationMicRefresh(object sender, RoutedEventArgs e)
    {
        _suspend = true;
        FillDictationMicCombo();
        _suspend = false;
        Note("Список микрофонов обновлён.");
    }

    private void OnDictationMicChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspend) return;
        if (DictationMicCombo.SelectedItem is not MicItem item)
            return;
        var s = ScreenshotStore.Current;
        if (s.DictationMicDevice == item.Index)
            return;
        s.DictationMicDevice = item.Index;
        ScreenshotStore.Save(s);
        Note(item.Index < 0
            ? "Микрофон: устройство по умолчанию."
            : "Микрофон: " + item.Title + ".");
    }

    private void OnDictationHudClick(object sender, RoutedEventArgs e)
    {
        if (_suspend) return;
        var s = ScreenshotStore.Current;
        s.DictationShowHud = DictationHudBox.IsChecked == true;
        ScreenshotStore.Save(s);
        Note(s.DictationShowHud ? "Текст по центру экрана включён." : "Оверлей диктовки выключен.");
    }

    private void OnDictationSoundsClick(object sender, RoutedEventArgs e)
    {
        if (_suspend) return;
        var s = ScreenshotStore.Current;
        s.DictationSounds = DictationSoundsBox.IsChecked == true;
        ScreenshotStore.Save(s);
        Note(s.DictationSounds
            ? "Тихие звуки включены — сейчас пример старта."
            : "Звуки диктовки выключены.");
        if (s.DictationSounds)
            DictationCue.PlayStart();
    }

    private void OnDictationPasteClick(object sender, RoutedEventArgs e)
    {
        if (_suspend) return;
        var s = ScreenshotStore.Current;
        s.DictationAutoPaste = DictationPasteBox.IsChecked == true;
        ScreenshotStore.Save(s);
        Note(s.DictationAutoPaste
            ? "После диктовки сразу вставка в окно."
            : "После диктовки только буфер — вставь сам.");
    }

    private void OnDictationCapClick(object sender, RoutedEventArgs e)
    {
        if (_suspend) return;
        var s = ScreenshotStore.Current;
        s.DictationCapitalize = DictationCapBox.IsChecked == true;
        ScreenshotStore.Save(s);
        Note(s.DictationCapitalize ? "Первая буква будет заглавной." : "Регистр как распознала.");
    }

    private void OnDictationSpaceClick(object sender, RoutedEventArgs e)
    {
        if (_suspend) return;
        var s = ScreenshotStore.Current;
        s.DictationTrailingSpace = DictationSpaceBox.IsChecked == true;
        ScreenshotStore.Save(s);
        Note(s.DictationTrailingSpace
            ? "После фразы будет пробел — следующая не слипнется."
            : "Без пробела в конце — фразы могут слипнуться.");
    }

    private void OnDictationVoiceCmdClick(object sender, RoutedEventArgs e)
    {
        if (_suspend) return;
        var s = ScreenshotStore.Current;
        s.DictationVoiceCommands = DictationVoiceCmdBox.IsChecked == true;
        ScreenshotStore.Save(s);
        Note(s.DictationVoiceCommands
            ? "«Открой Telegram», «закрой вкладку» — выполняются командой."
            : "Всё распознанное снова только как текст.");
    }

    private void OnDictationLangChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspend || DictationLangCombo?.SelectedItem is not MiddleClickModeItem item)
            return;
        var s = ScreenshotStore.Current;
        s.DictationLangMode = item.Id switch
        {
            "1" => 1,
            "2" => 2,
            _ => 0
        };
        ScreenshotStore.Save(s);
        if (s.DictationLangMode is 0 or 2)
            _ = VoskModelStore.PreloadUkAsync();
        Note(s.DictationLangMode switch
        {
            1 => "Только русская модель.",
            2 => "Только украинская модель.",
            _ => "Суржик: обе модели, выбирается лучший вариант."
        });
    }

    private void OnDictationQuietChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suspend) return;
        UpdateDictationTimingLabels();
    }

    private void OnDictationBufferChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suspend) return;
        UpdateDictationTimingLabels();
    }

    private void OnDictationHudMsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suspend) return;
        UpdateDictationTimingLabels();
    }

    private void OnDictationPasteDelayChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suspend) return;
        UpdateDictationTimingLabels();
    }

    private void OnDictationTimingCommit(object sender, RoutedEventArgs e)
    {
        if (_suspend) return;
        var s = ScreenshotStore.Current;
        s.DictationQuietMs = (int)DictationQuietSlider.Value;
        s.DictationBufferMs = (int)DictationBufferSlider.Value;
        s.DictationHudMs = (int)DictationHudMsSlider.Value;
        s.DictationPasteDelayMs = (int)DictationPasteDelaySlider.Value;
        ScreenshotStore.Save(s);
        UpdateDictationTimingLabels();
        Note("Тайминги диктовки сохранены.");
    }

    private void UpdateDictationTimingLabels()
    {
        // Пока грузится XAML, слайдеры уже шлют ValueChanged — подписи ещё могут быть null.
        if (DictationQuietLabel == null
            || DictationBufferLabel == null
            || DictationHudMsLabel == null
            || DictationPasteDelayLabel == null
            || DictationQuietSlider == null
            || DictationBufferSlider == null
            || DictationHudMsSlider == null
            || DictationPasteDelaySlider == null)
            return;

        DictationQuietLabel.Text = ((int)DictationQuietSlider.Value) + " мс";
        DictationBufferLabel.Text = ((int)DictationBufferSlider.Value) + " мс";
        DictationHudMsLabel.Text = ((int)DictationHudMsSlider.Value) + " мс";
        DictationPasteDelayLabel.Text = ((int)DictationPasteDelaySlider.Value) + " мс";
    }

    private static string DictationTriggerNote(ScreenshotSettings s)
    {
        bool mouse = s.DictationSideButton is 1 or 2;
        bool key = s.DictationHotkey != 0;
        if (!mouse && !key)
            return "Диктовка выключена: назначь кнопку мыши или клавишу.";
        string m = s.DictationSideButton switch
        {
            1 => "X1",
            2 => "X2",
            _ => ""
        };
        string k = key ? ScreenshotStore.HotkeyTitle(s.DictationHotkey) : "";
        if (mouse && key)
            return $"Диктовка: {m} или {k}. Зажал — говори, отпустил — готово.";
        if (mouse)
            return $"Диктовка на {m}. Зажал — говори, отпустил — готово.";
        return $"Диктовка на {k}. Зажал — говори, отпустил — готово.";
    }

    private static bool ConflictsWithOtherHotkey(int vk, ScreenshotSettings s, bool ignoreDictation)
    {
        if (vk == s.Hotkey || vk == s.ClipHotkey || vk == s.TranslateHotkey)
            return true;
        if (!ignoreDictation && vk == s.DictationHotkey && s.DictationHotkey != 0)
            return true;
        return false;
    }

    private static string HotkeyConflictMessage(int vk, ScreenshotSettings s)
    {
        if (vk == s.Hotkey) return "Эта клавиша уже у снимка.";
        if (vk == s.ClipHotkey) return "Эта клавиша уже у клипа.";
        if (vk == s.TranslateHotkey) return "Эта клавиша уже у перевода.";
        if (vk == s.DictationHotkey) return "Эта клавиша уже у диктовки.";
        return "Клавиша занята.";
    }

    private sealed class MicItem
    {
        public MicItem(int index, string title)
        {
            Index = index;
            Title = title;
        }

        public int Index { get; }
        public string Title { get; }
    }

    private void FillRingSkinCombo()
    {
        var skins = AppearanceStore.ListSkins();
        var items = new List<RingSkinComboItem>
        {
            new(null, "Обычный диск (без скина)")
        };
        foreach (var skin in skins)
            items.Add(new RingSkinComboItem(skin.FileName, skin.Title));

        RingSkinCombo.DisplayMemberPath = nameof(RingSkinComboItem.Title);
        RingSkinCombo.ItemsSource = items;
        RingSkinCombo.IsEnabled = true;

        var cur = AppearanceStore.Current;
        if (!cur.UseRingSkin || string.IsNullOrWhiteSpace(cur.RingSkinFile))
            RingSkinCombo.SelectedIndex = 0;
        else
        {
            int idx = items.FindIndex(i =>
                i.FileName != null
                && string.Equals(i.FileName, cur.RingSkinFile, StringComparison.OrdinalIgnoreCase));
            RingSkinCombo.SelectedIndex = idx >= 0 ? idx : 0;
        }

        if (skins.Count == 0)
            RingSkinCombo.ToolTip = "Папка Assets\\Skins пуста: положи туда PNG без фона.";
    }

    private void OnRingSkinSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspend) return;
        if (RingSkinCombo.SelectedItem is not RingSkinComboItem pick)
            return;

        var s = AppearanceStore.Current;
        if (pick.FileName == null)
        {
            s.UseRingSkin = false;
            AppearanceStore.Save(s);
            Note("Скин круга выключен: обычный диск.");
            return;
        }

        s.UseRingSkin = true;
        s.RingSkinFile = pick.FileName;
        AppearanceStore.Save(s);
        Note($"Скин «{pick.Title}»: открой колесо, чтобы увидеть рамку.");
    }

    private sealed class RingSkinComboItem
    {
        public RingSkinComboItem(string? fileName, string title)
        {
            FileName = fileName;
            Title = title;
        }

        public string? FileName { get; }
        public string Title { get; }
        public override string ToString() => Title;
    }

    private sealed class MiddleClickModeItem
    {
        public MiddleClickModeItem(string id, string title)
        {
            Id = id;
            Title = title;
        }

        public string Id { get; }
        public string Title { get; }
        public override string ToString() => Title;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => TrySave();

    private bool TrySave()
    {
        // Сбросить поля формы в модель до записи: иначе последний ввод мог ещё не уйти в объект.
        FlushEditorToModel();

        foreach (var item in _items)
        {
            item.Label = item.Label.Trim();
            if (item.Label.Length == 0)
                item.Label = "Без названия";
            item.Description = item.Description.Trim();
            item.Value = item.Value.Trim();
            item.Arguments = item.Arguments.Trim();
            foreach (var child in item.Children)
            {
                child.Label = child.Label.Trim();
                if (child.Label.Length == 0)
                    child.Label = "Без названия";
                child.Description = child.Description.Trim();
                child.Value = child.Value.Trim();
                child.Arguments = child.Arguments.Trim();
            }
        }
        MenuStore.Renumber(_items);

        try
        {
            MenuStore.Save(_items);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this,
                "Не удалось записать настройки: " + ex.Message,
                "Zwmenu",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        _dirty = false;
        FooterStatus.Text = "Сохранено. Следующее колесо уже с этими пунктами.";
        FooterStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xC5, 0xA0));
        Saved?.Invoke(this, EventArgs.Empty);
        if (_selected != null)
            PushToForm();
        return true;
    }

    private void MarkDirty()
    {
        _dirty = true;
        FooterStatus.Text = "Есть несохранённые изменения.";
        FooterStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xC0, 0x78));
    }

    private void FillScreenshot()
    {
        var s = ScreenshotStore.Current;
        var keys = new List<HotkeyItem>();
        for (int vk = 0x70; vk <= 0x7B; vk++)
            keys.Add(new HotkeyItem(vk, ScreenshotStore.HotkeyTitle(vk)));
        keys.Add(new HotkeyItem(0x2C, "Print Screen"));
        if (keys.All(k => k.Vk != s.Hotkey))
            keys.Insert(0, new HotkeyItem(s.Hotkey, ScreenshotStore.HotkeyTitle(s.Hotkey)));

        ShotHotkeyCombo.DisplayMemberPath = nameof(HotkeyItem.Title);
        ShotHotkeyCombo.ItemsSource = keys;
        int idx = keys.FindIndex(k => k.Vk == s.Hotkey);
        ShotHotkeyCombo.SelectedIndex = idx >= 0 ? idx : 0;

        var clipKeys = new List<HotkeyItem>();
        for (int vk = 0x70; vk <= 0x7B; vk++)
            clipKeys.Add(new HotkeyItem(vk, ScreenshotStore.HotkeyTitle(vk)));
        if (clipKeys.All(k => k.Vk != s.ClipHotkey))
            clipKeys.Insert(0, new HotkeyItem(s.ClipHotkey, ScreenshotStore.HotkeyTitle(s.ClipHotkey)));
        ClipHotkeyCombo.DisplayMemberPath = nameof(HotkeyItem.Title);
        ClipHotkeyCombo.ItemsSource = clipKeys;
        int clipIdx = clipKeys.FindIndex(k => k.Vk == s.ClipHotkey);
        ClipHotkeyCombo.SelectedIndex = clipIdx >= 0 ? clipIdx : 0;

        ClipSecondsCombo.DisplayMemberPath = nameof(SecondsItem.Title);
        ClipSecondsCombo.ItemsSource = new List<SecondsItem>
        {
            new(5),
            new(10),
            new(15)
        };
        ClipSecondsCombo.SelectedIndex = s.ClipSeconds switch
        {
            5 => 0,
            15 => 2,
            _ => 1
        };

        ShotNotifyBox.IsChecked = s.ShowNotifications;
        ShotRememberBox.IsChecked = s.RememberRegion;
        ShotCursorBox.IsChecked = s.CaptureCursor;

        ShotFormatCombo.DisplayMemberPath = nameof(FormatItem.Title);
        ShotFormatCombo.ItemsSource = new List<FormatItem>
        {
            new("png", "PNG"),
            new("jpeg", "JPEG")
        };
        ShotFormatCombo.SelectedIndex = string.Equals(s.Format, "jpeg", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        ShotQualitySlider.Value = s.Quality;
        UpdateQualityUi();
    }

    private void OnShotHotkeyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspend)
            return;
        if (ShotHotkeyCombo.SelectedItem is not HotkeyItem item)
            return;

        var s = ScreenshotStore.Current;
        if (s.Hotkey == item.Vk)
            return;
        if (item.Vk == s.ClipHotkey)
        {
            Note("Эта клавиша уже у клипа.");
            ReselectHotkey(ShotHotkeyCombo, s.Hotkey);
            return;
        }

        if (item.Vk == s.TranslateHotkey)
        {
            Note("Эта клавиша уже у перевода.");
            ReselectHotkey(ShotHotkeyCombo, s.Hotkey);
            return;
        }

        if (item.Vk == s.DictationHotkey && s.DictationHotkey != 0)
        {
            Note("Эта клавиша уже у диктовки.");
            ReselectHotkey(ShotHotkeyCombo, s.Hotkey);
            return;
        }

        s.Hotkey = item.Vk;
        ScreenshotStore.Save(s);
        Note($"Скриншот: {item.Title}.");
    }

    private void OnClipHotkeyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspend)
            return;
        if (ClipHotkeyCombo.SelectedItem is not HotkeyItem item)
            return;

        var s = ScreenshotStore.Current;
        if (s.ClipHotkey == item.Vk)
            return;
        if (item.Vk == s.Hotkey)
        {
            Note("Эта клавиша уже у снимка.");
            ReselectHotkey(ClipHotkeyCombo, s.ClipHotkey);
            return;
        }

        if (item.Vk == s.TranslateHotkey)
        {
            Note("Эта клавиша уже у перевода.");
            ReselectHotkey(ClipHotkeyCombo, s.ClipHotkey);
            return;
        }

        if (item.Vk == s.DictationHotkey && s.DictationHotkey != 0)
        {
            Note("Эта клавиша уже у диктовки.");
            ReselectHotkey(ClipHotkeyCombo, s.ClipHotkey);
            return;
        }

        s.ClipHotkey = item.Vk;
        ScreenshotStore.Save(s);
        Note($"Клип: {item.Title}.");
    }

    private void OnClipSecondsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspend)
            return;
        if (ClipSecondsCombo.SelectedItem is not SecondsItem item)
            return;
        var s = ScreenshotStore.Current;
        if (s.ClipSeconds == item.Seconds)
            return;
        s.ClipSeconds = item.Seconds;
        ScreenshotStore.Save(s);
        Note($"Клип до {item.Seconds} с, раньше — Esc или та же клавиша.");
    }

    private void OnClipHotkeyAssign(object sender, RoutedEventArgs e)
    {
        if (_assignWhich != null)
        {
            StopHotkeyAssign();
            return;
        }

        _assignWhich = "clip";
        ScreenshotStore.SuspendHotkey = true;
        ClipHotkeyAssign.Content = "Жду клавишу…";
        Note("Нажми клавишу для клипа. Esc — оставить как есть.");
    }

    private void OnShotHotkeyAssign(object sender, RoutedEventArgs e)
    {
        if (_assignWhich != null)
        {
            StopHotkeyAssign();
            return;
        }

        _assignWhich = "shot";
        ScreenshotStore.SuspendHotkey = true;
        ShotHotkeyAssign.Content = "Жду клавишу…";
        Note("Нажми клавишу для снимка. Esc — оставить как есть.");
    }

    private void StopHotkeyAssign()
    {
        _assignWhich = null;
        ScreenshotStore.SuspendHotkey = false;
        ScreenshotStore.SuspendDictationCapture = false;
        ShotHotkeyAssign.Content = "Назначить";
        ClipHotkeyAssign.Content = "Назначить";
        TranslateHotkeyAssign.Content = "Назначить";
        DictationHotkeyAssign.Content = "Назначить";
    }

    private void SelectHotkey(int vk)
    {
        if (ShotHotkeyCombo.ItemsSource is not List<HotkeyItem> keys)
            return;

        if (keys.All(k => k.Vk != vk))
        {
            keys.Insert(0, new HotkeyItem(vk, ScreenshotStore.HotkeyTitle(vk)));
            ShotHotkeyCombo.ItemsSource = null;
            ShotHotkeyCombo.ItemsSource = keys;
        }

        _suspend = true;
        ShotHotkeyCombo.SelectedIndex = keys.FindIndex(k => k.Vk == vk);
        _suspend = false;

        var s = ScreenshotStore.Current;
        if (vk == s.ClipHotkey)
        {
            Note("Эта клавиша уже у клипа.");
            ReselectHotkey(ShotHotkeyCombo, s.Hotkey);
            return;
        }

        if (vk == s.TranslateHotkey)
        {
            Note("Эта клавиша уже у перевода.");
            ReselectHotkey(ShotHotkeyCombo, s.Hotkey);
            return;
        }

        if (vk == s.DictationHotkey && s.DictationHotkey != 0)
        {
            Note("Эта клавиша уже у диктовки.");
            ReselectHotkey(ShotHotkeyCombo, s.Hotkey);
            return;
        }

        s.Hotkey = vk;
        ScreenshotStore.Save(s);
        Note($"Скриншот: {ScreenshotStore.HotkeyTitle(vk)}.");
    }

    private void SelectClipHotkey(int vk)
    {
        if (ClipHotkeyCombo.ItemsSource is not List<HotkeyItem> keys)
            return;

        var s = ScreenshotStore.Current;
        if (vk == s.Hotkey)
        {
            Note("Эта клавиша уже у снимка.");
            return;
        }

        if (vk == s.TranslateHotkey)
        {
            Note("Эта клавиша уже у перевода.");
            return;
        }

        if (vk == s.DictationHotkey && s.DictationHotkey != 0)
        {
            Note("Эта клавиша уже у диктовки.");
            return;
        }

        if (keys.All(k => k.Vk != vk))
        {
            keys.Insert(0, new HotkeyItem(vk, ScreenshotStore.HotkeyTitle(vk)));
            ClipHotkeyCombo.ItemsSource = null;
            ClipHotkeyCombo.ItemsSource = keys;
        }

        _suspend = true;
        ClipHotkeyCombo.SelectedIndex = keys.FindIndex(k => k.Vk == vk);
        _suspend = false;
        s.ClipHotkey = vk;
        ScreenshotStore.Save(s);
        Note($"Клип: {ScreenshotStore.HotkeyTitle(vk)}.");
    }

    private void FillTranslateHotkey()
    {
        var s = ScreenshotStore.Current;
        var keys = new List<HotkeyItem>();
        for (int vk = 0x70; vk <= 0x7B; vk++)
            keys.Add(new HotkeyItem(vk, ScreenshotStore.HotkeyTitle(vk)));
        if (keys.All(k => k.Vk != s.TranslateHotkey))
            keys.Insert(0, new HotkeyItem(s.TranslateHotkey, ScreenshotStore.HotkeyTitle(s.TranslateHotkey)));
        TranslateHotkeyCombo.DisplayMemberPath = nameof(HotkeyItem.Title);
        TranslateHotkeyCombo.ItemsSource = keys;
        int idx = keys.FindIndex(k => k.Vk == s.TranslateHotkey);
        TranslateHotkeyCombo.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void OnTranslateHotkeyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspend)
            return;
        if (TranslateHotkeyCombo.SelectedItem is not HotkeyItem item)
            return;

        var s = ScreenshotStore.Current;
        if (s.TranslateHotkey == item.Vk)
            return;
        if (item.Vk == s.Hotkey)
        {
            Note("Эта клавиша уже у снимка.");
            ReselectHotkey(TranslateHotkeyCombo, s.TranslateHotkey);
            return;
        }

        if (item.Vk == s.ClipHotkey)
        {
            Note("Эта клавиша уже у клипа.");
            ReselectHotkey(TranslateHotkeyCombo, s.TranslateHotkey);
            return;
        }

        if (item.Vk == s.DictationHotkey && s.DictationHotkey != 0)
        {
            Note("Эта клавиша уже у диктовки.");
            ReselectHotkey(TranslateHotkeyCombo, s.TranslateHotkey);
            return;
        }

        s.TranslateHotkey = item.Vk;
        ScreenshotStore.Save(s);
        Note($"Перевод буфера: {item.Title}.");
    }

    private void OnTranslateHotkeyAssign(object sender, RoutedEventArgs e)
    {
        if (_assignWhich != null)
        {
            StopHotkeyAssign();
            return;
        }

        _assignWhich = "translate";
        ScreenshotStore.SuspendHotkey = true;
        TranslateHotkeyAssign.Content = "Жду клавишу…";
        Note("Нажми клавишу для перевода буфера. Esc — оставить как есть.");
    }

    private void SelectTranslateHotkey(int vk)
    {
        if (TranslateHotkeyCombo.ItemsSource is not List<HotkeyItem> keys)
            return;

        var s = ScreenshotStore.Current;
        if (vk == s.Hotkey)
        {
            Note("Эта клавиша уже у снимка.");
            return;
        }

        if (vk == s.ClipHotkey)
        {
            Note("Эта клавиша уже у клипа.");
            return;
        }

        if (vk == s.DictationHotkey && s.DictationHotkey != 0)
        {
            Note("Эта клавиша уже у диктовки.");
            return;
        }

        if (keys.All(k => k.Vk != vk))
        {
            keys.Insert(0, new HotkeyItem(vk, ScreenshotStore.HotkeyTitle(vk)));
            TranslateHotkeyCombo.ItemsSource = null;
            TranslateHotkeyCombo.ItemsSource = keys;
        }

        _suspend = true;
        TranslateHotkeyCombo.SelectedIndex = keys.FindIndex(k => k.Vk == vk);
        _suspend = false;
        s.TranslateHotkey = vk;
        ScreenshotStore.Save(s);
        Note($"Перевод буфера: {ScreenshotStore.HotkeyTitle(vk)}.");
    }

    private void ReselectHotkey(System.Windows.Controls.ComboBox combo, int vk)
    {
        if (combo.ItemsSource is not List<HotkeyItem> keys)
            return;
        int idx = keys.FindIndex(k => k.Vk == vk);
        if (idx < 0)
            return;
        _suspend = true;
        combo.SelectedIndex = idx;
        _suspend = false;
    }

    private void OnShotNotifyClick(object sender, RoutedEventArgs e)
    {
        if (_suspend)
            return;
        var s = ScreenshotStore.Current;
        s.ShowNotifications = ShotNotifyBox.IsChecked == true;
        ScreenshotStore.Save(s);
        Note(s.ShowNotifications
            ? "Уведомления скриншота включены."
            : "Уведомления скриншота выключены.");
    }

    private void OnShotRememberClick(object sender, RoutedEventArgs e)
    {
        if (_suspend)
            return;
        var s = ScreenshotStore.Current;
        s.RememberRegion = ShotRememberBox.IsChecked == true;
        ScreenshotStore.Save(s);
        Note(s.RememberRegion
            ? "Следующий снимок откроет прошлую область."
            : "Следующий снимок снова на весь экран.");
    }

    private void OnShotCursorClick(object sender, RoutedEventArgs e)
    {
        if (_suspend)
            return;
        var s = ScreenshotStore.Current;
        s.CaptureCursor = ShotCursorBox.IsChecked == true;
        ScreenshotStore.Save(s);
        Note(s.CaptureCursor
            ? "Курсор попадёт на скриншот."
            : "Курсор на скриншоте не сохраняется.");
    }

    private void OnShotFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspend)
            return;
        if (ShotFormatCombo.SelectedItem is not FormatItem item)
            return;

        var s = ScreenshotStore.Current;
        s.Format = item.Id;
        ScreenshotStore.Save(s);
        UpdateQualityUi();
        Note(item.Id == "jpeg" ? "Формат скриншота: JPEG." : "Формат скриншота: PNG.");
    }

    private void OnShotQualityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suspend || ShotQualityLabel == null)
            return;
        UpdateQualityUi();
        if (ShotQualitySlider.IsMouseCaptureWithin)
            return;
        SaveQuality();
    }

    private void OnShotQualityCommit(object sender, RoutedEventArgs e)
    {
        if (_suspend)
            return;
        SaveQuality();
    }

    private void SaveQuality()
    {
        var s = ScreenshotStore.Current;
        int q = (int)Math.Round(ShotQualitySlider.Value);
        if (s.Quality == q)
            return;
        s.Quality = q;
        ScreenshotStore.Save(s);
    }

    private void UpdateQualityUi()
    {
        int q = (int)Math.Round(ShotQualitySlider.Value);
        ShotQualityLabel.Text = q.ToString();
        bool jpeg = ShotFormatCombo.SelectedItem is FormatItem item && item.Id == "jpeg";
        ShotQualitySlider.IsEnabled = jpeg;
        ShotQualityHint.Opacity = jpeg ? 0.85 : 0.58;
        ShotQualityHint.Text = jpeg
            ? "Качество JPEG: выше — чётче и тяжелее файл."
            : "PNG сохраняется без потерь. Качество нужно только для JPEG.";
    }

    private sealed class HotkeyItem
    {
        public HotkeyItem(int vk, string title)
        {
            Vk = vk;
            Title = title;
        }

        public int Vk { get; }
        public string Title { get; }
        public override string ToString() => Title;
    }

    private sealed class SecondsItem
    {
        public SecondsItem(int seconds) => Seconds = seconds;
        public int Seconds { get; }
        public string Title => Seconds + " секунд";
    }

    private sealed class FormatItem
    {
        public FormatItem(string id, string title)
        {
            Id = id;
            Title = title;
        }

        public string Id { get; }
        public string Title { get; }
        public override string ToString() => Title;
    }

    private void Note(string text)
    {
        FooterStatus.Text = text;
        FooterStatus.Foreground = new SolidColorBrush(Color.FromArgb(0xD0, 0xF5, 0xF0, 0xE8));
    }

    private bool _allowClose;

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        if (_dirty)
        {
            var answer = System.Windows.MessageBox.Show(this,
                "Есть несохранённые изменения. Записать их?",
                "Zwmenu",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (answer == MessageBoxResult.Yes && !TrySave())
            {
                e.Cancel = true;
                return;
            }
        }

        StopHotkeyAssign();
        e.Cancel = true;
        Hide();
    }
}
