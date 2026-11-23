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


    // TC-2.1 | F-11
    [Fact]
    public async Task ReadChannelChunksAsync_ValidWavFile_YieldsNonEmptyData()
    {
        using var reader = AsyncWavReader.CreateFromFile(FILE);

        int chunks = 0;
        await foreach (AudioChunk chunk in reader
            .ReadChannelChunksAsync(0, 1024, cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.True(chunk.Samples.Length > 0);
            chunks++;
        }

        Assert.True(chunks > 0);
    }

    // TC-2.2 | F-11
    [Fact]
    public async Task ReadChannelChunksAsync_ValidWavFile_YieldsChunks()
    {
        using var reader = AsyncWavReader.CreateFromFile(FILE);

        await foreach (AudioChunk chunk in reader
            .ReadChannelChunksAsync(0, 1024, cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.True(chunk.Samples.Length > 0);
            return;
        }

        Assert.Fail("No chunks produced");
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

    // TC-2.3 | F-11
    [Fact]
    public async Task ReadChannelChunksAsync_ValidMockWav_YieldsChunks()
    {
        var mockStream = MockWavGenerator.CreateTestPcm16Wav(seconds: 1);
        var reader = new AsyncWavReader(mockStream, true);

        int chunks = 0;
        await foreach (AudioChunk chunk in reader
            .ReadChannelChunksAsync(0, 1024, cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.True(chunk.Samples.Length > 0);
            chunks++;
        }

        Assert.True(chunks > 0);
    }

    // TC-2.4 | F-11
    [Fact]
    public async Task ReadChannelChunksAsync_ValidWav_BatchIsSamplesPerChannel()
    {
        var pipe = MockWavGenerator.CreateTestPcm16Wav(seconds: 2);
        var reader = new AsyncWavReader(pipe, true);

        List<int> lengths = [];
        await foreach (AudioChunk chunk in reader
            .ReadChannelChunksAsync(0, 512, cancellationToken: TestContext.Current.CancellationToken))
        {
            lengths.Add(chunk.Samples.Length);
        }

        Assert.Equal(1024, lengths[0]); // 512 сэмплов * 2 байта
        Assert.Equal(44100 * 2 * 2, lengths.Sum()); // 2 сек * 44100 Гц * 2 байта
    }

    // TC-2.5 | F-11
    [Fact]
    public async Task ReadChannelChunksAsync_Stereo_BothChannelsAreReadable()
    {
        var pipe = MockWavGenerator.CreateTestPcm16Wav(numChannels: 2);
        var reader = new AsyncWavReader(pipe, true);

        long left = 0;
        await foreach (AudioChunk chunk in reader
            .ReadChannelChunksAsync(0, 1024, cancellationToken: TestContext.Current.CancellationToken))
        {
            left += chunk.Samples.Length;
        }

        var pipe2 = MockWavGenerator.CreateTestPcm16Wav(numChannels: 2);
        var reader2 = new AsyncWavReader(pipe2, true);
        long right = 0;
        await foreach (AudioChunk chunk in reader2
            .ReadChannelChunksAsync(1, 1024, cancellationToken: TestContext.Current.CancellationToken))
        {
            right += chunk.Samples.Length;
        }

        Assert.Equal(44100 * 2, left);
        Assert.Equal(left, right);
    }

    // TC-2.6 | F-11
    [Fact]
    public async Task ReadChannelChunksAsync_LastChunk_HasEofFlag()
    {
        using var reader = AsyncWavReader.CreateFromFile(FILE);

        AudioChunk last = default;
        await foreach (AudioChunk chunk in reader
            .ReadChannelChunksAsync(0, 1024, cancellationToken: TestContext.Current.CancellationToken))
        {
            last = chunk;
        }

        Assert.True(last.IsEof);
        Assert.True(last.Samples.Length > 0);
    }

    // TC-2.7 | F-11
    [Fact]
    public async Task ReadChannelChunksAsync_Pcm16Format_ReturnsTwoBytesPerSample()
    {
        using var reader = AsyncWavReader.CreateFromFile(FILE);

        await foreach (AudioChunk chunk in reader
            .ReadChannelChunksAsync(0, 1024, AudioEncoding.Pcm16BitSigned, TestContext.Current.CancellationToken))
        {
            Assert.Equal(2048, chunk.Samples.Length);
            return;
        }

        Assert.Fail("No chunks produced");
    }

    // TC-2.8 | F-11
    [Fact]
    public async Task ReadChannelChunksAsync_WholeFile_DoesNotSpinOnTail()
    {
        // 30 секунд стерео > размера внутреннего буфера StreamPipeReader (64 KiB),
        // неполный кадр на границе буфера раньше зацикливал ReadAsync.
        var pipe = new CountingPipeReader(MockWavGenerator.CreateTestPcm16Wav(numChannels: 2, seconds: 30));
        var reader = new AsyncWavReader(pipe, true);

        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        long bytes = 0;
        await foreach (AudioChunk chunk in reader
            .ReadChannelChunksAsync(0, 4096, cancellationToken: watchdog.Token))
        {
            bytes += chunk.Samples.Length;
        }

        Assert.Equal(30L * 44100 * 2, bytes);
        // Один вызов ReadAsync на порцию данных StreamPipeReader; при кручении сработал бы
        // watchdog (5 c) и OperationCanceledException, а не ~1.3K вызовов за 86 мс.
        Assert.True(pipe.ReadAsyncCount < 2000,
            $"Reader крутился впустую: {pipe.ReadAsyncCount} вызовов ReadAsync");
    }

    // TC-2.9 | F-11
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

        long bytes = 0;
        await foreach (AudioChunk chunk in reader
            .ReadChannelChunksAsync(0, 1024, cancellationToken: TestContext.Current.CancellationToken))
        {
            bytes += chunk.Samples.Length;
        }

        Assert.Equal(44100 * 2, bytes); // 1 сек * 2 байта на сэмпл
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
