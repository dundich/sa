using Sa.Media;

namespace Sa.MediaTests;

/// <summary>
/// Публичные маппинги между <see cref="AudioEncoding"/>, <see cref="WaveFormatType"/> и разрядностью.
/// Раньше <see cref="WaveFormatTypeExtensions.GetAudioEncoding"/> не покрывался ни одним тестом
/// (0 %), а <see cref="AudioEncodingExtensions.GetWaveFormatType"/> — на треть.
/// </summary>
public sealed class AudioEncodingExtensionsTests
{
    [Theory]
    [InlineData(AudioEncoding.Pcm8BitUnsigned, 1)]
    [InlineData(AudioEncoding.Pcm16BitSigned, 2)]
    [InlineData(AudioEncoding.Pcm24BitSigned, 3)]
    [InlineData(AudioEncoding.Pcm32BitSigned, 4)]
    [InlineData(AudioEncoding.IeeeFloat32Bit, 4)]
    [InlineData(AudioEncoding.IeeeFloat64Bit, 8)]
    public void GetBytesPerSample_MatchesTheContainerSize(AudioEncoding encoding, int expected)
        => Assert.Equal(expected, encoding.GetBytesPerSample());

    [Fact]
    public void GetBytesPerSample_UnknownEncoding_Throws()
        => Assert.Throws<NotSupportedException>(() => ((AudioEncoding)99).GetBytesPerSample());

    [Theory]
    [InlineData(AudioEncoding.Pcm8BitUnsigned, WaveFormatType.Pcm)]
    [InlineData(AudioEncoding.Pcm16BitSigned, WaveFormatType.Pcm)]
    [InlineData(AudioEncoding.Pcm24BitSigned, WaveFormatType.Pcm)]
    [InlineData(AudioEncoding.Pcm32BitSigned, WaveFormatType.Pcm)]
    [InlineData(AudioEncoding.IeeeFloat32Bit, WaveFormatType.IeeeFloat)]
    [InlineData(AudioEncoding.IeeeFloat64Bit, WaveFormatType.IeeeFloat)]
    public void GetWaveFormatType_MatchesTheSampleFormat(AudioEncoding encoding, WaveFormatType expected)
        => Assert.Equal(expected, encoding.GetWaveFormatType());

    [Fact]
    public void GetWaveFormatType_UnknownEncoding_Throws()
        => Assert.Throws<NotSupportedException>(() => ((AudioEncoding)99).GetWaveFormatType());

    [Theory]
    [InlineData(WaveFormatType.Pcm, 8, AudioEncoding.Pcm8BitUnsigned)]
    [InlineData(WaveFormatType.Pcm, 16, AudioEncoding.Pcm16BitSigned)]
    [InlineData(WaveFormatType.Pcm, 24, AudioEncoding.Pcm24BitSigned)]
    [InlineData(WaveFormatType.Pcm, 32, AudioEncoding.Pcm32BitSigned)]
    [InlineData(WaveFormatType.IeeeFloat, 32, AudioEncoding.IeeeFloat32Bit)]
    [InlineData(WaveFormatType.IeeeFloat, 64, AudioEncoding.IeeeFloat64Bit)]
    public void GetAudioEncoding_MapsFormatAndBitDepth(
        WaveFormatType format, int bitsPerSample, AudioEncoding expected)
        => Assert.Equal(expected, format.GetAudioEncoding(bitsPerSample));

    [Theory]
    [InlineData(WaveFormatType.Pcm, 12)]
    [InlineData(WaveFormatType.Pcm, 64)]
    [InlineData(WaveFormatType.IeeeFloat, 8)]
    [InlineData(WaveFormatType.IeeeFloat, 16)]
    [InlineData(WaveFormatType.Adpcm, 16)]
    // Extensible сюда не приходит: ридер сначала сводит его к PCM/IEEE float по SubFormat.
    [InlineData(WaveFormatType.Extensible, 16)]
    public void GetAudioEncoding_UnsupportedCombination_Throws(WaveFormatType format, int bitsPerSample)
        => Assert.Throws<NotSupportedException>(() => format.GetAudioEncoding(bitsPerSample));
}
