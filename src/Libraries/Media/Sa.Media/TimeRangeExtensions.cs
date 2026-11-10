using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sa.Media;

/// <summary>
/// Helpers for working with <see cref="TimeRange"/> values.
/// </summary>
public static class TimeRangeExtensions
{

    /// <summary>
    /// Determines whether the range contains the given time.
    /// </summary>
    /// <param name="range">The range to test.</param>
    /// <param name="time">The time to look for.</param>
    /// <returns><c>true</c> if the time lies within the range; otherwise <c>false</c>.</returns>
    public static bool Contains(this TimeRange range, TimeSpan time)
        => time >= range.From && time <= range.To;

    /// <summary>
    /// Determines whether the range contains the given time in milliseconds.
    /// </summary>
    /// <param name="range">The range to test.</param>
    /// <param name="milliseconds">The time to look for, in milliseconds.</param>
    /// <returns><c>true</c> if the time lies within the range; otherwise <c>false</c>.</returns>
    public static bool Contains(this TimeRange range, long milliseconds)
        => milliseconds >= range.From.TotalMilliseconds && milliseconds <= range.To.TotalMilliseconds;

    /// <summary>
    /// Determines whether the range contains the given time in milliseconds.
    /// </summary>
    /// <param name="range">The range to test.</param>
    /// <param name="milliseconds">The time to look for, in milliseconds.</param>
    /// <returns><c>true</c> if the time lies within the range; otherwise <c>false</c>.</returns>
    public static bool Contains(this TimeRange range, double milliseconds)
        => milliseconds >= range.From.TotalMilliseconds && milliseconds <= range.To.TotalMilliseconds;


    /// <summary>
    /// Sorts the ranges in place by their start time.
    /// </summary>
    /// <param name="ranges">The ranges to sort.</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Sort(this Span<TimeRange> ranges)
        => ranges.Sort(static (a, b) => a.From.CompareTo(b.From));


    /// <summary>
    /// Merges ranges that are closer together than the given threshold.
    /// </summary>
    /// <param name="ranges">The ranges to merge.</param>
    /// <param name="thresholdMilliseconds">The maximum gap, in milliseconds, between ranges that should be merged.</param>
    /// <returns>The merged ranges.</returns>
    public static IReadOnlyCollection<TimeRange> Merge(
        this TimeRange[] ranges,
        int thresholdMilliseconds = 300)
            => MergeCloseRanges(ranges, thresholdMilliseconds);



    /// <summary>
    /// Merges close ranges within each chunk and stretches neighbouring ranges towards each other.
    /// </summary>
    /// <param name="chunks">The chunks of ranges to process.</param>
    /// <param name="thresholdMilliseconds">The maximum gap, in milliseconds, between ranges that should be merged.</param>
    /// <param name="gapMilliseconds">An additional gap to preserve between neighbouring ranges.</param>
    /// <returns>The expanded chunks, preserving the original chunk layout.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#pragma warning disable S3776
#pragma warning disable S2368
    public static TimeRange[][] ExpandTimeRanges(
#pragma warning restore S2368
#pragma warning restore S3776
        TimeRange[][] chunks,
        int thresholdMilliseconds = 300,
        int gapMilliseconds = 0)
    {
        if (chunks.Length == 0)
            return chunks;

        // Слияние близких диапазонов внутри каждого чанка
        var mergedChunks = new TimeRange[chunks.Length][];
        for (int c = 0; c < chunks.Length; c++)
        {
            mergedChunks[c] = MergeCloseRanges(chunks[c], thresholdMilliseconds);
        }

        // Подсчёт общего количества после слияния
        int totalCount = 0;
        foreach (var arr in mergedChunks)
            totalCount += arr.Length;

        if (totalCount == 0)
            return mergedChunks;

        // Адаптивное выделение памяти
        Item[] arrayFromPool = ArrayPool<Item>.Shared.Rent(totalCount);
        Span<Item> span = arrayFromPool.AsSpan(0, totalCount);

        try
        {
            // Заполнение плоского массива
            int idx = 0;
            for (int s = 0; s < mergedChunks.Length; s++)
            {
                ref var arr = ref mergedChunks[s];
                for (int r = 0; r < arr.Length; r++)
                {
                    ref var range = ref arr[r];
                    span[idx++] = new Item
                    {
                        From = (long)range.From.TotalMilliseconds,
                        To = (long)range.To.TotalMilliseconds,
                        SourceIdx = s,
                        RangeIdx = r
                    };
                }
            }

            // Сортировка по началу диапазона
            span.Sort(static (a, b) => a.From.CompareTo(b.From));

            // Растягивание 
            ref Item first = ref MemoryMarshal.GetReference(span);
            for (int i = 1; i < span.Length; i++)
            {
                ref var curr = ref Unsafe.Add(ref first, i);
                ref var prev = ref Unsafe.Add(ref first, i - 1);

                long available = curr.From - prev.To - thresholdMilliseconds * 2 - gapMilliseconds;
                if (available > 0)
                {
                    curr.From -= thresholdMilliseconds;
                    prev.To += thresholdMilliseconds;
                }
                else
                {
                    available = curr.From - prev.To - gapMilliseconds;
                    if (available > 0)
                    {
                        var delta = (long)((ulong)available >> 1);
                        curr.From -= delta;
                        prev.To += delta;
                    }
                }

                if (i == span.Length - 1
                    && TimeSpan.MaxValue.TotalMilliseconds - thresholdMilliseconds > curr.To)
                {
                    curr.To += thresholdMilliseconds;
                }
            }

            // первый элемент
            if (span.Length > 0)
            {
                long from = Math.Max(first.From - thresholdMilliseconds, 0);
                first.From = from;

                if (span.Length == 1 && TimeSpan.MaxValue.TotalMilliseconds - thresholdMilliseconds > first.To)
                {
                    first.To += thresholdMilliseconds;
                }
            }

            // Сборка результата 
            var result = new TimeRange[mergedChunks.Length][];
            for (int i = 0; i < mergedChunks.Length; i++)
                result[i] = new TimeRange[mergedChunks[i].Length];

            foreach (ref var item in span)
            {
                result[item.SourceIdx][item.RangeIdx] =
                    TimeRange.Ms(item.From, item.To);
            }

            return result;
        }
        finally
        {
            ArrayPool<Item>.Shared.Return(arrayFromPool);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TimeRange[] MergeCloseRanges(TimeRange[] ranges, int thresholdMilliseconds)
    {
        if (ranges.Length <= 1)
            return ranges;

        // Предварительная сортировка на случай неупорядоченного входа
        var sorted = ranges.AsSpan().ToArray();
        Array.Sort(sorted, static (a, b) => a.From.CompareTo(b.From));

        var merged = new TimeRange[ranges.Length]; // максимум — без слияний
        int count = 0;

        var currentFrom = (long)sorted[0].From.TotalMilliseconds;
        var currentTo = (long)sorted[0].To.TotalMilliseconds;

        for (int i = 1; i < sorted.Length; i++)
        {
            var nextFrom = (long)sorted[i].From.TotalMilliseconds;
            var nextTo = (long)sorted[i].To.TotalMilliseconds;

            // Если расстояние меньше gap — сливаем
            if (nextFrom - currentTo < thresholdMilliseconds)
            {
                // Расширяем текущий диапазон до максимума
                currentTo = Math.Max(currentTo, nextTo);
            }
            else
            {
                // Сохраняем текущий и начинаем новый
                merged[count++] = TimeRange.Ms(currentFrom, currentTo);
                currentFrom = nextFrom;
                currentTo = nextTo;
            }
        }

        // Добавляем последний диапазон
        merged[count++] = TimeRange.Ms(currentFrom, currentTo);

        // Возвращаем массив точного размера
        return count == merged.Length ? merged : merged.AsSpan(0, count).ToArray();
    }

    [StructLayout(LayoutKind.Auto)]
    private struct Item
    {
        public long From;
        public long To;
        public int SourceIdx;
        public int RangeIdx;
    }
}
