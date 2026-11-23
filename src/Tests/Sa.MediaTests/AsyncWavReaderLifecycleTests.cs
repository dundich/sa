using Sa.Media;
using Sa.MediaTests.Echo;
using System.IO.Pipelines;

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

    // TC-4.1 | F-11
    [Fact]
    public async Task ReadChannelChunks_StreamingWav_ReadsAllSamples()
    {
        using TempDirectory directory = new();
        string path = WavTestData.CreateStreamingStereo(directory.Path, 1000, -1000, frames: 8000);

        await using AsyncWavReader reader = AsyncWavReader.CreateFromFile(path);

        long bytes = 0;
        await foreach (AudioChunk chunk in reader
            .ReadChannelChunksAsync(0, 1024, cancellationToken: TestContext.Current.CancellationToken)
            .WithCancellation(TestContext.Current.CancellationToken))
        {
            bytes += chunk.Samples.Length;
        }

        Assert.Equal(8000L * 2, bytes); // 8000 сэмплов по 2 байта
    }

    [Fact]
    public async Task PipeReaderConstructor_ReadsWithoutOwningThePipe()
    {
        // Конструктор без ownsReader — публичная точка входа для вызывающих со своим пайпом.
        using TempDirectory directory = new();
        string path = WavTestData.CreateConstantStereo(directory.Path, 1000, -2000, frames: 32);

        using MemoryStream stream = new(File.ReadAllBytes(path));
        var pipe = PipeReader.Create(stream);

        await using var reader = new AsyncWavReader(pipe);
        WavHeader header = await reader.GetHeaderAsync(TestContext.Current.CancellationToken);

        Assert.Equal(8000u, header.SampleRate);
        Assert.Equal(2, header.NumChannels);

        long bytes = 0;
        await foreach (AudioChunk chunk in reader
            .ReadChannelChunksAsync(0, 8, cancellationToken: TestContext.Current.CancellationToken))
        {
            bytes += chunk.Samples.Length;
        }

        Assert.Equal(32L * 2, bytes);
    }

    [Fact]
    public async Task ReadInterleavedFloatChunksAsync_MonoInput_ThrowsNotSupported()
    {
        // Внутренний путь Echo рассчитан только на стерео: моно обязан падать с понятным
        // исключением, а не читать «правый канал» из несуществующих данных.
        using TempDirectory directory = new();
        string path = WavTestData.CreateMono(directory.Path);

        await using AsyncWavReader reader = AsyncWavReader.CreateFromFile(path);

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (ReadOnlyMemory<float> _ in reader
                .ReadInterleavedFloatChunksAsync(framesPerBatch: 16, cancellationToken: TestContext.Current.CancellationToken))
            {
            }
        });
    }

    [Fact]
    public async Task GetHeaderAsync_StreamingWavInStreamWithoutLength_LeavesDataSizeUnknown()
    {
        // Поток seekable, но длину не сообщает (типично для сетевых обёрток): восстановить
        // размер потоковой записи нельзя — DataSize остаётся 0xFFFFFFFF, а не «13 часов».
        using TempDirectory directory = new();
        string path = WavTestData.CreateStreamingStereo(directory.Path, 1000, -1000, frames: 64);

        using var stream = new StreamWithoutLength(File.ReadAllBytes(path));
        await using AsyncWavReader reader = AsyncWavReader.Create(stream);

        WavHeader header = await reader.GetHeaderAsync(TestContext.Current.CancellationToken);

        Assert.False(header.HasDataSize);
        Assert.Equal(0.0, header.GetDurationInSeconds());
    }

    [Fact]
    public async Task GetHeaderAsync_StreamingWavWithoutData_LeavesDataSizeUnknown()
    {
        // Потоковый заголовок без единого байта данных: length - DataOffset == 0,
        // отрицательный/нулевой размер не должен превращаться в DataSize.
        using TempDirectory directory = new();
        string path = WavTestData.CreateStreamingStereo(directory.Path, 1000, -1000, frames: 0);

        await using AsyncWavReader reader = AsyncWavReader.CreateFromFile(path);
        WavHeader header = await reader.GetHeaderAsync(TestContext.Current.CancellationToken);

        Assert.False(header.HasDataSize);
    }

    /// <summary>
    /// Поток, который сообщает <see cref="CanSeek"/>, но не умеет сообщать длину —
    /// так ведут себя некоторые сетевые и криптографические обёртки.
    /// </summary>
    private sealed class StreamWithoutLength(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
