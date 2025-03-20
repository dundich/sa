using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace Sa.Media.Wav;

/// <summary>
/// Структура, описывающая заголовок WAV файла.
/// <seealso href="https://gitlab.activebc.ru/ABC/activebc.tools.media/-/blob/master/src/ActiveBC.Tools.Media/WavReader.cs?ref_type=heads"/>
/// <seealso href="https://audiocoding.cc/articles/2008-05-22-wav-file-structure/"/>
/// <seealso href="https://stackoverflow.com/questions/8754111/how-to-read-the-data-in-a-wav-file-to-an-array/34667370#34667370"/>
/// </summary>

public sealed class WavFile : IDisposable
{
    static class Env
    {
        public const UInt32 ChunkId = 0x46464952;
        public const UInt32 WaveFormat = 0x45564157;
        public const UInt16 WaveFormatPcm = 0x0001;
        public const UInt32 Subchunk1IdJunk = 0x4B4E554A;
    }

    private BinaryReader? _reader;


    /// <summary>
    /// Содержит символы "RIFF" в ASCII кодировке
    /// </summary>
    public UInt32 ChunkId { get; private set; }

    /// <summary>
    ///  Это оставшийся размер цепочки, начиная с этой позиции.
    ///   Иначе говоря, это размер файла - 8, то есть, исключены поля chunkId и chunkSize.
    /// </summary>
    public UInt32 ChunkSize { get; private set; }

    /// <summary>
    /// Содержит символы "WAVE"
    /// </summary>
    public UInt32 Format { get; private set; }

    /// <summary>
    /// Содержит символы "fmt "
    /// </summary>
    public UInt32 Subchunk1Id { get; private set; }

    /// <summary>
    /// 16 для формата PCM. (or 18)
    /// Это оставшийся размер подцепочки, начиная с этой позиции.
    /// </summary>
    public UInt32 Subchunk1Size { get; private set; }

    /// <summary>
    /// Аудио формат
    /// Для PCM = 1 (то есть, Линейное квантование).
    /// Значения, отличающиеся от 1, обозначают некоторый формат сжатия.
    /// <seealso cref="http://audiocoding.ru/wav_formats.txt"/>
    /// </summary>
    public UInt16 AudioFormat { get; private set; }

    /// <summary>
    /// Количество каналов. Моно = 1, Стерео = 2 и т.д.
    /// </summary>
    public UInt16 NumChannels { get; private set; }

    /// <summary>
    /// Частота дискретизации. 8000 Гц, 44100 Гц и т.д.
    /// </summary>
    public UInt32 SampleRate { get; private set; }

    /// <summary>
    /// sampleRate * numChannels * bitsPerSample/8
    /// </summary>
    public UInt32 ByteRate { get; private set; }

    /// <summary>
    /// numChannels * bitsPerSample/8
    /// Количество байт для одного сэмпла, включая все каналы.
    /// </summary>
    public UInt16 BlockAlign { get; private set; }

    /// <summary>
    /// Так называемая "глубиная" или точность звучания. 8 бит, 16 бит и т.д.
    /// </summary>
    public UInt16 BitsPerSample { get; private set; }

    // Подцепочка "data" содержит аудио-данные и их размер.

    /// <summary>
    /// Содержит символы "data"
    /// </summary>
    public UInt32 Subchunk2Id { get; private set; }

    /// <summary>
    /// numSamples * numChannels * bitsPerSample/8
    /// Количество байт в области данных.
    /// </summary>
    public int Subchunk2Size { get; private set; }

    /// <summary>
    /// Смещение к области данных
    /// </summary>
    public long DataOffset { get; private set; }

    /// <summary>
    /// the number of samples per channel
    /// </summary>
    public int SamplesPerChannel { get; private set; }

    /// <summary>
    /// Из файла
    /// </summary>
    public string? FileName { get; private set; }


    public bool IsWave => IsLoaded() && ChunkId == Env.ChunkId
        && Format == Env.WaveFormat;

    public bool IsPcmWave => IsWave
        && (Subchunk1Size == 16 || Subchunk2Size == 18)
        && AudioFormat == Env.WaveFormatPcm;

    public bool IsLoaded() => DataOffset > 0;


    public WavFile ReadHeader(bool suppressErrors = true)
    {
        if (IsLoaded()) return this;

        BinaryReader reader = OpenReader();

        // chunk 0
        ChunkId = reader.ReadUInt32();
        ChunkSize = reader.ReadUInt32();
        Format = reader.ReadUInt32();

        // chunk 1
        Subchunk1Id = reader.ReadUInt32();

        // chunk 1
        // Содержит символы "fmt "
        // (0x666d7420 в big-endian представлении)
        while (Subchunk1Id == Env.Subchunk1IdJunk) //JUNK
        {
            //skip JUNK chunks: https://www.daubnet.com/en/file-format-riff
            UInt32 JunkSubchunk1Size = reader.ReadUInt32(); // bytes for this chunk
            if (JunkSubchunk1Size % 2 == 1)
            {
                ++JunkSubchunk1Size;    //When writing RIFFs, JUNK chunks should not have odd number as Size.
            }
            reader.ReadBytes((int)JunkSubchunk1Size);
            Subchunk1Id = reader.ReadUInt32();  //read next subchunk
        }


        Subchunk1Size = reader.ReadUInt32(); // bytes for this chunk (expect 16 or 18)

        // 16 bytes coming...
        AudioFormat = reader.ReadUInt16();
        NumChannels = reader.ReadUInt16();
        SampleRate = reader.ReadUInt32();
        ByteRate = reader.ReadUInt32();
        BlockAlign = reader.ReadUInt16();
        BitsPerSample = reader.ReadUInt16();


        if (Subchunk1Size == 18)
        {
            // Read any extra values
            int fmtExtraSize = reader.ReadInt16();
            reader.ReadBytes(fmtExtraSize);
        }

        // chunk 2


        while (true)
        {
            Subchunk2Id = reader.ReadUInt32();
            Subchunk2Size = reader.ReadInt32();

            if (Subchunk2Id == 0x5453494c)
            {
                //just skip LIST subchunk
                reader.ReadBytes(Subchunk2Size);
                continue;
            }
            if (Subchunk2Id == 0x524c4c46)
            {
                //just skip FLLR subchunk https://stackoverflow.com/questions/6284651/avaudiorecorder-doesnt-write-out-proper-wav-file-header
                reader.ReadBytes(Subchunk2Size);
                continue;
            }

            if (Subchunk2Id != 0x61746164)
            {
                if (suppressErrors) return this;
                throw new NotImplementedException($"Bad Subchunk2Id: 0x{Subchunk2Id:x8}");
            }
            break;
        }

        if (Subchunk2Size == 0x7FFFFFFF)
        {
            //size does not set!!
            //hack to support custom file length calculation
            //this does not check if there are otehr subchunks after "data" in thefile
            long sizeInBytesLong = (reader.BaseStream.Length - reader.BaseStream.Position);
            if (sizeInBytesLong > Int32.MaxValue)
            {
                if (suppressErrors) return this;
                throw new ArgumentNullException("Too long wave! " + sizeInBytesLong);
            }

            Subchunk2Size = (int)sizeInBytesLong;
        }

        // Calculate the number of samples per channel
        SamplesPerChannel = Subchunk2Size / (BlockAlign * NumChannels);

        // save start data offset
        DataOffset = reader.BaseStream.Position;

        return this;
    }


    public WavFile WithFileName(string filename)
    {
        if (filename != FileName)
        {
            Close();
            FileName = filename;
        }

        return this;
    }


    public IEnumerable<(int channelId, byte[] sample)> ReadWave(float? cutFromSeconds = null, float? cutToSeconds = null)
    {
        ReadHeader();

        BinaryReader reader = OpenReader();

        // Calculate the byte offset for the start of the data
        long dataOffset = DataOffset;

        // Calculate the byte offset for the end of the data
        long dataEndOffset = dataOffset + Subchunk2Size;

        // Calculate the byte offset for the start of the cut
        long cutFromOffset = dataOffset;
        if (cutFromSeconds != null)
        {
            cutFromOffset += (long)(cutFromSeconds.Value * SampleRate * BlockAlign);
        }

        // Calculate the byte offset for the end of the cut
        long cutToOffset = dataEndOffset;
        if (cutToSeconds != null)
        {
            cutToOffset = dataOffset + (long)(cutToSeconds.Value * SampleRate * BlockAlign);
        }

        if (reader.BaseStream.CanSeek)
        {
            reader.BaseStream.Position = cutFromOffset;
        }

        // Read samples from the current channel
        for (long i = cutFromOffset; i < cutToOffset; i += BlockAlign)
        {
            for (int channelId = 0; channelId < NumChannels; channelId++)
            {
                // Read the sample from the stream
                byte[] sample = reader.ReadBytes(BlockAlign / NumChannels);
                yield return (channelId, sample);
            }
        }
    }

    /// <summary>
    /// Convert and return audio data in double format
    /// </summary>
    /// <param name="cutFromSeconds"></param>
    /// <param name="cutToSeconds"></param>
    /// <returns></returns>
    public IEnumerable<(int channelId, double[] sample)> ReadDoubleWave(float? cutFromSeconds = null, float? cutToSeconds = null)
        => ReadWave(cutFromSeconds, cutToSeconds)
            .Select(c => (c.channelId, ConvertToDouble(BitsPerSample, c.sample)));



    /// <summary>
    /// для распознавалок
    /// </summary>
    public IEnumerable<(int channelId, byte[] sample)> ReadDoubleWaveAsByte(float? cutFromSeconds = null, float? cutToSeconds = null)
        => ReadDoubleWave(cutFromSeconds, cutToSeconds)
            .Select(c => (c.channelId, ConvertToByte(c.sample)));

    public double GetLengthSeconds()
        => IsLoaded() && SampleRate != 0
            ? SamplesPerChannel / SampleRate
            : 0;

    public TimeSpan GetLength() => TimeSpan.FromSeconds(GetLengthSeconds());

    public long WriteChannel([NotNull] string fileName, [Range(0, 10)] int indexChannel)
    {
        using FileStream fs = File.Open(fileName ?? throw new ArgumentNullException(nameof(fileName)), FileMode.OpenOrCreate);
        return WriteChannel(fs, indexChannel);
    }

    public long WriteChannel(FileStream fs, [Range(0, 10)] int indexChannel)
    {
        ReadHeader();

        if (!IsLoaded()) throw new NotSupportedException();

        if (indexChannel >= NumChannels || indexChannel < 0) throw new ArgumentOutOfRangeException(nameof(indexChannel));

        using var writer = new BinaryWriter(fs);
        writer.Write(ChunkId);
        writer.Write(ChunkSize);
        writer.Write(Format);
        writer.Write(Subchunk1Id);
        writer.Write(Subchunk1Size);
        writer.Write(AudioFormat);
        writer.Write((UInt16)1); //NumChannels
        writer.Write(SampleRate);
        writer.Write(ByteRate);
        writer.Write((UInt16)(BlockAlign / NumChannels));
        writer.Write(BitsPerSample);
        writer.Write(Subchunk2Id);
        writer.Write(Subchunk2Size / NumChannels);


        foreach (var (_, sample) in ReadWave().Where(c => c.channelId == indexChannel))
        {
            writer.Write(sample);
        }

        writer.Flush();
        return fs.Length;
    }

    public void Close()
    {
        _reader?.Dispose();
        _reader = null;
        DataOffset = 0;
    }

    public void Dispose() => Close();

    private BinaryReader OpenReader()
    {
        if (_reader == null)
        {
            FileStream fs = File.Open(FileName ?? throw new ArgumentException(nameof(FileName)), FileMode.Open);
            _reader = new BinaryReader(fs);
        }
        else
        {
            _reader.BaseStream.Position = 0;
        }
        return _reader;
    }

    private static byte[] ConvertToByte(double[] data)
    {
        short[] array = Array.ConvertAll(data, (double e) => (short)(e * 32767.0));
        byte[] array2 = new byte[array.Length * 2];
        Buffer.BlockCopy(array, 0, array2, 0, array2.Length);
        return array2;
    }

    private static double[] ConvertToDouble(ushort bitsPerSample, byte[] data)
    {
        int len = data.Length;
        double[] sample;
        switch (bitsPerSample)
        {
            case 64:
                sample = new double[len / sizeof(double)];
                Buffer.BlockCopy(data, 0, sample, 0, len);
                break;
            case 32:
                float[] asFloat = new float[len / sizeof(float)];
                Buffer.BlockCopy(data, 0, asFloat, 0, len);
                sample = Array.ConvertAll(asFloat, e => (double)e);
                break;
            case 16:
                Int16[] asInt16 = new Int16[len / sizeof(Int16)];
                Buffer.BlockCopy(data, 0, asInt16, 0, len);
                sample = Array.ConvertAll(asInt16, e => e / -(double)Int16.MinValue);
                break;
            default: throw new ArgumentException("Bad BitsPerSample: " + bitsPerSample);
        }

        return sample;
    }
}
