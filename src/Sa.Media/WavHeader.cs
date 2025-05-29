namespace Sa.Media;

/// <summary>
/// Представляет формат аудио в WAV-файле.
/// </summary>
public enum WaveFormatType : ushort
{
    /// <summary>
    /// PCM (Pulse Code Modulation) — стандартный несжатый формат
    /// </summary>
    Pcm = 0x0001,

    /// <summary>
    /// ADPCM (Adaptive Differential Pulse Code Modulation) — сжатый формат
    /// </summary>
    Adpcm = 0x0002,

    /// <summary>
    /// IEEE Float — 32- или 64-битные float значения
    /// </summary>
    IeeeFloat = 0x0003,

    /// <summary>
    /// WAVE_FORMAT_EXTENSIBLE — расширенный формат WAV (для многоканального звука)
    /// </summary>
    Extensible = 0xFFFE
}

/// <summary>
/// Представляет заголовок WAV-файла и предоставляет информацию о его содержимом.
/// Поддерживает проверку корректности и удобное отображение данных.
/// </summary>
/// <seealso href="https://audiocoding.cc/articles/2008-05-22-wav-file-structure/ "/>
/// <seealso href="https://hasan-hasanov.com/post/2023/10/how_to_parse_wav_file/ "/>
public class WavHeader
{
    // === RIFF Chunk ===

    /// <summary>
    /// Идентификатор чанка RIFF ("RIFF")
    /// </summary>
    public uint ChunkId { get; set; }

    /// <summary>
    /// Размер всего файла минус первые 8 байт (ChunkId + ChunkSize)
    /// </summary>
    public uint ChunkSize { get; set; }

    /// <summary>
    /// Формат файла ("WAVE")
    /// </summary>
    public uint Format { get; set; }

    // === fmt Subchunk ===

    /// <summary>
    /// Идентификатор подчанка формата ("fmt ")
    /// </summary>
    public uint Subchunk1Id { get; set; }

    /// <summary>
    /// Размер подчанка формата (обычно 16, но может быть больше)
    /// </summary>
    public uint Subchunk1Size { get; set; }

    /// <summary>
    /// Тип аудиоформата: PCM, IEEE Float и т.д.
    /// </summary>
    public WaveFormatType AudioFormat { get; set; }

    /// <summary>
    /// Количество каналов (моно=1, стерео=2 и т.д.)
    /// </summary>
    public ushort NumChannels { get; set; }

    /// <summary>
    /// Частота дискретизации (Sample Rate): например, 44100 Гц
    /// </summary>
    public uint SampleRate { get; set; }

    /// <summary>
    /// Байт в секунду (Byte Rate): SampleRate * NumChannels * BitsPerSample / 8
    /// </summary>
    public uint ByteRate { get; set; }

    /// <summary>
    /// Выравнивание блока (Block Align): NumChannels * BitsPerSample / 8
    /// </summary>
    public ushort BlockAlign { get; set; }

    /// <summary>
    /// Битность семпла (Bit Depth): 8, 16, 24, 32 или 64 бита
    /// </summary>
    public ushort BitsPerSample { get; set; }

    // === data Subchunk ===

    /// <summary>
    /// Идентификатор данных ("data")
    /// </summary>
    public uint Subchunk2Id { get; set; }

    /// <summary>
    /// Размер данных в байтах
    /// </summary>
    public uint Subchunk2Size { get; set; }

    /// <summary>
    /// Смещение до начала данных
    /// </summary>
    public long DataOffset { get; set; }

    /// <summary>
    /// Проверяет, что заголовок содержит поддерживаемый формат PCM или IEEE Float
    /// </summary>
    public void Validate()
    {
        if (AudioFormat is not (WaveFormatType.Pcm or WaveFormatType.IeeeFloat))
            throw new NotSupportedException($"Unsupported audio format: {AudioFormat}");

        if (BitsPerSample is not (8 or 16 or 24 or 32 or 64))
            throw new NotSupportedException($"Unsupported bits per sample: {BitsPerSample}");

        if (NumChannels == 0 || NumChannels > 6)
            throw new NotSupportedException($"Unsupported number of channels: {NumChannels}");

        if (SampleRate == 0)
            throw new InvalidDataException("Sample rate is zero");

        if (Subchunk2Size == 0)
            throw new InvalidDataException("No data found in the file");
    }

    /// <summary>
    /// Возвращает длительность файла в секундах
    /// </summary>
    public double GetDurationInSeconds()
    {
        if (BitsPerSample == 0 || NumChannels == 0)
            return 0;

        long bytesPerChannel = (long)Subchunk2Size / NumChannels;
        long samplesPerChannel = bytesPerChannel / (BitsPerSample / 8);
        return SampleRate != 0 ? samplesPerChannel / (double)SampleRate : 0;
    }

    /// <summary>
    /// Возвращает длительность файла как TimeSpan
    /// </summary>
    public TimeSpan GetDuration() => TimeSpan.FromSeconds(GetDurationInSeconds());

    /// <summary>
    /// Возвращает размер одного семпла на один канал в байтах
    /// </summary>
    public int GetBytesPerSamplePerChannel() => BitsPerSample / 8;

    /// <summary>
    /// Возвращает true, если формат — PCM (Pulse Code Modulation)
    /// </summary>
    public bool IsPcm => AudioFormat == WaveFormatType.Pcm;

    /// <summary>
    /// Возвращает true, если формат — IEEE Float (32 или 64 бита)
    /// </summary>
    public bool IsIeeeFloat => AudioFormat == WaveFormatType.IeeeFloat;

    /// <summary>
    /// Возвращает true, если файл монофонический (один канал)
    /// </summary>
    public bool IsMono => NumChannels == 1;

    /// <summary>
    /// Возвращает true, если файл стереофонический (два канала)
    /// </summary>
    public bool IsStereo => NumChannels == 2;

    /// <summary>
    /// Размер одного семпла для всех каналов (в байтах)
    /// </summary>
    public int SampleSize => BlockAlign / NumChannels;

    /// <summary>
    /// Возвращает человекочитаемое представление заголовка
    /// </summary>
    public override string ToString()
    {
        var stereo = IsStereo ? "(Stereo)" : "";
        var ieefloat = IsIeeeFloat ? "FLOAT" : AudioFormat.ToString();

        return $"""
    [WAV Header]

    Format: {(IsPcm ? "PCM" : ieefloat)}
    Channels: {NumChannels} {(IsMono ? "(Mono)" : stereo)}
    Sample Rate: {SampleRate} Hz
    Bit Depth: {BitsPerSample}-bit
    Duration: {GetDuration():g}
    """;
    }
}