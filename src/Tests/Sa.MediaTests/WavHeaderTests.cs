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

    /// <summary>Та же раскладка, но 8 кГц: 32000 байт/с и 4 байта на кадр.</summary>
    private static WavHeader CreatePcm16Stereo8k(uint dataSize)
    {
        WavHeader header = CreatePcm16Stereo(dataSize);
        header.SampleRate = 8000;
        header.ByteRate = 8000 * 4;
        return header;
    }

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

    [Fact]
    public void Validate_ExtensibleWithUnknownSubFormat_ThrowsNotSupported()
    {
        WavHeader header = CreatePcm16Stereo(1000);
        header.AudioFormat = WaveFormatType.Extensible;
        header.SubFormatGuid = Guid.NewGuid();

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

    [Fact]
    public void FormatFlags_ResolveExtensiblePcm()
    {
        WavHeader header = CreatePcm16Stereo(1000);
        header.AudioFormat = WaveFormatType.Extensible;
        header.SubFormatGuid = WavHeader.PcmSubtypeGuid;

        Assert.True(header.IsExtensible);
        Assert.True(header.IsPcm);
        Assert.True(header.IsIntegerPcm);
        Assert.False(header.IsIeeeFloat);
        Assert.False(header.IsFloat);
    }

    [Fact]
    public void FormatFlags_ResolveExtensibleFloat()
    {
        WavHeader header = CreatePcm16Stereo(1000);
        header.AudioFormat = WaveFormatType.Extensible;
        header.SubFormatGuid = WavHeader.IeeeFloatSubtypeGuid;
        header.BitsPerSample = 32;
        header.BlockAlign = 8;

        Assert.True(header.IsExtensible);
        Assert.False(header.IsPcm);
        Assert.False(header.IsIntegerPcm);
        Assert.True(header.IsIeeeFloat);
        Assert.True(header.IsFloat);
    }

    [Fact]
    public void FormatFlags_ResolvePlainPcm()
    {
        WavHeader header = CreatePcm16Stereo(1000);

        Assert.False(header.IsExtensible);
        Assert.True(header.IsPcm);
        Assert.True(header.IsIntegerPcm);
        Assert.False(header.IsFloat);
        Assert.True(header.IsStereo);
        Assert.False(header.IsMono);
    }

    [Fact]
    public void GetBytesPerSecond_IsSampleRateTimesBlockAlign()
    {
        WavHeader header = CreatePcm16Stereo(1000);

        Assert.Equal(44100L * 4, header.GetBytesPerSecond());
        Assert.Equal(2, header.GetBytesPerSample());
        Assert.Equal(2, header.SampleSize);
    }

    [Fact]
    public void CalculateCutOffsets_KnownSize_ConvertsSecondsToBytes()
    {
        WavHeader header = CreatePcm16Stereo((uint)BytesPerSecond);   // 1 секунда данных

        (long from, long to) = header.CalculateCutOffsets(TimeRange.Seconds(0.25, 0.5));

        Assert.Equal(DataOffset + BytesPerSecond / 4, from);
        Assert.Equal(DataOffset + BytesPerSecond / 2, to);
    }

    [Fact]
    public void CalculateCutOffsets_OpenEndedRange_ExtendsToTheEndOfTheData()
    {
        WavHeader header = CreatePcm16Stereo((uint)BytesPerSecond);

        (_, long to) = header.CalculateCutOffsets(TimeRange.Seconds(0.5));

        Assert.Equal(DataOffset + BytesPerSecond, to);
    }

    [Fact]
    public void CalculateCutOffsets_RangePastTheEnd_IsClampedToTheData()
    {
        WavHeader header = CreatePcm16Stereo((uint)BytesPerSecond);

        (long from, long to) = header.CalculateCutOffsets(TimeRange.Seconds(5, 10));

        Assert.Equal(DataOffset + BytesPerSecond, from);
        Assert.Equal(DataOffset + BytesPerSecond, to);
    }

    [Fact]
    public void CalculateCutOffsets_NegativeRange_IsClampedToTheDataStart()
    {
        WavHeader header = CreatePcm16Stereo((uint)BytesPerSecond);

        (long from, _) = header.CalculateCutOffsets(TimeRange.Seconds(-5, 0.25));

        Assert.Equal(DataOffset, from);
    }

    [Fact]
    public void CalculateCutOffsets_UnalignedRange_IsAlignedToWholeFrames()
    {
        // 8 кГц, стерео 16 бит: bytesPerSecond = 32000, кадр = 4 байта.
        // 0.1 мс = 3.2 байта → 3 байта, что не кратно кадру.
        WavHeader header = CreatePcm16Stereo8k(32_000);
        TimeRange unaligned = TimeRange.Create(TimeSpan.Zero, TimeSpan.FromMilliseconds(0.1));

        (long from, long to) = header.CalculateCutOffsets(unaligned, alignToFrames: true);

        Assert.Equal(DataOffset, from);
        Assert.Equal(DataOffset, to);
    }

    [Fact]
    public void CalculateCutOffsets_AlignmentDisabled_KeepsRawOffsets()
    {
        WavHeader header = CreatePcm16Stereo8k(32_000);
        TimeRange unaligned = TimeRange.Create(TimeSpan.Zero, TimeSpan.FromMilliseconds(0.1));

        (long from, long to) = header.CalculateCutOffsets(unaligned, alignToFrames: false);

        Assert.Equal(DataOffset, from);
        Assert.Equal(DataOffset + 3, to);
    }

    [Fact]
    public void ToString_DescribesFormatChannelsAndDuration()
    {
        WavHeader header = CreatePcm16Stereo((uint)BytesPerSecond);

        string text = header.ToString();

        Assert.Contains("PCM", text, StringComparison.Ordinal);
        Assert.Contains("Stereo", text, StringComparison.Ordinal);
        Assert.Contains("44,100 Hz", text, StringComparison.Ordinal);
        Assert.Contains("16-bit", text, StringComparison.Ordinal);
        Assert.Contains("0:00:01", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_ExtensibleFloat_ReportsBothFormatAndSubFormat()
    {
        WavHeader header = CreatePcm16Stereo(1000);
        header.AudioFormat = WaveFormatType.Extensible;
        header.SubFormatGuid = WavHeader.IeeeFloatSubtypeGuid;
        header.BitsPerSample = 32;
        header.BlockAlign = 8;

        Assert.Contains("Extensible Float", header.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_ExtensiblePcm_ReportsExtensiblePcm()
    {
        WavHeader header = CreatePcm16Stereo(1000);
        header.AudioFormat = WaveFormatType.Extensible;
        header.SubFormatGuid = WavHeader.PcmSubtypeGuid;

        Assert.Contains("Extensible PCM", header.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_PlainIeeeFloat_ReportsIeeeFloat()
    {
        WavHeader header = CreatePcm16Stereo(1000);
        header.AudioFormat = WaveFormatType.IeeeFloat;
        header.BitsPerSample = 32;
        header.BlockAlign = 8;

        Assert.Contains("IEEE Float", header.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="WavHeader.GetNormalizedConverter"/> — публичный вход в <c>SampleConverter</c>
    /// для каждой поддерживаемой пары (формат, разрядность). Тишина в целевом формате обязана
    /// читаться как 0.0 (для 8-битного unsigned середина шкалы — 128).
    /// </summary>
    [Theory]
    [InlineData(WaveFormatType.Pcm, 8, 1)]
    [InlineData(WaveFormatType.Pcm, 16, 2)]
    [InlineData(WaveFormatType.Pcm, 24, 3)]
    [InlineData(WaveFormatType.Pcm, 32, 4)]
    [InlineData(WaveFormatType.IeeeFloat, 32, 4)]
    [InlineData(WaveFormatType.IeeeFloat, 64, 8)]
    public void GetNormalizedConverter_MapsSilenceToZero(
        WaveFormatType format, ushort bitsPerSample, ushort bytesPerSample)
    {
        WavHeader header = CreatePcm16Stereo(1000);
        header.AudioFormat = format;
        header.BitsPerSample = bitsPerSample;
        header.BlockAlign = (ushort)(header.NumChannels * bytesPerSample);

        byte[] silence = new byte[bytesPerSample];
        if (format == WaveFormatType.Pcm && bitsPerSample == 8)
            silence[0] = 128;   // unsigned mid-scale

        Assert.Equal(0.0, header.GetNormalizedConverter()(silence), 1e-2);
    }

    [Fact]
    public void ToString_Mono_ReportsMono()
    {
        WavHeader header = CreatePcm16Stereo(1000);
        header.NumChannels = 1;
        header.BlockAlign = (ushort)header.GetBytesPerSample();

        Assert.Contains("(Mono)", header.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_UnrecognisedFormat_PrintsTheRawFormatName()
    {
        WavHeader header = CreatePcm16Stereo(1000);
        header.AudioFormat = WaveFormatType.Adpcm;

        Assert.Contains("Adpcm", header.ToString(), StringComparison.Ordinal);
    }
}
