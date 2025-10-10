using System.IO.Pipelines;

namespace Sa.Media;

internal sealed class BinaryPipeReader(PipeReader reader)
{
    public long Position { get; private set; }

    public async ValueTask<uint> ReadUInt32Async(CancellationToken cancellationToken)
    {
        var idBuffer = await reader.ReadAtLeastAsync(4, cancellationToken);
        uint result = BitConverter.ToUInt32(idBuffer.Buffer.First.Span);
        reader.AdvanceTo(idBuffer.Buffer.GetPosition(4));
        Position += 4;
        return result;
    }

    public async ValueTask<ushort> ReadUInt16Async(CancellationToken cancellationToken)
    {
        var idBuffer = await reader.ReadAtLeastAsync(2, cancellationToken);
        ushort result = BitConverter.ToUInt16(idBuffer.Buffer.First.Span);
        reader.AdvanceTo(idBuffer.Buffer.GetPosition(2));
        Position += 2;
        return result;
    }

    public async Task SkeepBytesAsync(long offset, CancellationToken cancellationToken)
    {
        await reader.SkipAsync(offset, cancellationToken);
        Position += offset;
    }
}