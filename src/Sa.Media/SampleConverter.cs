using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sa.Media;

/// <summary>
/// Converts between PCM and floating-point formats
/// </summary>
internal static class SampleConverter
{
    /// <summary>
    ///  Конвертирует raw PCM/float байты в нормализованный double [-1.0, 1.0]
    /// </summary>
    public static Func<ReadOnlySpan<byte>, double> GetNormalizedConverter(ushort bitsPerSample, WaveFormatType format)
    {
        return (bitsPerSample, format) switch
        {
            (8, WaveFormatType.Pcm) => SampleConverter.Convert8BitToDouble,
            (16, WaveFormatType.Pcm) => SampleConverter.Convert16BitToDouble,
            (24, WaveFormatType.Pcm) => SampleConverter.Convert24BitToDouble,
            (32, WaveFormatType.Pcm) => SampleConverter.Convert32BitToDouble,
            (32, WaveFormatType.IeeeFloat) => SampleConverter.Convert32BitFloatToDouble,
            (64, WaveFormatType.IeeeFloat) => SampleConverter.Convert64BitFloatToDouble,
            _ => throw new NotSupportedException($"Unsupported format: {format} ({bitsPerSample}-bit)")
        };
    }

    /// <summary>
    ///  Конвертирует raw PCM/float байты в нормализованный double [-1.0, 1.0]
    /// </summary>
    public static Func<ReadOnlySpan<byte>, double> GetNormalizedConverter(AudioEncoding format)
    {
        return format switch
        {
            AudioEncoding.Pcm8BitUnsigned => SampleConverter.Convert8BitToDouble,
            AudioEncoding.Pcm16BitSigned => SampleConverter.Convert16BitToDouble,
            AudioEncoding.Pcm24BitSigned => SampleConverter.Convert24BitToDouble,
            AudioEncoding.Pcm32BitSigned => SampleConverter.Convert32BitToDouble,
            AudioEncoding.IeeeFloat32Bit => SampleConverter.Convert32BitFloatToDouble,
            AudioEncoding.IeeeFloat64Bit => SampleConverter.Convert64BitFloatToDouble,
            _ => throw new NotSupportedException($"Unsupported format: {format}")
        };
    }

    /// <summary>
    /// Конвертирует raw PCM/float байты сразу в нормализованный float [-1.0, 1.0].
    /// Для DSP-путей (Echo): без промежуточного double, который всё равно приводится к float.
    /// </summary>
    public static Func<ReadOnlySpan<byte>, float> GetNormalizedFloatConverter(ushort bitsPerSample, WaveFormatType format)
    {
        return (bitsPerSample, format) switch
        {
            (8, WaveFormatType.Pcm) => Convert8BitToFloat,
            (16, WaveFormatType.Pcm) => Convert16BitToFloat,
            (24, WaveFormatType.Pcm) => Convert24BitToFloat,
            (32, WaveFormatType.Pcm) => Convert32BitToFloat,
            (32, WaveFormatType.IeeeFloat) => Convert32BitFloatToFloat,
            (64, WaveFormatType.IeeeFloat) => Convert64BitFloatToFloat,
            _ => throw new NotSupportedException($"Unsupported format: {format} ({bitsPerSample}-bit)")
        };
    }


    /// <summary>
    /// Конвертирует нормализованный double [-1.0, 1.0] в нужный формат
    /// </summary>
    public static Func<double, Memory<byte>, ReadOnlyMemory<byte>> GetConverter(AudioEncoding format)
    {
        return format switch
        {
            AudioEncoding.Pcm8BitUnsigned => WritePcm8Bit,
            AudioEncoding.Pcm16BitSigned => WritePcm16Bit,
            AudioEncoding.Pcm24BitSigned => WritePcm24Bit,
            AudioEncoding.Pcm32BitSigned => WritePcm32Bit,
            AudioEncoding.IeeeFloat32Bit => WriteIeeeFloat32Bit,
            AudioEncoding.IeeeFloat64Bit => WriteIeeeFloat64Bit,
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
        // Деление на double обязательно: value / byte.MaxValue — целочисленное деление,
        // из-за которого любой сэмпл, кроме 255, читался как -1.0.
        return (value / (double)byte.MaxValue) * 2.0 - 1.0; // [0..255] → [-1.0..+1.0]
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
    /// Конвертирует 24-битный signed PCM (упакован в 3 байта, little-endian) в double [-1.0, 1.0]
    /// </summary>
    public static double Convert24BitToDouble(ReadOnlySpan<byte> source)
    {
        if (source.Length < 3) throw new ArgumentException("Not enough data for 24-bit sample", nameof(source));

        // 24-bit little-endian: байты [0]=LSB, [1], [2]=MSB
        int value = source[0] | (source[1] << 8) | (source[2] << 16);

        // Знаковое расширение: если бит знака установлен — расширить до int
        if ((source[2] & 0x80) != 0)
            value |= ~0xFFFFFF;

        return value / (double)(1 << 23); // 24 бита → [-8388608..8388607]
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

    // ─────────────────────────────────────────────────────────────────────
    // Float-варианты читателей: для DSP-путей (Echo) double не нужен вовсе.
    // Деление на степень двойки (8/16/24/32-bit) в float точное, поэтому
    // результат совпадает с double-вариантом с точностью до 1 ULP.
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Конвертирует 8-битный unsigned PCM в float [-1.0, 1.0]</summary>
    public static float Convert8BitToFloat(ReadOnlySpan<byte> source)
    {
        if (source.Length < 1) throw new ArgumentException("Not enough data for 8-bit sample", nameof(source));
        return (source[0] / (float)byte.MaxValue) * 2f - 1f;
    }

    /// <summary>Конвертирует 16-битный signed PCM в float [-1.0, 1.0]</summary>
    public static float Convert16BitToFloat(ReadOnlySpan<byte> source)
    {
        if (source.Length < 2) throw new ArgumentException("Not enough data for 16-bit sample", nameof(source));
        return BinaryPrimitives.ReadInt16LittleEndian(source) / -(float)short.MinValue;
    }

    /// <summary>Конвертирует 24-битный signed PCM в float [-1.0, 1.0]</summary>
    public static float Convert24BitToFloat(ReadOnlySpan<byte> source)
    {
        if (source.Length < 3) throw new ArgumentException("Not enough data for 24-bit sample", nameof(source));

        int value = source[0] | (source[1] << 8) | (source[2] << 16);
        if ((source[2] & 0x80) != 0)
            value |= ~0xFFFFFF;

        return value / (float)(1 << 23);
    }

    /// <summary>Конвертирует 32-битный signed PCM в float [-1.0, 1.0]</summary>
    public static float Convert32BitToFloat(ReadOnlySpan<byte> source)
    {
        if (source.Length < 4) throw new ArgumentException("Not enough data for 32-bit sample", nameof(source));
        return BinaryPrimitives.ReadInt32LittleEndian(source) / -(float)int.MinValue;
    }

    /// <summary>Конвертирует 32-битный float (IEEE) в float</summary>
    public static float Convert32BitFloatToFloat(ReadOnlySpan<byte> source)
    {
        if (source.Length < 4) throw new ArgumentException("Not enough data for 32-bit float", nameof(source));
        return MemoryMarshal.Read<float>(source);
    }

    /// <summary>Конвертирует 64-битный float (IEEE) в float</summary>
    public static float Convert64BitFloatToFloat(ReadOnlySpan<byte> source)
    {
        if (source.Length < 8) throw new ArgumentException("Not enough data for 64-bit float", nameof(source));
        return (float)MemoryMarshal.Read<double>(source);
    }



    /// <summary>Полная шкала 24-битного signed PCM (2^23 - 1).</summary>
    private const int Max24Bit = 8_388_607;

    /// <summary>
    /// Приводит сэмпл к диапазону [-1.0, 1.0], NaN превращает в тишину.
    /// <para>
    /// Без этого unchecked-приведение <see cref="double"/> к целому при |sample| &gt; 1.0 даёт
    /// неопределённый результат: float-WAV с перегрузкой (значения за пределами ±1.0 — норма
    /// для клиппированного или интерполированного материала) превращался бы в тишину, а не
    /// в клиппинг.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Clamp(double sample)
    {
        if (double.IsNaN(sample)) return 0.0;
        if (sample > 1.0) return 1.0;
        if (sample < -1.0) return -1.0;
        return sample;
    }

    /// <summary>
    /// Конвертирует нормализованный double [-1.0, 1.0] в 8-битный беззнаковый PCM.
    /// Результат: [0..255]
    /// </summary>
    public static ReadOnlyMemory<byte> WritePcm8Bit(double sample, Memory<byte> buffer)
    {
        byte value = (byte)Math.Round((Clamp(sample) + 1.0) * (byte.MaxValue / 2.0));
        buffer.Span[0] = value;
        return buffer[..1];
    }

    /// <summary>
    /// Конвертирует нормализованный double [-1.0, 1.0] в 16-битный signed PCM (little-endian).
    /// Результат: [-32767..32767]
    /// </summary>
    public static ReadOnlyMemory<byte> WritePcm16Bit(double sample, Memory<byte> buffer)
    {
        short value = (short)Math.Round(Clamp(sample) * short.MaxValue);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.Span, value);
        return buffer[..2];
    }

    /// <summary>
    /// Конвертирует нормализованный double [-1.0, 1.0] в 24-битный signed PCM (little-endian).
    /// Результат: [-8388607..8388607]
    /// </summary>
    public static ReadOnlyMemory<byte> WritePcm24Bit(double sample, Memory<byte> buffer)
    {
        // Полная шкала 24 бит — ±8_388_607. Раньше здесь было int.MaxValue / 65536 (±32767),
        // то есть амплитуда 16 бит в 24-битном контейнере: вывод тише на 48 дБ.
        int value = (int)Math.Round(Clamp(sample) * Max24Bit);
        buffer.Span[0] = (byte)(value & 0xFF);
        buffer.Span[1] = (byte)((value >> 8) & 0xFF);
        buffer.Span[2] = (byte)((value >> 16) & 0xFF);
        return buffer[..3];
    }


    /// <summary>
    /// Конвертирует нормализованный double [-1.0, 1.0] в 32-битный signed PCM (little-endian).
    /// Результат: [-2147483647..2147483647]
    /// </summary>
    public static ReadOnlyMemory<byte> WritePcm32Bit(double sample, Memory<byte> buffer)
    {
        int value = (int)Math.Round(Clamp(sample) * int.MaxValue);
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
