using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Sa.Media;

/// <summary>
/// Converts between PCM and floating-point formats
/// </summary>
internal static class Converter
{

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
}