using System.IO.Pipelines;

namespace Sa.Media;

public static class WavHeaderReader
{
    static class Constants
    {
        public const uint Subchunk1IdJunk = 0x4B4E554A; // "JUNK"
        public const uint DataSubchunkId = 0x61746164; // "data"
        public const uint СhunkRiff = 0x46464952; // RIFF
        public const uint FormatWave = 0x45564157; //WAVE
    }

    public static async Task<WavHeader> ReadHeaderAsync(PipeReader pipe, CancellationToken cancellationToken = default)
    {
        BinaryPipeReader reader = new(pipe);
        uint chunkId = await reader.ReadUInt32Async(cancellationToken);
        uint chunkSize = await reader.ReadUInt32Async(cancellationToken);
        uint format = await reader.ReadUInt32Async(cancellationToken);

        if (chunkId != Constants.СhunkRiff || format != Constants.FormatWave)
            throw new NotSupportedException("ERROR: File is not a WAV file");

        uint subchunk1Id = await reader.ReadUInt32Async(cancellationToken);

        // Skip JUNK chunks
        while (subchunk1Id == Constants.Subchunk1IdJunk)
        {
            uint junkSize = await reader.ReadUInt32Async(cancellationToken);
            if (junkSize % 2 == 1) junkSize++; // align to even size
            await reader.SkeepBytesAsync(junkSize, cancellationToken);
            subchunk1Id = await reader.ReadUInt32Async(cancellationToken);
        }

        uint subchunk1Size = await reader.ReadUInt32Async(cancellationToken);
        ushort audioFormatValue = await reader.ReadUInt16Async(cancellationToken);
        WaveFormatType audioFormat = (WaveFormatType)audioFormatValue;

        if (audioFormat is not (WaveFormatType.Pcm or WaveFormatType.IeeeFloat))
            throw new NotSupportedException($"Unsupported audio format: {audioFormat}");

        ushort numChannels = await reader.ReadUInt16Async(cancellationToken);
        uint sampleRate = await reader.ReadUInt32Async(cancellationToken);
        uint byteRate = await reader.ReadUInt32Async(cancellationToken);
        ushort blockAlign = await reader.ReadUInt16Async(cancellationToken);
        ushort bitsPerSample = await reader.ReadUInt16Async(cancellationToken);


        // Skip extra fmt data (e.g., for WAVE_FORMAT_EXTENSIBLE)
        var (dataOffset, dataSize) = await FindDataChunkAsync(reader, cancellationToken);

        var header = new WavHeader
        {
            ChunkId = chunkId,
            ChunkSize = chunkSize,
            Format = format,
            Subchunk1Id = subchunk1Id,
            Subchunk1Size = subchunk1Size,
            AudioFormat = audioFormat,
            NumChannels = numChannels,
            SampleRate = sampleRate,
            ByteRate = byteRate,
            BlockAlign = blockAlign,
            BitsPerSample = bitsPerSample,
            // calculated
            DataOffset = (uint)dataOffset,
            DataSize = dataSize,
        };

        header.Validate();
        return header;
    }


    private static async Task<(long, uint dataSize)> FindDataChunkAsync(BinaryPipeReader reader, CancellationToken cancellationToken = default)
    {
        while (reader.Position < 4096)
        {
            var chunkId = await reader.ReadUInt32Async(cancellationToken);
            var chunkSize = await reader.ReadUInt32Async(cancellationToken);

            if (chunkId == Constants.DataSubchunkId) // "data"
            {
                return (reader.Position, chunkSize); // возвращаем смещение и размер данных
            }

            // Пропускаем чанк (с выравниванием)
            long paddedSize = (chunkSize % 2 == 0) ? chunkSize : chunkSize + 1;
            await reader.SkeepBytesAsync(paddedSize, cancellationToken);
        }

        throw new InvalidDataException("WAV file does not contain a 'data' chunk.");
    }
}
