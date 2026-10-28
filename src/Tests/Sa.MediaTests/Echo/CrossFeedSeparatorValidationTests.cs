using Sa.Media.Echo;

namespace Sa.MediaTests.Echo;

/// <summary>
/// Ошибки входа: аргументы проверяются до файлового I/O, поэтому неверные опции
/// не должны маскироваться под «файл не найден».
/// </summary>
public sealed class CrossFeedSeparatorValidationTests
{
    private const string MissingFile = "this-file-does-not-exist.wav";

    [Fact]
    public async Task Execute_nullOptions_throwsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => CrossFeedSeparator.ExecuteAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Execute_emptyInputPath_throwsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => CrossFeedSeparator.ExecuteAsync(new AudioSeparationOptions(InputPath: ""), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Execute_nonexistentInput_throwsFileNotFoundException()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => CrossFeedSeparator.ExecuteAsync(
                new AudioSeparationOptions(
                    InputPath: MissingFile,
                    Processing: CrossFeedSeparator.ProcessingMode.MaximumSpeed),
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public async Task Execute_invalidSpectralMaskPower_throwsArgumentException(double maskPower)
    {
        // Путь заведомо не существует: валидация аргументов обязана сработать раньше File.Exists.
        await Assert.ThrowsAsync<ArgumentException>(
            () => CrossFeedSeparator.ExecuteAsync(
                new AudioSeparationOptions(
                    InputPath: MissingFile,
                    SpectralMaskPower: maskPower),
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    public async Task Execute_invalidMaskFloor_throwsArgumentException(double maskFloor)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => CrossFeedSeparator.ExecuteAsync(
                new AudioSeparationOptions(
                    InputPath: MissingFile,
                    MaskFloor: maskFloor),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Execute_monoInput_throwsInvalidOperationException()
    {
        using TempDirectory directory = new();
        string input = WavTestData.CreateMono(directory.Path);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CrossFeedSeparator.ExecuteAsync(
                new AudioSeparationOptions(
                    InputPath: input,
                    OutputPath: directory.Combine("out.wav")),
                TestContext.Current.CancellationToken));

        Assert.Contains("stereo", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_tooShortInput_throwsInvalidOperationException()
    {
        using TempDirectory directory = new();
        string input = WavTestData.CreateTooShortStereo(directory.Path);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CrossFeedSeparator.ExecuteAsync(
                new AudioSeparationOptions(
                    InputPath: input,
                    OutputPath: directory.Combine("out.wav")),
                TestContext.Current.CancellationToken));

        Assert.Contains("too short", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_impossibleDominanceThreshold_throwsInvalidOperationException()
    {
        using TempDirectory directory = new();
        string input = WavTestData.CreateCrossTalkStereo(directory.Path);

        // +12 дБ — физический максимум утечки в синтетике, порог 120 дБ недостижим.
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CrossFeedSeparator.ExecuteAsync(
                new AudioSeparationOptions(
                    InputPath: input,
                    OutputPath: directory.Combine("out.wav"),
                    DominanceThresholdDb: 120.0),
                TestContext.Current.CancellationToken));

        Assert.Contains("single-speaker", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
