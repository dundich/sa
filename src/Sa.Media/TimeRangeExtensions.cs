using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sa.Media;

public static class TimeRangeExpander
{

    static bool NeedsMerging(
        this Span<TimeRange> ranges,
        TimeSpan threshold)
    {
        if (ranges.Length < 2)
            return false;

        Sort(ranges);

        for (int i = 1; i < ranges.Length; i++)
        {
            var prev = ranges[i - 1];
            var curr = ranges[i];

            if (!prev.HasEnd || !curr.HasEnd)
                continue;

            var gap = curr.From - prev.To!.Value;
            if (gap <= threshold && gap >= TimeSpan.Zero)
                return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Sort(this Span<TimeRange> ranges)
    {
        ranges.Sort(static (a, b) => a.From.CompareTo(b.From));
    }

    public static IReadOnlyCollection<TimeRange> Merge(
        this Span<TimeRange> ranges,
        TimeSpan threshold)
    {
        if (ranges.Length == 0)
            return [];

        if (!ranges.NeedsMerging(threshold))
            return ranges.ToArray();

        using var result = new PooledList<TimeRange>(ranges.Length);
        var current = ranges[0];

        for (int i = 1; i < ranges.Length; i++)
        {
            var next = ranges[i];

            if (!next.HasEnd) continue;

            var gap = next.From - current.To!.Value;

            if (gap <= threshold && gap >= TimeSpan.Zero)
            {
                current = new TimeRange(
                    current.From,
                    next.To!.Value > current.To!.Value ? next.To!.Value : current.To!.Value
                );
            }
            else
            {
                result.Add(current);
                current = next;
            }
        }

        result.Add(current);

        return result.ToArray();
    }

    public static IReadOnlyCollection<TimeRange> Merge(
        this Span<TimeRange> ranges,
        int thresholdMillesecods = 300)
            => Merge(ranges, TimeSpan.FromMilliseconds(thresholdMillesecods));


    // Вспомогательный класс для pooling
    internal ref struct PooledList<T>(int capacity)
    {
        private T[] _array = ArrayPool<T>.Shared.Rent(capacity);
        private int _count = 0;

        public void Add(T item)
        {
            if (_count >= capacity)
                throw new InvalidOperationException("Capacity exceeded");

            _array[_count++] = item;
        }

        public readonly T[] ToArray()
        {
            var result = new T[_count];
            Array.Copy(_array, result, _count);
            return result;
        }

        public void Dispose()
        {
            if (_array != null)
            {
                ArrayPool<T>.Shared.Return(_array);
                _array = null!;
            }
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static TimeRange[][] ExpandTimeRanges(
       TimeRange[][] chunks,
       int gapMilliseconds = 100)
    {
        if (chunks.Length == 0)
            return chunks;

        // Слияние близких диапазонов внутри каждого чанка
        var mergedChunks = new TimeRange[chunks.Length][];
        for (int c = 0; c < chunks.Length; c++)
        {
            mergedChunks[c] = MergeCloseRanges(chunks[c], gapMilliseconds);
        }

        // Подсчёт общего количества после слияния
        int totalCount = 0;
        foreach (var arr in mergedChunks)
            totalCount = checked(totalCount + arr.Length);

        if (totalCount == 0)
            return mergedChunks;

        // Адаптивное выделение памяти
        Item[]? arrayFromPool = null;
        Span<Item> span = totalCount <= 256
            ? stackalloc Item[totalCount]
            : (arrayFromPool = ArrayPool<Item>.Shared.Rent(totalCount)).AsSpan(0, totalCount);

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
                        To = range.To.HasValue
                            ? (long)range.To.Value.TotalMilliseconds
                            : (long)TimeSpan.MaxValue.TotalMilliseconds,
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

                long available = curr.From - prev.To - gapMilliseconds;
                if (available > 0)
                {
                    var delta = (long)((ulong)available >> 1);
                    curr.From -= delta;
                    prev.To += delta;
                }

                if (i == span.Length - 1 && long.MaxValue - gapMilliseconds > curr.To)
                {
                    curr.To += gapMilliseconds;
                }
            }

            // первый элемент
            if (span.Length > 0)
            {
                long from = Math.Max(first.From - gapMilliseconds, 0);
                first.From = from;

                if (span.Length == 1 && long.MaxValue - gapMilliseconds > first.To)
                {
                    first.To += gapMilliseconds;
                }
            }

            // Сборка результата 
            var result = new TimeRange[mergedChunks.Length][];
            for (int i = 0; i < mergedChunks.Length; i++)
                result[i] = new TimeRange[mergedChunks[i].Length];

            foreach (ref var item in span)
            {
                result[item.SourceIdx][item.RangeIdx] =
                    TimeRange.RangeFromMilliseconds(item.From, item.To);
            }

            return result;
        }
        finally
        {
            if (arrayFromPool != null)
                ArrayPool<Item>.Shared.Return(arrayFromPool);
        }
    }

    /// <summary>
    /// Сливает диапазоны внутри одного чанка, если расстояние между ними &lt; gap
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TimeRange[] MergeCloseRanges(TimeRange[] ranges, int gapMs)
    {
        if (ranges.Length <= 1)
            return ranges;

        // Предварительная сортировка на случай неупорядоченного входа
        var sorted = ranges.AsSpan().ToArray();
        Array.Sort(sorted, static (a, b) => a.From.CompareTo(b.From));

        var merged = new TimeRange[ranges.Length]; // максимум — без слияний
        int count = 0;

        var currentFrom = (long)sorted[0].From.TotalMilliseconds;
        var currentTo = (long)sorted[0].To!.Value.TotalMilliseconds;

        for (int i = 1; i < sorted.Length; i++)
        {
            var nextFrom = (long)sorted[i].From.TotalMilliseconds;
            var nextTo = (long)sorted[i].To!.Value.TotalMilliseconds;

            // Если расстояние меньше gap — сливаем
            if (nextFrom - currentTo < gapMs)
            {
                // Расширяем текущий диапазон до максимума
                currentTo = Math.Max(currentTo, nextTo);
            }
            else
            {
                // Сохраняем текущий и начинаем новый
                merged[count++] = TimeRange.RangeFromMilliseconds(currentFrom, currentTo);
                currentFrom = nextFrom;
                currentTo = nextTo;
            }
        }

        // Добавляем последний диапазон
        merged[count++] = TimeRange.RangeFromMilliseconds(currentFrom, currentTo);

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

