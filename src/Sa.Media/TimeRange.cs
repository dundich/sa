using System.Diagnostics;

namespace Sa.Media;


[DebuggerDisplay("[{From.TotalMilliseconds}, {To?.TotalMilliseconds}]")]
public sealed record TimeRange(TimeSpan From = default, TimeSpan? To = null)
{
    /// <summary>
    /// Возвращает true, если конечное время не задано (обрезка до конца файла)
    /// </summary>
    public bool HasEnd => To.HasValue;

    /// <summary>
    /// Длительность диапазона. Если To не задан — null
    /// </summary>
    public TimeSpan? Duration => To.HasValue ? To.Value - From : null;

    /// <summary>
    /// Начальное время в секундах (double)
    /// </summary>
    public double FromSeconds => From.TotalSeconds;

    /// <summary>
    /// Конечное время в секундах (double)
    /// </summary>
    public double ToSeconds => To?.TotalSeconds ?? double.PositiveInfinity;

    /// <summary>
    /// Проверяет, содержит ли диапазон указанное время
    /// </summary>
    public bool Contains(TimeSpan time)
    {
        return time >= From && (!HasEnd || time <= To!.Value);
    }

    /// <summary>
    /// Валидация диапазона
    /// </summary>
    public void Validate()
    {
        if (From < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(From), "Start time cannot be negative");

        if (To.HasValue && To.Value < From)
            throw new ArgumentOutOfRangeException(nameof(To), "End time cannot be before start time");
    }

    public static TimeRange RangeFromDuration(TimeSpan from, TimeSpan duration)
        => new(from, from + duration);

    public static TimeRange RangeFromTimes(TimeSpan from, TimeSpan end)
        => new(from, end);

    public static TimeRange RangeFromMilliseconds(long from, long end)
        => new(TimeSpan.FromMilliseconds(from), TimeSpan.FromMilliseconds(end));

    public static TimeRange RangeFromSeconds(double fromSeconds, double? toSeconds = null)
        => new(TimeSpan.FromSeconds(fromSeconds), toSeconds.HasValue ? TimeSpan.FromSeconds(toSeconds.Value) : null);
}

