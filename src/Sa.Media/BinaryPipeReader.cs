using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;

namespace Sa.Media;

internal sealed class BinaryPipeReader(PipeReader reader)
{
    public long Position { get; private set; }

    public async ValueTask<uint> ReadUInt32Async(CancellationToken cancellationToken = default)
    {
        var idBuffer = await reader.ReadAtLeastAsync(4, cancellationToken).ConfigureAwait(false);
        uint result = ReadUInt32Little(idBuffer.Buffer);
        reader.AdvanceTo(idBuffer.Buffer.GetPosition(4));
        Position += 4;
        return result;
    }

    public async ValueTask<ushort> ReadUInt16Async(CancellationToken cancellationToken = default)
    {
        var idBuffer = await reader.ReadAtLeastAsync(2, cancellationToken).ConfigureAwait(false);
        ushort result = ReadUInt16Little(idBuffer.Buffer);
        reader.AdvanceTo(idBuffer.Buffer.GetPosition(2));
        Position += 2;
        return result;
    }

    public async ValueTask<int> ReadInt32Async(CancellationToken cancellationToken = default)
    {
        var idBuffer = await reader.ReadAtLeastAsync(4, cancellationToken).ConfigureAwait(false);
        int result = ReadInt32Little(idBuffer.Buffer);
        reader.AdvanceTo(idBuffer.Buffer.GetPosition(4));
        Position += 4;
        return result;
    }

    public async ValueTask<Guid> ReadGuidAsync(CancellationToken cancellationToken = default)
    {
        var seq = await reader.ReadAtLeastAsync(16, cancellationToken).ConfigureAwait(false);
        Span<byte> tmp = stackalloc byte[16];
        seq.Buffer.Slice(0, 16).CopyTo(tmp);
        reader.AdvanceTo(seq.Buffer.GetPosition(16));
        Position += 16;
        return new Guid(tmp);
    }

    public async Task SkipBytesAsync(long count, CancellationToken cancellationToken = default)
    {
        Position += count;
        await PipeReaderExtensions.SkipAsync(reader, count, cancellationToken).ConfigureAwait(false);
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

    private static int ReadInt32Little(ReadOnlySequence<byte> seq)
    {
        if (seq.Length >= 4 && seq.IsSingleSegment)
            return BinaryPrimitives.ReadInt32LittleEndian(seq.First.Span);

        Span<byte> buf = stackalloc byte[4];
        CopyTo(seq, buf);
        return BinaryPrimitives.ReadInt32LittleEndian(buf);
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
        seq.Slice(0, Math.Min(seq.Length, destination.Length))
            .CopyTo(destination);
    }
}
