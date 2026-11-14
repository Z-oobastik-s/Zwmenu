using System.Windows;
using Zwmenu.Services;

namespace Zwmenu.Workout;

public partial class WorkoutReminderWindow : Window
{
    public event Action? OpenWorkoutRequested;
    public event Action? SnoozeRequested;

    public WorkoutReminderWindow()
    {
        InitializeComponent();
        AppChrome.ApplyWindowIcon(this);
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { /* ignore */ }
            }
        };
        ApplyCopy();
    }

    private void ApplyCopy()
    {
        var day = DateTime.Now.DayOfWeek;
        string plan = day switch
        {
            DayOfWeek.Monday => "Сегодня логичен день A: грудь и пресс.",
            DayOfWeek.Wednesday => "Сегодня логичен день B: ноги и ягодицы.",
            DayOfWeek.Thursday => "Сегодня логичен день C: кор и спина.",
            DayOfWeek.Saturday => "Сегодня можно круговую D: жиросжигание.",
            DayOfWeek.Sunday => "Воскресенье: полный отдых или лёгкая ходьба.",
            _ => "Между силовыми днями: ходьба 6 до 10 тысяч шагов."
        };

        string[] tips =
        [
            "20 до 40 минут хватит. Без фанатизма, но регулярно.",
            "Разминка 3 минуты, потом подходы. Вода рядом.",
            "Если нет сил на полный день: сделай хотя бы планку и приседания.",
            "Пресс рисует еда + объём работы. Сегодня просто не пропускай."
        ];

        Headline.Text = "Вечер. Пора подвигаться";
        BodyText.Text =
            "Короткое напоминание: тело не просит идеального зала, ему нужен регулярный импульс. " +
            plan + " Можно открыть план в Zwmenu и пройти пару упражнений.";
        TipText.Text = tips[DateTime.Now.DayOfYear % tips.Length];
    }

    private void OnOpenWorkout(object sender, RoutedEventArgs e)
    {
        OpenWorkoutRequested?.Invoke();
        Close();
    }

    private void OnSnooze(object sender, RoutedEventArgs e)
    {
        SnoozeRequested?.Invoke();
        Close();
    }

    private void OnDismiss(object sender, RoutedEventArgs e) => Close();
}