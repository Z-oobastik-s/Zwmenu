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
        bool dualVol = linked;
        long lastPeerVol = 0;
        float pendingVol = DeskControl.GetMasterVolume();
        bool pendingMute = DeskControl.GetMasterMute();
        var peerVolTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        peerVolTimer.Tick += (_, _) =>
        {
            peerVolTimer.Stop();
            if (!dualVol || !EdgeShareService.Shared.Linked)
                return;
            WorkshopHub.SendVolumeToPeer(pendingVol, pendingMute);
        };

        void ApplyLocalVol(float level, bool muteNow, bool pushPeer)
        {
            level = Math.Clamp(level, 0f, 1f);
            DeskControl.SetMasterVolume(level);
            DeskControl.SetMasterMute(muteNow);
            pendingVol = level;
            pendingMute = muteNow;
            if (!pushPeer || !dualVol)
                return;
            long now = Environment.TickCount64;
            if (now - lastPeerVol >= 90)
            {
                lastPeerVol = now;
                peerVolTimer.Stop();
                if (EdgeShareService.Shared.Linked)
                    WorkshopHub.SendVolumeToPeer(level, muteNow);
            }
            else
            {
                peerVolTimer.Stop();
                peerVolTimer.Start();
            }
        }

        var dualTick = TickRow(
            "Громкость сразу на оба",
            dualVol,
            on =>
            {
                dualVol = on;
                tip(on
                    ? (linked
                        ? "Ползунок и тишина идут на оба компьютера."
                        : "Галка стоит, но второй не на связи. Сначала включи связь.")
                    : "Ползунок крутит только этот компьютер.");
            },
            tip,
            "Пока включено, ползунок и кнопки звука меняют оба компьютера. Нужна связь.");
        root.Children.Add(dualTick);

        float vol = DeskControl.GetMasterVolume();
        bool mute = DeskControl.GetMasterMute();
        var volLabel = ValueLabel(mute ? "Тишина" : Pct(vol));
        var volSlider = MakeSlider(0, 1, mute ? 0 : vol, tip, "Громкость. С галкой выше уходит и на второй.");
        volSlider.ValueChanged += (_, e) =>
        {
            float level = (float)e.NewValue;
            ApplyLocalVol(level, muteNow: false, pushPeer: true);
            volLabel.Text = Pct(level);
        };
        root.Children.Add(SliderLine(volSlider, volLabel));
        root.Children.Add(Grid2(
            Cell(mute ? "Включить звук" : "Без звука", () =>
            {
                bool next = !DeskControl.GetMasterMute();
                float level = DeskControl.GetMasterVolume();
                if (dualVol)
                    tip(DeskControl.SetMasterBoth(level, next));
                else
                {
                    DeskControl.SetMasterMute(next);
                    tip(next ? "Звук этого компьютера выключен." : "Звук этого компьютера включён.");
                }

                volLabel.Text = next ? "Тишина" : Pct(level);
            }, tip, "Тишина или звук. С галкой выше: на обоих."),
            Cell("Тишина оба", () =>
            {
                dualVol = true;
                dualTick.IsChecked = true;
                tip(DeskControl.SetMasterBoth(DeskControl.GetMasterVolume(), mute: true));
                volLabel.Text = "Тишина";
            }, tip, "Выключает звук здесь и на втором. Честно пишет, ушло ли.")));
        root.Children.Add(WideCell("Поставить оба как здесь", () =>
        {
            tip(DeskControl.SetMasterBoth(DeskControl.GetMasterVolume(), DeskControl.GetMasterMute()));
        }, tip, "Копирует текущую громкость и тишину на второй прямо сейчас."));

        var apps = new StackPanel();
        foreach (var app in DeskControl.ListAppVolumes().Take(5))
        {
            var name = new TextBlock
            {
                Text = Trim(app.Name, 28),
                Foreground = TemplateTheme.Brush(TemplateTheme.FgDim),
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(2, 0, 2, 0)
            };
            var pct = ValueLabel(Pct(app.Level));
            var slider = MakeSlider(0, 1, app.Level, tip, "Громкость «" + app.Name + "».");
            string appId = app.Id;
            string appName = app.Name;
            slider.ValueChanged += (_, e) =>
            {
                DeskControl.SetAppVolume(appId, (float)e.NewValue);
                pct.Text = Pct((float)e.NewValue);
                tip(appName + ": " + pct.Text);
            };
            var line = SliderLine(slider, pct);
            line.Margin = new Thickness(2, 0, 2, 6);
            apps.Children.Add(name);
            apps.Children.Add(line);
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

        root.Children.Add(MakeHiddenScroll(apps, 120));

        root.Children.Add(Head("Яркость"));
        var lamps = DeskControl.ListScreens();
        if (lamps.Count == 0)
        {
            root.Children.Add(Dim("Яркость этим путём недоступна."));
        }
        else
        {
            bool dualBright = linked;
            long lastPeerBright = 0;
            int pendingBright = lamps[0].Level;
            var peerBrightTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            peerBrightTimer.Tick += (_, _) =>
            {
                peerBrightTimer.Stop();
                if (!dualBright || !EdgeShareService.Shared.Linked)
                    return;
                WorkshopHub.SendBrightnessToPeer(pendingBright);
            };

            root.Children.Add(TickRow(
                "Яркость сразу на оба",
                dualBright,
                on =>
                {
                    dualBright = on;
                    tip(on
                        ? (linked
                            ? "Ползунок яркости идёт на оба."
                            : "Галка стоит, но второй не на связи.")
                        : "Ползунок яркости только здесь.");
                },
                tip,
                "Пока включено, яркость уходит и на второй компьютер."));

            int first = lamps[0].Level;
            var brightLabel = ValueLabel(first + "%");
            var all = MakeSlider(0, 100, first, tip, "Яркость. С галкой выше уходит и на второй.");
            all.ValueChanged += (_, e) =>
            {
                int level = (int)Math.Round(e.NewValue);
                DeskControl.SetAllBrightness(level);
                brightLabel.Text = level + "%";
                pendingBright = level;
                if (!dualBright)
                    return;
                long now = Environment.TickCount64;
                if (now - lastPeerBright >= 120)
                {
                    lastPeerBright = now;
                    peerBrightTimer.Stop();
                    if (EdgeShareService.Shared.Linked)
                        WorkshopHub.SendBrightnessToPeer(level);
                }
                else
                {
                    peerBrightTimer.Stop();
                    peerBrightTimer.Start();
                }
            };
            root.Children.Add(SliderLine(all, brightLabel));
            root.Children.Add(WideCell("Поставить оба как здесь", () =>
            {
                tip(DeskControl.SetBrightnessBoth((int)Math.Round(all.Value)));
            }, tip, "Ставит ту же яркость здесь и на втором. Честно пишет, ушло ли."));
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

        var scroll = MakeHiddenScroll(root, 620);
        return new Border
        {
            Width = 340,
            Child = scroll,
            Background = Brushes.Transparent,
            SnapsToDevicePixels = true
        };
    }

    static Border StatusBanner(string text, bool ok)
    {
        var fg = ok ? TemplateTheme.AccentText : Color.FromRgb(0xE7, 0xB4, 0xB0);
        var rail = new Border
        {
            Width = 3,
            Margin = new Thickness(0, 1, 8, 1),
            CornerRadius = new CornerRadius(1.5),
            Background = TemplateTheme.Brush(fg),
            Opacity = 0.9
        };
        DockPanel.SetDock(rail, Dock.Left);
        var row = new DockPanel { LastChildFill = true };
        row.Children.Add(rail);
        row.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = TemplateTheme.Brush(TemplateTheme.Fg),
            FontSize = 12,
            FontFamily = new FontFamily(UiTheme.Current.FontUi),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        return new Border
        {
            Background = TemplateTheme.Brush(ok ? TemplateTheme.StatusOk : TemplateTheme.StatusWarn),
            BorderBrush = TemplateTheme.Brush(Color.FromArgb(0x66, fg.R, fg.G, fg.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(UiTheme.Current.CornerChip),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 4, 0, 8),
            Child = row
        };
    }

    static UIElement Head(string text)
    {
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 6) };
        var mark = new Border
        {
            Width = 3,
            Height = 12,
            Margin = new Thickness(0, 1, 7, 0),
            CornerRadius = new CornerRadius(1.5),
            Background = TemplateTheme.Brush(TemplateTheme.AccentText),
            Opacity = 0.75,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(mark, Dock.Left);
        row.Children.Add(mark);
        row.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = TemplateTheme.Brush(TemplateTheme.AccentText),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily(UiTheme.Current.FontUi),
            VerticalAlignment = VerticalAlignment.Center
        });
        return row;
    }

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
            Foreground = TemplateTheme.Brush(accent ? TemplateTheme.AccentText : TemplateTheme.Fg),
            FontFamily = new FontFamily(UiTheme.Current.FontUi),
            FontSize = 12,
            FontWeight = accent ? FontWeights.SemiBold : FontWeights.Normal,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var fill = accent ? TemplateTheme.Accent : TemplateTheme.BtnBg;
        var hover = accent ? TemplateTheme.AccentHover : TemplateTheme.BtnHover;
        var edge = accent
            ? Color.FromArgb(0x66, TemplateTheme.AccentText.R, TemplateTheme.AccentText.G, TemplateTheme.AccentText.B)
            : TemplateTheme.Border;
        var border = new Border
        {
            Background = TemplateTheme.Brush(fill),
            BorderBrush = TemplateTheme.Brush(edge),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(UiTheme.Current.CornerChip),
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
