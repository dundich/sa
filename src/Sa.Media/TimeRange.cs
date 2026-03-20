using System.Diagnostics;

namespace Sa.Media;


[DebuggerDisplay("[{From.TotalMilliseconds}, {To.TotalMilliseconds}]")]
public sealed record TimeRange(TimeSpan From, TimeSpan To)
{
    public bool HasEnd => To != TimeSpan.MaxValue;

    public TimeSpan Duration => To - From;

    public bool IsPositive => To >= From;

    public double FromSeconds => From.TotalSeconds;

    public double ToSeconds => To.TotalSeconds;


    public static TimeRange[][] ToChunks(params TimeRange[][] chunks) => chunks;


    public static readonly TimeRange Default
        = TimeRange.Create(default, TimeSpan.MaxValue);

    /// <summary>
    /// Валидация диапазона
    /// </summary>
    public void Validate()
    {
        if (From < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(From), "Start time cannot be negative");

        if (To < From)
            throw new ArgumentOutOfRangeException(nameof(To), "End time cannot be before start time");
    }

    public static TimeRange RangeFromDuration(TimeSpan from, TimeSpan duration)
        => new(from, from + duration);

    public static TimeRange Create(TimeSpan from, TimeSpan end)
        => new(from, end);

    public static TimeRange Ms(long from, long end)
        => new(TimeSpan.FromMilliseconds(from),
            TimeSpan.MaxValue.TotalMilliseconds > end ? TimeSpan.FromMilliseconds(end) : TimeSpan.MaxValue);

    public static TimeRange Seconds(double fromSeconds, double? toSeconds = null)
        => new(TimeSpan.FromSeconds(fromSeconds),
            toSeconds.HasValue
            && TimeSpan.MaxValue.TotalSeconds < toSeconds
                ? TimeSpan.FromSeconds(toSeconds.Value)
                : TimeSpan.MaxValue);
}

