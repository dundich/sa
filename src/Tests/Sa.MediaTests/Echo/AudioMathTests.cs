using Sa.Media.Echo;

namespace Sa.MediaTests.Echo;

/// <summary>
/// Математика инверсии кросс-тока: коэффициенты, нормализация и защита от неустойчивой
/// инверсии. Последняя — не косметика: при <c>|1 − αβ|</c> около нуля усиление уходит
/// в бесконечность, и «разделение» превращается в клиппинг по всей записи.
/// </summary>
public sealed class AudioMathTests
{
    [Fact]
    public void ThrowIfUnsafeCoefficients_AcceptsAStableModel()
        => AudioMath.ThrowIfUnsafeCoefficients(0.25, 0.35);

    [Theory]
    [InlineData(0.8, 0.1)]              // |alpha| >= 0.8
    [InlineData(-0.85, 0.1)]
    [InlineData(0.1, 0.8)]              // |beta| >= 0.8
    [InlineData(double.NaN, 0.1)]
    [InlineData(0.1, double.PositiveInfinity)]
    [InlineData(0.5, 2.0)]              // |1 - alpha*beta| = 0 < 0.2 (LeakageSafetyMargin)
    public void ThrowIfUnsafeCoefficients_RejectsAnUnstableModel(double alpha, double beta)
        => Assert.Throws<InvalidOperationException>(() => AudioMath.ThrowIfUnsafeCoefficients(alpha, beta));

    [Fact]
    public void IsFinite_ClassifiesDoublesAndFloats()
    {
        Assert.True(AudioMath.IsFinite(0.0));
        Assert.False(AudioMath.IsFinite(double.NaN));
        Assert.False(AudioMath.IsFinite(double.NegativeInfinity));
        Assert.True(AudioMath.IsFinite(1.0f));
        Assert.False(AudioMath.IsFinite(float.NaN));
        Assert.False(AudioMath.IsFinite(float.PositiveInfinity));
    }

    [Fact]
    public void MakeCoefficients_InvertsTheGivenModel()
    {
        // yL = xL + beta*xR, yR = xR + alpha*xL; инверсия возвращает исходные x.
        const double alpha = 0.25;
        const double beta = 0.35;

        InversionCoefficients c = AudioMath.MakeCoefficients(alpha, beta);
        const double xl = 0.4;
        const double xr = -0.2;
        double yl = xl + beta * xr;
        double yr = xr + alpha * xl;

        double backL = c.CLL * yl + c.CLR * yr;
        double backR = c.CRL * yl + c.CRR * yr;

        Assert.Equal(xl, backL, 1e-5);
        Assert.Equal(xr, backR, 1e-5);
    }

    [Fact]
    public void Percentile_InterpolatesBetweenNeighbours()
    {
        double[] sorted = [0.0, 1.0, 2.0, 3.0];

        Assert.Equal(0.0, AudioMath.Percentile(sorted, 0));
        Assert.Equal(3.0, AudioMath.Percentile(sorted, 100));
        Assert.Equal(1.5, AudioMath.Percentile(sorted, 50), 9);
        Assert.Equal(0.0, AudioMath.Percentile([], 50));
    }

    [Fact]
    public void NormalizePeak_ScalesTheLoudestSampleToTheTarget()
    {
        float[] audio = [0.1f, -0.5f, 0.25f, -0.25f];

        AudioMath.NormalizePeak(audio);

        float peak = audio.Max(Math.Abs);
        Assert.Equal(AudioConstants.NormalizationPeak, peak, 1e-6);
    }

    [Fact]
    public void NormalizePeak_SilenceStaysSilent()
    {
        float[] audio = [0f, 0f, 0f, 0f];

        AudioMath.NormalizePeak(audio);

        Assert.All(audio, sample => Assert.Equal(0f, sample));
    }
}
