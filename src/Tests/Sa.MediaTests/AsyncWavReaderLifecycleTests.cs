using Sa.Media;
using Sa.MediaTests.Echo;

namespace Sa.MediaTests;

/// <summary>
/// Владение ресурсами и ленивый заголовок: отмена одного вызова не должна «травить» ридер,
/// Dispose должен быть идемпотентным, а переданный поток — оставаться у вызывающего.
/// </summary>
public sealed class AsyncWavReaderLifecycleTests
{
    [Fact]
    public async Task Create_LeavesCallerStreamOpenAfterDisposeAsync()
    {
        using TempDirectory directory = new();
        string path = WavTestData.CreateConstantStereo(directory.Path, 1000, -2000, frames: 32);
        byte[] bytes = File.ReadAllBytes(path);
        using MemoryStream stream = new(bytes);

        await using (AsyncWavReader reader = AsyncWavReader.Create(stream))
        {
            WavHeader header = await reader.GetHeaderAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, header.NumChannels);
        }

        Assert.True(stream.CanRead);
        Assert.Equal(bytes.Length, stream.Length);
    }

    [Fact]
    public void Create_LeavesCallerStreamOpenAfterDispose()
    {
        using TempDirectory directory = new();
        string path = WavTestData.CreateConstantStereo(directory.Path, 1000, -2000, frames: 32);
        using MemoryStream stream = new(File.ReadAllBytes(path));

        AsyncWavReader reader = AsyncWavReader.Create(stream);
        reader.Dispose();

        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        using TempDirectory directory = new();
        string path = WavTestData.CreateConstantStereo(directory.Path, 1000, -2000, frames: 32);

        AsyncWavReader reader = AsyncWavReader.CreateFromFile(path);
        reader.Dispose();
        reader.Dispose();
        await reader.DisposeAsync();
        await reader.DisposeAsync();
    }

    [Fact]
    public async Task GetHeaderAsync_FirstCallCancelled_DoesNotPoisonReader()
    {
        using TempDirectory directory = new();
        string path = WavTestData.CreateConstantStereo(directory.Path, 1000, -2000, frames: 32);

        await using AsyncWavReader reader = AsyncWavReader.CreateFromFile(path);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.GetHeaderAsync(cancelled.Token));

        WavHeader header = await reader.GetHeaderAsync(TestContext.Current.CancellationToken);
        Assert.Equal(8000u, header.SampleRate);
    }

    [Fact]
    public async Task GetHeaderAsync_StreamingWavFile_ResolvesDataSizeFromFileLength()
    {
        using TempDirectory directory = new();
        string path = WavTestData.CreateStreamingStereo(directory.Path, 1000, -1000, frames: 8000);

        await using AsyncWavReader reader = AsyncWavReader.CreateFromFile(path);
        WavHeader header = await reader.GetHeaderAsync(TestContext.Current.CancellationToken);

        Assert.True(header.HasDataSize, "data size должен восстанавливаться из длины файла");
        Assert.Equal(8000u * 4, header.DataSize);
        Assert.Equal(1.0, header.GetDurationInSeconds(), 3);
    }

    [Fact]
    public async Task GetHeaderAsync_StreamingWavInSeekableStream_ResolvesDataSize()
    {
        using TempDirectory directory = new();
        string path = WavTestData.CreateStreamingStereo(directory.Path, 1000, -1000, frames: 4000);
        using MemoryStream stream = new(File.ReadAllBytes(path));

        await using AsyncWavReader reader = AsyncWavReader.Create(stream);
        WavHeader header = await reader.GetHeaderAsync(TestContext.Current.CancellationToken);

        Assert.True(header.HasDataSize);
        Assert.Equal(4000u * 4, header.DataSize);
    }

    [Fact]
    public async Task ReadSamplesPerChannel_StreamingWav_ReadsAllSamples()
    {
        using TempDirectory directory = new();
        string path = WavTestData.CreateStreamingStereo(directory.Path, 1000, -1000, frames: 8000);

        await using AsyncWavReader reader = AsyncWavReader.CreateFromFile(path);

        long count = 0;
        await foreach (AudioPacket _ in reader
            .ReadSamplesPerChannelAsync(cancellationToken: TestContext.Current.CancellationToken)
            .WithCancellation(TestContext.Current.CancellationToken))
        {
            count++;
        }

        Assert.Equal(8000L * 2, count);
    }
}
