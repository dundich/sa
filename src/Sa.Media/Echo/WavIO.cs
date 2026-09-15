using System.Buffers;

namespace Sa.Media.Echo;

// ───────────────────────────────────────────────────────────────────
// Общие операции ввода-вывода WAV
// ───────────────────────────────────────────────────────────────────
internal static class WavIO
{
    public static async Task<float[]> ReadInterleavedFloatArrayAsync(string path, CancellationToken ct)
    {
        await using AsyncWavReader reader = AsyncWavReader.CreateFromFile(path);
        var header = await reader.GetHeaderAsync(ct);

        int sampleCount = (int)(header.DataSize / 4);
        float[] interleaved = new float[sampleCount * 2];
        int idx = 0;

        await foreach (var packet in reader.ReadDoubleSamplesAsync(allowBufferReuse: true, cancellationToken: ct)
            .WithCancellation(ct))
        {
            if (packet.ChannelId == 0)
                interleaved[idx * 2] = (float)packet.Sample;
            else
                interleaved[idx * 2 + 1] = (float)packet.Sample;
            if (packet.ChannelId == 1) idx++;
        }

        if (idx < sampleCount) Array.Resize(ref interleaved, idx * 2);
        return interleaved;
    }

    public static async Task WriteWavFileAsync(
        Stream stream,
        int sampleRate,
        ReadOnlyMemory<float> audio,
        CancellationToken cancellationToken)
    {
        int sampleCount = audio.Length / 2;

        const int channels = 2;
        const int bitsPerSample = 16;

        int byteRate = sampleRate * channels * bitsPerSample / 8;
        int blockAlign = channels * bitsPerSample / 8;
        int dataSize = sampleCount * blockAlign;

        var header = new byte[44];

        WriteAscii(header, 0, "RIFF");
        WriteInt32(header, 4, 36 + dataSize);
        WriteAscii(header, 8, "WAVE");
        WriteAscii(header, 12, "fmt ");
        WriteInt32(header, 16, 16);
        WriteInt16(header, 20, 1);
        WriteInt16(header, 22, channels);
        WriteInt32(header, 24, sampleRate);
        WriteInt32(header, 28, byteRate);
        WriteInt16(header, 32, blockAlign);
        WriteInt16(header, 34, bitsPerSample);
        WriteAscii(header, 36, "data");
        WriteInt32(header, 40, dataSize);

        await stream.WriteAsync(header, cancellationToken);

        var chunk = ArrayPool<byte>.Shared.Rent(1 << 20);

        try
        {
            int offset = 0;

            while (offset < sampleCount)
            {
                int batchSize = Math.Min(chunk.Length / 4, sampleCount - offset);

                int pos = FillPcmChunk(
                    chunk,
                    audio.Slice(offset * 2, batchSize * 2));

                await stream.WriteAsync(chunk.AsMemory(0, pos), cancellationToken);

                offset += batchSize;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    private static int FillPcmChunk(
    byte[] chunk,
    ReadOnlyMemory<float> samples)
    {
        ReadOnlySpan<float> span = samples.Span;

        int frameCount = span.Length / 2;
        int pos = 0;

        for (int i = 0; i < frameCount; i++)
        {
            short l = (short)(Math.Clamp(span[i * 2], -1.0f, 1.0f) * 32767);
            short r = (short)(Math.Clamp(span[i * 2 + 1], -1.0f, 1.0f) * 32767);

            chunk[pos++] = (byte)(l & 0xFF);
            chunk[pos++] = (byte)((l >> 8) & 0xFF);
            chunk[pos++] = (byte)(r & 0xFF);
            chunk[pos++] = (byte)((r >> 8) & 0xFF);
        }

        return pos;
    }

    public static async Task WriteWavStreamAsync(Stream stream, int sampleRate, IAsyncEnumerable<float> audio, CancellationToken ct)
    {
        const int channels = 2;
        const int bitsPerSample = 16;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        int blockAlign = channels * bitsPerSample / 8;

        var header = new byte[44];
        WriteHeader(header, sampleRate, 0, byteRate, blockAlign, channels, bitsPerSample);
        await stream.WriteAsync(header, ct);

        var chunk = ArrayPool<byte>.Shared.Rent(1 << 20);
        long bytesWritten = 0;
        try
        {
            int pos = 0;
            await foreach (var sample in audio.WithCancellation(ct))
            {
                short s = (short)(Math.Clamp(sample, -1.0f, 1.0f) * 32767);
                chunk[pos++] = (byte)(s & 0xFF);
                chunk[pos++] = (byte)((s >> 8) & 0xFF);
                if (pos >= chunk.Length)
                {
                    await stream.WriteAsync(chunk.AsMemory(0, pos), ct);
                    bytesWritten += pos;
                    pos = 0;
                }
            }
            if (pos > 0)
            {
                await stream.WriteAsync(chunk.AsMemory(0, pos), ct);
                bytesWritten += pos;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(chunk); }

        if (stream.CanSeek)
        {
            stream.Seek(4, SeekOrigin.Begin);
            var sizeBuffer = new byte[4];
            WriteInt32(sizeBuffer, 0, (int)(36 + bytesWritten));
            await stream.WriteAsync(sizeBuffer, ct);

            stream.Seek(40, SeekOrigin.Begin);
            WriteInt32(sizeBuffer, 0, (int)bytesWritten);
            await stream.WriteAsync(sizeBuffer, ct);
        }
    }

    private static void WriteHeader(byte[] buffer, int sampleRate, int dataSize, int byteRate, int blockAlign, int channels, int bitsPerSample)
    {
        WriteAscii(buffer, 0, "RIFF");
        WriteInt32(buffer, 4, 36 + dataSize);
        WriteAscii(buffer, 8, "WAVE");
        WriteAscii(buffer, 12, "fmt ");
        WriteInt32(buffer, 16, 16);
        WriteInt16(buffer, 20, 1);
        WriteInt16(buffer, 22, channels);
        WriteInt32(buffer, 24, sampleRate);
        WriteInt32(buffer, 28, byteRate);
        WriteInt16(buffer, 32, blockAlign);
        WriteInt16(buffer, 34, bitsPerSample);
        WriteAscii(buffer, 36, "data");
        WriteInt32(buffer, 40, dataSize);
    }

    private static void WriteAscii(byte[] buffer, int offset, string value)
    {
        for (int i = 0; i < value.Length; i++)
            buffer[offset + i] = (byte)value[i];
    }

    private static void WriteInt16(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }
}
