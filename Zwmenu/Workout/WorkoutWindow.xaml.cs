using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Zwmenu.Services;

namespace Zwmenu.Workout;

public partial class WorkoutWindow : Window
{
    private WorkoutDay? _day;
    private Exercise? _exercise;
    private int _setsDone;
    private int _targetSets = 1;
    private int _workSeconds = 30;
    private int _circuitRound = 1;
    private const int CircuitRounds = 4;
    private bool _advanceAfterRest;
    private bool _working;
    private bool _resting;
    private int _workLeft;
    private int _restLeft;

    private bool _videoMode;
    private IReadOnlyList<WorkoutVideo> _videos = Array.Empty<WorkoutVideo>();
    private int _videoIndex = -1;
    private bool _videoPlaying;
    private bool _videoMuted;
    private double _videoVolumeBeforeMute = 0.8;

    private WorkoutVoiceListener? _voice;
    private WorkoutMusicPlayer? _music;
    private WorkoutPhrasePlayer? _phrases;
    private WorkoutAudioSettings _audio = WorkoutAudioStore.Load();

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _volumeSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private bool _volumeSavePending;

    public WorkoutWindow()
    {
        InitializeComponent();
        AppChrome.ApplyWindowIcon(this);
        ProfileText.Text = WorkoutPlan.ProfileNote + " " + WorkoutPlan.ScheduleNote;
        _tick.Tick += OnTick;
        _volumeSaveTimer.Tick += (_, _) =>
        {
            _volumeSaveTimer.Stop();
            if (!_volumeSavePending) return;
            _volumeSavePending = false;
            try { WorkoutAudioStore.Save(_audio); } catch { /* ignore */ }
        };
        Loaded += OnLoaded;
        Closed += OnClosed;
        BuildDayTabs();
        SelectDay(0);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Maximized;
        _phrases = new WorkoutPhrasePlayer();
        _phrases.Started += () => _music?.Duck(true);
        _phrases.Ended += () => _music?.Duck(false);

        _music = new WorkoutMusicPlayer();
        _music.Configure(_audio);
        MusicStatus.Text = _music.Status;

        VoiceStatus.Text = "Голос: подключу через секунду…";
        DoneStatus.Text = "Скажи «начать» или жми кнопку. «Видео»: ролики на весь экран.";

        // голос не на UI-потоке при открытии: меньше лагов входа
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!IsLoaded) return;
            _voice = new WorkoutVoiceListener();
            _voice.StatusChanged += s => Dispatcher.BeginInvoke(() => VoiceStatus.Text = s);
            _voice.CommandRecognized += cmd => Dispatcher.BeginInvoke(() => HandleVoiceCommand(cmd));
            _voice.Start();
        }));
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _tick.Stop();
        _volumeSaveTimer.Stop();
        if (_volumeSavePending)
        {
            _volumeSavePending = false;
            try { WorkoutAudioStore.Save(_audio); } catch { /* ignore */ }
        }
        _voice?.Stop();
        _voice?.Dispose();
        _voice = null;
        _phrases?.Dispose();
        _phrases = null;
        _music?.Dispose();
        _music = null;
        StopVideoPlayer();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (_videoMode)
        {
            if (e.Key is Key.Left or Key.OemComma)
            {
                MoveVideo(-1);
                e.Handled = true;
            }
            else if (e.Key is Key.Right or Key.OemPeriod)
            {
                MoveVideo(1);
                e.Handled = true;
            }
            else if (e.Key == Key.Space)
            {
                ToggleVideoPlayPause();
                e.Handled = true;
            }
            else if (e.Key == Key.L)
            {
                ShowVideoList(!_videoListVisible);
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                NudgeVideoVolume(0.05);
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                NudgeVideoVolume(-0.05);
                e.Handled = true;
            }
            else if (e.Key == Key.M)
            {
                ToggleVideoMute();
                e.Handled = true;
            }
        }
    }

    private void OnAudioSettings(object sender, RoutedEventArgs e)
    {
        var w = new WorkoutAudioWindow { Owner = this };
        if (w.ShowDialog() == true)
        {
            _audio = WorkoutAudioStore.Load();
            bool wasPlaying = _music?.IsPlaying == true;
            _music?.Configure(_audio);
            if (wasPlaying)
                _music?.Start();
            MusicStatus.Text = _music?.Status ?? "";
            if (_exercise != null)
                _workSeconds = ResolveWorkSeconds(_exercise);
            DoneStatus.Text = "Аудио сохранено. Трек и фразы подхвачены.";
        }
    }

    private void HandleVoiceCommand(string cmd)
    {
        switch (cmd)
        {
            case "video_mode":
                SetVideoMode(true);
                break;
            case "plan_mode":
                SetVideoMode(false);
                break;
            case "video_next":
                SetVideoMode(true);
                MoveVideo(1);
                break;
            case "video_prev":
                SetVideoMode(true);
                MoveVideo(-1);
                break;
            case "pause":
                if (_videoMode) PauseVideo();
                break;
            case "resume":
                if (_videoMode) PlayVideo();
                break;
            case "start":
                if (_videoMode) PlayVideo();
                else StartWorkSet();
                break;
            case "done":
                if (!_videoMode) CompleteSet(fromVoice: true);
                break;
            case "next":
                if (_videoMode) MoveVideo(1);
                else MoveExercise(1);
                break;
            case "prev":
                if (_videoMode) MoveVideo(-1);
                else MoveExercise(-1);
                break;
            case "skip_rest":
                if (!_videoMode) SkipRest();
                break;
        }
    }

    private bool IsCircuitDay => _day?.Key == "D";

    private void BuildDayTabs()
    {
        DayTabs.Children.Clear();
        for (int i = 0; i < WorkoutPlan.Days.Count; i++)
        {
            int index = i;
            var day = WorkoutPlan.Days[i];
            var btn = new Button
            {
                Content = $"{day.Key}: {day.Title.Replace("День " + day.Key + ": ", "")}",
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(16, 10, 16, 10),
                Background = new SolidColorBrush(Color.FromArgb(0x33, 0xF5, 0xF0, 0xE8)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF0, 0xE8)),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Tag = index,
                ToolTip = day.When,
                FontSize = 14
            };
            btn.Click += (_, _) => SelectDay(index);
            DayTabs.Children.Add(btn);
        }
    }

    private void SelectDay(int index)
    {
        if (index < 0 || index >= WorkoutPlan.Days.Count)
            return;

        StopTimers(silent: true);
        _advanceAfterRest = false;
        _circuitRound = 1;
        _day = WorkoutPlan.Days[index];
        for (int i = 0; i < DayTabs.Children.Count; i++)
        {
            if (DayTabs.Children[i] is Button b)
            {
                b.Background = new SolidColorBrush(i == index
                    ? Color.FromArgb(0x88, 0x3E, 0x5E, 0x4A)
                    : Color.FromArgb(0x33, 0xF5, 0xF0, 0xE8));
            }
        }

        RebuildExerciseList();
        UpdateDayGoal();
        if (ExerciseList.Items.Count > 0)
            ExerciseList.SelectedIndex = 0;
    }

    private void UpdateDayGoal()
    {
        if (_day == null) return;
        string extra = IsCircuitDay ? $" Раунд {_circuitRound} из {CircuitRounds}." : "";
        DayGoalText.Text = $"{_day.When}. {_day.Goal}{extra}";
    }

    private void RebuildExerciseList()
    {
        if (_day == null) return;
        int keep = ExerciseList.SelectedIndex;
        ExerciseList.Items.Clear();
        foreach (var ex in _day.Exercises)
        {
            ExerciseList.Items.Add(new ListBoxItem
            {
                Content = FormatListItem(ex, selected: false),
                Tag = ex,
                FontSize = 14
            });
        }
        if (keep >= 0 && keep < ExerciseList.Items.Count)
            ExerciseList.SelectedIndex = keep;
    }

    private string FormatListItem(Exercise ex, bool selected)
    {
        int target = ParseTargetSets(ex.SetsReps);
        int work = ResolveWorkSeconds(ex);
        if (selected)
            return $"{ex.Name}\n{_setsDone} из {target} · работа ~{work} сек";
        return $"{ex.Name}\n{ex.SetsReps}";
    }

    private void RefreshSelectedListItem()
    {
        if (ExerciseList.SelectedItem is ListBoxItem item && item.Tag is Exercise ex)
            item.Content = FormatListItem(ex, selected: true);
    }

    private void OnExerciseSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ExerciseList.SelectedItem is not ListBoxItem item || item.Tag is not Exercise ex)
            return;

        StopTimers(silent: true);
        _advanceAfterRest = false;
        _exercise = ex;
        _setsDone = 0;
        _targetSets = ParseTargetSets(ex.SetsReps);
        _workSeconds = ResolveWorkSeconds(ex);
        DoneStatus.Text = $"Скажи «начать»: музыка + таймер {_workSeconds} сек. Нужно {_targetSets} подход(а).";
        ExTitle.Text = ex.Name;
        ExMeta.Text = $"{ex.Focus}. {ex.SetsReps}. Работа ~{_workSeconds} сек. Отдых {ex.Rest}";
        ExWhy.Text = ex.Why;
        RefreshSelectedListItem();

        FillPoseGallery(ex.Images);
        StepsPanel.Children.Clear();
        int n = 1;
        foreach (var step in ex.Steps)
        {
            StepsPanel.Children.Add(new TextBlock
            {
                Text = $"{n}. {step.Text}",
                FontSize = 15,
                LineHeight = 22,
                Margin = new Thickness(0, 0, 0, 10),
                Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF0, 0xE8)),
                TextWrapping = TextWrapping.Wrap
            });
            n++;
        }

        MistakesPanel.Children.Clear();
        foreach (var m in ex.Mistakes)
        {
            MistakesPanel.Children.Add(new TextBlock
            {
                Text = "• " + m,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 6),
                Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xA1, 0x9A)),
                TextWrapping = TextWrapping.Wrap
            });
        }
    }


    private void FillPoseGallery(IReadOnlyList<ExerciseImage> images)
    {
        PoseGallery.Children.Clear();
        if (images == null || images.Count == 0)
        {
            PoseGallery.Children.Add(Placeholder("Нет картинок"));
            return;
        }

        foreach (var img in images)
        {
            var card = new StackPanel { Margin = new Thickness(0, 0, 14, 0), Width = 280 };
            card.Children.Add(LoadExerciseImage(img.File));
            card.Children.Add(new TextBlock
            {
                Text = img.Caption,
                Margin = new Thickness(4, 8, 4, 0),
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xF5, 0xF0, 0xE8)),
                TextWrapping = TextWrapping.Wrap
            });
            PoseGallery.Children.Add(card);
        }
    }
    private static UIElement LoadExerciseImage(string fileName)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Workout", fileName);
            if (!File.Exists(path))
                return Placeholder("Нет картинки");

            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(path, UriKind.Absolute);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 420;
            bi.EndInit();
            bi.Freeze();

            return new Border
            {
                Width = 280,
                Height = 210,
                CornerRadius = new CornerRadius(14),
                ClipToBounds = true,
                Background = new SolidColorBrush(Color.FromArgb(0x22, 0xF5, 0xF0, 0xE8)),
                Child = new System.Windows.Controls.Image
                {
                    Source = bi,
                    Stretch = Stretch.UniformToFill,
                    SnapsToDevicePixels = true
                }
            };
        }
        catch
        {
            return Placeholder("Не удалось загрузить");
        }
    }

    private static UIElement Placeholder(string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        return new Border
        {
            Width = 280,
            Height = 200,
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xF5, 0xF0, 0xE8)),
            Child = tb
        };
    }

    private void OnStartSet(object sender, RoutedEventArgs e) => StartWorkSet();
    private void OnDoneSet(object sender, RoutedEventArgs e) => CompleteSet(fromVoice: false);

    private void StartWorkSet()
    {
        if (_exercise == null)
            return;
        if (_resting)
            SkipRest(silent: true);

        if (_music?.IsPlaying != true)
        {
            _music?.Configure(_audio);
            _music?.Start();
            MusicStatus.Text = _music?.Status ?? "";
        }

        _phrases?.PlayKey(_audio, "start");

        _working = true;
        _resting = false;
        _workLeft = _workSeconds;
        RestPanel.Visibility = Visibility.Collapsed;
        WorkPanel.Visibility = Visibility.Visible;
        WorkTimerText.Text = $"{_workLeft} сек";
        WorkHint.Text = "Работай. Скажи «выполнил», когда подход готов.";
        DoneStatus.Text = $"Подход {_setsDone + 1} из {_targetSets}: работа {_workSeconds} сек.";
        _tick.Stop();
        _tick.Start();
    }

    private void CompleteSet(bool fromVoice)
    {
        if (_exercise == null)
            return;

        // если не стартовали таймер, всё равно считаем подход
        _working = false;
        WorkPanel.Visibility = Visibility.Collapsed;

        _setsDone++;
        RefreshSelectedListItem();
        int restSec = ParseRestSeconds(_exercise.Rest);
        bool last = _setsDone >= _targetSets;
        _advanceAfterRest = last;

        string how = fromVoice ? " (голос)" : "";
        DoneStatus.Text = last
            ? $"Подход {_setsDone} из {_targetSets} готов{how}. Отдых {restSec} сек, потом следующее."
            : $"Подход {_setsDone} из {_targetSets}{how}. Отдых {restSec} сек.";

        _phrases?.PlayKey(_audio, "set_done");

        StartRest(restSec);
    }

    private void StartRest(int seconds)
    {
        _working = false;
        WorkPanel.Visibility = Visibility.Collapsed;
        if (seconds <= 0)
        {
            RestPanel.Visibility = Visibility.Collapsed;
            AfterRest();
            return;
        }

        _resting = true;
        _restLeft = seconds;
        RestPanel.Visibility = Visibility.Visible;
        UpdateRestUi();
        _tick.Stop();
        _tick.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_working)
        {
            _workLeft--;
            if (_workLeft <= 0)
            {
                _working = false;
                WorkPanel.Visibility = Visibility.Collapsed;
                _tick.Stop();
                DoneStatus.Text = "Время подхода вышло. Скажи «выполнил», если сделал, или «начать» ещё раз.";
                WorkHint.Text = "Время вышло.";
                _phrases?.PlayKey(_audio, "work_time_up");
                return;
            }
            WorkTimerText.Text = $"{_workLeft} сек";
            return;
        }

        if (_resting)
        {
            _restLeft--;
            if (_restLeft <= 0)
            {
                FinishRest();
                return;
            }
            UpdateRestUi();
        }
    }

    private void UpdateRestUi()
    {
        RestTimerText.Text = $"{_restLeft} сек";
        string exercise = string.IsNullOrWhiteSpace(_exercise?.Name) ? "упражнения" : _exercise.Name;
        string restText = string.IsNullOrWhiteSpace(_exercise?.Rest) ? _restLeft + " сек" : _exercise.Rest;
        RestHint.Text = _advanceAfterRest
            ? $"Отдых {restText} после «{exercise}». Дальше следующее."
            : $"Отдых {restText} после «{exercise}». «пропустить отдых», если готов.";
    }

    private void FinishRest()
    {
        _resting = false;
        RestPanel.Visibility = Visibility.Collapsed;
        _tick.Stop();
        AfterRest();
    }

    private void SkipRest(bool silent = false)
    {
        if (!_resting)
            return;
        _resting = false;
        RestPanel.Visibility = Visibility.Collapsed;
        _tick.Stop();
        if (!silent)
            AfterRest();
        else
            _advanceAfterRest = false;
    }

    private void StopTimers(bool silent)
    {
        _working = false;
        _resting = false;
        WorkPanel.Visibility = Visibility.Collapsed;
        RestPanel.Visibility = Visibility.Collapsed;
        _tick.Stop();
        if (silent)
            _advanceAfterRest = false;
    }

    private void AfterRest()
    {
        if (_advanceAfterRest)
        {
            _advanceAfterRest = false;
            AdvanceToNextExercise();
            return;
        }
        DoneStatus.Text = $"Отдых закончен. {_setsDone} из {_targetSets}. Скажи «начать» для следующего подхода.";
        _phrases?.PlayKey(_audio, "rest_over");
    }

    private void AdvanceToNextExercise()
    {
        if (_day == null || ExerciseList.Items.Count == 0)
            return;

        int idx = ExerciseList.SelectedIndex;
        if (idx < ExerciseList.Items.Count - 1)
        {
            ExerciseList.SelectedIndex = idx + 1;
            DoneStatus.Text = "Следующее упражнение. Скажи «начать».";
            _phrases?.PlayKey(_audio, "next");
            return;
        }

        if (IsCircuitDay && _circuitRound < CircuitRounds)
        {
            int finished = _circuitRound;
            _circuitRound++;
            UpdateDayGoal();
            ExerciseList.SelectedIndex = 0;
            DoneStatus.Text = $"Раунд {finished} готов. Раунд {_circuitRound} из {CircuitRounds}. Скажи «начать».";
            _phrases?.PlayKey(_audio, "next");
            return;
        }

        DoneStatus.Text = "День готов. Можно закрыть окно.";
        _phrases?.PlayKey(_audio, "day_done");
        _music?.Stop();
        MusicStatus.Text = _music?.Status ?? "";
    }

    private void OnSkipRestClick(object sender, RoutedEventArgs e) => SkipRest();

    private void MoveExercise(int delta)
    {
        if (ExerciseList.Items.Count == 0) return;
        int next = ExerciseList.SelectedIndex + delta;
        if (next < 0 || next >= ExerciseList.Items.Count)
        {
            DoneStatus.Text = delta > 0 ? "Это последнее упражнение дня." : "Это первое упражнение дня.";
            return;
        }
        ExerciseList.SelectedIndex = next;
    }

    private int ResolveWorkSeconds(Exercise ex)
    {
        string s = ex.SetsReps ?? "";
        // "40 сек в раунде" / "по 40 сек"
        var single = Regex.Match(s, @"(\d+)\s*сек", RegexOptions.IgnoreCase);
        var range = Regex.Match(s, @"(\d+)\s*до\s*(\d+)\s*сек", RegexOptions.IgnoreCase);
        if (range.Success &&
            int.TryParse(range.Groups[1].Value, out int a) &&
            int.TryParse(range.Groups[2].Value, out int b))
            return Math.Clamp((a + b) / 2, 10, 180);
        if (single.Success && int.TryParse(single.Groups[1].Value, out int one))
            return Math.Clamp(one, 10, 180);

        // повторы: 8-15 раз ≈ 20-40 сек → берём из настроек (по умолчанию 30)
        return Math.Clamp(_audio.DefaultWorkSeconds, 10, 180);
    }

    internal static int ParseTargetSets(string setsReps)
    {
        var m = Regex.Match(setsReps ?? "", @"(\d+)\s*подход", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int n))
            return Math.Clamp(n, 1, 20);
        if ((setsReps ?? "").Contains("раунде", StringComparison.OrdinalIgnoreCase))
            return 1;
        return 3;
    }

    private static int ParseRestSeconds(string rest)
    {
        var m = Regex.Match(rest ?? "", @"(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out int s) ? Math.Clamp(s, 5, 600) : 60;
    }


    private void OnModePlan(object sender, RoutedEventArgs e) => SetVideoMode(false);

    private bool _videoListVisible = true;

    private void OnToggleVideoList(object sender, RoutedEventArgs e) => ShowVideoList(!_videoListVisible);

    private void ShowVideoList(bool show)
    {
        _videoListVisible = show;
        if (VideoListCol != null)
            VideoListCol.Width = show ? new GridLength(200) : new GridLength(0);
    }
    private void OnModeVideo(object sender, RoutedEventArgs e) => SetVideoMode(true);

    private void SetVideoMode(bool video)
    {
        _videoMode = video;
        PlanHost.Visibility = video ? Visibility.Collapsed : Visibility.Visible;
        VideoHost.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
        DayTabs.Visibility = video ? Visibility.Collapsed : Visibility.Visible;
        DayGoalText.Visibility = video ? Visibility.Collapsed : Visibility.Visible;

        ModePlanBtn.Background = new SolidColorBrush(video
            ? Color.FromArgb(0x33, 0xF5, 0xF0, 0xE8)
            : Color.FromArgb(0x88, 0x3E, 0x5E, 0x4A));
        ModeVideoBtn.Background = new SolidColorBrush(video
            ? Color.FromArgb(0x88, 0x3E, 0x5E, 0x4A)
            : Color.FromArgb(0x33, 0xF5, 0xF0, 0xE8));

        if (video)
        {
            _music?.Stop();
            MusicStatus.Text = "Музыка: пауза (видео)";
            if (_videos.Count == 0)
                LoadVideos();
            ShowVideoList(true);
            InitVideoVolumeUi();
            if (_videoIndex < 0 && _videos.Count > 0)
                VideoList.SelectedIndex = 0;
            VoiceStatus.Text = "Видео на весь экран. «вперёд» / «назад» / «пауза».";
        }
        else
        {
            PauseVideo();
            MusicStatus.Text = _music?.Status ?? "Музыка: …";
            VoiceStatus.Text = "Голос (план): «начать» / «выполнил» / «видео».";
        }
    }

    private void LoadVideos()
    {
        VideoFolderHint.Text = "Смотрю папку с роликами…";
        Task.Run(WorkoutVideoLibrary.Load).ContinueWith(t =>
        {
            var list = t.Status == TaskStatus.RanToCompletion
                ? t.Result
                : Array.Empty<WorkoutVideo>();
            Dispatcher.BeginInvoke(() => ApplyVideos(list));
        });
    }

    private void ApplyVideos(IReadOnlyList<WorkoutVideo> videos)
    {
        _videos = videos;
        VideoList.Items.Clear();
        foreach (var v in _videos)
        {
            VideoList.Items.Add(new ListBoxItem
            {
                Content = v.Title,
                Tag = v,
                FontSize = 14
            });
        }
        VideoFolderHint.Text = _videos.Count == 0
            ? "Нет mp4. Положи файлы в Assets\\Workout\\Videos"
            : $"Найдено роликов: {_videos.Count}. ← → листать.";
        if (_videoMode && _videoIndex < 0 && _videos.Count > 0)
            VideoList.SelectedIndex = 0;
    }

    private void OnVideoSelected(object sender, SelectionChangedEventArgs e)
    {
        if (VideoList.SelectedItem is not ListBoxItem item || item.Tag is not WorkoutVideo v)
            return;
        _videoIndex = v.Index;
        OpenVideo(v);
    }

    private void OpenVideo(WorkoutVideo v)
    {
        try
        {
            VideoTitle.Text = $"{v.Title}  ({_videoIndex + 1} из {_videos.Count})";
            VideoPlayer.Stop();
            VideoPlayer.Source = new Uri(v.Path, UriKind.Absolute);
            VideoPlayer.Position = TimeSpan.Zero;
            PlayVideo();
        }
        catch (Exception ex)
        {
            VideoTitle.Text = "Не удалось открыть: " + ex.Message;
        }
    }

    private void MoveVideo(int delta)
    {
        if (_videos.Count == 0)
        {
            LoadVideos();
            if (_videos.Count == 0) return;
        }
        int next = _videoIndex < 0 ? 0 : _videoIndex + delta;
        if (next < 0) next = _videos.Count - 1;
        if (next >= _videos.Count) next = 0;
        VideoList.SelectedIndex = next;
    }

    private void OnVideoPrev(object sender, RoutedEventArgs e) => MoveVideo(-1);
    private void OnVideoNext(object sender, RoutedEventArgs e) => MoveVideo(1);

    private void OnVideoPlayPause(object sender, RoutedEventArgs e) => ToggleVideoPlayPause();

    private void OnVideoRestart(object sender, RoutedEventArgs e)
    {
        if (_videoIndex < 0 || _videoIndex >= _videos.Count) return;
        VideoPlayer.Position = TimeSpan.Zero;
        PlayVideo();
    }

    private void OnVideoEnded(object sender, RoutedEventArgs e)
    {
        _videoPlaying = false;
        VideoPlayBtn.Content = "Смотреть";
        // авто следующее
        MoveVideo(1);
    }

    private void ToggleVideoPlayPause()
    {
        if (_videoPlaying) PauseVideo();
        else PlayVideo();
    }

    private void PlayVideo()
    {
        if (VideoPlayer.Source == null && _videos.Count > 0)
        {
            if (_videoIndex < 0) VideoList.SelectedIndex = 0;
            else OpenVideo(_videos[_videoIndex]);
            return;
        }
        try
        {
            VideoPlayer.Play();
            _videoPlaying = true;
            VideoPlayBtn.Content = "Пауза";
        }
        catch { /* ignore */ }
    }

    private void PauseVideo()
    {
        try { VideoPlayer.Pause(); } catch { /* ignore */ }
        _videoPlaying = false;
        VideoPlayBtn.Content = "Смотреть";
    }


    private void InitVideoVolumeUi()
    {
        double vol = Math.Clamp(_audio.VideoVolume, 0, 1);
        if (vol <= 0) vol = 0.8;
        _videoMuted = false;
        VideoVolumeSlider.Value = vol;
        ApplyVideoVolume(vol, save: false);
    }

    private void OnVideoVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        if (_videoMuted && e.NewValue > 0.001)
            _videoMuted = false;
        ApplyVideoVolume(e.NewValue, save: true);
    }

    private void OnVideoMute(object sender, RoutedEventArgs e) => ToggleVideoMute();

    private void ToggleVideoMute()
    {
        if (_videoMuted)
        {
            _videoMuted = false;
            double restore = _videoVolumeBeforeMute <= 0.01 ? 0.5 : _videoVolumeBeforeMute;
            VideoVolumeSlider.Value = restore;
            ApplyVideoVolume(restore, save: true);
        }
        else
        {
            _videoVolumeBeforeMute = VideoVolumeSlider.Value;
            _videoMuted = true;
            ApplyVideoVolume(0, save: false);
        }
    }

    private void NudgeVideoVolume(double delta)
    {
        _videoMuted = false;
        VideoVolumeSlider.Value = Math.Clamp(VideoVolumeSlider.Value + delta, 0, 1);
    }

    private void ApplyVideoVolume(double vol, bool save)
    {
        vol = Math.Clamp(vol, 0, 1);
        try { VideoPlayer.Volume = _videoMuted ? 0 : vol; } catch { /* ignore */ }
        VideoVolumeLabel.Text = $"{(int)Math.Round((_videoMuted ? 0 : vol) * 100)}%";
        VideoMuteBtn.Content = (_videoMuted || vol <= 0.001) ? "Без звука" : "Звук";
        if (save && !_videoMuted)
        {
            _audio.VideoVolume = (float)vol;
            // Не пишем JSON на каждый тик слайдера: debounce.
            _volumeSavePending = true;
            _volumeSaveTimer.Stop();
            _volumeSaveTimer.Start();
        }
    }
    private void StopVideoPlayer()
    {
        try
        {
            VideoPlayer.Stop();
            VideoPlayer.Source = null;
        }
        catch { /* ignore */ }
        _videoPlaying = false;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}