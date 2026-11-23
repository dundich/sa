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
    private readonly bool _ownsStream;
    private Task<WavHeader>? _headerTask;
    private int _disposed;

    /// <summary>
    /// Creates a reader over the given pipe reader. The reader is not owned by this instance.
    /// </summary>
    /// <param name="reader">The pipe reader to read WAV data from.</param>
    public AsyncWavReader(PipeReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _ownsReader = false;
    }

    /// <summary>
    /// Creates a reader over the given pipe reader.
    /// </summary>
    /// <param name="reader">The pipe reader to read WAV data from.</param>
    /// <param name="ownsReader">If true, the pipe reader is completed when this instance is disposed.</param>
    public AsyncWavReader(PipeReader reader, bool ownsReader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _ownsReader = ownsReader;
    }

    private AsyncWavReader(PipeReader reader, Stream stream, bool ownsStream)
    {
        _reader = reader;
        _stream = stream;
        _ownsReader = true;
        _ownsStream = ownsStream;
    }

    /// <summary>
    /// Creates a reader over the given stream. The stream is left open when the reader is disposed.
    /// </summary>
    /// <param name="stream">The readable stream containing WAV data.</param>
    /// <param name="options">Optional options for the underlying pipe reader.</param>
    /// <returns>A reader over the stream.</returns>
    public static AsyncWavReader Create(Stream stream, StreamPipeReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new ArgumentException("Stream must be readable", nameof(stream));

        // StreamPipeReaderOptions.LeaveOpen по умолчанию false: CompleteAsync() закрыл бы поток
        // вызывающего. Ридер владеет только своим PipeReader, но не чужим Stream.
        options ??= new StreamPipeReaderOptions(leaveOpen: true);

        var reader = PipeReader.Create(stream, options);

        // Поток не наш, но seekable-поток нужен, чтобы восстановить data size потоковых WAV.
        return new AsyncWavReader(reader, stream, ownsStream: false);
    }

    /// <summary>
    /// Creates a reader over the specified file.
    /// </summary>
    /// <param name="filePath">Path to the WAV file.</param>
    /// <param name="fileOptions">Optional options used to open the file.</param>
    /// <returns>A reader over the file.</returns>
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

        return new AsyncWavReader(reader, stream, ownsStream: true);
    }

    /// <summary>
    /// Reads and caches the WAV header.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the wait for the header.</param>
    /// <returns>The parsed WAV header.</returns>
    public Task<WavHeader> GetHeaderAsync(CancellationToken cancellationToken)
    {
        if (_headerTask is null)
        {
            lock (_headerLock)
            {
                // Заголовок парсится без внешнего токена: отмена одного вызова не должна
                // «отравлять» закэшированную задачу для всех последующих.
                _headerTask ??= ReadHeaderAsync();
            }
        }

        return cancellationToken.CanBeCanceled
            ? _headerTask.WaitAsync(cancellationToken)
            : _headerTask;
    }

    private async Task<WavHeader> ReadHeaderAsync()
    {
        WavHeader header = await WavHeaderReader
            .ReadHeaderAsync(_reader, CancellationToken.None)
            .ConfigureAwait(false);

        // Для потоковых записей (data size == 0xFFFFFFFF) размер берём из длины файла,
        // иначе GetDuration()/обрезка по времени получают фиктивные ~13 часов.
        EnsureDataSize(header);
        return header;
    }

    /// <summary>
    /// Reads PCM batches for a single channel.
    /// </summary>
    /// <remarks>
    /// В отличие от per-sample ридеров, читаем целыми кадрами и помечаем весь
    /// буфер pipe как просмотренный. Поэтому неполный кадр на границе буфера
    /// (например, <c>dataOffset = 78</c> при <c>blockAlign = 4</c>) не может
    /// зациклить <c>PipeReader.ReadAsync()</c>.
    /// </remarks>
    /// <param name="channel">Индекс канала (0-based).</param>
    /// <param name="samplesPerBatch">Число сэмплов на канал в одном чанке.</param>
    /// <param name="format">Целевой формат; PCM16 читается напрямую, без double.</param>
    /// <param name="cancellationToken">Токен отмены перечисления.</param>
    public async IAsyncEnumerable<AudioChunk> ReadChannelChunksAsync(
        int channel,
        int samplesPerBatch,
        AudioEncoding format = AudioEncoding.Pcm16BitSigned,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(samplesPerBatch);

        WavHeader header = await GetHeaderAsync(cancellationToken).ConfigureAwait(false);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(channel, header.NumChannels);

        int frameSize = header.BlockAlign;
        int sampleSize = header.SampleSize;
        int bytesPerSample = format.GetBytesPerSample();

        long dataSize = header.ResolveDataSize();
        long remaining = dataSize <= 0 ? long.MaxValue : dataSize;

        bool direct = format == AudioEncoding.Pcm16BitSigned
            && header.EffectiveAudioFormat == WaveFormatType.Pcm
            && header.BitsPerSample == 16;

        Func<ReadOnlySpan<byte>, double>? normalize = direct ? null : header.GetNormalizedConverter();
        Func<double, Memory<byte>, ReadOnlyMemory<byte>>? convert = direct ? null : SampleConverter.GetConverter(format);

        byte[] output = ArrayPool<byte>.Shared.Rent(samplesPerBatch * bytesPerSample);
        byte[] sample = direct ? [] : ArrayPool<byte>.Shared.Rent(sampleSize);

        try
        {
            long position = 0;
            int samplesInChunk = 0;
            bool eof = false;

            while (!eof)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ReadResult result = await _reader
                    .ReadAtLeastAsync(frameSize, cancellationToken)
                    .ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;
                SequencePosition consumed = buffer.Start;

                try
                {
                    while (buffer.Length >= frameSize
                           && samplesInChunk < samplesPerBatch
                           && remaining > 0)
                    {
                        ReadOnlySequence<byte> frame = buffer.Slice(0, frameSize);
                        int written = samplesInChunk * bytesPerSample;

                        if (direct)
                        {
                            frame.Slice(channel * sampleSize, sizeof(short))
                                .CopyTo(output.AsSpan(written, sizeof(short)));
                        }
                        else
                        {
                            frame.Slice(channel * sampleSize, sampleSize).CopyTo(sample);
                            double value = normalize!(sample.AsSpan(0, sampleSize));
                            convert!(value, output.AsMemory(written, bytesPerSample));
                        }

                        buffer = buffer.Slice(frameSize);
                        consumed = buffer.Start;
                        position += frameSize;
                        if (remaining != long.MaxValue)
                        {
                            remaining -= frameSize;
                        }

                        samplesInChunk++;
                    }

                    eof = remaining <= 0 || (result.IsCompleted && buffer.Length < frameSize);

                    if (samplesInChunk > 0 && (samplesInChunk == samplesPerBatch || eof))
                    {
                        yield return new AudioChunk(
                            output.AsMemory(0, samplesInChunk * bytesPerSample),
                            position - (long)samplesInChunk * frameSize,
                            eof);

                        samplesInChunk = 0;
                    }
                }
                finally
                {
                    _reader.AdvanceTo(consumed, buffer.End);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(output);
            if (!direct)
            {
                ArrayPool<byte>.Shared.Return(sample);
            }
        }
    }

    /// <summary>
    /// Внутренний чанковый ридер для Echo: тоже целыми кадрами, но сразу в
    /// нормализованные float и с обоими каналами в interleaved-порядке.
    /// </summary>
    internal async IAsyncEnumerable<ReadOnlyMemory<float>> ReadInterleavedFloatChunksAsync(
        int framesPerBatch,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesPerBatch);

        WavHeader header = await GetHeaderAsync(cancellationToken).ConfigureAwait(false);
        if (header.NumChannels != 2)
            throw new NotSupportedException($"Expected stereo WAV, found {header.NumChannels} channel(s).");

        int frameSize = header.BlockAlign;
        int sampleSize = header.SampleSize;
        Func<ReadOnlySpan<byte>, float> convert = header.GetNormalizedFloatConverter();

        long dataSize = header.ResolveDataSize();
        long remaining = dataSize <= 0 ? long.MaxValue : dataSize;

        float[] output = ArrayPool<float>.Shared.Rent(framesPerBatch * 2);
        byte[] sample = ArrayPool<byte>.Shared.Rent(sampleSize);

        try
        {
            int framesInChunk = 0;
            bool eof = false;

            while (!eof)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ReadResult result = await _reader
                    .ReadAtLeastAsync(frameSize, cancellationToken)
                    .ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;
                SequencePosition consumed = buffer.Start;

                try
                {
                    while (buffer.Length >= frameSize
                           && framesInChunk < framesPerBatch
                           && remaining > 0)
                    {
                        ReadOnlySequence<byte> frame = buffer.Slice(0, frameSize);

                        for (int channel = 0; channel < 2; channel++)
                        {
                            frame.Slice(channel * sampleSize, sampleSize).CopyTo(sample);
                            output[framesInChunk * 2 + channel] = convert(sample.AsSpan(0, sampleSize));
                        }

                        buffer = buffer.Slice(frameSize);
                        consumed = buffer.Start;
                        if (remaining != long.MaxValue)
                        {
                            remaining -= frameSize;
                        }

                        framesInChunk++;
                    }

                    eof = remaining <= 0 || (result.IsCompleted && buffer.Length < frameSize);

                    if (framesInChunk > 0 && (framesInChunk == framesPerBatch || eof))
                    {
                        yield return output.AsMemory(0, framesInChunk * 2);
                        framesInChunk = 0;
                    }
                }
                finally
                {
                    _reader.AdvanceTo(consumed, buffer.End);
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(output);
            ArrayPool<byte>.Shared.Return(sample);
        }
    }

    private void EnsureDataSize(WavHeader header)
    {
        if (header.HasDataSize || _stream is null || !_stream.CanSeek)
            return;

        long length;
        try
        {
            length = _stream.Length;
        }
        catch (NotSupportedException)
        {
            return;
        }

        long size = length - header.DataOffset;

        // Отрицательное значение — заголовок длиннее файла, > uint.MaxValue — предел RIFF.
        if (size <= 0 || size > uint.MaxValue)
            return;

        header.DataSize = (uint)size;
    }

    /// <summary>
    /// Releases the resources held by the reader.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_ownsReader)
        {
            _reader.Complete();
        }

        if (_ownsStream)
        {
            _stream?.Dispose();
        }
    }

    /// <summary>
    /// Asynchronously releases the resources held by the reader.
    /// </summary>
    /// <returns>A task that completes when the resources have been released.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_ownsReader)
        {
            await _reader.CompleteAsync().ConfigureAwait(false);
        }

        if (_ownsStream && _stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
