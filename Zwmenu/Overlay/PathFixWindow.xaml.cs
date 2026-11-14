using System.Windows;
using System.Windows.Media;
using Zwmenu.Models;
using Zwmenu.Services;

namespace Zwmenu.Overlay;

public partial class PathFixWindow: Window
{
    private readonly ActionProblem _problem;
    private readonly Func<MenuAction, string, bool> _applyPath;

    public PathFixWindow(ActionProblem problem, Func<MenuAction, string, bool> applyPath)
    {
        InitializeComponent();
        AppChrome.ApplyWindowIcon(this);
        _problem = problem;
        _applyPath = applyPath;

        Title = "Zwmenu: путь не найден";
        TitleText.Text = _problem.Title;
        ExplainText.Text = _problem.Explanation;
        PathText.Text = string.IsNullOrWhiteSpace(_problem.BrokenPath) ? "не указан": _problem.BrokenPath;

        BrowseFileBtn.IsEnabled = _problem.CanFixPath;
        BrowseFolderBtn.IsEnabled = _problem.CanFixPath;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnBrowseFile(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Новый путь для «{_problem.Action.Label}»",
            Filter = "Программы и файлы|*.exe;*.lnk;*.bat;*.cmd;*.*|Все файлы|*.*",
            CheckFileExists = true
        };

        string? dir = TryInitialDir(_problem.BrokenPath);
        if (dir != null)
            dlg.InitialDirectory = dir;

        if (dlg.ShowDialog(this) != true)
            return;

        Apply(dlg.FileName);
    }

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = $"Новая папка для «{_problem.Action.Label}»",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        string? dir = TryInitialDir(_problem.BrokenPath);
        if (dir != null)
            dlg.SelectedPath = dir;

        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return;

        Apply(dlg.SelectedPath);
    }

    private void Apply(string path)
    {
        StatusText.Visibility = Visibility.Collapsed;

        if (!_applyPath(_problem.Action, path))
        {
            StatusText.Text = "Не удалось сохранить. Попробуй ещё раз или поправь путь в настройках колеса.";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xA1, 0x9A));
            StatusText.Visibility = Visibility.Visible;
            return;
        }

        ShowSuccess(path);
    }

    private void ShowSuccess(string path)
    {
        Title = "Zwmenu: готово";
        TitleText.Text = $"«{_problem.Action.Label}» обновлено";
        ExplainText.Text = "Пункт на колесе теперь ведёт на новый путь. Можно сразу открывать средней кнопкой мыши.";
        PathLabel.Text = "Новый путь";
        PathText.Text = path;
        PathBox.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xA8, 0xC5, 0xA0));
        HintText.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
        FixButtons.Visibility = Visibility.Collapsed;
        DoneButtons.Visibility = Visibility.Visible;
        BrowseFileBtn.IsEnabled = false;
        BrowseFolderBtn.IsEnabled = false;
    }

    private static string? TryInitialDir(string? broken)
    {
        if (string.IsNullOrWhiteSpace(broken))
            return null;
        try
        {
            if (Directory.Exists(broken))
                return broken;
            string? parent = Path.GetDirectoryName(broken);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                return parent;
            var dir = new DirectoryInfo(Path.GetDirectoryName(broken) ?? broken);
            while (dir != null)
            {
                if (dir.Exists)
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }
}
