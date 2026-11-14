using System.Runtime.InteropServices;
using Microsoft.Win32;
using Zwmenu.Models;
using Zwmenu.Overlay;

namespace Zwmenu.Services;

/// <summary>Значки рабочего стола: спрятать и вернуть тем же пунктом колеса.</summary>
static class DesktopIcons
{
    private const int WmCommand = 0x0111;
    private const int ToggleCommand = 0x7402;
    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    public static bool IconsHidden
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(AdvancedKey);
                return key?.GetValue("HideIcons") is int value && value != 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool TryToggle(out bool nowHidden, out string error)
    {
        nowHidden = IconsHidden;
        error = "";
        IntPtr view = ShellSelectionService.FindDesktopDefView();
        if (view == IntPtr.Zero)
        {
            error = "Не получилось дотянуться до рабочего стола";
            return false;
        }

        SendMessage(view, WmCommand, (IntPtr)ToggleCommand, IntPtr.Zero);
        nowHidden = !nowHidden;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AdvancedKey);
            key?.SetValue("HideIcons", nowHidden ? 1 : 0, RegistryValueKind.DWord);
        }
        catch
        {
            // Картинка уже переключилась. Подпись сектора догонит в следующий раз.
        }

        return true;
    }

    public static void ApplyCaption(MenuAction item, bool hidden)
    {
        if (hidden)
        {
            item.Label = "Показать иконки";
            item.Description = "Вернуть значки на рабочий стол.";
            item.Glyph = "\uE7B3";
            return;
        }

        item.Label = "Скрыть иконки";
        item.Description = "Убрать значки и оставить одни обои. Повторный клик вернёт.";
        item.Glyph = "\uE73E";
    }

    public static void Toast(string text)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
            return;
        dispatcher.BeginInvoke(() =>
        {
            var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).Bounds;
            ScreenshotToast.Show(text, screen, 1.6);
        });
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
