using System.Buffers;

namespace Sa.Media;


/// <summary>
/// Helper class for reading binary values safely
/// </summary>
internal class Reader(Stream stream)
{
    private readonly Memory<byte> _buffer = new byte[sizeof(uint)];

    public long Position { get; private set; }

    public async Task<uint> ReadUInt32Async(CancellationToken cancellationToken)
    {
        await stream.ReadExactlyAsync(_buffer, cancellationToken);
        Position += _buffer.Length;
        return BitConverter.ToUInt32(_buffer.Span);
    }

    public async Task<ushort> ReadUInt16Async(CancellationToken cancellationToken)
    {
        var buffer = _buffer[..sizeof(ushort)];
        await stream.ReadExactlyAsync(buffer, cancellationToken);
        return BitConverter.ToUInt16(buffer.Span);
    }

    public async Task SkeepBytesAsync(long offset, CancellationToken cancellationToken)
    {
        if (offset == 0) return;

        if (stream.CanSeek)
        {
            stream.Seek(offset, SeekOrigin.Current);
            Position = stream.Position;
        }
        else
        {
            Position += offset;

            long bufferSize = Math.Min(4096, offset);
            byte[] buffer = ArrayPool<byte>.Shared.Rent((int)bufferSize);
            try
            {
                while (offset > 0)
                {
                    long toRead = Math.Min(bufferSize, offset);
                    await stream.ReadExactlyAsync(buffer, 0, (int)toRead, cancellationToken);
                    offset -= toRead;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}

