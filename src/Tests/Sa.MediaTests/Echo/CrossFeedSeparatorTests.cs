using Sa.Media.Echo;

namespace Sa.MediaTests.Echo;

/// <summary>
/// Проверки режимов подавления кросс-феда: мало того, что файл создан, — «чужой» тон
/// в каждом канале должен реально ослабнуть.
/// </summary>
public sealed class CrossFeedSeparatorTests
{
    /// <summary>Минимальное ослабление утечки, дБ. Теоретически при α=0.25/β=0.35 — ~50 дБ.</summary>
    private const double MinimumSuppressionDb = 40.0;

    [Theory]
    [InlineData(CrossFeedSeparator.ProcessingMode.MaximumSpeed, 0.9)]
    [InlineData(CrossFeedSeparator.ProcessingMode.MinimumMemory, 0.9)]
    [InlineData(CrossFeedSeparator.ProcessingMode.MinimumMemoryFastNormalize, 0.3)]
    public async Task Execute_linearMode_suppressesCrossTalk(
        CrossFeedSeparator.ProcessingMode mode,
        double minimumPeak)
    {
        using TempDirectory directory = new();
        string input = WavTestData.CreateCrossTalkStereo(directory.Path);
        string output = directory.Combine("separated.wav");

        await CrossFeedSeparator.ExecuteAsync(
            new AudioSeparationOptions(InputPath: input, OutputPath: output, Processing: mode),
            TestContext.Current.CancellationToken);

        await AssertSeparatedAsync(input, output, minimumPeak, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Execute_aggressiveMode_suppressesCrossTalk()
    {
        using TempDirectory directory = new();
        string input = WavTestData.CreateCrossTalkStereo(directory.Path);
        string output = directory.Combine("separated_aggressive.wav");

        await CrossFeedSeparator.ExecuteAsync(
            new AudioSeparationOptions(
                InputPath: input,
                OutputPath: output,
                Processing: CrossFeedSeparator.ProcessingMode.Aggressive),
            TestContext.Current.CancellationToken);

        await AssertSeparatedAsync(input, output, minimumPeak: 0.9, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Execute_autoMode_isByteIdenticalToMaximumSpeed()
    {
        using TempDirectory directory = new();
        string input = WavTestData.CreateCrossTalkStereo(directory.Path);
        string autoOutput = directory.Combine("auto.wav");
        string inMemoryOutput = directory.Combine("in_memory.wav");

        await CrossFeedSeparator.ExecuteAsync(
            new AudioSeparationOptions(InputPath: input, OutputPath: autoOutput),
            TestContext.Current.CancellationToken);

        await CrossFeedSeparator.ExecuteAsync(
            new AudioSeparationOptions(
                InputPath: input,
                OutputPath: inMemoryOutput,
                Processing: CrossFeedSeparator.ProcessingMode.MaximumSpeed),
            TestContext.Current.CancellationToken);

        // 6 секунд заведомо ниже порога in-memory, поэтому Auto обязан пойти тем же путём.
        Assert.Equal(File.ReadAllBytes(inMemoryOutput), File.ReadAllBytes(autoOutput));
    }

    [Fact]
    public async Task Execute_autoOutputPath_createsFileNextToInput()
    {
        using TempDirectory directory = new();
        string input = WavTestData.CreateCrossTalkStereo(directory.Path);
        string expected = Path.Combine(
            Path.GetDirectoryName(input)!,
            Path.GetFileNameWithoutExtension(input) + "_linear.wav");

        try
        {
            await CrossFeedSeparator.ExecuteAsync(
                new AudioSeparationOptions(
                    InputPath: input,
                    OutputPath: null,
                    Processing: CrossFeedSeparator.ProcessingMode.MaximumSpeed),
                TestContext.Current.CancellationToken);

            Assert.True(File.Exists(expected), $"Ожидался файл {expected}");
            await AssertSeparatedAsync(input, expected, minimumPeak: 0.9, TestContext.Current.CancellationToken);
        }
        finally
        {
            if (File.Exists(expected)) File.Delete(expected);
        }
    }

    [Fact]
    public async Task Execute_streamingWavWithUnknownDataSize_suppressesCrossTalk()
    {
        // data size == 0xFFFFFFFF: раньше это давало ~1 млрд кадров, рент на гигабайты
        // в aggressive и неверный выбор режима в Auto.
        using TempDirectory directory = new();
        string input = WavTestData.CreateCrossTalkStereo(
            directory.Path, fileName: "streaming.wav", streamingDataSize: true);
        string output = directory.Combine("separated_streaming.wav");

        await CrossFeedSeparator.ExecuteAsync(
            new AudioSeparationOptions(
                InputPath: input,
                OutputPath: output,
                Processing: CrossFeedSeparator.ProcessingMode.Aggressive),
            TestContext.Current.CancellationToken);

        await AssertSeparatedAsync(input, output, minimumPeak: 0.9, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Execute_inMemoryAndStreamingModes_estimateTheSameCrossTalk()
    {
        using TempDirectory directory = new();
        string input = WavTestData.CreateCrossTalkStereo(directory.Path);
        string inMemory = directory.Combine("in_memory.wav");
        string streaming = directory.Combine("streaming.wav");

        await CrossFeedSeparator.ExecuteAsync(
            new AudioSeparationOptions(
                InputPath: input,
                OutputPath: inMemory,
                Processing: CrossFeedSeparator.ProcessingMode.MaximumSpeed),
            TestContext.Current.CancellationToken);

        await CrossFeedSeparator.ExecuteAsync(
            new AudioSeparationOptions(
                InputPath: input,
                OutputPath: streaming,
                Processing: CrossFeedSeparator.ProcessingMode.MinimumMemory),
            TestContext.Current.CancellationToken);

        // Оценка α/β не должна зависеть от того, in-memory она считается или потоково:
        // оба пути опираются на одни и те же суммы по кадрам. Допуск — 1 LSB 16-бита.
        double[] left = await WavTestData.ReadChannelAsync(inMemory, 0, TestContext.Current.CancellationToken);
        double[] right = await WavTestData.ReadChannelAsync(streaming, 0, TestContext.Current.CancellationToken);

        Assert.Equal(left.Length, right.Length);
        for (int i = 0; i < left.Length; i++)
            Assert.Equal(left[i], right[i], 1.0 / 32768.0);
    }

    /// <summary>
    /// Общая проверка результата: формат, целостность длины и фактическое подавление
    /// утечки в обе стороны на участке, где звучат оба источника.
    /// </summary>
    private static async Task AssertSeparatedAsync(
        string inputPath,
        string outputPath,
        double minimumPeak,
        CancellationToken cancellationToken)
    {
        Assert.Equal(new FileInfo(inputPath).Length, new FileInfo(outputPath).Length);
        WavTestData.AssertPcm16Stereo(outputPath, expectedSampleRate: WavTestData.SampleRate);

        double[] inputLeft = await WavTestData.ReadChannelAsync(inputPath, 0, cancellationToken);
        double[] inputRight = await WavTestData.ReadChannelAsync(inputPath, 1, cancellationToken);
        double[] outputLeft = await WavTestData.ReadChannelAsync(outputPath, 0, cancellationToken);
        double[] outputRight = await WavTestData.ReadChannelAsync(outputPath, 1, cancellationToken);

        Assert.Equal(inputLeft.Length, outputLeft.Length);
        Assert.Equal(inputRight.Length, outputRight.Length);

        double[] inLeft = WavTestData.Slice(inputLeft, WavTestData.BothFrom, WavTestData.Duration);
        double[] inRight = WavTestData.Slice(inputRight, WavTestData.BothFrom, WavTestData.Duration);
        double[] outLeft = WavTestData.Slice(outputLeft, WavTestData.BothFrom, WavTestData.Duration);
        double[] outRight = WavTestData.Slice(outputRight, WavTestData.BothFrom, WavTestData.Duration);

        // Левый канал: свой тон 440 Гц, чужой (утечка из правого источника) 1000 Гц.
        double beforeLeft = WavTestData.LeakageDb(
            WavTestData.ToneAmplitude(inLeft, WavTestData.RightToneHz),
            WavTestData.ToneAmplitude(inLeft, WavTestData.LeftToneHz));

        double afterLeft = WavTestData.LeakageDb(
            WavTestData.ToneAmplitude(outLeft, WavTestData.RightToneHz),
            WavTestData.ToneAmplitude(outLeft, WavTestData.LeftToneHz));

        Assert.True(
            afterLeft <= beforeLeft - MinimumSuppressionDb,
            $"Утечка в левом канале: до {beforeLeft:F1} дБ, после {afterLeft:F1} дБ (нужно ≥ {MinimumSuppressionDb} дБ)");

        // Правый канал: свой тон 1000 Гц, чужой 440 Гц.
        double beforeRight = WavTestData.LeakageDb(
            WavTestData.ToneAmplitude(inRight, WavTestData.LeftToneHz),
            WavTestData.ToneAmplitude(inRight, WavTestData.RightToneHz));

        double afterRight = WavTestData.LeakageDb(
            WavTestData.ToneAmplitude(outRight, WavTestData.LeftToneHz),
            WavTestData.ToneAmplitude(outRight, WavTestData.RightToneHz));

        Assert.True(
            afterRight <= beforeRight - MinimumSuppressionDb,
            $"Утечка в правом канале: до {beforeRight:F1} дБ, после {afterRight:F1} дБ (нужно ≥ {MinimumSuppressionDb} дБ)");

        double peak = Math.Max(WavTestData.Peak(outputLeft), WavTestData.Peak(outputRight));
        Assert.InRange(peak, minimumPeak, 1.0);
    }
}
