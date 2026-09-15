using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Sa.Media;


internal static class AsyncWavWriter
{
    public static async Task WriteToPcm16Le(
        Stream stream,
        int sampleRate,
        IAsyncEnumerable<float> audio,
        CancellationToken cancellationToken = default)
    {
        const int channels = 2;
        const int bitsPerSample = 16;
        const int bytesPerSample = bitsPerSample / 8;
        short blockAlign = channels * bytesPerSample;
        int byteRate = sampleRate * blockAlign;

        // Длину заранее не знаем, поэтому размеры в заголовке придётся патчить.
        if (!stream.CanSeek)
            throw new ArgumentException(
                "Для записи WAV из IAsyncEnumerable нужен seekable Stream (например, FileStream).",
                nameof(stream));

        long headerStart = stream.Position;

        // ---------- Заголовок (44 байта) с плейсхолдерами ----------
        var header = new byte[44];
        var h = header.AsSpan();
        WriteAscii(h, 0, "RIFF");
        BinaryPrimitives.WriteInt32LittleEndian(h.Slice(4, 4), 0);   // ← патч потом
        WriteAscii(h, 8, "WAVE");
        WriteAscii(h, 12, "fmt ");
        BinaryPrimitives.WriteInt32LittleEndian(h.Slice(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(h.Slice(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(h.Slice(22, 2), channels);
        BinaryPrimitives.WriteInt32LittleEndian(h.Slice(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(h.Slice(28, 4), byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(h.Slice(32, 2), blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(h.Slice(34, 2), bitsPerSample);
        WriteAscii(h, 36, "data");
        BinaryPrimitives.WriteInt32LittleEndian(h.Slice(40, 4), 0);   // ← патч потом

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);

        // ---------- Данные ----------
        const int floatBufSize = 8192;                 // чётное, interleaved L/R
        const int byteBufSize = floatBufSize * 2;     // 2 байта на сэмпл

        var floatBuf = ArrayPool<float>.Shared.Rent(floatBufSize);
        var byteBuf = ArrayPool<byte>.Shared.Rent(byteBufSize);

        long totalDataBytes = 0;
        int floatCount = 0;

        try
        {
            await foreach (var sample in audio
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                if (floatCount == floatBufSize)
                {
                    int written = FloatToPcm16(floatBuf.AsSpan(0, floatCount), byteBuf);
                    await stream.WriteAsync(
                        byteBuf.AsMemory(0, written),
                        cancellationToken).ConfigureAwait(false);
                    totalDataBytes += written;
                    floatCount = 0;
                }
                floatBuf[floatCount++] = sample;
            }

            // Остаток. Если нечётное число сэмплов — последний L/R-пару отбрасываем.
            int even = floatCount & ~1;
            if (even > 0)
            {
                int written = FloatToPcm16(floatBuf.AsSpan(0, even), byteBuf);
                await stream.WriteAsync(
                    byteBuf.AsMemory(0, written),
                    cancellationToken).ConfigureAwait(false);
                totalDataBytes += written;
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(floatBuf);
            ArrayPool<byte>.Shared.Return(byteBuf);
        }

        // ---------- Патч размеров ----------
        long endPos = stream.Position;

        Span<byte> tmp = stackalloc byte[4];

        stream.Position = headerStart + 4;
        BinaryPrimitives.WriteInt32LittleEndian(tmp, (int)(36 + totalDataBytes));
        stream.Write(tmp);

        stream.Position = headerStart + 40;
        BinaryPrimitives.WriteInt32LittleEndian(tmp, (int)totalDataBytes);
        stream.Write(tmp);

        stream.Position = endPos;
    }

    private static int FloatToPcm16(ReadOnlySpan<float> src, Span<byte> dst)
    {
        int pairs = src.Length >> 1;
        int o = 0;
        for (int i = 0; i < pairs; i++)
        {
            int l = ClampToInt16(src[i * 2]);
            int r = ClampToInt16(src[i * 2 + 1]);
            dst[o++] = (byte)l;
            dst[o++] = (byte)(l >> 8);
            dst[o++] = (byte)r;
            dst[o++] = (byte)(r >> 8);
        }
        return o;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ClampToInt16(float v)
    {
        // Сохраняем поведение оригинала: Math.Clamp(v, -1, 1) * 32767.
        if (v > 1f) v = 1f;
        else if (v < -1f) v = -1f;
        return (int)(v * 32767f);
    }

    private static void WriteAscii(Span<byte> dst, int offset, string ascii)
    {
        for (int i = 0; i < ascii.Length; i++)
            dst[offset + i] = (byte)ascii[i];
    }
}
