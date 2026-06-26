using System.IO.Pipelines;

namespace Sa.Media;

internal static class PipeReaderExtensions
{
    public static async ValueTask<long> SkipAsync(this PipeReader reader, long count, CancellationToken ct = default)
    {
        long remaining = count;
        while (remaining > 0)
        {
            ReadResult result = await reader.ReadAsync(ct);
            if (result.Buffer.IsEmpty && result.IsCompleted)
                break; // или throw new InvalidOperationException("Недостаточно данных")

            var toConsume = Math.Min(remaining, result.Buffer.Length);
            var consumed = result.Buffer.GetPosition(toConsume);
            reader.AdvanceTo(consumed, consumed);
            remaining -= toConsume;
        }
        return count - remaining; // сколько фактически пропущено
    }
}
