using System.IO.Pipelines;

namespace Sa.Media;

public static class WavHeaderReader
{
    static class Constants
    {
        public const uint Subchunk1IdJunk = 0x4B4E554A; // "JUNK"
        public const uint DataSubchunkId = 0x61746164; // "data"
        public const uint ListSubchunkId = 0x5453494c; // "LIST"
        public const uint FllrSubchunkId = 0x524c4c46; // "FLLR"
        public const uint СhunkRiff = 0x46464952; // RIFF
        public const uint FormatWave = 0x45564157; //WAVE
    }

    public static async Task<WavHeader> ReadHeaderAsync(PipeReader pipe, CancellationToken cancellationToken = default)
    {
        BinaryPinpeReader reader = new(pipe);
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
        if (subchunk1Size > 16)
        {
            ushort fmtExtraSize = await reader.ReadUInt16Async(cancellationToken);
            await reader.SkeepBytesAsync(fmtExtraSize, cancellationToken);
        }

        // Find "data" subchunk
        uint subchunk2Id;
        uint subchunk2Size;
        while (true)
        {
            subchunk2Id = await reader.ReadUInt32Async(cancellationToken);
            subchunk2Size = await reader.ReadUInt32Async(cancellationToken);

            if (subchunk2Id == Constants.ListSubchunkId ||
                subchunk2Id == Constants.FllrSubchunkId)
            {
                await reader.SkeepBytesAsync(subchunk2Size, cancellationToken);
                continue;
            }

            if (subchunk2Id != Constants.DataSubchunkId)
            {
                throw new NotSupportedException($"Unsupported subchunk type: 0x{subchunk2Id:x8}");
            }

            break;
        }

        long dataOffset = reader.Position;

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
            Subchunk2Id = subchunk2Id,
            Subchunk2Size = subchunk2Size,
            DataOffset = dataOffset
        };

        header.Validate();
        return header;
    }
}
