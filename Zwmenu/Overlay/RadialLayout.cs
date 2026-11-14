using System.Windows;
using System.Windows.Media;

namespace Zwmenu.Overlay;

/// <summary>Общая геометрия радиальных меню (углы, DIP, привязка к рабочей области).</summary>
public static class RadialLayout
{
    /// <summary>Угол с вершины (12 часов), равномерно по кругу.</summary>
    public static double AngleTop(int count, int index)
    {
        if (count <= 0) return 0;
        return -Math.PI / 2 + index * (2 * Math.PI / count);
    }

    public static Point DeviceToDip(Visual visual, double deviceX, double deviceY)
    {
        var src = PresentationSource.FromVisual(visual);
        if (src?.CompositionTarget == null)
            return new Point(deviceX, deviceY);
        var m = src.CompositionTarget.TransformFromDevice;
        return m.Transform(new Point(deviceX, deviceY));
    }

    /// <summary>
    /// Ставит окно size×size у (screenX,screenY) в device pixels,
    /// не вылезая за WorkingArea монитора под курсором.
    /// Окно уже должно быть Shown (нужен PresentationSource).
    /// </summary>
    public static void PlaceNearScreen(Window window, double sizeDip, int screenX, int screenY)
    {
        Point dip = DeviceToDip(window, screenX, screenY);
        double half = sizeDip / 2;
        double left = dip.X - half;
        double top = dip.Y - half;

        var work = System.Windows.Forms.Screen
            .FromPoint(new System.Drawing.Point(screenX, screenY)).WorkingArea;
        Point tl = DeviceToDip(window, work.Left, work.Top);
        Point br = DeviceToDip(window, work.Right, work.Bottom);
        left = Math.Clamp(left, tl.X + 4, Math.Max(tl.X + 4, br.X - sizeDip - 4));
        top = Math.Clamp(top, tl.Y + 4, Math.Max(tl.Y + 4, br.Y - sizeDip - 4));

        window.Left = left;
        window.Top = top;
    }

    /// <summary>
    /// Ставит окно в preferredLeft/Top (DIP), затем прижимает к WorkingArea
    /// монитора у screenHint (device px). Окно уже Shown.
    /// </summary>
    public static void PlaceInWorkArea(
        Window window,
        double preferredLeft,
        double preferredTop,
        double widthDip,
        double heightDip,
        System.Drawing.Point screenHint)
    {
        window.UpdateLayout();
        double w = widthDip > 1 ? widthDip : (window.ActualWidth > 1 ? window.ActualWidth : window.Width);
        double h = heightDip > 1 ? heightDip : (window.ActualHeight > 1 ? window.ActualHeight : 420);
        if (w < 1) w = 540;
        if (h < 1) h = 420;

        var work = System.Windows.Forms.Screen.FromPoint(screenHint).WorkingArea;
        Point tl = DeviceToDip(window, work.Left, work.Top);
        Point br = DeviceToDip(window, work.Right, work.Bottom);
        const double pad = 10;
        double maxLeft = Math.Max(tl.X + pad, br.X - w - pad);
        double maxTop = Math.Max(tl.Y + pad, br.Y - h - pad);
        window.Left = Math.Clamp(preferredLeft, tl.X + pad, maxLeft);
        window.Top = Math.Clamp(preferredTop, tl.Y + pad, maxTop);
    }

    /// <summary>Центр WorkingArea монитора у точки (device px), в DIP.</summary>
    public static Point WorkAreaCenterDip(Visual visual, System.Drawing.Point screenHint)
    {
        var work = System.Windows.Forms.Screen.FromPoint(screenHint).WorkingArea;
        return DeviceToDip(visual, work.Left + work.Width / 2.0, work.Top + work.Height / 2.0);
    }
}
