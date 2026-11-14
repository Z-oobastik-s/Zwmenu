using System.Windows;
using System.Windows.Media.Imaging;

namespace Zwmenu.Services;

/// <summary>Общая «облицовка» окон: иконка exe/окна из Assets.</summary>
public static class AppChrome
{
    public static void ApplyWindowIcon(Window window)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "zwmenu.ico");
            if (!File.Exists(path))
                return;

            var frame = BitmapFrame.Create(
                new Uri(path, UriKind.Absolute),
                BitmapCreateOptions.None,
                BitmapCacheOption.OnLoad);
            frame.Freeze();
            window.Icon = frame;
        }
        catch
        {
            // без иконки окно всё равно откроется
        }
    }
}
