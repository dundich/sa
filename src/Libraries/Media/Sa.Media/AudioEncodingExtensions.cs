namespace Sa.Media;

/// <summary>
/// Helpers for <see cref="AudioEncoding"/>.
/// </summary>
public static class AudioEncodingExtensions
{
    /// <summary>
    /// Gets the number of bytes occupied by a single sample in the given encoding.
    /// </summary>
    /// <param name="encoding">The audio encoding.</param>
    /// <returns>The size of one sample in bytes.</returns>
    public static int GetBytesPerSample(this AudioEncoding encoding) => encoding switch
    {
        AudioEncoding.Pcm8BitUnsigned => 1,
        AudioEncoding.Pcm16BitSigned => 2,
        AudioEncoding.Pcm24BitSigned => 3,
        AudioEncoding.Pcm32BitSigned or AudioEncoding.IeeeFloat32Bit => 4,
        AudioEncoding.IeeeFloat64Bit => 8,
        _ => throw new NotSupportedException($"Unknown audio encoding: {encoding}")
    };

    /// <summary>
    /// Gets the WAV format type corresponding to the given encoding.
    /// </summary>
    /// <param name="encoding">The audio encoding.</param>
    /// <returns>The matching <see cref="WaveFormatType"/>.</returns>
    public static WaveFormatType GetWaveFormatType(this AudioEncoding encoding) => encoding switch
    {
        AudioEncoding.Pcm8BitUnsigned or
        AudioEncoding.Pcm16BitSigned or
        AudioEncoding.Pcm24BitSigned or
        AudioEncoding.Pcm32BitSigned => WaveFormatType.Pcm,

        AudioEncoding.IeeeFloat32Bit or
        AudioEncoding.IeeeFloat64Bit => WaveFormatType.IeeeFloat,

        _ => throw new NotSupportedException()
    };
}
