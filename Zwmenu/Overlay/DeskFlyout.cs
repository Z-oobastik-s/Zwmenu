using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Zwmenu.Services;

namespace Zwmenu.Overlay;

/// <summary>Выезд пульта с колеса: край, питание, звук, яркость, трансляция.</summary>
static class DeskFlyout
{
    const double PanelW = 340;

    public static Border Build(Action<string> tip, Action dismiss)
    {
        var root = new StackPanel { Width = PanelW };

        bool linked = EdgeShareService.Shared.Linked;
        string peer = EdgeShareService.Shared.PeerName ?? "второй";
        root.Children.Add(StatusBanner(
            linked ? "На связи: " + peer : "Второй не на связи",
            linked));

        root.Children.Add(Head("Экраны"));
        root.Children.Add(TickRow(
            "Курсор на второй по краю",
            EdgeShareService.Shared.MouseCross,
            on =>
            {
                EdgeShareService.Shared.MouseCross = on;
                tip(on
                    ? "Курсор может уходить на второй ноутбук по краю."
                    : "Край закрыт. Курсор остаётся здесь.");
            },
            tip,
            "Пока включено, мышь у края уходит на второй. Выключи в игре, если не хочешь случайно уехать."));
        root.Children.Add(TickRow(
            "Связь двух Zwmenu",
            EdgeShareService.Shared.Running,
            on =>
            {
                if (on && !EdgeShareService.Shared.Running)
                    EdgeShareService.Shared.Start();
                else if (!on && EdgeShareService.Shared.Running)
                    EdgeShareService.Shared.Stop();
                tip(EdgeShareService.Shared.Status);
            },
            tip,
            "Ищет второй Zwmenu в сети и держит канал для мыши, файлов и пульта."));

        root.Children.Add(Head("Питание"));
        root.Children.Add(Grid2(
            Cell("Сон оба", () => tip(DeskControl.SleepBoth()), tip, "Оба компьютера уходят в сон."),
            Cell("Сон этот", () => tip(DeskControl.SleepLocal()), tip, "Только этот компьютер уходит в сон."),
            Cell("Перезагрузка оба", () => tip(DeskControl.RebootBoth()), tip, "Оба перезагрузятся через секунду."),
            Cell("Перезагрузка этот", () => tip(DeskControl.RebootLocal()), tip, "Только этот перезагрузится."),
            Cell("Выключить оба", () => tip(DeskControl.ShutdownBoth()), tip, "Оба выключатся через секунду."),
            Cell("Выключить этот", () => tip(DeskControl.ShutdownLocal()), tip, "Только этот выключится.")));

        root.Children.Add(Head("Звук"));
        float vol = DeskControl.GetMasterVolume();
        bool mute = DeskControl.GetMasterMute();
        var volLabel = ValueLabel(mute ? "Тишина" : Pct(vol));
        var volSlider = MakeSlider(0, 1, mute ? 0 : vol, tip, "Громкость этого компьютера.");
        volSlider.ValueChanged += (_, e) =>
        {
            DeskControl.SetMasterVolume((float)e.NewValue);
            if (DeskControl.GetMasterMute())
                DeskControl.SetMasterMute(false);
            volLabel.Text = Pct((float)e.NewValue);
        };
        root.Children.Add(SliderLine(volSlider, volLabel));
        root.Children.Add(Grid2(
            Cell(mute ? "Включить звук" : "Без звука", () =>
            {
                bool next = !DeskControl.GetMasterMute();
                DeskControl.SetMasterMute(next);
                tip(next ? "Звук этого компьютера выключен." : "Звук этого компьютера включён.");
            }, tip, "Быстро выключить или вернуть звук здесь."),
            Cell("Тишина оба", () =>
            {
                DeskControl.SetMasterMute(true);
                WorkshopHub.SendVolumeToPeer(DeskControl.GetMasterVolume(), true);
                tip("Тишина на обоих.");
            }, tip, "Выключает звук здесь и на втором.")));
        root.Children.Add(WideCell("Как здесь на второй", () =>
        {
            float level = DeskControl.GetMasterVolume();
            bool m = DeskControl.GetMasterMute();
            if (!WorkshopHub.SendVolumeToPeer(level, m))
                tip("Второй не принял громкость. Проверь связь.");
            else
                tip(m ? "Тишину отправил на второй." : "Громкость " + Pct(level) + " отправил на второй.");
        }, tip, "Текущая громкость и тишина копируются на второй."));

        var apps = new StackPanel();
        foreach (var app in DeskControl.ListAppVolumes().Take(5))
        {
            var label = ValueLabel(Trim(app.Name, 16), dim: true);
            label.Width = 108;
            var slider = MakeSlider(0, 1, app.Level, tip, "Громкость «" + app.Name + "».");
            string appId = app.Id;
            string appName = app.Name;
            slider.ValueChanged += (_, e) =>
            {
                DeskControl.SetAppVolume(appId, (float)e.NewValue);
                tip(appName + ": " + Pct((float)e.NewValue));
            };
            apps.Children.Add(SliderLine(slider, label, labelFirst: true));
        }

        if (apps.Children.Count == 0)
        {
            apps.Children.Add(new TextBlock
            {
                Text = "Сейчас нет программ со звуком.",
                Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
                FontSize = 11.5,
                Margin = new Thickness(0, 2, 0, 2)
            });
        }

        root.Children.Add(MakeHiddenScroll(apps, 72));

        root.Children.Add(Head("Яркость"));
        var lamps = DeskControl.ListScreens();
        if (lamps.Count == 0)
        {
            root.Children.Add(Dim("Яркость этим путём недоступна."));
        }
        else
        {
            int first = lamps[0].Level;
            var brightLabel = ValueLabel(first + "%");
            var all = MakeSlider(0, 100, first, tip, "Яркость на экранах, которыми умеем крутить.");
            all.ValueChanged += (_, e) =>
            {
                int level = (int)Math.Round(e.NewValue);
                DeskControl.SetAllBrightness(level);
                brightLabel.Text = level + "%";
                tip("Яркость: " + level + "%");
            };
            root.Children.Add(SliderLine(all, brightLabel));
            root.Children.Add(WideCell("Яркость оба как здесь", () =>
            {
                int level = (int)Math.Round(all.Value);
                DeskControl.SetAllBrightness(level);
                if (!WorkshopHub.SendBrightnessToPeer(level))
                    tip("Второй не принял яркость. Проверь связь.");
                else
                    tip("Яркость " + level + "% отправил на второй.");
            }, tip, "Ставит ту же яркость здесь и на втором."));
        }

        root.Children.Add(Head("Трансляция"));
        var liveNote = Dim(linked
            ? "Взгляд: один кадр. Трансляция: кадры подряд, пока не остановишь."
            : "Без связи с вторым кадры не придут. Включи связь выше.");
        root.Children.Add(liveNote);
        root.Children.Add(Grid2(
            Cell("Один кадр", () =>
            {
                if (!EdgeShareService.Shared.Linked)
                {
                    tip("Второй не на связи.");
                    return;
                }

                if (!WorkshopHub.Send(WorkshopHub.GlanceAsk, ReadOnlySpan<byte>.Empty))
                    tip("Запрос не ушёл.");
                else
                    tip("Жду один кадр со второго. Окно откроется само.");
            }, tip, "Один снимок экрана второго компьютера."),
            Cell("Трансляция", () =>
            {
                if (!EdgeShareService.Shared.Linked)
                {
                    tip("Второй не на связи.");
                    return;
                }

                GlanceWindow.MarkLive();
                if (!WorkshopHub.StartPeerLive())
                {
                    tip("Трансляцию не удалось начать. Связь оборвалась.");
                    return;
                }

                tip("Второй шлёт кадры. Окно «Экран второго» уже открыто.");
            }, tip, "Второй шлёт кадры экрана сюда, пока не остановишь.", accent: true),
            Cell("Стоп", () =>
            {
                WorkshopHub.StopPeerLive();
                tip("Трансляцию остановил.");
            }, tip, "Просит второй перестать слать кадры."),
            Cell("Мастерская", () =>
            {
                dismiss();
                WorkshopWindow.ShowCurrent();
            }, tip, "Окно мастерской: печать, сеть и прочее.")));

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x14, 0x16, 0x1A)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0x3A, 0x48, 0x52)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 10, 12, 12),
            Child = MakeHiddenScroll(root, 620),
            Width = 364,
            SnapsToDevicePixels = true
        };
    }

    static Border StatusBanner(string text, bool ok)
    {
        return new Border
        {
            Background = new SolidColorBrush(ok
                ? Color.FromArgb(0x55, 0x2A, 0x5A, 0x3A)
                : Color.FromArgb(0x55, 0x5A, 0x3A, 0x2A)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 0, 0, 8),
            Child = new TextBlock
            {
                Text = text,
                Foreground = TemplateTheme.Brush(TemplateTheme.Fg),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            }
        };
    }

    static UIElement Head(string text) => new TextBlock
    {
        Text = text,
        Foreground = TemplateTheme.Brush(TemplateTheme.AccentText),
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 10, 0, 6)
    };

    static TextBlock Dim(string text) => new()
    {
        Text = text,
        Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
        FontSize = 11.5,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 6)
    };

    static TextBlock ValueLabel(string text, bool dim = false) => new()
    {
        Text = text,
        Foreground = TemplateTheme.Brush(dim ? TemplateTheme.FgDim : TemplateTheme.Fg),
        FontSize = 12,
        Width = 56,
        TextAlignment = TextAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center
    };

    static UniformGrid Grid2(params Border[] cells)
    {
        var grid = new UniformGrid
        {
            Columns = 2,
            Margin = new Thickness(0, 0, 0, 2)
        };
        foreach (var cell in cells)
            grid.Children.Add(cell);
        return grid;
    }

    static Border WideCell(string text, Action click, Action<string> tip, string help, bool accent = false)
    {
        var border = Cell(text, click, tip, help, accent);
        border.Margin = new Thickness(2, 2, 2, 2);
        return border;
    }

    static Border Cell(string text, Action click, Action<string> tip, string help, bool accent = false)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = TemplateTheme.Brush(TemplateTheme.Fg),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            FontWeight = accent ? FontWeights.SemiBold : FontWeights.Normal,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var fill = accent ? TemplateTheme.Accent : TemplateTheme.BtnBg;
        var hover = accent ? TemplateTheme.AccentHover : TemplateTheme.BtnHover;
        var border = new Border
        {
            Background = TemplateTheme.Brush(fill),
            BorderBrush = TemplateTheme.Brush(TemplateTheme.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 9, 8, 9),
            Margin = new Thickness(3),
            MinHeight = 38,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = label,
            SnapsToDevicePixels = true
        };
        border.MouseEnter += (_, _) => border.Background = TemplateTheme.Brush(hover);
        border.MouseLeave += (_, _) => border.Background = TemplateTheme.Brush(fill);
        border.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            try { click(); } catch { /* кнопка не валит окно */ }
        };
        ArmTip(border, tip, help);
        return border;
    }

    static System.Windows.Controls.CheckBox TickRow(string text, bool on, Action<bool> change, Action<string> tip, string help)
    {
        var box = TemplateTheme.Tick(text);
        box.IsChecked = on;
        box.Margin = new Thickness(2, 2, 2, 4);
        box.Checked += (_, _) => change(true);
        box.Unchecked += (_, _) => change(false);
        ArmTip(box, tip, help);
        return box;
    }

    static Slider MakeSlider(double min, double max, double value, Action<string> tip, string help)
    {
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            VerticalAlignment = VerticalAlignment.Center,
            IsMoveToPointEnabled = true,
            Focusable = true,
            MinHeight = 28,
            Height = 28
        };
        ArmTip(slider, tip, help);
        return slider;
    }

    static DockPanel SliderLine(Slider slider, TextBlock label, bool labelFirst = false)
    {
        var row = new DockPanel { Margin = new Thickness(2, 2, 2, 4), LastChildFill = true };
        if (labelFirst)
        {
            DockPanel.SetDock(label, Dock.Left);
            label.Margin = new Thickness(0, 0, 8, 0);
            label.TextAlignment = TextAlignment.Left;
            row.Children.Add(label);
            row.Children.Add(slider);
        }
        else
        {
            DockPanel.SetDock(label, Dock.Right);
            label.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(label);
            row.Children.Add(slider);
        }

        return row;
    }

    static void ArmTip(FrameworkElement el, Action<string> tip, string help)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            tip(help);
        };
        el.MouseEnter += (_, _) =>
        {
            timer.Stop();
            timer.Start();
        };
        el.MouseLeave += (_, _) => timer.Stop();
    }

    static ScrollViewer MakeHiddenScroll(UIElement content, double height) =>
        new()
        {
            Content = content,
            MaxHeight = height,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            PanningMode = PanningMode.VerticalOnly,
            Focusable = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent
        };

    static string Pct(float v) => ((int)Math.Round(Math.Clamp(v, 0f, 1f) * 100)) + "%";

    static string Trim(string text, int max) =>
        text.Length <= max ? text : text[..Math.Max(1, max - 1)] + "...";
}
