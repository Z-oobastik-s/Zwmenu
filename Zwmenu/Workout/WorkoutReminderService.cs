using System.Windows;
using System.Windows.Threading;

namespace Zwmenu.Workout;

/// <summary>Ежедневное напоминание около настроенного времени (± джиттер).</summary>
public sealed class WorkoutReminderService : IDisposable
{
    public static WorkoutReminderService? Instance { get; private set; }

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly Random _rng = new();
    private DateTime _nextFire = DateTime.MaxValue;
    private DateTime? _snoozeUntil;
    private WorkoutReminderWindow? _open;
    private bool _disposed;
    private bool _firedForSlot;
    private WorkoutAudioSettings _settings = new();

    public WorkoutReminderService()
    {
        Instance = this;
        _settings = WorkoutAudioStore.Load();
        ScheduleNext(fromNow: false);
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>Вызывать после сохранения аудио-настроек: без чтения диска каждые 30 сек.</summary>
    public void ReloadSchedule()
    {
        _settings = WorkoutAudioStore.Load();
        ScheduleNext(fromNow: false);
    }

    private void ScheduleNext(bool fromNow)
    {
        var s = _settings;
        if (!s.ReminderEnabled)
        {
            _nextFire = DateTime.MaxValue;
            _firedForSlot = true;
            return;
        }

        int hour = Math.Clamp(s.ReminderHour, 0, 23);
        int minute = Math.Clamp(s.ReminderMinute, 0, 59);
        int jitterMax = Math.Clamp(s.ReminderJitterMinutes, 0, 120);

        var now = DateTime.Now;
        int jitterMin = jitterMax == 0 ? 0 : _rng.Next(-jitterMax, jitterMax + 1);
        var candidate = now.Date.AddHours(hour).AddMinutes(minute + jitterMin);

        if (!fromNow && now < candidate)
            _nextFire = candidate;
        else
        {
            jitterMin = jitterMax == 0 ? 0 : _rng.Next(-jitterMax, jitterMax + 1);
            _nextFire = now.Date.AddDays(1).AddHours(hour).AddMinutes(minute + jitterMin);
        }

        _firedForSlot = false;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (!_settings.ReminderEnabled) return;

        var now = DateTime.Now;

        if (_snoozeUntil is DateTime snooze)
        {
            if (now < snooze) return;
            _snoozeUntil = null;
            ShowReminder();
            ScheduleNext(fromNow: true);
            return;
        }

        if (_firedForSlot) return;
        if (_nextFire == DateTime.MaxValue || now < _nextFire) return;

        _firedForSlot = true;
        ShowReminder();
        ScheduleNext(fromNow: true);
    }

    public void ShowReminder()
    {
        var app = Application.Current;
        if (app == null) return;

        void Show()
        {
            if (_open != null)
            {
                _open.Activate();
                return;
            }

            var w = new WorkoutReminderWindow();
            _open = w;
            w.OpenWorkoutRequested += () => Services.ActionDispatcher.OpenWorkoutWindow();
            w.SnoozeRequested += () => { _snoozeUntil = DateTime.Now.AddMinutes(45); };
            w.Closed += (_, _) =>
            {
                if (ReferenceEquals(_open, w))
                    _open = null;
            };
            w.Show();
            w.Activate();
            Services.UiSounds.Notify();
        }

        if (app.Dispatcher.CheckAccess()) Show();
        else app.Dispatcher.Invoke(Show);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ReferenceEquals(Instance, this))
            Instance = null;
        _timer.Stop();
        try { _open?.Close(); } catch { /* ignore */ }
    }
}
