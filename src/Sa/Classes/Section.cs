using System.Diagnostics;

namespace Sa.Classes;

/// <summary>
/// line
/// экземпляр с конкретным началом и окончанием
/// </summary>
/// <typeparam name="T"></typeparam>
/// <param name="Start">начало</param>
/// <param name="End">конец</param>
[DebuggerStepThrough]
public record Section<T>(T Start, T End) where T : IComparable<T>;

/// <summary>
/// line with lim end
/// экземпляр с конкретным началом, окончанием и указанием включен ли конец в диапазон
/// </summary>
/// <typeparam name="T"></typeparam>
/// <param name="Start">начало</param>
/// <param name="End">конец</param>
/// <param name="HasEnd">Indicates whether the value at the end of the range is included</param>

[DebuggerStepThrough]
public record LimSection<T>(T Start, T End, bool HasEnd = false)
    : Section<T>(Start, End) where T : IComparable<T>;

/// <summary>
/// half-line or ray
/// экземпляр с конкретным началом, возможным окончанием и указанием включен ли конец в диапазон
/// </summary>
/// <typeparam name="T"></typeparam>
/// <param name="Start">начало</param>
/// <param name="End">конец или бесконечность</param>
/// <param name="HasEnd">Indicates whether the value at the end of the range is included</param>
[DebuggerStepThrough]
public record HalfSection<T>(T Start, T? End, bool HasEnd = false);

[DebuggerStepThrough]
public static class RangeExtensions
{
    public static LimSection<T> RangeTo<T>(Section<T> range, bool hasEnd) where T : IComparable<T> => new(range.Start, range.End, hasEnd);
    public static Section<T> RangeTo<T>(this T from, T to) where T : IComparable<T> => new(from, to);
    public static Section<T> RangeTo<T>(this T from, Func<T, T> to) where T : IComparable<T> => new(from, to(from));
    public static LimSection<T> RangeTo<T>(this T from, T to, bool hasEnd) where T : IComparable<T> => new(from, to, hasEnd);
    public static LimSection<T> RangeTo<T>(this T from, Func<T, T> to, bool hasEnd) where T : IComparable<T> => new(from, to(from), hasEnd);
    public static bool IsPositive<T>(this Section<T> range) where T : IComparable<T> => range.Start.CompareTo(range.End) <= 0;
    public static bool IsPositive<T>(this LimSection<T> range) where T : IComparable<T> => range.Start.CompareTo(range.End) <= 0;
    public static Section<T> Reverse<T>(this Section<T> range) where T : IComparable<T> => range.End.RangeTo(range.Start);
    public static LimSection<T> Reverse<T>(this LimSection<T> range) where T : IComparable<T> => range.End.RangeTo(range.Start, range.HasEnd);
    public static Section<T> Normalize<T>(this Section<T> range) where T : IComparable<T> => range.IsPositive() ? range : Reverse(range);
    public static LimSection<T> Normalize<T>(this LimSection<T> range) where T : IComparable<T> => range.IsPositive() ? range : Reverse(range);
    public static bool InRange<T>(this Section<T> range, T value) where T : IComparable<T> => range.Start.CompareTo(value) <= 0 && (range.End.CompareTo(value) >= 0);
    public static bool InRange<T>(this LimSection<T> range, T value) where T : IComparable<T>
        => range.Start.CompareTo(value) <= 0 && (range.HasEnd ? range.End.CompareTo(value) >= 0 : range.End.CompareTo(value) > 0);

    /// <summary>
    /// список пустых (незанятых) интервалов
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="range">интервал</param>
    /// <param name="busyIntervals"> список отрезков, которые заняты</param>
    /// <returns>список пустых (незанятых) интервалов</returns>
    public static List<Section<T>> FindEmptyIntervals<T>(this Section<T> range, IEnumerable<Section<T>> busyIntervals)
        where T : IComparable<T>
    {
        List<Section<T>> emptyIntervals = [];

        // Сортируем временные отрезки по начальному времени busyIntervals
        Section<T>[] sortedBusyIntervals = [.. busyIntervals];

        if (sortedBusyIntervals.Length > 1)
        {
            Array.Sort(sortedBusyIntervals, (a, b) => a.Start.CompareTo(b.Start));
        }

        T currentStart = range.Start;
        foreach (Section<T> interval in sortedBusyIntervals)
        {
            if (interval.Start.CompareTo(currentStart) > 0)
            {
                T currentEnd = interval.Start;
                emptyIntervals.Add(new Section<T>(currentStart, currentEnd));
            }
            currentStart = interval.End;
        }

        if (range.End.CompareTo(currentStart) > 0)
        {
            emptyIntervals.Add(new Section<T>(currentStart, range.End));
        }

        return emptyIntervals;
    }

    /// <summary>
    /// Поиск пересечения
    /// </summary>
    public static Section<T>? FindIntersections<T>(this Section<T> self, Section<T> other)
        where T : IComparable<T>
    {
        if (other.End.CompareTo(self.Start) >= 0 && self.End.CompareTo(other.Start) >= 0)
        {
            T start = self.Start.CompareTo(other.Start) >= 0 ? self.Start : other.Start;
            T end = self.End.CompareTo(other.End) < 0 ? self.End : other.End;
            return new Section<T>(start, end);
        }

        return null;
    }

    /// <summary>
    /// объединения интервалов
    /// </summary>
    public static List<Section<T>> MergeIntervals<T>(this Section<T> self, IEnumerable<Section<T>> intervals)
        where T : IComparable<T>
    {
        var list = new List<Section<T>>(intervals)
        {
            self
        };

        return MergeIntervals(list);
    }

    /// <summary>
    /// объединения интервалов
    /// </summary>
    public static List<Section<T>> MergeIntervals<T>(IEnumerable<Section<T>> intervals)
        where T : IComparable<T>
    {
        List<Section<T>> sortedList = [.. intervals];

        if (sortedList.Count > 1)
        {
            sortedList.Sort((a, b) => a.Start.CompareTo(b.Start)); // Сортировка по начальным точкам
        }

        List<Section<T>> mergedIntervals = [];

        Section<T> currentInterval = sortedList[0];

        foreach (Section<T> interval in sortedList)
        {
            if (currentInterval.End.CompareTo(interval.Start) >= 0) // Пересечение интервалов
            {
                var currentEnd = currentInterval.End.CompareTo(interval.End) >= 0
                    ? currentInterval.End
                    : interval.End;

                currentInterval = new Section<T>(currentInterval.Start, currentEnd);
            }
            else
            {
                mergedIntervals.Add(currentInterval);
                currentInterval = interval;
            }
        }

        mergedIntervals.Add(currentInterval); // Добавление последнего интервала

        return mergedIntervals;
    }


    public static IEnumerable<T> Enumerate<T>(this Section<T> self, Func<T?, T> next)
        where T : IComparable<T>
    {
        T? c = self.Start;
        while (c is not null && self.End.CompareTo(c) > 0)
        {
            yield return c;
            c = next(c);
        }
    }


    /// <summary>
    /// разделения интервала
    /// </summary>
    public static List<Section<T>> SplitInterval<T>(this Section<T> self, params T[] points)
        where T : IComparable<T> => SplitInterval(self, points.AsEnumerable());

    public static List<Section<T>> SplitInterval<T>(this Section<T> self, IEnumerable<T> points)
      where T : IComparable<T>
    {
        var sortedPoints = points.ToArray();

        if (sortedPoints.Length > 1) Array.Sort(sortedPoints);

        List<Section<T>> splitIntervals = [];

        T prevPoint = self.Start;
        foreach (T point in sortedPoints
            .Where(point => point.CompareTo(self.Start) > 0 && self.End.CompareTo(point) > 0))
        {
            splitIntervals.Add(prevPoint.RangeTo(point));
            prevPoint = point;
        }

        splitIntervals.Add(prevPoint.RangeTo(self.End));
        return splitIntervals;
    }

    /// <summary>
    /// разбиение интервала
    /// </summary>
    public static List<Section<T>> SplitInterval<T>(this Section<T> self, Func<T?, T> next)
        where T : IComparable<T> => SplitInterval(self, Enumerate(self, next).Skip(1));

    /// <summary>
    /// проверки включения интервалов
    /// </summary>
    public static bool IsIntervalIncluded<T>(this Section<T> self, Section<T> other)
        where T : IComparable<T> => other.Start.CompareTo(self.Start) >= 0 && self.End.CompareTo(other.End) >= 0;

    public static bool IsPoint<T>(this Section<T> self) where T : IComparable<T> => self.Start.CompareTo(self.End) == 0;

    /// <summary>
    /// Пуста ли секция, то есть не содержит ли ни одного значения: <c>Start &gt; End</c>.
    /// <para>
    /// Проверять «секция равна <c>Empty</c>» нельзя. Для любого value-типа
    /// <c>T</c> обычная <c>new Section&lt;int&gt;(0, 0)</c> сравнима по record-равенству
    /// с <c>new Section&lt;int&gt;(default, default)</c>, то есть с sentinel, и такой тест
    /// объявлял пустой совершенно законно построенную точку. Поэтому <c>Empty</c>-sentinel'ы
    /// убраны, а emptiness определяется структурно.
    /// </para>
    /// </summary>
    public static bool IsEmpty<T>(this Section<T> self) where T : IComparable<T> => self.Start.CompareTo(self.End) > 0;

    /// <inheritdoc cref="IsEmpty{T}(Section{T})"/>
    /// <remarks>
    /// У <see cref="LimSection{T}"/> секция с равными границами пуста, если конец не включён:
    /// <c>(0, 0)</c> не содержит ни одного значения, а <c>[0, 0]</c> содержит точку 0.
    /// </remarks>
    public static bool IsEmpty<T>(this LimSection<T> self) where T : IComparable<T>
    {
        var order = self.Start.CompareTo(self.End);
        return order > 0 || (order == 0 && !self.HasEnd);
    }

    /// <inheritdoc cref="IsEmpty{T}(Section{T})"/>
    /// <remarks>
    /// У <see cref="HalfSection{T}"/> незаданный конец (<c>End == null</c>) означает бесконечность,
    /// а не пустоту, поэтому сравнивать не с чем и такая секция непуста.
    /// </remarks>
    public static bool IsEmpty<T>(this HalfSection<T> self) where T : IComparable<T>
        => self.End is not null && self.End.CompareTo(self.Start) < 0;

    public static TimeSpan GetLength(this Section<DateTime> range) => range.End.ToUniversalTime() - range.Start.ToUniversalTime();
    public static int GetLength(this Section<int> range) => range.End - range.Start;
    public static long GetLength(this Section<long> range) => range.End - range.Start;
    public static float GetLength(this Section<float> range) => range.End - range.Start;
    public static double GetLength(this Section<double> range) => range.End - range.Start;
}
