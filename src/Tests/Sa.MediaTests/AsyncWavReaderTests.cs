using Sa.Media;
using System.IO.Pipelines;


namespace Sa.MediaTests;

public class AsyncWavReaderTests
{
    private const string FILE = "data/12345.wav";


    [Theory]
    [InlineData("./data/pсmS16Le.wav")]
    [InlineData("./data/12345.wav")]
    public async Task ReadHeaderAsync_ValidWavFile_ReturnsValidHeader(string filePath)
    {
        using var reader = AsyncWavReader.CreateFromFile(filePath);

        var header = await reader.GetHeaderAsync(TestContext.Current.CancellationToken);

        Assert.True(header.SampleRate > 0);
        Assert.InRange(header.BitsPerSample, 8, 64);
        Assert.InRange(header.NumChannels, (ushort)1, (ushort)8);
    }


    [Theory]
    [InlineData("./data/ffout.wav")]
    [InlineData("./data/pсmS16Le.wav")]
    [InlineData("./data/12345.wav")]
    public async Task GetLengthSecondsAsync_ValidWav_ReturnsCorrectDuration(string filePath)
    {
        using var reader = AsyncWavReader.CreateFromFile(filePath);

        var h = await reader.GetHeaderAsync(TestContext.Current.CancellationToken);

        double lengthInSeconds = h.GetDurationInSeconds();

        Assert.True(lengthInSeconds > 0);

        if (!h.HasDataSize)
        {
            lengthInSeconds = h.GetDurationInSeconds(new FileInfo(filePath).Length);
            Assert.True(lengthInSeconds > 0);
        }
    }


    [Fact]
    public async Task ReadRawChannelSamplesAsync_ValidWavFile_YieldsNonEmptyData()
    {
        using var reader = AsyncWavReader.CreateFromFile(FILE);

        await foreach (var (_, sample, _, _) in
            reader.ReadSamplesPerChannelAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.True(sample.Length > 0);
        }

        Assert.True(true);
    }


    [Fact]
    public async Task ReadNormalizedDoubleSamplesAsync_ValidWavFile_YieldsInRangeValues()
    {
        using var reader = AsyncWavReader.CreateFromFile(FILE);

        await foreach (var (_, sample, _, _) in
            reader.ReadDoubleSamplesAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.InRange(sample, -1.0, 1.0);
            return;
        }
    }

    [Fact]
    public async Task ReadStreamableChunksAsync_ValidWavFile_YieldsChunks()
    {
        using var reader = AsyncWavReader.CreateFromFile(FILE);

        await foreach (var (_, samples, _, _) in
            reader.ReadStreamableChunksAsync(samplesPerBatch: 1024, cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.True(samples.Length > 0);
            return;
        }
    }


    [Fact]
    public async Task OpenWavFile_MultipleProcesses_NoException()
    {

        using var reader1 = AsyncWavReader.CreateFromFile(FILE);
        using var reader2 = AsyncWavReader.CreateFromFile(FILE);

        await Task.WhenAll(
            reader1.GetHeaderAsync(TestContext.Current.CancellationToken)
            , reader2.GetHeaderAsync(TestContext.Current.CancellationToken));

        Assert.NotNull(reader1);
        Assert.NotNull(reader2);
    }


    [Fact]
    public async Task ReadHeader_ValidMockWav_ReturnsCorrectHeader()
    {
        var mockStream = MockWavGenerator.CreateTestPcm16Wav();
        var reader = new AsyncWavReader(mockStream, true);

        var header = await reader.GetHeaderAsync(TestContext.Current.CancellationToken);

        Assert.Equal<uint>(0x46464952, header.ChunkId); // "RIFF"
        Assert.Equal<uint>(0x45564157, header.Format);   // "WAVE"
        Assert.Equal(1u, header.NumChannels);
        Assert.Equal(44100u, header.SampleRate);
        Assert.Equal(16, header.BitsPerSample);
        Assert.Equal(WaveFormatType.Pcm, header.AudioFormat);
    }

    [Fact]
    public async Task ReadNormalizedDoubleSamples_ValidMockWav_YieldsInRangeValues()
    {
        var mockStream = MockWavGenerator.CreateTestPcm16Wav(seconds: 1);
        var reader = new AsyncWavReader(mockStream, true);

        await foreach (var (_, samples, _, _)
            in reader.ReadDoubleSamplesAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.InRange(samples, -1.0, 1.0);
        }
    }

    [Fact]
    public async Task ReadStreamableChunks_ValidMockWav_YieldsChunks()
    {
        var mockStream = MockWavGenerator.CreateTestPcm16Wav(seconds: 1);
        var reader = new AsyncWavReader(mockStream, true);

        int chunks = 0;
        await foreach (var (_, samples, _, _)
            in reader.ReadStreamableChunksAsync(samplesPerBatch: 1024, cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.True(samples.Length > 0);
            chunks++;
        }

        Assert.True(chunks > 0);
    }


    [Fact]
    public async Task ReadStreamableChunksAsync_ValidWav_YieldsChunks()
    {
        var pipe = MockWavGenerator.CreateTestPcm16Wav(seconds: 2);
        var reader = new AsyncWavReader(pipe, true);

        int chunksCount = 0;
        await foreach (var (channelId, samples, _, _)
            in reader.ReadStreamableChunksAsync(samplesPerBatch: 512, cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.InRange(channelId, 0, 1);
            Assert.True(samples.Length > 0);
            chunksCount++;
        }

        Assert.True(chunksCount > 0);
    }

    [Fact]
    public async Task ReadRawChannelSamplesAsync_ValidStereo_ReturnsTwoChannels()
    {
        var pipe = MockWavGenerator.CreateTestPcm16Wav(numChannels: 2);
        var reader = new AsyncWavReader(pipe, true);

        List<int> channelIds = [];

        await foreach (var (channelId, _, _, _) in reader
            .ReadSamplesPerChannelAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            if (!channelIds.Contains(channelId)) channelIds.Add(channelId);
        }

        Assert.Equal(2, channelIds.Count);
    }


    [Fact]
    public async Task ReadNormalizedDoubleSamplesAsync_WithCut_ReturnsTrimmedData()
    {
        const int seconds = 5;
        var pipe = MockWavGenerator.CreateTestPcm16Wav(seconds: seconds);
        var reader = new AsyncWavReader(pipe, true);

        float cutFrom = 1.0f;
        float cutTo = 4.0f;

        int count = 0;
        bool eof = false;

        await foreach (var (_, _, _, isEof) in reader.ReadDoubleSamplesAsync(
                TimeRange.Seconds(cutFrom, cutTo),
                cancellationToken: TestContext.Current.CancellationToken))
        {
            count++;
            eof = isEof;
        }

        // Assert.InRange(count, 44100 * 2, 44100 * 3); // ~ от 1 до 4 секунды
        Assert.True(eof);
    }


    [Fact]
    public async Task ReadRawChannelSamplesAsync_ShouldSetIsEofAtEndOfFile()
    {
        using var reader = AsyncWavReader.CreateFromFile(FILE);

        bool eof = false;
        await foreach (var (_, _, _, isEof) in reader
            .ReadSamplesPerChannelAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            eof = isEof;
        }

        Assert.True(eof);
    }


    [Fact]
    public async Task ConvertNormalizedDoubleAsync_ShouldConvertBackToPCM16()
    {
        using var reader = AsyncWavReader.CreateFromFile(FILE);

        await foreach (var (_, sample, _, _) in reader.ConvertToFormatAsync(
            AudioEncoding.Pcm16BitSigned,
            cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.Equal(2, sample.Length); // 16-bit PCM
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Регрессии: обрезка по времени не должна крутить PipeReader вхолостую
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Обрезка на середине файла должна завершиться сразу после cutTo и не читать хвост.
    /// Раньше после cutTo ридер крутился в ReadAsync над одним и тем же непотреблённым
    /// буфером (100% CPU на больших файлах, вечный цикл на live-каналах) до отмены токена.
    /// Считаем вызовы ReadAsync, чтобы детерминированно поймать кручение.
    /// </summary>
    [Fact]
    public async Task ReadSamplesPerChannelAsync_TrimMidFile_DoesNotSpinOnTail()
    {
        // 30 секунд стерео > размера внутреннего буфера StreamPipeReader (64 KiB),
        // хвост данных остаётся в потоке, когда cutTo уже достигнут.
        var pipe = new CountingPipeReader(MockWavGenerator.CreateTestPcm16Wav(numChannels: 2, seconds: 30));
        var reader = new AsyncWavReader(pipe, true);

        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int frameCount = 0;
        bool eofSeen = false;

        await foreach (var (_, _, _, isEof) in reader.ReadSamplesPerChannelAsync(
            TimeRange.Seconds(1, 2),
            cancellationToken: watchdog.Token))
        {
            frameCount++;
            eofSeen |= isEof;
        }

        Assert.True(eofSeen, "Обрезка должна завершиться пакетом с IsEof=true");
        // ~1 секунда полезных сэмплов (44100 фреймов/с * 2 канала), с допуском на границы кадров
        Assert.InRange(frameCount, 44100 * 2 - 8, 44100 * 2 + 8);
        // Голова (заголовок + skip) + данные + хвост: десятки обращений.
        // При кручении было бы > 1000 (а фактически миллионы до срабатывания watchdog).
        Assert.True(pipe.ReadAsyncCount < 200,
            $"Reader крутился впустую после cutTo: {pipe.ReadAsyncCount} вызовов ReadAsync");
    }

    [Fact]
    public async Task ReadStreamableChunksAsync_TrimMidFile_DoesNotSpinOnTail()
    {
        var pipe = new CountingPipeReader(MockWavGenerator.CreateTestPcm16Wav(numChannels: 2, seconds: 30));
        var reader = new AsyncWavReader(pipe, true);

        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int chunks = 0;

        await foreach (var _ in reader.ReadStreamableChunksAsync(
            samplesPerBatch: 4096,
            cutRange: TimeRange.Seconds(1, 2),
            cancellationToken: watchdog.Token))
        {
            chunks++;
        }

        Assert.True(chunks > 0);
        Assert.True(pipe.ReadAsyncCount < 200,
            $"Reader крутился впустую после cutTo: {pipe.ReadAsyncCount} вызовов ReadAsync");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Регрессии: WAVE_FORMAT_EXTENSIBLE должен читаться как обычный PCM/float
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadHeader_ExtensiblePcmWav_ReturnsPcmHeaderAndSamples()
    {
        using var ms = MockWavGenerator.CreateTestExtensiblePcm16Wav(numChannels: 2, seconds: 1);
        var reader = new AsyncWavReader(PipeReader.Create(ms), ownsReader: true);

        var header = await reader.GetHeaderAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WaveFormatType.Extensible, header.AudioFormat);
        Assert.True(header.IsExtensible);
        Assert.True(header.IsPcm, "Extensible PCM должен распознаваться как PCM");
        Assert.Equal(2, header.NumChannels);
        Assert.Equal(16, header.BitsPerSample);
        Assert.Equal(WavHeader.PcmSubtypeGuid, header.SubFormatGuid);

        int samples = 0;
        await foreach (var (_, value, _, _) in reader
            .ReadDoubleSamplesAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.InRange(value, -1.0, 1.0);
            samples++;
        }

        Assert.Equal(44100 * 2, samples); // 1 сек * 2 канала
    }

    // ─────────────────────────────────────────────────────────────────────
    // Регрессии: фабрики TimeRange
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void TimeRangeSeconds_FiniteTo_IsNotClampedToMaxValue()
    {
        var range = TimeRange.Seconds(5, 15);

        Assert.Equal(15, range.To.TotalSeconds, 5);
        Assert.NotEqual(TimeSpan.MaxValue, range.To);
    }

    [Fact]
    public void TimeRangeSeconds_OpenEnd_IsMaxValue()
    {
        Assert.Equal(TimeSpan.MaxValue, TimeRange.Seconds(5).To);
    }
}



/// <summary>
/// PipeReader, считающий обращения к ReadAsync/ReadAtLeastAsync —
/// чтобы детектировать холостое кручение чтения после достижения cutTo.
/// </summary>
public sealed class CountingPipeReader(PipeReader inner) : PipeReader
{
    public int ReadAsyncCount;

    public override void AdvanceTo(SequencePosition consumed) => inner.AdvanceTo(consumed);

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => inner.AdvanceTo(consumed, examined);

    public override void CancelPendingRead() => inner.CancelPendingRead();

    public override void Complete(Exception? exception = null) => inner.Complete(exception);

    public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        ReadAsyncCount++;
        return inner.ReadAsync(cancellationToken);
    }

    public override bool TryRead(out ReadResult result) => inner.TryRead(out result);
}


public static class MockWavGenerator
{
    /// <summary>
    /// Генерирует PCM WAV в памяти (16 бит, 44100 Гц)
    /// </summary>
    public static PipeReader CreateTestPcm16Wav(int sampleRate = 44100, int numChannels = 1, int seconds = 1)
    {
        int bitsPerSample = 16;
        int bytesPerSample = bitsPerSample / 8;
        int totalSamples = sampleRate * seconds;
        int totalDataSize = totalSamples * numChannels * bytesPerSample;

        var ms = new MemoryStream();
        var writer = new BinaryWriter(ms);

        // RIFF header
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + totalDataSize); // chunkSize
        writer.Write("WAVE"u8.ToArray());

        // fmt subchunk
        writer.Write("fmt "u8.ToArray());
        writer.Write(16); // subchunk1Size
        writer.Write((ushort)1); // audioFormat: PCM
        writer.Write((ushort)numChannels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * numChannels * bytesPerSample); // byteRate
        writer.Write((ushort)(numChannels * bytesPerSample)); // blockAlign
        writer.Write((ushort)bitsPerSample);

        // data subchunk
        writer.Write("data"u8.ToArray());
        writer.Write(totalDataSize);

        // Записываем тон 440 Гц
        for (int i = 0; i < totalSamples; i++)
        {
            short value = (short)(short.MaxValue * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
            writer.Write(value);
            if (numChannels == 2)
                writer.Write(value); // правый канал такой же
        }

        ms.Position = 0;

        return PipeReader.Create(ms);
    }

    /// <summary>
    /// Генерирует WAVE_FORMAT_EXTENSIBLE PCM-файл в памяти (16 бит, 44100 Гц).
    /// </summary>
    public static MemoryStream CreateTestExtensiblePcm16Wav(int sampleRate = 44100, int numChannels = 2, int seconds = 1)
    {
        int bitsPerSample = 16;
        int bytesPerSample = bitsPerSample / 8;
        int totalSamples = sampleRate * seconds;
        int totalDataSize = totalSamples * numChannels * bytesPerSample;

        var ms = new MemoryStream();
        var writer = new BinaryWriter(ms);

        // RIFF header
        writer.Write("RIFF"u8.ToArray());
        writer.Write(56 + totalDataSize); // chunkSize (без 8 байт RIFF-заголовка)
        writer.Write("WAVE"u8.ToArray());

        // fmt subchunk (WAVE_FORMAT_EXTENSIBLE, 40 байт)
        writer.Write("fmt "u8.ToArray());
        writer.Write(40); // subchunk1Size
        writer.Write((ushort)WaveFormatType.Extensible); // audioFormat = 0xFFFE
        writer.Write((ushort)numChannels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * numChannels * bytesPerSample); // byteRate
        writer.Write((ushort)(numChannels * bytesPerSample)); // blockAlign
        writer.Write((ushort)bitsPerSample);
        writer.Write((ushort)22); // cbSize
        writer.Write((ushort)bitsPerSample); // wValidBitsPerSample
        writer.Write(0x3); // dwChannelMask: FL|FR
        writer.Write(WavHeader.PcmSubtypeGuid.ToByteArray()); // SubFormat: KSDATAFORMAT_SUBTYPE_PCM

        // data subchunk
        writer.Write("data"u8.ToArray());
        writer.Write(totalDataSize);

        for (int i = 0; i < totalSamples; i++)
        {
            short value = (short)(short.MaxValue * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
            for (int ch = 0; ch < numChannels; ch++)
                writer.Write(value);
        }

        ms.Position = 0;
        return ms;
    }
}
