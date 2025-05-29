using Sa.Media;
using System.IO.Pipelines;


namespace Sa.MediaTests;

public class AsyncWavReaderTests
{
    private const string FILE = "data/12345.wav";

    [Fact()]
    public async Task ReadHeaderFromPipeAsync_ValidWavFile_ReturnsCorrectHeader()
    {
        using var stream = OpenSharedWavFile();
        var pipeReader = PipeReader.Create(stream);

        var header = await AsyncWavReader.ReadHeaderAsync(pipeReader, TestContext.Current.CancellationToken);

        Assert.NotNull(header);
    }


    [Fact]
    public async Task ReadHeaderAsync_ValidWavFile_ReturnsValidHeader()
    {
        using var stream = OpenSharedWavFile();
        var reader = new AsyncWavReader(stream);

        var header = await reader.GetHeaderAsync();

        Assert.True(header.SampleRate > 0);
        Assert.InRange(header.BitsPerSample, 8, 64);
        Assert.InRange(header.NumChannels, (ushort)1, (ushort)8);
    }


    [Fact]
    public async Task GetLengthSecondsAsync_ValidWav_ReturnsCorrectDuration()
    {
        using var stream = OpenSharedWavFile();
        var reader = new AsyncWavReader(stream);

        double lengthInSeconds = (await reader.GetHeaderAsync()).GetDurationInSeconds();

        Assert.True(lengthInSeconds > 0);
    }


    [Fact]
    public async Task ReadRawChannelSamplesAsync_ValidWavFile_YieldsNonEmptyData()
    {
        using var stream = OpenSharedWavFile();
        var reader = new AsyncWavReader(stream);

        await foreach (var (_, sample, _) in reader.ReadRawChannelSamplesAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.True(sample.Length > 0);
            return;
        }
    }


    [Fact]
    public async Task ReadNormalizedDoubleSamplesAsync_ValidWavFile_YieldsInRangeValues()
    {
        using var stream = OpenSharedWavFile();
        var reader = new AsyncWavReader(stream);

        await foreach (var (_, sample, _) in reader.ReadNormalizedDoubleSamplesAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.InRange(sample, -1.0, 1.0);
            return;
        }
    }

    [Fact]
    public async Task ReadStreamableChunksAsync_ValidWavFile_YieldsChunks()
    {
        using var stream = OpenSharedWavFile();
        var reader = new AsyncWavReader(stream);

        await foreach (var (_, samples, _) in reader.ReadPcm16BitStreamableChunksAsync(bufferSize: 1024, cancellationToken: TestContext.Current.CancellationToken))
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
        using var mockStream = MockWavGenerator.CreateTestPcm16Wav();
        using var reader = new AsyncWavReader(mockStream);

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
        using var mockStream = MockWavGenerator.CreateTestPcm16Wav(seconds: 1);
        using var reader = new AsyncWavReader(mockStream);

        await foreach (var (_, samples, _) in reader.ReadNormalizedDoubleSamplesAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.InRange(samples, -1.0, 1.0);
            return;
        }
    }

    [Fact]
    public async Task ReadStreamableChunks_ValidMockWav_YieldsChunks()
    {
        using var mockStream = MockWavGenerator.CreateTestPcm16Wav(seconds: 1);
        using var reader = new AsyncWavReader(mockStream);

        int chunks = 0;
        await foreach (var (_, samples, _) in reader.ReadPcm16BitStreamableChunksAsync(bufferSize: 1024, cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.True(samples.Length > 0);
            chunks++;
        }

        Assert.True(chunks > 0);
    }


    [Fact]
    public async Task ReadStreamableChunksAsync_ValidWav_YieldsChunks()
    {
        using var stream = MockWavGenerator.CreateTestPcm16Wav(seconds: 2);
        var reader = new AsyncWavReader(stream);

        int chunksCount = 0;
        await foreach (var (channelId, samples, _) in reader.ReadPcm16BitStreamableChunksAsync(512, cancellationToken: TestContext.Current.CancellationToken))
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
        await using var stream = MockWavGenerator.CreateTestPcm16Wav(numChannels: 2);
        var reader = new AsyncWavReader(stream);

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
        await using var stream = MockWavGenerator.CreateTestPcm16Wav(seconds: seconds);
        var reader = new AsyncWavReader(stream);

        float cutFrom = 1.0f;
        float cutTo = 4.0f;

        int count = 0;
        await foreach (var (_, _, _) in reader.ReadNormalizedDoubleSamplesAsync(cutFrom, cutTo, cancellationToken: TestContext.Current.CancellationToken))
        {
            count++;
        }

        Assert.InRange(count, 44100 * 2, 44100 * 3); // ~ от 1 до 4 секунды
    }


    private static FileStream OpenSharedWavFile(string filePath = FILE) => new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite
        );
}



public static class MockWavGenerator
{
    /// <summary>
    /// Генерирует PCM WAV в памяти (16 бит, 44100 Гц)
    /// </summary>
    public static Stream CreateTestPcm16Wav(int sampleRate = 44100, int numChannels = 1, int seconds = 1)
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

        return ms;
    }
}