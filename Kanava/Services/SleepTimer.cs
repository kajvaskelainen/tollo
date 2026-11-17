using System.Windows.Threading;

namespace Kanava.Services;

/// <summary>Uniajastin: pysäyttää kaiken toiston valitun ajan kuluttua (MainWindow kuuntelee Expired-tapahtumaa).</summary>
public static class SleepTimer
{
    private static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private static DateTime? _endsAt;

    public static event Action? Expired;
    public static event Action? Changed;

    static SleepTimer()
    {
        Timer.Tick += (_, _) =>
        {
            if (_endsAt is { } t && DateTime.Now >= t)
            {
                Cancel();
                Expired?.Invoke();
            }
            else Changed?.Invoke();
        };
    }

    public static bool IsActive => _endsAt is not null;
    public static TimeSpan Remaining => _endsAt is { } t && t > DateTime.Now ? t - DateTime.Now : TimeSpan.Zero;

    public static void Start(int minutes)
    {
        _endsAt = DateTime.Now.AddMinutes(minutes);
        Timer.Start();
        Changed?.Invoke();
    }

    public static void Cancel()
    {
        _endsAt = null;
        Timer.Stop();
        Changed?.Invoke();
    }

    /// <summary>Esim. "23 min" tai "1 h 05 min".</summary>
    public static string RemainingText
    {
        get
        {
            var r = Remaining;
            var min = (int)Math.Ceiling(r.TotalMinutes);
            return min >= 60 ? $"{min / 60} h {min % 60:00} min" : $"{min} min";
        }
    }
}
