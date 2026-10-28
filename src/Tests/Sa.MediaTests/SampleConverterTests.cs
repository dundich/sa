using Sa.Media;

namespace Sa.MediaTests;

/// <summary>
/// Конвертация PCM ↔ double. Тесты фиксируют два регресса: 24-битный писатель
/// масштабировал до 16 бит (±32767 вместо ±8 388 607), и ни один PCM-писатель
/// не клампил вход — unchecked-приведение double вне ±1.0 даёт неопределённый результат.
/// </summary>
public sealed class SampleConverterTests
{
    private static readonly double[] RoundTripSamples = [-0.9, -0.5, 0.0, 0.25, 0.5, 0.9];

    [Theory]
    [InlineData(AudioEncoding.Pcm8BitUnsigned)]
    [InlineData(AudioEncoding.Pcm16BitSigned)]
    [InlineData(AudioEncoding.Pcm24BitSigned)]
    [InlineData(AudioEncoding.Pcm32BitSigned)]
    [InlineData(AudioEncoding.IeeeFloat32Bit)]
    [InlineData(AudioEncoding.IeeeFloat64Bit)]
    public void RoundTrip_NormalizedSample_IsPreserved(AudioEncoding encoding)
    {
        Func<double, Memory<byte>, ReadOnlyMemory<byte>> write = SampleConverter.GetConverter(encoding);
        Func<ReadOnlySpan<byte>, double> read = SampleConverter.GetNormalizedConverter(encoding);

        byte[] buffer = new byte[encoding.GetBytesPerSample()];

        foreach (double sample in RoundTripSamples)
        {
            double actual = read(write(sample, buffer).Span);
            Assert.Equal(sample, actual, Tolerance(encoding));
        }
    }

    [Fact]
    public void WritePcm24Bit_UsesEntire24BitRange()
    {
        byte[] buffer = new byte[3];

        SampleConverter.WritePcm24Bit(1.0, buffer);
        Assert.Equal(8_388_607, ReadInt24(buffer));   // 0x7FFFFF, не 0x007FFF

        SampleConverter.WritePcm24Bit(-1.0, buffer);
        Assert.Equal(-8_388_607, ReadInt24(buffer));

        SampleConverter.WritePcm24Bit(0.5, buffer);
        Assert.Equal(4_194_304, ReadInt24(buffer));

        SampleConverter.WritePcm24Bit(0.0, buffer);
        Assert.Equal(0, ReadInt24(buffer));
    }

    [Theory]
    [InlineData(AudioEncoding.Pcm8BitUnsigned)]
    [InlineData(AudioEncoding.Pcm16BitSigned)]
    [InlineData(AudioEncoding.Pcm24BitSigned)]
    [InlineData(AudioEncoding.Pcm32BitSigned)]
    public void WritePcm_OutOfRange_ClampsToFullScale(AudioEncoding encoding)
    {
        Func<double, Memory<byte>, ReadOnlyMemory<byte>> write = SampleConverter.GetConverter(encoding);
        Func<ReadOnlySpan<byte>, double> read = SampleConverter.GetNormalizedConverter(encoding);

        byte[] buffer = new byte[encoding.GetBytesPerSample()];

        Assert.Equal(1.0, read(write(1.5, buffer).Span), Tolerance(encoding));
        Assert.Equal(-1.0, read(write(-1.5, buffer).Span), Tolerance(encoding));
        Assert.Equal(1.0, read(write(double.PositiveInfinity, buffer).Span), Tolerance(encoding));
        Assert.Equal(-1.0, read(write(double.NegativeInfinity, buffer).Span), Tolerance(encoding));
    }

    [Theory]
    [InlineData(AudioEncoding.Pcm8BitUnsigned)]
    [InlineData(AudioEncoding.Pcm16BitSigned)]
    [InlineData(AudioEncoding.Pcm24BitSigned)]
    [InlineData(AudioEncoding.Pcm32BitSigned)]
    public void WritePcm_NaN_ProducesSilence(AudioEncoding encoding)
    {
        Func<double, Memory<byte>, ReadOnlyMemory<byte>> write = SampleConverter.GetConverter(encoding);
        Func<ReadOnlySpan<byte>, double> read = SampleConverter.GetNormalizedConverter(encoding);

        byte[] buffer = new byte[encoding.GetBytesPerSample()];
        double value = read(write(double.NaN, buffer).Span);

        // Для беззнакового 8-бита тишина — это 128, для знаковых форматов — 0.
        double expected = encoding == AudioEncoding.Pcm8BitUnsigned ? 128.0 / 255.0 * 2.0 - 1.0 : 0.0;
        Assert.Equal(expected, value, Tolerance(encoding));
    }

    [Fact]
    public void Convert8Bit_MapsEndpointsToFullScale()
    {
        Assert.Equal(-1.0, SampleConverter.Convert8BitToDouble([0]));
        Assert.Equal(1.0, SampleConverter.Convert8BitToDouble([255]));
        Assert.Equal(0.0, SampleConverter.Convert8BitToDouble([128]), 1e-2);
    }

    [Fact]
    public void Convert16Bit_MapsSignedEndpoints()
    {
        Assert.Equal(-1.0, SampleConverter.Convert16BitToDouble([0x00, 0x80]));   // -32768
        Assert.Equal(-1.0 / 32768.0, SampleConverter.Convert16BitToDouble([0xFF, 0xFF]), 1e-12);
        Assert.Equal(1.0, SampleConverter.Convert16BitToDouble([0xFF, 0x7F]), 4e-5);
    }

    [Fact]
    public void Convert24Bit_SignExtendsNegativeSamples()
    {
        Assert.Equal(8_388_607 / 8_388_608.0, SampleConverter.Convert24BitToDouble([0xFF, 0xFF, 0x7F]), 1e-9);
        Assert.Equal(-1.0, SampleConverter.Convert24BitToDouble([0x00, 0x00, 0x80]), 1e-9);
        Assert.Equal(-1.0 / 8_388_608.0, SampleConverter.Convert24BitToDouble([0xFF, 0xFF, 0xFF]), 1e-12);
    }

    [Fact]
    public void Convert32Bit_MapsSignedEndpoints()
    {
        Assert.Equal(-1.0, SampleConverter.Convert32BitToDouble([0x00, 0x00, 0x00, 0x80]));   // int.MinValue
        Assert.Equal(1.0, SampleConverter.Convert32BitToDouble([0xFF, 0xFF, 0xFF, 0x7F]), 1e-9);
    }

    [Fact]
    public void ConvertFloat_DoesNotClampAudioAboveFullScale()
    {
        // IEEE float WAV законно хранит значения за пределами ±1.0; читатель их не трогает.
        byte[] buffer = new byte[4];
        SampleConverter.WriteIeeeFloat32Bit(1.5, buffer);
        Assert.Equal(1.5, SampleConverter.Convert32BitFloatToDouble(buffer), 1e-6);
    }

    [Theory]
    [InlineData(AudioEncoding.Pcm8BitUnsigned, 8, WaveFormatType.Pcm)]
    [InlineData(AudioEncoding.Pcm16BitSigned, 16, WaveFormatType.Pcm)]
    [InlineData(AudioEncoding.Pcm24BitSigned, 24, WaveFormatType.Pcm)]
    [InlineData(AudioEncoding.Pcm32BitSigned, 32, WaveFormatType.Pcm)]
    [InlineData(AudioEncoding.IeeeFloat32Bit, 32, WaveFormatType.IeeeFloat)]
    [InlineData(AudioEncoding.IeeeFloat64Bit, 64, WaveFormatType.IeeeFloat)]
    public void FloatConverter_MatchesDoubleConverter(
        AudioEncoding encoding, ushort bitsPerSample, WaveFormatType format)
    {
        Func<ReadOnlySpan<byte>, double> toDouble = SampleConverter.GetNormalizedConverter(encoding);
        Func<ReadOnlySpan<byte>, float> toFloat = SampleConverter.GetNormalizedFloatConverter(bitsPerSample, format);
        Func<double, Memory<byte>, ReadOnlyMemory<byte>> write = SampleConverter.GetConverter(encoding);

        byte[] buffer = new byte[encoding.GetBytesPerSample()];

        foreach (double sample in RoundTripSamples.Append(1.0).Append(-1.0))
        {
            ReadOnlyMemory<byte> bytes = write(sample, buffer);
            Assert.Equal(toDouble(bytes.Span), toFloat(bytes.Span), 1e-6);
        }
    }

    [Fact]
    public void FloatConverter_UnsupportedFormat_Throws()
    {
        Assert.Throws<NotSupportedException>(
            () => SampleConverter.GetNormalizedFloatConverter(64, WaveFormatType.Pcm));
    }

    private static double Tolerance(AudioEncoding encoding) => encoding switch
    {
        AudioEncoding.Pcm8BitUnsigned => 2.0 / 255.0,
        AudioEncoding.Pcm16BitSigned => 2.0 / 32_768.0,
        AudioEncoding.Pcm24BitSigned => 2.0 / 8_388_608.0,
        AudioEncoding.Pcm32BitSigned => 2.0 / 2_147_483_648.0,
        AudioEncoding.IeeeFloat32Bit => 1e-7,
        AudioEncoding.IeeeFloat64Bit => 1e-12,
        _ => throw new ArgumentOutOfRangeException(nameof(encoding))
    };

    private static int ReadInt24(ReadOnlySpan<byte> bytes)
    {
        int value = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
        if ((bytes[2] & 0x80) != 0) value |= ~0xFFFFFF;
        return value;
    }
}
