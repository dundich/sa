using System.Buffers;
using System.Runtime.CompilerServices;

namespace Sa.Media.Echo;


public sealed class CrossFeedSeparator()
{
    public enum ProcessingMode { Auto, MaximumSpeed, MinimumMemory, MinimumMemoryFastNormalize, Aggressive }

    private readonly record struct AudioPacket(int FrameIndex, float LeftSample, float RightSample, bool IsFrameEof);
    private sealed record HeadInfo(int FrameSize, int FrameCount, int SampleRate, long TotalFrames);
    private sealed record EstimateResult(double Alpha, double Beta, float PeakLeft, float PeakRight);

    private const long MaxInMemorySamples = 64_000_000;
    private const long HardInMemorySamples = 200_000_000;

    public static async Task ExecuteAsync(AudioSeparationOptions options, CancellationToken cancellationToken = default)
    {
        string inputPath = Path.GetFullPath(options.InputPath);
        if (!File.Exists(inputPath)) throw new FileNotFoundException($"Input file not found: {inputPath}");
        if (options.SpectralMaskPower <= 0) throw new ArgumentException("--spectral-mask-power must be greater than zero");
        if (options.MaskFloor is < 0 or > 1) throw new ArgumentException("--mask-floor must be between zero and one");

        HeadInfo head = await ReadHeadInfoAsync(inputPath, cancellationToken);
        if (head.TotalFrames == 0 || head.FrameCount == 0) throw new InvalidOperationException("Recording is too short to analyze");

        bool isAggressive = options.Processing == ProcessingMode.Aggressive;

        var modeName = isAggressive
            ? "aggressive"
            : "linear";

        string inputDirectory = Path.GetDirectoryName(inputPath) ?? Directory.GetCurrentDirectory();
        string outputPath = Path.GetFullPath(options.OutputPath
            ?? Path.Combine(inputDirectory, $"{Path.GetFileNameWithoutExtension(inputPath)}_{modeName}.wav"));

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory)) Directory.CreateDirectory(outputDirectory);

        if (isAggressive)
        {
            EstimateResult estimate = await EstimateLeakageStreamingAsync(
                inputPath, head, options.DominanceThresholdDb, cancellationToken);

            await AggressiveMode.Execute(options, head.SampleRate, estimate.Alpha, estimate.Beta, cancellationToken);
            return;
        }

        ProcessingMode effectiveMode = ResolveMode(options.Processing, head);
        switch (effectiveMode)
        {
            case ProcessingMode.MaximumSpeed when TryGetInMemoryCapacity(head, effectiveMode, out _):
                await WriteLinearInMemoryAsync(inputPath, outputPath, head, options.DominanceThresholdDb, cancellationToken);
                break;
            case ProcessingMode.MinimumMemoryFastNormalize:
                EstimateResult fastEstimate = await EstimateLeakageStreamingAsync(inputPath, head, options.DominanceThresholdDb, cancellationToken);
                await WriteLinearStreamingFastNormalizeAsync(inputPath, outputPath, head.SampleRate, fastEstimate, cancellationToken);
                break;
            default:
                EstimateResult exactEstimate = await EstimateLeakageStreamingAsync(inputPath, head, options.DominanceThresholdDb, cancellationToken);
                await WriteLinearStreamingExactAsync(inputPath, outputPath, head.SampleRate, exactEstimate.Alpha, exactEstimate.Beta, cancellationToken);
                break;
        }
    }

    private static ProcessingMode ResolveMode(ProcessingMode mode, HeadInfo head)
    {
        if (mode != ProcessingMode.Auto) return mode;
        return head.TotalFrames > 0 && head.TotalFrames <= MaxInMemorySamples / 2 ? ProcessingMode.MaximumSpeed : ProcessingMode.MinimumMemory;
    }

    private static bool TryGetInMemoryCapacity(HeadInfo head, ProcessingMode effectiveMode, out int capacity)
    {
        long limit = effectiveMode == ProcessingMode.MaximumSpeed ? HardInMemorySamples : MaxInMemorySamples;
        if (head.TotalFrames > 0 && head.TotalFrames <= limit / 2 && head.TotalFrames <= int.MaxValue / 2)
        {
            capacity = checked((int)(head.TotalFrames * 2));
            return true;
        }
        capacity = 0;
        return false;
    }

    private static async Task<HeadInfo> ReadHeadInfoAsync(string path, CancellationToken cancellationToken)
    {
        WavHeader wavHeader = await WavHeaderReader.ReadHeader(path, cancellationToken);
        if (wavHeader.NumChannels != 2)
            throw new InvalidOperationException($"Expected a stereo recording, found {wavHeader.NumChannels} channel(s)");
        int sampleRate = (int)wavHeader.SampleRate;
        long totalFrames = (long)wavHeader.DataSize / 4;
        int frameSize = Math.Max(1, (int)Math.Round(sampleRate * 0.1));
        int frameCount = (int)Math.Min((long)int.MaxValue, totalFrames / frameSize);
        return new HeadInfo(frameSize, frameCount, sampleRate, totalFrames);
    }

    private static async IAsyncEnumerable<AudioPacket> ReadDataAsync(
        string path, int? maxFrameIndex, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using AsyncWavReader reader = AsyncWavReader.CreateFromFile(path);
        WavHeader wavHeader = await reader.GetHeaderAsync(cancellationToken);
        int sampleRate = (int)wavHeader.SampleRate;
        long totalFrames = (long)wavHeader.DataSize / 4;
        int frameSize = Math.Max(1, (int)Math.Round(sampleRate * 0.1));
        int frameCount = (int)Math.Min((long)int.MaxValue, totalFrames / frameSize);
        if (frameCount == 0) throw new InvalidOperationException("Recording is too short to analyze");

        int frameOffset = frameSize;
        int frameIndex = 0;
        float leftSample = 0f;

        await foreach (var packet in reader.ReadDoubleSamplesAsync(
            allowBufferReuse: true, cancellationToken: cancellationToken).WithCancellation(cancellationToken))
        {
            float sample = (float)packet.Sample;
            if (packet.ChannelId == 0)
            {
                leftSample = sample;
            }
            else
            {
                if (maxFrameIndex.HasValue && frameIndex > maxFrameIndex.Value) yield break;
                frameOffset--;
                bool isEof = frameOffset == 0;
                yield return new AudioPacket(frameIndex, leftSample, sample, isEof);
                if (isEof)
                {
                    frameIndex++;
                    frameOffset = frameSize;
                    if (maxFrameIndex.HasValue && frameIndex > maxFrameIndex.Value) yield break;
                }
            }
        }
    }

    private static async Task WriteLinearInMemoryAsync(
        string inputPath, string outputPath, HeadInfo head, double dominanceDb, CancellationToken cancellationToken)
    {
        if (!TryGetInMemoryCapacity(head, ProcessingMode.MaximumSpeed, out int capacity))
            throw new InvalidOperationException("File is too large for in-memory processing");

        float[] data = ArrayPool<float>.Shared.Rent(capacity);
        try
        {
            int count = 0;
            await foreach (var packet in ReadDataAsync(inputPath, null, cancellationToken).WithCancellation(cancellationToken))
            {
                if (count + 1 >= capacity) break;
                data[count++] = packet.LeftSample;
                data[count++] = packet.RightSample;
            }
            count &= ~1;
            if (count == 0) throw new InvalidOperationException("No audio samples read");

            var (alpha, beta) = EstimateFromMemory(data.AsSpan(0, count), head.FrameSize, dominanceDb);
            AudioMath.InvertCrossFeedModel(data.AsSpan(0, count), alpha, beta);
            AudioMath.NormalizePeak(data.AsSpan(0, count));

            using Stream output = new FileStream(outputPath, new FileStreamOptions { Access = FileAccess.Write, Mode = FileMode.Create, Share = FileShare.None, BufferSize = 1 << 20, Options = FileOptions.SequentialScan | FileOptions.Asynchronous });
            await WavIO.WriteWavFileAsync(output, head.SampleRate, data.AsSpan(0, count).ToArray(), cancellationToken);
        }
        finally { ArrayPool<float>.Shared.Return(data); }
    }

    private static (double Alpha, double Beta) EstimateFromMemory(
        ReadOnlySpan<float> interleaved, int frameSize, double dominanceDb)
    {
        int stride = checked(frameSize * 2);
        int frameCount = interleaved.Length / stride;
        if (frameCount == 0) throw new InvalidOperationException("Recording is too short to analyze");

        var energyL = new double[frameCount];
        var energyR = new double[frameCount];
        var loudness = new double[frameCount];

        for (int f = 0; f < frameCount; f++)
        {
            int start = f * stride;
            double sumL = 0.0, sumR = 0.0;
            for (int i = 0; i < stride; i += 2)
            {
                float l = interleaved[start + i], r = interleaved[start + i + 1];
                sumL += (double)l * l;
                sumR += (double)r * r;
            }
            energyL[f] = sumL;
            energyR[f] = sumR;
            double rmsL = Math.Sqrt(sumL / frameSize + AudioConstants.RmsEpsilon);
            double rmsR = Math.Sqrt(sumR / frameSize + AudioConstants.RmsEpsilon);
            loudness[f] = Math.Max(rmsL, rmsR);
        }

        var sortedLoudness = (double[])loudness.Clone();
        Array.Sort(sortedLoudness);
        double activeThreshold = Math.Max(AudioMath.Percentile(sortedLoudness, 20), AudioMath.Percentile(sortedLoudness, 90) * 0.02);

        var dominance = new byte[frameCount];
        int leftCount = 0, rightCount = 0;
        for (int f = 0; f < frameCount; f++)
        {
            if (loudness[f] <= activeThreshold) continue;
            double rmsL = Math.Sqrt(energyL[f] / frameSize + AudioConstants.RmsEpsilon);
            double rmsR = Math.Sqrt(energyR[f] / frameSize + AudioConstants.RmsEpsilon);
            double ratioDb = 20.0 * Math.Log10((rmsL + 1e-10) / (rmsR + 1e-10));
            if (ratioDb > dominanceDb) { dominance[f] = 1; leftCount++; }
            else if (ratioDb < -dominanceDb) { dominance[f] = 2; rightCount++; }
        }

        if (leftCount < 3 || rightCount < 3) throw new InvalidOperationException("Could not find enough single-speaker regions to estimate cross-talk. Try a lower --dominance-db value.");

        double crossA = 0.0, selfA = 0.0, crossB = 0.0, selfB = 0.0;
        for (int f = 0; f < frameCount; f++)
        {
            if (dominance[f] == 0) continue;
            int start = f * stride;
            if (dominance[f] == 1)
            {
                for (int i = 0; i < stride; i += 2) { float l = interleaved[start + i], r = interleaved[start + i + 1]; crossA += (double)l * r; selfA += (double)l * l; }
            }
            else
            {
                for (int i = 0; i < stride; i += 2) { float l = interleaved[start + i], r = interleaved[start + i + 1]; crossB += (double)l * r; selfB += (double)r * r; }
            }
        }

        if (selfA <= AudioConstants.RmsEpsilon || selfB <= AudioConstants.RmsEpsilon) throw new InvalidOperationException("Not enough energy in dominant frames to estimate cross-talk.");
        double alpha = crossA / selfA, beta = crossB / selfB;
        AudioMath.ThrowIfUnsafeCoefficients(alpha, beta);
        return (alpha, beta);
    }

    private static async Task<EstimateResult> EstimateLeakageStreamingAsync(
        string path, HeadInfo head, double dominanceDb, CancellationToken cancellationToken)
    {
        if (head.FrameCount == 0) throw new InvalidOperationException("Recording is too short to analyze");
        var energyL = new double[head.FrameCount];
        var energyR = new double[head.FrameCount];
        var loudness = new double[head.FrameCount];
        double sumL = 0.0, sumR = 0.0;
        float peakLeft = 0f, peakRight = 0f;

        await foreach (var packet in ReadDataAsync(path, null, cancellationToken).WithCancellation(cancellationToken))
        {
            float l = packet.LeftSample, r = packet.RightSample;
            float absL = Math.Abs(l); if (absL > peakLeft) peakLeft = absL;
            float absR = Math.Abs(r); if (absR > peakRight) peakRight = absR;
            if (packet.FrameIndex >= head.FrameCount) continue;
            sumL += (double)l * l; sumR += (double)r * r;
            if (!packet.IsFrameEof) continue;
            int f = packet.FrameIndex;
            energyL[f] = sumL; energyR[f] = sumR;
            double rmsL = Math.Sqrt(sumL / head.FrameSize + AudioConstants.RmsEpsilon);
            double rmsR = Math.Sqrt(sumR / head.FrameSize + AudioConstants.RmsEpsilon);
            loudness[f] = Math.Max(rmsL, rmsR);
            sumL = 0.0; sumR = 0.0;
        }

        var sortedLoudness = (double[])loudness.Clone();
        Array.Sort(sortedLoudness);
        double activeThreshold = Math.Max(AudioMath.Percentile(sortedLoudness, 20), AudioMath.Percentile(sortedLoudness, 90) * 0.02);
        var dominance = new byte[head.FrameCount];
        int leftCount = 0, rightCount = 0, lastDominantFrame = -1;
        for (int f = 0; f < head.FrameCount; f++)
        {
            if (loudness[f] <= activeThreshold) continue;
            double rmsL = Math.Sqrt(energyL[f] / head.FrameSize + AudioConstants.RmsEpsilon);
            double rmsR = Math.Sqrt(energyR[f] / head.FrameSize + AudioConstants.RmsEpsilon);
            double ratioDb = 20.0 * Math.Log10((rmsL + 1e-10) / (rmsR + 1e-10));
            if (ratioDb > dominanceDb) { dominance[f] = 1; leftCount++; lastDominantFrame = f; }
            else if (ratioDb < -dominanceDb) { dominance[f] = 2; rightCount++; lastDominantFrame = f; }
        }

        if (leftCount < 3 || rightCount < 3) throw new InvalidOperationException("Could not find enough single-speaker regions to estimate cross-talk. Try a lower --dominance-db value.");
        var (alpha, beta) = await ComputeLeakageGainAsync(path, dominance, lastDominantFrame, cancellationToken);
        return new EstimateResult(alpha, beta, peakLeft, peakRight);
    }

    private static async Task<(double Alpha, double Beta)> ComputeLeakageGainAsync(string path, byte[] dominance, int lastDominantFrame, CancellationToken cancellationToken)
    {
        double crossA = 0.0, selfA = 0.0, crossB = 0.0, selfB = 0.0;
        int currentFrame = -1;
        bool useLeft = false, useRight = false;

        await foreach (var packet in ReadDataAsync(path, lastDominantFrame, cancellationToken).WithCancellation(cancellationToken))
        {
            if (packet.FrameIndex > lastDominantFrame) break;
            if (packet.FrameIndex != currentFrame)
            {
                currentFrame = packet.FrameIndex;
                byte mask = dominance[currentFrame];
                useLeft = (mask & 1) != 0; useRight = (mask & 2) != 0;
            }
            if (!useLeft && !useRight) continue;
            float l = packet.LeftSample, r = packet.RightSample;
            if (useLeft) { crossA += (double)l * r; selfA += (double)l * l; }
            if (useRight) { crossB += (double)l * r; selfB += (double)r * r; }
        }

        if (selfA <= AudioConstants.RmsEpsilon || selfB <= AudioConstants.RmsEpsilon)
            throw new InvalidOperationException("Not enough energy in dominant frames to estimate cross-talk.");

        double alpha = crossA / selfA, beta = crossB / selfB;
        AudioMath.ThrowIfUnsafeCoefficients(alpha, beta);
        return (alpha, beta);
    }

    private static async Task WriteLinearStreamingExactAsync(
        string inputPath, string outputPath, int sampleRate, double alpha, double beta, CancellationToken cancellationToken)
    {
        float peak = await GetPeakAsync(inputPath, alpha, beta, cancellationToken);
        float gain = peak <= 1e-12f ? 1f : (float)(AudioConstants.NormalizationPeak / peak);
        using Stream output = new FileStream(outputPath, new FileStreamOptions { Access = FileAccess.Write, Mode = FileMode.Create, Share = FileShare.None, BufferSize = 1 << 20, Options = FileOptions.SequentialScan | FileOptions.Asynchronous });
        await WavIO.WriteWavStreamAsync(output, sampleRate, InvertWithGainAsync(inputPath, alpha, beta, gain, cancellationToken), cancellationToken);
    }

    private static async Task WriteLinearStreamingFastNormalizeAsync(
        string inputPath, string outputPath, int sampleRate, EstimateResult estimate, CancellationToken cancellationToken)
    {
        float gain = ComputeFastGain(estimate.Alpha, estimate.Beta, estimate.PeakLeft, estimate.PeakRight);
        using Stream output = new FileStream(outputPath, new FileStreamOptions { Access = FileAccess.Write, Mode = FileMode.Create, Share = FileShare.None, BufferSize = 1 << 20, Options = FileOptions.SequentialScan | FileOptions.Asynchronous });
        await WavIO.WriteWavStreamAsync(output, sampleRate, InvertWithGainAsync(inputPath, estimate.Alpha, estimate.Beta, gain, cancellationToken), cancellationToken);
    }

    private static async Task<float> GetPeakAsync(string path, double alpha, double beta, CancellationToken cancellationToken)
    {
        var c = AudioMath.MakeCoefficients(alpha, beta);
        float peak = 0f;
        await foreach (var packet in ReadDataAsync(path, null, cancellationToken).WithCancellation(cancellationToken))
        {
            float l = c.CLL * packet.LeftSample + c.CLR * packet.RightSample;
            float r = c.CRL * packet.LeftSample + c.CRR * packet.RightSample;
            float abs = Math.Abs(l); if (abs > peak) peak = abs;
            abs = Math.Abs(r); if (abs > peak) peak = abs;
        }
        return peak;
    }

    private static async IAsyncEnumerable<float> InvertWithGainAsync(
        string path, double alpha, double beta, float gain, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        var c = AudioMath.MakeCoefficients(alpha, beta);
        await foreach (var packet in ReadDataAsync(path, null, cancellationToken).WithCancellation(cancellationToken))
        {
            yield return (c.CLL * packet.LeftSample + c.CLR * packet.RightSample) * gain;
            yield return (c.CRL * packet.LeftSample + c.CRR * packet.RightSample) * gain;
        }
    }

    private static float ComputeFastGain(double alpha, double beta, float peakLeft, float peakRight)
    {
        double denom = Math.Abs(1.0 - alpha * beta);
        if (!AudioMath.IsFinite(denom) || denom <= 1e-12) return 1f;
        double boundLeft = (peakLeft + Math.Abs(beta) * peakRight) / denom;
        double boundRight = (peakRight + Math.Abs(alpha) * peakLeft) / denom;
        double bound = Math.Max(boundLeft, boundRight);
        if (!AudioMath.IsFinite(bound) || bound <= 1e-12) return 1f;
        return (float)(AudioConstants.NormalizationPeak / bound);
    }
}
