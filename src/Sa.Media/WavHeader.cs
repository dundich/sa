namespace Sa.Media;

/// <summary>
/// Представляет заголовок WAV-файла и предоставляет информацию о его содержимом.
/// Поддерживает проверку корректности и удобное отображение данных.
/// </summary>
/// <seealso href="https://audiocoding.cc/articles/2008-05-22-wav-file-structure/"/> 
/// <seealso href="https://hasan-hasanov.com/post/2023/10/how_to_parse_wav_file/"/> 
public class WavHeader
{
    // === RIFF Chunk ===

    public uint ChunkId { get; set; }
    public uint ChunkSize { get; set; }
    public uint Format { get; set; }

    // === fmt Subchunk ===

    public uint Subchunk1Id { get; set; }
    public uint Subchunk1Size { get; set; }
    public WaveFormatType AudioFormat { get; set; }
    public ushort NumChannels { get; set; }
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

    public uint Subchunk2Id { get; set; }
    public uint Subchunk2Size { get; set; }
    public long DataOffset { get; set; }

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

        if (Subchunk2Size == 0)
            throw new InvalidDataException("No data found in the file");
    }

    public double GetDurationInSeconds()
    {
        if (BitsPerSample == 0 || NumChannels == 0)
            return 0;

        long bytesPerChannel = (long)Subchunk2Size / NumChannels;
        long samplesPerChannel = bytesPerChannel / (BitsPerSample / 8);
        return SampleRate != 0 ? samplesPerChannel / (double)SampleRate : 0;
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
Data Size:      {Subchunk2Size} bytes
""";

        string isFloat() => (IsIeeeFloat ? "FLOAT" : AudioFormat.ToString());
        string isStereo() => (IsStereo ? "Stereo" : String.Empty);

    }
}