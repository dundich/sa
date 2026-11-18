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

    /// <summary>RIFF chunk identifier ("RIFF").</summary>
    public uint ChunkId { get; set; }
    /// <summary>
    /// общий размер данных RIFF (всего файла - 8)
    /// </summary>
    public uint ChunkSize { get; set; }
    /// <summary>Format identifier ("WAVE").</summary>
    public uint Format { get; set; }

    // === fmt Subchunk ===

    /// <summary>Format subchunk identifier ("fmt ").</summary>
    public uint Subchunk1Id { get; set; }
    /// <summary>Size of the format subchunk, in bytes.</summary>
    public uint Subchunk1Size { get; set; }
    /// <summary>Audio format tag from the format subchunk.</summary>
    public WaveFormatType AudioFormat { get; set; }
    /// <summary>
    /// количество каналов (1 = моно, 2 = стерео)
    /// </summary>
    public ushort NumChannels { get; set; }
    /// <summary>
    /// частота дискретизации (например, 16000 Гц)
    /// </summary>
    public uint SampleRate { get; set; }
    /// <summary>Average number of bytes per second.</summary>
    public uint ByteRate { get; set; }
    /// <summary>
    /// Количество байт для одного сэмпла, включая все каналы.
    ///  numChannels * bitsPerSample/8
    /// </summary>
    public ushort BlockAlign { get; set; }
    /// <summary>Number of bits per sample, including padding.</summary>
    public ushort BitsPerSample { get; set; }

    // Расширения для WAVE_FORMAT_EXTENSIBLE
    /// <summary>Size of the extension block, in bytes.</summary>
    public ushort ExtensionSize { get; set; }
    /// <summary>Number of valid bits per sample in the extension block.</summary>
    public ushort ValidBitsPerSample { get; set; }
    /// <summary>Speaker position mask from the extension block.</summary>
    public int ChannelMask { get; set; }
    /// <summary>Sub-format GUID identifying the sample format for extensible headers.</summary>
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

    /// <summary>
    /// Validates that the header describes a supported audio format.
    /// </summary>
    /// <exception cref="NotSupportedException">The audio format, channel count or bit depth is not supported.</exception>
    /// <exception cref="InvalidDataException">The header values are inconsistent.</exception>
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
        {
            throw new NotSupportedException(
                $"Unsupported bits per sample: {BitsPerSample} for {EffectiveAudioFormat} format");
        }

        // BlockAlign — единственный источник правды для шага кадра в читателе
        // (SampleSize = BlockAlign / NumChannels). Несогласованный BlockAlign даёт
        // «сэмплы» неверного размера вместо понятной ошибки.
        int bytesPerSample = BitsPerSample / 8;
        int expectedBlockAlign = NumChannels * bytesPerSample;
        if (BlockAlign != expectedBlockAlign)
        {
            throw new InvalidDataException(
                $"Invalid WAV file: block align {BlockAlign} does not match {NumChannels} channel(s) x {bytesPerSample} byte(s)");
        }
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

    /// <summary>
    /// Gets the duration of the audio data in seconds.
    /// </summary>
    /// <param name="fileSize">Optional total file size, used for streamed files without a data size.</param>
    /// <returns>The duration in seconds, or 0 when it cannot be determined.</returns>
    public double GetDurationInSeconds(long? fileSize = default)
    {
        long dataSize = ResolveDataSize(fileSize);

        // Потоковая запись без известной длины не имеет длительности, а не «13 часов».
        if (dataSize <= 0 || SampleRate == 0 || BlockAlign == 0)
            return 0;

        long frames = dataSize / BlockAlign;
        return frames / (double)SampleRate;
    }

    /// <summary>
    /// Gets the duration of the audio data.
    /// </summary>
    /// <returns>The duration as a <see cref="TimeSpan"/>.</returns>
    public TimeSpan GetDuration() => TimeSpan.FromSeconds(GetDurationInSeconds());

    /// <summary>
    /// Gets the number of bytes occupied by a single sample.
    /// </summary>
    /// <returns>The sample size in bytes.</returns>
    public int GetBytesPerSample() => BitsPerSample / 8;

    /// <summary>Gets a value indicating whether the samples are integer PCM.</summary>
    public bool IsPcm => AudioFormat == WaveFormatType.Pcm
        || (AudioFormat == WaveFormatType.Extensible && SubFormatGuid == PcmSubtypeGuid);
    /// <summary>Gets a value indicating whether the samples are IEEE floating point.</summary>
    public bool IsIeeeFloat => AudioFormat == WaveFormatType.IeeeFloat
        || (AudioFormat == WaveFormatType.Extensible && SubFormatGuid == IeeeFloatSubtypeGuid);
    /// <summary>Gets a value indicating whether the samples are floating point.</summary>
    public bool IsFloat => IsIeeeFloat;
    /// <summary>Gets a value indicating whether the samples are integer PCM.</summary>
    public bool IsIntegerPcm => IsPcm;
    /// <summary>Gets a value indicating whether the header uses the extensible format.</summary>
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

    /// <summary>Gets a value indicating whether the audio is mono.</summary>
    public bool IsMono => NumChannels == 1;
    /// <summary>Gets a value indicating whether the audio is stereo.</summary>
    public bool IsStereo => NumChannels == 2;

    /// <summary>Gets the size of a single-channel sample, in bytes.</summary>
    public int SampleSize => BlockAlign / NumChannels;

    /// <summary>
    /// Для потоковых данных может иметь максимальный размер
    /// </summary>
    public bool HasDataSize => DataSize != uint.MaxValue;

    /// <summary>
    /// Returns a human-readable description of the header.
    /// </summary>
    /// <returns>A formatted multi-line description of the header.</returns>
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

    /// <summary>
    /// Calculates the byte offsets of the given time range within the audio data.
    /// </summary>
    /// <param name="range">The time range to convert.</param>
    /// <param name="fileSize">Optional total file size, used for streamed files without a data size.</param>
    /// <param name="alignToFrames">If true, the offsets are aligned to whole frames.</param>
    /// <returns>The start and end offsets of the range, clamped to the audio data.</returns>
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


    /// <summary>
    /// Gets a converter that normalizes raw samples to the [-1.0, 1.0] range.
    /// </summary>
    /// <returns>A delegate converting a raw sample to a double.</returns>
    public Func<ReadOnlySpan<byte>, double> GetNormalizedConverter()
        => SampleConverter.GetNormalizedConverter(BitsPerSample, EffectiveAudioFormat);

    /// <summary>
    /// Float-вариант <see cref="GetNormalizedConverter"/> для DSP-путей: без промежуточного double.
    /// </summary>
    internal Func<ReadOnlySpan<byte>, float> GetNormalizedFloatConverter()
        => SampleConverter.GetNormalizedFloatConverter(BitsPerSample, EffectiveAudioFormat);

}
