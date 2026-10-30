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
