using System.Windows;
using System.Windows.Input;
using Zwmenu.Services;

namespace Zwmenu.Overlay;

/// <summary>Кто держит уже выбранный файл. Без мастерской и без второго выбора.</summary>
public sealed class FileHoldWindow : Window
{
    readonly System.Windows.Controls.ListBox _list;
    readonly string[] _paths;

    public FileHoldWindow(IReadOnlyList<string> paths)
    {
        _paths = paths.Where(File.Exists).Take(8).ToArray();
        Title = "Кто держит";
        Width = 440;
        Height = 220;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowActivated = true;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        TemplateTheme.StyleWindow(this);
        _list = TemplateTheme.DarkList();
        _list.Margin = new Thickness(12);
        _list.Items.Add("Смотрю...");
        Content = _list;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    public void ShowNearCursor()
    {
        var point = System.Windows.Forms.Cursor.Position;
        var area = System.Windows.Forms.Screen.FromPoint(point).WorkingArea;
        double left = point.X + 16;
        double top = point.Y + 16;
        if (left + Width > area.Right)
            left = area.Right - Width - 8;
        if (top + Height > area.Bottom)
            top = area.Bottom - Height - 8;
        if (left < area.Left)
            left = area.Left + 8;
        if (top < area.Top)
            top = area.Top + 8;
        Left = left;
        Top = top;
        Show();
    }

    public void Load()
    {
        string[] paths = _paths;
        Task.Run(() =>
        {
            var lines = new List<string>();
            if (paths.Length == 0)
            {
                lines.Add("Нужен файл, не папка.");
            }
            else
            {
                foreach (string path in paths)
                {
                    var (holders, error) = FileLock.Who(path);
                    string name = Path.GetFileName(path);
                    if (error != null)
                        lines.Add(name + ": " + error);
                    else if (holders.Count == 0)
                        lines.Add(name + ": никто не держит");
                    else
                    {
                        foreach (var holder in holders)
                            lines.Add(name + ": " + holder.Name + "   " + holder.Pid + "   " + holder.Kind);
                    }
                }
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (!IsLoaded)
                    return;
                _list.Items.Clear();
                foreach (string line in lines)
                    _list.Items.Add(line);
            });
        });
    }
}
