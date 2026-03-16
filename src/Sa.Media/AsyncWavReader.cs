using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;

namespace Sa.Media;


/// <summary>
/// <seealso href="https://stackoverflow.com/questions/8754111/how-to-read-the-data-in-a-wav-file-to-an-array/34667370#34667370"/>
/// </summary>
public sealed class AsyncWavReader(PipeReader reader)
{
    public static AsyncWavReader Create(Stream stream, StreamPipeReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new ArgumentException("Stream must be readable", nameof(stream));

        var reader = PipeReader.Create(stream, options);

        return new AsyncWavReader(reader);
    }


    private readonly Lock _headerLock = new();

    private Task<WavHeader>? _headerTask;

    public Task<WavHeader> GetHeaderAsync(CancellationToken cancellationToken)
    {
        if (_headerTask is null)
        {
            lock (_headerLock)
            {
                _headerTask ??= WavHeaderReader.ReadHeaderAsync(reader, cancellationToken);
            }
        }
        return _headerTask;
    }


    /// <summary>
    /// Читает сырые сэмплы по каналам с поддержкой обрезки по времени.
    /// </summary>
    /// <param name="allowBufferReuse">
    /// Если true - возвращает ссылку на внутренний буфер (высокая производительность, 
    /// но данные должны быть скопированы до следующего yield return).
    /// Если false - возвращает копию данных (безопасно, но с аллокациями).
    /// </param>
    public async IAsyncEnumerable<AudioPacket> ReadRawChannelSamplesAsync(
        TimeRange? cutRange = null,
        bool allowBufferReuse = true,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var header = await GetHeaderAsync(cancellationToken);
        ValidateHeader(header);

        var (cutFrom, cutTo) = header.CalculateCutOffsets(cutRange ?? new());

        long offsetToSkip = cutFrom - header.DataOffset;

        if (offsetToSkip > 0)
        {
            await reader.SkipAsync(offsetToSkip, cancellationToken);
        }

        int channels = header.NumChannels;
        int blockAlign = header.BlockAlign;
        int sampleSize = header.SampleSize;
        long currentOffset = cutFrom;

        var sampleBuffer = new byte[sampleSize];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ReadResult result = await reader.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> sequence = result.Buffer;

            SequencePosition consumed = sequence.Start;
            bool success = false;
            try
            {
                // Обрабатываем полные блоки
                while (sequence.Length >= blockAlign && currentOffset < cutTo)
                {
                    ReadOnlySequence<byte> block = sequence.Slice(0, blockAlign);

                    bool blockIsEof = (currentOffset + blockAlign >= cutTo) || result.IsCompleted;

                    // Извлекаем сэмплы по каналам
                    for (int channelId = 0; channelId < channels; channelId++)
                    {
                        int offsetInBlock = channelId * sampleSize;

                        block.Slice(offsetInBlock, sampleSize).CopyTo(sampleBuffer);
                        var chunk = allowBufferReuse ? sampleBuffer : [.. sampleBuffer];
                        yield return new(channelId, chunk, currentOffset, blockIsEof);
                    }

                    // Продвигаем позиции
                    currentOffset += blockAlign;
                    sequence = sequence.Slice(blockAlign);
                    consumed = sequence.Start;
                }

                success = true;
            }
            finally
            {
                if (success)
                {
                    reader.AdvanceTo(consumed, result.IsCompleted ? sequence.End : consumed);
                }
                else
                {
                    reader.AdvanceTo(sequence.Start, sequence.End); // Сброс при ошибке
                }
            }

            if (result.IsCompleted || currentOffset >= cutTo)
                yield break;
        }
    }


    /// <summary>
    /// Диапазон нормализованные [-1.0, 1.0],
    /// </summary>
    public async IAsyncEnumerable<AudioNormalizedPacket> ReadNormalizedDoubleSamplesAsync(
        TimeRange? cutRange = null,
        bool allowBufferReuse = true,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var header = await GetHeaderAsync(cancellationToken);

        await foreach (var (channelId, rawSample, offset, isEof) in
            ReadRawChannelSamplesAsync(cutRange, allowBufferReuse, cancellationToken: cancellationToken)
            .WithCancellation(cancellationToken))
        {
            double sample = (header.BitsPerSample, header.AudioFormat) switch
            {
                (8, WaveFormatType.Pcm) => SampleConverter.Convert8BitToDouble(rawSample.Span),
                (16, WaveFormatType.Pcm) => SampleConverter.Convert16BitToDouble(rawSample.Span),
                (24, WaveFormatType.Pcm) => SampleConverter.Convert24BitToDouble(rawSample.Span),
                (32, WaveFormatType.Pcm) => SampleConverter.Convert32BitToDouble(rawSample.Span),
                (32, WaveFormatType.IeeeFloat) => SampleConverter.Convert32BitFloatToDouble(rawSample.Span),
                (64, WaveFormatType.IeeeFloat) => SampleConverter.Convert64BitFloatToDouble(rawSample.Span),
                var (bits, format) => throw new NotSupportedException(
                    $"Unsupported format: {format} ({bits}-bit)")
            };

            yield return new AudioNormalizedPacket(channelId, sample, offset, isEof);
        }

    }

    public async IAsyncEnumerable<AudioPacket> ConvertNormalizedDoubleAsync(
        AudioEncoding targetFormat = AudioEncoding.Pcm16BitSigned,
        TimeRange? cutRange = null,
        bool allowBufferReuse = true,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int bytesPerSample = targetFormat.GetBytesPerSample();

        var buffer = new byte[bytesPerSample];

        await foreach (var (channelId, sample, offset, isEof) in
            ReadNormalizedDoubleSamplesAsync(cutRange, allowBufferReuse, cancellationToken)
            .WithCancellation(cancellationToken))
        {
            ReadOnlyMemory<byte> result = SampleConverter.FromNormalizedDouble(sample, targetFormat, buffer);

            var chunk = allowBufferReuse ? result : result.ToArray();

            yield return new(channelId, chunk, offset, isEof);
        }
    }

    /// <summary>
    /// Для потоковой обработки или воспроизведения
    /// </summary>
    public async IAsyncEnumerable<AudioPacket> ReadStreamableChunksAsync(
        AudioEncoding targetFormat = AudioEncoding.Pcm16BitSigned,
        TimeRange? cutRange = null,
        int bufferSize = 1024,
        bool allowBufferReuse = true,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int bytesPerSample = targetFormat.GetBytesPerSample();

        // Выравниваем до кратного 16,32... bytesPerSample
        int alignedSize = Math.Max(bytesPerSample, (bufferSize / bytesPerSample) * bytesPerSample);

        // Инициализируем буферы по количеству каналов
        var header = await GetHeaderAsync(cancellationToken);
        int channelCount = header.NumChannels;

        IMemoryOwner<byte>[] channelBuffers = new IMemoryOwner<byte>[channelCount];

        var positions = new int[channelCount];

        for (int i = 0; i < channelCount; i++)
        {
            channelBuffers[i] = MemoryPool<byte>.Shared.Rent(alignedSize);
        }

        try
        {
            await foreach (var (channelId, sample, offset, isEof) in ConvertNormalizedDoubleAsync(
                targetFormat,
                cutRange,
                allowBufferReuse,
                cancellationToken: cancellationToken).WithCancellation(cancellationToken))
            {
                // Копируем семплы в соответствующий канал
                var buffer = channelBuffers[channelId].Memory;
                ref int pos = ref positions[channelId];
                int available = alignedSize - pos;
                int copyLength = Math.Min(sample.Length, available);

                sample[..copyLength].CopyTo(buffer[pos..]);
                pos += copyLength;

                // Если порция заполнена или конец файла — отправляем
                if (pos == alignedSize || isEof)
                {
                    var result = buffer[..pos];

                    var chunk = allowBufferReuse ? result : result.ToArray();

                    yield return new(channelId, chunk, offset, isEof);
                    positions[channelId] = 0;
                }
            }

            // Отправляем оставшиеся данные для всех каналов, если они есть
            for (int channelId = 0; channelId < channelCount; channelId++)
            {
                int remaining = positions[channelId];
                if (remaining > 0)
                {
                    var result = channelBuffers[channelId].Memory[..remaining];
                    var chunk = allowBufferReuse ? result : result.ToArray();

                    yield return new AudioPacket(channelId, chunk, (long)remaining, true);
                }
            }
        }
        finally
        {
            foreach (var buffer in channelBuffers)
            {
                buffer?.Dispose();
            }
        }
    }

    private static void ValidateHeader(WavHeader header)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(header.NumChannels, nameof(header.NumChannels));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(header.SampleRate, nameof(header.SampleRate));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(header.BlockAlign, nameof(header.BlockAlign));
        ArgumentOutOfRangeException.ThrowIfNegative(header.DataSize, nameof(header.DataSize));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(header.BitsPerSample, 0, nameof(header.BitsPerSample));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(header.BitsPerSample, 64, nameof(header.BitsPerSample));
    }
}
