using Sa.Media.Echo;


namespace Sa.MediaTests.Echo;

public class CrossFeedSeparatorIntegrationTests
{
    private const string WavFileName = "pcm_s16le.wav";

    private static string DataPath(string fileName)
        => Path.Combine(AppContext.BaseDirectory, "data", fileName);

    [Fact(Skip = "Missing test data file: pcm_s16le.wav")]
    public async Task Execute_linearMode_producesValidWav()
    {
        var inputPath = DataPath(WavFileName);
        var outputPath = Path.GetTempFileName() + ".wav";

        try
        {
            await CrossFeedSeparator.ExecuteAsync(new AudioSeparationOptions(
                InputPath: inputPath,
                OutputPath: outputPath,
                Processing: CrossFeedSeparator.ProcessingMode.MaximumSpeed,
                DominanceThresholdDb: 6.0,
                SpectralMaskPower: 4.0,
                MaskFloor: 0.01),
                TestContext.Current.CancellationToken);

            Assert.True(File.Exists(outputPath), "Output WAV file should exist");
            ValidateWavFile(outputPath, expectedChannels: 2);
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    [Fact(Skip = "Missing test data file: pcm_s16le.wav")]
    public async Task Execute_aggressiveMode_producesValidWav()
    {
        var inputPath = DataPath(WavFileName);
        var outputPath = Path.GetTempFileName() + ".wav";

        try
        {
            await CrossFeedSeparator.ExecuteAsync(new AudioSeparationOptions(
                InputPath: inputPath,
                OutputPath: outputPath,
                Processing: CrossFeedSeparator.ProcessingMode.Aggressive,
                DominanceThresholdDb: 6.0,
                SpectralMaskPower: 4.0,
                MaskFloor: 0.01), TestContext.Current.CancellationToken);

            Assert.True(File.Exists(outputPath), "Output WAV file should exist");
            ValidateWavFile(outputPath, expectedChannels: 2);
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    [Theory]
    [InlineData("/nonexistent/path/audio.mp3")]
    public async Task Execute_nonexistentInput_throwsFileNotFoundException(string inputPath)
    {

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            CrossFeedSeparator.ExecuteAsync(new AudioSeparationOptions(
                InputPath: inputPath,
                OutputPath: null!,
                Processing: CrossFeedSeparator.ProcessingMode.MaximumSpeed,
                DominanceThresholdDb: 6.0,
                SpectralMaskPower: 4.0,
                MaskFloor: 0.01), TestContext.Current.CancellationToken));
    }

    [Fact(Skip = "Missing test data file: pcm_s16le.wav")]
    public async Task Execute_invalidSpectralMaskPower_throwsArgumentException()
    {
        var inputPath = DataPath(WavFileName);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            CrossFeedSeparator.ExecuteAsync(new AudioSeparationOptions(
                InputPath: inputPath,
                OutputPath: Path.GetTempFileName() + ".wav",
                Processing: CrossFeedSeparator.ProcessingMode.MaximumSpeed,
                DominanceThresholdDb: 6.0,
                SpectralMaskPower: 0,
                MaskFloor: 0.01), TestContext.Current.CancellationToken));
    }

    [Fact(Skip = "Missing test data file: pcm_s16le.wav")]
    public async Task Execute_invalidMaskFloor_throwsArgumentException()
    {
        var inputPath = DataPath(WavFileName);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            CrossFeedSeparator.ExecuteAsync(new AudioSeparationOptions(
                InputPath: inputPath,
                OutputPath: Path.GetTempFileName() + ".wav",
                Processing: CrossFeedSeparator.ProcessingMode.MaximumSpeed,
                DominanceThresholdDb: 6.0,
                SpectralMaskPower: 4.0,
                MaskFloor: -0.5), TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            CrossFeedSeparator.ExecuteAsync(new AudioSeparationOptions(
                InputPath: inputPath,
                OutputPath: Path.GetTempFileName() + ".wav",
                Processing: CrossFeedSeparator.ProcessingMode.MaximumSpeed,
                DominanceThresholdDb: 6.0,
                SpectralMaskPower: 4.0,
                MaskFloor: 1.5), TestContext.Current.CancellationToken));
    }

    [Fact(Skip = "Missing test data file: pcm_s16le.wav")]
    public async Task Execute_autoOutputPath_generatesWavInSameDirectory()
    {
        var inputPath = DataPath(WavFileName);
        var expectedOutput = Path.Combine(
            Path.GetDirectoryName(inputPath)!,
            $"{Path.GetFileNameWithoutExtension(inputPath)}_linear.wav");

        try
        {
            await CrossFeedSeparator.ExecuteAsync(new AudioSeparationOptions(
                InputPath: inputPath,
                OutputPath: null,
                Processing: CrossFeedSeparator.ProcessingMode.MaximumSpeed,
                DominanceThresholdDb: 6.0,
                SpectralMaskPower: 4.0,
                MaskFloor: 0.01), TestContext.Current.CancellationToken);

            Assert.True(File.Exists(expectedOutput), $"Expected output should exist at {expectedOutput}");
            ValidateWavFile(expectedOutput, expectedChannels: 2);
        }
        finally
        {
            if (File.Exists(expectedOutput))
                File.Delete(expectedOutput);
        }
    }

    private static void ValidateWavFile(string path, int expectedChannels)
    {
        using var fs = File.OpenRead(path);
        using var reader = new BinaryReader(fs);

        // RIFF header
        var riff = reader.ReadChars(4);
        Assert.Equal(['R', 'I', 'F', 'F'], riff);

        _ = reader.ReadInt32(); // file size - 8
        var wave = reader.ReadChars(4);
        Assert.Equal(['W', 'A', 'V', 'E'], wave);

        // fmt chunk
        var fmt = reader.ReadChars(4);
        Assert.Equal(['f', 'm', 't', ' '], fmt);
        _ = reader.ReadInt32(); // chunk size (16)
        var audioFormat = reader.ReadInt16();
        Assert.Equal(1, audioFormat); // PCM

        var channels = reader.ReadInt16();
        Assert.Equal(expectedChannels, channels);

        _ = reader.ReadInt32(); // sample rate
        _ = reader.ReadInt32(); // byte rate
        _ = reader.ReadInt16(); // block align
        _ = reader.ReadInt16(); // bits per sample

        // data chunk
        var data = reader.ReadChars(4);
        Assert.Equal(['d', 'a', 't', 'a'], data);
        int dataSize = reader.ReadInt32();
        Assert.True(dataSize > 0, "WAV data chunk should not be empty");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Интеграционные тесты на синтетике — не зависят от отсутствующих .wav
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_linearInMemoryMode_onSyntheticStereo_producesValidWav()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sa_media_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string inputPath = GenerateSyntheticStereoWav(dir);
        string outputPath = Path.Combine(dir, "output_linear.wav");

        try
        {
            await CrossFeedSeparator.ExecuteAsync(new AudioSeparationOptions(
                InputPath: inputPath,
                OutputPath: outputPath,
                Processing: CrossFeedSeparator.ProcessingMode.MaximumSpeed,
                DominanceThresholdDb: 6.0,
                SpectralMaskPower: 4.0,
                MaskFloor: 0.01),
                TestContext.Current.CancellationToken);

            Assert.True(File.Exists(outputPath));
            ValidateWavFile(outputPath, expectedChannels: 2);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task Execute_aggressiveMode_onSyntheticStereo_producesValidWav()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sa_media_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string inputPath = GenerateSyntheticStereoWav(dir);
        string outputPath = Path.Combine(dir, "output_aggressive.wav");

        try
        {
            await CrossFeedSeparator.ExecuteAsync(new AudioSeparationOptions(
                InputPath: inputPath,
                OutputPath: outputPath,
                Processing: CrossFeedSeparator.ProcessingMode.Aggressive,
                DominanceThresholdDb: 6.0,
                SpectralMaskPower: 4.0,
                MaskFloor: 0.01),
                TestContext.Current.CancellationToken);

            Assert.True(File.Exists(outputPath));
            ValidateWavFile(outputPath, expectedChannels: 2);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task Execute_streamingExactMode_onSyntheticStereo_producesValidWav()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sa_media_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string inputPath = GenerateSyntheticStereoWav(dir);
        string outputPath = Path.Combine(dir, "output_streaming.wav");

        try
        {
            await CrossFeedSeparator.ExecuteAsync(new AudioSeparationOptions(
                InputPath: inputPath,
                OutputPath: outputPath,
                Processing: CrossFeedSeparator.ProcessingMode.MinimumMemory,
                DominanceThresholdDb: 6.0,
                SpectralMaskPower: 4.0,
                MaskFloor: 0.01),
                TestContext.Current.CancellationToken);

            Assert.True(File.Exists(outputPath));
            ValidateWavFile(outputPath, expectedChannels: 2);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    /// <summary>
    /// 10 секунд стерео 16-bit PCM: первые 5с доминирует левый канал (α≈0.3),
    /// вторые 5с — правый (β≈0.3). Каждые 400мс вставляется 100мс тишины,
    /// чтобы оценка dominance-порога находила активные участки (иначе сигнал
    /// однороден и активный порог отбрасывает все кадры).
    /// </summary>
    private static string GenerateSyntheticStereoWav(string dir)
    {
        const int sampleRate = 44100;
        const double seconds = 10;
        const double leakage = 0.3;
        const double silenceAmplitude = 0.01;
        const int activeMs = 400;
        const int silenceMs = 100;
        const int periodMs = activeMs + silenceMs;

        int totalSamples = (int)(sampleRate * seconds);
        int bytesPerSample = 2;
        int totalDataSize = totalSamples * 2 * bytesPerSample;

        string path = Path.Combine(dir, "synthetic_stereo.wav");
        using var fs = new FileStream(path, new FileStreamOptions { Access = FileAccess.Write, Mode = FileMode.Create, Share = FileShare.None });
        using var writer = new BinaryWriter(fs);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + totalDataSize);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write((ushort)2);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2 * bytesPerSample);
        writer.Write((ushort)(2 * bytesPerSample));
        writer.Write((ushort)16);
        writer.Write("data"u8.ToArray());
        writer.Write(totalDataSize);

        for (int i = 0; i < totalSamples; i++)
        {
            double t = i / (double)sampleRate;
            double tone = Math.Sin(2 * Math.PI * 440 * t);

            // Активность с паузами: periodMs = активные активMs мс + тишина silenceMs мс.
            int posInPeriodMs = (int)(t * 1000) % periodMs;
            double gain = posInPeriodMs < activeMs ? 0.8 : silenceAmplitude;

            float left, right;
            if (t < seconds / 2)
            {
                left = (float)(gain * tone);
                right = (float)(gain * leakage * tone);
            }
            else
            {
                left = (float)(gain * leakage * tone);
                right = (float)(gain * tone);
            }

            short l = (short)(Math.Clamp(left, -1f, 1f) * 32000);
            short r = (short)(Math.Clamp(right, -1f, 1f) * 32000);
            writer.Write(l);
            writer.Write(r);
        }

        return path;
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Файлы могут быть заняты антивирусом/индексатором — не валим тест по этой причине.
        }
    }
}
