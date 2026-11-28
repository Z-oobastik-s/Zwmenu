using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Zwmenu.Services;

/// <summary>
/// Ступенчатый старт после входа в Windows: сначала хуки и трей, тяжёлое позже.
/// На свежей загрузке ПК паузы длиннее, чтобы не драться с Explorer и антивирусом.
/// </summary>
public static class BootPace
{
    static readonly object Gate = new();
    static readonly List<DispatcherTimer> Timers = new();
    static int _stage;

    /// <summary>Секунд с загрузки ОС. Если API недоступен: считаем «свежий» старт.</summary>
    public static double SecondsSinceBoot
    {
        get
        {
            try
            {
                return Environment.TickCount64 / 1000.0;
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>Первые ~4 минуты после загрузки: бережный режим.</summary>
    public static bool SoftLogin => SecondsSinceBoot < 240;

    public static TimeSpan Pace(double normalSeconds, double softSeconds)
    {
        double sec = SoftLogin ? softSeconds : normalSeconds;
        if (sec < 0)
            sec = 0;
        return TimeSpan.FromSeconds(sec);
    }

    /// <summary>Фоновая работа с низким приоритетом потока: не мешает хукам и UI.</summary>
    public static Task RunLowAsync(Action work)
    {
        return Task.Factory.StartNew(() =>
        {
            var old = Thread.CurrentThread.Priority;
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.Lowest;
                work();
            }
            finally
            {
                try { Thread.CurrentThread.Priority = old; } catch { /* поток уже умирает */ }
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public static void After(Dispatcher dispatcher, TimeSpan delay, Action action)
    {
        if (dispatcher.HasShutdownStarted)
            return;
        if (delay <= TimeSpan.Zero)
        {
            dispatcher.BeginInvoke(DispatcherPriority.Background, action);
            return;
        }

        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = delay
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            lock (Gate)
                Timers.Remove(timer);
            if (dispatcher.HasShutdownStarted)
                return;
            try { action(); }
            catch { /* ступень старта не валит процесс */ }
        };
        lock (Gate)
            Timers.Add(timer);
        timer.Start();
    }

    /// <summary>Несколько ступеней подряд с накопленной задержкой от сейчас.</summary>
    public static void Schedule(Dispatcher dispatcher, params (double NormalSec, double SoftSec, Action Work)[] stages)
    {
        foreach (var stage in stages)
        {
            int id = Interlocked.Increment(ref _stage);
            After(dispatcher, Pace(stage.NormalSec, stage.SoftSec), () =>
            {
                Debug.WriteLine("Zwmenu boot stage " + id);
                stage.Work();
            });
        }
    }

    public static void CancelAll()
    {
        lock (Gate)
        {
            foreach (var timer in Timers)
            {
                try { timer.Stop(); } catch { /* уже */ }
            }

            Timers.Clear();
        }
    }

    [DllImport("kernel32.dll")]
    static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr min, IntPtr max);

    /// <summary>После прогрева просим ОС сбросить лишний рабочий набор. Не вызывает OOM.</summary>
    public static void TrimWorkingSet()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            SetProcessWorkingSetSize(p.Handle, (IntPtr)(-1), (IntPtr)(-1));
        }
        catch
        {
            // необязательно
        }
    }
}
