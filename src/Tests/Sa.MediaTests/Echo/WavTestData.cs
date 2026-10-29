using Sa.Media;

namespace Sa.MediaTests.Echo;

/// <summary>
/// Синтетические WAV-файлы и измерения для тестов Echo.
/// <para>
/// Вход пишется «сырым» <see cref="BinaryWriter"/>, а не через продуктовый писатель:
/// иначе тест разделения проверял бы ридер ридером и не заметил бы ошибку в обоих.
/// </para>
/// </summary>
internal static class WavTestData
{
    public const int SampleRate = 44100;

    // Модель из readme: yL = xL + β·xR, yR = xR + α·xL.
    public const double LeftToneHz = 440.0;
    public const double RightToneHz = 1000.0;
    public const double Alpha = 0.25;   // xL → yR
    public const double Beta = 0.35;    // xR → yL

    // Фазы записи: 0..LeftOnlyUntil — звучит только левый источник (оценка α),
    // LeftOnlyUntil..BothFrom — только правый (оценка β),
    // BothFrom..Duration — оба одновременно (здесь и меряем подавление).
    public const double LeftOnlyUntil = 2.0;
    public const double BothFrom = 4.0;
    public const double Duration = 6.0;

    /// <summary>
    /// Стерео-запись с управляемой взаимной утечкой каналов.
    /// Огибающая 400 мс звук / 100 мс тишина обязательна: на равномерном сигнале
    /// 20-й перцентиль громкости равен 90-му, порог активности отбрасывает все кадры
    /// и оценка кросс-тока падает с «not enough single-speaker regions».
    /// </summary>
    public static string CreateCrossTalkStereo(
        string directory,
        string fileName = "synthetic_stereo.wav",
        int sampleRate = SampleRate,
        double seconds = Duration,
        double amplitude = 0.6,
        double alpha = Alpha,
        double beta = Beta,
        double leftOnlyUntil = LeftOnlyUntil,
        double bothFrom = BothFrom,
        bool streamingDataSize = false)
    {
        string path = Path.Combine(directory, fileName);
        int totalFrames = (int)Math.Round(sampleRate * seconds);

        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using BinaryWriter writer = new(stream);

        WritePcm16Header(writer, sampleRate, channels: 2, totalFrames, streamingDataSize);

        for (int i = 0; i < totalFrames; i++)
        {
            double t = i / (double)sampleRate;
            double envelope = t * 1000 % 500 < 400 ? 1.0 : 0.0;

            // Фазы: 0..leftOnlyUntil — только левый источник, leftOnlyUntil..bothFrom — только
            // правый, bothFrom.. — оба (здесь меряем подавление).
            bool leftEnabled = t < leftOnlyUntil || t >= bothFrom;
            bool rightEnabled = t >= leftOnlyUntil;

            double sourceLeft = leftEnabled
                ? amplitude * envelope * Math.Sin(2 * Math.PI * LeftToneHz * t)
                : 0.0;

            double sourceRight = rightEnabled
                ? amplitude * envelope * Math.Sin(2 * Math.PI * RightToneHz * t)
                : 0.0;

            writer.Write(ToPcm16(sourceLeft + beta * sourceRight));
            writer.Write(ToPcm16(sourceRight + alpha * sourceLeft));
        }

        return path;
    }

    /// <summary>Стерео-файл с постоянными значениями каналов — для тестов буферов/позиций.</summary>
    public static string CreateConstantStereo(
        string directory,
        short left,
        short right,
        int frames,
        string fileName = "constant_stereo.wav",
        int sampleRate = 8000)
    {
        string path = Path.Combine(directory, fileName);

        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using BinaryWriter writer = new(stream);

        WritePcm16Header(writer, sampleRate, channels: 2, totalFrames: frames);

        for (int i = 0; i < frames; i++)
        {
            writer.Write(left);
            writer.Write(right);
        }

        return path;
    }

    /// <summary>Моно-файл — для проверки отказа на не-стерео входе.</summary>
    public static string CreateMono(
        string directory,
        string fileName = "mono.wav",
        int sampleRate = SampleRate,
        double seconds = 1.0)
    {
        string path = Path.Combine(directory, fileName);
        int totalFrames = (int)Math.Round(sampleRate * seconds);

        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using BinaryWriter writer = new(stream);

        WritePcm16Header(writer, sampleRate, channels: 1, totalFrames);

        for (int i = 0; i < totalFrames; i++)
            writer.Write(ToPcm16(0.5 * Math.Sin(2 * Math.PI * LeftToneHz * i / sampleRate)));

        return path;
    }

    /// <summary>Стерео-файл короче одного кадра анализа (кадр = 100 мс).</summary>
    public static string CreateTooShortStereo(
        string directory,
        string fileName = "too_short.wav",
        int sampleRate = SampleRate,
        int milliseconds = 50)
    {
        int frames = sampleRate * milliseconds / 1000;
        return CreateConstantStereo(directory, left: 1000, right: -1000, frames, fileName, sampleRate);
    }

    /// <summary>Все сэмплы одного канала в нормализованном виде [-1.0, 1.0].</summary>
    public static async Task<double[]> ReadChannelAsync(
        string path,
        int channelId,
        CancellationToken cancellationToken)
    {
        List<double> samples = [];

        await using AsyncWavReader reader = AsyncWavReader.CreateFromFile(path);

        await foreach (AudioNormalizedPacket packet in reader
            .ReadDoubleSamplesAsync(cancellationToken: cancellationToken)
            .WithCancellation(cancellationToken))
        {
            if (packet.ChannelId == channelId)
                samples.Add(packet.Sample);
        }

        return samples.ToArray();
    }

    /// <summary>Амплитуда тона заданной частоты (корреляция с sin/cos на этой частоте).</summary>
    public static double ToneAmplitude(IReadOnlyList<double> samples, double frequency, int sampleRate = SampleRate)
    {
        if (samples.Count == 0) return 0.0;

        double re = 0.0;
        double im = 0.0;

        for (int i = 0; i < samples.Count; i++)
        {
            double angle = 2.0 * Math.PI * frequency * i / sampleRate;
            re += samples[i] * Math.Cos(angle);
            im += samples[i] * Math.Sin(angle);
        }

        return 2.0 * Math.Sqrt(re * re + im * im) / samples.Count;
    }

    /// <summary>Отношение «чужого» тона к «своему» в децибелах.</summary>
    public static double LeakageDb(double foreignAmplitude, double ownAmplitude)
        => 20.0 * Math.Log10((foreignAmplitude + 1e-12) / (ownAmplitude + 1e-12));

    public static double Peak(IReadOnlyList<double> samples)
    {
        double peak = 0.0;
        for (int i = 0; i < samples.Count; i++)
            peak = Math.Max(peak, Math.Abs(samples[i]));
        return peak;
    }

    public static double[] Slice(
        double[] samples,
        double fromSeconds,
        double toSeconds,
        int sampleRate = SampleRate)
    {
        int from = Math.Clamp((int)Math.Round(fromSeconds * sampleRate), 0, samples.Length);
        int to = Math.Clamp((int)Math.Round(toSeconds * sampleRate), from, samples.Length);
        return samples[from..to];
    }

    /// <summary>Проверяет, что это 16-bit PCM stereo WAV с данными до конца файла.</summary>
    public static void AssertPcm16Stereo(string path, int? expectedSampleRate = null)
    {
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream);

        Assert.Equal("RIFF", new string(reader.ReadChars(4)));
        _ = reader.ReadInt32();                                  // размер RIFF
        Assert.Equal("WAVE", new string(reader.ReadChars(4)));

        Assert.Equal("fmt ", new string(reader.ReadChars(4)));
        Assert.Equal(16, reader.ReadInt32());                    // размер fmt
        Assert.Equal(1, reader.ReadInt16());                     // PCM
        Assert.Equal(2, reader.ReadInt16());                     // stereo

        int sampleRate = reader.ReadInt32();
        _ = reader.ReadInt32();                                  // byte rate
        Assert.Equal(4, reader.ReadInt16());                     // block align
        Assert.Equal(16, reader.ReadInt16());                    // bits per sample

        Assert.Equal("data", new string(reader.ReadChars(4)));
        int dataSize = reader.ReadInt32();

        Assert.True(dataSize > 0, "data chunk не должен быть пустым");
        Assert.Equal(dataSize, (int)(stream.Length - 44));
        Assert.True(dataSize % 4 == 0, "data должна содержать целые стерео-кадры");

        if (expectedSampleRate.HasValue)
            Assert.Equal(expectedSampleRate.Value, sampleRate);
    }

    /// <summary>
    /// Стерео-файл с «потоковым» заголовком: data size = 0xFFFFFFFF (типично для рекордеров
    /// и пайпов), хотя данные в файле есть. Размер должен восстанавливаться из длины файла.
    /// </summary>
    public static string CreateStreamingStereo(
        string directory,
        short left,
        short right,
        int frames,
        string fileName = "streaming.wav",
        int sampleRate = 8000)
    {
        string path = Path.Combine(directory, fileName);

        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using BinaryWriter writer = new(stream);

        WritePcm16Header(writer, sampleRate, channels: 2, totalFrames: frames, streamingDataSize: true);

        for (int i = 0; i < frames; i++)
        {
            writer.Write(left);
            writer.Write(right);
        }

        return path;
    }

    private static void WritePcm16Header(
        BinaryWriter writer, int sampleRate, int channels, int totalFrames, bool streamingDataSize = false)
    {
        const int bytesPerSample = 2;
        int dataSize = streamingDataSize ? -1 : totalFrames * channels * bytesPerSample;   // 0xFFFFFFFF

        writer.Write("RIFF"u8.ToArray());
        writer.Write(streamingDataSize ? 0 : 36 + dataSize);
        writer.Write("WAVE"u8.ToArray());

        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write((ushort)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bytesPerSample);
        writer.Write((ushort)(channels * bytesPerSample));
        writer.Write((ushort)16);

        writer.Write("data"u8.ToArray());
        writer.Write(dataSize);
    }

    private static short ToPcm16(double sample)
        => (short)Math.Round(Math.Clamp(sample, -1.0, 1.0) * short.MaxValue);
}
