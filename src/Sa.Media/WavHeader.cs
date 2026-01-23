namespace Sa.Media;

/// <summary>
/// Представляет заголовок WAV-файла и предоставляет информацию о его содержимом.
/// Поддерживает проверку корректности и удобное отображение данных.
/// </summary>
/// <seealso href="https://audiocoding.cc/articles/2008-05-22-wav-file-structure/"/> 
/// <seealso href="https://hasan-hasanov.com/post/2023/10/how_to_parse_wav_file/"/> 
public sealed class WavHeader
{
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

        if (BitsPerSample is not (8 or 16 or 24 or 32 or 64))
            throw new NotSupportedException($"Unsupported bits per sample: {BitsPerSample}");

        if (NumChannels == 0 || NumChannels > 6)
            throw new NotSupportedException($"Unsupported number of channels: {NumChannels}");

        if (SampleRate == 0)
            throw new InvalidDataException("Sample rate is zero");
    }

    public double GetDurationInSeconds(long? fileSize = default)
    {
        if (BitsPerSample == 0 || NumChannels == 0 || SampleRate == 0)
            return 0;

        long dataSize = (fileSize >= DataOffset && !HasDataSize) ? fileSize.Value - DataOffset : DataSize;

        long bytesPerChannel = dataSize / NumChannels;
        long samplesPerChannel = bytesPerChannel / (BitsPerSample / 8);
        return samplesPerChannel / (double)SampleRate;
    }

    public TimeSpan GetDuration() => TimeSpan.FromSeconds(GetDurationInSeconds());

    public int GetBytesPerSamplePerChannel() => BitsPerSample / 8;

    public bool IsPcm => AudioFormat == WaveFormatType.Pcm;
    public bool IsIeeeFloat => AudioFormat == WaveFormatType.IeeeFloat;
    public bool IsFloat => IsIeeeFloat;
    public bool IsIntegerPcm => IsPcm;
    public bool IsExtensible => AudioFormat == WaveFormatType.Extensible;

    public bool IsMono => NumChannels == 1;
    public bool IsStereo => NumChannels == 2;

    public int SampleSize => BlockAlign / NumChannels;

    /// <summary>
    /// Для потоковых данных может иметь максимальный размер
    /// </summary>
    public bool HasDataSize => DataSize != uint.MaxValue;

    public override string ToString()
    {
        return $"""
[WAV Header]
            
Format:         {(IsPcm ? "PCM" : isFloat())}
Channels:       {NumChannels} {(IsMono ? "(Mono)" : isStereo())}
Sample Rate:    {SampleRate} Hz
Bit Depth:      {BitsPerSample}-bit
Duration:       {GetDuration():g}
File Size:      {ChunkSize + 8} bytes
Data Size:      {DataSize} bytes
""";

        string isFloat() => (IsIeeeFloat ? "FLOAT" : AudioFormat.ToString());
        string isStereo() => (IsStereo ? "Stereo" : String.Empty);
    }
}
