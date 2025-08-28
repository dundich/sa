using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;

namespace Sa.Media;

/// <summary>
/// <seealso href="https://stackoverflow.com/questions/8754111/how-to-read-the-data-in-a-wav-file-to-an-array/34667370#34667370"/>
/// </summary>
public class AsyncWavReader(PipeReader reader)
{

    private readonly Lazy<Task<WavHeader>> _header = new(WavHeaderReader.ReadHeaderAsync(reader));

    public Task<WavHeader> GetHeaderAsync() => _header.Value;

    public async IAsyncEnumerable<(int channelId, ReadOnlyMemory<byte> sample, bool isEof)> ReadRawChannelSamplesAsync(
        float? cutFromSeconds = null,
        float? cutToSeconds = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var header = await GetHeaderAsync();

        long dataOffset = header.DataOffset;
        long dataEndOffset = dataOffset + header.DataSize;

        long cutFromOffset = cutFromSeconds.HasValue
            ? dataOffset + (long)(cutFromSeconds.Value * header.SampleRate * header.BlockAlign)
            : dataOffset;

        long cutToOffset = cutToSeconds.HasValue
            ? dataOffset + (long)(cutToSeconds.Value * header.SampleRate * header.BlockAlign)
            : dataEndOffset;

        long offsetToData = cutFromOffset - dataOffset;

        if (offsetToData > 0)
            await reader.SkipAsync(offsetToData, cancellationToken);

        int channels = header.NumChannels;
        int blockAlign = header.BlockAlign;
        int sampleSize = header.SampleSize;

        var sampleBuffer = new byte[sampleSize];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Читаем как можно больше
            ReadResult result = await reader.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (buffer.IsEmpty)
            {
                if (result.IsCompleted)
                    yield break;
                continue;
            }

            // Пока есть достаточно данных для одного блока (на все каналы)
            while (buffer.Length >= blockAlign)
            {
                // Получаем один блок (на все каналы)
                ReadOnlySequence<byte> block = buffer.Slice(0, blockAlign);


                // Разбираем его на каналы
                for (int channelId = 0; channelId < channels; channelId++)
                {
                    int offsetInBlock = channelId * sampleSize;

                    bool isEof = (cutFromOffset + blockAlign >= cutToOffset) || result.IsCompleted;

                    block.Slice(offsetInBlock, sampleSize).CopyTo(sampleBuffer);

                    yield return (channelId, sampleBuffer, isEof);
                }

                // смещаемся по буферу
                buffer = buffer.Slice(blockAlign);

                cutFromOffset += blockAlign;

                if (cutFromOffset >= cutToOffset)
                {
                    // Сообщаем PipeReader, что мы прочитали всё возможное
                    reader.AdvanceTo(buffer.Start);
                    yield break;
                }
            }

            // Сообщаем PipeReader, что мы прочитали всё возможное
            reader.AdvanceTo(buffer.Start);

            if (result.IsCompleted)
                break;
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
        var header = await GetHeaderAsync();

        await foreach (var (channelId, rawSample, isEof) in ReadRawChannelSamplesAsync(cutFromSeconds, cutToSeconds, cancellationToken))
        {
            double result = header.BitsPerSample switch
            {
                8 when header.AudioFormat == WaveFormatType.Pcm =>
                    SampleConverter.Convert8BitToDouble(rawSample.Span),

                16 when header.AudioFormat == WaveFormatType.Pcm =>
                    SampleConverter.Convert16BitToDouble(rawSample.Span),

                24 when header.AudioFormat == WaveFormatType.Pcm =>
                    SampleConverter.Convert24BitToDouble(rawSample.Span),

                32 when header.AudioFormat == WaveFormatType.Pcm =>
                    SampleConverter.Convert32BitToDouble(rawSample.Span),

                32 when header.AudioFormat == WaveFormatType.IeeeFloat =>
                    SampleConverter.Convert32BitFloatToDouble(rawSample.Span),

                64 when header.AudioFormat == WaveFormatType.IeeeFloat =>
                    SampleConverter.Convert64BitFloatToDouble(rawSample.Span),

                _ => throw new NotSupportedException($"Unsupported format: {header.AudioFormat} ({header.BitsPerSample}-bit)")
            };

            yield return (channelId, result, isEof);
        }

    }

    public async IAsyncEnumerable<(int channelId, ReadOnlyMemory<byte> sample, bool isEof)> ConvertNormalizedDoubleAsync(
        AudioEncoding targetFormat = AudioEncoding.Pcm16BitSigned,
        float? cutFromSeconds = null,
        float? cutToSeconds = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int bytesPerSample = targetFormat.GetBytesPerSample();

        using var owner = MemoryPool<byte>.Shared.Rent(bytesPerSample);
        Memory<byte> buffer = owner.Memory[..bytesPerSample];

        await foreach (var (channelId, sample, isEof) in ReadNormalizedDoubleSamplesAsync(cutFromSeconds, cutToSeconds, cancellationToken))
        {
            ReadOnlyMemory<byte> result = SampleConverter.FromNormalizedDouble(sample, targetFormat, buffer);
            yield return (channelId, result, isEof);
        }
    }

    /// <summary>
    /// Подходит для потоковой обработки или воспроизведения
    /// </summary>
    /// <param name="bufferSize"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async IAsyncEnumerable<(int channelId, ReadOnlyMemory<byte> samples, bool isEof)> ReadStreamableChunksAsync(
        AudioEncoding targetFormat = AudioEncoding.Pcm16BitSigned,
        float? cutFromSeconds = null,
        float? cutToSeconds = null,
        int bufferSize = 1024,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int bytesPerSample = targetFormat.GetBytesPerSample();

        // Выравниваем до кратного 16,32... bytesPerSample
        int alignedSize = Math.Max(bytesPerSample, (bufferSize / bytesPerSample) * bytesPerSample);

        // Инициализируем буферы по количеству каналов
        var header = await GetHeaderAsync();
        int channelCount = header.NumChannels;

        IMemoryOwner<byte>[] buffers = new IMemoryOwner<byte>[channelCount];
        var positions = new int[channelCount];

        for (int i = 0; i < channelCount; i++)
        {
            buffers[i] = MemoryPool<byte>.Shared.Rent(alignedSize);
        }

        try
        {
            await foreach (var (channelId, sample, isEof) in ConvertNormalizedDoubleAsync(targetFormat, cutFromSeconds, cutToSeconds, cancellationToken: cancellationToken))
            {
                // Копируем семплы в соответствующий канал
                var buffer = buffers[channelId].Memory;
                int pos = positions[channelId];

                int copyLength = Math.Min(sample.Length, alignedSize - pos);

                sample[..copyLength].CopyTo(buffer[pos..]);
                positions[channelId] += copyLength;

                // Если порция заполнена или конец файла — отправляем
                if (positions[channelId] == alignedSize || isEof)
                {
                    yield return (channelId, buffer[..positions[channelId]], isEof);
                    positions[channelId] = 0;
                }
            }

            // Отправляем оставшиеся данные для всех каналов, если они есть
            for (int channelId = 0; channelId < channelCount; channelId++)
            {
                int remaining = positions[channelId];
                if (remaining > 0)
                {
                    yield return (channelId, buffers[channelId].Memory[..remaining], isEof: true);
                }
            }
        }
        finally
        {
            foreach (var buffer in buffers)
            {
                buffer?.Dispose();
            }
        }
    }
}
