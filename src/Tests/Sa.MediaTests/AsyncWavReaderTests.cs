using Sa.Media;
using System.IO.Pipelines;


namespace Sa.MediaTests;

public class AsyncWavReaderTests
{
    private const string FILE = "data/12345.wav";

    [Fact()]
    public async Task ReadHeaderFromPipeAsync_ValidWavFile_ReturnsCorrectHeader()
    {
        var pipeReader = OpenSharedWavFile();

        var header = await WavHeaderReader.ReadHeaderAsync(pipeReader, TestContext.Current.CancellationToken);

        Assert.NotNull(header);
    }


    [Theory]
    [InlineData("./data/psmS16Le.wav")]
    [InlineData("./data/12345.wav")]
    public async Task ReadHeaderAsync_ValidWavFile_ReturnsValidHeader(string filePath)
    {
        var pipe = OpenSharedWavFile(filePath);
        var reader = new AsyncWavReader(pipe);

        var header = await reader.GetHeaderAsync();

        Assert.True(header.SampleRate > 0);
        Assert.InRange(header.BitsPerSample, 8, 64);
        Assert.InRange(header.NumChannels, (ushort)1, (ushort)8);
    }


    [Theory]
    [InlineData("./data/ffout.wav")]
    [InlineData("./data/psmS16Le.wav")]
    [InlineData("./data/12345.wav")]
    public async Task GetLengthSecondsAsync_ValidWav_ReturnsCorrectDuration(string filePath)
    {
        var pipe = OpenSharedWavFile(filePath);
        var reader = new AsyncWavReader(pipe);

        var h = await reader.GetHeaderAsync();

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
        var pipe = OpenSharedWavFile();
        var reader = new AsyncWavReader(pipe);

        await foreach (var (_, sample, _) in reader.ReadRawChannelSamplesAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.True(sample.Length > 0);
            return;
        }
    }


    [Fact]
    public async Task ReadNormalizedDoubleSamplesAsync_ValidWavFile_YieldsInRangeValues()
    {
        var pipe = OpenSharedWavFile();
        var reader = new AsyncWavReader(pipe);

        await foreach (var (_, sample, _) in reader.ReadNormalizedDoubleSamplesAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.InRange(sample, -1.0, 1.0);
            return;
        }
    }

    [Fact]
    public async Task ReadStreamableChunksAsync_ValidWavFile_YieldsChunks()
    {
        var pipe = OpenSharedWavFile();
        var reader = new AsyncWavReader(pipe);

        await foreach (var (_, samples, _) in reader.ReadStreamableChunksAsync(bufferSize: 1024, cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.True(samples.Length > 0);
            return;
        }
    }


    [Fact]
    public async Task OpenWavFile_MultipleProcesses_NoException()
    {
        var reader1 = new AsyncWavReader(OpenSharedWavFile());
        var reader2 = new AsyncWavReader(OpenSharedWavFile());

        await Task.WhenAll(reader1.GetHeaderAsync(), reader2.GetHeaderAsync());

        Assert.NotNull(reader1);
        Assert.NotNull(reader2);
    }


    [Fact]
    public async Task ReadHeader_ValidMockWav_ReturnsCorrectHeader()
    {
        var mockStream = MockWavGenerator.CreateTestPcm16Wav();
        var reader = new AsyncWavReader(mockStream);

        var header = await reader.GetHeaderAsync();

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
        var reader = new AsyncWavReader(mockStream);

        await foreach (var (_, samples, _) in reader.ReadNormalizedDoubleSamplesAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.InRange(samples, -1.0, 1.0);
        }
    }

    [Fact]
    public async Task ReadStreamableChunks_ValidMockWav_YieldsChunks()
    {
        var mockStream = MockWavGenerator.CreateTestPcm16Wav(seconds: 1);
        var reader = new AsyncWavReader(mockStream);

        int chunks = 0;
        await foreach (var (_, samples, _) in reader.ReadStreamableChunksAsync(bufferSize: 1024, cancellationToken: TestContext.Current.CancellationToken))
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
        var reader = new AsyncWavReader(pipe);

        int chunksCount = 0;
        await foreach (var (channelId, samples, _) in reader.ReadStreamableChunksAsync(bufferSize: 512, cancellationToken: TestContext.Current.CancellationToken))
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
        var reader = new AsyncWavReader(pipe);

        List<int> channelIds = [];

        await foreach (var (channelId, _, _) in reader.ReadRawChannelSamplesAsync(cancellationToken: TestContext.Current.CancellationToken))
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
        var reader = new AsyncWavReader(pipe);

        float cutFrom = 1.0f;
        float cutTo = 4.0f;

        int count = 0;
        bool eof = false;

        await foreach (var (_, _, isEof) in reader.ReadNormalizedDoubleSamplesAsync(cutFrom, cutTo, cancellationToken: TestContext.Current.CancellationToken))
        {
            count++;
            eof = isEof;
        }

        Assert.InRange(count, 44100 * 2, 44100 * 3); // ~ от 1 до 4 секунды
        Assert.True(eof);
    }


    [Fact]
    public async Task ReadRawChannelSamplesAsync_ShouldSetIsEofAtEndOfFile()
    {
        var reader = new AsyncWavReader(OpenSharedWavFile());

        bool eof = false;
        await foreach (var (_, _, isEof) in reader.ReadRawChannelSamplesAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            eof = isEof;
        }

        Assert.True(eof);
    }


    [Fact]
    public async Task ConvertNormalizedDoubleAsync_ShouldConvertBackToPCM16()
    {
        var reader = new AsyncWavReader(OpenSharedWavFile());

        await foreach (var (_, sample, _) in reader.ConvertNormalizedDoubleAsync(AudioEncoding.Pcm16BitSigned, cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.Equal(2, sample.Length); // 16-bit PCM
        }
    }


    private static PipeReader OpenSharedWavFile(string filePath = FILE) => PipeReader.Create(new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite
    ));
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
