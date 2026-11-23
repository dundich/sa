using Sa.Media.Echo;

// Путь можно передать аргументом. Без аргумента генерируется синтетическая стерео-запись
// с управляемой утечкой каналов: в репозитории нет подходящей стерео-фикстуры, а брать
// моно-файл бессмысленно — CrossFeedSeparator отвергает не-стерео вход.
string wavPath = args.Length > 0 ? args[0] : CreateSyntheticRecording();

if (!File.Exists(wavPath))
{
    Console.WriteLine($"File not found: {wavPath}");
    Console.WriteLine("Usage: dotnet run --project src/Samples/Echo -- <path-to-stereo-wav>");
    return;
}

string outputPath = Path.GetRandomFileName() + ".wav";

try
{
    await CrossFeedSeparator.ExecuteAsync(new AudioSeparationOptions { InputPath = wavPath, OutputPath = outputPath }, CancellationToken.None);

    Console.WriteLine($"Wrote suppressed output: {outputPath}");
    Console.WriteLine($"Output size: {new FileInfo(outputPath).Length} bytes");
}
finally
{
    if (File.Exists(outputPath))
    {
        File.Delete(outputPath);
        Console.WriteLine("Result deleted.");
    }

    if (args.Length == 0 && File.Exists(wavPath))
    {
        File.Delete(wavPath);
    }
}

// 6 секунд, 44.1 кГц: слева 440 Гц, справа 1000 Гц, огибающая 400 мс звук / 100 мс тишина.
// Фазы 0..2 с — только левый источник, 2..4 с — только правый, 4..6 с — оба: разделение
// видно именно на последнем участке. Утечка: yL = xL + 0.35*xR, yR = xR + 0.25*xL.
static string CreateSyntheticRecording()
{
    const int sampleRate = 44100;
    const double seconds = 6.0;
    const double amplitude = 0.6;
    const double alpha = 0.25;
    const double beta = 0.35;

    string path = Path.Combine(Path.GetTempPath(), $"sa_echo_demo_{Guid.NewGuid():N}.wav");
    int totalFrames = (int)(sampleRate * seconds);
    int dataSize = totalFrames * 4;

    using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
    using BinaryWriter writer = new(stream);

    writer.Write("RIFF"u8.ToArray());
    writer.Write(36 + dataSize);
    writer.Write("WAVE"u8.ToArray());
    writer.Write("fmt "u8.ToArray());
    writer.Write(16);
    writer.Write((ushort)1);                 // PCM
    writer.Write((ushort)2);                 // stereo
    writer.Write((uint)sampleRate);
    writer.Write((uint)(sampleRate * 4));    // byte rate
    writer.Write((ushort)4);                 // block align
    writer.Write((ushort)16);                // bits per sample
    writer.Write("data"u8.ToArray());
    writer.Write(dataSize);

    for (int i = 0; i < totalFrames; i++)
    {
        double t = i / (double)sampleRate;
        double envelope = t * 1000 % 500 < 400 ? 1.0 : 0.0;

        bool leftEnabled = t < 2.0 || t >= 4.0;
        bool rightEnabled = t >= 2.0;

        double sourceLeft = leftEnabled ? amplitude * envelope * Math.Sin(2 * Math.PI * 440 * t) : 0.0;
        double sourceRight = rightEnabled ? amplitude * envelope * Math.Sin(2 * Math.PI * 1000 * t) : 0.0;

        writer.Write(ToPcm16(sourceLeft + beta * sourceRight));
        writer.Write(ToPcm16(sourceRight + alpha * sourceLeft));
    }

    Console.WriteLine($"Generated synthetic stereo recording: {path}");
    return path;
}

static short ToPcm16(double sample)
    => (short)Math.Round(Math.Clamp(sample, -1.0, 1.0) * short.MaxValue);
