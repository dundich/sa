using System.IO.Pipelines;

namespace Sa.Media;

public static class PipeReaderExtensions
{
    public static async ValueTask SkipAsync(this PipeReader reader, long count, CancellationToken ct = default)
    {
        while (count > 0)
        {
            ReadResult result = await reader.ReadAsync(ct);
            SequencePosition consumed = result.Buffer.GetPosition(Math.Min(count, result.Buffer.Length));
            reader.AdvanceTo(consumed);
            count -= result.Buffer.Length;
        }
    }
}