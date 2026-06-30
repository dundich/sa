using System.Buffers;
using System.IO.Pipelines;

namespace Sa.Media;

internal static class PipeReaderExtensions
{
    /// <summary>
    /// Пропускает указанное количество байт в PipeReader, эффективно обрабатывая многосегментные последовательности.
    /// </summary>
    public static async ValueTask<long> SkipAsync(this PipeReader reader, long count, CancellationToken ct = default)
    {
        long remaining = count;
        while (remaining > 0)
        {
            ReadResult result = await reader.ReadAsync(ct).ConfigureAwait(false);
            if (result.Buffer.IsEmpty && result.IsCompleted)
                break; // Недостаточно данных

            var toConsume = Math.Min(remaining, result.Buffer.Length);
            var consumed = result.Buffer.GetPosition(toConsume);
            reader.AdvanceTo(consumed, consumed);
            remaining -= toConsume;
        }
        return count - remaining;
    }

    /// <summary>
    /// Эффективно пропускает данные, продвигая Buffer полностью когда возможно.
    /// Минимизирует количество вызовов AdvanceTo.
    /// </summary>
    public static async ValueTask SkipFullSegmentsAsync(this PipeReader reader, long count, CancellationToken ct = default)
    {
        long remaining = count;
        while (remaining > 0)
        {
            ReadResult result = await reader.ReadAsync(ct).ConfigureAwait(false);
            if (result.Buffer.IsEmpty && result.IsCompleted)
                return;

            var toConsume = Math.Min(remaining, result.Buffer.Length);
            var consumed = result.Buffer.GetPosition(toConsume);

            // Продвигаем Buffer до consumed, frontier тоже
            reader.AdvanceTo(consumed, consumed);
            remaining -= toConsume;
        }
    }
}
