using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;

namespace Sa.Media;

internal sealed class BinaryPipeReader(PipeReader reader)
{
    public long Position { get; private set; }

    public async ValueTask<uint> ReadUInt32Async(CancellationToken cancellationToken = default)
    {
        ReadResult result = await reader.ReadAtLeastAsync(4, cancellationToken).ConfigureAwait(false);
        EnsureAvailable(result, 4);
        uint value = ReadUInt32Little(result.Buffer);
        reader.AdvanceTo(result.Buffer.GetPosition(4));
        Position += 4;
        return value;
    }

    public async ValueTask<ushort> ReadUInt16Async(CancellationToken cancellationToken = default)
    {
        ReadResult result = await reader.ReadAtLeastAsync(2, cancellationToken).ConfigureAwait(false);
        EnsureAvailable(result, 2);
        ushort value = ReadUInt16Little(result.Buffer);
        reader.AdvanceTo(result.Buffer.GetPosition(2));
        Position += 2;
        return value;
    }

    public async ValueTask<int> ReadInt32Async(CancellationToken cancellationToken = default)
    {
        ReadResult result = await reader.ReadAtLeastAsync(4, cancellationToken).ConfigureAwait(false);
        EnsureAvailable(result, 4);
        int value = ReadInt32Little(result.Buffer);
        reader.AdvanceTo(result.Buffer.GetPosition(4));
        Position += 4;
        return value;
    }

    public async ValueTask<Guid> ReadGuidAsync(CancellationToken cancellationToken = default)
    {
        ReadResult result = await reader.ReadAtLeastAsync(16, cancellationToken).ConfigureAwait(false);
        EnsureAvailable(result, 16);
        Span<byte> tmp = stackalloc byte[16];
        result.Buffer.Slice(0, 16).CopyTo(tmp);
        reader.AdvanceTo(result.Buffer.GetPosition(16));
        Position += 16;
        return new Guid(tmp);
    }

    public async Task SkipBytesAsync(long count, CancellationToken cancellationToken = default)
    {
        long skipped = await PipeReaderExtensions.SkipAsync(reader, count, cancellationToken).ConfigureAwait(false);
        Position += skipped;

        if (skipped < count)
            throw new InvalidDataException(
                $"Unexpected end of WAV file: expected {count} more byte(s), got {skipped}");
    }

    /// <summary>
    /// ReadAtLeast возвращает меньше запрошенного, если поток закончился. Без этой проверки
    /// GetPosition()/BinaryPrimitives бросали ArgumentOutOfRangeException вместо понятной ошибки.
    /// </summary>
    private static void EnsureAvailable(ReadResult result, int count)
    {
        if (result.Buffer.Length < count)
            throw new InvalidDataException(
                $"Unexpected end of WAV file: expected {count} byte(s), got {result.Buffer.Length}");
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
