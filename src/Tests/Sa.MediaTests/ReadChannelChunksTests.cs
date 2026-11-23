using Sa.Media;
using System.IO.Pipelines;

namespace Sa.MediaTests;

/// <summary>
/// Новый чанковый ридер: батч считается в сэмплах на канал, неполный кадр на
/// границе буфера не должен зацикливать PipeReader.
/// </summary>
public sealed class ReadChannelChunksTests
{
    private const int Batch = 1024;

    // TC-1.1 | F-11
    [Fact]
    public async Task ChunkSize_IsSamplesPerChannel()
    {
        byte[] wav = BuildWav(frames: 3000, junkSize: 0, extraDataBytes: 0);

        using AsyncWavReader reader = CreateReader(wav, bufferSize: 4096);
        List<int> lengths = [];
        await foreach (AudioChunk chunk in reader
            .ReadChannelChunksAsync(channel: 1, samplesPerBatch: Batch, cancellationToken: TestContext.Current.CancellationToken))
        {
            lengths.Add(chunk.Samples.Length);
        }

        Assert.Equal(new[] { 2048, 2048, 1904 }, lengths);
    }

    // TC-1.2 | F-11
    [Fact]
    public async Task UnalignedDataOffset_ReadsWholeFileWithoutSpinning()
    {
        // dataOffset = 78, blockAlign = 4: (4096 - 78) % 4 == 2.
        // Старый ReadSamplesPerChannelAsync здесь крутился на хвосте из 2 байт.
        byte[] wav = BuildWav(frames: 3000, junkSize: 26, extraDataBytes: 0);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using AsyncWavReader reader = CreateReader(wav, bufferSize: 4096);
        var header = await reader.GetHeaderAsync(cts.Token);

        long bytes = 0;
        long lastPosition = -1;
        await foreach (AudioChunk chunk in reader
            .ReadChannelChunksAsync(channel: 1, samplesPerBatch: Batch, cancellationToken: cts.Token)
            .WithCancellation(cts.Token))
        {
            bytes += chunk.Samples.Length;
            lastPosition = chunk.Position;
        }

        Assert.Equal((uint)78, header.DataOffset);
        Assert.Equal(6000, bytes);
        Assert.Equal(8192, lastPosition);
        Assert.False(cts.IsCancellationRequested);
    }

    // TC-1.3 | F-11
    [Fact]
    public async Task ChannelSelection_ReturnsOnlyRequestedChannel()
    {
        const int frames = 8;
        byte[] wav = BuildWav(frames, junkSize: 0, extraDataBytes: 0, left: 0x0102, right: 0x0304);

        using AsyncWavReader reader = CreateReader(wav, bufferSize: 4096);
        List<short> left = [];
        List<short> right = [];
        await foreach (AudioChunk chunk in reader
            .ReadChannelChunksAsync(channel: 0, samplesPerBatch: frames, cancellationToken: TestContext.Current.CancellationToken))
        {
            for (int i = 0; i < chunk.Samples.Length; i += 2)
                left.Add(BitConverter.ToInt16(chunk.Samples.Span[i..(i + 2)]));
        }

        using AsyncWavReader reader2 = CreateReader(wav, bufferSize: 4096);
        await foreach (AudioChunk chunk in reader2
            .ReadChannelChunksAsync(channel: 1, samplesPerBatch: frames, cancellationToken: TestContext.Current.CancellationToken))
        {
            for (int i = 0; i < chunk.Samples.Length; i += 2)
                right.Add(BitConverter.ToInt16(chunk.Samples.Span[i..(i + 2)]));
        }

        Assert.All(left, value => Assert.Equal(0x0102, value));
        Assert.All(right, value => Assert.Equal(0x0304, value));
        Assert.Equal(frames, left.Count);
        Assert.Equal(frames, right.Count);
    }

    // TC-1.4 | F-11
    [Fact]
    public async Task IncompleteLastFrame_IsDroppedWithoutException()
    {
        byte[] wav = BuildWav(frames: 10, junkSize: 0, extraDataBytes: 2);

        using AsyncWavReader reader = CreateReader(wav, bufferSize: 4096);
        List<AudioChunk> chunks = [];
        await foreach (AudioChunk chunk in reader
            .ReadChannelChunksAsync(channel: 0, samplesPerBatch: Batch, cancellationToken: TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        Assert.Single(chunks);
        Assert.Equal(20, chunks[0].Samples.Length);
        Assert.True(chunks[0].IsEof);
    }

    // TC-1.5 | F-11
    [Fact]
    public async Task Cancellation_ThrowsOperationCanceledException()
    {
        byte[] wav = BuildWav(frames: 3000, junkSize: 26, extraDataBytes: 0);
        using var cts = new CancellationTokenSource();

        using AsyncWavReader reader = CreateReader(wav, bufferSize: 4096);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (AudioChunk _ in reader
                .ReadChannelChunksAsync(channel: 0, samplesPerBatch: Batch, cancellationToken: cts.Token)
                .WithCancellation(cts.Token))
            {
                cts.Cancel();
            }
        });
    }

    // TC-1.6 | F-11
    [Fact]
    public async Task RealFile_ReadsWholeFileWithinDeadline()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "data", "12345.wav");
        Assert.True(File.Exists(path), $"test data missing: {path}");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using AsyncWavReader reader = AsyncWavReader.CreateFromFile(path);
        var header = await reader.GetHeaderAsync(cts.Token);

        long bytes = 0;
        int chunks = 0;
        await foreach (AudioChunk chunk in reader
            .ReadChannelChunksAsync(channel: 0, samplesPerBatch: Batch, cancellationToken: cts.Token)
            .WithCancellation(cts.Token))
        {
            bytes += chunk.Samples.Length;
            chunks++;
        }

        Assert.Equal(header.DataSize, (uint)bytes);
        Assert.Equal(13, chunks);
        Assert.False(cts.IsCancellationRequested);
    }

    // TC-1.7 | F-11
    [Fact]
    public async Task NonPcm16Format_ConvertsThroughNormalizedValue()
    {
        const int frames = 8;
        byte[] wav = BuildWav(frames, junkSize: 0, extraDataBytes: 0, left: 258, right: 772);

        using AsyncWavReader reader = CreateReader(wav, bufferSize: 4096);
        List<float> values = [];
        await foreach (AudioChunk chunk in reader
            .ReadChannelChunksAsync(0, frames, AudioEncoding.IeeeFloat32Bit, TestContext.Current.CancellationToken))
        {
            for (int i = 0; i < chunk.Samples.Length; i += 4)
                values.Add(BitConverter.ToSingle(chunk.Samples.Span[i..(i + 4)]));
        }

        Assert.Equal(frames, values.Count);
        Assert.All(values, value => Assert.Equal(258 / 32768f, value, 1e-6));
    }

    // TC-1.8 | F-11
    [Fact]
    public async Task EmptyDataChunk_YieldsNoSamplesAndDoesNotReadFollowingChunks()
    {
        // Явно объявленный data size = 0 — это пустая запись, а не «размер неизвестен».
        // Раньше оба случая попадали в long.MaxValue, и байты следующего LIST-чанка
        // уходили в результат как аудио.
        byte[] wav = BuildWav(frames: 0, junkSize: 0, extraDataBytes: 0, trailingChunkBytes: 8);

        using AsyncWavReader reader = CreateReader(wav, bufferSize: 4096);
        WavHeader header = await reader.GetHeaderAsync(TestContext.Current.CancellationToken);

        Assert.True(header.HasDataSize);
        Assert.Equal(0u, header.DataSize);

        List<AudioChunk> chunks = [];
        await foreach (AudioChunk chunk in reader
            .ReadChannelChunksAsync(channel: 0, samplesPerBatch: Batch, cancellationToken: TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        Assert.Empty(chunks);
    }

    private static AsyncWavReader CreateReader(byte[] wav, int bufferSize)
        => AsyncWavReader.Create(
            new MemoryStream(wav),
            new StreamPipeReaderOptions(bufferSize: bufferSize, leaveOpen: true));

    private static byte[] BuildWav(
        int frames,
        int junkSize,
        int extraDataBytes,
        short left = 1234,
        short right = 5678,
        int trailingChunkBytes = 0)
    {
        int dataSize = frames * 4 + extraDataBytes;

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(0u);
        writer.Write("WAVE"u8.ToArray());

        writer.Write("fmt "u8.ToArray());
        writer.Write(16u);
        writer.Write((ushort)1);      // PCM
        writer.Write((ushort)2);      // stereo
        writer.Write(8000u);
        writer.Write(32000u);
        writer.Write((ushort)4);      // blockAlign
        writer.Write((ushort)16);

        if (junkSize > 0)
        {
            writer.Write("JUNK"u8.ToArray());
            writer.Write((uint)junkSize);
            writer.Write(new byte[junkSize]);
        }

        writer.Write("data"u8.ToArray());
        writer.Write((uint)dataSize);

        for (int i = 0; i < frames; i++)
        {
            writer.Write(left);
            writer.Write(right);
        }

        writer.Write(new byte[extraDataBytes]);

        if (trailingChunkBytes > 0)
        {
            // Метаданные после data: если размер data объявлен честно, эти байты аудио не являются.
            writer.Write("LIST"u8.ToArray());
            writer.Write((uint)trailingChunkBytes);
            writer.Write(new byte[trailingChunkBytes]);
        }

        writer.Flush();

        // RIFF size = file size - 8
        byte[] result = stream.ToArray();
        BitConverter.GetBytes(result.Length - 8).CopyTo(result, 4);
        return result;
    }
}
