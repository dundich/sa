using Sa.Media;
using Sa.MediaTests.Echo;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Sa.MediaTests;

/// <summary>
/// Контракт владения буфером нового чанкового ридера: <see cref="AudioChunk.Samples"/>
/// ссылается на пулированный буфер и действителен только до следующего MoveNextAsync.
/// </summary>
public sealed class AsyncWavReaderBufferReuseTests
{
    private const short LeftValue = 1000;
    private const short RightValue = -2000;
    private const int Frames = 8;

    // TC-3.1 | F-11
    [Fact]
    public async Task ReadChannelChunks_BufferIsReusedBetweenMoves()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TempDirectory directory = new();
        string path = WavTestData.CreateConstantStereo(
            directory.Path, LeftValue, RightValue, Frames, fileName: "reuse.wav");

        await using AsyncWavReader reader = AsyncWavReader.CreateFromFile(path);
        await using IAsyncEnumerator<AudioChunk> enumerator = reader
            .ReadChannelChunksAsync(0, 1, cancellationToken: cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        Assert.True(await enumerator.MoveNextAsync());
        ReadOnlyMemory<byte> firstChunk = enumerator.Current.Samples;
        Assert.Equal(Int16Le(LeftValue), firstChunk.ToArray());

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(Int16Le(LeftValue), enumerator.Current.Samples.ToArray());

        // Один и тот же пулированный массив лежит в основе обоих чанков.
        Assert.True(MemoryMarshal.TryGetArray(firstChunk, out ArraySegment<byte> firstArray));
        Assert.True(MemoryMarshal.TryGetArray(enumerator.Current.Samples, out ArraySegment<byte> secondArray));
        Assert.Same(firstArray.Array, secondArray.Array);
    }

    // TC-3.2 | F-11
    [Fact]
    public async Task ReadChannelChunks_ReportsDataRelativeOffsetsAndEof()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TempDirectory directory = new();
        string path = WavTestData.CreateConstantStereo(
            directory.Path, LeftValue, RightValue, Frames, fileName: "positions.wav");

        List<(long Position, bool IsEof)> chunks = [];

        await using (AsyncWavReader reader = AsyncWavReader.CreateFromFile(path))
        {
            await foreach (AudioChunk chunk in reader
                .ReadChannelChunksAsync(0, 3, cancellationToken: cancellationToken)
                .WithCancellation(cancellationToken))
            {
                chunks.Add((chunk.Position, chunk.IsEof));
            }
        }

        // Position — байтовое смещение от начала data-чанка, а не от начала файла.
        Assert.Equal(new[] { 0L, 12L, 24L }, chunks.Select(c => c.Position));
        Assert.False(chunks[0].IsEof);
        Assert.False(chunks[1].IsEof);
        Assert.True(chunks[2].IsEof);
    }

    private static byte[] Int16Le(short value)
    {
        byte[] bytes = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, value);
        return bytes;
    }
}
