using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Sa.Media;

internal sealed class AsyncWavWriter : IDisposable, IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly uint _sampleRate;
    private readonly ushort _bitsPerSample;
    private readonly ushort _numChannels;
    private readonly bool _leaveOpen;
    private long _dataSize = 0;

    private readonly IMemoryOwner<byte> _bufferOwner;
    private Memory<byte> _currentBuffer;
    private int _currentBufferSize;

    public AsyncWavWriter(
        Stream stream,
        uint sampleRate,
        ushort bitsPerSample = 16,
        ushort numChannels = 1,
        bool leaveOpen = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (!_stream.CanWrite)
            throw new ArgumentException("Stream must be writable", nameof(stream));

        _sampleRate = sampleRate;
        _bitsPerSample = bitsPerSample;
        _numChannels = numChannels;
        _leaveOpen = leaveOpen;

        _bufferOwner = MemoryPool<byte>.Shared.Rent(8192);
        _currentBuffer = _bufferOwner.Memory;
        _currentBufferSize = 0;

        WriteHeader(); // пишем заголовок с нулевым размером данных
    }

    /// <summary>
    /// Пишет WAV заголовок с заглушкой на размер данных
    /// </summary>
    private void WriteHeader()
    {
        Span<byte> header = stackalloc byte[44]; // стандартный WAV заголовок
        int offset = 0;

        // RIFF Header
        WriteBytes(header, ref offset, "RIFF");
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0); // chunkSize (заглушка)
        offset += 4;
        WriteBytes(header, ref offset, "WAVE");

        // fmt Subchunk
        WriteBytes(header, ref offset, "fmt ");
        BinaryPrimitives.WriteUInt32LittleEndian(header[offset..], 16); // subchunk1Size
        offset += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(header[offset..], 1); // audioFormat: PCM
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(header[offset..], _numChannels);
        offset += 2;
        BinaryPrimitives.WriteUInt32LittleEndian(header[offset..], _sampleRate);
        offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(header[offset..], (uint)(_sampleRate * _numChannels * (_bitsPerSample / 8))); // byteRate
        offset += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(header[offset..], (ushort)(_numChannels * (_bitsPerSample / 8))); // blockAlign
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(header[offset..], _bitsPerSample); // bitsPerSample
        offset += 2;

        // data Subchunk
        WriteBytes(header, ref offset, "data");

        BinaryPrimitives.WriteUInt32LittleEndian(header[offset..], 0); // subchunk2Size (заглушка)
        offset += 4;

        _stream.Write(header);
    }

    ///// <summary>
    ///// Асинхронно записывает блок нормализованных семплов по каналам
    ///// Каждый элемент samples — семплы для одного канала
    ///// Все ReadOnlyMemory должны быть одинаковой длины
    ///// </summary>
    public async ValueTask WriteSamplesAsync(ReadOnlyMemory<double> interleavedSamples, CancellationToken cancellationToken = default)
    {
        if (MemoryMarshal.TryGetArray(interleavedSamples, out var segment))
        {
            await WriteSamplesAsync(segment.Array!, segment.Offset, segment.Count, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Данные не в backing-массиве — копируем в локальный буфер
        var localBuffer = ArrayPool<double>.Shared.Rent(interleavedSamples.Length);
        try
        {
            interleavedSamples.Span.CopyTo(localBuffer);
            await WriteSamplesAsync(localBuffer, 0, interleavedSamples.Length, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(localBuffer);
        }
    }

    private async Task WriteSamplesAsync(double[] samples, int offset, int count, CancellationToken cancellationToken)
    {
        for (int i = offset; i < count; i++)
        {
            WriteSampleCore(samples[i]);
            if (_currentBufferSize >= _currentBuffer.Length)
                await FlushBufferAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void WriteSampleCore(double sample)
    {
        switch (_bitsPerSample)
        {
            case 8:
                WriteSample8Bit(sample);
                break;
            case 16:
                WriteSample16Bit(sample);
                break;
            case 24:
                WriteSample24Bit(sample);
                break;
            case 32:
                WriteSample32Bit(sample);
                break;
            case 64:
                WriteSample64Bit(sample);
                break;
            default:
                throw new NotSupportedException($"Unsupported BitsPerSample: {_bitsPerSample}");
        }
    }

    private void WriteSample8Bit(double value)
    {
        byte b = (byte)((value + 1.0) * byte.MaxValue / 2.0);
        _currentBuffer.Span[_currentBufferSize] = b;
        _currentBufferSize += sizeof(byte);
        _dataSize += sizeof(byte);
    }

    private void WriteSample16Bit(double value)
    {
        short s = (short)(value * short.MaxValue);
        BinaryPrimitives.WriteInt16LittleEndian(_currentBuffer.Span[_currentBufferSize..], s);
        _currentBufferSize += sizeof(short);
        _dataSize += sizeof(short);
    }

    private void WriteSample24Bit(double value)
    {
        // Защита от невалидных входных значений
        if (double.IsNaN(value) || double.IsInfinity(value))
            value = 0.0;

        // Корректный масштаб для 24 бит
        const double max24Bit = 8388607.0; // 2^23 - 1
        double scaled = value * max24Bit;

        // Клиппинг
        if (scaled > max24Bit) scaled = max24Bit;
        else if (scaled < -max24Bit - 1) scaled = -max24Bit - 1;

        int i = (int)scaled;

        // Проверка доступного места (если не гарантировано снаружи)
        if (_currentBufferSize + 3 > _currentBuffer.Span.Length)
            throw new InvalidOperationException("Buffer overflow");

        var span = _currentBuffer.Span[_currentBufferSize..];
        span[0] = (byte)(i & 0xFF);        // младший байт
        span[1] = (byte)((i >> 8) & 0xFF); // средний
        span[2] = (byte)((i >> 16) & 0xFF);// старший (little-endian)
        _currentBufferSize += 3;
        _dataSize += 3;
    }

    private void WriteSample32Bit(double value)
    {
        int i = (int)(value * int.MaxValue);
        BinaryPrimitives.WriteInt32LittleEndian(_currentBuffer.Span[_currentBufferSize..], i);
        _currentBufferSize += sizeof(int);
        _dataSize += sizeof(int);
    }

    private void WriteSample64Bit(double value)
    {
        MemoryMarshal.Write(_currentBuffer.Span[_currentBufferSize..], in value);
        _currentBufferSize += sizeof(double);
        _dataSize += sizeof(double);
    }

    private async Task FlushBufferAsync(CancellationToken cancellationToken)
    {
        if (_currentBufferSize == 0) return;

        await _stream.WriteAsync(_currentBuffer[.._currentBufferSize], cancellationToken).ConfigureAwait(false);
        _currentBufferSize = 0;
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        await FlushBufferAsync(cancellationToken).ConfigureAwait(false);
        CorrectHeader();

        if (!_leaveOpen)
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _bufferOwner.Dispose();
        if (!_leaveOpen)
            await _stream.DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        Close();
        _bufferOwner.Dispose();
        if (!_leaveOpen)
            _stream.Dispose();
    }

    public void Close()
    {
        FlushBuffer();
        CorrectHeader();

        if (!_leaveOpen)
            _stream.Flush();
    }

    private void FlushBuffer()
    {
        if (_currentBufferSize > 0)
        {
            _stream.Write(_currentBuffer.Span[.._currentBufferSize]);
            _currentBufferSize = 0;
        }
    }

    /// <summary>
    /// Обновляет заголовок с реальным размером данных
    /// </summary>
    private void CorrectHeader()
    {
        if (_stream.CanSeek)
        {
            _stream.Position = 4;
            Span<byte> chunkSizeBuffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(chunkSizeBuffer, (uint)(_dataSize + 36));
            _stream.Write(chunkSizeBuffer);

            _stream.Position = 40;
            Span<byte> dataSizeBuffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(dataSizeBuffer, (uint)_dataSize);
            _stream.Write(dataSizeBuffer);
        }
    }

    private static void WriteBytes(Span<byte> buffer, ref int offset, string value)
    {
        foreach (var ch in value)
        {
            buffer[offset++] = (byte)ch;
        }
    }

}
