using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Zwmenu.Models;
using Zwmenu.Services;

namespace Zwmenu.Overlay;

public partial class RadialMenuWindow: Window
{
    private const double ButtonSize = 64;
    private const double CenterSize = 70;
    private const double OrbitRadius = 116;
    private static readonly FontFamily IconFont = new("Segoe MDL2 Assets");
    private static readonly Dictionary<int, ControlTemplate> TemplateCache = new();

    private readonly IReadOnlyList<MenuAction> _actions;
    private readonly Dictionary<Button, Brush> _restFills = new();
    private readonly Dictionary<Button, Brush> _hoverFills = new();
    private readonly List<FrameworkElement> _flyout = new();
    private Border? _flyoutPanel;
    private Border? _peekPanel;
    private Button? _flyoutParent;
    private string? _peekPath;
    private readonly List<string> _peekStack = new();
    private System.Windows.Threading.DispatcherTimer? _hideFlyoutTimer;
    private System.Windows.Threading.DispatcherTimer? _hintTimer;
    private MenuAction? _hintAction;
    private System.Windows.Threading.DispatcherTimer? _peekDebounce;
    private string? _pendingPeekPath;
    private bool _pendingPeekReset;
    private int _peekLoadGen;
    /// <summary>Пока превью открыто: соседние папки не перебивают его при проезде мыши к панели.</summary>
    private bool _peekSticky;
    private bool _foldersCompact;
    private MenuAction? _foldersGroupAction;
    private double _ringCx = 400;
    private double _ringCy = 350;
    private double _discClearR = 200;
    private Rect _clusterRect = Rect.Empty;
    private string? _loadedRingSkinPath;
    private bool _skinActive;
    private bool _buttonsBuilt;
    private readonly List<RingSlot> _slots = new();
    private readonly Dictionary<MenuAction, Border> _keptFlyouts = new();
    private int _peekSchedule;
    private double _orbitRadius = OrbitRadius;
    private double _slotSize = ButtonSize;
    private SkinFit? _cachedFit;
    private int _builtAtAppearanceRevision = int.MinValue;
    private double _lastWinW;
    private double _lastWinH;

    private sealed class RingSlot
    {
        public required Button Button { get; init; }
        public required MenuAction Action { get; init; }
        public double Angle { get; init; }
        public bool IsCenter { get; init; }
        public FrameworkElement? Chevron { get; set; }
    }

    public event EventHandler<MenuAction>? ActionChosen;
    public event EventHandler? Dismissed;

    public RadialMenuWindow(IReadOnlyList<MenuAction> actions)
    {
        InitializeComponent();
        _actions = actions;
        Cursor = System.Windows.Input.Cursors.Arrow;
        // Кнопки строим в Warmup/ShowAt: не дважды на старте.
        SourceInitialized += (_, _) => ApplyToolWindowStyle();
        // Нельзя закрывать по Deactivated: окно без активации + ShowActivated=false
        // получает ложный Deactivated и колесо исчезает в момент открытия.
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        MouseLeave += (_, _) => HideHint();
    }

    /// <summary>Прогрев после создания окна: кнопки + скин в кэше до первого СКМ.</summary>
    public void Warmup()
    {
        // Максимальный компактный размер из PlaceCompactWindow.
        // Иначе первый СКМ растягивает HWND и DWM заново собирает layered-окно.
        const double winW = 1300;
        const double winH = 980;
        Width = winW;
        Height = winH;
        RingCanvas.Width = winW;
        RingCanvas.Height = winH;
        _lastWinW = winW;
        _lastWinH = winH;
        _ringCx = 420;
        _ringCy = 490;
        EnsureButtonsForCurrentSettings();
        RingImageCache.WarmActiveSkin();
        if (_cachedFit != null)
            ApplyRingSkin(_ringCx, _ringCy, _cachedFit.ClearRadius, _cachedFit);
        UpdateLayout();
        PrepareFlyouts();
    }

    /// <summary>Выезды групп собираются до первого наведения, потом только показываются.</summary>
    private void PrepareFlyouts()
    {
        foreach (var slot in _slots)
        {
            if (slot.IsCenter || slot.Action.Kind != ActionKind.Group || slot.Action.IsProcessGroup)
                continue;
            if (slot.Action.Children.Count == 0)
                continue;
            ShowFlyout(slot.Button, slot.Action);
        }
        ClearFlyout();
        HideHintImmediate();
    }

    public void ShowAt(int screenX, int screenY)
    {
        ClearFlyout();
        HideHintImmediate();

        // Пересборка только если сохранили настройки внешнего вида. Иначе кнопки уже готовы.
        EnsureButtonsForCurrentSettings();

        // Компактное окно у курсора: без растягивания на весь монитор (это и давало лаг).
        PlaceCompactWindow(screenX, screenY);
        MoveRingToCenter();

        BeginAnimation(OpacityProperty, null);
        Opacity = 1;

        if (!IsVisible)
            Show();
        else
            Visibility = Visibility.Visible;
    }

    /// <summary>Строим кнопки один раз; снова только после AppearanceStore.Save (смена скина).</summary>
    private void EnsureButtonsForCurrentSettings()
    {
        int rev = AppearanceStore.Revision;
        if (_buttonsBuilt && _builtAtAppearanceRevision == rev)
            return;

        var (radius, slotSize, fit) = ComputeOrbitLayout();
        _orbitRadius = radius;
        _slotSize = slotSize;
        _cachedFit = fit;
        _skinActive = fit != null;
        double artClear = 0;
        if (fit == null && RingLooks.TryLayout(AppearanceStore.ActiveLook(), radius, out var artLay))
            artClear = artLay.Clear;
        _discClearR = fit?.ClearRadius ?? (artClear > 0 ? artClear : radius + slotSize * 0.55 + 54);
        BuildButtons();
        _builtAtAppearanceRevision = rev;
    }

    /// <summary>
    /// Окно ≈ кольцо + запас под flyout/hint. Не на весь экран:
    /// иначе WPF каждый СКМ layout'ит гигантский прозрачный surface.
    /// </summary>
    private void PlaceCompactWindow(int screenX, int screenY)
    {
        var workPx = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(screenX, screenY)).WorkingArea;
        Point topLeft = DeviceToDip(workPx.Left, workPx.Top);
        Point bottomRight = DeviceToDip(workPx.Right, workPx.Bottom);
        Point cursor = DeviceToDip(screenX, screenY);

        double workW = Math.Max(640, bottomRight.X - topLeft.X);
        double workH = Math.Max(480, bottomRight.Y - topLeft.Y);

        double clear = Math.Max(_discClearR, 200);
        // Справа заранее место под «Папки» + превью (~480), чтобы при наведении не расширять окно рывком.
        const double sideReserve = 520;
        double winW = Math.Min(workW, Math.Clamp(clear * 2 + sideReserve, 900, 1300));
        double winH = Math.Min(workH, Math.Clamp(clear * 2 + 300, 700, 980));

        // Кольцо слева: справа свободная полоса под панели без сдвига круга.
        double preferCx = Math.Min(clear + 28, winW - sideReserve - clear);
        preferCx = Math.Max(clear + 16, preferCx);
        double preferCy = winH * 0.5;

        double left = cursor.X - preferCx;
        double top = cursor.Y - preferCy;
        left = Math.Clamp(left, topLeft.X, topLeft.X + workW - winW);
        top = Math.Clamp(top, topLeft.Y, topLeft.Y + workH - winH);

        _ringCx = Math.Clamp(cursor.X - left, clear + 16, winW - clear - 16);
        _ringCy = Math.Clamp(cursor.Y - top, clear + 16, winH - clear - 16);

        if (Math.Abs(_lastWinW - winW) > 0.5 || Math.Abs(_lastWinH - winH) > 0.5)
        {
            Width = winW;
            Height = winH;
            RingCanvas.Width = winW;
            RingCanvas.Height = winH;
            _lastWinW = winW;
            _lastWinH = winH;
        }

        Left = left;
        Top = top;
    }

    private void MoveRingToCenter()
    {
        double cx = _ringCx;
        double cy = _ringCy;
        double radius = _orbitRadius;
        double slotSize = _slotSize;
        SyncDecorToOrbit(cx, cy, radius, slotSize, _cachedFit);

        int outerCount = 0;
        for (int i = 0; i < _slots.Count; i++)
            if (!_slots[i].IsCenter) outerCount++;
        int n = Math.Max(outerCount, 1);
        int oi = 0;
        RingSlot? center = null;
        for (int i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            if (slot.IsCenter)
            {
                center = slot;
                continue;
            }

            double angle = -Math.PI / 2 + oi * (2 * Math.PI / n);
            oi++;
            double bx = cx + Math.Cos(angle) * radius;
            double by = cy + Math.Sin(angle) * radius;
            Canvas.SetLeft(slot.Button, Math.Round(bx - slotSize / 2));
            Canvas.SetTop(slot.Button, Math.Round(by - slotSize / 2));

            if (slot.Chevron != null)
            {
                const double markSize = 14;
                double dist = slotSize * 0.5 + 11;
                Canvas.SetLeft(slot.Chevron, Math.Round(bx + Math.Cos(angle) * dist - markSize / 2));
                Canvas.SetTop(slot.Chevron, Math.Round(by + Math.Sin(angle) * dist - markSize / 2));
                if (slot.Chevron.RenderTransform is RotateTransform rt)
                    rt.Angle = angle * 180 / Math.PI;
            }
        }

        if (center != null)
        {
            Canvas.SetLeft(center.Button, Math.Round(cx - CenterSize / 2));
            Canvas.SetTop(center.Button, Math.Round(cy - CenterSize / 2));
        }
    }

    private Point DeviceToDip(double deviceX, double deviceY)
    {
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget != null)
            return src.CompositionTarget.TransformFromDevice.Transform(new Point(deviceX, deviceY));
        return new Point(deviceX, deviceY);
    }

    private void ClearRingItems()
    {
        for (int i = RingCanvas.Children.Count - 1; i >= 0; i--)
        {
            var child = RingCanvas.Children[i];
            if (ReferenceEquals(child, MatteDisc)
                || ReferenceEquals(child, OrbitRing)
                || ReferenceEquals(child, SoftGlow)
                || ReferenceEquals(child, RingSkin)
                || ReferenceEquals(child, LookAura)
                || ReferenceEquals(child, LookRing)
                || ReferenceEquals(child, LookCore)
                || ReferenceEquals(child, HintCard)
                || ReferenceEquals(child, _flyoutPanel)
                || ReferenceEquals(child, _peekPanel))
                continue;
            RingCanvas.Children.RemoveAt(i);
        }
        _restFills.Clear();
        _hoverFills.Clear();
        _slots.Clear();
        _buttonsBuilt = false;
    }

    private (double Radius, double SlotSize, SkinFit? Fit) ComputeOrbitLayout()
    {
        var outer = _actions.Where(a => a.Tag > 0).OrderBy(a => a.Tag).ToList();
        int n = Math.Max(outer.Count, 1);
        double outerSize = n switch
        {
            > 12 => 48,
            > 10 => 52,
            > 8 => 58,
            _ => ButtonSize
        };

        double gap = 10;
        double minRadius = (outerSize + gap) / (2 * Math.Sin(Math.PI / Math.Max(n, 2)));
        double radius = Math.Clamp(Math.Max(minRadius, OrbitRadius), OrbitRadius, 130);

        SkinFit? skinFit = AppearanceStore.TryFitActiveSkin(radius, outerSize, CenterSize);
        if (skinFit != null)
        {
            radius = Math.Clamp(skinFit.OrbitRadius, OrbitRadius * 0.85, 210);
            if (n >= 8 && radius > 150)
                outerSize = Math.Max(44, outerSize - 4);
        }

        return (radius, outerSize, skinFit);
    }

    private void BuildButtons()
    {
        ClearRingItems();
        double cx = _ringCx;
        double cy = _ringCy;
        var outer = _actions.Where(a => a.Tag > 0).OrderBy(a => a.Tag).ToList();
        int n = Math.Max(outer.Count, 1);
        double radius = _orbitRadius;
        double outerSize = _slotSize;
        SkinFit? skinFit = _cachedFit;

        SyncDecorToOrbit(cx, cy, radius, outerSize, skinFit);

        for (int i = 0; i < outer.Count; i++)
        {
            var action = outer[i];
            double angle = -Math.PI / 2 + i * (2 * Math.PI / n);
            double bx = cx + Math.Cos(angle) * radius;
            double by = cy + Math.Sin(angle) * radius;
            double x = Math.Round(bx - outerSize / 2);
            double y = Math.Round(by - outerSize / 2);

            var btn = CreateButton(action, outerSize, center: false, isFlyout: false);
            Canvas.SetLeft(btn, x);
            Canvas.SetTop(btn, y);
            Panel.SetZIndex(btn, 10);
            RingCanvas.Children.Add(btn);

            FrameworkElement? chevron = null;
            if (action.Children.Count > 0 || action.Kind == ActionKind.Group)
                chevron = PlaceGroupChevron(bx, by, angle, outerSize);

            _slots.Add(new RingSlot
            {
                Button = btn,
                Action = action,
                Angle = angle,
                IsCenter = false,
                Chevron = chevron
            });
        }

        var centerAction = _actions.FirstOrDefault(a => a.Tag == 0);
        if (centerAction != null)
        {
            double x = Math.Round(cx - CenterSize / 2);
            double y = Math.Round(cy - CenterSize / 2);
            var btn = CreateButton(centerAction, CenterSize, center: true, isFlyout: false);
            Canvas.SetLeft(btn, x);
            Canvas.SetTop(btn, y);
            Panel.SetZIndex(btn, 20);
            RingCanvas.Children.Add(btn);
            _slots.Add(new RingSlot
            {
                Button = btn,
                Action = centerAction,
                Angle = 0,
                IsCenter = true
            });
        }

        _buttonsBuilt = true;
    }

    /// <summary>Диск и кольцо ровно в тех же координатах, что и центры кнопок.</summary>
    private void SyncDecorToOrbit(double cx, double cy, double radius, double slotSize, SkinFit? skinFit)
    {
        var look = AppearanceStore.ActiveLook();
        ApplyLookChrome(look);

        double discR = radius + slotSize * 0.55 + 18;
        if (skinFit != null)
        {
            // Под скином диск только под центр: не перекрывает орнамент рамки.
            discR = Math.Min(discR, Math.Max(CenterSize * 0.72, skinFit.SkinRadius * skinFit.Metrics.Inner * 0.82));
        }

        double discD = discR * 2;
        MatteDisc.Width = discD;
        MatteDisc.Height = discD;
        Canvas.SetLeft(MatteDisc, Math.Round(cx - discR));
        Canvas.SetTop(MatteDisc, Math.Round(cy - discR));

        double orbitD = radius * 2;
        OrbitRing.Width = orbitD;
        OrbitRing.Height = orbitD;
        OrbitRing.StrokeThickness = look.OrbitThickness;
        Canvas.SetLeft(OrbitRing, Math.Round(cx - radius));
        Canvas.SetTop(OrbitRing, Math.Round(cy - radius));

        double glowR = (skinFit?.ClearRadius ?? (discR + 36));
        SoftGlow.Width = glowR * 2;
        SoftGlow.Height = glowR * 2;
        Canvas.SetLeft(SoftGlow, Math.Round(cx - glowR));
        Canvas.SetTop(SoftGlow, Math.Round(cy - glowR));

        ApplyRingSkin(cx, cy, glowR, skinFit);
        ApplyLookArt(cx, cy, radius, skinFit);
    }

    private void ApplyLookChrome(RingLookPalette look)
    {
        SoftGlow.Fill = look.SoftGlow;
        MatteDisc.Fill = look.MatteDisc;
        OrbitRing.Stroke = look.OrbitStroke;
        HintCard.Background = look.HintBg;
        HintCard.BorderBrush = look.HintBorder;
        HintTitle.Foreground = look.HintTitle;
        HintBody.Foreground = look.HintBody;
    }

    private void ApplyRingSkin(double cx, double cy, double glowR, SkinFit? skinFit)
    {
        if (skinFit == null)
        {
            RingSkin.Visibility = Visibility.Collapsed;
            SoftGlow.Visibility = Visibility.Visible;
            SoftGlow.Opacity = 1;
            OrbitRing.Visibility = Visibility.Visible;
            OrbitRing.Opacity = 1;
            MatteDisc.Visibility = Visibility.Visible;
            MatteDisc.Opacity = 1;
            _discClearR = glowR + 16;
            return;
        }

        double skinR = skinFit.SkinRadius;
        double skinD = skinR * 2;
        RingSkin.Width = skinD;
        RingSkin.Height = skinD;
        Canvas.SetLeft(RingSkin, Math.Round(cx - skinR));
        Canvas.SetTop(RingSkin, Math.Round(cy - skinR));

        string path = skinFit.Path;
        if (!string.Equals(_loadedRingSkinPath, path, StringComparison.OrdinalIgnoreCase)
            || RingSkin.Source == null)
        {
            var bmp = RingImageCache.GetSkin(path);
            if (bmp == null)
            {
                RingSkin.Visibility = Visibility.Collapsed;
                SoftGlow.Visibility = Visibility.Visible;
                SoftGlow.Opacity = 1;
                OrbitRing.Visibility = Visibility.Visible;
                OrbitRing.Opacity = 1;
                MatteDisc.Visibility = Visibility.Visible;
                MatteDisc.Opacity = 1;
                _discClearR = glowR + 16;
                _skinActive = false;
                return;
            }

            RingSkin.Source = bmp;
            _loadedRingSkinPath = path;
        }

        RingSkin.Visibility = Visibility.Visible;
        // Collapsed, не Opacity=0: иначе огромный SoftGlow всё равно участвует в layout/compose.
        SoftGlow.Visibility = Visibility.Collapsed;
        OrbitRing.Visibility = Visibility.Collapsed;
        MatteDisc.Visibility = Visibility.Collapsed;
        _discClearR = Math.Max(glowR, skinFit.ClearRadius);
    }

    private string? _lookAuraPath;
    private string? _lookRingPath;
    private string? _lookCorePath;

    /// <summary>Рисунки вида: свечение сзади, кольцо под кнопками, центр под главной кнопкой.</summary>
    private void ApplyLookArt(double cx, double cy, double radius, SkinFit? skinFit)
    {
        bool skinOn = skinFit != null && RingSkin.Visibility == Visibility.Visible;
        var look = AppearanceStore.ActiveLook();
        if (skinOn || !RingLooks.TryLayout(look, radius, out var lay))
        {
            HideLookArt();
            return;
        }

        bool ok = ShowLookLayer(LookAura, RingLooks.LayerPath(look, "aura"), ref _lookAuraPath, cx, cy, lay.AuraRadius * 2, lay.AuraOpacity)
            && ShowLookLayer(LookRing, RingLooks.LayerPath(look, "ring"), ref _lookRingPath, cx, cy, lay.RingRadius * 2, 1)
            && ShowLookLayer(LookCore, RingLooks.LayerPath(look, "core"), ref _lookCorePath, cx, cy, lay.CoreSize, 1);
        if (!ok)
        {
            HideLookArt();
            SoftGlow.Visibility = Visibility.Visible;
            OrbitRing.Visibility = Visibility.Visible;
            MatteDisc.Visibility = Visibility.Visible;
            return;
        }

        SoftGlow.Visibility = Visibility.Collapsed;
        OrbitRing.Visibility = Visibility.Collapsed;
        MatteDisc.Visibility = Visibility.Collapsed;
        _discClearR = lay.Clear;
    }

    private void HideLookArt()
    {
        LookAura.Visibility = Visibility.Collapsed;
        LookRing.Visibility = Visibility.Collapsed;
        LookCore.Visibility = Visibility.Collapsed;
    }

    private static bool ShowLookLayer(
        System.Windows.Controls.Image image,
        string? path,
        ref string? loaded,
        double cx,
        double cy,
        double size,
        double opacity)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (!string.Equals(loaded, path, StringComparison.OrdinalIgnoreCase) || image.Source == null)
        {
            var bmp = RingImageCache.GetSkin(path, size > 200 ? 900 : 420);
            if (bmp == null)
                return false;
            image.Source = bmp;
            loaded = path;
        }

        image.Width = size;
        image.Height = size;
        image.Opacity = opacity;
        Canvas.SetLeft(image, Math.Round(cx - size / 2));
        Canvas.SetTop(image, Math.Round(cy - size / 2));
        image.Visibility = Visibility.Visible;
        return true;
    }

    /// <summary>Стрелка снаружи слота, по радиусу наружу: не в углу кнопки.</summary>
    private FrameworkElement PlaceGroupChevron(double bx, double by, double angle, double slotSize)
    {
        const double markSize = 14;
        double dist = slotSize * 0.5 + 11;
        double mx = Math.Round(bx + Math.Cos(angle) * dist - markSize / 2);
        double my = Math.Round(by + Math.Sin(angle) * dist - markSize / 2);

        var mark = new TextBlock
        {
            Text = "\uE76C",
            FontFamily = IconFont,
            FontSize = 11,
            Width = markSize,
            Height = markSize,
            Foreground = Solid(ChevronColor()),
            TextAlignment = System.Windows.TextAlignment.Center,
            IsHitTestVisible = false,
            // Поворот: стрелка MDL2 смотрит вправо: крутим по касательной наружу.
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(angle * 180 / Math.PI)
        };

        Canvas.SetLeft(mark, mx);
        Canvas.SetTop(mark, my);
        Panel.SetZIndex(mark, 15);
        RingCanvas.Children.Add(mark);
        return mark;
    }

    private Button CreateButton(MenuAction action, double size, bool center, bool isFlyout)
    {
        // На колесе (орбита + центр): только иконка/глиф, без круглого диска.
        // Подложку оставляем только строкам выезда.
        bool onWheel = !isFlyout;
        bool fileIcon = action.HasFileIcon && !center;
        Brush rest;
        Brush hover;
        if (onWheel || fileIcon)
        {
            rest = Brushes.Transparent;
            hover = Brushes.Transparent;
        }
        else
        {
            var baseColor = ParseTint(action.Tint);
            rest = Solid(baseColor);
            hover = Solid(Lift(baseColor, 1.14));
        }

        UIElement content = CreateIconVisual(action, size, center, isFlyout);

        var btn = new Button
        {
            Width = size,
            Height = size,
            MinWidth = size,
            MinHeight = size,
            MaxWidth = size,
            MaxHeight = size,
            Content = content,
            Background = rest,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Tag = action,
            Focusable = false,
            IsHitTestVisible = true,
            // false: иначе лёгкий scale при наведении обрежется шаблоном.
            ClipToBounds = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
            Template = (onWheel || fileIcon) ? GetFlatTemplate(size) : GetRoundTemplate(size)
        };

        _restFills[btn] = rest;
        _hoverFills[btn] = hover;

        btn.Click += (_, e) =>
        {
            if (!isFlyout && action.Kind == ActionKind.Group)
            {
                e.Handled = true;
                // Процессы: клик = диспетчер задач; наведение отдельно выезжает список.
                if (action.IsProcessGroup)
                {
                    ActionChosen?.Invoke(this, new MenuAction
                    {
                        Label = "Диспетчер задач",
                        Kind = ActionKind.Launch,
                        Value = "taskmgr.exe"
                    });
                    return;
                }

                if (action.IsDeskPanel || action.Children.Count > 0)
                    ShowFlyout(btn, action);
                return;
            }

            ActionChosen?.Invoke(this, action);
        };

        btn.MouseEnter += (_, _) =>
        {
            if (isFlyout)
                ApplyFill(btn, hover: true);
            else
                AnimateIconHover(btn, on: true);
            if (isFlyout)
                CancelHideFlyout();
            else if (action.IsDeskPanel || action.IsProcessGroup || action.Children.Count > 0)
                ShowFlyout(btn, action);
            else
                ClearFlyout();
            if (!isFlyout)
                ArmHint(action);
        };
        btn.MouseLeave += (_, _) =>
        {
            if (isFlyout)
                ApplyFill(btn, hover: false);
            else
                AnimateIconHover(btn, on: false);
            if (isFlyout || action.IsDeskPanel || action.IsProcessGroup || action.Children.Count > 0 || action.Kind == ActionKind.Group)
                ScheduleHideFlyout();
            CancelHint();
        };

        return btn;
    }

    private static void AnimateIconHover(Button btn, bool on)
    {
        if (btn.RenderTransform is not ScaleTransform st)
        {
            st = new ScaleTransform(1, 1);
            btn.RenderTransformOrigin = new Point(0.5, 0.5);
            btn.RenderTransform = st;
        }

        double to = on ? 1.14 : 1.0;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        st.ScaleX = to;
        st.ScaleY = to;
    }

    private static Color ChevronColor()
    {
        var look = AppearanceStore.ActiveLook();
        return string.IsNullOrEmpty(look.ArtFolder)
            ? Color.FromArgb(0xDD, 0xF5, 0xF0, 0xE8)
            : look.IconColor;
    }

    private static UIElement CreateIconVisual(MenuAction action, double size, bool center, bool isFlyout)
    {
        var look = AppearanceStore.ActiveLook();
        bool onWheel = !isFlyout;
        bool themed = onWheel && !string.IsNullOrEmpty(look.ArtFolder);
        var ink = themed ? look.IconColor : Color.FromRgb(0xF5, 0xF0, 0xE8);

        UIElement visual;
        if (action.HasFileIcon)
        {
            double iconSize = isFlyout ? 32 : Math.Min(themed ? size * 0.40 : 40, size * 0.62);
            var bitmap = RingImageCache.GetIcon(action.IconPath);
            if (bitmap != null)
            {
                visual = new System.Windows.Controls.Image
                {
                    Source = bitmap,
                    Width = iconSize,
                    Height = iconSize,
                    MaxWidth = iconSize,
                    MaxHeight = iconSize,
                    Stretch = Stretch.Uniform,
                    SnapsToDevicePixels = true,
                    UseLayoutRounding = true,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    IsHitTestVisible = false
                };
            }
            else
            {
                visual = GlyphBlock(action, size, center, isFlyout, onWheel, ink);
            }
        }
        else
        {
            visual = GlyphBlock(action, size, center, isFlyout, onWheel, ink);
        }

        if (!themed)
            return visual;

        // Тарелка меньше кнопки и сидит внутри плотной части кольца, не на шипах.
        double plate = size * (center ? 0.50 : 0.58);
        var disc = new System.Windows.Shapes.Ellipse
        {
            Width = plate,
            Height = plate,
            Fill = Solid(look.PlateColor),
            Stroke = Solid(look.IconColor),
            StrokeThickness = center ? 1.4 : 1.7,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        var inner = new System.Windows.Shapes.Ellipse
        {
            Width = plate * 0.72,
            Height = plate * 0.72,
            Stroke = Solid(look.IconColor),
            StrokeThickness = 1,
            Opacity = 0.55,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        var host = new Grid
        {
            Width = size,
            Height = size,
            IsHitTestVisible = false
        };
        host.Children.Add(disc);
        host.Children.Add(inner);
        host.Children.Add(visual);
        return host;
    }

    private static TextBlock GlyphBlock(MenuAction action, double size, bool center, bool isFlyout, bool onWheel, Color ink)
    {
        double font = center ? (onWheel ? 18 : 28) : (isFlyout ? 18 : (string.IsNullOrEmpty(AppearanceStore.ActiveLook().ArtFolder) ? 26 : 17));
        return new TextBlock
        {
            Text = string.IsNullOrEmpty(action.Glyph) ? "\uE8B7" : action.Glyph,
            FontFamily = IconFont,
            FontSize = font,
            Foreground = onWheel ? Solid(ink) : Brushes.White,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            IsHitTestVisible = false
        };
    }

    /// <summary>
    /// Выезд без скролла: всё видно сразу. Много пунктов → больше колонок, панель шире.
    /// </summary>
    private void ShowFlyout(Button parent, MenuAction action)
    {
        CancelHideFlyout();
        if (ReferenceEquals(_flyoutParent, parent))
            return;

        if (!action.IsProcessGroup && _keptFlyouts.TryGetValue(action, out var kept))
        {
            DetachFlyout(destroyDynamic: true);
            _flyoutParent = parent;
            _flyoutPanel = kept;
            if (!RingCanvas.Children.Contains(kept))
                RingCanvas.Children.Add(kept);
            if (action.IsFoldersGroup)
            {
                _foldersGroupAction = action;
                _foldersCompact = kept.Tag is true;
                LayoutFolderCluster();
            }
            else
            {
                kept.Measure(new System.Windows.Size(kept.Width, double.PositiveInfinity));
                AttachFlyout(parent, kept, kept.Width, Math.Max(kept.DesiredSize.Height, 40));
            }
            return;
        }

        ClearFlyout();

        if (action.IsProcessGroup)
        {
            ShowProcessFlyout(parent, action);
            return;
        }

        if (action.IsDeskPanel)
        {
            ShowDeskFlyout(parent, action);
            return;
        }

        if (action.Children.Count == 0)
            return;

        _flyoutParent = parent;
        bool folders = action.IsFoldersGroup;

        if (folders)
        {
            _foldersGroupAction = action;
            // Сразу узкий список: широкий грид наезжал на круг в компактном окне.
            _foldersCompact = true;
            _flyoutPanel = BuildFoldersPanel(action, compact: true);
            RingCanvas.Children.Add(_flyoutPanel);
            _flyout.Add(_flyoutPanel);
            Panel.SetZIndex(_flyoutPanel, 50);
            _flyoutPanel.Tag = true;
            _keptFlyouts[action] = _flyoutPanel;
            LayoutFolderCluster();
            return;
        }

        int n = action.Children.Count;
        const double pad = 10;
        const double gap = 6;
        const double rowH = 40;
        const double iconBox = 32;

        int cols = n switch { <= 8 => 1, <= 14 => 2, _ => 3 };
        double tileW = 200;
        double innerW = cols * tileW + (cols - 1) * gap;
        double panelW = innerW + pad * 2;

        var grid = new UniformGrid
        {
            Columns = cols,
            Rows = (int)Math.Ceiling(n / (double)cols)
        };

        foreach (var child in action.Children)
        {
            FrameworkElement cell = CreateFlyoutRow(child, tileW - 2, rowH, iconBox);
            cell.Margin = new Thickness(gap / 2);
            grid.Children.Add(cell);
            _flyout.Add(cell);
        }

        var root = new StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical };
        root.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(action.Label) ? "Ярлыки" : action.Label,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Solid(Color.FromArgb(0x99, 0xF5, 0xF0, 0xE8)),
            Margin = new Thickness(4, 0, 4, 8),
            IsHitTestVisible = false
        });
        root.Children.Add(grid);

        _flyoutPanel = new Border
        {
            Width = panelW,
            Padding = new Thickness(pad),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            BorderBrush = Solid(Color.FromArgb(0x40, 0xF5, 0xF0, 0xE8)),
            Background = Solid(Color.FromArgb(0xF0, 0x16, 0x16, 0x18)),
            Child = root,
            SnapsToDevicePixels = true,
            Opacity = 0,
            RenderTransformOrigin = new Point(0, 0.5),
            RenderTransform = new TranslateTransform(-12, 0)
        };

        _flyoutPanel.Measure(new System.Windows.Size(panelW, double.PositiveInfinity));
        double panelH = _flyoutPanel.DesiredSize.Height;
        _keptFlyouts[action] = _flyoutPanel;
        AttachFlyout(parent, _flyoutPanel, panelW, panelH);
    }

    /// <summary>Сетка папок. compact=true: узкий список, когда справа открыто превью.</summary>
    private Border BuildFoldersPanel(MenuAction action, bool compact)
    {
        const double pad = 10;
        const double gap = 6;
        int n = action.Children.Count;

        int cols;
        double tileW;
        double tileH;
        if (compact)
        {
            cols = 1;
            tileW = 158;
            tileH = 40;
        }
        else
        {
            cols = n switch { <= 6 => 2, <= 12 => 3, _ => 3 };
            tileW = 128;
            tileH = 48;
        }

        double innerW = cols * tileW + (cols - 1) * gap;
        double panelW = innerW + pad * 2;
        int rows = Math.Max(1, (int)Math.Ceiling(Math.Max(n, 1) / (double)cols));

        var grid = new UniformGrid { Columns = cols, Rows = rows };
        foreach (var child in action.Children)
        {
            var cell = CreateFolderTile(child, tileW - 2, tileH, peekable: true);
            cell.Margin = new Thickness(gap / 2);
            grid.Children.Add(cell);
            _flyout.Add(cell);
        }

        double maxGridH = Math.Clamp(RingCanvas.Height - 120, 160, compact ? 520 : 380);
        double gridH = rows * tileH + Math.Max(0, rows - 1) * gap;
        UIElement body = grid;
        if (gridH > maxGridH || n > cols * 8)
            body = MakeHiddenScroll(grid, maxGridH);

        var root = new StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical };
        root.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(action.Label) ? "Папки" : action.Label,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Solid(Color.FromArgb(0x99, 0xF5, 0xF0, 0xE8)),
            Margin = new Thickness(4, 0, 4, 8),
            IsHitTestVisible = false
        });
        root.Children.Add(body);

        var panel = new Border
        {
            Width = panelW,
            Padding = new Thickness(pad),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            BorderBrush = Solid(Color.FromArgb(0x40, 0xF5, 0xF0, 0xE8)),
            Background = Solid(Color.FromArgb(0xF0, 0x16, 0x16, 0x18)),
            Child = root,
            SnapsToDevicePixels = true,
            Opacity = 1
        };
        panel.MouseEnter += FlyoutKeepAliveEnter;
        panel.MouseLeave += FlyoutKeepAliveLeave;
        panel.Measure(new System.Windows.Size(panelW, double.PositiveInfinity));
        return panel;
    }

    /// <summary>Сжать/развернуть список папок и переставить кластер рядом с кругом (не поверх).</summary>
    private void SetFoldersCompactMode(bool compact)
    {
        if (_foldersGroupAction == null || _flyoutParent == null)
            return;
        if (_foldersCompact == compact && _flyoutPanel != null)
        {
            LayoutFolderCluster();
            return;
        }

        _foldersCompact = compact;
        if (_flyoutPanel != null)
        {
            RingCanvas.Children.Remove(_flyoutPanel);
            _flyout.Remove(_flyoutPanel);
        }

        // Убрать старые плитки папок из трекинга (превью оставляем).
        for (int i = _flyout.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(_flyout[i], _peekPanel))
                _flyout.RemoveAt(i);
        }

        _flyoutPanel = BuildFoldersPanel(_foldersGroupAction, compact);
        RingCanvas.Children.Add(_flyoutPanel);
        _flyout.Add(_flyoutPanel);
        Panel.SetZIndex(_flyoutPanel, 50);
        _flyoutPanel.Tag = compact;
        _keptFlyouts[_foldersGroupAction] = _flyoutPanel;
        LayoutFolderCluster();
    }

    /// <summary>
    /// Папки + превью строго справа от круга. Если места мало — расширяем окно,
    /// круг двигаем влево. Никогда не кладём панели поверх кольца.
    /// </summary>
    private void LayoutFolderCluster()
    {
        if (_flyoutPanel == null)
            return;

        const double margin = 8;
        const double gapRing = 10;
        const double overlap = 2;
        const double hintReserve = 70;

        _flyoutPanel.Measure(new System.Windows.Size(_flyoutPanel.Width, double.PositiveInfinity));
        double fw = _flyoutPanel.Width;
        double fh = Math.Max(_flyoutPanel.DesiredSize.Height, 80);
        if (_flyoutPanel.MaxHeight > 0 && !double.IsInfinity(_flyoutPanel.MaxHeight))
            fh = Math.Min(fh, _flyoutPanel.MaxHeight);

        double pw = 0;
        double ph = 0;
        if (_peekPanel != null)
        {
            _peekPanel.Measure(new System.Windows.Size(_peekPanel.Width, double.PositiveInfinity));
            pw = _peekPanel.Width;
            ph = Math.Max(_peekPanel.DesiredSize.Height, 80);
            if (_peekPanel.MaxHeight > 0 && !double.IsInfinity(_peekPanel.MaxHeight))
                ph = Math.Min(ph, _peekPanel.MaxHeight);
        }

        double totalW = fw + (pw > 0 ? pw - overlap : 0);
        double clusterH = Math.Max(fh, ph > 0 ? ph : fh);

        // Место справа от круга под список (+ превью): при нехватке растём окно, не наезжаем.
        EnsureSpaceBesideRing(totalW + gapRing + margin, clusterH + hintReserve + 2 * margin);

        double canvasH = RingCanvas.Height;
        double maxH = Math.Max(120, canvasH - 2 * margin - hintReserve);
        if (fh > maxH)
        {
            _flyoutPanel.MaxHeight = maxH;
            fh = maxH;
        }
        if (_peekPanel != null && ph > maxH)
        {
            _peekPanel.MaxHeight = maxH;
            ph = maxH;
        }
        clusterH = Math.Max(fh, ph > 0 ? ph : fh);

        double left = _ringCx + _discClearR + gapRing;
        double top = Math.Clamp(_ringCy - clusterH / 2, margin, Math.Max(margin, canvasH - clusterH - hintReserve - margin));

        Canvas.SetLeft(_flyoutPanel, Math.Round(left));
        Canvas.SetTop(_flyoutPanel, Math.Round(top));
        Panel.SetZIndex(_flyoutPanel, 50);
        _flyoutPanel.Width = fw;
        _flyoutPanel.Arrange(new Rect(left, top, fw, fh));

        if (_peekPanel != null)
        {
            double peekLeft = left + fw - overlap;
            Canvas.SetLeft(_peekPanel, Math.Round(peekLeft));
            Canvas.SetTop(_peekPanel, Math.Round(top));
            Panel.SetZIndex(_peekPanel, 55);
            _peekPanel.Arrange(new Rect(peekLeft, top, pw, Math.Max(ph, fh)));
        }

        _clusterRect = new Rect(left, top, totalW, clusterH);
        PlaceHintCard();
    }

    /// <summary>
    /// Полоса справа от кольца под панели. Круг на экране не двигаем:
    /// только расширяем окно вправо/влево/вниз. Иначе при наведении на «Папки»
    /// кольцо дёргается на долю секунды.
    /// </summary>
    private void EnsureSpaceBesideRing(double needRight, double needH)
    {
        double clear = Math.Max(_discClearR, 200);
        double ringRight = _ringCx + clear;
        double curW = _lastWinW > 0 ? _lastWinW : Width;
        double curH = _lastWinH > 0 ? _lastWinH : Height;
        double spaceRight = curW - ringRight - 8;
        double spaceBelow = curH - 16;

        if (spaceRight >= needRight - 0.5 && spaceBelow >= needH - 0.5)
            return;

        var workPx = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point(
                (int)Math.Round(Left + _ringCx),
                (int)Math.Round(Top + _ringCy))).WorkingArea;
        Point topLeft = DeviceToDip(workPx.Left, workPx.Top);
        Point bottomRight = DeviceToDip(workPx.Right, workPx.Bottom);
        double workRight = bottomRight.X;
        double workLeft = topLeft.X;
        double workBottom = bottomRight.Y;
        double workTop = topLeft.Y;

        // Экранная позиция центра кольца: якорь, его не трогаем.
        double screenRingX = Left + _ringCx;
        double screenRingY = Top + _ringCy;

        double deficitR = Math.Max(0, needRight - spaceRight);
        double growRight = Math.Min(deficitR, Math.Max(0, workRight - (Left + curW)));
        double still = deficitR - growRight;
        double growLeft = Math.Min(still, Math.Max(0, Left - workLeft));

        double deficitH = Math.Max(0, needH - spaceBelow);
        double growDown = Math.Min(deficitH, Math.Max(0, workBottom - (Top + curH)));
        double stillH = deficitH - growDown;
        double growUp = Math.Min(stillH, Math.Max(0, Top - workTop));

        if (growRight < 0.5 && growLeft < 0.5 && growDown < 0.5 && growUp < 0.5)
            return;

        double newLeft = Left - growLeft;
        double newTop = Top - growUp;
        double newW = curW + growLeft + growRight;
        double newH = curH + growUp + growDown;

        // Локальный центр кольца сдвигаем ровно на growLeft/growUp: на экране точка та же.
        _ringCx = screenRingX - newLeft;
        _ringCy = screenRingY - newTop;

        Left = newLeft;
        Top = newTop;
        Width = newW;
        Height = newH;
        RingCanvas.Width = newW;
        RingCanvas.Height = newH;
        _lastWinW = newW;
        _lastWinH = newH;

        // Кнопки только если локальные координаты центра изменились (рост влево/вверх).
        if (growLeft >= 0.5 || growUp >= 0.5)
            MoveRingToCenter();
    }

    private int _processGen;

    /// <summary>
    /// Список процессов не строим внутри наведения, если кэша нет:
    /// снимок идёт в фоне, панель появляется сразу из кэша или после него.
    /// </summary>
    private void ShowProcessFlyout(Button parent, MenuAction action)
    {
        int gen = ++_processGen;
        var fresh = ProcessKillService.PeekFresh(42, 2.5);
        if (fresh != null)
        {
            BuildProcessFlyout(parent, action, fresh);
            return;
        }

        var stale = ProcessKillService.PeekFresh(42, 20);
        if (stale != null)
            BuildProcessFlyout(parent, action, stale);
        else
            _flyoutParent = parent;

        Task.Run(() => ProcessKillService.Snapshot(42)).ContinueWith(t =>
        {
            if (t.IsFaulted)
                return;
            Dispatcher.BeginInvoke(() =>
            {
                if (gen != _processGen || !ReferenceEquals(_flyoutParent, parent))
                    return;
                ClearFlyout();
                BuildProcessFlyout(parent, action, t.Result);
            });
        });
    }

    /// <summary>Динамический список процессов: клик = завершить.</summary>
    private void BuildProcessFlyout(Button parent, MenuAction action, IReadOnlyList<ProcessEntry> entries)
    {
        _flyoutParent = parent;
        const double pad = 10;
        const double gap = 6;
        const double tileH = 40;
        const double tileW = 168;
        const int cols = 3;
        // Высота сетки: сколько реально влезает на этом мониторе (оставляем место подсказке).
        double maxGridH = Math.Clamp(RingCanvas.Height - 160, 140, 420);

        // +3: «Диспетчер», «Проводник», «Обновить»
        int n = entries.Count + 3;
        int rows = (int)Math.Ceiling(n / (double)cols);
        double panelW = cols * tileW + (cols - 1) * gap + pad * 2;

        var grid = new UniformGrid { Columns = cols, Rows = rows };

        var openMgr = CreateProcessActionTile(
            tileW - 2, tileH,
            "Диспетчер",
            "Открыть классический диспетчер задач Windows.",
            "\uE7F4",
            () =>
            {
                ClearFlyout();
                ActionDispatcher.Execute(new MenuAction
                {
                    Label = "Диспетчер задач",
                    Kind = ActionKind.Launch,
                    Value = "taskmgr.exe"
                });
                Dismissed?.Invoke(this, EventArgs.Empty);
            });
        openMgr.Margin = new Thickness(gap / 2);
        grid.Children.Add(openMgr);
        _flyout.Add(openMgr);

        var restartExplorer = CreateProcessActionTile(
            tileW - 2, tileH,
            "Проводник",
            "Перезапустить Explorer. Если убил случайно: запустит снова.",
            "\uE8B7",
            () =>
            {
                HintTitle.Text = "Проводник";
                HintBody.Text = "Перезапускаю…";
                HintCard.Visibility = Visibility.Visible;
                HintCard.Opacity = 1;
                PlaceHintCard();
                Task.Run(ProcessKillService.TryRestartExplorer).ContinueWith(t =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        string err = t.Status == TaskStatus.RanToCompletion ? t.Result : "Не удалось перезапустить проводник.";
                        if (err.Length > 0)
                        {
                            HintBody.Text = err;
                            PlaceHintCard();
                            return;
                        }

                        ClearFlyout();
                        ShowProcessFlyout(parent, action);
                        HintTitle.Text = "Проводник";
                        HintBody.Text = "Explorer перезапущен.";
                        HintCard.Visibility = Visibility.Visible;
                        HintCard.Opacity = 1;
                        PlaceHintCard();
                    });
                });
            });
        restartExplorer.Margin = new Thickness(gap / 2);
        grid.Children.Add(restartExplorer);
        _flyout.Add(restartExplorer);

        var refresh = CreateProcessActionTile(
            tileW - 2, tileH,
            "Обновить",
            "Перечитать список процессов.",
            "\uE72C",
            () =>
            {
                ClearFlyout();
                ShowProcessFlyout(parent, action);
            });
        refresh.Margin = new Thickness(gap / 2);
        grid.Children.Add(refresh);
        _flyout.Add(refresh);

        foreach (var entry in entries)
        {
            var tile = CreateProcessKillTile(entry, tileW - 2, tileH);
            tile.Margin = new Thickness(gap / 2);
            grid.Children.Add(tile);
            _flyout.Add(tile);
        }

        double gridH = rows * tileH + Math.Max(0, rows - 1) * gap;
        UIElement gridHost = grid;
        if (gridH > maxGridH)
            gridHost = MakeHiddenScroll(grid, maxGridH);

        var root = new StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical };
        root.Children.Add(new TextBlock
        {
            Text = "Процессы: клик завершает (проверяем выход)",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Solid(Color.FromArgb(0x99, 0xF5, 0xF0, 0xE8)),
            Margin = new Thickness(4, 0, 4, 8),
            IsHitTestVisible = false
        });
        root.Children.Add(gridHost);

        _flyoutPanel = new Border
        {
            Width = panelW,
            Padding = new Thickness(pad),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            BorderBrush = Solid(Color.FromArgb(0x40, 0xF5, 0xF0, 0xE8)),
            Background = Solid(Color.FromArgb(0xF0, 0x16, 0x16, 0x18)),
            Child = root,
            SnapsToDevicePixels = true,
            Opacity = 0,
            RenderTransformOrigin = new Point(0, 0.5),
            RenderTransform = new TranslateTransform(-12, 0)
        };

        _flyoutPanel.Measure(new System.Windows.Size(panelW, double.PositiveInfinity));
        double panelH = _flyoutPanel.DesiredSize.Height;
        AttachFlyout(parent, _flyoutPanel, panelW, panelH);
    }

    /// <summary>
    /// Ставит flyout рядом с кнопкой: справа → слева → снизу → сверху.
    /// Всегда целиком внутри холста.
    /// </summary>
    private void AttachFlyout(Button parent, Border panel, double panelW, double panelH, double reserveRight = 0)
    {
        const double gap = 14;
        const double margin = 8;
        double canvasW = RingCanvas.Width;
        double canvasH = RingCanvas.Height;
        double px = Canvas.GetLeft(parent);
        double py = Canvas.GetTop(parent);
        double pw = parent.Width;
        double ph = parent.Height;

        double spaceRight = canvasW - (px + pw + gap) - margin - reserveRight;
        double spaceLeft = px - gap - margin;
        double spaceBelow = canvasH - (py + ph + gap) - margin;
        double spaceAbove = py - gap - margin;

        // Если панель выше доступного места: подрежем через MaxHeight + скролл внутри уже есть у процессов.
        double fitH = panelH;
        double left;
        double top;

        if (spaceRight >= Math.Min(panelW, 120))
        {
            left = px + pw + gap;
            // Не залезать в зону, зарезервированную под превью справа.
            double peekCap = canvasW - margin - reserveRight - panelW;
            if (reserveRight > 0 && left > peekCap)
                left = Math.Max(margin, peekCap);
            fitH = Math.Min(panelH, canvasH - 2 * margin);
            top = py + ph / 2 - fitH / 2;
        }
        else if (spaceLeft >= Math.Min(panelW, 120))
        {
            left = px - gap - panelW;
            fitH = Math.Min(panelH, canvasH - 2 * margin);
            top = py + ph / 2 - fitH / 2;
        }
        else if (spaceBelow >= Math.Min(panelH, 100))
        {
            fitH = Math.Min(panelH, spaceBelow);
            left = px + pw / 2 - panelW / 2;
            top = py + ph + gap;
        }
        else
        {
            fitH = Math.Min(panelH, Math.Max(100, spaceAbove));
            left = px + pw / 2 - panelW / 2;
            top = py - gap - fitH;
        }

        if (fitH < panelH - 1)
        {
            panel.MaxHeight = fitH;
            // Папки/ярлыки без внутреннего скролла: обернём, чтобы ничего не обрезалось.
            if (panel.Child is FrameworkElement content && content is not ScrollViewer)
            {
                panel.Child = null;
                panel.Child = MakeHiddenScroll(content, fitH);
            }
        }

        double maxLeft = canvasW - panelW - margin - Math.Max(0, reserveRight);
        left = Math.Clamp(left, margin, Math.Max(margin, maxLeft));
        top = Math.Clamp(top, margin, Math.Max(margin, canvasH - fitH - margin));

        Canvas.SetLeft(panel, Math.Round(left));
        Canvas.SetTop(panel, Math.Round(top));
        Panel.SetZIndex(panel, 50);
        if (!RingCanvas.Children.Contains(panel))
            RingCanvas.Children.Add(panel);
        if (!_flyout.Contains(panel))
            _flyout.Add(panel);

        panel.MouseEnter -= FlyoutKeepAliveEnter;
        panel.MouseLeave -= FlyoutKeepAliveLeave;
        panel.MouseEnter += FlyoutKeepAliveEnter;
        panel.MouseLeave += FlyoutKeepAliveLeave;

        panel.BeginAnimation(OpacityProperty, null);
        panel.Opacity = 1;
        if (panel.RenderTransform is TranslateTransform tt)
        {
            tt.BeginAnimation(TranslateTransform.XProperty, null);
            tt.X = 0;
        }
    }

    private void FlyoutKeepAliveEnter(object sender, System.Windows.Input.MouseEventArgs e) => CancelHideFlyout();
    private void FlyoutKeepAliveLeave(object sender, System.Windows.Input.MouseEventArgs e) => ScheduleHideFlyout();

    private Button CreateProcessActionTile(double width, double height, string label, string hint, string glyph, Action onClick)
    {
        var content = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = IconFont,
            FontSize = 16,
            Foreground = Brushes.White,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            IsHitTestVisible = false
        });
        content.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Solid(Color.FromRgb(0xF5, 0xF0, 0xE8)),
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            IsHitTestVisible = false
        });

        var rest = Solid(Color.FromArgb(0x40, 0x3E, 0x5E, 0x4A));
        var hover = Solid(Color.FromArgb(0x70, 0x3E, 0x5E, 0x4A));
        var btn = new Button
        {
            Width = width,
            Height = height,
            Content = content,
            Background = rest,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Focusable = false,
            Padding = new Thickness(8, 4, 8, 4),
            Template = GetFlyoutRowTemplate()
        };
        _restFills[btn] = rest;
        _hoverFills[btn] = hover;
        btn.Click += (_, _) => onClick();
        btn.MouseEnter += (_, _) =>
        {
            btn.Background = hover;
            HintTitle.Text = label;
            HintBody.Text = hint;
            HintCard.Visibility = Visibility.Visible;
            HintCard.Opacity = 1;
            PlaceHintCard();
            CancelHideFlyout();
        };
        btn.MouseLeave += (_, _) =>
        {
            btn.Background = rest;
            ScheduleHideFlyout();
        };
        return btn;
    }

    private Button CreateProcessKillTile(ProcessEntry entry, double width, double height)
    {
        string label = ProcessKillService.FormatLabel(entry);
        var pids = entry.AllPids is { Count: > 0 } ? entry.AllPids : new[] { entry.Pid };
        var text = new TextBlock
        {
            Text = label,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Solid(Color.FromRgb(0xF5, 0xF0, 0xE8)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            IsHitTestVisible = false
        };

        var rest = Solid(Color.FromArgb(0x28, 0xF5, 0xF0, 0xE8));
        var hover = Solid(Color.FromArgb(0x66, 0xA0, 0x4A, 0x4A));
        var btn = new Button
        {
            Width = width,
            Height = height,
            Content = text,
            Background = rest,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Focusable = false,
            Padding = new Thickness(10, 4, 10, 4),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
            Template = GetFlyoutRowTemplate(),
            Tag = entry
        };
        _restFills[btn] = rest;
        _hoverFills[btn] = hover;

        btn.Click += (_, _) =>
        {
            // WaitForExit нельзя на UI: клик иначе держит колесо до 2 секунд.
            btn.IsEnabled = false;
            HintTitle.Text = entry.Name;
            HintBody.Text = "Завершаю…";
            HintCard.Visibility = Visibility.Visible;
            HintCard.Opacity = 1;
            PlaceHintCard();
            int[] ids = pids.ToArray();
            Task.Run(() => ProcessKillService.TryKillMany(ids)).ContinueWith(t =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    string err = t.Status == TaskStatus.RanToCompletion ? t.Result : "Не удалось завершить.";
                    if (err.Length > 0)
                    {
                        btn.IsEnabled = true;
                        HintBody.Text = err;
                        PlaceHintCard();
                        return;
                    }

                    if (btn.Parent is Panel panel)
                        panel.Children.Remove(btn);
                    _flyout.Remove(btn);
                    HintBody.Text = ids.Length > 1 ? $"Завершены все ({ids.Length})." : "Завершён.";
                    PlaceHintCard();
                });
            });
        };
        btn.MouseEnter += (_, _) =>
        {
            btn.Background = hover;
            CancelHideFlyout();
        };
        btn.MouseLeave += (_, _) =>
        {
            btn.Background = rest;
            ScheduleHideFlyout();
        };
        return btn;
    }

    private Button CreateFolderTile(MenuAction action, double width, double height, bool peekable = false)
    {
        const double iconBox = 36;
        var icon = CreateIconVisual(action, iconBox, center: false, isFlyout: true);
        if (icon is FrameworkElement fe)
        {
            fe.Width = iconBox;
            fe.Height = iconBox;
            fe.Margin = new Thickness(0, 0, 8, 0);
            fe.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        }

        var label = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(action.Label) ? Path.GetFileName(action.Value) : action.Label,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Solid(Color.FromRgb(0xF5, 0xF0, 0xE8)),
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            MaxHeight = 36,
            IsHitTestVisible = false
        };

        var row = new DockPanel { LastChildFill = true, Width = width - 12, Height = height - 8 };
        var iconHost = new Border { Width = iconBox + 4, Child = icon, VerticalAlignment = System.Windows.VerticalAlignment.Center };
        DockPanel.SetDock(iconHost, System.Windows.Controls.Dock.Left);
        row.Children.Add(iconHost);
        row.Children.Add(label);

        var rest = Solid(Color.FromArgb(0x28, 0xF5, 0xF0, 0xE8));
        var hover = Solid(Color.FromArgb(0x55, 0xF5, 0xF0, 0xE8));

        var btn = new Button
        {
            Width = width,
            Height = height,
            Padding = new Thickness(6, 4, 6, 4),
            Content = row,
            Background = rest,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Tag = action,
            Focusable = false,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            Template = GetFlyoutRowTemplate()
        };

        if (peekable)
            WireFolderPeekButton(btn, action, rest, hover);
        else
            WireFlyoutButton(btn, action, rest, hover);
        return btn;
    }

    private void WireFolderPeekButton(Button btn, MenuAction action, Brush rest, Brush hover)
    {
        _restFills[btn] = rest;
        _hoverFills[btn] = hover;

        btn.Click += (_, _) => ActionChosen?.Invoke(this, action);
        btn.MouseEnter += (_, _) =>
        {
            ApplyFill(btn, hover: true);
            CancelHideFlyout();
            if (!action.ValueIsDirectory)
            {
                if (!_peekSticky)
                    ClearPeek();
                return;
            }

            string full = Path.GetFullPath(action.Value);
            // Уже смотрим эту папку: ничего не трогаем (можно спокойно ехать в превью).
            if (_peekPanel != null
                && string.Equals(_peekPath, full, StringComparison.OrdinalIgnoreCase))
            {
                CancelPendingPeekSwitch();
                return;
            }

            // Превью уже открыто на другую папку: не перебивать при проезде к панели.
            // Сменить можно только если зависнуть на новой ~2.5с.
            if (_peekSticky && _peekPanel != null)
            {
                ScheduleFolderPeek(full, resetStack: true, delayMs: 2500);
                return;
            }

            ScheduleFolderPeek(full, resetStack: true, delayMs: 120);
        };
        btn.MouseLeave += (_, _) =>
        {
            ApplyFill(btn, hover: false);
            // Уезжаем к превью: не стартуем скрытие сразу, кластер сам решит.
            ScheduleHideFlyout();
        };
    }

    /// <summary>Пауза перед открытием/сменой превью. Пока едешь к панели: соседние папки не срывают контент.</summary>
    private void ScheduleFolderPeek(string folderPath, bool resetStack, int delayMs = 120)
    {
        CancelHideFlyout();
        _pendingPeekPath = folderPath;
        _pendingPeekReset = resetStack;
        _peekDebounce ??= new System.Windows.Threading.DispatcherTimer();
        _peekDebounce.Interval = TimeSpan.FromMilliseconds(Math.Max(40, delayMs));
        _peekDebounce.Stop();
        _peekDebounce.Tick -= OnPeekDebounceTick;
        _peekDebounce.Tick += OnPeekDebounceTick;
        _peekDebounce.Start();
    }

    private void CancelPendingPeekSwitch()
    {
        _peekDebounce?.Stop();
        _pendingPeekPath = null;
    }

    private void OnPeekDebounceTick(object? sender, EventArgs e)
    {
        _peekDebounce?.Stop();
        string? path = _pendingPeekPath;
        bool reset = _pendingPeekReset;
        _pendingPeekPath = null;
        if (path != null)
            ShowFolderPeek(path, reset);
    }

    /// <summary>Боковая панель: что лежит внутри папки (с эскизами картинок).</summary>
    private void ShowFolderPeek(string folderPath, bool resetStack)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            ClearPeek();
            return;
        }

        string full = Path.GetFullPath(folderPath);
        if (string.Equals(_peekPath, full, StringComparison.OrdinalIgnoreCase) && _peekPanel != null)
            return;

        // Каталог ещё не в кэше: не сканируем диск внутри наведения.
        if (!FolderPeekService.TryTakeCached(full, 30, out _))
        {
            int ticket = ++_peekSchedule;
            bool reset = resetStack;
            Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(full))
                        return false;
                    FolderPeekService.List(full, 30);
                    return true;
                }
                catch
                {
                    return false;
                }
            }).ContinueWith(t =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (ticket != _peekSchedule || t.Result != true)
                        return;
                    ShowFolderPeek(full, reset);
                });
            });
            return;
        }

        CancelHideFlyout();

        if (resetStack || _peekStack.Count == 0)
        {
            _peekStack.Clear();
            _peekStack.Add(full);
        }
        else if (!string.Equals(_peekStack[^1], full, StringComparison.OrdinalIgnoreCase))
        {
            string? fullParent = SafeParent(full);
            string top = _peekStack[^1];
            string? topParent = SafeParent(top);
            if (fullParent != null && string.Equals(fullParent, top, StringComparison.OrdinalIgnoreCase))
                _peekStack.Add(full); // внутрь текущей
            else if (fullParent != null && topParent != null
                     && string.Equals(fullParent, topParent, StringComparison.OrdinalIgnoreCase))
                _peekStack[^1] = full; // соседняя папка
            else
                _peekStack.Add(full);
        }

        // ClearPeek bump'ает gen (срывает старые загрузки). Новый gen берём ПОСЛЕ этого.
        ClearPeek(keepStack: true);
        _peekPath = full;
        _peekSticky = true;
        // Превью открыто: список папок сжимаем в узкую колонку, чтобы оба влезли у круга.
        if (_foldersGroupAction != null)
            SetFoldersCompactMode(true);
        int loadGen = _peekLoadGen;

        const double pad = 10;
        const double gap = 6;
        const double tileW = 96;
        const double tileH = 86;
        const int cols = 3;
        const int maxItems = 30;

        var entries = FolderPeekService.List(full, maxItems);
        var grid = new UniformGrid
        {
            Columns = cols,
            Rows = Math.Max(1, (int)Math.Ceiling(Math.Max(entries.Count, 1) / (double)cols))
        };

        if (entries.Count == 0)
        {
            grid.Children.Add(new TextBlock
            {
                Text = "Пусто",
                FontSize = 13,
                Foreground = Solid(Color.FromArgb(0x99, 0xF5, 0xF0, 0xE8)),
                Margin = new Thickness(8),
                IsHitTestVisible = false
            });
        }
        else
        {
            foreach (var entry in entries)
            {
                var tile = CreatePeekTile(entry, tileW - 2, tileH, loadGen);
                tile.Margin = new Thickness(gap / 2);
                grid.Children.Add(tile);
                _flyout.Add(tile);
            }
        }

        double panelW = cols * tileW + (cols - 1) * gap + pad * 2;
        double maxGridH = Math.Clamp(RingCanvas.Height - 160, 140, 420);
        double gridH = grid.Rows * tileH + Math.Max(0, grid.Rows - 1) * gap;
        UIElement gridHost = grid;
        if (gridH > maxGridH)
            gridHost = MakeHiddenScroll(grid, maxGridH);

        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(4, 0, 4, 8) };
        if (_peekStack.Count > 1)
        {
            var back = new Button
            {
                Content = "←",
                Width = 32,
                Height = 28,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Solid(Color.FromRgb(0xF5, 0xF0, 0xE8)),
                Background = Solid(Color.FromArgb(0x40, 0x3E, 0x5E, 0x4A)),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Focusable = false,
                Margin = new Thickness(0, 0, 8, 0),
                Template = GetFlyoutRowTemplate()
            };
            DockPanel.SetDock(back, System.Windows.Controls.Dock.Left);
            back.Click += (_, _) =>
            {
                if (_peekStack.Count <= 1) return;
                _peekStack.RemoveAt(_peekStack.Count - 1);
                ScheduleFolderPeek(_peekStack[^1], resetStack: false, delayMs: 40);
            };
            back.MouseEnter += (_, _) =>
            {
                CancelHideFlyout();
                CancelPendingPeekSwitch();
            };
            back.MouseLeave += (_, _) => ScheduleHideFlyout();
            header.Children.Add(back);
            _flyout.Add(back);
        }

        header.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Solid(Color.FromArgb(0x99, 0xF5, 0xF0, 0xE8)),
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false
        });

        var root = new StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical };
        root.Children.Add(header);
        root.Children.Add(gridHost);

        _peekPanel = new Border
        {
            Width = panelW,
            Padding = new Thickness(pad),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            BorderBrush = Solid(Color.FromArgb(0x40, 0xF5, 0xF0, 0xE8)),
            Background = Solid(Color.FromArgb(0xF0, 0x16, 0x16, 0x18)),
            Child = root,
            SnapsToDevicePixels = true,
            Opacity = 1
        };

        double maxPeekH = Math.Clamp(RingCanvas.Height - 100, 160, 520);
        _peekPanel.Measure(new System.Windows.Size(panelW, double.PositiveInfinity));
        if (_peekPanel.DesiredSize.Height > maxPeekH)
            _peekPanel.MaxHeight = maxPeekH;

        _peekPanel.MouseEnter += OnPeekPanelEnter;
        _peekPanel.MouseLeave += FlyoutKeepAliveLeave;
        RingCanvas.Children.Add(_peekPanel);
        _flyout.Add(_peekPanel);
        Panel.SetZIndex(_peekPanel, 55);
        LayoutFolderCluster();
    }

    private Button CreatePeekTile(FolderPeekEntry entry, double width, double height, int loadGen)
    {
        const double thumb = 48;
        var glyph = new TextBlock
        {
            Text = entry.IsDirectory ? "\uE8B7" : (entry.IsImage ? "\uEB9F" : "\uE7C3"),
            FontFamily = IconFont,
            FontSize = 26,
            Foreground = Brushes.White,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            IsHitTestVisible = false
        };

        var host = new Border
        {
            Width = thumb,
            Height = thumb,
            Child = glyph,
            CornerRadius = new CornerRadius(8),
            Background = Solid(Color.FromArgb(0x22, 0xF5, 0xF0, 0xE8)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4),
            ClipToBounds = true
        };

        var stack = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        stack.Children.Add(host);
        stack.Children.Add(new TextBlock
        {
            Text = entry.Name,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Solid(Color.FromRgb(0xF5, 0xF0, 0xE8)),
            TextAlignment = System.Windows.TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            MaxWidth = width - 8,
            IsHitTestVisible = false
        });

        var rest = Solid(Color.FromArgb(0x22, 0xF5, 0xF0, 0xE8));
        var hover = Solid(Color.FromArgb(0x55, 0xF5, 0xF0, 0xE8));
        var btn = new Button
        {
            Width = width,
            Height = height,
            Content = stack,
            Background = rest,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Focusable = false,
            Padding = new Thickness(4, 6, 4, 4),
            Template = GetFlyoutRowTemplate(),
            Tag = entry
        };
        _restFills[btn] = rest;
        _hoverFills[btn] = hover;

        btn.Click += (_, _) =>
        {
            ActionChosen?.Invoke(this, new MenuAction
            {
                Label = entry.Name,
                Kind = ActionKind.Launch,
                Value = entry.Path
            });
        };
        btn.MouseEnter += (_, _) =>
        {
            ApplyFill(btn, hover: true);
            CancelHideFlyout();
            // Внутрь каталога: только после 2.5с зависания, иначе проезд мыши проваливает случайно.
            if (entry.IsDirectory)
                ScheduleFolderPeek(entry.Path, resetStack: false, delayMs: 2500);
        };
        btn.MouseLeave += (_, _) =>
        {
            ApplyFill(btn, hover: false);
            ScheduleHideFlyout();
        };

        // Shell-иконка и декод картинки не на UI: иначе наведение на папку фризит колесо.
        int px = (int)thumb;
        _ = Task.Run(() =>
        {
            try { return FolderPeekService.LoadVisual(entry, px); }
            catch { return null; }
        }).ContinueWith(t =>
        {
            var src = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
            if (src == null)
                return;
            Dispatcher.BeginInvoke(() =>
            {
                if (loadGen != _peekLoadGen)
                    return;
                host.Child = new System.Windows.Controls.Image
                {
                    Source = src,
                    Width = thumb,
                    Height = thumb,
                    Stretch = entry.IsImage ? Stretch.UniformToFill : Stretch.Uniform,
                    SnapsToDevicePixels = true,
                    IsHitTestVisible = false
                };
            });
        });

        return btn;
    }

    /// <summary>Зашли в превью: отменяем смену папки, которую могли задеть по дороге.</summary>
    private void OnPeekPanelEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        CancelHideFlyout();
        CancelPendingPeekSwitch();
    }

    private void ClearPeek(bool keepStack = false)
    {
        _peekLoadGen++;
        if (!keepStack)
        {
            CancelPendingPeekSwitch();
            _peekSticky = false;
        }

        if (_peekPanel != null)
        {
            RingCanvas.Children.Remove(_peekPanel);
            _flyout.Remove(_peekPanel);
            _peekPanel = null;
        }
        _peekPath = null;
        if (!keepStack)
        {
            _peekStack.Clear();
            // Превью закрыли: вернуть широкий вид списка папок, если он ещё открыт.
            if (_foldersGroupAction != null && _flyoutPanel != null && _foldersCompact)
                SetFoldersCompactMode(false);
        }
    }

    private bool IsMouseInFlyoutCluster()
    {
        if (_flyoutParent?.IsMouseOver == true) return true;
        if (_flyoutPanel?.IsMouseOver == true) return true;
        if (_peekPanel?.IsMouseOver == true) return true;
        if (_flyout.Any(el => el.IsMouseOver)) return true;

        // Геометрический «мост» между панелями: щель не закрывает меню.
        if (_flyoutPanel != null && _peekPanel != null)
        {
            try
            {
                Point pos = Mouse.GetPosition(RingCanvas);
                var a = PanelRect(_flyoutPanel);
                var b = PanelRect(_peekPanel);
                var hull = Rect.Union(a, b);
                hull.Inflate(10, 10);
                if (hull.Contains(pos))
                    return true;
            }
            catch { /* ignore */ }
        }

        return false;
    }

    private static Rect PanelRect(FrameworkElement el)
    {
        double x = Canvas.GetLeft(el);
        double y = Canvas.GetTop(el);
        double w = el.ActualWidth > 1 ? el.ActualWidth : el.Width;
        double h = el.ActualHeight > 1 ? el.ActualHeight : Math.Max(el.DesiredSize.Height, el.Height);
        if (double.IsNaN(w) || w <= 0) w = 1;
        if (double.IsNaN(h) || h <= 0) h = 1;
        return new Rect(x, y, w, h);
    }

    private static string? SafeParent(string path)
    {
        try
        {
            var p = Directory.GetParent(path);
            return p == null ? null : Path.GetFullPath(p.FullName);
        }
        catch
        {
            return null;
        }
    }

    private Button CreateFlyoutRow(MenuAction action, double width, double height, double iconBox)
    {
        var icon = CreateIconVisual(action, iconBox, center: false, isFlyout: true);
        if (icon is FrameworkElement fe)
        {
            fe.Width = iconBox;
            fe.Height = iconBox;
            fe.Margin = new Thickness(4, 0, 10, 0);
            fe.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        }

        var label = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(action.Label) ? action.Value: action.Label,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Solid(Color.FromRgb(0xF5, 0xF0, 0xE8)),
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            IsHitTestVisible = false
        };

        var row = new DockPanel { LastChildFill = true, Width = width, Height = height };
        var iconHost = new Border
        {
            Width = iconBox + 8,
            Height = height,
            Child = icon,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        DockPanel.SetDock(iconHost, System.Windows.Controls.Dock.Left);
        row.Children.Add(iconHost);
        row.Children.Add(label);

        var rest = Solid(Color.FromArgb(0x00, 0, 0, 0));
        var hover = Solid(Color.FromArgb(0x55, 0xF5, 0xF0, 0xE8));

        var btn = new Button
        {
            Width = width,
            Height = height,
            Padding = new Thickness(2, 0, 8, 0),
            Content = row,
            Background = rest,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Tag = action,
            Focusable = false,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            Template = GetFlyoutRowTemplate()
        };

        WireFlyoutButton(btn, action, rest, hover);
        return btn;
    }

    private void WireFlyoutButton(Button btn, MenuAction action, Brush rest, Brush hover)
    {
        _restFills[btn] = rest;
        _hoverFills[btn] = hover;

        btn.Click += (_, _) => ActionChosen?.Invoke(this, action);
        btn.MouseEnter += (_, _) =>
        {
            ApplyFill(btn, hover: true);
            CancelHideFlyout();
        };
        btn.MouseLeave += (_, _) =>
        {
            ApplyFill(btn, hover: false);
            ScheduleHideFlyout();
        };
    }

    private void ApplyFill(Button btn, bool hover)
    {
        if (hover)
        {
            if (_hoverFills.TryGetValue(btn, out var h))
                btn.Background = h;
        }
        else if (_restFills.TryGetValue(btn, out var r))
            btn.Background = r;
    }

    private static ControlTemplate GetFlyoutRowTemplate()
    {
        const int key = -9001;
        if (TemplateCache.TryGetValue(key, out var cached))
            return cached;

        var template = new ControlTemplate(typeof(Button));
        var shell = new FrameworkElementFactory(typeof(Border));
        shell.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        shell.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        shell.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Stretch);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
        shell.AppendChild(presenter);
        template.VisualTree = shell;
        TemplateCache[key] = template;
        return template;
    }

    private void ScheduleHideFlyout()
    {
        _hideFlyoutTimer ??= new System.Windows.Threading.DispatcherTimer();
        _hideFlyoutTimer.Interval = TimeSpan.FromMilliseconds(_peekPanel != null ? 380 : 220);
        _hideFlyoutTimer.Stop();
        _hideFlyoutTimer.Tick -= HideFlyoutTick;
        _hideFlyoutTimer.Tick += HideFlyoutTick;
        _hideFlyoutTimer.Start();
    }

    private void CancelHideFlyout() => _hideFlyoutTimer?.Stop();

    private void HideFlyoutTick(object? sender, EventArgs e)
    {
        _hideFlyoutTimer?.Stop();
        if (IsMouseInFlyoutCluster())
            return;
        ClearFlyout();
    }

    private void ClearFlyout() => DetachFlyout(destroyDynamic: true);

    /// <summary>Снять выезд с холста. Собранные группы не уничтожаем: следующее наведение их только ставит.</summary>
    private void DetachFlyout(bool destroyDynamic)
    {
        CancelHideFlyout();
        CancelPendingPeekSwitch();
        _peekSticky = false;
        _foldersCompact = false;
        _foldersGroupAction = null;
        _clusterRect = Rect.Empty;
        ClearPeek();
        bool keep = _flyoutPanel != null && _keptFlyouts.ContainsValue(_flyoutPanel);
        if (_flyoutPanel != null)
            RingCanvas.Children.Remove(_flyoutPanel);
        if (destroyDynamic && !keep)
        {
            foreach (var el in _flyout)
            {
                if (el is Button b)
                {
                    _restFills.Remove(b);
                    _hoverFills.Remove(b);
                }
            }
        }
        _flyout.Clear();
        _flyoutPanel = null;
        _flyoutParent = null;
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindButton(e.OriginalSource as DependencyObject) != null)
            return;
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    private static Button? FindButton(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is Button button)
                return button;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    void ShowDeskFlyout(Button parent, MenuAction action)
    {
        _flyoutParent = parent;
        void Tip(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                HideHintImmediate();
                return;
            }

            HintTitle.Text = "Пульт";
            HintBody.Text = text;
            HintCard.BeginAnimation(OpacityProperty, null);
            HintCard.Opacity = 1;
            HintCard.Visibility = Visibility.Visible;
            PlaceHintCard();
        }

        var panel = DeskFlyout.Build(Tip, () => Dismissed?.Invoke(this, EventArgs.Empty));
        panel.MouseEnter += FlyoutKeepAliveEnter;
        panel.MouseLeave += FlyoutKeepAliveLeave;
        _flyoutPanel = panel;
        RingCanvas.Children.Add(panel);
        _flyout.Add(panel);
        panel.Measure(new System.Windows.Size(360, 560));
        AttachFlyout(parent, panel, 348, Math.Max(panel.DesiredSize.Height, 220));
    }

    void ArmHint(MenuAction action)
    {
        CancelHint();
        _hintAction = action;
        _hintTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer?.Stop();
            if (_hintAction != null)
                ShowHint(_hintAction);
        };
        _hintTimer.Start();
    }

    void CancelHint()
    {
        _hintTimer?.Stop();
        _hintTimer = null;
        _hintAction = null;
        HideHintImmediate();
    }

    private void ShowHint(MenuAction action)
    {
        HintTitle.Text = action.Label;
        string body = action.Kind == ActionKind.Group
            ? (action.IsProcessGroup
                ? (string.IsNullOrWhiteSpace(action.Description)
                    ? "Наведи: список процессов. Кликни: открыть диспетчер задач."
                   : action.Description)
               : action.Children.Count == 0
                    ? "Группа пустая. В настройках добавь пункты: они выедут сюда."
                   : (string.IsNullOrWhiteSpace(action.Description)
                        ? $"Выезжает {action.Children.Count} пунктов."
                       : action.Description))
           : string.IsNullOrWhiteSpace(action.Description) ? action.Value: action.Description;
        if (action.PathMissing)
            body = "Путь не найден: файл или папку перенесли. Нажми пункт: предложим указать новый путь.\n"
                   + (string.IsNullOrWhiteSpace(action.Value) ? "": action.Value);
        HintBody.Text = string.IsNullOrWhiteSpace(body) ? "Нет описания.": body;
        HintCard.BeginAnimation(OpacityProperty, null);
        HintCard.Opacity = 1;
        HintCard.Visibility = Visibility.Visible;
        PlaceHintCard();
        // После layout flyout ActualHeight точнее: пододвинем ещё раз.
        if (_flyoutPanel != null)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (HintCard.Visibility == Visibility.Visible)
                    PlaceHintCard();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>Колёсико крутит, полосы прокрутки не рисуем.</summary>
    private static ScrollViewer MakeHiddenScroll(UIElement content, double height) =>
        new()
        {
            Content = content,
            Height = height,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            PanningMode = PanningMode.VerticalOnly,
            Focusable = false,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent
        };

    /// <summary>Подсказка строго под/над кластером, никогда поверх сетки папок.</summary>
    private void PlaceHintCard()
    {
        const double margin = 8;
        const double gap = 12;
        HintCard.Measure(new System.Windows.Size(360, double.PositiveInfinity));
        double hw = Math.Clamp(HintCard.DesiredSize.Width, 220, 360);
        double hh = Math.Max(HintCard.DesiredSize.Height, 48);
        HintCard.Width = hw;

        double canvasW = RingCanvas.Width;
        double canvasH = RingCanvas.Height;
        double left;
        double top;

        Rect hull = _clusterRect;
        if ((hull.Width < 8 || hull.Height < 8) && (_flyoutPanel != null || _peekPanel != null))
        {
            hull = Rect.Empty;
            if (_flyoutPanel != null)
                hull = PanelRect(_flyoutPanel);
            if (_peekPanel != null)
                hull = hull.IsEmpty ? PanelRect(_peekPanel) : Rect.Union(hull, PanelRect(_peekPanel));
        }

        if (hull.Width >= 8 && hull.Height >= 8)
        {
            left = hull.Left + (hull.Width - hw) / 2;
            double below = hull.Bottom + gap;
            double above = hull.Top - gap - hh;
            // Никогда не ставим внутрь/поверх панели — только ниже или выше.
            if (below + hh <= canvasH - margin)
                top = below;
            else if (above >= margin)
                top = above;
            else
                top = Math.Max(margin, canvasH - hh - margin); // прижать к низу экрана, не наезжать на сетку
        }
        else
        {
            left = _ringCx - hw / 2;
            double belowRing = _ringCy + _discClearR + 12;
            double aboveRing = _ringCy - _discClearR - hh - 12;
            top = belowRing + hh <= canvasH - margin ? belowRing
                : aboveRing >= margin ? aboveRing
                : Math.Clamp(_ringCy + 120, margin, canvasH - hh - margin);
        }

        left = Math.Clamp(left, margin, Math.Max(margin, canvasW - hw - margin));
        top = Math.Clamp(top, margin, Math.Max(margin, canvasH - hh - margin));

        // Финальная защита: если всё же пересекаем кластер — сдвинуть под низ.
        var hintRect = new Rect(left, top, hw, hh);
        if (hull.Width >= 8 && hull.IntersectsWith(hintRect))
        {
            top = Math.Min(canvasH - hh - margin, hull.Bottom + gap);
            if (top + hh > canvasH - margin && hull.Top - gap - hh >= margin)
                top = hull.Top - gap - hh;
        }

        Canvas.SetLeft(HintCard, Math.Round(left));
        Canvas.SetTop(HintCard, Math.Round(top));
        Panel.SetZIndex(HintCard, 60);
    }

    private void HideHint()
    {
        HintCard.Visibility = Visibility.Collapsed;
        HintCard.Opacity = 0;
        HintCard.Width = double.NaN;
    }

    private void HideHintImmediate() => HideHint();

    private static Color ParseTint(string? tint)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(tint))
                return (Color)System.Windows.Media.ColorConverter.ConvertFromString(tint)!;
        }
        catch
        {
            // ignore bad color from config
        }
        return Color.FromRgb(0x3E, 0x4E, 0x62);
    }

    private static SolidColorBrush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color Lift(Color c, double factor)
    {
        byte L(byte v) => (byte)Math.Clamp(v * factor, 0, 255);
        return Color.FromArgb(c.A, L(c.R), L(c.G), L(c.B));
    }

    private static ControlTemplate GetRoundTemplate(double size)
    {
        int look = AppearanceStore.GetRingLook();
        int key = (look + 1) * 10000 + (int)Math.Round(size);
        if (TemplateCache.TryGetValue(key, out var cached))
            return cached;

        var template = new ControlTemplate(typeof(Button));
        var shell = new FrameworkElementFactory(typeof(Border));
        shell.SetValue(Border.CornerRadiusProperty, new CornerRadius(size / 2));
        shell.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        shell.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        shell.SetValue(Border.BorderBrushProperty, AppearanceStore.ActiveLook().SlotRim);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
        shell.AppendChild(presenter);
        template.VisualTree = shell;
        TemplateCache[key] = template;
        return template;
    }

    /// <summary>
    /// Прозрачная кнопка под PNG/глиф: без круглой оболочки.
    /// Важно: корень обязан иметь Background=Transparent, иначе WPF не ловит клики
    /// (null/отсутствие заливки = hit-test пропускает событие насквозь окна).
    /// </summary>
    private static ControlTemplate GetFlatTemplate(double size)
    {
        // Новый ключ: шаблон с hit-test заливкой (старый Grid без Background ломал клики).
        int key = -200000 - (int)Math.Round(size);
        if (TemplateCache.TryGetValue(key, out var cached))
            return cached;

        var template = new ControlTemplate(typeof(Button));
        var shell = new FrameworkElementFactory(typeof(Border));
        shell.SetValue(FrameworkElement.WidthProperty, size);
        shell.SetValue(FrameworkElement.HeightProperty, size);
        shell.SetValue(FrameworkElement.ClipToBoundsProperty, false);
        shell.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        shell.SetValue(Border.CornerRadiusProperty, new CornerRadius(0));
        shell.SetValue(Border.BorderThicknessProperty, new Thickness(0));
        // Transparent, не null: иначе клики проходят сквозь кнопку на рабочий стол.
        shell.SetValue(Border.BackgroundProperty, Brushes.Transparent);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
        presenter.SetValue(UIElement.IsHitTestVisibleProperty, false);
        shell.AppendChild(presenter);
        template.VisualTree = shell;
        TemplateCache[key] = template;
        return template;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Dismissed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ApplyToolWindowStyle()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        const int GwlExstyle = -20;
        const int WsExToolwindow = 0x00000080;
        const int WsExNoactivate = 0x08000000;

        int style = Native.GetWindowLong(hwnd, GwlExstyle);
        Native.SetWindowLong(hwnd, GwlExstyle, style | WsExToolwindow | WsExNoactivate);
    }

    private static class Native
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
