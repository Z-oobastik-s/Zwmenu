using System.Windows;
using System.Windows.Input;

namespace Zwmenu.Overlay;

public partial class ResizeCustomDialog : Window
{
    public int ResultWidth { get; private set; }
    public int ResultHeight { get; private set; }

    public ResizeCustomDialog(int suggestW = 512, int suggestH = 512)
    {
        InitializeComponent();
        WidthBox.Text = suggestW.ToString();
        HeightBox.Text = suggestH.ToString();
        Loaded += (_, _) =>
        {
            WidthBox.Focus();
            WidthBox.SelectAll();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                DialogResult = false;
            }
        };
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(WidthBox.Text.Trim(), out int w)
            || !int.TryParse(HeightBox.Text.Trim(), out int h)
            || w < 16 || h < 16 || w > 8192 || h > 8192)
        {
            System.Windows.MessageBox.Show(this, "Введи ширину и высоту от 16 до 8192.", "Zwmenu",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ResultWidth = w;
        ResultHeight = h;
        DialogResult = true;
    }
}
