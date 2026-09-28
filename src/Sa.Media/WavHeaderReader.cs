using System.IO.Pipelines;

namespace Sa.Media;

internal static class WavHeaderReader
{
    static class Constants
    {
        public const uint Subchunk1IdJunk = 0x4B4E554A; // "JUNK"
        public const uint Subchunk1IdFmt = 0x20746D66; // "fmt "
        public const uint DataSubchunkId = 0x61746164; // "data"
        public const uint RiffChunkId = 0x46464952; // RIFF
        public const uint FormatWave = 0x45564157; //WAVE

        // SubFormat-GUIDs из WAVE_FORMAT_EXTENSIBLE (KSDATAFORMAT_SUBTYPE_*)
        public static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
        public static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    }

    public static async Task<WavHeader> ReadHeaderAsync(
        PipeReader pipe,
        CancellationToken cancellationToken = default)
    {
        BinaryPipeReader reader = new(pipe);
        uint chunkId = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);
        uint chunkSize = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);
        uint format = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);

        if (chunkId != Constants.RiffChunkId || format != Constants.FormatWave)
            throw new NotSupportedException("ERROR: File is not a WAV file");

        uint subchunk1Id = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);

        // Skip JUNK chunks
        while (subchunk1Id == Constants.Subchunk1IdJunk)
        {
            uint junkSize = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);
            if (junkSize % 2 == 1) junkSize++; // align to even size
            await reader.SkipBytesAsync(junkSize, cancellationToken).ConfigureAwait(false);
            subchunk1Id = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);
        }

        // После JUNK-пропуска обязан идти "fmt " — иначе мы приняли бы чужой чанк за формат.
        if (subchunk1Id != Constants.Subchunk1IdFmt)
            throw new InvalidDataException($"Invalid WAV file: expected 'fmt ' chunk, found 0x{subchunk1Id:X8}");

        uint subchunk1Size = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);
        ushort audioFormatValue = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
        WaveFormatType audioFormat = (WaveFormatType)audioFormatValue;

        if (audioFormat is not (WaveFormatType.Pcm or WaveFormatType.IeeeFloat or WaveFormatType.Extensible))
            throw new NotSupportedException($"Unsupported audio format: {audioFormat}");

        ushort numChannels = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
        uint sampleRate = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);
        uint byteRate = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);
        ushort blockAlign = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
        ushort bitsPerSample = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);

        // Расширения fmt-чанка: WAVE_FORMAT_EXTENSIBLE и произвольные хвосты (cbSize и т.п.).
        ushort extensionSize = 0;
        ushort validBitsPerSample = 0;
        int channelMask = 0;
        Guid subFormatGuid = default;

        if (subchunk1Size > 16)
        {
            if (audioFormat == WaveFormatType.Extensible)
            {
                // WAVEFORMATEXTENSIBLE: cbSize(2) + wValidBitsPerSample(2) + dwChannelMask(4) + SubFormat(16)
                if (subchunk1Size < 40)
                    throw new InvalidDataException($"Invalid WAV file: WAVE_FORMAT_EXTENSIBLE requires fmt size >= 40, got {subchunk1Size}");

                extensionSize = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
                validBitsPerSample = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
                channelMask = await reader.ReadInt32Async(cancellationToken).ConfigureAwait(false);
                subFormatGuid = await reader.ReadGuidAsync(cancellationToken).ConfigureAwait(false);

                if (subFormatGuid != Constants.SubtypePcm && subFormatGuid != Constants.SubtypeIeeeFloat)
                    throw new NotSupportedException($"WAVE_FORMAT_EXTENSIBLE with unsupported subformat: {subFormatGuid}");

                const int extensiblePayload = 2 + 2 + 4 + 16;
                if (subchunk1Size > 16 + extensiblePayload)
                {
                    await reader.SkipBytesAsync(subchunk1Size - 16 - extensiblePayload, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                // Обычный WAVEFORMATEX с лишними байтами в fmt — пропускаем.
                await reader.SkipBytesAsync(subchunk1Size - 16, cancellationToken).ConfigureAwait(false);
            }
        }

        // Skip extra fmt data (e.g., for WAVE_FORMAT_EXTENSIBLE)
        var (dataOffset, dataSize) = await FindDataChunkAsync(reader, cancellationToken).ConfigureAwait(false);

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
            ExtensionSize = extensionSize,
            ValidBitsPerSample = validBitsPerSample,
            ChannelMask = channelMask,
            SubFormatGuid = subFormatGuid,
            // calculated
            DataOffset = (uint)dataOffset,
            DataSize = dataSize,
        };

        header.Validate();
        return header;
    }

    public static async Task<WavHeader> ReadHeader(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        await using Stream stream = File.OpenRead(filePath);
        PipeReader pipe = PipeReader.Create(stream);
        try
        {
            return await ReadHeaderAsync(pipe, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await pipe.CompleteAsync().ConfigureAwait(false);
        }
    }


    private static async Task<(long, uint dataSize)> FindDataChunkAsync(
        BinaryPipeReader reader, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunkId = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);
            var chunkSize = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);

            if (chunkId == Constants.DataSubchunkId) // "data"
            {
                return (reader.Position, chunkSize); // возвращаем смещение и размер данных
            }

            // Пропускаем чанк (с выравниванием на чётную границу)
            long paddedSize = (chunkSize % 2 == 0) ? chunkSize : chunkSize + 1;
            if (paddedSize == 0)
                throw new InvalidDataException("Invalid WAV file: zero-size chunk");

            await reader.SkipBytesAsync(paddedSize, cancellationToken).ConfigureAwait(false);
        }
    }
}
