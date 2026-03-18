using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;

namespace Sa.Media;


/// <summary>
/// Async WAV file reader for .NET
/// </summary>
public sealed class AsyncWavReader : IDisposable, IAsyncDisposable
{
    private readonly Lock _headerLock = new();
    private readonly PipeReader _reader;
    private readonly Stream? _stream;
    private readonly bool _ownsReader;
    private Task<WavHeader>? _headerTask;
    private bool _disposed;

    public AsyncWavReader(PipeReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _ownsReader = false;
    }

    public AsyncWavReader(PipeReader reader, bool ownsReader)
    {
        _reader = reader;
        _ownsReader = ownsReader;
    }

    private AsyncWavReader(PipeReader reader, Stream stream)
    {
        _reader = reader;
        _stream = stream;
        _ownsReader = true;
    }

    public static AsyncWavReader Create(Stream stream, StreamPipeReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new ArgumentException("Stream must be readable", nameof(stream));

        var reader = PipeReader.Create(stream, options);

        return new AsyncWavReader(reader, ownsReader: true);
    }

    public static AsyncWavReader CreateFromFile(string filePath, FileStreamOptions? fileOptions = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        fileOptions ??= new FileStreamOptions
        {
            Access = FileAccess.Read,
            Mode = FileMode.Open,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        };

        var stream = new FileStream(filePath, fileOptions);

        var reader = PipeReader.Create(stream);

        return new AsyncWavReader(reader, stream);
    }

    public Task<WavHeader> GetHeaderAsync(CancellationToken cancellationToken)
    {
        if (_headerTask is null)
        {
            lock (_headerLock)
            {
                _headerTask ??= WavHeaderReader.ReadHeaderAsync(_reader, cancellationToken);
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
    public async IAsyncEnumerable<AudioPacket> ReadSamplesPerChannelAsync(
        TimeRange? cutRange = null,
        bool allowBufferReuse = true,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var header = await GetHeaderAsync(cancellationToken);
        ValidateHeader(header);

        var (cutFrom, cutTo) = header.CalculateCutOffsets(cutRange ?? TimeRange.Default);

        long offsetToSkip = cutFrom - header.DataOffset;

        if (offsetToSkip > 0)
        {
            await _reader.SkipAsync(offsetToSkip, cancellationToken);
        }

        int channels = header.NumChannels;
        int blockAlign = header.BlockAlign;
        int sampleSize = header.SampleSize;
        long currentOffset = cutFrom;

        var sampleBuffer = new byte[sampleSize];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ReadResult result = await _reader.ReadAsync(cancellationToken);
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
                    _reader.AdvanceTo(consumed, result.IsCompleted ? sequence.End : consumed);
                }
                else
                {
                    _reader.AdvanceTo(sequence.Start, sequence.End); // Сброс при ошибке
                }
            }

            if (result.IsCompleted || currentOffset >= cutTo)
                yield break;
        }
    }


    /// <summary>
    /// Читает нормализованные double-сэмплы [-1.0, 1.0],
    /// </summary>
    public async IAsyncEnumerable<AudioNormalizedPacket> ReadDoubleSamplesAsync(
        TimeRange? cutRange = null,
        bool allowBufferReuse = true,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var convert = await GetNormalizedConverterAsync(cancellationToken);

        await foreach (var (channelId, rawSample, offset, isEof) in
            ReadSamplesPerChannelAsync(cutRange, allowBufferReuse, cancellationToken: cancellationToken)
            .WithCancellation(cancellationToken))
        {
            double sample = convert(rawSample.Span);
            yield return new AudioNormalizedPacket(channelId, sample, offset, isEof);
        }
    }

    /// <summary>
    /// Конвертирует в целевой аудиоформат
    /// </summary>
    public async IAsyncEnumerable<AudioPacket> ConvertToFormatAsync(
        AudioEncoding targetFormat = AudioEncoding.Pcm16BitSigned,
        TimeRange? cutRange = null,
        bool allowBufferReuse = true,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int bytesPerSample = targetFormat.GetBytesPerSample();

        var buffer = new byte[bytesPerSample];

        var convert = SampleConverter.GetConverter(targetFormat);

        await foreach (var (channelId, sample, offset, isEof) in
            ReadDoubleSamplesAsync(cutRange, allowBufferReuse, cancellationToken)
            .WithCancellation(cancellationToken))
        {
            ReadOnlyMemory<byte> result = convert(sample, buffer);
            // При true все пакеты используют ОДИН внутренний буфер!
            var chunk = allowBufferReuse ? result : result.ToArray();
            yield return new(channelId, chunk, offset, isEof);
        }
    }

    /// <summary>
    /// Читает пакеты сэмплов по каналам
    /// </summary>
    /// <remarks>
    /// Для потоковой обработки или воспроизведения
    /// </remarks>
    public async IAsyncEnumerable<AudioPacket> ReadStreamableChunksAsync(
        AudioEncoding targetFormat = AudioEncoding.Pcm16BitSigned,
        TimeRange? cutRange = null,
        int samplesPerBatch = 1024,
        bool allowBufferReuse = true,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int bytesPerSample = targetFormat.GetBytesPerSample();

        // Выравниваем до кратного 16,32... bytesPerSample
        int alignedSize = Math.Max(bytesPerSample, (samplesPerBatch / bytesPerSample) * bytesPerSample);

        // Инициализируем буферы по количеству каналов
        var header = await GetHeaderAsync(cancellationToken);
        int channelCount = header.NumChannels;

        var channelBuffers = new IMemoryOwner<byte>[channelCount];

        try
        {
            var positions = new int[channelCount];

            for (int i = 0; i < channelCount; i++)
            {
                channelBuffers[i] = MemoryPool<byte>.Shared.Rent(alignedSize);
            }

            long lastOffset = 0;

            await foreach (var (channelId, sample, position, isEof) in ConvertToFormatAsync(
                targetFormat,
                cutRange,
                allowBufferReuse,
                cancellationToken: cancellationToken).WithCancellation(cancellationToken))
            {
                lastOffset = position;

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

                    yield return new(channelId, chunk, position, isEof);
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

                    yield return new AudioPacket(channelId, chunk, lastOffset, true);
                }
            }
        }
        finally
        {
            foreach (IMemoryOwner<byte> buffer in channelBuffers)
            {
                buffer?.Dispose();
            }
        }
    }

    private async Task<Func<ReadOnlySpan<byte>, double>> GetNormalizedConverterAsync(CancellationToken cancellationToken)
    {
        var header = await GetHeaderAsync(cancellationToken);
        return header.GetNormalizedConverter();
    }


    private void ValidateHeader(WavHeader header)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(header.NumChannels, nameof(header.NumChannels));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(header.SampleRate, nameof(header.SampleRate));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(header.BlockAlign, nameof(header.BlockAlign));
        ArgumentOutOfRangeException.ThrowIfNegative(header.DataSize, nameof(header.DataSize));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(header.BitsPerSample, 0, nameof(header.BitsPerSample));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(header.BitsPerSample, 64, nameof(header.BitsPerSample));


        if (!header.HasDataSize)
        {
            // restore datasize
            if (_stream != null && _stream.CanSeek && _stream.Length > 0)
            {
                header.DataSize = (uint)(_stream.Length - header.DataOffset);
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_ownsReader)
            {
                _reader.Complete();
            }
            _stream?.Dispose();
            _disposed = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            if (_ownsReader)
            {
                await _reader.CompleteAsync();
            }

            if (_stream != null)
            {
                await _stream.DisposeAsync();
            }
            _disposed = true;
        }
    }
}
