using System.Diagnostics;

namespace Sa.Media;


/// <summary>
/// A time interval with an inclusive start and end.
/// </summary>
[DebuggerDisplay("[{From.TotalMilliseconds}, {To.TotalMilliseconds}]")]
public sealed record TimeRange
{
    /// <summary>The start of the interval.</summary>
    public TimeSpan From { get; init; }

    /// <summary>The end of the interval, or <see cref="TimeSpan.MaxValue"/> for an open-ended range.</summary>
    public TimeSpan To { get; init; }

    /// <summary>
    /// Gets a value indicating whether the range has a finite end.
    /// </summary>
    public bool HasEnd => To != TimeSpan.MaxValue;

    /// <summary>
    /// Gets the length of the range.
    /// </summary>
    public TimeSpan Duration => To - From;

    /// <summary>
    /// Gets a value indicating whether the end is not before the start.
    /// </summary>
    public bool IsPositive => To >= From;

    /// <summary>
    /// Gets the start of the range in seconds.
    /// </summary>
    public double FromSeconds => From.TotalSeconds;

    /// <summary>
    /// Gets the end of the range in seconds.
    /// </summary>
    public double ToSeconds => To.TotalSeconds;


    /// <summary>
    /// Packs the given ranges into a jagged array.
    /// </summary>
    /// <param name="chunks">The ranges to pack.</param>
    /// <returns>The same arrays, unchanged.</returns>
    public static TimeRange[][] ToChunks(params TimeRange[][] chunks) => chunks;


    /// <summary>
    /// A range covering everything from the start of time to its end.
    /// </summary>
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

    /// <summary>
    /// Creates a range starting at <paramref name="from"/> and lasting for <paramref name="duration"/>.
    /// </summary>
    /// <param name="from">The start of the range.</param>
    /// <param name="duration">The length of the range.</param>
    /// <returns>The resulting range.</returns>
    public static TimeRange RangeFromDuration(TimeSpan from, TimeSpan duration)
        => new() { From = from, To = from + duration };

    /// <summary>
    /// Creates a range with the given start and end.
    /// </summary>
    /// <param name="from">The start of the range.</param>
    /// <param name="end">The end of the range.</param>
    /// <returns>The resulting range.</returns>
    public static TimeRange Create(TimeSpan from, TimeSpan end)
        => new() { From = from, To = end };

    /// <summary>
    /// Creates a range from millisecond bounds.
    /// </summary>
    /// <param name="from">The start of the range in milliseconds.</param>
    /// <param name="end">The end of the range in milliseconds.</param>
    /// <returns>The resulting range.</returns>
    public static TimeRange Ms(long from, long end)
        => new()
        {
            From = TimeSpan.FromMilliseconds(from),
            To = TimeSpan.MaxValue.TotalMilliseconds > end ? TimeSpan.FromMilliseconds(end) : TimeSpan.MaxValue,
        };

    /// <summary>
    /// Creates a range from second bounds.
    /// </summary>
    /// <param name="fromSeconds">The start of the range in seconds.</param>
    /// <param name="toSeconds">The optional end of the range in seconds; when omitted the range is open-ended.</param>
    /// <returns>The resulting range.</returns>
    public static TimeRange Seconds(double fromSeconds, double? toSeconds = null)
    {
        TimeSpan from = TimeSpan.FromSeconds(fromSeconds);

        // Условие проверяет, что конечная граница попадает в диапазон TimeSpan
        // (а не наоборот — раньше условие было инвертировано, и любой конечный
        // toSeconds игнорировался: TimeRange.Seconds(5,15) отдавал [5s, ∞)).
        TimeSpan to = toSeconds.HasValue
            && TimeSpan.MaxValue.TotalSeconds > toSeconds.Value
                ? TimeSpan.FromSeconds(toSeconds.Value)
                : TimeSpan.MaxValue;

        return new() { From = from, To = to };
    }
}
