using System.IO.Pipelines;
using Sa.Media;

namespace Sa.MediaTests;

/// <summary>
/// Разбор заголовка на «плохих» файлах: обрезанный поток и несогласованные поля должны
/// давать InvalidDataException/NotSupportedException, а не ArgumentOutOfRangeException
/// из недр ReadOnlySequence/BinaryPrimitives.
/// </summary>
public sealed class WavHeaderReaderTests
{
    [Fact]
    public async Task GetHeaderAsync_TruncatedRiff_ThrowsInvalidDataException()
    {
        byte[] bytes = BuildWav()[..2];   // "RI"

        await AssertHeaderFailureAsync<InvalidDataException>(bytes);
    }

    [Fact]
    public async Task GetHeaderAsync_TruncatedFmtChunk_ThrowsInvalidDataException()
    {
        // fmt объявлен 16 байт, но файл обрывается на 4 байта раньше конца fmt.
        byte[] full = BuildWav(includeDataChunk: false);   // 12 (RIFF/WAVE) + 8 (fmt id/size) + 16 (fmt payload)
        byte[] bytes = full[..^4];

        await AssertHeaderFailureAsync<InvalidDataException>(bytes);
    }

    [Fact]
    public async Task GetHeaderAsync_MissingDataChunk_ThrowsInvalidDataException()
    {
        byte[] bytes = BuildWav(includeDataChunk: false);

        await AssertHeaderFailureAsync<InvalidDataException>(bytes);
    }

    [Fact]
    public async Task GetHeaderAsync_FmtChunkTooSmall_ThrowsInvalidDataException()
    {
        byte[] bytes = BuildWav(fmtSize: 12);

        await AssertHeaderFailureAsync<InvalidDataException>(bytes);
    }

    [Fact]
    public async Task GetHeaderAsync_BlockAlignMismatch_ThrowsInvalidDataException()
    {
        byte[] bytes = BuildWav(blockAlign: 3);

        await AssertHeaderFailureAsync<InvalidDataException>(bytes);
    }

    [Fact]
    public async Task GetHeaderAsync_UnsupportedBitsPerSample_ThrowsNotSupported()
    {
        byte[] bytes = BuildWav(bitsPerSample: 12, blockAlign: 3);

        await AssertHeaderFailureAsync<NotSupportedException>(bytes);
    }

    [Fact]
    public async Task GetHeaderAsync_NotRiff_ThrowsNotSupported()
    {
        byte[] bytes = BuildWav();
        "RIFX"u8.CopyTo(bytes);

        await AssertHeaderFailureAsync<NotSupportedException>(bytes);
    }

    [Fact]
    public async Task GetHeaderAsync_ValidHeader_ReturnsHeader()
    {
        await using AsyncWavReader reader = CreateReader(BuildWav());

        WavHeader header = await reader.GetHeaderAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, header.NumChannels);
        Assert.Equal(44100u, header.SampleRate);
        Assert.Equal(16, header.BitsPerSample);
        Assert.Equal(WaveFormatType.Pcm, header.AudioFormat);
    }

    private static async Task AssertHeaderFailureAsync<TException>(byte[] bytes)
        where TException : Exception
    {
        await using AsyncWavReader reader = CreateReader(bytes);

        await Assert.ThrowsAsync<TException>(
            () => reader.GetHeaderAsync(TestContext.Current.CancellationToken));
    }

    private static AsyncWavReader CreateReader(byte[] bytes)
        => new(PipeReader.Create(new MemoryStream(bytes)), ownsReader: true);

    private static byte[] BuildWav(
        uint fmtSize = 16,
        ushort audioFormat = 1,
        ushort channels = 2,
        uint sampleRate = 44100,
        ushort blockAlign = 4,
        ushort bitsPerSample = 16,
        uint dataSize = 0,
        bool includeDataChunk = true)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8.ToArray());

        writer.Write("fmt "u8.ToArray());
        writer.Write(fmtSize);
        writer.Write(audioFormat);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * (bitsPerSample / 8u));
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);

        for (int i = 16; i < fmtSize; i++)
            writer.Write((byte)0);

        if (includeDataChunk)
        {
            writer.Write("data"u8.ToArray());
            writer.Write(dataSize);
        }

        writer.Flush();
        return stream.ToArray();
    }
}
