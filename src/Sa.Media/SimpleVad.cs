using System.Buffers;
using System.Numerics;

namespace Sa.Media;

public sealed class SimpleVad(SimpleVad.Options? options = null)
{
    private readonly Options _options = options ?? new Options();

    public sealed record Options
    {
        public double EnergyThreshold { get; init; } = 0.01;
        public double ZeroCrossingRateThreshold { get; init; } = 0.1;
        public int MinVoiceDurationMs { get; init; } = 100;
        public int FrameSizeMs { get; init; } = 30;
        public bool UseAdaptiveThreshold { get; init; } = true;
    }

    public sealed record Result
    {
        public bool HasVoice { get; init; }
        public double MaxEnergy { get; init; }
        public double AverageEnergy { get; init; }
        public TimeSpan VoiceDuration { get; init; }
        public double Confidence { get; init; }
    }

    public async Task<Result> AnalyzeVoiceAsync(
          string audioPath,
          CancellationToken cancellationToken = default)
    {
        await using var reader = AsyncWavReader.CreateFromFile(audioPath);
        return await AnalyzeVoiceAsync(reader, cancellationToken);
    }

    public async Task<Result> AnalyzeVoiceAsync(
        AsyncWavReader reader,
        CancellationToken cancellationToken = default)
    {
        var header = await reader.GetHeaderAsync(cancellationToken);

        int frameSize = (int)(header.SampleRate * _options.FrameSizeMs / 1000.0);
        int bytesPerSample = header.GetBytesPerSample();

        var energyBuffer = ArrayPool<double>.Shared.Rent(frameSize);

        try
        {
            List<double> energies = [];
            List<bool> voiceFrames = [];
            double maxEnergy = 0;

            Func<ReadOnlySpan<byte>, double> converter = header.GetNormalizedConverter();
            int sampleIndex = 0;
            await foreach (var packet in reader.ReadSamplesPerChannelAsync(
                allowBufferReuse: true,
                cancellationToken: cancellationToken))
            {
                // Берем только первый канал для VAD
                if (packet.ChannelId == 0)
                {
                    // Конвертируем байты в double
                    double sample = converter(packet.Sample.Span);

                    // Накопляем фрейм
                    // Здесь нужно накопить frameSize сэмплов перед анализом

                    energyBuffer[sampleIndex] = sample;

                    // Когда набрали полный фрейм
                    if (sampleIndex == frameSize - 1)
                    {
                        var frameEnergy = CalculateEnergy(energyBuffer.AsSpan(0, frameSize));
                        energies.Add(frameEnergy);
                        maxEnergy = Math.Max(maxEnergy, frameEnergy);

                        var threshold = _options.UseAdaptiveThreshold
                            ? CalculateAdaptiveThreshold(energies)
                            : _options.EnergyThreshold;

                        var hasVoice = frameEnergy > threshold &&
                                      CheckZeroCrossingRate(energyBuffer.AsSpan(0, frameSize));

                        voiceFrames.Add(hasVoice);
                        sampleIndex = 0;
                    }
                    else
                    {
                        sampleIndex++;
                    }
                }
            }

            return CreateResult(energies, voiceFrames, maxEnergy);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(energyBuffer);
        }
    }

    private double CalculateAdaptiveThreshold(List<double> energies)
    {
        if (energies.Count == 0) return _options.EnergyThreshold;

        // Используем медиану + стандартное отклонение
        var median = energies.OrderBy(x => x).Skip(energies.Count / 2).First();
        var stdDev = Math.Sqrt(energies.Average(e => Math.Pow(e - median, 2)));

        return (double)(median + stdDev);
    }

    private bool CheckZeroCrossingRate(ReadOnlySpan<double> samples)
    {
        int zeroCrossings = 0;

        for (int i = 1; i < samples.Length; i++)
        {
            if (Math.Sign(samples[i]) != Math.Sign(samples[i - 1]))
                zeroCrossings++;
        }

        double zcr = zeroCrossings / (double)samples.Length;
        return zcr >= _options.ZeroCrossingRateThreshold;
    }

    private Result CreateResult(
        List<double> energies,
        List<bool> voiceFrames,
        double maxEnergy)
    {
        var voiceDuration = TimeSpan.FromSeconds(
            voiceFrames.Count(f => f) * _options.FrameSizeMs / 1000.0);

        var confidence = voiceFrames.Count > 0
            ? voiceFrames.Count(f => f) / (double)voiceFrames.Count
            : 0;

        return new Result
        {
            HasVoice = voiceDuration.TotalMilliseconds >= _options.MinVoiceDurationMs,
            MaxEnergy = maxEnergy,
            AverageEnergy = energies.DefaultIfEmpty(0).Average(),
            VoiceDuration = voiceDuration,
            Confidence = confidence
        };
    }

    private static double CalculateEnergy(ReadOnlySpan<double> samples)
    {
        double sum = 0;

        // Используем SIMD-оптимизацию через Vector
        if (Vector.IsHardwareAccelerated && samples.Length >= Vector<double>.Count)
        {
            var vectorSum = Vector<double>.Zero;
            int i;

            for (i = 0; i <= samples.Length - Vector<double>.Count; i += Vector<double>.Count)
            {
                var vector = new Vector<double>(samples.Slice(i, Vector<double>.Count));
                vectorSum += vector * vector;
            }

            // Суммируем элементы вектора
            for (int j = 0; j < Vector<double>.Count; j++)
                sum += vectorSum[j];

            // Обрабатываем остаток
            for (; i < samples.Length; i++)
                sum += samples[i] * samples[i];
        }
        else
        {
            // Fallback для систем без SIMD
            foreach (var sample in samples)
                sum += sample * sample;
        }

        return sum / samples.Length;
    }
}



public static class VadExtensions
{
    public static async Task<SimpleVad.Result> AnalyzeVoiceAsync(
        this SimpleVad vad,
        string audioPath,
        CancellationToken cancellationToken = default)
    {
        await using var reader = AsyncWavReader.CreateFromFile(audioPath);
        return await vad.AnalyzeVoiceAsync(reader, cancellationToken);
    }
}
