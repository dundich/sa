using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;

namespace Sa.Media;

/// <summary>
/// <seealso href="https://stackoverflow.com/questions/8754111/how-to-read-the-data-in-a-wav-file-to-an-array/34667370#34667370"/>
/// </summary>
public sealed class AsyncWavReader : IAsyncDisposable, IDisposable
{
    static class Constants
    {
        public const uint Subchunk1IdJunk = 0x4B4E554A; // "JUNK"
        public const uint DataSubchunkId = 0x61746164; // "data"
        public const uint ListSubchunkId = 0x5453494c; // "LIST"
        public const uint FllrSubchunkId = 0x524c4c46; // "FLLR"
        public const uint СhunkRiff = 0x46464952; // RIFF
        public const uint FormatWave = 0x45564157; //WAVE
    }

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly Lazy<Task<WavHeader>> _header;
    private bool _disposed;

    public AsyncWavReader(Stream stream, bool leaveOpen = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));

        if (!stream.CanRead)
            throw new ArgumentException("Stream must be readable", nameof(stream));

        _leaveOpen = leaveOpen;
        _header = new Lazy<Task<WavHeader>>(() => ReadHeaderAsync(_stream));
    }

    public Task<WavHeader> GetHeaderAsync() => _header.Value;

    public async IAsyncEnumerable<(int channelId, ReadOnlyMemory<byte> sample, bool isEof)> ReadRawChannelSamplesAsync(
        float? cutFromSeconds = null,
        float? cutToSeconds = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(AsyncWavReader));

        var header = await GetHeaderAsync();

        long dataOffset = header.DataOffset;
        long dataEndOffset = dataOffset + header.Subchunk2Size;

        long cutFromOffset = cutFromSeconds.HasValue
            ? dataOffset + (long)(cutFromSeconds.Value * header.SampleRate * header.BlockAlign)
            : dataOffset;

        long cutToOffset = cutToSeconds.HasValue
            ? dataOffset + (long)(cutToSeconds.Value * header.SampleRate * header.BlockAlign)
            : dataEndOffset;

        long offsetToData = cutFromOffset - dataOffset;

        if (offsetToData < 0)
        {
            if (_stream.CanSeek)
                _stream.Position = cutFromOffset;
            else
                ArgumentOutOfRangeException.ThrowIfNegative(offsetToData);
        }
        else if (offsetToData > 0)
        {
            await new Reader(_stream).SkeepBytesAsync(offsetToData, cancellationToken);
        }

        int sampleSize = header.SampleSize;

        Memory<byte> buffer = new byte[sampleSize];

        for (long i = cutFromOffset; i < cutToOffset; i += header.BlockAlign)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool isEof = i + header.BlockAlign >= cutToOffset;

            for (int channelId = 0; channelId < header.NumChannels; channelId++)
            {
                int bytesRead = await _stream.ReadAsync(buffer, cancellationToken);

                if (bytesRead != sampleSize)
                    yield break;

                yield return (channelId, buffer, isEof);
            }
        }

    }

    /// <summary>
    /// Диапазон [-1.0, 1.0],
    /// </summary>
    /// <param name="cutFromSeconds"></param>
    /// <param name="cutToSeconds"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="NotSupportedException"></exception>
    public async IAsyncEnumerable<(int channelId, double sample, bool isEof)> ReadNormalizedDoubleSamplesAsync(
        float? cutFromSeconds = null,
        float? cutToSeconds = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(AsyncWavReader));

        var header = await GetHeaderAsync();

        await foreach (var (channelId, rawSample, isEof) in ReadRawChannelSamplesAsync(cutFromSeconds, cutToSeconds, cancellationToken))
        {
            double result = header.BitsPerSample switch
            {
                8 when header.AudioFormat == WaveFormatType.Pcm =>
                    Converter.Convert8BitToDouble(rawSample.Span),

                16 when header.AudioFormat == WaveFormatType.Pcm =>
                    Converter.Convert16BitToDouble(rawSample.Span),

                24 when header.AudioFormat == WaveFormatType.Pcm =>
                    Converter.Convert24BitToDouble(rawSample.Span),

                32 when header.AudioFormat == WaveFormatType.Pcm =>
                    Converter.Convert32BitToDouble(rawSample.Span),

                32 when header.AudioFormat == WaveFormatType.IeeeFloat =>
                    Converter.Convert32BitFloatToDouble(rawSample.Span),

                64 when header.AudioFormat == WaveFormatType.IeeeFloat =>
                    Converter.Convert64BitFloatToDouble(rawSample.Span),

                _ => throw new NotSupportedException($"Unsupported format: {header.AudioFormat} ({header.BitsPerSample}-bit)")
            };

            yield return (channelId, result, isEof);
        }

    }

    public async IAsyncEnumerable<(int channelId, ReadOnlyMemory<byte> sample, bool isEof)> ReadPcm16BitSamplesAsync(
        float? cutFromSeconds = null,
        float? cutToSeconds = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        WavHeader header = await GetHeaderAsync();
        int sampleSize = header.SampleSize;

        Memory<byte> buffer = new byte[sampleSize];

        await foreach (var (channelId, sample, isEof) in ReadNormalizedDoubleSamplesAsync(cutFromSeconds, cutToSeconds, cancellationToken))
        {
            var span = buffer.Span;
            int index = 0;

            // Little-endian: сначала младший, потом старший
            short s = (short)(sample * 32767.0);
            span[index++] = (byte)(s & 0xFF);     // младший байт
            span[index++] = (byte)(s >> 8);       // старший байт


            yield return (channelId, buffer[..index], isEof);
        }
    }

    /// <summary>
    /// Подходит для потоковой обработки или воспроизведения
    /// </summary>
    /// <param name="bufferSize"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async IAsyncEnumerable<(int channelId, ReadOnlyMemory<byte> samples, bool isEof)> ReadPcm16BitStreamableChunksAsync(
        int bufferSize = 512,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(AsyncWavReader));

        // Выравниваем до кратного 16
        int alignedSize = Math.Max(16, (bufferSize / 16) * 16);

        // Инициализируем буферы по количеству каналов
        var header = await GetHeaderAsync();
        int channelCount = header.NumChannels;

        var buffers = new byte[channelCount][];
        var positions = new int[channelCount];


        for (int i = 0; i < channelCount; i++)
        {
            buffers[i] = ArrayPool<byte>.Shared.Rent(alignedSize);
        }

        try
        {
            await foreach (var (channelId, sample, isEof) in ReadPcm16BitSamplesAsync(cancellationToken: cancellationToken))
            {

                // Копируем семплы в соответствующий канал
                var buffer = buffers[channelId];
                int pos = positions[channelId];

                int copyLength = Math.Min(sample.Length, alignedSize - pos);

                sample[..copyLength].CopyTo(buffer.AsMemory(pos));
                positions[channelId] += copyLength;

                bool shouldYield = positions[channelId] == alignedSize || isEof;

                // Если порция заполнена или конец файла — отправляем
                if (shouldYield && copyLength > 0)
                {
                    yield return (channelId, buffer.AsMemory(0, positions[channelId]), isEof);
                    positions[channelId] = 0;
                }
            }

            // Отправляем оставшиеся данные для всех каналов, если они есть
            for (int channelId = 0; channelId < channelCount; channelId++)
            {
                int remaining = positions[channelId];
                if (remaining > 0)
                {
                    yield return (channelId, buffers[channelId].AsMemory(0, remaining), isEof: true);
                    positions[channelId] = 0;
                }
            }
        }
        finally
        {
            foreach (var buffer in buffers)
            {
                if (buffer != null)
                    ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }


    public void Dispose()
    {
        if (_disposed) return;
        if (!_leaveOpen)
            _stream.Dispose();
        _disposed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (!_leaveOpen)
            await _stream.DisposeAsync();
        _disposed = true;
    }


    public static async Task<WavHeader> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        Reader reader = new(stream);

        uint chunkId = await reader.ReadUInt32Async(cancellationToken);
        uint chunkSize = await reader.ReadUInt32Async(cancellationToken);
        uint format = await reader.ReadUInt32Async(cancellationToken);

        if (chunkId != Constants.СhunkRiff || format != Constants.FormatWave)
        {
            throw new NotSupportedException("ERROR: File is not a WAV file");
        }

        uint subchunk1Id = await reader.ReadUInt32Async(cancellationToken);

        // Skip JUNK chunks
        while (subchunk1Id == Constants.Subchunk1IdJunk)
        {
            //skip JUNK chunks: https://www.daubnet.com/en/file-format-riff
            uint junkSize = await reader.ReadUInt32Async(cancellationToken);
            if (junkSize % 2 == 1) junkSize++; //When writing RIFFs, JUNK chunks should not have odd number as Size.
            await reader.SkeepBytesAsync(junkSize, cancellationToken);
            subchunk1Id = await reader.ReadUInt32Async(cancellationToken);
        }

        uint subchunk1Size = await reader.ReadUInt32Async(cancellationToken);
        ushort audioFormatValue = await reader.ReadUInt16Async(cancellationToken);
        WaveFormatType audioFormat = (WaveFormatType)audioFormatValue;

        if (audioFormat is not (WaveFormatType.Pcm or WaveFormatType.IeeeFloat))
        {
            throw new NotSupportedException($"Unsupported audio format: {audioFormat}");
        }

        ushort numChannels = await reader.ReadUInt16Async(cancellationToken);
        uint sampleRate = await reader.ReadUInt32Async(cancellationToken);
        uint byteRate = await reader.ReadUInt32Async(cancellationToken);
        ushort blockAlign = await reader.ReadUInt16Async(cancellationToken);
        ushort bitsPerSample = await reader.ReadUInt16Async(cancellationToken);

        // Если это формат с расширением (например WAVE_FORMAT_EXTENSIBLE), пропустим дополнительные данные
        if (subchunk1Size > 16)
        {
            ushort fmtExtraSize = await reader.ReadUInt16Async(cancellationToken);
            await reader.SkeepBytesAsync(fmtExtraSize, cancellationToken);
        }

        // Find data subchunk
        uint subchunk2Id;
        uint subchunk2Size;

        while (true)
        {
            subchunk2Id = await reader.ReadUInt32Async(cancellationToken);
            subchunk2Size = await reader.ReadUInt32Async(cancellationToken);

            // just skip LIST subchunk
            // just skip FLLR subchunk https://stackoverflow.com/questions/6284651/avaudiorecorder-doesnt-write-out-proper-wav-file-header
            if (subchunk2Id == Constants.ListSubchunkId ||
                subchunk2Id == Constants.FllrSubchunkId)
            {
                await reader.SkeepBytesAsync(subchunk2Size, cancellationToken);
                continue;
            }

            if (subchunk2Id != Constants.DataSubchunkId)
            {
                throw new NotSupportedException($"Unsupported subchunk type: 0x{subchunk2Id:x8}");
            }
            break;
        }

        if (subchunk2Size == 0x7FFFFFFF) //25344
        {
            long remainingBytes = stream.Length - stream.Position;
            if (remainingBytes > int.MaxValue)
            {
                throw new NotSupportedException("File is too large");
            }
            subchunk2Size = (uint)remainingBytes;
        }

        long dataOffset = stream.CanSeek ? stream.Position : reader.Position;

        var header = new WavHeader
        {
            ChunkId = chunkId,
            ChunkSize = chunkSize,
            Format = format,
            Subchunk1Id = subchunk1Id,
            Subchunk1Size = subchunk1Size,
            AudioFormat = audioFormat,
            NumChannels = numChannels,
            SampleRate = sampleRate,
            ByteRate = byteRate,
            BlockAlign = blockAlign,
            BitsPerSample = bitsPerSample,
            Subchunk2Id = subchunk2Id,
            Subchunk2Size = subchunk2Size,
            DataOffset = dataOffset
        };

        header.Validate();

        return header;
    }

    /// <summary>
    /// Поддерживает чтение заголовков WAV из потока без возможности Seek (например, HTTP или WASM).
    /// </summary>
    public static async Task<WavHeader> ReadHeaderAsync(PipeReader reader, CancellationToken cancellationToken = default)
    {
        Stream pipeStream = reader.AsStream();
        return await ReadHeaderAsync(pipeStream, cancellationToken);
    }
}
