using System.Windows;
using System.Windows.Media;
using Zwmenu.Services;

namespace Zwmenu;

public partial class MainWindow: Window
{
    private readonly LauncherService _launcher;
    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0xA8, 0xC5, 0xA0));
    private static readonly Brush BadBrush = new SolidColorBrush(Color.FromRgb(0xD4, 0xA1, 0x9A));

    public event EventHandler? SettingsRequested;

    public MainWindow(LauncherService launcher)
    {
        InitializeComponent();
        AppChrome.ApplyWindowIcon(this);
        OkBrush.Freeze();
        BadBrush.Freeze();

        _launcher = launcher;
        _launcher.StatusChanged += (_, _) => Dispatcher.Invoke(RefreshStatus);
        _launcher.ConfigChanged += (_, _) => Dispatcher.Invoke(RefreshStatus);
        _launcher.ActionFailed += (_, msg) => Dispatcher.Invoke(() =>
        {
            // Коротко в статусе; подробности и «исправить путь»: в отдельном окне.
            ErrorText.Text = TruncateStatus(msg);
            ErrorText.Visibility = Visibility.Visible;
        });
        _launcher.ActionProblem += (_, problem) => Dispatcher.Invoke(() =>
        {
            ErrorText.Text = $"{problem.Title}. Можно сразу указать новый путь в открывшемся окне.";
            ErrorText.Visibility = Visibility.Visible;
        });

        Loaded += (_, _) => RefreshStatus();
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);

    private static string TruncateStatus(string msg)
    {
        if (string.IsNullOrEmpty(msg))
            return msg;
        // Не тащим в статус англоязычный Win32 про working directory.
        int cut = msg.IndexOf("with working directory", StringComparison.OrdinalIgnoreCase);
        if (cut > 0)
            msg = msg[..cut].Trim().TrimEnd('.') + ".";
        if (msg.Length > 220)
            msg = msg[..217] + "…";
        return msg;
    }

    private void RefreshStatus()
    {
        if (_launcher.IsRunning)
        {
            StatusText.Text = $"Всё работает. СКМ открывает колесо: {_launcher.ItemCount} пунктов.";
            StatusText.Foreground = OkBrush;
            if (!string.IsNullOrEmpty(_launcher.ConfigWarning))
            {
                ErrorText.Text = _launcher.ConfigWarning;
                ErrorText.Visibility = Visibility.Visible;
            }
            else if (string.IsNullOrEmpty(ErrorText.Text) || ErrorText.Text == _launcher.ConfigWarning)
                ErrorText.Visibility = Visibility.Collapsed;
        }
        else
        {
            StatusText.Text = "Хуки не установлены.";
            StatusText.Foreground = BadBrush;
            ErrorText.Text = string.IsNullOrEmpty(_launcher.StatusError)
                ? "Антивирус или политика Windows мог заблокировать SetWindowsHookEx."
               : _launcher.StatusError;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
