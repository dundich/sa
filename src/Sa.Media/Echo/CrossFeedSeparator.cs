// <summary>Separates two speakers from a stereo recording with cross-feed interference.</summary>
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Buffers;
using System.Numerics;

namespace Sa.Media.Echo;


internal sealed class CrossFeedSeparator(ILogger<CrossFeedSeparator>? logger = null)
{
    private readonly ILogger<CrossFeedSeparator> _log = logger ?? NullLogger<CrossFeedSeparator>.Instance;

    // ── Algorithm constants ───────────────────────────────────────────
    private const int WindowDurationMs = 32;
    private const int HopRatio = 4;
    private const double NormalizationPeak = 0.98;
    private const double RmsEpsilon = 1e-12;
    private const double LeakageSafetyMargin = 0.2;

    /// <summary>Runs the full separation pipeline.</summary>
    public async Task Execute(AudioSeparationOptions options, CancellationToken cancellationToken = default)
    {
        string inputPath = Path.GetFullPath(options.InputPath);
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException($"Input file not found: {inputPath}");
        }
        if (options.SpectralMaskPower <= 0)
        {
            throw new ArgumentException("--spectral-mask-power must be greater than zero");
        }
        if (options.MaskFloor is < 0 or > 1)
        {
            throw new ArgumentException("--mask-floor must be between zero and one");
        }

        WavHeader wavHeader = await WavHeaderReader.ReadHeader(inputPath);
        int sampleRate = (int)wavHeader.SampleRate;
        int channels = wavHeader.NumChannels;
        _log.LogTrace("Probed: {SampleRate} Hz, {Channels} channel(s)", sampleRate, channels);

        if (channels != 2)
        {
            throw new InvalidOperationException(
                $"Expected a stereo recording, found {channels} channel(s)");
        }

        _log.LogInformation("Decoding audio…");
        float[] audio = await ReadInterleavedFloatArrayAsync(inputPath, cancellationToken);

        _log.LogInformation("Estimating cross-feed…");
        (double alpha, double beta) = EstimateLeakageCoefficients(audio, sampleRate, options.DominanceThresholdDb);
        _log.LogTrace("α = {Alpha:F4}, β = {Beta:F4}", alpha, beta);

        _log.LogInformation("Cancelling linear cross-feed…");
        InvertCrossFeedModel(audio, alpha, beta);

        if (options.SeparationMode == "aggressive")
        {
            _log.LogInformation("Applying spectral masking…");
            ApplySpectralMasking(audio, sampleRate, options.SpectralMaskPower, options.MaskFloor);
        }

        _log.LogInformation("Normalizing…");
        NormalizePeak(audio);

        string outputPath = Path.GetFullPath(
            options.OutputPath
            ?? Path.Combine(
                Path.GetDirectoryName(inputPath)!,
                $"{Path.GetFileNameWithoutExtension(inputPath)}_{options.SeparationMode}.wav"));
        if (!Path.GetExtension(outputPath).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Output path must use the .wav extension");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        _log.LogTrace("Writing output: {OutputPath}", outputPath);
        using (Stream output = File.Create(outputPath))
        {
            WriteWavFile(output, sampleRate, audio);
        }

        _log.LogInformation("Mode: {Mode}", options.SeparationMode);
        _log.LogInformation("Estimated left-to-right leakage: {Alpha:F4}", alpha);
        _log.LogInformation("Estimated right-to-left leakage: {Beta:F4}", beta);
        _log.LogInformation("Wrote: {OutputPath}", outputPath);
    }

    #region Decoding

    /// <summary>
    /// Reads the WAV file and returns interleaved f32 samples as a <see cref="float[]"/>.
    /// The returned array layout is [L0, R0, L1, R1, …].
    /// Values are normalized to [-1.0, 1.0] by <see cref="AsyncWavReader"/>.
    /// </summary>
    private static async Task<float[]> ReadInterleavedFloatArrayAsync(
        string path, CancellationToken cancellationToken)
    {
        using AsyncWavReader reader = AsyncWavReader.CreateFromFile(path);
        var left = new List<float>();
        var right = new List<float>();

        await foreach (var packet in reader.ReadDoubleSamplesAsync(allowBufferReuse: false, cancellationToken: cancellationToken)
            .WithCancellation(cancellationToken))
        {
            if (packet.ChannelId == 0)
                left.Add((float)packet.Sample);
            else
                right.Add((float)packet.Sample);
        }

        int count = Math.Min(left.Count, right.Count);
        var interleaved = new float[count * 2];
        for (int i = 0; i < count; i++)
        {
            interleaved[i * 2] = left[i];
            interleaved[i * 2 + 1] = right[i];
        }
        return interleaved;
    }

    #endregion

    #region Analysis

    /// <summary>Estimates linear cross-feed coefficients α and β between stereo channels.</summary>
    private static (double Alpha, double Beta) EstimateLeakageCoefficients(
        ReadOnlySpan<float> audio, int sampleRate, double dominanceDb)
    {
        int sampleCount = audio.Length / 2;
        int frameSize = Math.Max(1, (int)Math.Round(sampleRate * 0.1));
        int frameCount = sampleCount / frameSize;
        if (frameCount == 0)
        {
            throw new InvalidOperationException("Recording is too short to analyze");
        }

        // Per-frame RMS using stride access on interleaved data.
        var rmsLeft = new double[frameCount];
        var rmsRight = new double[frameCount];
        var loudness = new double[frameCount];

        for (int frame = 0; frame < frameCount; frame++)
        {
            int startSample = frame * frameSize;
            double sumL = 0.0, sumR = 0.0;
            for (int i = startSample; i < startSample + frameSize; i++)
            {
                float l = audio[i * 2];
                float r = audio[i * 2 + 1];
                sumL += l * l;
                sumR += r * r;
            }
            rmsLeft[frame] = Math.Sqrt(sumL / frameSize + RmsEpsilon);
            rmsRight[frame] = Math.Sqrt(sumR / frameSize + RmsEpsilon);
            loudness[frame] = Math.Max(rmsLeft[frame], rmsRight[frame]);
        }

        double activeThreshold = Math.Max(
            Percentile(loudness, 20), Percentile(loudness, 90) * 0.02);

        var leftDominant = new List<int>();
        var rightDominant = new List<int>();
        for (int frame = 0; frame < frameCount; frame++)
        {
            if (loudness[frame] <= activeThreshold)
                continue;
            double ratioDb = 20 * Math.Log10((rmsLeft[frame] + 1e-10) / (rmsRight[frame] + 1e-10));
            if (ratioDb > dominanceDb)
            {
                leftDominant.Add(frame);
            }
            else if (ratioDb < -dominanceDb)
            {
                rightDominant.Add(frame);
            }
        }

        if (leftDominant.Count < 3 || rightDominant.Count < 3)
        {
            throw new InvalidOperationException(
                "Could not find enough single-speaker regions to estimate cross-talk. "
                + "Try a lower --dominance-db value.");
        }

        double alpha = ComputeLeakageGain(audio, sourceChannel: 0, targetChannel: 1, leftDominant, frameSize);
        double beta = ComputeLeakageGain(audio, sourceChannel: 1, targetChannel: 0, rightDominant, frameSize);

        if (Math.Abs(alpha) >= 0.8 || Math.Abs(beta) >= 0.8 || Math.Abs(1 - alpha * beta) < LeakageSafetyMargin)
        {
            throw new InvalidOperationException(
                $"Estimated cross-talk is unsafe to invert: alpha={alpha:F4}, beta={beta:F4}");
        }
        return (alpha, beta);
    }

    /// <summary>Computes the least-squares gain of source leaking into target over dominant frames.</summary>
    private static double ComputeLeakageGain(
        ReadOnlySpan<float> audio, int sourceChannel, int targetChannel,
        List<int> frames, int frameSize)
    {
        double crossSum = 0.0, selfSum = 0.0;
        foreach (int frame in frames)
        {
            int start = frame * frameSize;
            for (int i = start; i < start + frameSize; i++)
            {
                float s = audio[i * 2 + sourceChannel];
                float t = audio[i * 2 + targetChannel];
                crossSum += s * t;
                selfSum += s * s;
            }
        }
        return crossSum / selfSum;
    }

    #endregion

    #region Processing

    /// <summary>Inverts the 2×2 cross-feed model in-place.</summary>
    private static void InvertCrossFeedModel(Span<float> audio, double alpha, double beta)
    {
        double invDenom = 1.0 / (1.0 - alpha * beta);
        int sampleCount = audio.Length / 2;
        for (int i = 0; i < sampleCount; i++)
        {
            float l = audio[i * 2];
            float r = audio[i * 2 + 1];
            audio[i * 2] = (float)((l - beta * r) * invDenom);
            audio[i * 2 + 1] = (float)((r - alpha * l) * invDenom);
        }
    }

    /// <summary>Applies time-frequency spectral masking in-place on interleaved stereo.</summary>
    private static void ApplySpectralMasking(
        Span<float> audio, int sampleRate, double maskPower, double floor)
    {
        int sampleCount = audio.Length / 2;

        int windowSize = Math.Max(64, (int)Math.Round(sampleRate * WindowDurationMs / 1000.0));
        windowSize = (int)BitOperations.RoundUpToPowerOf2((uint)windowSize);
        int hop = windowSize / HopRatio;

        double[] window = PeriodicHannWindow(windowSize);
        int binCount = windowSize / 2 + 1;

        // Allocate one flat spectrum buffer per channel: [frame0_bin0_re, frame0_bin0_im, frame0_bin1_re, …]
        int frameCount = (sampleCount + windowSize - 1) / hop + 1;
        var leftSpectrum = new double[frameCount * binCount * 2];
        var rightSpectrum = new double[frameCount * binCount * 2];

        StftForward(audio, channel: 0, window, hop, sampleCount, leftSpectrum, frameCount, binCount);
        StftForward(audio, channel: 1, window, hop, sampleCount, rightSpectrum, frameCount, binCount);

        // Compute soft masks into a flat buffer.
        var leftShares = new double[frameCount * binCount];
        var rightShares = new double[frameCount * binCount];

        for (int idx = 0; idx < frameCount * binCount; idx++)
        {
            double magL = Magnitude(leftSpectrum, idx);
            double magR = Magnitude(rightSpectrum, idx);
            double wL = Math.Pow(magL + 1e-10, maskPower);
            double wR = Math.Pow(magR + 1e-10, maskPower);
            double total = wL + wR;
            leftShares[idx] = wL / total;
            rightShares[idx] = wR / total;
        }

        // 3×3 uniform smoothing + mask application, then inverse STFT.
        var leftSmoothed = new double[frameCount * binCount];
        var rightSmoothed = new double[frameCount * binCount];
        UniformFilter3x3(leftShares, frameCount, binCount, leftSmoothed);
        UniformFilter3x3(rightShares, frameCount, binCount, rightSmoothed);

        for (int idx = 0; idx < frameCount * binCount; idx++)
        {
            double maskL = floor + (1.0 - floor) * leftSmoothed[idx];
            double maskR = floor + (1.0 - floor) * rightSmoothed[idx];
            leftSpectrum[idx * 2] *= maskL;
            leftSpectrum[idx * 2 + 1] *= maskL;
            rightSpectrum[idx * 2] *= maskR;
            rightSpectrum[idx * 2 + 1] *= maskR;
        }

        StftInverse(leftSpectrum, frameCount, binCount, window, hop, sampleCount, audio, channel: 0);
        StftInverse(rightSpectrum, frameCount, binCount, window, hop, sampleCount, audio, channel: 1);
    }

    /// <summary>Normalizes interleaved stereo to <see cref="NormalizationPeak"/> in-place.</summary>
    private static void NormalizePeak(Span<float> audio)
    {
        float peak = 0.0f;
        foreach (float sample in audio)
        {
            float abs = Math.Abs(sample);
            if (abs > peak) peak = abs;
        }
        if (peak > 0)
        {
            float gain = (float)(NormalizationPeak / peak);
            for (int i = 0; i < audio.Length; i++)
            {
                audio[i] *= gain;
            }
        }
    }

    #endregion

    #region STFT

    /// <summary>
    /// Computes one-sided STFT for one channel of interleaved stereo,
    /// writing results into a flat <c>double[]</c> (interleaved re/im).
    /// </summary>
    private static void StftForward(
        ReadOnlySpan<float> audio, int channel,
        double[] window, int hop, int sampleCount,
        double[] output, int frameCount, int binCount)
    {
        int windowSize = window.Length;
        int half = windowSize / 2;
        var fftBuffer = ArrayPool<double>.Shared.Rent(windowSize * 2);
        var fftSpan = fftBuffer.AsSpan(0, windowSize * 2);

        try
        {
            for (int frame = 0; frame < frameCount; frame++)
            {
                int start = frame * hop - half;
                // Window + convert to complex (re, im=0) in flat layout.
                for (int i = 0; i < windowSize; i++)
                {
                    int index = start + i;
                    double sample = (index >= 0 && index < sampleCount)
                        ? audio[index * 2 + channel]
                        : 0.0;
                    double val = sample * window[i];
                    fftSpan[i * 2] = val;
                    fftSpan[i * 2 + 1] = 0.0;
                }

                Fft(fftSpan, inverse: false);

                // Copy one-sided bins to output.
                int outOffset = frame * binCount * 2;
                fftSpan.Slice(0, binCount * 2).CopyTo(output.AsSpan(outOffset));
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(fftBuffer);
        }
    }

    /// <summary>
    /// Inverse STFT with weighted overlap-add, writing back into one channel of interleaved stereo.
    /// </summary>
    private static void StftInverse(
        double[] spectrum, int frameCount, int binCount,
        double[] window, int hop, int sampleCount,
        Span<float> audio, int channel)
    {
        int windowSize = window.Length;
        int half = windowSize / 2;
        int paddedLength = (frameCount - 1) * hop + windowSize;

        var accumulator = new double[paddedLength];
        var normalization = new double[paddedLength];

        var fftBuffer = ArrayPool<double>.Shared.Rent(windowSize * 2);
        var fftSpan = fftBuffer.AsSpan(0, windowSize * 2);

        try
        {
            for (int frame = 0; frame < frameCount; frame++)
            {
                int inOffset = frame * binCount * 2;

                // Reconstruct full spectrum from one-sided bins (Hermitian symmetry).
                fftSpan[0] = spectrum[inOffset];
                fftSpan[1] = spectrum[inOffset + 1];
                fftSpan[windowSize] = spectrum[inOffset + (windowSize / 2) * 2];
                fftSpan[windowSize + 1] = spectrum[inOffset + (windowSize / 2) * 2 + 1];
                for (int bin = 1; bin < windowSize / 2; bin++)
                {
                    int src = inOffset + bin * 2;
                    fftSpan[bin * 2] = spectrum[src];
                    fftSpan[bin * 2 + 1] = spectrum[src + 1];
                    fftSpan[(windowSize - bin) * 2] = spectrum[src];
                    fftSpan[(windowSize - bin) * 2 + 1] = -spectrum[src + 1];
                }

                Fft(fftSpan, inverse: true);

                int start = frame * hop;
                for (int i = 0; i < windowSize; i++)
                {
                    double re = fftSpan[i * 2] * window[i];
                    accumulator[start + i] += re;
                    normalization[start + i] += window[i] * window[i];
                }
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(fftBuffer);
        }

        // Write back to interleaved audio.
        for (int i = 0; i < sampleCount; i++)
        {
            int index = i + half;
            if (index < paddedLength && normalization[index] > 1e-10)
            {
                audio[i * 2 + channel] = (float)(accumulator[index] / normalization[index]);
            }
            else
            {
                audio[i * 2 + channel] = 0.0f;
            }
        }
    }

    /// <summary>In-place radix-2 Cooley-Tukey FFT on interleaved (re, im) data.</summary>
    private static void Fft(Span<double> values, bool inverse)
    {
        int n = values.Length / 2; // number of complex elements

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }
            j ^= bit;
            if (i < j)
            {
                (values[i * 2], values[j * 2]) = (values[j * 2], values[i * 2]);
                (values[i * 2 + 1], values[j * 2 + 1]) = (values[j * 2 + 1], values[i * 2 + 1]);
            }
        }

        for (int length = 2; length <= n; length <<= 1)
        {
            double angle = 2.0 * Math.PI / length * (inverse ? 1.0 : -1.0);
            double realRoot = Math.Cos(angle);
            double imagRoot = Math.Sin(angle);

            for (int i = 0; i < n; i += length)
            {
                double wRe = 1.0, wIm = 0.0;
                for (int j = 0; j < length / 2; j++)
                {
                    int idx1 = i + j;
                    int idx2 = i + j + length / 2;

                    // even = values[idx1], odd = values[idx2] * w
                    double evenRe = values[idx1 * 2];
                    double evenIm = values[idx1 * 2 + 1];
                    double oddRe = values[idx2 * 2] * wRe - values[idx2 * 2 + 1] * wIm;
                    double oddIm = values[idx2 * 2] * wIm + values[idx2 * 2 + 1] * wRe;

                    values[idx1 * 2] = evenRe + oddRe;
                    values[idx1 * 2 + 1] = evenIm + oddIm;
                    values[idx2 * 2] = evenRe - oddRe;
                    values[idx2 * 2 + 1] = evenIm - oddIm;

                    // w *= root
                    double newWRe = wRe * realRoot - wIm * imagRoot;
                    wIm = wRe * imagRoot + wIm * realRoot;
                    wRe = newWRe;
                }
            }
        }

        if (inverse)
        {
            double invN = 1.0 / n;
            for (int i = 0; i < values.Length; i++)
            {
                values[i] *= invN;
            }
        }
    }

    #endregion

    #region DSP helpers

    private static double[] PeriodicHannWindow(int size)
    {
        var window = new double[size];
        for (int i = 0; i < size; i++)
        {
            window[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / size));
        }
        return window;
    }

    /// <summary>3×3 mean filter with edge replication on a flat 2D buffer.</summary>
    private static void UniformFilter3x3(
        ReadOnlySpan<double> input, int rows, int columns, Span<double> output)
    {
        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < columns; col++)
            {
                double sum = 0.0;
                for (int dr = -1; dr <= 1; dr++)
                {
                    int r = Math.Clamp(row + dr, 0, rows - 1);
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        int c = Math.Clamp(col + dc, 0, columns - 1);
                        sum += input[r * columns + c];
                    }
                }
                output[row * columns + col] = sum / 9.0;
            }
        }
    }

    private static double Magnitude(ReadOnlySpan<double> spectrum, int complexIndex)
    {
        double re = spectrum[complexIndex * 2];
        double im = spectrum[complexIndex * 2 + 1];
        return Math.Sqrt(re * re + im * im);
    }

    /// <summary>NumPy-style linear-interpolation percentile.</summary>
    private static double Percentile(ReadOnlySpan<double> values, double percent)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        double rank = percent / 100.0 * (sorted.Length - 1);
        int lower = (int)Math.Floor(rank);
        int upper = (int)Math.Ceiling(rank);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (rank - lower);
    }

    #endregion

    #region Output

    /// <summary>Writes interleaved stereo as PCM 16-bit WAV to the given stream.</summary>
    private static void WriteWavFile(Stream stream, int sampleRate, ReadOnlySpan<float> audio)
    {
        int sampleCount = audio.Length / 2;
        const int channels = 2;
        const int bitsPerSample = 16;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        int blockAlign = channels * bitsPerSample / 8;
        int dataSize = sampleCount * blockAlign;

        // Write WAV header (44 bytes).
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
        stream.Write(header);

        // Write interleaved 16-bit PCM data in pooled chunks.
        var chunk = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            int offset = 0;
            while (offset < sampleCount)
            {
                int batchSize = Math.Min(chunk.Length / 4, sampleCount - offset);
                int pos = 0;
                for (int i = 0; i < batchSize; i++)
                {
                    short l = (short)(Math.Clamp(audio[(offset + i) * 2], -1.0f, 1.0f) * 32767);
                    short r = (short)(Math.Clamp(audio[(offset + i) * 2 + 1], -1.0f, 1.0f) * 32767);
                    chunk[pos++] = (byte)(l & 0xFF);
                    chunk[pos++] = (byte)((l >> 8) & 0xFF);
                    chunk[pos++] = (byte)(r & 0xFF);
                    chunk[pos++] = (byte)((r >> 8) & 0xFF);
                }
                stream.Write(chunk.AsSpan(0, pos));
                offset += batchSize;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
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

    #endregion

    #region Utility

    #endregion
}
