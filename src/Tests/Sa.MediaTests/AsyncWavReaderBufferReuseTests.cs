using System.Buffers.Binary;
using Sa.Media;
using Sa.MediaTests.Echo;

namespace Sa.MediaTests;

/// <summary>
/// Фиксирует контракт владения буферами: с <c>allowBufferReuse: true</c> пакеты ссылаются
/// на один пулированный буфер и переживают только до следующей итерации; с
/// <c>allowBufferReuse: false</c> каждый пакет — независимая копия. Readme обязан
/// описывать именно это поведение (по умолчанию буферы переиспользуются).
/// </summary>
public sealed class AsyncWavReaderBufferReuseTests
{
    private const short LeftValue = 1000;
    private const short RightValue = -2000;
    private const int Frames = 8;
    private const int BlockAlign = 4;
    private const int DataOffset = 44;

    [Fact]
    public async Task ReadSamplesPerChannel_AllowBufferReuse_ReusesSameBuffer()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TempDirectory directory = new();
        string path = WavTestData.CreateConstantStereo(
            directory.Path, LeftValue, RightValue, Frames, fileName: "reuse.wav");

        await using AsyncWavReader reader = AsyncWavReader.CreateFromFile(path);
        await using IAsyncEnumerator<AudioPacket> enumerator = reader
            .ReadSamplesPerChannelAsync(allowBufferReuse: true, cancellationToken: cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        Assert.True(await enumerator.MoveNextAsync());
        ReadOnlyMemory<byte> firstPacket = enumerator.Current.Sample;
        Assert.Equal(Int16Le(LeftValue), firstPacket.ToArray());

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(Int16Le(RightValue), enumerator.Current.Sample.ToArray());

        // Один и тот же буфер: после второго MoveNext первый пакет содержит уже правый канал.
        Assert.Equal(Int16Le(RightValue), firstPacket.ToArray());
    }

    [Fact]
    public async Task ReadSamplesPerChannel_NoBufferReuse_KeepsPacketsIndependent()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TempDirectory directory = new();
        string path = WavTestData.CreateConstantStereo(
            directory.Path, LeftValue, RightValue, Frames, fileName: "no_reuse.wav");

        await using AsyncWavReader reader = AsyncWavReader.CreateFromFile(path);
        await using IAsyncEnumerator<AudioPacket> enumerator = reader
            .ReadSamplesPerChannelAsync(allowBufferReuse: false, cancellationToken: cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        Assert.True(await enumerator.MoveNextAsync());
        ReadOnlyMemory<byte> firstPacket = enumerator.Current.Sample;
        Assert.Equal(Int16Le(LeftValue), firstPacket.ToArray());

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(Int16Le(RightValue), enumerator.Current.Sample.ToArray());

        // Копия: первый пакет не изменился.
        Assert.Equal(Int16Le(LeftValue), firstPacket.ToArray());
    }

    [Fact]
    public async Task ReadSamplesPerChannel_ReportsFrameByteOffsetsAndEof()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TempDirectory directory = new();
        string path = WavTestData.CreateConstantStereo(
            directory.Path, LeftValue, RightValue, Frames, fileName: "positions.wav");

        List<(long Position, bool IsEof)> packets = [];

        await using (AsyncWavReader reader = AsyncWavReader.CreateFromFile(path))
        {
            await foreach (AudioPacket packet in reader
                .ReadSamplesPerChannelAsync(cancellationToken: cancellationToken)
                .WithCancellation(cancellationToken))
            {
                packets.Add((packet.Position, packet.IsEof));
            }
        }

        Assert.Equal(Frames * 2, packets.Count);

        // Position — байтовое смещение начала кадра, а не номер сэмпла.
        Assert.Equal(DataOffset, packets[0].Position);
        Assert.Equal(DataOffset, packets[1].Position);
        Assert.Equal(DataOffset + 3 * BlockAlign, packets[6].Position);
        Assert.Equal(DataOffset + (Frames - 1) * BlockAlign, packets[14].Position);
        Assert.Equal(packets[14].Position, packets[15].Position);

        Assert.False(packets[12].IsEof);
        Assert.True(packets[14].IsEof);
        Assert.True(packets[15].IsEof);
    }

    private static byte[] Int16Le(short value)
    {
        byte[] bytes = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, value);
        return bytes;
    }
}
