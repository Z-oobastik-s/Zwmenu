using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using WpfControls = System.Windows.Controls;

namespace Zwmenu.Workout;

public partial class WorkoutAudioWindow : Window
{
    private WorkoutAudioSettings _settings;
    private readonly Dictionary<string, WpfControls.TextBox> _phraseBoxes = new(StringComparer.OrdinalIgnoreCase);
    private WorkoutPhrasePlayer? _testPlayer;

    public WorkoutAudioWindow()
    {
        InitializeComponent();
        _settings = WorkoutAudioStore.Load();
        MusicPathBox.Text = _settings.MusicPath ?? "";
        VolSlider.Value = Math.Clamp(_settings.MusicVolume, 0.05, 1);
        VolLabel.Text = ((int)(VolSlider.Value * 100)).ToString();
        WorkSecBox.Text = Math.Clamp(_settings.DefaultWorkSeconds, 10, 180).ToString();
        ReminderEnabledBox.IsChecked = _settings.ReminderEnabled;
        ReminderHourBox.Text = Math.Clamp(_settings.ReminderHour, 0, 23).ToString("00");
        ReminderMinuteBox.Text = Math.Clamp(_settings.ReminderMinute, 0, 59).ToString("00");
        ReminderJitterBox.Text = Math.Clamp(_settings.ReminderJitterMinutes, 0, 120).ToString();
        BuildPhraseRows();
    }

    private void BuildPhraseRows()
    {
        PhrasesHost.Children.Clear();
        _phraseBoxes.Clear();
        foreach (var (key, title, hint) in WorkoutAudioSettings.PhraseSlots)
        {
            var box = new WpfControls.TextBox
            {
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(10, 8, 10, 8),
                Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1E)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF0, 0xE8)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xF5, 0xF0, 0xE8)),
                Text = _settings.Phrases.TryGetValue(key, out var p) ? p : ""
            };
            _phraseBoxes[key] = box;

            var browse = new WpfControls.Button
            {
                Content = "Обзор…",
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 6, 0),
                Background = new SolidColorBrush(Color.FromArgb(0x33, 0xF5, 0xF0, 0xE8)),
                Foreground = box.Foreground,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = key
            };
            browse.Click += OnBrowsePhrase;

            var test = new WpfControls.Button
            {
                Content = "▶",
                Padding = new Thickness(10, 8, 10, 8),
                Background = browse.Background,
                Foreground = box.Foreground,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = key,
                ToolTip = "Проверить"
            };
            test.Click += OnTestPhrase;

            var right = new WpfControls.StackPanel { Orientation = WpfControls.Orientation.Horizontal };
            WpfControls.DockPanel.SetDock(right, WpfControls.Dock.Right);
            right.Children.Add(browse);
            right.Children.Add(test);

            var labels = new WpfControls.StackPanel();
            labels.Children.Add(new WpfControls.TextBlock
            {
                Text = title + "  (" + key + ")",
                FontWeight = FontWeights.SemiBold,
                Foreground = box.Foreground,
                Margin = new Thickness(0, 0, 0, 4)
            });
            labels.Children.Add(new WpfControls.TextBlock
            {
                Text = hint,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xF5, 0xF0, 0xE8)),
                Margin = new Thickness(0, 0, 0, 4)
            });
            labels.Children.Add(box);

            var row = new WpfControls.DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            row.Children.Add(right);
            row.Children.Add(labels);
            PhrasesHost.Children.Add(row);
        }
    }

    private void OnBrowseMusic(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Audio|*.mp3;*.wav;*.m4a;*.wma|All|*.*",
            Title = "Workout track"
        };
        if (dlg.ShowDialog(this) == true)
            MusicPathBox.Text = dlg.FileName;
    }

    private void OnBrowsePhrase(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfControls.Button b || b.Tag is not string key)
            return;
        Directory.CreateDirectory(WorkoutAudioStore.PhrasesFolder);
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Audio|*.mp3;*.wav;*.m4a;*.wma|All|*.*",
            Title = "Phrase: " + key,
            InitialDirectory = WorkoutAudioStore.PhrasesFolder
        };
        if (dlg.ShowDialog(this) == true && _phraseBoxes.TryGetValue(key, out var box))
            box.Text = dlg.FileName;
    }

    private void OnTestPhrase(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfControls.Button b || b.Tag is not string key)
            return;
        string? path = _phraseBoxes.TryGetValue(key, out var box) && !string.IsNullOrWhiteSpace(box.Text)
            ? box.Text.Trim()
            : WorkoutAudioStore.ResolvePhrase(Collect(), key);
        _testPlayer?.Dispose();
        _testPlayer = new WorkoutPhrasePlayer();
        _testPlayer.Play(path);
    }

    private void OnVolChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolLabel != null)
            VolLabel.Text = ((int)(e.NewValue * 100)).ToString();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(WorkoutAudioStore.PhrasesFolder);
        Process.Start(new ProcessStartInfo
        {
            FileName = WorkoutAudioStore.PhrasesFolder,
            UseShellExecute = true
        });
    }

    private WorkoutAudioSettings Collect()
    {
        var s = new WorkoutAudioSettings
        {
            MusicPath = string.IsNullOrWhiteSpace(MusicPathBox.Text) ? null : MusicPathBox.Text.Trim(),
            MusicVolume = (float)VolSlider.Value,
            VideoVolume = _settings.VideoVolume,
            DefaultWorkSeconds = int.TryParse(WorkSecBox.Text, out int sec) ? Math.Clamp(sec, 10, 180) : 30,
            ReminderEnabled = ReminderEnabledBox.IsChecked == true,
            ReminderHour = int.TryParse(ReminderHourBox.Text, out int h) ? Math.Clamp(h, 0, 23) : 20,
            ReminderMinute = int.TryParse(ReminderMinuteBox.Text, out int m) ? Math.Clamp(m, 0, 59) : 0,
            ReminderJitterMinutes = int.TryParse(ReminderJitterBox.Text, out int j) ? Math.Clamp(j, 0, 120) : 30
        };
        foreach (var (key, box) in _phraseBoxes)
        {
            if (!string.IsNullOrWhiteSpace(box.Text))
                s.Phrases[key] = box.Text.Trim();
        }
        return s;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _settings = Collect();
        WorkoutAudioStore.Save(_settings);
        WorkoutReminderService.Instance?.ReloadSchedule();
        _testPlayer?.Dispose();
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _testPlayer?.Dispose();
        DialogResult = false;
        Close();
    }
}