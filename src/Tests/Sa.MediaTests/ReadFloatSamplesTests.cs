using Sa.Media;
using Sa.MediaTests.Echo;

namespace Sa.MediaTests;

/// <summary>
/// Публичный float-путь <see cref="AsyncWavReader.ReadFloatSamplesAsync"/>:
/// те же каналы/позиции/EOF, что у double-пути, но без промежуточного double.
/// </summary>
public sealed class ReadFloatSamplesTests
{
    private const short LeftValue = 1000;
    private const short RightValue = -2000;

    [Fact]
    public async Task ReadFloatSamples_MatchesDoubleSamples()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TempDirectory directory = new();
        string path = WavTestData.CreateConstantStereo(
            directory.Path, LeftValue, RightValue, frames: 64);

        List<AudioNormalizedPacket> doubles = [];
        await using (AsyncWavReader reader = AsyncWavReader.CreateFromFile(path))
        {
            await foreach (AudioNormalizedPacket packet in reader
                .ReadDoubleSamplesAsync(cancellationToken: cancellationToken)
                .WithCancellation(cancellationToken))
            {
                doubles.Add(packet);
            }
        }

        List<AudioFloatPacket> singles = [];
        await using (AsyncWavReader reader = AsyncWavReader.CreateFromFile(path))
        {
            await foreach (AudioFloatPacket packet in reader
                .ReadFloatSamplesAsync(cancellationToken: cancellationToken)
                .WithCancellation(cancellationToken))
            {
                singles.Add(packet);
            }
        }

        Assert.Equal(doubles.Count, singles.Count);
        for (int i = 0; i < doubles.Count; i++)
        {
            Assert.Equal(doubles[i].ChannelId, singles[i].ChannelId);
            Assert.Equal(doubles[i].Position, singles[i].Position);
            Assert.Equal(doubles[i].IsEof, singles[i].IsEof);
            Assert.Equal(doubles[i].Sample, singles[i].Sample, 1e-6);
        }
    }

    [Fact]
    public async Task ReadFloatSamples_YieldsNormalizedValuesPerChannel()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TempDirectory directory = new();
        string path = WavTestData.CreateConstantStereo(
            directory.Path, LeftValue, RightValue, frames: 4);

        List<AudioFloatPacket> packets = [];
        await using (AsyncWavReader reader = AsyncWavReader.CreateFromFile(path))
        {
            await foreach (AudioFloatPacket packet in reader
                .ReadFloatSamplesAsync(cancellationToken: cancellationToken)
                .WithCancellation(cancellationToken))
            {
                packets.Add(packet);
            }
        }

        Assert.Equal(4 * 2, packets.Count);

        float expectedLeft = LeftValue / 32768f;
        float expectedRight = RightValue / 32768f;

        Assert.Equal(expectedLeft, packets[0].Sample, 1e-6);
        Assert.Equal(expectedRight, packets[1].Sample, 1e-6);
        Assert.Equal(0, packets[0].ChannelId);
        Assert.Equal(1, packets[1].ChannelId);

        // Первый кадр начинается сразу после 44-байтного заголовка.
        Assert.Equal(44, packets[0].Position);
        Assert.Equal(48, packets[2].Position);

        Assert.False(packets[4].IsEof);
        Assert.True(packets[6].IsEof);
        Assert.True(packets[7].IsEof);
    }

    [Fact]
    public async Task ReadFloatSamples_WithCutRange_ReturnsTrimmedSamples()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TempDirectory directory = new();
        string path = WavTestData.CreateConstantStereo(
            directory.Path, LeftValue, RightValue, frames: 8000, sampleRate: 8000);

        int count = 0;
        await using (AsyncWavReader reader = AsyncWavReader.CreateFromFile(path))
        {
            await foreach (AudioFloatPacket _ in reader
                .ReadFloatSamplesAsync(TimeRange.Seconds(0.25, 0.5), cancellationToken)
                .WithCancellation(cancellationToken))
            {
                count++;
            }
        }

        // 0.25 с при 8000 Гц = 2000 кадров, по два пакета на кадр.
        Assert.Equal(2000 * 2, count);
    }
}
