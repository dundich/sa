namespace Sa.Media;

public static class WaveFormatTypeExtensions
{
    public static AudioEncoding GetAudioEncoding(this WaveFormatType encoding, int bitRate)
    {
        return (encoding, bitRate) switch
        {
            (WaveFormatType.Pcm, 8) => AudioEncoding.Pcm8BitUnsigned,
            (WaveFormatType.Pcm, 16) => AudioEncoding.Pcm16BitSigned,
            (WaveFormatType.Pcm, 24) => AudioEncoding.Pcm24BitSigned,
            (WaveFormatType.Pcm, 32) => AudioEncoding.Pcm32BitSigned,

            (WaveFormatType.IeeeFloat, 32) => AudioEncoding.IeeeFloat32Bit,
            (WaveFormatType.IeeeFloat, 64) => AudioEncoding.IeeeFloat64Bit,

            _ => throw new NotSupportedException($"Unsupported format: {encoding} ({bitRate}-bit)")
        };
    }
}