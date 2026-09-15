using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Sa.Media.Echo;


internal static class AggressiveMode
{
    private const int HopRatio = 4;
    private const int WindowDurationMs = 32;

    public static async Task Execute(
        AudioSeparationOptions options, int sampleRate, double alpha, double beta, CancellationToken cancellationToken = default)
    {
        var inputPath = options.InputPath;
        float[] audio = await WavIO.ReadInterleavedFloatArrayAsync(inputPath, cancellationToken);

        AudioMath.InvertCrossFeedModel(audio, alpha, beta);
        ApplySpectralMasking(audio, sampleRate, options.SpectralMaskPower, options.MaskFloor);
        AudioMath.NormalizePeak(audio);

        string outputPath = Path.GetFullPath(options.OutputPath
            ?? Path.Combine(Path.GetDirectoryName(inputPath)!, $"{Path.GetFileNameWithoutExtension(inputPath)}_aggressive.wav"));

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using Stream output = new FileStream(outputPath, new FileStreamOptions { Access = FileAccess.Write, Mode = FileMode.Create, Share = FileShare.None, BufferSize = 1 << 20, Options = FileOptions.SequentialScan | FileOptions.Asynchronous });
        await WavIO.WriteWavFileAsync(output, sampleRate, audio, cancellationToken);
    }

    /// <summary>
    /// Потоковое спектральное маскирование.
    /// Использует кольцевой буфер на 3 кадра и скользящий overlap-add.
    /// </summary>
    private static void ApplySpectralMasking(
        Span<float> audio, int sampleRate, double maskPower, double floor)
    {
        int sampleCount = audio.Length / 2;
        if (sampleCount == 0)
        {
            return;
        }

        int windowSize = Math.Max(64, (int)Math.Round(sampleRate * WindowDurationMs / 1000.0));
        windowSize = (int)BitOperations.RoundUpToPowerOf2((uint)windowSize);

        int hop = windowSize / HopRatio;
        int half = windowSize / 2;
        int binCount = windowSize / 2 + 1;

        // Последний индекс аккумулятора, который нужен для последнего сэмпла:
        // output[i] читается из acc[i + half].
        long lastAccIndex = (long)sampleCount - 1 + half;

        // Нужен один запасной кадр, чтобы последний полезный индекс был полностью свернут.
        long frameCountLong = (lastAccIndex + hop - 1) / hop + 1;

        if (frameCountLong > int.MaxValue)
        {
            throw new InvalidOperationException("Recording is too long for STFT processing.");
        }

        int frameCount = (int)frameCountLong;

        double[] window = PeriodicHannWindow(windowSize);

        // Кольцевые буферы для 3 соседних кадров (нужно для 3x3 фильтра).
        double[][] specRingL = new double[3][];
        double[][] specRingR = new double[3][];
        double[][] hSharesRingL = new double[3][];
        double[][] hSharesRingR = new double[3][];

        for (int k = 0; k < 3; k++)
        {
            specRingL[k] = new double[binCount * 2];
            specRingR[k] = new double[binCount * 2];
            hSharesRingL[k] = new double[binCount];
            hSharesRingR[k] = new double[binCount];
        }

        double[] fftBuffer = ArrayPool<double>.Shared.Rent(windowSize * 2);
        var fftSpan = fftBuffer.AsSpan(0, windowSize * 2);

        // Важно: буфер должен вмещать окно и сдвиг.
        int accLength = windowSize + hop;

        double[] accumL = new double[accLength];
        double[] normL = new double[accLength];
        double[] accumR = new double[accLength];
        double[] normR = new double[accLength];

        double[] rawSharesL = new double[binCount];
        double[] rawSharesR = new double[binCount];

        // nextAccIndex — глобальный индекс аккумулятора, соответствующий accum[0].
        // Начинаем с -hop, чтобы первый кадр лег с ожидаемым смещением.
        int nextAccIndex = -hop;

        try
        {
            for (int i = 0; i < frameCount; i++)
            {
                int start = i * hop - half;
                int ringIdx = i % 3;

                // Forward FFT Left
                for (int j = 0; j < windowSize; j++)
                {
                    int index = start + j;
                    double sample = (index >= 0 && index < sampleCount)
                        ? audio[index * 2]
                        : 0.0;

                    fftSpan[j * 2] = sample * window[j];
                    fftSpan[j * 2 + 1] = 0.0;
                }

                Fft(fftSpan, inverse: false);
                fftSpan[..(binCount * 2)].CopyTo(specRingL[ringIdx]);

                // Forward FFT Right
                for (int j = 0; j < windowSize; j++)
                {
                    int index = start + j;
                    double sample = (index >= 0 && index < sampleCount)
                        ? audio[index * 2 + 1]
                        : 0.0;

                    fftSpan[j * 2] = sample * window[j];
                    fftSpan[j * 2 + 1] = 0.0;
                }

                Fft(fftSpan, inverse: false);
                fftSpan[..(binCount * 2)].CopyTo(specRingR[ringIdx]);

                // Вычисление масок и горизонтальный фильтр (по частотам).
                for (int c = 0; c < binCount; c++)
                {
                    double reL = specRingL[ringIdx][c * 2];
                    double imL = specRingL[ringIdx][c * 2 + 1];
                    double magL = Math.Sqrt(reL * reL + imL * imL);

                    double reR = specRingR[ringIdx][c * 2];
                    double imR = specRingR[ringIdx][c * 2 + 1];
                    double magR = Math.Sqrt(reR * reR + imR * imR);

                    double wL = Math.Pow(magL + 1e-10, maskPower);
                    double wR = Math.Pow(magR + 1e-10, maskPower);
                    double total = wL + wR;

                    rawSharesL[c] = wL / total;
                    rawSharesR[c] = wR / total;
                }

                for (int c = 0; c < binCount; c++)
                {
                    int cPrev = Math.Max(0, c - 1);
                    int cNext = Math.Min(binCount - 1, c + 1);

                    hSharesRingL[ringIdx][c] =
                        (rawSharesL[cPrev] + rawSharesL[c] + rawSharesL[cNext]) / 3.0;

                    hSharesRingR[ringIdx][c] =
                        (rawSharesR[cPrev] + rawSharesR[c] + rawSharesR[cNext]) / 3.0;
                }

                // Обрабатываем предыдущий кадр, когда уже есть текущий для 3x3 сглаживания.
                if (i >= 1)
                {
                    ProcessAndWriteFrame(
                        i - 1,
                        frameCount,
                        sampleCount,
                        hop,
                        half,
                        windowSize,
                        window,
                        specRingL,
                        specRingR,
                        hSharesRingL,
                        hSharesRingR,
                        accumL,
                        normL,
                        accumR,
                        normR,
                        audio,
                        fftSpan,
                        ref nextAccIndex,
                        floor);
                }
            }

            // Обработка самого последнего кадра.
            ProcessAndWriteFrame(
                frameCount - 1,
                frameCount,
                sampleCount,
                hop,
                half,
                windowSize,
                window,
                specRingL,
                specRingR,
                hSharesRingL,
                hSharesRingR,
                accumL,
                normL,
                accumR,
                normR,
                audio,
                fftSpan,
                ref nextAccIndex,
                floor);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(fftBuffer);
        }
    }

    private static void ProcessAndWriteFrame(
     int k,
     int frameCount,
     int sampleCount,
     int hop,
     int half,
     int windowSize,
     double[] window,
     double[][] specRingL,
     double[][] specRingR,
     double[][] hSharesRingL,
     double[][] hSharesRingR,
     double[] accumL,
     double[] normL,
     double[] accumR,
     double[] normR,
     Span<float> audio,
     Span<double> fftSpan,
     ref int nextAccIndex,
     double floor)
    {
        int kRing = k % 3;

        int prevFrame = Math.Max(0, k - 1);
        int nextFrame = Math.Min(frameCount - 1, k + 1);

        int prevRing = prevFrame % 3;
        int nextRing = nextFrame % 3;

        int binCount = windowSize / 2 + 1;

        // Вертикальный фильтр (по времени) и применение маски.
        for (int c = 0; c < binCount; c++)
        {
            double maskL =
                (hSharesRingL[prevRing][c]
                 + hSharesRingL[kRing][c]
                 + hSharesRingL[nextRing][c]) / 3.0;

            double maskR =
                (hSharesRingR[prevRing][c]
                 + hSharesRingR[kRing][c]
                 + hSharesRingR[nextRing][c]) / 3.0;

            maskL = floor + (1.0 - floor) * maskL;
            maskR = floor + (1.0 - floor) * maskR;

            specRingL[kRing][c * 2] *= maskL;
            specRingL[kRing][c * 2 + 1] *= maskL;

            specRingR[kRing][c * 2] *= maskR;
            specRingR[kRing][c * 2 + 1] *= maskR;
        }

        int start = k * hop;
        int offset = start - nextAccIndex;

        int srcSkip = 0;

        if (offset < 0)
        {
            srcSkip = -offset;
            offset = 0;
        }

        int copyLength = windowSize - srcSkip;

        // Защита на случай рассинхронизации.
        // В нормальном режиме сюда попадать не должно, потому что длина буфера = windowSize + hop.
        if (copyLength > 0 && offset + copyLength > accumL.Length)
        {
            int shift = offset + copyLength - accumL.Length;

            FlushAndShift(
                shift,
                sampleCount,
                half,
                audio,
                accumL,
                normL,
                accumR,
                normR,
                ref nextAccIndex);

            offset = start - nextAccIndex;
            srcSkip = 0;

            if (offset < 0)
            {
                srcSkip = -offset;
                offset = 0;
            }

            copyLength = windowSize - srcSkip;
        }

        if (copyLength > 0)
        {
            InverseFftFrame(specRingL[kRing], fftSpan, windowSize);
            AddWindowedReal(
                fftSpan,
                window,
                srcSkip,
                copyLength,
                offset,
                accumL,
                normL);

            InverseFftFrame(specRingR[kRing], fftSpan, windowSize);
            AddWindowedReal(
                fftSpan,
                window,
                srcSkip,
                copyLength,
                offset,
                accumR,
                normR);
        }

        // После каждого кадра сдвигаем окно на hop.
        FlushAndShift(
            hop,
            sampleCount,
            half,
            audio,
            accumL,
            normL,
            accumR,
            normR,
            ref nextAccIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddWindowedReal(
    Span<double> fftSpan,
    double[] window,
    int srcSkip,
    int copyLength,
    int dstOffset,
    double[] accum,
    double[] norm)
    {
        for (int j = 0; j < copyLength; j++)
        {
            int src = srcSkip + j;
            double w = window[src];

            accum[dstOffset + j] += fftSpan[src * 2] * w;
            norm[dstOffset + j] += w * w;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FlushAndShift(
        int count,
        int sampleCount,
        int half,
        Span<float> audio,
        double[] accumL,
        double[] normL,
        double[] accumR,
        double[] normR,
        ref int nextAccIndex)
    {
        if (count <= 0)
        {
            return;
        }

        int length = accumL.Length;

        if (count > length)
        {
            count = length;
        }

        for (int j = 0; j < count; j++)
        {
            int accIndex = nextAccIndex + j;
            int sampleIndex = accIndex - half;

            // Проверяет одновременно:
            // sampleIndex >= 0 && sampleIndex < sampleCount
            if ((uint)sampleIndex < (uint)sampleCount)
            {
                double nL = normL[j];
                audio[sampleIndex * 2] = (float)(nL > 1e-10 ? accumL[j] / nL : 0.0);

                double nR = normR[j];
                audio[sampleIndex * 2 + 1] = (float)(nR > 1e-10 ? accumR[j] / nR : 0.0);
            }
        }

        int remaining = length - count;

        if (remaining > 0)
        {
            Array.Copy(accumL, count, accumL, 0, remaining);
            Array.Copy(normL, count, normL, 0, remaining);
            Array.Copy(accumR, count, accumR, 0, remaining);
            Array.Copy(normR, count, normR, 0, remaining);
        }

        Array.Clear(accumL, remaining, count);
        Array.Clear(normL, remaining, count);
        Array.Clear(accumR, remaining, count);
        Array.Clear(normR, remaining, count);

        nextAccIndex += count;
    }

    //[MethodImpl(MethodImplOptions.AggressiveInlining)]
    //private static void FlushAccumulator(int samplesToWrite, int nextWriteSample, Span<float> audio, double[] accumL, double[] normL, double[] accumR, double[] normR)
    //{
    //    for (int j = 0; j < samplesToWrite; j++)
    //    {
    //        double nL = normL[j]; audio[(nextWriteSample + j) * 2] = (float)(nL > 1e-10 ? accumL[j] / nL : 0.0);
    //        double nR = normR[j]; audio[(nextWriteSample + j) * 2 + 1] = (float)(nR > 1e-10 ? accumR[j] / nR : 0.0);
    //    }
    //}

    //[MethodImpl(MethodImplOptions.AggressiveInlining)]
    //private static void ShiftAccumulator(int shift, int windowSize, double[] accumL, double[] normL, double[] accumR, double[] normR)
    //{
    //    int remaining = windowSize - shift;
    //    if (remaining > 0) { Array.Copy(accumL, shift, accumL, 0, remaining); Array.Copy(normL, shift, normL, 0, remaining); Array.Copy(accumR, shift, accumR, 0, remaining); Array.Copy(normR, shift, normR, 0, remaining); }
    //    Array.Clear(accumL, remaining, shift); Array.Clear(normL, remaining, shift); Array.Clear(accumR, remaining, shift); Array.Clear(normR, remaining, shift);
    //}

    private static void InverseFftFrame(double[] spec, Span<double> fftSpan, int windowSize)
    {
        fftSpan[0] = spec[0]; fftSpan[1] = spec[1];
        fftSpan[windowSize] = spec[(windowSize / 2) * 2]; fftSpan[windowSize + 1] = spec[(windowSize / 2) * 2 + 1];
        for (int bin = 1; bin < windowSize / 2; bin++)
        {
            int src = bin * 2;
            fftSpan[bin * 2] = spec[src]; fftSpan[bin * 2 + 1] = spec[src + 1];
            fftSpan[(windowSize - bin) * 2] = spec[src]; fftSpan[(windowSize - bin) * 2 + 1] = -spec[src + 1];
        }
        Fft(fftSpan, inverse: true);
    }

    private static double[] PeriodicHannWindow(int size)
    {
        var window = new double[size];
        for (int i = 0; i < size; i++) window[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / size));
        return window;
    }

    private static void Fft(Span<double> values, bool inverse)
    {
        int n = values.Length / 2;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (values[i * 2], values[j * 2]) = (values[j * 2], values[i * 2]); (values[i * 2 + 1], values[j * 2 + 1]) = (values[j * 2 + 1], values[i * 2 + 1]); }
        }
        for (int length = 2; length <= n; length <<= 1)
        {
            double angle = 2.0 * Math.PI / length * (inverse ? 1.0 : -1.0);
            double realRoot = Math.Cos(angle), imagRoot = Math.Sin(angle);
            for (int i = 0; i < n; i += length)
            {
                double wRe = 1.0, wIm = 0.0;
                for (int j = 0; j < length / 2; j++)
                {
                    int idx1 = i + j, idx2 = i + j + length / 2;
                    double evenRe = values[idx1 * 2], evenIm = values[idx1 * 2 + 1];
                    double oddRe = values[idx2 * 2] * wRe - values[idx2 * 2 + 1] * wIm;
                    double oddIm = values[idx2 * 2] * wIm + values[idx2 * 2 + 1] * wRe;
                    values[idx1 * 2] = evenRe + oddRe; values[idx1 * 2 + 1] = evenIm + oddIm;
                    values[idx2 * 2] = evenRe - oddRe; values[idx2 * 2 + 1] = evenIm - oddIm;
                    double newWRe = wRe * realRoot - wIm * imagRoot;
                    wIm = wRe * imagRoot + wIm * realRoot; wRe = newWRe;
                }
            }
        }
        if (inverse) { double invN = 1.0 / n; for (int i = 0; i < values.Length; i++) values[i] *= invN; }
    }
}
