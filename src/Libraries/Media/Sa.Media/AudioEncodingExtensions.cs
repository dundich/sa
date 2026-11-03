namespace Sa.Media;

public static class AudioEncodingExtensions
{
    public static int GetBytesPerSample(this AudioEncoding encoding) => encoding switch
    {
        AudioEncoding.Pcm8BitUnsigned => 1,
        AudioEncoding.Pcm16BitSigned => 2,
        AudioEncoding.Pcm24BitSigned => 3,
        AudioEncoding.Pcm32BitSigned or AudioEncoding.IeeeFloat32Bit => 4,
        AudioEncoding.IeeeFloat64Bit => 8,
        _ => throw new NotSupportedException($"Unknown audio encoding: {encoding}")
    };

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
