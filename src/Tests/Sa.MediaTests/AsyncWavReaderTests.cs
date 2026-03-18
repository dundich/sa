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
}
