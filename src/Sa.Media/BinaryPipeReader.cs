using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;

namespace Sa.Media;

internal sealed class BinaryPipeReader(PipeReader reader)
{
    public long Position { get; private set; }

    public async ValueTask<uint> ReadUInt32Async(CancellationToken cancellationToken = default)
    {
        var idBuffer = await reader.ReadAtLeastAsync(4, cancellationToken);
        uint result = ReadUInt32Little(idBuffer.Buffer);
        reader.AdvanceTo(idBuffer.Buffer.GetPosition(4));
        Position += 4;
        return result;
    }

    public async ValueTask<ushort> ReadUInt16Async(CancellationToken cancellationToken = default)
    {
        var idBuffer = await reader.ReadAtLeastAsync(2, cancellationToken);
        ushort result = ReadUInt16Little(idBuffer.Buffer);
        reader.AdvanceTo(idBuffer.Buffer.GetPosition(2));
        Position += 2;
        return result;
    }

    public async Task SkipBytesAsync(long count, CancellationToken cancellationToken = default)
    {
        Position += count;
        await PipeReaderExtensions.SkipAsync(reader, count, cancellationToken);
    }

    private static uint ReadUInt32Little(ReadOnlySequence<byte> seq)
    {
        if (seq.Length >= 4 && seq.IsSingleSegment)
            return BinaryPrimitives.ReadUInt32LittleEndian(seq.First.Span);

        // Многосегментный или недостаточно байт в одном сегменте
        Span<byte> buf = stackalloc byte[4];
        CopyTo(seq, buf);
        return BinaryPrimitives.ReadUInt32LittleEndian(buf);
    }

    private static ushort ReadUInt16Little(ReadOnlySequence<byte> seq)
    {
        if (seq.Length >= 2 && seq.IsSingleSegment)
            return BinaryPrimitives.ReadUInt16LittleEndian(seq.First.Span);

        Span<byte> buf = stackalloc byte[2];
        CopyTo(seq, buf);
        return BinaryPrimitives.ReadUInt16LittleEndian(buf);
    }

    private static void CopyTo(ReadOnlySequence<byte> seq, Span<byte> destination)
    {
        int copied = 0;
        foreach (var segment in seq)
        {
            int toCopy = Math.Min(segment.Length, destination.Length - copied);
            segment.Span[..toCopy].CopyTo(destination[copied..]);
            copied += toCopy;
            if (copied >= destination.Length) break;
        }
    }
}
