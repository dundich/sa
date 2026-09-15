using Sa.Media.Echo;


namespace Sa.MediaTests.Echo;

public class CrossFeedSeparatorIntegrationTests
{
    private const string WavFileName = "pcm_s16le.wav";

    private static string DataPath(string fileName)
        => Path.Combine(AppContext.BaseDirectory, "data", fileName);

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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
}
