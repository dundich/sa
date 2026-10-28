namespace Sa.Media;

/// <summary>
/// Представляет заголовок WAV-файла и предоставляет информацию о его содержимом.
/// Поддерживает проверку корректности и удобное отображение данных.
/// </summary>
/// <seealso href="https://audiocoding.cc/articles/2008-05-22-wav-file-structure/"/> 
/// <seealso href="https://hasan-hasanov.com/post/2023/10/how_to_parse_wav_file/"/> 
public sealed class WavHeader
{
    // SubFormat-GUID из WAVE_FORMAT_EXTENSIBLE (KSDATAFORMAT_SUBTYPE_*)
    internal static readonly Guid PcmSubtypeGuid = new("00000001-0000-0010-8000-00aa00389b71");
    internal static readonly Guid IeeeFloatSubtypeGuid = new("00000003-0000-0010-8000-00aa00389b71");

    // === RIFF Chunk ===

    public uint ChunkId { get; set; }
    /// <summary>
    /// общий размер данных RIFF (всего файла - 8)
    /// </summary>
    public uint ChunkSize { get; set; }
    public uint Format { get; set; }

    // === fmt Subchunk ===

    public uint Subchunk1Id { get; set; }
    public uint Subchunk1Size { get; set; }
    public WaveFormatType AudioFormat { get; set; }
    /// <summary>
    /// количество каналов (1 = моно, 2 = стерео)
    /// </summary>
    public ushort NumChannels { get; set; }
    /// <summary>
    /// частота дискретизации (например, 16000 Гц)
    /// </summary>
    public uint SampleRate { get; set; }
    public uint ByteRate { get; set; }
    /// <summary>
    /// Количество байт для одного сэмпла, включая все каналы.
    ///  numChannels * bitsPerSample/8
    /// </summary>
    public ushort BlockAlign { get; set; }
    public ushort BitsPerSample { get; set; }

    // Расширения для WAVE_FORMAT_EXTENSIBLE
    public ushort ExtensionSize { get; set; }
    public ushort ValidBitsPerSample { get; set; }
    public int ChannelMask { get; set; }
    public Guid SubFormatGuid { get; set; }

    // === data Subchunk ===

    /// <summary>
    /// смещение до начала аудиоданных
    /// </summary>
    public uint DataOffset { get; set; }
    /// <summary>
    /// Размер данных (для неопределленых (потоковых) => uint.MaxValue)
    /// </summary>
    public uint DataSize { get; set; }

    public void Validate()
    {
        if (AudioFormat is not (WaveFormatType.Pcm or WaveFormatType.IeeeFloat or WaveFormatType.Extensible))
            throw new NotSupportedException($"Unsupported audio format: {AudioFormat}");

        if (AudioFormat == WaveFormatType.Extensible && !IsPcm && !IsIeeeFloat)
            throw new NotSupportedException("Extended WAV format with unknown subformat is not supported");

        if (NumChannels == 0 || NumChannels > 6)
            throw new NotSupportedException($"Unsupported number of channels: {NumChannels}");

        if (SampleRate == 0)
            throw new InvalidDataException("Sample rate is zero");

        // Поддерживаемые пары (формат, разрядность). Проверяем здесь, а не в конвертере:
        // иначе PCM 64-bit или IEEE float 8-bit проходят разбор заголовка и падают уже
        // в середине чтения сэмплов.
        bool supportedBitsPerSample = EffectiveAudioFormat switch
        {
            WaveFormatType.Pcm => BitsPerSample is 8 or 16 or 24 or 32,
            WaveFormatType.IeeeFloat => BitsPerSample is 32 or 64,
            _ => false
        };

        if (!supportedBitsPerSample)
            throw new NotSupportedException(
                $"Unsupported bits per sample: {BitsPerSample} for {EffectiveAudioFormat} format");

        // BlockAlign — единственный источник правды для шага кадра в читателе
        // (SampleSize = BlockAlign / NumChannels). Несогласованный BlockAlign даёт
        // «сэмплы» неверного размера вместо понятной ошибки.
        int bytesPerSample = BitsPerSample / 8;
        int expectedBlockAlign = NumChannels * bytesPerSample;
        if (BlockAlign != expectedBlockAlign)
            throw new InvalidDataException(
                $"Invalid WAV file: block align {BlockAlign} does not match {NumChannels} channel(s) x {bytesPerSample} byte(s)");
    }

    /// <summary>
    /// Реальный размер данных: из заголовка, а для потоковых записей
    /// (<see cref="DataSize"/> == <see cref="uint.MaxValue"/>) — из длины файла, если она известна.
    /// </summary>
    /// <returns>Размер в байтах или 0, если определить его нельзя.</returns>
    internal long ResolveDataSize(long? fileSize = null)
    {
        if (HasDataSize)
            return DataSize;

        if (fileSize is not { } length || length <= DataOffset)
            return 0;

        return length - DataOffset;
    }

    public double GetDurationInSeconds(long? fileSize = default)
    {
        long dataSize = ResolveDataSize(fileSize);

        // Потоковая запись без известной длины не имеет длительности, а не «13 часов».
        if (dataSize <= 0 || SampleRate == 0 || BlockAlign == 0)
            return 0;

        long frames = dataSize / BlockAlign;
        return frames / (double)SampleRate;
    }

    public TimeSpan GetDuration() => TimeSpan.FromSeconds(GetDurationInSeconds());

    public int GetBytesPerSample() => BitsPerSample / 8;

    public bool IsPcm => AudioFormat == WaveFormatType.Pcm
        || (AudioFormat == WaveFormatType.Extensible && SubFormatGuid == PcmSubtypeGuid);
    public bool IsIeeeFloat => AudioFormat == WaveFormatType.IeeeFloat
        || (AudioFormat == WaveFormatType.Extensible && SubFormatGuid == IeeeFloatSubtypeGuid);
    public bool IsFloat => IsIeeeFloat;
    public bool IsIntegerPcm => IsPcm;
    public bool IsExtensible => AudioFormat == WaveFormatType.Extensible;

    /// <summary>
    /// Фактический PCM/float-формат с учётом SubFormat для WAVE_FORMAT_EXTENSIBLE.
    /// Сэмплы extensible-файлов читаются как обычный PCM/IEEE float.
    /// </summary>
    internal WaveFormatType EffectiveAudioFormat => AudioFormat switch
    {
        WaveFormatType.Extensible when IsPcm => WaveFormatType.Pcm,
        WaveFormatType.Extensible when IsIeeeFloat => WaveFormatType.IeeeFloat,
        _ => AudioFormat
    };

    public bool IsMono => NumChannels == 1;
    public bool IsStereo => NumChannels == 2;

    public int SampleSize => BlockAlign / NumChannels;

    /// <summary>
    /// Для потоковых данных может иметь максимальный размер
    /// </summary>
    public bool HasDataSize => DataSize != uint.MaxValue;

    public override string ToString()
    {

        var format = AudioFormat switch
        {
            WaveFormatType.Pcm => "PCM",
            WaveFormatType.IeeeFloat => "IEEE Float",
            WaveFormatType.Extensible when IsPcm => "Extensible PCM",
            WaveFormatType.Extensible when IsIeeeFloat => "Extensible Float",
            _ => AudioFormat.ToString()
        };

        var isStereo = IsStereo ? "(Stereo)" : "";
        var stereoOrMono = IsMono ? "(Mono)" : isStereo;

        return $$"""
    [WAV Header]
    
    Format:         {{format}}
    Channels:       {{NumChannels}} {{(stereoOrMono)}}
    Sample Rate:    {{SampleRate:N0}} Hz
    Bit Depth:      {{BitsPerSample}}-bit
    Byte Rate:      {{GetBytesPerSecond():N0}} bytes/sec
    Block Align:    {{BlockAlign}} bytes
    Duration:       {{GetDuration():g}}
    File Size:      {{ChunkSize + 8:N0}} bytes
    Data Offset:    {{DataOffset}} bytes
    """;
    }

    /// <summary>
    /// Возвращает количество байт аудиоданных в одной секунде.
    /// </summary>
    /// <returns>Байт в секунду</returns>
    public long GetBytesPerSecond() => (long)SampleRate * BlockAlign;

    public (long cutFrom, long cutTo) CalculateCutOffsets(
        TimeRange range,
        long? fileSize = null,
        bool alignToFrames = true)
    {
        long dataOffset = DataOffset;

        long bytesPerSecond = GetBytesPerSecond();

        long dataEnd = HasDataSize || fileSize.HasValue
            ? dataOffset + ResolveDataSize(fileSize)
            : dataOffset + (long)TimeSpan.MaxValue.TotalSeconds * bytesPerSecond;


        long fromOffset = dataOffset + (long)(range.From.TotalSeconds * bytesPerSecond);
        long toOffset = range.HasEnd
            ? dataOffset + (long)(range.To.TotalSeconds * bytesPerSecond)
            : dataEnd;

        if (alignToFrames)
        {
            fromOffset = AlignToFrame(fromOffset, dataOffset);
            toOffset = AlignToFrame(toOffset, dataOffset);
        }

        return (Math.Clamp(fromOffset, dataOffset, dataEnd),
                Math.Clamp(toOffset, dataOffset, dataEnd));
    }

    private long AlignToFrame(long offset, long baseOffset)
    {
        if (BlockAlign <= 1) return offset;

        long relativeOffset = offset - baseOffset;
        long alignedRelative = (relativeOffset / BlockAlign) * BlockAlign;
        return baseOffset + alignedRelative;
    }


    public Func<ReadOnlySpan<byte>, double> GetNormalizedConverter()
        => SampleConverter.GetNormalizedConverter(BitsPerSample, EffectiveAudioFormat);

    /// <summary>
    /// Float-вариант <see cref="GetNormalizedConverter"/> для DSP-путей: без промежуточного double.
    /// </summary>
    internal Func<ReadOnlySpan<byte>, float> GetNormalizedFloatConverter()
        => SampleConverter.GetNormalizedFloatConverter(BitsPerSample, EffectiveAudioFormat);

}
