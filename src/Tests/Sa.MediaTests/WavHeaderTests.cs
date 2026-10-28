using Sa.Media;

namespace Sa.MediaTests;

/// <summary>
/// Заголовок WAV: длительность для потоковых записей (data size == 0xFFFFFFFF) и
/// проверка согласованности полей, на которые опирается читатель сэмплов.
/// </summary>
public sealed class WavHeaderTests
{
    private const uint DataOffset = 44;
    private const uint BytesPerSecond = 44100 * 4;

    private static WavHeader CreatePcm16Stereo(uint dataSize) => new()
    {
        AudioFormat = WaveFormatType.Pcm,
        NumChannels = 2,
        SampleRate = 44100,
        ByteRate = BytesPerSecond,
        BlockAlign = 4,
        BitsPerSample = 16,
        DataOffset = DataOffset,
        DataSize = dataSize
    };

    [Fact]
    public void GetDurationInSeconds_KnownDataSize_ReturnsDuration()
    {
        WavHeader header = CreatePcm16Stereo(BytesPerSecond);   // 1 секунда

        Assert.True(header.HasDataSize);
        Assert.Equal(1.0, header.GetDurationInSeconds(), 6);
        Assert.Equal(TimeSpan.FromSeconds(1), header.GetDuration());
    }

    [Fact]
    public void GetDurationInSeconds_UnknownDataSizeWithoutFileSize_ReturnsZero()
    {
        WavHeader header = CreatePcm16Stereo(uint.MaxValue);

        Assert.False(header.HasDataSize);
        // Раньше здесь было ~13.5 часа: DataSize == uint.MaxValue попадал в расчёт как есть.
        Assert.Equal(0.0, header.GetDurationInSeconds());
        Assert.Equal(TimeSpan.Zero, header.GetDuration());
    }

    [Fact]
    public void GetDurationInSeconds_UnknownDataSizeWithFileSize_UsesFileLength()
    {
        WavHeader header = CreatePcm16Stereo(uint.MaxValue);

        Assert.Equal(1.0, header.GetDurationInSeconds(DataOffset + BytesPerSecond), 6);
        Assert.Equal(0.5, header.GetDurationInSeconds(DataOffset + BytesPerSecond / 2), 6);
    }

    [Fact]
    public void GetDurationInSeconds_FileShorterThanDataOffset_ReturnsZero()
    {
        WavHeader header = CreatePcm16Stereo(uint.MaxValue);

        Assert.Equal(0.0, header.GetDurationInSeconds(DataOffset - 1));
    }

    [Fact]
    public void CalculateCutOffsets_UnknownDataSize_UsesFileLength()
    {
        WavHeader header = CreatePcm16Stereo(uint.MaxValue);

        (long from, long to) = header.CalculateCutOffsets(
            TimeRange.Seconds(0.25, 0.5),
            fileSize: DataOffset + BytesPerSecond);

        Assert.Equal(DataOffset + BytesPerSecond / 4, from);
        Assert.Equal(DataOffset + BytesPerSecond / 2, to);
    }

    [Fact]
    public void Validate_BlockAlignMismatch_ThrowsInvalidDataException()
    {
        WavHeader header = CreatePcm16Stereo(1000);
        header.BlockAlign = 3;   // 2 канала x 2 байта = 4

        Assert.Throws<InvalidDataException>(() => header.Validate());
    }

    [Fact]
    public void Validate_ZeroBlockAlign_ThrowsInvalidDataException()
    {
        WavHeader header = CreatePcm16Stereo(1000);
        header.BlockAlign = 0;

        Assert.Throws<InvalidDataException>(() => header.Validate());
    }

    [Theory]
    [InlineData(WaveFormatType.Pcm, 64)]
    [InlineData(WaveFormatType.Pcm, 12)]
    [InlineData(WaveFormatType.IeeeFloat, 8)]
    [InlineData(WaveFormatType.IeeeFloat, 16)]
    public void Validate_UnsupportedFormatBits_ThrowsNotSupported(WaveFormatType format, ushort bitsPerSample)
    {
        WavHeader header = CreatePcm16Stereo(1000);
        header.AudioFormat = format;
        header.BitsPerSample = bitsPerSample;
        header.BlockAlign = (ushort)(header.NumChannels * (bitsPerSample / 8 == 0 ? 1 : bitsPerSample / 8));

        Assert.Throws<NotSupportedException>(() => header.Validate());
    }

    [Fact]
    public void Validate_Adpcm_ThrowsNotSupported()
    {
        WavHeader header = CreatePcm16Stereo(1000);
        header.AudioFormat = WaveFormatType.Adpcm;

        Assert.Throws<NotSupportedException>(() => header.Validate());
    }

    [Theory]
    [InlineData(WaveFormatType.Pcm, 8, 2)]
    [InlineData(WaveFormatType.Pcm, 16, 4)]
    [InlineData(WaveFormatType.Pcm, 24, 6)]
    [InlineData(WaveFormatType.Pcm, 32, 8)]
    [InlineData(WaveFormatType.IeeeFloat, 32, 8)]
    [InlineData(WaveFormatType.IeeeFloat, 64, 16)]
    public void Validate_SupportedFormatBits_DoesNotThrow(
        WaveFormatType format, ushort bitsPerSample, ushort blockAlign)
    {
        WavHeader header = CreatePcm16Stereo(1000);
        header.AudioFormat = format;
        header.BitsPerSample = bitsPerSample;
        header.BlockAlign = blockAlign;

        header.Validate();
    }
}
