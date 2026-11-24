using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Zwmenu.Services;

namespace Zwmenu.Overlay;

/// <summary>Выезд пульта с колеса: край, питание, звук, яркость, трансляция.</summary>
static class DeskFlyout
{
    public static Border Build(Action<string> tip, Action dismiss)
    {
        var root = new StackPanel { Width = 320, Margin = new Thickness(10) };
        var note = new TextBlock
        {
            Text = EdgeShareService.Shared.Linked
                ? "Второй на связи: " + (EdgeShareService.Shared.PeerName ?? "ок")
                : "Второй не на связи. Часть кнопок сработает только здесь.",
            Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        root.Children.Add(note);

        root.Children.Add(Head("Два экрана"));
        var edge = Tick(
            EdgeShareService.Shared.MouseCross ? "Переход курсора включён" : "Переход курсора выключен",
            EdgeShareService.Shared.MouseCross,
            on =>
            {
                EdgeShareService.Shared.MouseCross = on;
                tip(on
                    ? "Курсор снова может уходить на второй ноутбук по краю."
                    : "Край закрыт. Курсор остаётся на этом экране.");
            },
            tip,
            "Пока включено, мышь у края уходит на второй ноутбук. Выключи, если играешь и не хочешь случайно уехать.");
        root.Children.Add(edge);

        var link = Tick(
            EdgeShareService.Shared.Running ? "Связь двух экранов включена" : "Связь двух экранов выключена",
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
            "Ищет второй Zwmenu в сети и держит канал для мыши, файлов и пульта.");
        root.Children.Add(link);

        root.Children.Add(Head("Питание"));
        root.Children.Add(Row(
            Pill("Сон оба", () => tip(DeskControl.SleepBoth()), tip, "Оба компьютера уходят в сон."),
            Pill("Сон этот", () => tip(DeskControl.SleepLocal()), tip, "Только этот компьютер уходит в сон.")));
        root.Children.Add(Row(
            Pill("Перезагрузка оба", () => tip(DeskControl.RebootBoth()), tip, "Оба компьютера перезагрузятся через секунду."),
            Pill("Перезагрузка этот", () => tip(DeskControl.RebootLocal()), tip, "Только этот компьютер перезагрузится.")));
        root.Children.Add(Row(
            Pill("Выключить оба", () => tip(DeskControl.ShutdownBoth()), tip, "Оба компьютера выключатся через секунду."),
            Pill("Выключить этот", () => tip(DeskControl.ShutdownLocal()), tip, "Только этот компьютер выключится.")));

        root.Children.Add(Head("Звук"));
        float vol = DeskControl.GetMasterVolume();
        bool mute = DeskControl.GetMasterMute();
        var volLabel = new TextBlock
        {
            Text = mute ? "Тишина" : ((int)Math.Round(vol * 100)) + "%",
            Foreground = TemplateTheme.Brush(TemplateTheme.Fg),
            FontSize = 12,
            Width = 64,
            VerticalAlignment = VerticalAlignment.Center
        };
        var volSlider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Value = vol,
            Width = 160,
            VerticalAlignment = VerticalAlignment.Center,
            IsMoveToPointEnabled = true
        };
        ArmTip(volSlider, tip, "Громкость этого компьютера. Ползунок сразу меняет звук.");
        volSlider.ValueChanged += (_, e) =>
        {
            DeskControl.SetMasterVolume((float)e.NewValue);
            if (DeskControl.GetMasterMute())
                DeskControl.SetMasterMute(false);
            volLabel.Text = ((int)Math.Round(e.NewValue * 100)) + "%";
        };
        var muteBtn = Pill(mute ? "Включить звук" : "Выключить звук", () =>
        {
            bool next = !DeskControl.GetMasterMute();
            DeskControl.SetMasterMute(next);
            tip(next ? "Звук этого компьютера выключен." : "Звук этого компьютера включён.");
        }, tip, "Быстро выключить или вернуть звук на этом компьютере.");
        root.Children.Add(Row(volSlider, volLabel));
        root.Children.Add(muteBtn);
        root.Children.Add(Row(
            Pill("Звук оба", () =>
            {
                float level = DeskControl.GetMasterVolume();
                bool m = DeskControl.GetMasterMute();
                WorkshopHub.SendVolumeToPeer(level, m);
                tip(m ? "Тишину отправил и сюда, и на второй." : "Громкость " + ((int)Math.Round(level * 100)) + "% отправил на второй.");
            }, tip, "Текущая громкость и тишина этого компьютера копируются на второй."),
            Pill("Тишина оба", () =>
            {
                DeskControl.SetMasterMute(true);
                WorkshopHub.SendVolumeToPeer(DeskControl.GetMasterVolume(), true);
                tip("Тишина на обоих.");
            }, tip, "Выключает звук здесь и просит второй сделать то же.")));

        var apps = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var app in DeskControl.ListAppVolumes())
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var name = new TextBlock
            {
                Text = Trim(app.Name, 18),
                Width = 110,
                Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            var slider = new Slider
            {
                Minimum = 0,
                Maximum = 1,
                Value = app.Level,
                Width = 120,
                VerticalAlignment = VerticalAlignment.Center,
                IsMoveToPointEnabled = true,
                Tag = app.Id
            };
            ArmTip(slider, tip, "Громкость «" + app.Name + "». Только на этом компьютере.");
            string appId = app.Id;
            string appName = app.Name;
            slider.ValueChanged += (_, e) =>
            {
                DeskControl.SetAppVolume(appId, (float)e.NewValue);
                tip(appName + ": " + ((int)Math.Round(e.NewValue * 100)) + "%");
            };
            DockPanel.SetDock(name, Dock.Left);
            row.Children.Add(name);
            row.Children.Add(slider);
            apps.Children.Add(row);
        }

        if (apps.Children.Count == 0)
        {
            apps.Children.Add(new TextBlock
            {
                Text = "Сейчас нет программ со звуком.",
                Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
                FontSize = 11.5
            });
        }

        root.Children.Add(MakeHiddenScroll(apps, 120));

        root.Children.Add(Head("Яркость"));
        var lamps = DeskControl.ListScreens();
        if (lamps.Count == 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = "Яркость этим путём недоступна.",
                Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
                FontSize = 11.5
            });
        }
        else
        {
            int first = lamps[0].Level;
            var all = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                Value = first,
                Width = 200,
                IsMoveToPointEnabled = true,
                Margin = new Thickness(0, 2, 0, 4)
            };
            ArmTip(all, tip, "Яркость сразу на всех экранах, которыми умеем крутить. На части внешних мониторов сработает через DDC.");
            all.ValueChanged += (_, e) =>
            {
                int level = (int)Math.Round(e.NewValue);
                DeskControl.SetAllBrightness(level);
                tip("Яркость этого компьютера: " + level + "%");
            };
            root.Children.Add(all);
            root.Children.Add(Pill("Яркость оба как здесь", () =>
            {
                int level = (int)Math.Round(all.Value);
                DeskControl.SetAllBrightness(level);
                WorkshopHub.SendBrightnessToPeer(level);
                tip("Яркость " + level + "% отправил и сюда, и на второй.");
            }, tip, "Ставит ту же яркость на этом компьютере и просит второй повторить."));

            foreach (var lamp in lamps)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var name = new TextBlock
                {
                    Text = Trim(lamp.Name, 16),
                    Width = 120,
                    Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var slider = new Slider
                {
                    Minimum = 0,
                    Maximum = 100,
                    Value = lamp.Level,
                    Width = 140,
                    IsEnabled = lamp.CanSet,
                    IsMoveToPointEnabled = true
                };
                string lampId = lamp.Id;
                string lampName = lamp.Name;
                ArmTip(slider, tip, lamp.CanSet
                    ? "Яркость «" + lamp.Name + "»."
                    : "Этот монитор яркость из программы не принимает. Кнопки на корпусе.");
                slider.ValueChanged += (_, e) =>
                {
                    int level = (int)Math.Round(e.NewValue);
                    DeskControl.SetBrightness(lampId, level);
                    tip(lampName + ": " + level + "%");
                };
                DockPanel.SetDock(name, Dock.Left);
                row.Children.Add(name);
                row.Children.Add(slider);
                root.Children.Add(row);
            }
        }

        root.Children.Add(Head("Трансляция"));
        root.Children.Add(Row(
            Pill("Взгляд на второй", () =>
            {
                if (!EdgeShareService.Shared.Linked)
                {
                    tip("Второй не на связи.");
                    return;
                }

                if (!WorkshopHub.Send(WorkshopHub.GlanceAsk, ReadOnlySpan<byte>.Empty))
                    tip("Запрос не ушёл.");
                else
                    tip("Прошу один кадр со второго.");
            }, tip, "Один снимок экрана второго компьютера."),
            Pill("Трансляция со второго", () =>
            {
                if (!EdgeShareService.Shared.Linked)
                {
                    tip("Второй не на связи.");
                    return;
                }

                GlanceWindow.MarkLive();
                if (!WorkshopHub.StartPeerLive())
                    tip("Трансляцию не удалось начать.");
                else
                    tip("Второй шлёт кадры. Окно откроется само.");
            }, tip, "Второй компьютер шлёт кадры экрана сюда, пока не остановишь.")));
        root.Children.Add(Row(
            Pill("Остановить трансляцию", () =>
            {
                WorkshopHub.StopPeerLive();
                tip("Трансляцию остановил.");
            }, tip, "Просит второй компьютер перестать слать кадры."),
            Pill("Мастерская", () =>
            {
                dismiss();
                WorkshopWindow.ShowCurrent();
            }, tip, "Старое окно мастерской: печать, сеть, уход и прочее.")));

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x12, 0x14, 0x18)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xAA, 0x3A, 0x4A, 0x52)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(4),
            Child = MakeHiddenScroll(root, 520),
            Width = 348
        };
    }

    static UIElement Head(string text) => new TextBlock
    {
        Text = text,
        Foreground = TemplateTheme.Brush(TemplateTheme.AccentText),
        FontSize = 12.5,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 10, 0, 4)
    };

    static StackPanel Row(params UIElement[] kids)
    {
        var row = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 2)
        };
        foreach (var kid in kids)
        {
            if (kid is FrameworkElement fe)
                fe.Margin = new Thickness(0, 0, 6, 0);
            row.Children.Add(kid);
        }

        return row;
    }

    static Border Pill(string text, Action click, Action<string> tip, string help)
    {
        var border = TemplateTheme.Pill(text, click);
        border.Margin = new Thickness(0, 2, 0, 2);
        ArmTip(border, tip, help);
        return border;
    }

    static System.Windows.Controls.CheckBox Tick(string text, bool on, Action<bool> change, Action<string> tip, string help)
    {
        var box = TemplateTheme.Tick(text);
        box.IsChecked = on;
        box.Margin = new Thickness(0, 2, 0, 4);
        box.Checked += (_, _) => change(true);
        box.Unchecked += (_, _) => change(false);
        ArmTip(box, tip, help);
        return box;
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
        el.MouseLeave += (_, _) =>
        {
            timer.Stop();
            tip("");
        };
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

    static string Trim(string text, int max) =>
        text.Length <= max ? text : text[..Math.Max(1, max - 1)] + "...";
}
