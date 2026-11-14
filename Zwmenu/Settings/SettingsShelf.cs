using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Zwmenu.Overlay;
using Zwmenu.Services;

namespace Zwmenu.Settings;

public partial class SettingsWindow
{
    private int _shelfAlign = 1;
    private int _shelfScroll;
    private int _shelfLayout;
    private List<KindRow> _kindRows = [];
    private DispatcherTimer? _shelfTimer;

    private void FillShelf()
    {
        var prefs = ShelfFiles.CurrentLook();
        _shelfAlign = prefs.Align;
        _shelfScroll = prefs.Scroll;
        _shelfLayout = prefs.Layout;
        if (ShelfSortCombo.ItemsSource == null)
        {
            ShelfSortCombo.DisplayMemberPath = nameof(SortRow.Title);
            ShelfSortCombo.ItemsSource = new List<SortRow>
            {
                new(0, "Вручную"),
                new(1, "По типу"),
                new(2, "По имени"),
                new(3, "Папки сначала"),
                new(4, "Серии вместе")
            };
        }

        ShelfSortCombo.SelectedIndex = prefs.Sort;
        _kindRows = ShelfArrange.Kinds(prefs.KindOrder).Select(id => new KindRow(id, ShelfArrange.Title(id))).ToList();
        ShelfKindList.DisplayMemberPath = nameof(KindRow.Title);
        ShelfKindList.ItemsSource = _kindRows;
        ShelfIconSlider.Value = prefs.Icon;
        ShelfGapSlider.Value = prefs.Gap;
        ShelfRadiusSlider.Value = prefs.Radius;
        ShelfPlateSlider.Value = prefs.Plate;
        ShelfNamesBox.IsChecked = prefs.Names;
        ShelfNameSizeSlider.Value = prefs.NameSize;
        ShelfWidthSlider.Value = prefs.ShelfWidth;
        ShelfRowsSlider.Value = prefs.Rows;
        ShelfColumnsSlider.Value = prefs.Columns;
        ShelfBottomAuto.IsChecked = prefs.Bottom < 0;
        ShelfBottomSlider.Value = prefs.Bottom < 0 ? 48 : prefs.Bottom;
        ShelfHoverSlider.Value = prefs.Hover;
        ShelfPressBox.IsChecked = prefs.Press;
        ShelfHideGameBox.IsChecked = prefs.HideInGame;
        ShelfAboveBox.IsChecked = prefs.Above;
        RefreshShelfUi();
    }

    private void OnShelfSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suspend)
            return;
        RefreshShelfUi();
        QueueShelf();
    }

    private void OnShelfClick(object sender, RoutedEventArgs e)
    {
        if (_suspend)
            return;
        RefreshShelfUi();
        QueueShelf();
    }

    private void OnShelfAlign(object sender, RoutedEventArgs e)
    {
        if (_suspend || sender is not Button { Tag: string tag } || !int.TryParse(tag, out int align))
            return;
        _shelfAlign = align;
        RefreshShelfUi();
        QueueShelf();
    }

    private void OnShelfScroll(object sender, RoutedEventArgs e)
    {
        if (_suspend || sender is not Button { Tag: string tag } || !int.TryParse(tag, out int mode))
            return;
        _shelfScroll = mode;
        RefreshShelfUi();
        QueueShelf();
    }

    private void OnShelfLayout(object sender, RoutedEventArgs e)
    {
        if (_suspend || sender is not Button { Tag: string tag } || !int.TryParse(tag, out int layout))
            return;
        _shelfLayout = layout;
        RefreshShelfUi();
        QueueShelf();
    }

    private void OnShelfSort(object sender, SelectionChangedEventArgs e)
    {
        if (_suspend)
            return;
        RefreshShelfUi();
        QueueShelf();
    }

    private void OnShelfKindUp(object sender, RoutedEventArgs e) => MoveKind(-1);

    private void OnShelfKindDown(object sender, RoutedEventArgs e) => MoveKind(1);

    private void MoveKind(int delta)
    {
        if (_suspend)
            return;
        int index = ShelfKindList.SelectedIndex;
        int next = index + delta;
        if (index < 0 || next < 0 || next >= _kindRows.Count)
            return;
        (_kindRows[index], _kindRows[next]) = (_kindRows[next], _kindRows[index]);
        ShelfKindList.ItemsSource = null;
        ShelfKindList.ItemsSource = _kindRows;
        ShelfKindList.SelectedIndex = next;
        if (ShelfSortCombo.SelectedIndex <= 0)
            ShelfSortCombo.SelectedIndex = 1;
        QueueShelf();
    }

    private void OnShelfReset(object sender, RoutedEventArgs e)
    {
        ShelfFiles.ResetLook();
        _suspend = true;
        FillShelf();
        _suspend = false;
        ShelfWindow.ApplyLook();
    }

    private void QueueShelf()
    {
        if (_shelfTimer == null)
        {
            _shelfTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _shelfTimer.Tick += CommitShelf;
        }

        _shelfTimer.Stop();
        _shelfTimer.Start();
    }

    private void CommitShelf(object? sender, EventArgs e)
    {
        _shelfTimer?.Stop();
        if (_suspend)
            return;
        ShelfFiles.EditLook(prefs =>
        {
            prefs.Icon = (int)Math.Round(ShelfIconSlider.Value);
            prefs.Gap = (int)Math.Round(ShelfGapSlider.Value);
            prefs.Radius = (int)Math.Round(ShelfRadiusSlider.Value);
            prefs.Plate = (int)Math.Round(ShelfPlateSlider.Value);
            prefs.Names = ShelfNamesBox.IsChecked == true;
            prefs.NameSize = (int)Math.Round(ShelfNameSizeSlider.Value);
            prefs.ShelfWidth = (int)Math.Round(ShelfWidthSlider.Value);
            prefs.Rows = (int)Math.Round(ShelfRowsSlider.Value);
            prefs.Columns = (int)Math.Round(ShelfColumnsSlider.Value);
            prefs.Bottom = ShelfBottomAuto.IsChecked == true ? -1 : (int)Math.Round(ShelfBottomSlider.Value);
            prefs.Align = _shelfAlign;
            prefs.Scroll = _shelfScroll;
            prefs.Hover = (int)Math.Round(ShelfHoverSlider.Value);
            prefs.Press = ShelfPressBox.IsChecked == true;
            prefs.HideInGame = ShelfHideGameBox.IsChecked == true;
            prefs.Above = ShelfAboveBox.IsChecked == true;
            prefs.Layout = _shelfLayout;
            prefs.Sort = ShelfSortCombo.SelectedItem is SortRow row ? row.Id : 0;
            prefs.KindOrder = string.Join(',', _kindRows.Select(item => item.Id));
        });
        ShelfWindow.ApplyLook();
    }

    private void RefreshShelfUi()
    {
        int icon = (int)Math.Round(ShelfIconSlider.Value);
        int gap = (int)Math.Round(ShelfGapSlider.Value);
        int cols = (int)Math.Round(ShelfColumnsSlider.Value);
        int rows = (int)Math.Round(ShelfRowsSlider.Value);
        bool names = ShelfNamesBox.IsChecked == true;
        bool autoBottom = ShelfBottomAuto.IsChecked == true;
        ShelfIconLabel.Text = icon.ToString();
        ShelfGapLabel.Text = gap.ToString();
        ShelfRadiusLabel.Text = ((int)Math.Round(ShelfRadiusSlider.Value)).ToString();
        ShelfPlateLabel.Text = ((int)Math.Round(ShelfPlateSlider.Value)).ToString();
        ShelfNameSizeLabel.Text = ((int)Math.Round(ShelfNameSizeSlider.Value)).ToString();
        ShelfWidthLabel.Text = ((int)Math.Round(ShelfWidthSlider.Value)) + "%";
        ShelfRowsLabel.Text = rows.ToString();
        ShelfColumnsLabel.Text = cols == 0 ? "авто" : cols.ToString();
        ShelfBottomLabel.Text = autoBottom ? "авто" : ((int)Math.Round(ShelfBottomSlider.Value)).ToString();
        ShelfHoverLabel.Text = ((int)Math.Round(ShelfHoverSlider.Value)) + "%";
        ShelfNameSizeSlider.IsEnabled = names;
        ShelfNameSizeLabel.Opacity = names ? 1 : 0.4;
        ShelfBottomSlider.IsEnabled = !autoBottom;
        ShelfBottomLabel.Opacity = autoBottom ? 0.4 : 1;
        PaintShelf(ShelfAlignLeft, _shelfAlign == 0);
        PaintShelf(ShelfAlignCenter, _shelfAlign == 1);
        PaintShelf(ShelfAlignRight, _shelfAlign == 2);
        PaintShelf(ShelfScrollFit, _shelfScroll == 0);
        PaintShelf(ShelfScrollX, _shelfScroll == 1);
        PaintShelf(ShelfScrollY, _shelfScroll == 2);
        Button[] looks =
        [
            ShelfLayShelf, ShelfLayDock, ShelfLayColumn, ShelfLayGroups,
            ShelfLayRibbon, ShelfLayList, ShelfLayRail, ShelfLayPoster,
            ShelfLayChips, ShelfLayIslands, ShelfLayTop, ShelfLayCenter,
            ShelfLayCover, ShelfLayCinema, ShelfLayFan, ShelfLayRing, ShelfLayThrone
        ];
        for (int i = 0; i < looks.Length; i++)
            PaintShelf(looks[i], _shelfLayout == i);
        bool grid = _shelfLayout is 0 or 1 or 3 or 7 or 8 or 9 or 10 or 11;
        ShelfColumnsSlider.IsEnabled = grid;
        ShelfColumnsLabel.Opacity = grid ? 1 : 0.4;
        ShelfRowsSlider.IsEnabled = grid;
        ShelfRowsLabel.Opacity = grid ? 1 : 0.4;
        ShelfScrollFit.IsEnabled = grid;
        ShelfScrollX.IsEnabled = grid;
        ShelfScrollY.IsEnabled = grid;
        ShelfLayoutHint.Text = _shelfLayout switch
        {
            1 => "Значки без своих карточек на одной общей плашке у нижнего края.",
            2 => "Один столбик карточек у края. Слева или справа берётся из выравнивания. Центр ставит столбик справа и по вертикали посередине. Число в ряду не влияет. Если выше экрана, колесо листает вниз.",
            3 => "Каждый тип своей кучкой и подписью. Очередь типов решает, что первым: картинки, диски, музыка, папки.",
            4 => "Одна горизонтальная лента: значок и имя в строку, на общей плашке. Если не влезает, колесо листает вбок.",
            5 => "Компактные строки у края: маленький значок и имя справа, у каждой своя плашка. Выше экрана листается колесом.",
            6 => "Узкая капсула у края, значок и имя в одну строку. Выше экрана листается колесом.",
            7 => "Крупные кадры, имя лежит на самой картинке. Сетка как у полки.",
            8 => "Каждый файл отдельной капсулой: значок и имя. Капсулы переносятся рядами.",
            9 => "У каждого типа своя плашка. Очередь типов решает порядок плашек.",
            10 => "Та же сетка, что у полки, но прижата к верхнему краю.",
            11 => "Отдельное окно по центру экрана, сетка значков на общей плашке.",
            12 => "Обложка. Средний значок крупный, соседи меньше. Наведи, и крупным станет он. Без анимации, сразу.",
            13 => "Кино. Слева большой кадр того, на что навёл, справа остальные в ряд на одной плашке.",
            14 => "Колода. Карточки веером снизу, наведённая выпрямляется и встаёт сверху.",
            15 => "Кольцо. Значки по кругу над нижним краем, середина пустая, сквозь неё видны обои.",
            16 => "Трон. Один большой значок снизу, остальные мелкой короной над ним. Наведи, чтобы сменить большой.",
            _ => "Отдельные карточки сеткой у нижнего края."
        };
        ShelfScrollHint.Text = _shelfScroll switch
        {
            1 => "Значки остаются крупными. Лишнее уезжает вбок: крути колесо мыши, полосы на экране нет.",
            2 => "Значки остаются крупными. Видно выбранное число рядов, остальное уезжает вниз. Листает колесо мыши, без полосы.",
            _ => "Всё помещается в выбранные ряды. Если ширины не хватает, значки становятся меньше. Прокрутки нет."
        };
        DrawShelfPreview(icon, gap, cols, rows, names, _shelfLayout);
    }

    private static void PaintShelf(Button button, bool on)
    {
        button.Background = new SolidColorBrush(on
            ? Color.FromRgb(0x3E, 0x5A, 0x6E)
            : Color.FromRgb(0x2A, 0x2A, 0x2E));
        button.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void DrawShelfPreview(int icon, int gap, int cols, int rows, bool names, int layout)
    {
        ShelfPreview.Children.Clear();
        if (layout >= 12)
        {
            DrawStagePreview(layout, icon);
            return;
        }

        bool edge = layout is 2 or 5 or 6;
        bool side = layout is 4 or 5 or 6 or 8 or 9;
        bool poster = layout == 7;
        bool bare = layout is 1 or 4 or 6 or 11;
        int across = edge ? 1 : layout == 4 ? 4 : cols > 0 ? Math.Min(cols, 4) : 4;
        int radius = (int)Math.Round(ShelfRadiusSlider.Value);
        int plate = (int)Math.Round(ShelfPlateSlider.Value);
        int nameSize = (int)Math.Round(ShelfNameSizeSlider.Value);
        double card = side ? icon + 96 : poster ? icon + 6 : icon + 20;
        ShelfPreview.Width = Math.Max(card + gap, across * (card + gap));
        ShelfPreview.Background = bare
            ? new SolidColorBrush(Color.FromArgb(0xE0, 0x16, 0x12, 0x10))
            : Brushes.Transparent;
        byte alpha = (byte)Math.Clamp(0xF0 * plate / 100, 0, 255);
        string[] samples = ["Папка", "Клинок", "Заметки", "Музыка", "Архив", "Игра", "Фото", "Ярлык"];
        var ink = new SolidColorBrush(Color.FromRgb(0xF6, 0xEE, 0xE6));
        ink.Freeze();
        var plateBrush = new SolidColorBrush(Color.FromArgb(alpha, 0x3A, 0x28, 0x22));
        plateBrush.Freeze();
        var rim = new SolidColorBrush(Color.FromArgb(0x88, 0xF0, 0xE0, 0xC8));
        rim.Freeze();
        var shade = new SolidColorBrush(Color.FromArgb(0xC0, 0x10, 0x0C, 0x08));
        shade.Freeze();
        int shown = edge ? 5 : samples.Length;
        for (int i = 0; i < shown; i++)
        {
            if ((layout == 3 || layout == 9) && (i == 0 || i == 4))
            {
                ShelfPreview.Children.Add(new Border
                {
                    Width = ShelfPreview.Width,
                    Background = layout == 9 ? new SolidColorBrush(Color.FromArgb(0xE0, 0x16, 0x12, 0x10)) : Brushes.Transparent,
                    CornerRadius = new CornerRadius(layout == 9 ? 12 : 0),
                    Padding = new Thickness(8, 6, 8, 2),
                    Margin = new Thickness(0, 6, 0, 0),
                    Child = new TextBlock
                    {
                        Text = i == 0 ? "Картинки" : "Музыка",
                        FontSize = 13,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = ink
                    }
                });
            }

            if (side)
            {
                var row = new StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    Width = card,
                    Height = icon + 8,
                    Margin = new Thickness(gap / 2.0)
                };
                if (layout is 5 or 8)
                {
                    row.Background = plateBrush;
                }

                row.Children.Add(new Border
                {
                    Width = icon,
                    Height = icon,
                    Margin = new Thickness(4, 0, 0, 0),
                    CornerRadius = new CornerRadius(Math.Min(radius, icon / 2.0)),
                    Background = Brushes.Transparent,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                });
                if (names)
                {
                    row.Children.Add(new TextBlock
                    {
                        Text = samples[i],
                        Width = 78,
                        Margin = new Thickness(8, 0, 4, 0),
                        FontSize = nameSize,
                        Foreground = ink,
                        VerticalAlignment = System.Windows.VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });
                }

                ShelfPreview.Children.Add(row);
                continue;
            }

            var stack = new StackPanel
            {
                Width = card,
                Margin = new Thickness(gap / 2.0)
            };
            var art = new Border
            {
                Width = poster ? icon : icon,
                Height = icon,
                CornerRadius = new CornerRadius(radius),
                Background = bare ? Brushes.Transparent : plateBrush,
                BorderBrush = bare ? Brushes.Transparent : rim,
                BorderThickness = new Thickness(bare ? 0 : 1),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
            if (poster && names)
            {
                art.Child = new TextBlock
                {
                    Text = samples[i],
                    FontSize = nameSize,
                    Foreground = ink,
                    Background = shade,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
                    Padding = new Thickness(2)
                };
            }

            stack.Children.Add(art);
            if (names && !poster)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = samples[i],
                    Width = card,
                    FontSize = nameSize,
                    Height = Math.Max(14, nameSize + 6),
                    Foreground = ink,
                    TextAlignment = TextAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }

            ShelfPreview.Children.Add(stack);
        }

        string acrossText = layout switch
        {
            2 or 5 or 6 => "один столбик",
            4 => "одна лента",
            10 => "у верхнего края",
            11 => "по центру экрана",
            _ => cols == 0 ? "в ряду сколько влезет" : "в ряду " + cols
        };
        string look = layout switch
        {
            1 => "Док. ",
            2 => "Колонка. ",
            3 => "Группы. ",
            4 => "Лента. ",
            5 => "Список. ",
            6 => "Рельс. ",
            7 => "Плакаты. ",
            8 => "Фишки. ",
            9 => "Острова. ",
            10 => "Сверху. ",
            11 => "Центр. ",
            _ => "Полка. "
        };
        string mode = layout is 2 or 5 or 6
            ? "если выше экрана, колесо вниз"
            : layout == 4
                ? "если не влезает, колесо вбок"
                : _shelfScroll switch
                {
                    1 => "листается вбок",
                    2 => "листается вниз",
                    _ => "без прокрутки, лишнее ужимается"
                };
        ShelfPreviewNote.Text = look + "Значок " + icon + ", рядов видно " + rows + ", " + acrossText + ". " + mode + ".";
    }

    private void DrawStagePreview(int layout, int icon)
    {
        var ink = new SolidColorBrush(Color.FromRgb(0xF6, 0xEE, 0xE6));
        ink.Freeze();
        var plate = new SolidColorBrush(Color.FromArgb(0xE0, 0x3A, 0x28, 0x22));
        plate.Freeze();
        ShelfPreview.Background = layout == 13
            ? new SolidColorBrush(Color.FromArgb(0xE0, 0x16, 0x12, 0x10))
            : Brushes.Transparent;
        ShelfPreview.Width = layout == 15 ? 220 : 280;
        if (layout == 15)
        {
            var ring = new Grid { Width = 200, Height = 200 };
            for (int i = 0; i < 8; i++)
            {
                double angle = -Math.PI / 2 + Math.PI * 2 * i / 8;
                ring.Children.Add(new Border
                {
                    Width = i == 0 ? 28 : 18,
                    Height = i == 0 ? 28 : 18,
                    Background = plate,
                    CornerRadius = new CornerRadius(6),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    VerticalAlignment = System.Windows.VerticalAlignment.Top,
                    Margin = new Thickness(100 + Math.Cos(angle) * 74 - 10, 100 + Math.Sin(angle) * 74 - 10, 0, 0)
                });
            }

            ShelfPreview.Children.Add(ring);
        }
        else if (layout == 13)
        {
            ShelfPreview.Children.Add(new Border
            {
                Width = icon + 36,
                Height = icon + 36,
                Background = plate,
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(8)
            });
            for (int i = 0; i < 4; i++)
            {
                ShelfPreview.Children.Add(new Border
                {
                    Width = icon * 0.55,
                    Height = icon * 0.55,
                    Background = plate,
                    CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(4),
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                });
            }
        }
        else
        {
            int count = layout == 16 ? 5 : 7;
            for (int i = 0; i < count; i++)
            {
                bool hero = layout == 16 ? i == count - 1 : i == count / 2;
                double size = hero ? icon + 16 : icon * 0.62;
                ShelfPreview.Children.Add(new Border
                {
                    Width = size,
                    Height = size,
                    Background = plate,
                    CornerRadius = new CornerRadius(hero ? 12 : 8),
                    Margin = new Thickness(3)
                });
            }
        }

        ShelfPreviewNote.Text = layout switch
        {
            13 => "Кино. Большой кадр слева, остальные справа.",
            14 => "Колода. Веер карточек, наведённая сверху.",
            15 => "Кольцо. Круг значков, середина пустая.",
            16 => "Трон. Один большой снизу, остальные над ним.",
            _ => "Обложка. Средний крупнее, наведи чтобы сменить."
        };
    }

    private sealed record SortRow(int Id, string Title);

    private sealed record KindRow(string Id, string Title);
}
