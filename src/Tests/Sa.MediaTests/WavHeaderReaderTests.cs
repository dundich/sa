using Sa.Media;
using System.IO.Pipelines;

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

    /// <summary>
    /// Чанк нулевого размера легален по RIFF (пустые <c>LIST</c>/<c>ID3 </c>/<c>bext</c> пишут
    /// реальные кодировщики). Раньше такой файл отвергался целиком с «zero-size chunk»,
    /// хотя заявлено, что метаданные пропускаются прозрачно.
    /// </summary>
    [Fact]
    public async Task GetHeaderAsync_ZeroSizeChunkBeforeData_IsSkipped()
    {
        byte[] bytes = BuildWav(dataSize: 8, preDataChunkId: "LIST", preDataChunkSize: 0);

        await using AsyncWavReader reader = CreateReader(bytes);
        WavHeader header = await reader.GetHeaderAsync(TestContext.Current.CancellationToken);

        // 12 (RIFF/WAVE) + 8 + 16 (fmt) + 8 (пустой LIST) + 8 (id/size самого data) = 52
        Assert.Equal(52u, header.DataOffset);
        Assert.Equal(8u, header.DataSize);
    }

    [Fact]
    public async Task GetHeaderAsync_JunkBeforeFmtChunk_IsSkipped()
    {
        byte[] bytes = BuildWav(dataSize: 8, junkBeforeFmt: true);

        await using AsyncWavReader reader = CreateReader(bytes);
        WavHeader header = await reader.GetHeaderAsync(TestContext.Current.CancellationToken);

        // 12 (RIFF/WAVE) + 12 (JUNK: id+size+4 байта) + 8 + 16 (fmt) + 8 (data id/size) = 56
        Assert.Equal(56u, header.DataOffset);
        Assert.Equal(8u, header.DataSize);
    }

    [Fact]
    public async Task GetHeaderAsync_ExtensibleFloat_IsResolvedThroughSubFormat()
    {
        byte[] bytes = BuildWav(
            fmtSize: 40,
            audioFormat: (ushort)WaveFormatType.Extensible,
            blockAlign: 8,
            bitsPerSample: 32,
            dataSize: 16,
            subFormatGuid: WavHeader.IeeeFloatSubtypeGuid);

        await using AsyncWavReader reader = CreateReader(bytes);
        WavHeader header = await reader.GetHeaderAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WaveFormatType.Extensible, header.AudioFormat);
        Assert.Equal(WavHeader.IeeeFloatSubtypeGuid, header.SubFormatGuid);
        Assert.True(header.IsIeeeFloat);
        Assert.True(header.IsFloat);
        Assert.False(header.IsPcm);
        Assert.Equal(32, header.ValidBitsPerSample);
    }

    [Fact]
    public async Task GetHeaderAsync_ExtensibleWithExtraBytesAfterSubFormat_AreSkipped()
    {
        // fmt больше 40 байт: после SubFormat идёт хвост, который парсер обязан пропустить,
        // иначе data-чанк ищется не с того места.
        byte[] bytes = BuildWav(
            fmtSize: 44,
            audioFormat: (ushort)WaveFormatType.Extensible,
            blockAlign: 4,
            dataSize: 8,
            subFormatGuid: WavHeader.PcmSubtypeGuid);

        await using AsyncWavReader reader = CreateReader(bytes);
        WavHeader header = await reader.GetHeaderAsync(TestContext.Current.CancellationToken);

        Assert.Equal(12u + 8 + 44 + 8, header.DataOffset);
        Assert.Equal(8u, header.DataSize);
    }

    [Fact]
    public async Task GetHeaderAsync_ExtensibleWithUnknownSubFormat_ThrowsNotSupported()
    {
        byte[] bytes = BuildWav(
            fmtSize: 40,
            audioFormat: (ushort)WaveFormatType.Extensible,
            blockAlign: 4,
            dataSize: 8,
            subFormatGuid: Guid.Parse("11111111-2222-3333-4444-555555555555"));

        await AssertHeaderFailureAsync<NotSupportedException>(bytes);
    }

    [Fact]
    public async Task GetHeaderAsync_ExtensibleWithTooShortFmt_ThrowsInvalidData()
    {
        byte[] bytes = BuildWav(
            fmtSize: 18,
            audioFormat: (ushort)WaveFormatType.Extensible,
            blockAlign: 4,
            dataSize: 8,
            subFormatGuid: null);

        await AssertHeaderFailureAsync<InvalidDataException>(bytes);
    }

    [Fact]
    public async Task GetHeaderAsync_ExtraBytesInPlainFmtChunk_AreSkipped()
    {
        // Не-extensible fmt объявлен на 20 байт: 4 лишних байта пропускаются.
        byte[] bytes = BuildWav(fmtSize: 20, blockAlign: 4, dataSize: 8);

        await using AsyncWavReader reader = CreateReader(bytes);
        WavHeader header = await reader.GetHeaderAsync(TestContext.Current.CancellationToken);

        Assert.Equal(12u + 8 + 20 + 8, header.DataOffset);
        Assert.Equal(8u, header.DataSize);
    }

    [Fact]
    public async Task GetHeaderAsync_ZeroChannels_ThrowsNotSupported()
    {
        byte[] bytes = BuildWav(channels: 0, blockAlign: 0, dataSize: 8);

        await AssertHeaderFailureAsync<NotSupportedException>(bytes);
    }

    [Fact]
    public async Task GetHeaderAsync_ZeroSampleRate_ThrowsInvalidData()
    {
        byte[] bytes = BuildWav(sampleRate: 0, dataSize: 8);

        await AssertHeaderFailureAsync<InvalidDataException>(bytes);
    }

    [Fact]
    public async Task GetHeaderAsync_Adpcm_ThrowsNotSupported()
    {
        byte[] bytes = BuildWav(audioFormat: (ushort)WaveFormatType.Adpcm, dataSize: 8);

        await AssertHeaderFailureAsync<NotSupportedException>(bytes);
    }

    [Fact]
    public async Task GetHeaderAsync_ZeroSizeChunkWithEvenPadding_KeepsFollowingChunksAligned()
    {
        // Непустой чанк нечётного размера дополняется до чётного — data после него читается.
        byte[] bytes = BuildWav(dataSize: 8, preDataChunkId: "JUNK", preDataChunkSize: 3);

        await using AsyncWavReader reader = CreateReader(bytes);
        WavHeader header = await reader.GetHeaderAsync(TestContext.Current.CancellationToken);

        Assert.Equal(56u, header.DataOffset);   // 52 + 3 байта payload + 1 байт выравнивания
        Assert.Equal(8u, header.DataSize);
    }

    /// <summary>
    /// Многосегментная последовательность уже на заголовке: при сегментах по 2 байта
    /// chunkId/data-смещения приходят несколькими сегментами. Раньше этот путь разбора
    /// (<see cref="System.Buffers.ReadOnlySequence{T}"/> → stackalloc) не покрывался
    /// ни одним тестом — а он и есть обычный случай для сетевого потока.
    /// </summary>
    [Fact]
    public async Task GetHeaderAsync_MultiSegmentPipe_ParsesAcrossSegments()
    {
        byte[] bytes = BuildWav(dataSize: 8);

        var pipe = new Pipe(new PipeOptions(minimumSegmentSize: 2));
        await pipe.Writer.WriteAsync(bytes, TestContext.Current.CancellationToken);
        await pipe.Writer.CompleteAsync();

        await using var reader = new AsyncWavReader(pipe.Reader);
        WavHeader header = await reader.GetHeaderAsync(TestContext.Current.CancellationToken);

        Assert.Equal(44100u, header.SampleRate);
        Assert.Equal(8u, header.DataSize);
        Assert.Equal(12u + 8 + 16 + 8, header.DataOffset);
    }

    [Fact]
    public async Task GetHeaderAsync_MetadataChunkLongerThanFile_ThrowsInvalidData()
    {
        // LIST объявляет 1000 байт, но файл кончается на 4: SkipBytesAsync вернёт меньше
        // запрошенного и обязан превратиться в InvalidDataException, а не в тихое смещение.
        byte[] bytes = BuildWav(
            preDataChunkId: "LIST",
            preDataChunkSize: 4,
            preDataChunkDeclaredSize: 1000);

        await AssertHeaderFailureAsync<InvalidDataException>(bytes);
    }

    [Fact]
    public async Task GetHeaderAsync_JunkFollowedByNonFmtChunk_ThrowsInvalidData()
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("RIFF"u8.ToArray());
            writer.Write(0);
            writer.Write("WAVE"u8.ToArray());
            writer.Write("JUNK"u8.ToArray());
            writer.Write(4u);
            writer.Write(new byte[4]);
            writer.Write("data"u8.ToArray());   // вместо fmt
            writer.Write(0u);
        }

        await AssertHeaderFailureAsync<InvalidDataException>(stream.ToArray());
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
        bool includeDataChunk = true,
        string? preDataChunkId = null,
        uint preDataChunkSize = 0,
        uint? preDataChunkDeclaredSize = null,
        bool junkBeforeFmt = false,
        Guid? subFormatGuid = null)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8.ToArray());

        if (junkBeforeFmt)
        {
            writer.Write("JUNK"u8.ToArray());
            writer.Write(4u);
            writer.Write(new byte[4]);
        }

        writer.Write("fmt "u8.ToArray());
        writer.Write(fmtSize);
        writer.Write(audioFormat);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * (bitsPerSample / 8u));
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);

        bool hasExtension = audioFormat == (ushort)WaveFormatType.Extensible
            && fmtSize >= 40
            && subFormatGuid.HasValue;

        if (hasExtension)
        {
            // WAVEFORMATEXTENSIBLE: cbSize(2) + wValidBitsPerSample(2) + dwChannelMask(4) + SubFormat(16)
            writer.Write((ushort)22);
            writer.Write(bitsPerSample);
            writer.Write(0x3);   // FL|FR
            writer.Write(subFormatGuid!.Value.ToByteArray());
        }

        int payloadWritten = hasExtension ? 40 : 16;
        for (int i = payloadWritten; i < fmtSize; i++)
            writer.Write((byte)0);

        if (preDataChunkId is not null)
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes(preDataChunkId.PadRight(4)[..4]));
            writer.Write(preDataChunkDeclaredSize ?? preDataChunkSize);
            // RIFF требует выравнивания чанка на чётную границу.
            writer.Write(new byte[(int)preDataChunkSize + (int)(preDataChunkSize % 2)]);
        }

        if (includeDataChunk)
        {
            writer.Write("data"u8.ToArray());
            writer.Write(dataSize);
        }

        writer.Flush();
        return stream.ToArray();
    }
}
