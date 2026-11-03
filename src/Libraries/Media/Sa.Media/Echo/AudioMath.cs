namespace Sa.Media.Echo;


internal readonly record struct InversionCoefficients(float CLL, float CLR, float CRL, float CRR);

internal static class AudioMath
{
    public static InversionCoefficients MakeCoefficients(double alpha, double beta)
    {
        double invDenom = 1.0 / (1.0 - alpha * beta);
        return new InversionCoefficients(
            (float)invDenom,
            (float)(-beta * invDenom),
            (float)(-alpha * invDenom),
            (float)invDenom);
    }

    public static void InvertCrossFeedModel(Span<float> audio, double alpha, double beta)
    {
        var c = MakeCoefficients(alpha, beta);
        for (int i = 0; i < audio.Length; i += 2)
        {
            float l = audio[i];
            float r = audio[i + 1];
            audio[i] = c.CLL * l + c.CLR * r;
            audio[i + 1] = c.CRL * l + c.CRR * r;
        }
    }

    public static void NormalizePeak(Span<float> audio, double targetPeak = AudioConstants.NormalizationPeak)
    {
        float peak = 0.0f;
        for (int i = 0; i < audio.Length; i++)
        {
            float abs = Math.Abs(audio[i]);
            if (abs > peak) peak = abs;
        }
        if (peak > 1e-12f)
        {
            float gain = (float)(targetPeak / peak);
            for (int i = 0; i < audio.Length; i++)
            {
                audio[i] *= gain;
            }
        }
    }

    public static double Percentile(ReadOnlySpan<double> sorted, double percent)
    {
        if (sorted.IsEmpty) return 0.0;
        double rank = percent / 100.0 * (sorted.Length - 1);
        int lower = (int)Math.Floor(rank);
        int upper = (int)Math.Ceiling(rank);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (rank - lower);
    }

    public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    public static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    public static void ThrowIfUnsafeCoefficients(double alpha, double beta)
    {
        if (!IsFinite(alpha) || !IsFinite(beta)
            || Math.Abs(alpha) >= 0.8 || Math.Abs(beta) >= 0.8
            || Math.Abs(1 - alpha * beta) < AudioConstants.LeakageSafetyMargin)
        {
            throw new InvalidOperationException(
                $"Estimated cross-talk is unsafe to invert: alpha={alpha:F4}, beta={beta:F4}");
        }
    }
}
