using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Sa.Media;

/// <summary>
/// Converts between PCM and floating-point formats
/// </summary>
internal static class SampleConverter
{

    /// <summary>
    /// Конвертирует raw PCM/float байты в нормализованный double [-1.0, 1.0]
    /// </summary>
    public static double ToNormalizedDouble(ReadOnlySpan<byte> source, AudioEncoding format)
    {
        return format switch
        {
            AudioEncoding.Pcm8BitUnsigned => Convert8BitToDouble(source),
            AudioEncoding.Pcm16BitSigned => Convert16BitToDouble(source),
            AudioEncoding.Pcm24BitSigned => Convert24BitToDouble(source),
            AudioEncoding.Pcm32BitSigned => Convert32BitToDouble(source),
            AudioEncoding.IeeeFloat32Bit => Convert32BitFloatToDouble(source),
            AudioEncoding.IeeeFloat64Bit => Convert64BitFloatToDouble(source),
            _ => throw new NotSupportedException($"Unsupported format: {format}")
        };
    }

    /// <summary>
    /// Конвертирует raw PCM/float байты в нормализованный double [-1.0, 1.0]
    /// </summary>
    public static double ToNormalizedDouble(ReadOnlySpan<byte> source, WaveFormatType format, ushort bitsPerSample)
    {
        return (format, bitsPerSample) switch
        {
            (WaveFormatType.Pcm, 8) => Convert8BitToDouble(source),
            (WaveFormatType.Pcm, 16) => Convert16BitToDouble(source),
            (WaveFormatType.Pcm, 24) => Convert24BitToDouble(source),
            (WaveFormatType.Pcm, 32) => Convert32BitToDouble(source),
            (WaveFormatType.IeeeFloat, 32) => Convert32BitFloatToDouble(source),
            (WaveFormatType.IeeeFloat, 64) => Convert64BitFloatToDouble(source),
            _ => throw new NotSupportedException($"Unsupported format: {format} ({bitsPerSample}-bit)")
        };
    }

    /// <summary>
    /// Конвертирует нормализованный double [-1.0, 1.0] в нужный формат
    /// </summary>
    public static ReadOnlyMemory<byte> FromNormalizedDouble(double sample, AudioEncoding format, Memory<byte> buffer)
    {
        return format switch
        {
            AudioEncoding.Pcm8BitUnsigned => WritePcm8Bit(sample, buffer),
            AudioEncoding.Pcm16BitSigned => WritePcm16Bit(sample, buffer),
            AudioEncoding.Pcm24BitSigned => WritePcm24Bit(sample, buffer),
            AudioEncoding.Pcm32BitSigned => WritePcm32Bit(sample, buffer),
            AudioEncoding.IeeeFloat32Bit => WriteIeeeFloat32Bit(sample, buffer),
            AudioEncoding.IeeeFloat64Bit => WriteIeeeFloat64Bit(sample, buffer),
            _ => throw new NotSupportedException($"Unsupported format: {format}")
        };
    }



    /// <summary>
    /// Конвертирует 8-битный unsigned PCM в double [-1.0, 1.0]
    /// </summary>
    public static double Convert8BitToDouble(ReadOnlySpan<byte> source)
    {
        if (source.Length < 1) throw new ArgumentException("Not enough data for 8-bit sample", nameof(source));
        byte value = source[0];
        return (value / byte.MaxValue * 2.0) - 1.0; // [0..255] → [-1.0..+1.0]
    }

    /// <summary>
    /// Конвертирует 16-битный signed PCM в double [-1.0, 1.0]
    /// </summary>
    public static double Convert16BitToDouble(ReadOnlySpan<byte> source)
    {
        if (source.Length < 2) throw new ArgumentException("Not enough data for 16-bit sample", nameof(source));
        short value = BinaryPrimitives.ReadInt16LittleEndian(source);
        return value / -(double)short.MinValue; // Учитываем асимметрию short.MinValue
    }

    /// <summary>
    /// Конвертирует 24-битный signed PCM (упакован в 3 байта) в double [-1.0, 1.0]
    /// </summary>
    public static double Convert24BitToDouble(ReadOnlySpan<byte> source)
    {
        if (source.Length < 3) throw new ArgumentException("Not enough data for 24-bit sample", nameof(source));

        // Читаем 3 байта и расширяем до int с учётом знака
        int value = (source[0] << 8) | (source[1] << 16) | (source[2] << 24);
        return (value >> 8) / (double)(1 << 23); // 24 бита → [-8388608..8388607]
    }

    /// <summary>
    /// Конвертирует 32-битный signed PCM в double [-1.0, 1.0]
    /// </summary>
    public static double Convert32BitToDouble(ReadOnlySpan<byte> source)
    {
        if (source.Length < 4) throw new ArgumentException("Not enough data for 32-bit sample", nameof(source));
        int value = BinaryPrimitives.ReadInt32LittleEndian(source);
        return value / -(double)int.MinValue; // Учитываем асимметрию int.MinValue
    }

    /// <summary>
    /// Конвертирует 32-битный float (IEEE) в double [-1.0, 1.0]
    /// </summary>
    public static double Convert32BitFloatToDouble(ReadOnlySpan<byte> source)
    {
        if (source.Length < 4) throw new ArgumentException("Not enough data for 32-bit float", nameof(source));
        return MemoryMarshal.Cast<byte, float>(source)[0];
    }

    /// <summary>
    /// Конвертирует 64-битный float (IEEE) в double [-1.0, 1.0]
    /// </summary>
    public static double Convert64BitFloatToDouble(ReadOnlySpan<byte> source)
    {
        if (source.Length < 8) throw new ArgumentException("Not enough data for 64-bit float", nameof(source));
        return MemoryMarshal.Read<double>(source);
    }



    /// <summary>
    /// Конвертирует нормализованный double [-1.0, 1.0] в 8-битный беззнаковый PCM.
    /// Результат: [0..255]
    /// </summary>
    public static ReadOnlyMemory<byte> WritePcm8Bit(double sample, Memory<byte> buffer)
    {
        byte value = (byte)((sample + 1.0) * byte.MaxValue / 2.0);
        buffer.Span[0] = value;
        return buffer[..1];
    }

    /// <summary>
    /// Конвертирует нормализованный double [-1.0, 1.0] в 16-битный signed PCM (little-endian).
    /// Результат: [-32768..32767]
    /// </summary>
    public static ReadOnlyMemory<byte> WritePcm16Bit(double sample, Memory<byte> buffer)
    {
        short value = (short)(sample * short.MaxValue);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.Span, value);
        return buffer[..2];
    }

    /// <summary>
    /// Конвертирует нормализованный double [-1.0, 1.0] в 24-битный signed PCM (little-endian).
    /// Результат: [-8388608..8388607]
    /// </summary>
    public static ReadOnlyMemory<byte> WritePcm24Bit(double sample, Memory<byte> buffer)
    {
        int value = (int)(sample * int.MaxValue / 65536); // Масштабируем до 24 бит
        buffer.Span[0] = (byte)(value & 0xFF);
        buffer.Span[1] = (byte)((value >> 8) & 0xFF);
        buffer.Span[2] = (byte)((value >> 16) & 0xFF);
        return buffer[..3];
    }


    /// <summary>
    /// Конвертирует нормализованный double [-1.0, 1.0] в 32-битный signed PCM (little-endian).
    /// Результат: [-2147483648..2147483647]
    /// </summary>
    public static ReadOnlyMemory<byte> WritePcm32Bit(double sample, Memory<byte> buffer)
    {
        int value = (int)(sample * int.MaxValue);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Span, value);
        return buffer[..4];
    }

    /// <summary>
    /// Конвертирует double [-1.0, 1.0] в IEEE float 32-bit.
    /// </summary>
    public static ReadOnlyMemory<byte> WriteIeeeFloat32Bit(double sample, Memory<byte> buffer)
    {
        float value = (float)sample;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.Span, value);
        return buffer[..4];
    }


    /// <summary>
    /// Конвертирует double [-1.0, 1.0] в IEEE float 64-bit.
    /// </summary>
    public static ReadOnlyMemory<byte> WriteIeeeFloat64Bit(double sample, Memory<byte> buffer)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(buffer.Span, sample);
        return buffer[..8];
    }
}
