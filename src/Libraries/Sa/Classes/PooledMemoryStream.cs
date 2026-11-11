using System.Buffers;

namespace Sa;

/// <summary>
/// A seekable, growable <see cref="Stream"/> over a buffer rented from
/// <see cref="ArrayPool{T}.Shared"/> and returned to the pool on dispose.
/// </summary>
/// <remarks>
/// The semantics follow <see cref="MemoryStream"/> (including seeking past the end, which
/// zero-fills the gap on the next write), but the buffer is pooled instead of being allocated per
/// stream: a stream is created per serialized payload or per batch, and the pooled buffer carries
/// over to the next one after <c>SetLength(0)</c>.
/// <para>
/// This type replaces <c>Microsoft.IO.RecyclableMemoryStream</c>: that package ships without the
/// <c>IsTrimmable</c>/<c>IsAotCompatible</c> assembly metadata in every released version (checked
/// against 3.0.1 and 4.0.1), which fails the library-wide
/// <c>VerifyReferenceTrimCompatibility</c>/<c>VerifyReferenceAotCompatibility</c> build checks.
/// </para>
/// <para>
/// The buffer is returned to the pool without clearing: the pool is process-local and the previous
/// owner (e.g. a payload serializer) already handed the contents to the caller.
/// </para>
/// </remarks>
internal sealed class PooledMemoryStream : Stream
{
    /// <summary>Initial buffer size, in bytes. The buffer doubles as the stream grows.</summary>
    private const int DefaultCapacity = 4096;

    private byte[] _buffer;
    private int _length;
    private int _position;
    private bool _disposed;

    /// <summary>Creates a stream with an empty body and at least <paramref name="capacity"/> bytes of buffer.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    public PooledMemoryStream(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);

        _buffer = ArrayPool<byte>.Shared.Rent(capacity);
    }

    /// <inheritdoc />
    public override bool CanRead => !_disposed;

    /// <inheritdoc />
    public override bool CanSeek => !_disposed;

    /// <inheritdoc />
    public override bool CanWrite => !_disposed;

    /// <inheritdoc />
    public override long Length
    {
        get
        {
            ThrowIfDisposed();

            return _length;
        }
    }

    /// <inheritdoc />
    /// <remarks>Seeking past the end is allowed, like on <see cref="MemoryStream"/>; the gap is zero-filled on write.</remarks>
    public override long Position
    {
        get
        {
            ThrowIfDisposed();

            return _position;
        }
        set
        {
            ThrowIfDisposed();
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, (long)int.MaxValue);

            _position = (int)value;
        }
    }

    /// <inheritdoc />
    public override void Flush()
    {
        // Nothing is buffered outside the rented array, so a flush is a no-op.
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (buffer.Length - offset < count)
        {
            throw new ArgumentException(
                "Offset and length were out of bounds for the array or count is greater than " +
                "the number of elements from index to the end of the source collection.",
                nameof(buffer));
        }

        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override int Read(Span<byte> destination)
    {
        ThrowIfDisposed();

        int bytesRead = Math.Min(destination.Length, _length - _position);

        if (bytesRead <= 0)
        {
            return 0;
        }

        _buffer.AsSpan(_position, bytesRead).CopyTo(destination);
        _position += bytesRead;

        return bytesRead;
    }

    /// <inheritdoc />
    public override int ReadByte()
    {
        ThrowIfDisposed();

        if (_position >= _length)
        {
            return -1;
        }

        return _buffer[_position++];
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();

        long newPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => (long)_position + offset,
            SeekOrigin.End => (long)_length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Invalid seek origin."),
        };

        if (newPosition < 0)
        {
            throw new IOException("An attempt was made to move the position before the beginning of the stream.");
        }

        if (newPosition > int.MaxValue)
        {
            throw new IOException("An attempt was made to move the position past the maximum stream length (2 GB).");
        }

        _position = (int)newPosition;

        return _position;
    }

    /// <inheritdoc />
    public override void SetLength(long value)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, (long)int.MaxValue);

        int newLength = (int)value;

        EnsureCapacity(newLength);

        if (newLength > _length)
        {
            // The extended region must read as zeros: the pooled buffer may hold another stream's data.
            Array.Clear(_buffer, _length, newLength - _length);
        }

        _length = newLength;

        if (_position > _length)
        {
            _position = _length;
        }
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (buffer.Length - offset < count)
        {
            throw new ArgumentException(
                "Offset and length were out of bounds for the array or count is greater than " +
                "the number of elements from index to the end of the source collection.",
                nameof(buffer));
        }

        Write(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> source)
    {
        ThrowIfDisposed();

        if (source.Length > int.MaxValue - _position)
        {
            throw new IOException("An attempt was made to move the position past the maximum stream length (2 GB).");
        }

        int newPosition = _position + source.Length;

        // Grows first: the gap (position > length) is cleared after the copy, against the final buffer.
        EnsureCapacity(newPosition);

        if (_position > _length)
        {
            Array.Clear(_buffer, _length, _position - _length);
            _length = _position;
        }

        source.CopyTo(_buffer.AsSpan(_position));
        _position = newPosition;

        if (_position > _length)
        {
            _length = _position;
        }
    }

    /// <inheritdoc />
    public override void WriteByte(byte value) => Write([value]);

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        // Like MemoryStream: the work is an in-memory copy, so it runs synchronously.
        Write(buffer, offset, count);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => Task.FromResult(Read(buffer, offset, count));

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Read(buffer.Span));

    /// <summary>Copies the written part of the stream (bytes <c>0..Length</c>) into a new array.</summary>
    /// <exception cref="ObjectDisposedException">The stream has been disposed.</exception>
    public byte[] ToArray()
    {
        ThrowIfDisposed();

        return _buffer.AsSpan(0, _length).ToArray();
    }

    /// <summary>The written part of the rented buffer: <c>0..Length</c> of the current buffer.</summary>
    /// <remarks>
    /// The segment is only valid until the next write grows the buffer or the stream is disposed;
    /// use it for a zero-copy handoff (writing the payload straight into a transport) and fall back
    /// to <see cref="ToArray"/> when the data must outlive the stream.
    /// </remarks>
    public ReadOnlyMemory<byte> WrittenMemory
    {
        get
        {
            ThrowIfDisposed();

            return _buffer.AsMemory(0, _length);
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;

            byte[] buffer = _buffer;
            _buffer = [];
            ArrayPool<byte>.Shared.Return(buffer);
        }

        base.Dispose(disposing);
    }

    /// <summary>Doubles the buffer until it fits <paramref name="required"/> bytes, preserving the written part.</summary>
    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length)
        {
            return;
        }

        int newCapacity = _buffer.Length <= int.MaxValue / 2 ? _buffer.Length * 2 : int.MaxValue;

        if (newCapacity < required)
        {
            newCapacity = required;
        }

        byte[] newBuffer = ArrayPool<byte>.Shared.Rent(newCapacity);
        _buffer.AsSpan(0, _length).CopyTo(newBuffer);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = newBuffer;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
